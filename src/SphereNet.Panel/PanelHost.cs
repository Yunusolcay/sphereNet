using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SphereNet.Panel.Auth;
using SphereNet.Panel.Hubs;
using SphereNet.Panel.Logging;

namespace SphereNet.Panel;

public sealed class PanelHost : IDisposable
{
    private readonly PanelContext _ctx;
    private readonly int _port;
    private readonly PanelLogSink _logSink;
    private readonly ILogger _logger;

    private WebApplication? _app;
    private CancellationTokenSource? _cts;

    // CPU tracking
    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTime _lastCpuMeasure = DateTime.MinValue;

    public PanelHost(PanelContext ctx, int port, PanelLogSink logSink, ILogger logger)
    {
        _ctx = ctx;
        _port = port;
        _logSink = logSink;
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var thread = new Thread(() => RunApp(_cts.Token))
        {
            IsBackground = true,
            Name = "PanelHost"
        };
        thread.Start();
    }

    private void RunApp(CancellationToken ct)
    {
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", $"http://localhost:{_port}");
            builder.Logging.ClearProviders();

            var tokens = new TokenStore();
            builder.Services.AddSingleton(_ctx);
            builder.Services.AddSingleton(tokens);
            builder.Services.AddSingleton(_logSink);
            builder.Services.AddSignalR(o => o.EnableDetailedErrors = false);

            builder.Services.AddCors(o => o.AddPolicy("DevCors", p =>
                p.WithOrigins("http://localhost:5173")
                 .AllowAnyHeader()
                 .AllowAnyMethod()
                 .AllowCredentials()));

            _app = builder.Build();

            _logSink.SetHubContext(_app.Services.GetRequiredService<IHubContext<ServerHub>>());

            _ = StatsLoop(_app.Services.GetRequiredService<IHubContext<ServerHub>>(), ct);

            _app.UseCors("DevCors");

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

            // Auth middleware — protects /api/* routes
            _app.Use(async (ctx, next) =>
            {
                var path = ctx.Request.Path.Value ?? "";

                if (path == "/api/auth/login" ||
                    path == "/api/setup/needed" ||
                    path == "/api/setup/config" ||
                    path == "/health" ||
                    path.StartsWith("/hubs/") ||
                    !path.StartsWith("/api/"))
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
            _app.MapHub<ServerHub>("/hubs/server");

            if (Directory.Exists(distPath))
            {
                _app.MapFallbackToFile("index.html", new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(distPath)
                });
            }

            _logger.LogInformation("Admin panel on http://localhost:{Port}", _port);
            _app.Run();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PanelHost crashed");
        }
    }

