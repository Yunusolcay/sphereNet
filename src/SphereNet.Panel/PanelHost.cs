using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Security;
using SphereNet.Panel.Auth;
using SphereNet.Panel.Hubs;
using SphereNet.Panel.Logging;
using SphereNet.Panel.Updates;

namespace SphereNet.Panel;

public sealed class PanelHost : IDisposable
{
    private const int MaxScriptContentChars = 4 * 1024 * 1024;

    private const string UpdaterNotConfigured =
        "Update system is not configured (set AppUpdateRepo in sphere.ini).";

    private readonly PanelContext _ctx;
    private readonly int _port;
    private readonly PanelLogSink _logSink;
    private readonly ILogger _logger;
    private readonly UpdateService? _updates;

    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private Task? _statsTask;
    private Task? _updateCheckTask;

    private readonly LoginRateLimiter _authLimiter = new();

    public PanelHost(PanelContext ctx, int port, PanelLogSink logSink, ILogger logger)
    {
        _ctx = ctx;
        _port = port;
        _logSink = logSink;
        _logger = logger;

        if (ctx.UpdateSettings is { } updateSettings)
            _updates = new UpdateService(ctx, updateSettings, logger);
    }

    public void Start()
    {
        if (_thread is { IsAlive: true })
            return;

        _cts = new CancellationTokenSource();
        _thread = new Thread(() => RunApp(_cts.Token))
        {
            IsBackground = true,
            Name = "PanelHost"
        };
        _thread.Start();
    }

    private void RunApp(CancellationToken ct)
    {
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", $"http://localhost:{_port}");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new ForwardingLoggerProvider(_logger));

            var tokens = new TokenStore();
            var hubConnections = new HubConnectionRegistry();
            // Logout and expiry must reach the open WebSocket, not just the token
            // table; aborting also drops it out of the Clients.All broadcasts.
            tokens.TokenInvalidated += token =>
            {
                int aborted = hubConnections.AbortToken(token);
                if (aborted > 0)
                    _logger.LogInformation("Panel token invalidated; closed {Count} live connection(s)", aborted);
            };
            builder.Services.AddSingleton(_ctx);
            builder.Services.AddSingleton(tokens);
            builder.Services.AddSingleton(hubConnections);
            builder.Services.AddSingleton(_logSink);
            builder.Services.AddSignalR(o => o.EnableDetailedErrors = false);

            builder.Services.AddCors(o => o.AddPolicy("DevCors", p =>
                p.WithOrigins("http://localhost:5173")
                 .AllowAnyHeader()
                 .AllowAnyMethod()
                 .AllowCredentials()));

            _app = builder.Build();

            _logSink.SetHubContext(_app.Services.GetRequiredService<IHubContext<ServerHub>>());

            _statsTask = StatsLoop(
                _app.Services.GetRequiredService<IHubContext<ServerHub>>(), tokens, ct);

            if (_updates is not null)
                _updateCheckTask = UpdateCheckLoop(ct);

            _app.UseCors("DevCors");

            _app.Use(async (httpCtx, next) =>
            {
                try
                {
                    await next();
                }
                catch (PanelBackendUnavailableException ex)
                {
                    _logger.LogWarning(ex, "Panel backend unavailable: {Method} {Path}",
                        httpCtx.Request.Method, httpCtx.Request.Path);
                    await WriteProblemAsync(httpCtx, StatusCodes.Status503ServiceUnavailable,
                        "Game server is unavailable");
                }
                catch (PanelBackendTimeoutException ex)
                {
                    _logger.LogWarning(ex, "Panel backend timeout: {Method} {Path}",
                        httpCtx.Request.Method, httpCtx.Request.Path);
                    await WriteProblemAsync(httpCtx, StatusCodes.Status504GatewayTimeout,
                        "Game server operation timed out");
                }
                catch (PanelBackendOperationException ex)
                {
                    _logger.LogWarning(ex, "Panel backend rejected operation: {Method} {Path}",
                        httpCtx.Request.Method, httpCtx.Request.Path);
                    await WriteProblemAsync(httpCtx, StatusCodes.Status502BadGateway,
                        "Game server rejected the operation");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Panel request error: {Method} {Path}",
                        httpCtx.Request.Method, httpCtx.Request.Path);
                    if (!httpCtx.Response.HasStarted)
                    {
                        httpCtx.Response.StatusCode = 500;
                        await httpCtx.Response.WriteAsJsonAsync(new { error = "Internal server error" });
                    }
                }
            });

