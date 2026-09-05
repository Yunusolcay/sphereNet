using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Security;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static IPBlockList? _ipBlockList;
    private static ConnectionRateLimiter? _connRateLimiter;
    /// <summary>Per-IP connect attempt history feeding the connectreq_ex and
    /// connection_acquired hooks. Bounded and TTL-expired (Source-X NETTTL); the raw
    /// dictionary it replaces never removed anything.</summary>
    private static IpAttemptHistory _connectionAttempts = new();

    /// <summary>CPU/thread sampler for THIS process. The panel used to sample its
    /// own process instead, which under the Host reported the Host's numbers beside
    /// the game server's world counts.</summary>
    private static readonly SphereNet.Core.Diagnostics.ProcessCpuSampler _processMetrics = new();

    private static void InitializeAdminSurfaces()
    {
            // --- Security: shared IP block list & connection rate limiter ---
            _ipBlockList = new IPBlockList();
            _connRateLimiter = new ConnectionRateLimiter();
            // Source-X forgets an IP once its NETTTL lapses (CIPHistoryManager::tick);
            // the attempt count rides along and is forgotten with the entry.
            _connectionAttempts = new IpAttemptHistory(_config.NetTTL);
            _ipBlockList.Blocked += ip =>
                _systemHooks.DispatchServer("blockip", _serverHookContext, ip, 0);
            _network.ConnectionAcceptFilter = ip =>
            {
                if ((SphereNet.Game.Diagnostics.BotEngine.BotModeActive || _trustLoopback)
                    && System.Net.IPAddress.IsLoopback(ip))
                    return false;
                string ipStr = ip.ToString();
                if (_ipBlockList.IsBlocked(ipStr))
                    return true;
                var attempt = _connectionAttempts.Register(ipStr);
                _connRateLimiter.RegisterAttempt(ipStr);
                if (!_connRateLimiter.ShouldThrottle(ipStr))
                    return false;

                var result = _systemHooks.DispatchServerResult("connectreq_ex", _serverHookContext, ipStr,
                    (int)Math.Clamp(attempt.CurrentMs - attempt.PreviousMs, 0, int.MaxValue),
                    unchecked((int)attempt.CurrentMs), attempt.Count);
                if (result == TriggerResult.False)
                    _ipBlockList.Add(ipStr);
                return true;
            };

            SphereNet.Game.Diagnostics.BotEngine.OnBotModeChanged += active =>
            {
                if (active)
                {
                    _connRateLimiter!.Reset("127.0.0.1");
                    _connRateLimiter.Reset("::1");
                    _network.ClientMaxIP = 0;
                }
                else
                {
                    _network.ClientMaxIP = _config.ClientMaxIP;
                }
            };

            // --- 9. Admin Console ---
            int telnetPort = _config.ServPort + 1;
            _telnet = new TelnetConsole(_world, _accounts, _config,
                () => _network.ActiveConnections,
                _loggerFactory.CreateLogger("Telnet"), _loggerFactory,
                _ipBlockList);
            _telnet.Start(telnetPort);
            _telnet.OnSaveRequested += RequestSaveOnMainLoop;
            _telnet.OnShutdownRequested += () => _running = false;
            _telnet.OnResyncRequested += PerformScriptResync;
            _telnet.OnAccountPrivLevelChanged += SyncOnlineAccountPrivLevel;
            _telnet.OnDebugToggleRequested += ToggleDebugPackets;
            _telnet.OnScriptDebugToggleRequested += ToggleScriptDebug;
            // The telnet console has its OWN AdminCommandProcessor: the
            // harness/maintenance events below were only wired on the local
            // console processor, so BOT/STRESS/RESPAWN/RESTOCK typed over
            // telnet were silently dropped.
            _telnet.Processor.OnBotRequested += (count, behavior) => HandleBotCommand(count, behavior, false);
            _telnet.Processor.OnStressRequested += (items, npcs, hostile) => _stressEngine?.QueueGenerate(items, npcs, hostile);
            _telnet.Processor.OnRespawnRequested += RequestRespawnOnMainLoop;
            _telnet.Processor.OnRespawnResetRequested += RequestRespawnResetOnMainLoop;
            _telnet.Processor.OnRestockRequested += RequestRestockOnMainLoop;

            // Console command processor (shares logic with telnet)
            _consoleProcessor = new AdminCommandProcessor(_world, _accounts, _config,
                () => _network.ActiveConnections, _loggerFactory, _ipBlockList);
            _consoleProcessor.OnSaveRequested += RequestSaveOnMainLoop;
            _consoleProcessor.OnShutdownRequested += () => _running = false;
            _consoleProcessor.OnResyncRequested += PerformScriptResync;
            _consoleProcessor.OnAccountPrivLevelChanged += SyncOnlineAccountPrivLevel;
            _consoleProcessor.OnDebugToggleRequested += ToggleDebugPackets;
            _consoleProcessor.OnScriptDebugToggleRequested += ToggleScriptDebug;
            _consoleProcessor.OnBotRequested += (count, behavior) => HandleBotCommand(count, behavior, false);
            _consoleProcessor.OnStressRequested += (items, npcs, hostile) => _stressEngine?.QueueGenerate(items, npcs, hostile);
            // Global RESPAWN/RESTOCK — run on the main loop (they mutate world state).
            _consoleProcessor.OnRespawnRequested += RequestRespawnOnMainLoop;
            _consoleProcessor.OnRespawnResetRequested += RequestRespawnResetOnMainLoop;
            _consoleProcessor.OnRestockRequested += RequestRestockOnMainLoop;

            // In-game ".serv.xxx" bridge (Admin/Owner): route through the SAME
            // processor telnet uses, echoing every output line to the invoker.
            SphereNet.Game.Speech.CommandHandler.ServerCommandBridge = (gm, cmd) =>
            {
                if (_consoleProcessor == null || string.IsNullOrWhiteSpace(cmd))
                    return false;
                _clientsByCharUid.TryGetValue(gm.Uid, out var invoker);
                void Echo(string line)
                {
                    if (invoker != null) invoker.SysMessage(line);
                    else _log.LogInformation("[serv:{Gm}] {Line}", gm.Name, line);
                }
                // World-ops (EXPORT/IMPORT/RESTORE/SAVESTATICS/LOAD) live in the
                // script-side server resolver, not the console processor — route
                // them there so .serv.export etc. work in-game too.
                string upperCmd = cmd.TrimStart().ToUpperInvariant();
                if (upperCmd.StartsWith("EXPORT") || upperCmd.StartsWith("IMPORT") ||
                    upperCmd.StartsWith("RESTORE") || upperCmd.StartsWith("SAVESTATICS") ||
                    upperCmd.StartsWith("LOAD"))
                {
                    Echo(ResolveServerProperty(cmd) ?? "(no result)");
                    return true;
                }
                _consoleProcessor.ProcessCommand(cmd, Echo);
                return true;
            };

            // Audit logging for admin commands
            _telnet.Processor.OnCommandExecuted += (source, cmd) =>
                _log.LogWarning("[AUDIT] [{Source}] {Command}", source, cmd);
            _consoleProcessor.OnCommandExecuted += (source, cmd) =>
                _log.LogWarning("[AUDIT] [{Source}] {Command}", source, cmd);

            // sphere.ini UseHttp gates the web status / dialog-designer HTTP endpoint.
            if (_config.UseHttp)
            {
                int webPort = _config.ServPort + 2;
                _webStatus = new WebStatusServer(_world, _accounts,
                    () => _network.ActiveConnections,
                    _loggerFactory.CreateLogger("WebStatus"),
                    GetRuntimeMetrics);
                // Dialog designer data sources (also reused by the admin panel
                // below): real gump art from the muls and the loaded [DIALOG]
                // sections for the live preview.
                _webStatus.GetGumpArt = (int id, out int w, out int h, out byte[] rgba) =>
                {
                    w = 0; h = 0; rgba = [];
                    return _mapData != null && _mapData.TryGetGumpArt(id, out w, out h, out rgba);
                };
                _webStatus.ListDialogNames = ListAllDialogNames;
                _webStatus.GetDialogSource = GetDialogSectionSource;
                _webStatus.Start(webPort);
            }

            // --- 10. Admin Panel (SphereNet.Panel) ---
            int panelPort = _config.AdminPanelPort > 0 ? _config.AdminPanelPort : _config.ServPort + 3;
            var panelCtx = new PanelContext
            {
                ServerName  = _config.ServName,
                StartTime   = _serverStartTime,
                AdminPassword = _config.AdminPassword,

                GetGumpPng = id => InvokePanelOnMainLoop(() => GetGumpPngCached(id), "gump art"),
                ListDialogNames = () => InvokePanelOnMainLoop<IReadOnlyList<string>>(
                    () => ListAllDialogNames(), "dialog list"),
                GetDialogSource = name => InvokePanelOnMainLoop(() => GetDialogSectionSource(name), "dialog source"),

                GetStats = () => InvokePanelOnMainLoop(() =>
                {
                    var (chars, items, sectors) = _world.GetStats();
                    var uptime = DateTime.UtcNow - _serverStartTime;
                    var runtime = GetTickTelemetrySnapshot();
                    var maps = _world.GetMapStats()
                        .Select(m => new MapStats(m.MapId, m.Chars, m.Items, m.Sectors, m.ActiveSectors, m.OnlinePlayers))
                        .ToList();
                    return new ServerStats(
                        _config.ServName,
                        uptime.ToString(@"d\.hh\:mm\:ss"),
                        (int)uptime.TotalSeconds,
                        _clients.Values.Count(c => c.IsPlaying),
                        chars,
                        items,
                        sectors,
                        _world.TickCount,
                        GC.GetTotalMemory(false) / 1024 / 1024,
                        _accounts.Count,
                        CpuPercent: _processMetrics.SamplePercent(),
                        ThreadCount: _processMetrics.ThreadCount,
                        AvgTickMs: runtime.AvgMs,
                        MaxTickMs: runtime.MaxMs,
                        P50TickMs: runtime.P50Ms,
                        P95TickMs: runtime.P95Ms,
                        P99TickMs: runtime.P99Ms,
                        MulticoreEnabled: runtime.MulticoreEnabled,
                        Maps: maps);
                }, "stats snapshot"),

                GetOnlinePlayers = () => InvokePanelOnMainLoop<IReadOnlyList<PlayerInfo>>(() => _clients.Values
                    .Where(c => c.IsPlaying && c.Character != null)
                    .Select(c => new PlayerInfo(
                        c.Character!.Name,
                        c.Account?.Name ?? "",
                        c.Character.Position.Map,
                        c.Character.Position.X,
                        c.Character.Position.Y,
                        c.NetState.RemoteEndPoint?.Address.ToString() ?? ""))
                    .ToList(), "player snapshot"),

                GetAllAccounts = () => InvokePanelOnMainLoop<IReadOnlyList<AccountInfo>>(() => _accounts.GetAllAccounts()
                    .Select(a => new AccountInfo(
                        a.Name, (int)a.PrivLevel, a.IsBanned,
                        a.LastIp, a.LastLogin, a.CreateDate, a.CharCount))
                    .ToList(), "account snapshot"),

                GetAccount = name => InvokePanelOnMainLoop(() =>
                {
                    var a = _accounts.FindAccount(name);
                    return a is null ? null
                        : new AccountInfo(a.Name, (int)a.PrivLevel, a.IsBanned,
                            a.LastIp, a.LastLogin, a.CreateDate, a.CharCount);
                }, "account lookup"),

                CreateAccount = (name, pass) => InvokePanelOnMainLoop(
                    () => _accounts.CreateAccount(name, pass) is not null, "account create"),
                DeleteAccount = name => InvokePanelOnMainLoop(
                    () => _accounts.DeleteAccount(name), "account delete"),
                SetAccountBanned = (name, banned) => InvokePanelOnMainLoop(
                    () => _accounts.SetAccountBlocked(name, banned), "account ban"),
                SetAccountPassword = (name, pass) => InvokePanelOnMainLoop(
                    () => _accounts.SetAccountPassword(name, pass), "account password"),
                SetAccountPrivLevel = (name, level) =>
                    InvokePanelOnMainLoop(
                        () => _accounts.SetAccountPrivLevel(name, (SphereNet.Core.Enums.PrivLevel)level),
                        "account privilege"),

                // Reuse AdminCommandProcessor so panel and telnet share the same logic
                OnSave = () => { RequestSaveOnMainLoop(); return true; },
                OnShutdown = () => { _mainLoopActions.Enqueue(() => _running = false); return true; },
                OnResync = () => { _mainLoopActions.Enqueue(PerformScriptResync); return true; },
                OnGc = () =>
                {
                    _mainLoopActions.Enqueue(() =>
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    });
                    return true;
                },
                OnRespawn = () => { RequestRespawnOnMainLoop(); return true; },
                OnRestock = () => { RequestRestockOnMainLoop(); return true; },
                OnBroadcast = msg => InvokePanelOnMainLoop(() =>
                {
                    _consoleProcessor?.ProcessCommand($"BROADCAST {msg}", _ => { });
                    return true;
                }, "broadcast"),

                ExecuteCommand = cmd => InvokePanelOnMainLoop<string[]>(() =>
                {
                    var lines = new List<string>();
                    _consoleProcessor?.ProcessCommand(cmd, lines.Add);
                    return [.. lines];
                }, "admin command"),
                AuditLog = msg => _log.LogWarning("Panel audit: {Message}", msg),

                // Paths
                IniPath     = _iniPath,
                ScriptsPath = _scriptDirs.Count > 0 ? _scriptDirs[0] : null,

                // Server lifecycle
                IsServerRunning = () => _running,
                OnRestart = () =>
                {
                    _mainLoopActions.Enqueue(() =>
                    {
                        _log.LogInformation("Panel: restart requested — shutting down game engine...");
                        _running = false;
                    });
                    return true;
                },

                // Debug toggles — same logic as ToggleDebugPackets / ToggleScriptDebug
                GetDebugState = () => InvokePanelOnMainLoop(
                    () => new SphereNet.Panel.DebugState(_config.DebugPackets, _config.ScriptDebug), "debug state"),
                SetPacketDebug = on => InvokePanelOnMainLoop(() =>
                {
                    _config.DebugPackets = on;
                    if (_network != null)
                    {
                        _network.DebugPackets = on;
                        foreach (var ns in _network.GetActiveStates())
                            ns.DebugPackets = on;
                    }
                    _logLevelSwitch.MinimumLevel = on
                        ? Serilog.Events.LogEventLevel.Debug
                        : Serilog.Events.LogEventLevel.Information;
                    _log.LogInformation("Panel: DebugPackets={Value}", on);
                    return true;
                }, "packet debug"),
                SetScriptDebug = on => InvokePanelOnMainLoop(() =>
                {
                    _config.ScriptDebug = on;
                    _triggerDispatcher.ScriptDebug = on;
                    if (_triggerRunner != null)
                        _triggerRunner.ScriptDebug = on;
                    if (on)
                        _logLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;
                    _log.LogInformation("Panel: ScriptDebug={Value}", on);
                    return true;
                }, "script debug"),
            };

            // In managed mode: start IPC server so Host can communicate with us.
            // Panel web server lives in SphereNet.Host, not here.
            if (_managed && !string.IsNullOrEmpty(_pipeName))
            {
                _ipcCts = new CancellationTokenSource();
                _ipcServer = new SphereNet.Server.Ipc.IpcServer(_pipeName);
                _ipcServer.SetContext(panelCtx);
                _ipcTask = _ipcServer.RunAsync(_ipcCts.Token);
                _ = _ipcTask.ContinueWith(task =>
                        _log.LogError(task.Exception?.GetBaseException(), "IPC server stopped unexpectedly"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
    }

    private static T InvokePanelOnMainLoop<T>(Func<T> action, string operation)
    {
        int mainThreadId = Volatile.Read(ref _mainLoopThreadId);
        if (mainThreadId != 0 && Environment.CurrentManagedThreadId == mainThreadId)
            return action();

        if (!_running)
            throw new InvalidOperationException("Game server main loop is not running");

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mainLoopActions.Enqueue(() =>
        {
            if (completion.Task.IsCompleted)
                return;

            try { completion.TrySetResult(action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });

        try
        {
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            completion.TrySetException(new TimeoutException($"Main-loop operation '{operation}' timed out"));
            throw new TimeoutException($"Main-loop operation '{operation}' timed out");
        }
    }

    // --- Console Commands ---

    /// <summary>
    /// Write a line to the console form (GUI) or stdout (headless).
    /// </summary>
    private static void ConsoleAppend(string text)
    {
        if (_log == null)
            Console.WriteLine(text);
    }

    private static void HandleConsoleCommand(string input)
    {
        if (_consoleProcessor == null) return;
        _consoleProcessor.ProcessCommand(input, ConsoleAppend);
    }

    private static void ToggleDebugPackets(Action<string> output)
    {
        if (_network == null || _config == null) return;
        bool newState = !_network.DebugPackets;
        _network.DebugPackets = newState;
        _config.DebugPackets = newState;

        // Update all existing connections
        foreach (var ns in _network.GetActiveStates())
            ns.DebugPackets = newState;

        // Switch Serilog minimum level
        _logLevelSwitch.MinimumLevel = newState
            ? Serilog.Events.LogEventLevel.Debug
            : Serilog.Events.LogEventLevel.Information;

        string state = newState ? "ON" : "OFF";
        output($"DebugPackets toggled: {state}");
        _log?.LogInformation("DebugPackets toggled: {State}", state);

#if WINFORMS
        _consoleForm?.SetDebugState(newState);
#endif
    }

    private static void ToggleScriptDebug(Action<string> output)
    {
        if (_config == null) return;
        bool newState = !_config.ScriptDebug;
        _config.ScriptDebug = newState;
        _triggerDispatcher.ScriptDebug = newState;
        if (_triggerRunner != null)
            _triggerRunner.ScriptDebug = newState;

        if (newState)
            _logLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Debug;

        string state = newState ? "ON" : "OFF";
        output($"ScriptDebug toggled: {state}");
        _log?.LogInformation("ScriptDebug toggled: {State}", state);
    }

    /// <summary>
    /// Resolve SERV.* property lookups for script engine.
    /// Maps to Source-X SER.* / SERV.* server properties.
    /// </summary>

    private static void ParseLogFileLevel(string raw,
        out Serilog.Events.LogEventLevel minLevel,
        out HashSet<Serilog.Events.LogEventLevel>? whitelist)
    {
        minLevel = Serilog.Events.LogEventLevel.Warning;
        whitelist = null;

        if (string.IsNullOrWhiteSpace(raw))
            return;

        // Accept '|', ',' and ';' as separators so the value reads
        // naturally regardless of which convention the operator picks.
        var parts = raw.Split(['|', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return;

        if (parts.Length == 1)
        {
            if (Enum.TryParse<Serilog.Events.LogEventLevel>(parts[0],
                ignoreCase: true, out var single))
            {
                minLevel = single;
            }
            return;
        }

        var set = new HashSet<Serilog.Events.LogEventLevel>();
        foreach (var token in parts)
        {
            if (Enum.TryParse<Serilog.Events.LogEventLevel>(token,
                ignoreCase: true, out var lvl))
            {
                set.Add(lvl);
            }
        }
        if (set.Count == 0)
            return;

        whitelist = set;
        // Don't let restrictedToMinimumLevel filter out the lowest
        // whitelisted entry before our ByIncludingOnly filter runs.
        var lowest = Serilog.Events.LogEventLevel.Fatal;
        foreach (var lvl in set)
        {
            if (lvl < lowest) lowest = lvl;
        }
        minLevel = lowest;
    }

    // ==================== Dialog designer data sources ====================

    private static readonly Dictionary<int, byte[]?> _gumpPngCache = [];

    /// <summary>PNG-encoded gump art for the panel designer (cached; null =
    /// missing id or no gumpart.mul in the muls directory).</summary>
    private static byte[]? GetGumpPngCached(int id)
    {
        lock (_gumpPngCache)
        {
            if (_gumpPngCache.TryGetValue(id, out var cached))
                return cached;
            byte[]? png = null;
            if (_mapData != null && _mapData.TryGetGumpArt(id, out int w, out int h, out byte[] rgba))
                png = SphereNet.Server.Admin.MiniPng.Encode(w, h, rgba);
            _gumpPngCache[id] = png;
            return png;
        }
    }

    /// <summary>All [DIALOG name] layout section names in the loaded packs
    /// (subsections like "name BUTTON"/"name TEXT" are folded into one entry).</summary>
    private static List<string> ListAllDialogNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        if (_resources == null) return result;
        foreach (var script in _resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                foreach (var section in file.ReadAllSections())
                {
                    if (!section.Name.Equals("DIALOG", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string name = section.Argument.Split(' ', 2)[0].Trim();
                    if (name.Length > 0 && seen.Add(name))
                        result.Add(name);
                }
            }
            finally { script.Close(); }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>Raw script text of every [DIALOG name ...] section (layout +
    /// BUTTON/TEXT subsections) for the designer's import box. Reads the
    /// source file directly so comments and formatting survive.</summary>
    private static string? GetDialogSectionSource(string name)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(name)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var script in _resources.ScriptFiles)
        {
            string path = script.FilePath;
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (!line.StartsWith("[DIALOG ", StringComparison.OrdinalIgnoreCase))
                    continue;
                string header = line.TrimEnd();
                int close = header.IndexOf(']');
                if (close < 0) continue;
                string arg = header[8..close].Trim();
                string first = arg.Split(' ', 2)[0].Trim();
                if (!first.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                sb.AppendLine(lines[i].TrimEnd());
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (lines[j].TrimStart().StartsWith('['))
                        break;
                    sb.AppendLine(lines[j].TrimEnd());
                }
                sb.AppendLine();
            }
            if (sb.Length > 0)
                return sb.ToString();
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