    private async Task StatsLoop(IHubContext<ServerHub> hub, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_ctx.GetStats != null)
                {
                    var stats = _ctx.GetStats() with
                    {
                        CpuPercent = GetCpuPercent(),
                        ThreadCount = Process.GetCurrentProcess().Threads.Count
                    };
                    await hub.Clients.All.SendAsync("StatsUpdate", stats, ct);
                }
            }
            catch { /* ignore — clients may disconnect mid-send */ }

            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    private double GetCpuPercent()
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastCpuMeasure).TotalSeconds;
        if (elapsed < 0.5)
            return 0;
        var cpuTime = proc.TotalProcessorTime;
        var delta = (cpuTime - _lastCpuTime).TotalSeconds;
        _lastCpuTime = cpuTime;
        _lastCpuMeasure = now;
        if (elapsed <= 0) return 0;
        return Math.Round(delta / elapsed / Environment.ProcessorCount * 100, 1);
    }

    private void MapRoutes(WebApplication app, TokenStore tokens)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // --- Auth ---
        app.MapPost("/api/auth/login", (LoginRequest req) =>
        {
            if (string.IsNullOrEmpty(_ctx.AdminPassword))
                return Results.BadRequest(new { error = "AdminPassword not configured in sphere.ini" });

            if (req.Password != _ctx.AdminPassword)
            {
                Thread.Sleep(500);
                return Results.Unauthorized();
            }

            var token = tokens.Create();
            return Results.Ok(new { token, serverName = _ctx.ServerName });
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
            var cfg = new SetupConfig(
                ServerName    : p.GetValue("SPHERE", "ServName")      ?? _ctx.ServerName,
                ServPort      : p.GetInt  ("SPHERE", "ServPort",       2593),
                AdminPassword : p.GetValue("SPHERE", "AdminPassword")  ?? "",
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

            PatchIniSection(_ctx.IniPath, "SPHERE", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ServName"]       = req.ServerName,
                ["ServPort"]       = req.ServPort.ToString(),
                ["AdminPassword"]  = req.AdminPassword,
                ["AdminPanelPort"] = req.AdminPanelPort.ToString(),
                ["TickSleepMode"]  = req.TickSleepMode.ToString(),
                ["DebugPackets"]   = req.DebugPackets ? "1" : "0",
                ["ScriptDebug"]    = req.ScriptDebug  ? "1" : "0",
            });
            _ctx.AdminPassword = req.AdminPassword;
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
            return Results.Ok(stats with { CpuPercent = GetCpuPercent() });
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
            _ctx.StartServer();
            return Results.Ok(new { message = "Start initiated" });
        });

        // --- Server Commands ---
        app.MapPost("/api/server/save", () =>
        {
            _ctx.OnSave?.Invoke();
            return Results.Ok(new { message = "Save initiated" });
        });

        app.MapPost("/api/server/shutdown", () =>
        {
            _ctx.OnShutdown?.Invoke();
            return Results.Ok(new { message = "Shutdown initiated" });
        });

        app.MapPost("/api/server/restart", () =>
        {
            _ctx.OnRestart?.Invoke();
            return Results.Ok(new { message = "Restart initiated" });
        });

        app.MapPost("/api/server/resync", () =>
        {
            _ctx.OnResync?.Invoke();
            return Results.Ok(new { message = "Script resync initiated" });
        });

        app.MapPost("/api/server/gc", () =>
        {
            _ctx.OnGc?.Invoke();
            return Results.Ok(new { memoryMB = GC.GetTotalMemory(true) / 1024 / 1024 });
        });

        app.MapPost("/api/server/respawn", () =>
        {
            _ctx.OnRespawn?.Invoke();
            return Results.Ok(new { message = "Respawn initiated" });
        });

        app.MapPost("/api/server/restock", () =>
        {
            _ctx.OnRestock?.Invoke();
            return Results.Ok(new { message = "Restock initiated" });
        });

        app.MapPost("/api/server/broadcast", (BroadcastRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Message))
                return Results.BadRequest(new { error = "Message required" });
            _ctx.OnBroadcast?.Invoke(req.Message);
            return Results.Ok();
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
            _ctx.SetAccountBanned?.Invoke(name, true);
            return Results.Ok();
        });

        app.MapPost("/api/accounts/{name}/unban", (string name) =>
        {
            _ctx.SetAccountBanned?.Invoke(name, false);
            return Results.Ok();
        });

        app.MapPut("/api/accounts/{name}/password", (string name, ChangePasswordRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "Password required" });
            _ctx.SetAccountPassword?.Invoke(name, req.Password);
            return Results.Ok();
        });

        app.MapPut("/api/accounts/{name}/plevel", (string name, ChangePlevelRequest req) =>
        {
            if (req.Level < 0 || req.Level > 7)
                return Results.BadRequest(new { error = "PrivLevel must be 0-7" });
            _ctx.SetAccountPrivLevel?.Invoke(name, req.Level);
            return Results.Ok();
        });

        // --- Settings / Debug ---
        app.MapGet("/api/settings/debug", () =>
        {
            var state = _ctx.GetDebugState?.Invoke() ?? new DebugState(false, false);
            return Results.Ok(state);
        });

        app.MapPost("/api/settings/debug", (DebugRequest req) =>
        {
            _ctx.SetPacketDebug?.Invoke(req.PacketDebug);
            _ctx.SetScriptDebug?.Invoke(req.ScriptDebug);

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
            var root = _ctx.ScriptsPath;
            if (root is null)
                return Results.Problem("ScriptsPath not configured");

            // Prevent path traversal
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Invalid path" });

            if (!File.Exists(full))
                return Results.NotFound();

            var content = File.ReadAllText(full);
            return Results.Ok(new { content });
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

                    var targetFull = Path.GetFullPath(Path.Combine(scriptsPath, scriptRelative));
                    if (!targetFull.StartsWith(Path.GetFullPath(scriptsPath), StringComparison.OrdinalIgnoreCase))
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

        File.WriteAllLines(filePath, lines);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _app?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _cts?.Dispose();
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