            // Serve built Vue app
            var distPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "panel");
            if (Directory.Exists(distPath))
            {
                var fileProvider = new PhysicalFileProvider(distPath);
                _app.UseDefaultFiles(new DefaultFilesOptions
                {
                    FileProvider = fileProvider,
                    RequestPath = ""
                });
                _app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = fileProvider,
                    RequestPath = ""
                });
            }

            // Auth middleware — protects /api/* routes.
            //
            // Endpoint routing is case-insensitive, so every comparison here must be
            // too. A case-sensitive scope test let "/API/server/running" reach the
            // very same route handler as "/api/server/running" while the middleware
            // classified it as non-API and waved it through unauthenticated.
            _app.Use(async (ctx, next) =>
            {
                var path = ctx.Request.Path.Value ?? "";

                bool isSetupPhase = string.IsNullOrEmpty(_ctx.AdminPassword);

                if (PathIs(path, "/api/auth/login") ||
                    PathIs(path, "/api/auth/local-hint") ||
                    PathIs(path, "/api/setup/needed") ||
                    (isSetupPhase && IsSetupPhaseEndpoint(path)) ||
                    PathIs(path, "/health") ||
                    PathStartsWith(path, "/hubs/") ||
                    !PathStartsWith(path, "/api/"))
                {
                    await next();
                    return;
                }

                var auth = ctx.Request.Headers.Authorization.ToString();
                var token = auth.StartsWith("Bearer ") ? auth["Bearer ".Length..] : "";

                if (!tokens.Validate(token))
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                    return;
                }

                await next();
            });

            MapRoutes(_app, tokens);

            // --- Dialog designer ---------------------------------------------
            // Gump art lives OUTSIDE /api so plain <img> tags can load it
            // without the bearer header (read-only client art, cached a day).
            _app.MapGet("/gumpart/{id}", (string id) =>
            {
                if (_ctx.GetGumpPng == null) return Results.NotFound();
                if (id.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    id = id[..^4];
                int gumpId;
                if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    _ = int.TryParse(id[2..], System.Globalization.NumberStyles.HexNumber, null, out gumpId);
                else
                    _ = int.TryParse(id, out gumpId);
                var png = _ctx.GetGumpPng(gumpId);
                return png == null
                    ? Results.NotFound()
                    : Results.File(png, "image/png");
            });
            _app.MapGet("/api/dialogs", () =>
                _ctx.ListDialogNames == null
                    ? Results.NotFound()
                    : Results.Json(_ctx.ListDialogNames()));
            _app.MapGet("/api/dialog-source", (string name) =>
            {
                if (_ctx.GetDialogSource == null) return Results.NotFound();
                var src = _ctx.GetDialogSource(name);
                return src == null ? Results.NotFound() : Results.Text(src, "text/plain");
            });

            _app.MapHub<ServerHub>("/hubs/server");

            if (Directory.Exists(distPath))
            {
                _app.MapFallbackToFile("index.html", new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(distPath)
                });
            }

            _logger.LogInformation("Admin panel on http://localhost:{Port}", _port);
            _app.StartAsync(ct).GetAwaiter().GetResult();
            _app.WaitForShutdownAsync(ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PanelHost crashed");
        }
    }

    private async Task StatsLoop(IHubContext<ServerHub> hub, TokenStore tokens, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_ctx.GetStats != null)
                {
                    // CpuPercent and ThreadCount arrive already filled in by the game
                    // server. Overwriting them here measured whichever process the
                    // panel happens to live in, which under the Host is the Host.
                    await hub.Clients.All.SendAsync("StatsUpdate", _ctx.GetStats(), ct);
                }
                tokens.PurgeExpired();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Panel stats push failed");
            }

            try { await Task.Delay(2000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    /// <summary>
    /// Periodically refreshes the "update available" badge so it appears without
    /// anyone having the Updates page open. Never applies anything — that stays
    /// an explicit operator action.
    /// </summary>
    private async Task UpdateCheckLoop(CancellationToken ct)
    {
        var interval = _ctx.UpdateSettings!.CheckMinutes;
        if (interval <= 0)
        {
            _logger.LogInformation("Update: background check disabled (AppUpdateCheckMinutes=0).");
            return;
        }

        // Let the server finish booting before reaching out to the network.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _updates!.CheckAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // A failed check must never take the panel down; the status
                // endpoint already surfaces the reason to the operator.
                _logger.LogDebug(ex, "Update: background check failed");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(interval), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null) return false;
        // Kestrel reports an IPv4 client as ::ffff:127.0.0.1 once the socket is
        // dual-stack, which IsLoopback does not recognise in mapped form.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private bool ReadIniBool(string key, bool fallback)
    {
        if (_ctx.IniPath is null || !File.Exists(_ctx.IniPath))
            return fallback;
        try
        {
            var parser = new Core.Configuration.IniParser();
            parser.Load(_ctx.IniPath);
            return parser.GetBool("SPHERE", key, fallback);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Panel could not read {Key} from sphere.ini", key);
            return fallback;
        }
    }

    /// <summary>Stand-in returned by /api/setup/config so the real admin password
    /// never reaches the browser. Posting it back to /api/setup/apply means
    /// "leave the password alone".</summary>
    internal const string PasswordMask = "********";

    /// <summary>Backend-side validation for the setup wizard. The frontend checks
    /// the same rules, but a 200 on an unusable configuration left the UI claiming
    /// success while /api/setup/needed still reported the server unconfigured.</summary>
    internal static bool TryValidateSetup(SetupConfig req, out string? error)
    {
        if (string.IsNullOrWhiteSpace(req.ServerName))
        {
            error = "Server name is required";
            return false;
        }
        if (req.ServPort is < 1 or > 65535)
        {
            error = "Server port must be between 1 and 65535";
            return false;
        }
        if (req.AdminPanelPort is not 0 && req.AdminPanelPort is < 1 or > 65535)
        {
            error = "Admin panel port must be 0 (disabled) or between 1 and 65535";
            return false;
        }
        if (req.AdminPanelPort != 0 && req.AdminPanelPort == req.ServPort)
        {
            error = "Admin panel port must differ from the server port";
            return false;
        }
        if (req.TickSleepMode is < 0 or > 2)
        {
            error = "Tick sleep mode must be 0 (spin), 1 (sleep) or 2 (hybrid)";
            return false;
        }
        if (string.IsNullOrWhiteSpace(req.AdminPassword))
        {
            error = "Admin password is required";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Ordinal, case-insensitive path equality. Routing matches paths this
    /// way, so the authorization scope must agree with it exactly — a culture-aware
    /// or case-sensitive comparison here is an auth bypass, not a cosmetic detail.
    /// Trailing slashes are ignored for the same reason.</summary>
    internal static bool PathIs(string path, string expected) =>
        string.Equals(TrimTrailingSlash(path), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ordinal, case-insensitive prefix test. See <see cref="PathIs"/>.</summary>
    internal static bool PathStartsWith(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The endpoints the first-run wizard may reach before an admin
    /// password exists. Deliberately a closed list: everything else stays behind
    /// the bearer token even during setup.</summary>
    internal static bool IsSetupPhaseEndpoint(string path) =>
        PathIs(path, "/api/setup/config") ||
        PathIs(path, "/api/setup/apply") ||
        PathIs(path, "/api/setup/status");

    private static string TrimTrailingSlash(string path) =>
        path.Length > 1 && path[^1] == '/' ? path[..^1] : path;

    private void MapRoutes(WebApplication app, TokenStore tokens)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // --- Auth ---
        app.MapPost("/api/auth/login", (LoginRequest req, HttpContext http) =>
        {
            string remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (_authLimiter.IsLimited(remoteIp, out _))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            if (string.IsNullOrEmpty(_ctx.AdminPassword))
                return Results.BadRequest(new { error = "AdminPassword not configured in sphere.ini" });

            if (!Core.Configuration.PasswordHelper.Verify(req.Password, _ctx.AdminPassword))
            {
                _authLimiter.RegisterFailure(remoteIp);
                return Results.Unauthorized();
            }

            _authLimiter.RegisterSuccess(remoteIp);
            var token = tokens.Create();
            return Results.Ok(new { token, serverName = _ctx.ServerName });
        });

        // Hands the plaintext admin password to the login page so a local
        // operator does not have to retype it. This gives away the panel's only
        // secret, so it needs all three gates below; failing any of them is not
        // an error, it just means "no hint" and the operator types it.
        app.MapGet("/api/auth/local-hint", (HttpContext http) =>
        {
            if (!IsLoopback(http.Connection.RemoteIpAddress))
                return Results.Json(new { password = (string?)null });

            if (!ReadIniBool("AdminPanelAutoFill", false))
                return Results.Json(new { password = (string?)null });

            // A hashed AdminPassword has no plaintext to hand back. Setup writes
            // the hash form, so this is the normal state after a run of the wizard.
            var stored = _ctx.AdminPassword;
            if (string.IsNullOrEmpty(stored) ||
                Core.Configuration.PasswordHelper.IsHashed(stored))
                return Results.Json(new { password = (string?)null });

            return Results.Json(new { password = stored });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ") ? auth["Bearer ".Length..] : "";
            tokens.Revoke(token);
            return Results.Ok();
        });

        // --- Setup ---
        app.MapGet("/api/setup/needed", () =>
        {
            var needed = string.IsNullOrEmpty(_ctx.AdminPassword);
            return Results.Ok(new { needed });
        });

        app.MapGet("/api/setup/config", () =>
        {
            if (_ctx.IniPath is null || !File.Exists(_ctx.IniPath))
                return Results.Problem("sphere.ini not found");

            var p = new Core.Configuration.IniParser();
            p.Load(_ctx.IniPath);
            var rawPassword = p.GetValue("SPHERE", "AdminPassword") ?? "";
            var cfg = new SetupConfig(
                ServerName    : p.GetValue("SPHERE", "ServName")      ?? _ctx.ServerName,
                ServPort      : p.GetInt  ("SPHERE", "ServPort",       2593),
                AdminPassword : string.IsNullOrEmpty(rawPassword) ? "" : PasswordMask,
                AdminPanelPort: p.GetInt  ("SPHERE", "AdminPanelPort", 0),
                TickSleepMode : p.GetInt  ("SPHERE", "TickSleepMode",  2),
                DebugPackets  : p.GetBool ("SPHERE", "DebugPackets",   false),
                ScriptDebug   : p.GetBool ("SPHERE", "ScriptDebug",    false)
            );
            return Results.Ok(cfg);
        });

        app.MapPost("/api/setup/apply", (SetupConfig req) =>
        {
            if (_ctx.IniPath is null || !File.Exists(_ctx.IniPath))
                return Results.Problem("sphere.ini not found");

            if (!TryValidateSetup(req, out string? invalid))
                return Results.BadRequest(new { error = invalid });

            // /api/setup/config hands back the password as a fixed mask so the real
            // one never leaves the box. Re-running the wizard posts the form back
            // unchanged, so the mask must mean "keep the current password" — stored
            // literally it would silently reset panel access to the mask string.
            bool keepPassword = req.AdminPassword == PasswordMask;
            string passwordHash = keepPassword
                ? _ctx.AdminPassword ?? ""
                : Core.Configuration.PasswordHelper.Hash(req.AdminPassword);

            if (string.IsNullOrEmpty(passwordHash))
                return Results.BadRequest(new { error = "Admin password is required" });

            var patch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ServName"]       = req.ServerName,
                ["ServPort"]       = req.ServPort.ToString(),
                ["AdminPanelPort"] = req.AdminPanelPort.ToString(),
                ["TickSleepMode"]  = req.TickSleepMode.ToString(),
                ["DebugPackets"]   = req.DebugPackets ? "1" : "0",
                ["ScriptDebug"]    = req.ScriptDebug  ? "1" : "0",
            };
            if (!keepPassword)
                patch["AdminPassword"] = passwordHash;

            PatchIniSection(_ctx.IniPath, "SPHERE", patch);
            _ctx.AdminPassword = passwordHash;
            _ctx.ServerName    = req.ServerName;

            // Mark setup as complete
            if (_ctx.IniPath != null)
            {
                var marker = Path.Combine(Path.GetDirectoryName(_ctx.IniPath)!, ".panel-setup-done");
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            }

            _ctx.OnResync?.Invoke();
            return Results.Ok(new { message = "Settings saved" });
        });

        app.MapGet("/api/setup/status", () =>
        {
            if (_ctx.IniPath is null) return Results.Ok(new { done = false, hasScripts = false });
            var marker     = Path.Combine(Path.GetDirectoryName(_ctx.IniPath)!, ".panel-setup-done");
            var scriptsDir = _ctx.ScriptsPath;
            var hasScripts = scriptsDir != null && Directory.Exists(scriptsDir) &&
                             Directory.EnumerateFiles(scriptsDir, "*.scp", SearchOption.AllDirectories).Any();
            return Results.Ok(new { done = File.Exists(marker), hasScripts });
        });

        // --- Server Status ---
        app.MapGet("/api/server/status", () =>
        {
            var stats = _ctx.GetStats?.Invoke() ?? new ServerStats(
                _ctx.ServerName, "0:00:00:00", 0, 0, 0, 0, 0, 0, 0, 0);
            // Same snapshot the SignalR push sends — no second, differently-sourced
            // CPU reading for the same moment.
            return Results.Ok(stats);
        });

        app.MapGet("/api/server/running", () =>
        {
            var running = _ctx.IsServerRunning?.Invoke() ?? true;
            return Results.Ok(new { running });
        });

        app.MapPost("/api/server/start", () =>
        {
            if (_ctx.StartServer == null)
                return Results.BadRequest(new { error = "Not available in standalone mode" });
            return _ctx.StartServer()
                ? Results.Ok(new { message = "Start initiated" })
                : Results.Conflict(new { error = "Server could not be started" });
        });

        // --- Server Commands ---
        app.MapPost("/api/server/save", () =>
        {
            return InvokeBackendMutation(_ctx.OnSave, "Save initiated");
        });

        app.MapPost("/api/server/shutdown", () =>
        {
            return InvokeBackendMutation(_ctx.OnShutdown, "Shutdown initiated");
        });

        app.MapPost("/api/server/restart", () =>
        {
            return InvokeBackendMutation(_ctx.OnRestart, "Restart initiated");
        });

        app.MapPost("/api/server/resync", () =>
        {
            return InvokeBackendMutation(_ctx.OnResync, "Script resync initiated");
        });

        app.MapPost("/api/server/gc", () =>
        {
            if (_ctx.OnGc?.Invoke() != true)
                return Results.Problem("Game server rejected the operation", statusCode: 502);
            return Results.Ok(new { message = "Garbage collection initiated" });
        });

        app.MapPost("/api/server/respawn", () =>
        {
            return InvokeBackendMutation(_ctx.OnRespawn, "Respawn initiated");
        });

        app.MapPost("/api/server/restock", () =>
        {
            return InvokeBackendMutation(_ctx.OnRestock, "Restock initiated");
        });

        app.MapPost("/api/server/broadcast", (BroadcastRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new { error = "Message required" });
            return _ctx.OnBroadcast?.Invoke(req.Message) == true
                ? Results.Ok()
                : Results.Problem("Game server rejected the broadcast", statusCode: 502);
        });

        app.MapPost("/api/server/command", (CommandRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new { error = "Command required" });
            var lines = _ctx.ExecuteCommand?.Invoke(req.Command) ?? [];
            return Results.Ok(new { lines });
        });

        // --- Players ---
        app.MapGet("/api/players", () =>
        {
            var players = _ctx.GetOnlinePlayers?.Invoke() ?? (IReadOnlyList<PlayerInfo>)[];
            return Results.Ok(players);
        });

        // --- Accounts ---
        app.MapGet("/api/accounts", () =>
        {
            var accounts = _ctx.GetAllAccounts?.Invoke() ?? (IReadOnlyList<AccountInfo>)[];
            return Results.Ok(accounts);
        });

        app.MapGet("/api/accounts/{name}", (string name) =>
        {
            var acc = _ctx.GetAccount?.Invoke(name);
            return acc is not null ? Results.Ok(acc) : Results.NotFound();
        });

        app.MapPost("/api/accounts", (CreateAccountRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Name and password required" });
            var ok = _ctx.CreateAccount?.Invoke(req.Name, req.Password) ?? false;
            return ok ? Results.Ok() : Results.Conflict(new { error = "Account already exists" });
        });

        app.MapDelete("/api/accounts/{name}", (string name) =>
        {
            var ok = _ctx.DeleteAccount?.Invoke(name) ?? false;
            return ok ? Results.Ok() : Results.NotFound();
        });

        app.MapPost("/api/accounts/{name}/ban", (string name) =>
        {
            return _ctx.SetAccountBanned?.Invoke(name, true) == true
                ? Results.Ok()
                : Results.NotFound();
        });

        app.MapPost("/api/accounts/{name}/unban", (string name) =>
        {
            return _ctx.SetAccountBanned?.Invoke(name, false) == true
                ? Results.Ok()
                : Results.NotFound();
        });

        app.MapPut("/api/accounts/{name}/password", (string name, ChangePasswordRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Password required" });
            return _ctx.SetAccountPassword?.Invoke(name, req.Password) == true
                ? Results.Ok()
                : Results.NotFound();
        });

        app.MapPut("/api/accounts/{name}/plevel", (string name, ChangePlevelRequest req) =>
        {
            if (req.Level < 0 || req.Level > 7)
                return Results.BadRequest(new { error = "PrivLevel must be 0-7" });
            return _ctx.SetAccountPrivLevel?.Invoke(name, req.Level) == true
                ? Results.Ok()
                : Results.NotFound();
        });

        // --- Settings / Debug ---
        app.MapGet("/api/settings/debug", () =>
        {
            var state = _ctx.GetDebugState?.Invoke() ?? new DebugState(false, false);
            return Results.Ok(state);
        });

        app.MapPost("/api/settings/debug", (DebugRequest req) =>
        {
            if (_ctx.SetPacketDebug?.Invoke(req.PacketDebug) != true ||
                _ctx.SetScriptDebug?.Invoke(req.ScriptDebug) != true)
                return Results.Problem("Game server rejected debug settings", statusCode: 502);

            // Persist to ini if available
            if (_ctx.IniPath is not null && File.Exists(_ctx.IniPath))
            {
                PatchIniSection(_ctx.IniPath, "SPHERE", new Dictionary<string, string>
                {
                    ["DebugPackets"] = req.PacketDebug ? "1" : "0",
                    ["ScriptDebug"]  = req.ScriptDebug  ? "1" : "0",
                });
            }

            return Results.Ok();
        });

        // --- Scripts ---
        app.MapGet("/api/scripts", () =>
        {
            var path = _ctx.ScriptsPath;
            if (path is null || !Directory.Exists(path))
                return Results.Ok(Array.Empty<ScriptFileInfo>());

            var files = Directory
                .EnumerateFiles(path, "*.scp", SearchOption.AllDirectories)
                .Select(f =>
                {
                    var info = new FileInfo(f);
                    var rel  = Path.GetRelativePath(path, f).Replace('\\', '/');
                    return new ScriptFileInfo(info.Name, rel, info.Length, info.LastWriteTimeUtc);
                })
                .OrderBy(f => f.RelativePath)
                .ToList();

            return Results.Ok(files);
        });

        app.MapGet("/api/scripts/content", (string path) =>
        {
            if (!TryResolveScriptPath(path, out var full, out var error))
                return Results.BadRequest(new { error });

            if (!File.Exists(full))
                return Results.NotFound();

            if (new FileInfo(full).Length > MaxScriptContentChars * 4L)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var content = File.ReadAllText(full);
            return Results.Ok(new { content });
        });

        app.MapPost("/api/scripts/validate", (ScriptContentRequest req) =>
        {
            if (req.Content.Length > MaxScriptContentChars)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var validation = ValidateScriptContent(req.Content);
            return Results.Ok(validation);
        });

        app.MapPut("/api/scripts/content", (ScriptContentRequest req, HttpContext http) =>
        {
            if (!TryResolveScriptPath(req.Path, out var full, out var error))
                return Results.BadRequest(new { error });

            if (!full.EndsWith(".scp", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Only .scp files can be edited" });

            if (req.Content.Length > MaxScriptContentChars)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var validation = ValidateScriptContent(req.Content);
            if (!validation.Ok)
                return Results.BadRequest(validation);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (File.Exists(full))
            {
                string backup = $"{full}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak";
                File.Copy(full, backup, overwrite: false);
            }

            AtomicWriteAllText(full, req.Content);
            string rel = Path.GetRelativePath(_ctx.ScriptsPath!, full).Replace('\\', '/');
            _ctx.AuditLog?.Invoke($"script saved path='{rel}' ip='{http.Connection.RemoteIpAddress}' bytes={req.Content.Length}");
            return Results.Ok(new { saved = true, path = rel, validation });
        });

        // --- App update ---------------------------------------------------
        // _updates is null when sphere.ini has no AppUpdateRepo. These routes
        // are still mapped in that case and answer 404 themselves — leaving
        // them unmapped does NOT produce a 404: the SPA fallback claims every
        // unmatched path, so a GET would return index.html with 200 (the panel
        // then parses markup as a status object) and a POST would hit that
        // GET-only fallback route and return 405. An explicit 404 is what tells
        // the panel to hide the Updates page.
        app.MapGet("/api/update/status", () =>
            _updates is null
                ? Results.NotFound(new { error = UpdaterNotConfigured })
                : Results.Ok(_updates.GetStatus()));

        app.MapPost("/api/update/check", async (CancellationToken ct) =>
            _updates is null
                ? Results.NotFound(new { error = UpdaterNotConfigured })
                : Results.Ok(await _updates.CheckAsync(ct)));

        app.MapPost("/api/update/apply", (HttpContext http) =>
        {
            if (_updates is null)
                return Results.NotFound(new { error = UpdaterNotConfigured });

            if (!_updates.TryBeginApply(out var error))
                return Results.Conflict(new { error });

            _ctx.AuditLog?.Invoke(
                $"update apply requested ip='{http.Connection.RemoteIpAddress}'");
            _logger.LogWarning("Update: apply requested from {Ip} — server will restart.",
                http.Connection.RemoteIpAddress);

            return Results.Accepted(value: _updates.GetStatus());
        });

        app.MapPost("/api/scripts/download", async () =>
        {
            var scriptsPath = _ctx.ScriptsPath;
            if (scriptsPath is null)
                return Results.Problem("ScriptsPath not configured");

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "SphereNet-Panel/1.0");
                client.Timeout = TimeSpan.FromMinutes(2);

                var zipBytes = await client.GetByteArrayAsync(
                    "https://github.com/UOSoftware/Scripts-T/archive/refs/heads/main.zip");

                using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);

                int count = 0;
                foreach (var entry in archive.Entries)
                {
                    // Entries: "Scripts-T-main/scripts/..."
                    var parts = entry.FullName.Split('/', 2);
                    if (parts.Length < 2) continue;

                    var relative = parts[1]; // "scripts/..."
                    if (!relative.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Strip "scripts/" prefix → path within scripts folder
                    var scriptRelative = relative["scripts/".Length..];
                    if (string.IsNullOrEmpty(scriptRelative)) continue;

                    if (!SafePath.TryResolveUnderRoot(scriptsPath, scriptRelative,
                            out var targetFull, out _))
                        continue;

                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(targetFull);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
                    entry.ExtractToFile(targetFull, overwrite: true);
                    count++;
                }

                return Results.Ok(new { filesInstalled = count });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Download failed: {ex.Message}");
            }
        });
    }

    private static IResult InvokeBackendMutation(Func<bool>? operation, string successMessage)
    {
        if (operation == null)
            return Results.Problem("Operation is not available", statusCode: 501);

        return operation()
            ? Results.Ok(new { message = successMessage })
            : Results.Problem("Game server rejected the operation", statusCode: 502);
    }

    private bool TryResolveScriptPath(string path, out string fullPath, out string? error)
    {
        fullPath = "";
        error = null;
        var root = _ctx.ScriptsPath;
        if (root is null)
        {
            error = "ScriptsPath not configured";
            return false;
        }

        return SafePath.TryResolveUnderRoot(root,
            path.Replace('/', Path.DirectorySeparatorChar), out fullPath, out error);
    }

    private static ScriptValidationResult ValidateScriptContent(string content)
    {
        var errors = new List<string>();
        var stack = new Stack<(string Token, int Line)>();
        string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//"))
                continue;

            if (trimmed.StartsWith('[') && !trimmed.Contains(']'))
                errors.Add($"Line {i + 1}: missing closing bracket");

            string upper = trimmed.Split(' ', '\t')[0].ToUpperInvariant();
            switch (upper)
            {
                case "IF":
                case "FOR":
                case "WHILE":
                case "DORAND":
                case "DOSWITCH":
                case "BEGIN":
                    stack.Push((upper, i + 1));
                    break;
                case "ENDIF":
                    PopExpected(stack, "IF", i + 1, errors);
                    break;
                case "ENDFOR":
                    PopExpected(stack, "FOR", i + 1, errors);
                    break;
                case "ENDWHILE":
                    PopExpected(stack, "WHILE", i + 1, errors);
                    break;
                case "ENDDO":
                    if (stack.Count == 0 || (stack.Peek().Token != "DORAND" && stack.Peek().Token != "DOSWITCH"))
                        errors.Add($"Line {i + 1}: ENDDO without DORAND/DOSWITCH");
                    else
                        stack.Pop();
                    break;
                case "END":
                    PopExpected(stack, "BEGIN", i + 1, errors);
                    break;
            }
        }

        foreach (var item in stack)
            errors.Add($"Line {item.Line}: {item.Token} block is not closed");

        return new ScriptValidationResult(errors.Count == 0, errors.ToArray());
    }

    private static void PopExpected(Stack<(string Token, int Line)> stack, string expected, int line, List<string> errors)
    {
        if (stack.Count == 0)
        {
            errors.Add($"Line {line}: END block without {expected}");
            return;
        }

        var top = stack.Pop();
        if (top.Token != expected)
            errors.Add($"Line {line}: expected END for {top.Token} opened at line {top.Line}, got {expected}");
    }

    /// <summary>
    /// Reads the INI file, replaces matching KEY=VALUE lines in the given section,
    /// and writes the file back. Lines not matching any key are preserved verbatim.
    /// </summary>
    private static void PatchIniSection(string filePath, string section,
        Dictionary<string, string> updates)
    {
        var lines = File.ReadAllLines(filePath).ToList();
        var remaining = new HashSet<string>(updates.Keys, StringComparer.OrdinalIgnoreCase);
        bool inSection = false;
        int sectionEnd = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith('['))
            {
                if (inSection)
                {
                    // We were in the target section and hit a new section
                    sectionEnd = i;
                    break;
                }

                int end = trimmed.IndexOf(']');
                if (end > 1 && trimmed[1..end].Equals(section, StringComparison.OrdinalIgnoreCase))
                    inSection = true;

                continue;
            }

            if (!inSection) continue;

            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            var key = trimmed[..eq].TrimEnd();
            if (updates.TryGetValue(key, out var newVal))
            {
                lines[i] = $"{key}={newVal}";
                remaining.Remove(key);
            }
        }

        // Append keys that didn't exist yet
        if (remaining.Count > 0)
        {
            int insertAt = sectionEnd >= 0 ? sectionEnd : lines.Count;
            foreach (var key in remaining)
                lines.Insert(insertAt++, $"{key}={updates[key]}");
        }

        AtomicWriteAllLines(filePath, lines);
    }

    private static void AtomicWriteAllText(string filePath, string content)
    {
        string tempPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void AtomicWriteAllLines(string filePath, IEnumerable<string> lines)
    {
        string tempPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllLines(tempPath, lines);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _app?.StopAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { _logger.LogDebug(ex.Flatten(), "Panel stop failed"); }

        if (_thread != null && _thread != Thread.CurrentThread)
            _thread.Join(TimeSpan.FromSeconds(3));

        try { _statsTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException ex) { _logger.LogDebug(ex.Flatten(), "Panel stats loop stopped with an error"); }

        try { _updateCheckTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException ex) { _logger.LogDebug(ex.Flatten(), "Panel update check loop stopped with an error"); }

        try { _app?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { _logger.LogDebug(ex.Flatten(), "Panel dispose failed"); }

        _updates?.Dispose();
        _cts?.Dispose();
        _statsTask = null;
        _updateCheckTask = null;
        _thread = null;
        _app = null;
        _cts = null;
    }
}

// Forwards ASP.NET Core internal logs (Kestrel, routing, SignalR) to the Host console logger
file sealed class ForwardingLoggerProvider(ILogger target) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Forwarder(target, categoryName);
    public void Dispose() { }

    private sealed class Forwarder(ILogger target, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            // Strip noisy Kestrel heartbeat / connection logs
            if (logLevel < LogLevel.Warning && category.StartsWith("Microsoft.AspNetCore."))
                return;
            target.Log(logLevel, exception, "[Panel:{Category}] {Msg}", category, msg);
        }
    }
}

// Request record types
file record LoginRequest(string Password);
file record BroadcastRequest(string Message);
file record CommandRequest(string Command);
file record CreateAccountRequest(string Name, string Password);
file record ChangePasswordRequest(string Password);
file record ChangePlevelRequest(int Level);
file record DebugRequest(bool PacketDebug, bool ScriptDebug);
file record ScriptContentRequest(string Path, string Content);
internal record ScriptValidationResult(bool Ok, string[] Errors);
