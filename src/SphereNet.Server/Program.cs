using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
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
    private static readonly AnsiConsoleTheme WarningConsoleTheme = new(
        new Dictionary<ConsoleThemeStyle, string>
        {
            [ConsoleThemeStyle.LevelWarning] = "\x1b[38;5;196m",
            [ConsoleThemeStyle.LevelError] = "\x1b[38;5;203m",
            [ConsoleThemeStyle.LevelFatal] = "\x1b[38;5;15m"
        });

    private static SphereConfig _config = null!;
    private static string _iniPath = "";
    private static List<string> _scriptDirs = [];
    private static CryptConfig _cryptConfig = null!;
    private static ILoggerFactory _loggerFactory = null!;
    private static Microsoft.Extensions.Logging.ILogger _log = null!;
    private static Serilog.Core.LoggingLevelSwitch _logLevelSwitch = null!;
    private static GameWorld _world = null!;
    private static NetworkManager _network = null!;
    private static AccountManager _accounts = null!;
    private static MapDataManager? _mapData;
    private static ResourceHolder _resources = null!;
    private static WorldSaver _saver = null!;
    private static WorldLoader _loader = null!;
    private static readonly Dictionary<int, GameClient> _clients = [];
    // Character UID → GameClient map. Maintained via
    // GameClient.OnCharacterOnline/Offline and used by BroadcastNearby
    // and SendPacketToChar to avoid O(players) _clients.Values scans
    // on every packet broadcast — on a 500-online shard a single
    // combat burst could cost 1-2 ms of pure iteration otherwise.
    private static readonly Dictionary<Serial, GameClient> _clientsByCharUid = [];

    // Engines
    private static MovementEngine _movement = null!;
    private static SpeechEngine _speech = null!;
    private static CommandHandler _commands = null!;
    private static SpellEngine _spellEngine = null!;
    private static SpellRegistry _spellRegistry = null!;
    private static DeathEngine _deathEngine = null!;
    private static PartyManager _partyManager = null!;
    private static GuildManager _guildManager = null!;
    private static TradeManager _tradeManager = null!;
    private static NpcAI _npcAI = null!;
    private static TerrainEngine _terrain = null!;
    private static SkillHandlers _skillHandlers = null!;
    private static CraftingEngine _craftingEngine = null!;
    private static WeatherEngine _weatherEngine = null!;
    private static HousingEngine? _housingEngine;
    private static CustomHousingEngine? _customHousing;
    private static SphereNet.Game.Chat.ChatEngine? _chatEngine;
    private static SphereNet.Game.Ships.ShipEngine? _shipEngine;
    private static SphereNet.Game.Mounts.MountEngine? _mountEngine;
    private static SphereNet.Game.Diagnostics.StressTestEngine? _stressEngine;
    private static SphereNet.Game.Diagnostics.BotEngine? _botEngine;
    private static long _lastBotRestockMs;
    private static long _lastAutoSaveMs;
    private static long _lastServerHookTimerMs;
    private static SphereNet.Game.NPCs.StableEngine _stableEngine = new();
    private static SphereNet.Game.Scheduling.TimerWheel _npcTimerWheel = null!;
    private static SphereNet.Game.Recording.RecordingEngine _recordingEngine = null!;
    private static SphereNet.Server.Recording.StateRecorder? _stateRecorder;
    private static SphereNet.Server.Macro.MacroEngine? _macroEngine;
    private static TriggerDispatcher _triggerDispatcher = null!;
    private static TriggerRunner _triggerRunner = null!;
    private static ScriptSystemHooks _systemHooks = null!;
    private static ScriptDbAdapter _scriptDb = null!;
    private static ScriptDbAdapter _scriptLdb = null!;
    private static ScriptDbAdapter _scriptMdb = null!;
    private static ScriptFileHandle? _scriptFile;
    private static readonly ServerHookContext _serverHookContext = new();
    private static TelnetConsole? _telnet;
    private static WebStatusServer? _webStatus;
    // Panel is now served by SphereNet.Host (separate process)
#if WINFORMS
    private static ConsoleForm? _consoleForm;
#endif
    private static long _lastLightWorldMinute = long.MinValue;
    private static AdminCommandProcessor? _consoleProcessor;

    // Region @RegPeriodic/@CliPeriodic firing cadence (~6s at the 50ms tick)
    // and the per-tick set used to fire @RegPeriodic once per inhabited region.
    private const int RegionPeriodicTicks = 120;
    private static readonly HashSet<uint> _regPeriodicFired = [];

    // Written from several threads (Console.CancelKeyPress signal handler, the
    // telnet accept thread, the WinForms UI thread, the IPC/panel callback) and
    // read every iteration by the main game loop. Without a barrier the JIT may
    // cache the read in the tight `while (_running)` loop, so a shutdown request
    // from another thread might not be observed for a long time — or ever. Marking
    // it volatile forces each read to re-fetch and publishes every write, so all
    // shutdown paths take effect on the loop's next iteration.
    private static volatile bool _running;
    private static bool _multicoreRuntimeEnabled;
    private static long _multicoreFallbackMs;
    private const long MulticoreRecoveryCooldownMs = 30_000;
    private static int _tickCounter;
    private static DateTime _serverStartTime;
    private static int _saveCount;
    private static readonly List<GameClient> _reusableClientSnapshot = [];
    // Reuse compute-phase buffers across ticks to keep gen-0 GC pressure
    // low. The dictionary/bag allocations were measurable on low-core
    // VDS hosts (~30–100 ms slow-tick spikes when GC kicked in).
    private static readonly List<NpcAI.NpcDecision> _reusableDecisionList = [];
    private static readonly Dictionary<int, SphereNet.Game.Clients.ClientViewDelta> _reusableClientDeltas = [];
    private static readonly List<GameClient> _reusableRefreshClients = [];
    private static readonly ConcurrentDictionary<int, SphereNet.Game.Clients.ClientViewDelta> _reusableViewDeltaConcurrent = new();
    private static readonly Dictionary<Serial, long> _summonedGuardExpiry = [];
    private static readonly List<SphereNet.Game.Objects.Items.Item> _decayCatchupBuffer = [];
    private static long _nextDecayCatchupTick;
    private static readonly List<(byte Map, short X, short Y, sbyte Z)> _expiredStaticDoorBuffer = [];
    private static readonly List<SphereNet.Game.World.StaticDoorClose> _staticDoorCloseBuffer = [];
    private static long _nextStaticDoorTick;
    private static long _telemetrySnapshotUs;
    // E8 sub-phases: _telemetrySnapshotUs used to lump the whole pre-compute
    // group (OnTickParallel + spell expiry + sector wake + wheel + client
    // snapshot) under one label, and the replay/state-recorder/macro/reschedule
    // work between apply and flush ran unmeasured. world_tick isolates the
    // parallel sector tick; post_apply covers the apply→flush gap; the
    // dominant-phase picker reports "snapshot" as the residual of the group.
    private static long _telemetryWorldTickUs;
    private static long _telemetryPostApplyUs;
    private static long _telemetryComputeUs;
    private static long _telemetryApplyUs;
    private static long _telemetryFlushUs;
    private static long _telemetryMaxTickUs;
    internal static readonly SphereNet.Game.Diagnostics.TickHistogram TickHistogram = new(500, 1);
    private static long _telemetryNpcBuildUs;
    private static long _telemetryClientStateUs;
    private static long _telemetryNpcApplyUs;
    // npc_apply sub-phases — field spikes (46-157ms @ ~15k chars) need to be
    // attributable to decision commit vs path purge vs dirty-view marking.
    private static long _telemetryNpcApplyDecisionsUs;
    private static long _telemetryNpcApplyPurgeUs;
    private static long _telemetryNpcApplyDirtyUs;
    private static int _telemetryNpcApplyDecisionCount;
    private static int _telemetryNpcApplyDirtyCount;
    private static long _telemetryViewBuildUs;
    private static long _lastSlowTickWarningMs;
    private static long _slowTickCount;
    private static string _lastSlowTickDominantPhase = "";
    private static long _lastTickStatsLogMs;
    private static long _tickStatsTotalUs;
    private static long _tickStatsMaxUs;
    private static int _tickStatsCount;
    // Main-loop iterations in the current tick_stats window. Divided by the tick
    // count it yields "loops per tick" — the idle-CPU / hot-spin gauge: a spinning
    // yield runs thousands of loops per 100ms tick, an adaptive/sleeping yield ~1.
    private static long _loopIterationCount;
    // GC pressure telemetry, sampled per tick_stats window. Bots run in-process
    // so allocation bytes include client-emulation churn — treat alloc as a
    // relative before/after gauge; gen2 count and pause% are the real GC-stall
    // signal (the ~40ms blocking pauses we want to eliminate).
    private static long _gcWindowStartAllocBytes;
    private static int _gcWindowStartGen0, _gcWindowStartGen1, _gcWindowStartGen2;
    private static long _gcWindowStartShed;
    private static bool _gcWindowInit;
    private const int TickTelemetryWindowSize = 2048;
    private static readonly long[] _tickTelemetryWindowUs = new long[TickTelemetryWindowSize];
    private static int _tickTelemetryWriteIndex;
    private static int _tickTelemetrySampleCount;
    private static bool _headless;
    private static bool _managed;       // running as child of SphereNet.Host
    private static string _pipeName = ""; // IPC pipe name when managed
    // --trustloopback: skip the connection rate-limit/accept filter for loopback
    // clients. For local soak testing with an out-of-process bot runner, which
    // hammers many connections from 127.0.0.1 that the flood filter would reject.
    private static bool _trustLoopback;
    // Server-side bot spawn placement for soak tests. Bot character positions are
    // chosen server-side (BotSpawnLocationProvider -> server's BotEngine), so to
    // cluster an out-of-process bot fleet these must be set on the SERVER, not
    // the runner. --botcity <name> + --botcluster pack bot logins around a town.
    private static string? _botSpawnCity;
    private static bool _botSpawnCluster;

    public static void Main(string[] args)
    {
        // Out-of-process load generator: connect bots to a remote/separate
        // server instead of booting one, so the bot clients run in their own
        // process (own thread pool + GC) and don't contend with the server.
        if (args.Any(a => a.Equals("--botrunner", StringComparison.OrdinalIgnoreCase)))
        {
            RunBotRunner(args);
            return;
        }

        _managed = args.Any(a => a.Equals("--managed", StringComparison.OrdinalIgnoreCase));
        _trustLoopback = args.Any(a => a.Equals("--trustloopback", StringComparison.OrdinalIgnoreCase));
        _botSpawnCluster = args.Any(a => a.Equals("--botcluster", StringComparison.OrdinalIgnoreCase));
        _botSpawnCity = args.SkipWhile(a => !a.Equals("--botcity", StringComparison.OrdinalIgnoreCase))
                            .Skip(1).FirstOrDefault();
        _pipeName = args.SkipWhile(a => !a.Equals("--pipe", StringComparison.OrdinalIgnoreCase))
                        .Skip(1).FirstOrDefault() ?? "";

        // Managed mode is always headless (panel lives in Host process)
        _headless = _managed ||
                    args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase) ||
                                  a.Equals("--nogui",    StringComparison.OrdinalIgnoreCase));

        RunHeadless(args);
    }

    /// <summary>Standalone bot load generator. Usage:
    /// <c>--botrunner &lt;host&gt; &lt;port&gt; &lt;count&gt; &lt;behavior&gt; [city]</c>
    /// (behavior: walk|combat|idle|smart|cluster). Runs the bot engine against a
    /// separately-running server so the load generator has its own process,
    /// thread pool and GC — removing the in-process CPU-contention artifact from
    /// soak measurements.</summary>
    private static void RunBotRunner(string[] args)
    {
        string[] rest = args.SkipWhile(a => !a.Equals("--botrunner", StringComparison.OrdinalIgnoreCase))
                            .Skip(1).ToArray();
        string host = rest.Length > 0 ? rest[0] : "127.0.0.1";
        int port = rest.Length > 1 && int.TryParse(rest[1], out var pp) ? pp : 2593;
        int count = rest.Length > 2 && int.TryParse(rest[2], out var cc) ? cc : 100;
        string behaviorStr = rest.Length > 3 ? rest[3] : "smart";
        string? city = rest.Length > 4 ? rest[4] : null;

        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();
        using var lf = LoggerFactory.Create(b => b.AddSerilog(dispose: true));
        var log = lf.CreateLogger("BotRunner");

        var behavior = behaviorStr.ToUpperInvariant() switch
        {
            "WALK" => SphereNet.Game.Diagnostics.BotBehavior.RandomWalk,
            "COMBAT" => SphereNet.Game.Diagnostics.BotBehavior.Combat,
            "IDLE" => SphereNet.Game.Diagnostics.BotBehavior.Idle,
            "SMART" => SphereNet.Game.Diagnostics.BotBehavior.SmartAI,
            "FULL" => SphereNet.Game.Diagnostics.BotBehavior.FullSimulation,
            "CLUSTER" => SphereNet.Game.Diagnostics.BotBehavior.Cluster,
            "SKILL" => SphereNet.Game.Diagnostics.BotBehavior.Skill,
            "LOOT" => SphereNet.Game.Diagnostics.BotBehavior.Loot,
            "VENDOR" => SphereNet.Game.Diagnostics.BotBehavior.Vendor,
            "SOCIAL" => SphereNet.Game.Diagnostics.BotBehavior.Social,
            "CHAOS" => SphereNet.Game.Diagnostics.BotBehavior.Chaos,
            "MAGE" => SphereNet.Game.Diagnostics.BotBehavior.Mage,
            _ => SphereNet.Game.Diagnostics.BotBehavior.SmartAI,
        };

        var engine = new SphereNet.Game.Diagnostics.BotEngine(
            lf.CreateLogger<SphereNet.Game.Diagnostics.BotEngine>());

        if (!string.IsNullOrEmpty(city))
        {
            var spawnCity = city.ToUpperInvariant() switch
            {
                "BRITAIN" => SphereNet.Game.Diagnostics.BotSpawnCity.Britain,
                "TRINSIC" => SphereNet.Game.Diagnostics.BotSpawnCity.Trinsic,
                "MOONGLOW" => SphereNet.Game.Diagnostics.BotSpawnCity.Moonglow,
                "YEW" => SphereNet.Game.Diagnostics.BotSpawnCity.Yew,
                "MINOC" => SphereNet.Game.Diagnostics.BotSpawnCity.Minoc,
                "VESPER" => SphereNet.Game.Diagnostics.BotSpawnCity.Vesper,
                "SKARA" => SphereNet.Game.Diagnostics.BotSpawnCity.Skara,
                "JHELOM" => SphereNet.Game.Diagnostics.BotSpawnCity.Jhelom,
                _ => SphereNet.Game.Diagnostics.BotSpawnCity.All,
            };
            engine.SetSpawnCity(spawnCity);
        }
        engine.SetClusterSpawn(behavior == SphereNet.Game.Diagnostics.BotBehavior.Cluster);

        log.LogInformation("[BotRunner] {Count} {Behavior} bots -> {Host}:{Port} (city={City})",
            count, behavior, host, port, city ?? "All");

        _running = true;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _running = false; };

        try { engine.SpawnBotsAsync(count, behavior, host, port).GetAwaiter().GetResult(); }
        catch (Exception ex) { log.LogError(ex, "[BotRunner] Spawn failed"); }

        log.LogInformation("[BotRunner] Spawn complete — bots running. Ctrl+C to stop.");
        long lastStatsMs = 0;
        while (_running)
        {
            System.Threading.Thread.Sleep(500);
            if (Environment.TickCount64 - lastStatsMs >= 10_000)
            {
                lastStatsMs = Environment.TickCount64;
                var st = engine.GetStats();
                log.LogInformation("[BotRunner] active={Active}/{Total} pps_in={In:F0} pps_out={Out:F0} sent={Sent} recv={Recv}",
                    st.ActiveBots, st.TotalBots, st.PacketsPerSecIn, st.PacketsPerSecOut,
                    engine.TotalPacketsSent, engine.TotalPacketsReceived);
            }
        }

        engine.Dispose();
        Serilog.Log.CloseAndFlush();
        log.LogInformation("[BotRunner] Stopped.");
    }

    private static void RunHeadless(string[] args)
    {
        Console.WriteLine(_managed
            ? $"SphereNet starting in managed mode (pipe: {_pipeName})…"
            : "SphereNet starting in headless mode…");

        _running = true;

        // In managed mode the Host sends commands via IPC, not stdin
        if (!_managed)
        {
            var inputThread = new Thread(() =>
            {
                while (_running)
                {
                    try
                    {
                        string? line = Console.ReadLine();
                        if (line == null) break;
                        _headlessCommandQueue.Enqueue(line);
                    }
                    catch { break; }
                }
            }) { IsBackground = true, Name = "ConsoleInput" };

            Console.CancelKeyPress += (_, e) => { e.Cancel = true; _running = false; };
            inputThread.Start();
        }
        ServerMain(args);
    }

    private static readonly ConcurrentQueue<string> _headlessCommandQueue = new();
    private static readonly ConcurrentQueue<Action> _mainLoopActions = new();
    private static int _mainLoopThreadId;
    private static SphereNet.Server.Ipc.IpcServer? _ipcServer;
    private static CancellationTokenSource? _ipcCts;
    private static Task? _ipcTask;

#if WINFORMS
    private static void RunWithGui(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _consoleForm = new ConsoleForm();
        _consoleForm.ShutdownRequested += () => _running = false;
        _consoleForm.SetStatsProviders(
            () => _clients.Values.Count(c => c.IsPlaying),
            () => _accounts?.Count ?? 0,
            () => _world?.TotalChars ?? 0,
            () => _world?.TotalItems ?? 0,
            () => _resources?.ResourceCount ?? 0);

        // Start server on background thread
        var serverThread = new Thread(() => ServerMain(args))
        {
            IsBackground = true,
            Name = "ServerMain"
        };
        serverThread.Start();

        // WinForms UI thread
        Application.Run(_consoleForm);
    }
#else
    private static void RunWithGui(string[] args) => RunHeadless(args);
#endif

    private static void ServerMain(string[] args)
    {
        try
        {
            ServerMainInner(args);
        }
        catch (Exception ex)
        {
            var msg = $"FATAL: ServerMain crashed: {ex}";
            if (_log != null)
                _log.LogCritical(ex, "ServerMain crashed");
            else
                ConsoleAppend(msg);

            // Keep form open so user can read the error
        }
    }

    private static void ServerMainInner(string[] args)
    {
        // --- 1. Configuration ---
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        _iniPath = FindConfigFile(basePath, "sphere.ini");
        string iniPath = _iniPath;
        if (iniPath == "")
        {
            ConsoleAppend("ERROR: sphere.ini not found.");
            return;
        }

        ConsoleAppend($"Loading config: {iniPath}");
        var iniParser = new IniParser();
        iniParser.Load(iniPath);
        _config = new SphereConfig();
        _config.LoadFromIni(iniParser);
        SphereNet.Scripting.Parsing.ScriptFile.ConfigureTextEncoding(
            _config.ScriptEncoding, _config.ScriptLegacyCodePage);
        var configWarnings = _config.Validate();
        foreach (var w in configWarnings)
            ConsoleAppend($"CONFIG WARNING: {w}");
        // Multicore is always on. The runtime flag still exists because
        // a phase timeout or unhandled exception in RunMulticoreTick
        // flips it to false as a hot fallback to single-thread.
        _multicoreRuntimeEnabled = true;
#if WINFORMS
        _consoleForm?.SetServerName(_config.ServName);
        _consoleForm?.SetDebugState(_config.DebugPackets);
#endif

        string cryptPath = FindConfigFile(basePath, "sphereCrypt.ini");
        _cryptConfig = new CryptConfig();
        if (cryptPath != "")
        {
            _cryptConfig.Load(cryptPath);
        }

        // --- 2. Logging (Serilog) ---
        _logLevelSwitch = new Serilog.Core.LoggingLevelSwitch(
            _config.DebugPackets ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information);
        var serilogConfig = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_logLevelSwitch);

#if WINFORMS
        if (_consoleForm != null)
            serilogConfig = serilogConfig.WriteTo.Sink(_consoleForm);
        else
#endif
            serilogConfig = serilogConfig.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                theme: WarningConsoleTheme);

        // File sink keeps its own filtering, independent of the live
        // console. Two syntaxes are supported via [SPHERE] LogFileLevel:
        //   "Warning"                            → minimum threshold
        //                                          (Warning + Error + Fatal)
        //   "Verbose | Warning | Error | Fatal"  → exact whitelist; only
        //                                          those levels are kept
        // The whitelist form is useful when you want raw traces + real
        // failures but none of the Information/Debug noise that lands
        // there during normal play.
        ParseLogFileLevel(_config.LogFileLevel, out var fileLogMinLevel,
            out var fileLogWhitelist);
        string filePath = Path.Combine(
            ResolvePath(basePath, _config.LogDir), "spherenet-.log");
        const string fileTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
        serilogConfig = serilogConfig.WriteTo.Logger(lc =>
        {
            // Sub-logger lets us add a per-event Filter that only
            // applies to the file output, not the console sink.
            lc = lc.MinimumLevel.Verbose();
            if (fileLogWhitelist != null)
            {
                var allowed = fileLogWhitelist;
                lc = lc.Filter.ByIncludingOnly(e => allowed.Contains(e.Level));
            }
            lc.WriteTo.File(
                filePath,
                restrictedToMinimumLevel: fileLogMinLevel,
                rollingInterval: RollingInterval.Day,
                outputTemplate: fileTemplate);
        });

        Log.Logger = serilogConfig.CreateLogger();
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(dispose: true);
            if (!string.IsNullOrWhiteSpace(_config.SentryDsn))
            {
                builder.AddSentry(o =>
                {
                    o.Dsn = _config.SentryDsn;
                    o.MinimumBreadcrumbLevel = LogLevel.Warning;
                    o.MinimumEventLevel = LogLevel.Warning;
                    o.Release = "SphereNet@1.0.0";
                    o.Environment = "production";
                });
            }
        });
        _log = _loggerFactory.CreateLogger("SphereNet");

        // Banner
        ConsoleAppend("===========================================");
        ConsoleAppend("  SphereNet — Ultima Online Server");
        ConsoleAppend("  Source-X Architecture / .NET 10 Port");
        ConsoleAppend("===========================================");
        ConsoleAppend("");

        _log.LogInformation("Server: {Name}", _config.ServName);
        _log.LogInformation("Port: {Port}", _config.ServPort);
        _log.LogInformation("Client Version: {Ver}", _config.ClientVersion);
        if (_config.DebugPackets)
            _log.LogWarning("DebugPackets=1 — all packets will be logged. This generates a LOT of output!");
        if (_config.ScriptDebug)
            _log.LogWarning("ScriptDebug=1 — all trigger dispatches will be logged.");

        // --- 3. Scripting Resources ---
        _resources = new ResourceHolder(_loggerFactory.CreateLogger<ResourceHolder>());
        RegisterBuiltinDefNames();
        _scriptDirs = ResolveScriptDirectories(basePath, _config.ScpFilesDir);
        var scriptDirs = _scriptDirs;
        if (scriptDirs.Count == 0)
        {
            string fallbackScriptsDir = FindDir(basePath, "scripts");
            if (!string.IsNullOrWhiteSpace(fallbackScriptsDir))
                scriptDirs.Add(fallbackScriptsDir);
        }
        if (scriptDirs.Count > 0)
        {
            // Use first directory as base; loader accepts absolute paths for the rest.
            _resources.ScpBaseDir = scriptDirs[0];
            int fileCount = 0;
            foreach (string dir in scriptDirs)
            {
                _log.LogInformation("Loading scripts from: {Dir}", dir);
                fileCount += LoadAllScripts(dir);
            }
            _log.LogInformation("Script files loaded: {Count}", fileCount);
            _resources.LogResourceSummary();

            // ISOBSCENE intrinsic → [OBSCENE] word list (Source-X g_Cfg.IsObscene)
            SphereNet.Scripting.Expressions.ExpressionParser.ObsceneChecker = _resources.IsObscene;
            // Source-X CAccount::NameStrip runs the same [OBSCENE] list over a new
            // account name before the account is allowed to exist.
            SphereNet.Core.Configuration.AccountNameValidator.ObsceneChecker = _resources.IsObscene;
            // Spell power-word letter decode → [RUNES] table (Source-X g_Cfg.GetRune)
            SphereNet.Game.Magic.SpellDef.RuneWordResolver =
                ch => _resources.Runes.Count > 0 ? _resources.GetRune(ch) : null;

            // Post-load sanity check: d_charprop1 FLAGS page needs
            // CharFlag.N entries from the [DEFNAME CharFlagNames] block
            // shipped with d_charprop.scp. If this lookup misses, the
            // script file either wasn't found in ScpFilesDir or the
            // DEFNAME block parser lost it — the .info flags tab
            // would then render empty.
            if (_resources.TryGetDefValue("CharFlag.1", out string cf1))
                _log.LogInformation("DEFNAME probe: CharFlag.1 = {Val}", cf1);
            else
                _log.LogWarning("DEFNAME probe: CharFlag.1 NOT LOADED — d_charprop1 FLAGS tab will be empty");
            // Script dosya içerikleri parse edildi, raw satır cache'ini serbest bırak
            SphereNet.Scripting.Parsing.ScriptFile.ClearFileCache();

            // Load DEFMESSAGE overrides into ServerMessages
            var defMsgs = _resources.GetAllDefMessages();
            if (defMsgs.Count > 0)
            {
                ServerMessages.LoadOverrides(defMsgs);
                _log.LogInformation("DEFMESSAGE overrides loaded: {Count}", defMsgs.Count);
            }
            // Read messages_settings defnames (SMSG_DEF_COLOR, SMSG_DEF_FONT)
            var colorRid = _resources.ResolveDefName("SMSG_DEF_COLOR");
            if (colorRid != ResourceId.Invalid)
                ServerMessages.SetDefaults((ushort)colorRid.Index, ServerMessages.DefaultFont);
            var fontRid = _resources.ResolveDefName("SMSG_DEF_FONT");
            if (fontRid != ResourceId.Invalid)
                ServerMessages.SetDefaults(ServerMessages.DefaultColor, (byte)fontRid.Index);
        }

        // --- 4. Map Data ---
        string mulPath = string.IsNullOrWhiteSpace(_config.MulFilesDir)
            ? FindDir(basePath, "mul")
            : ResolvePath(basePath, _config.MulFilesDir);
        if (string.IsNullOrEmpty(mulPath) || !Directory.Exists(mulPath))
        {
            _log.LogCritical(
                "Cannot find UO client files. Configured MULFILES='{Configured}' " +
                "(resolved to '{Resolved}'). Set MULFILES in sphere.ini to the folder " +
                "containing tiledata.mul, map0.mul / map0xLegacyMUL.uop, statics0.mul, etc.",
                _config.MulFilesDir, mulPath);
            throw new DirectoryNotFoundException(
                $"UO client files directory missing: '{mulPath}'. Server cannot start.");
        }
        mulPath = Path.GetFullPath(mulPath);
        _log.LogInformation("UO client data path resolved: configured='{Configured}' resolved='{Resolved}'",
            _config.MulFilesDir, mulPath);

        _mapData = new MapDataManager(mulPath);
        _mapData.OnMapFileLoaded += (id, path) =>
            _log.LogInformation("Map{Id} loaded from: {Path}", id, path);
        _mapData.OnMapFileLoadedDetailed += (id, path, bytes, utc) =>
            _log.LogInformation("Map{Id} data file: {Path} bytes={Bytes} modifiedUtc={ModifiedUtc:O}",
                id, path, bytes, utc);
        try
        {
            _mapData.Load();
        }
        catch (FileNotFoundException ex)
        {
            _log.LogCritical("Missing required UO client file: {Message}", ex.Message);
            throw;
        }
        _log.LogInformation("TileData & multi data loaded from: {Path}", mulPath);

        GameClient.ServerFeatureT2A = _config.FeatureT2A;
        GameClient.ServerFeatureLBR = _config.FeatureLBR;
        GameClient.ServerFeatureAOS = _config.FeatureAOS;
        GameClient.ServerFeatureSE = _config.FeatureSE;
        GameClient.ServerFeatureML = _config.FeatureML;
        GameClient.ServerFeatureKR = _config.FeatureKR;
        GameClient.ServerFeatureSA = _config.FeatureSA;
        GameClient.ServerFeatureTOL = _config.FeatureTOL;
        GameClient.ServerFeatureExtra = _config.FeatureExtra;
        GameClient.ServerMaxCharsPerAccount = Math.Clamp(_config.MaxCharsPerAccount, 1, 7);
        GameClient.ServerMinCharDeleteDays = Math.Max(0, _config.MinCharDeleteTime);
        GameClient.ServerAutoResDisp = _config.AutoResDisp;
        GameClient.ServerToolTipMode = _config.ToolTipMode;
        _log.LogInformation("Source-X feature masks: T2A={T2A:X} LBR={LBR:X} AOS={AOS:X} SE={SE:X} ML={ML:X} SA={SA:X} TOL={TOL:X}",
            _config.FeatureT2A, _config.FeatureLBR, _config.FeatureAOS,
            _config.FeatureSE, _config.FeatureML, _config.FeatureSA, _config.FeatureTOL);
        GameClient.ServerOptionFlags = (SphereNet.Core.Enums.OptionFlags)(uint)_config.OptionFlags;
        // Upper bound for the client's 0xBF.0x1C view-size request; the cap was
        // hardcoded to the default so the ini key did nothing.
        SphereNet.Network.State.NetState.MapViewSizeMax = (byte)Math.Clamp(_config.MapViewSizeMax, 5, 255);
        Character.DeadCannotSeeLiving = _config.DeadCannotSeeLiving;

        // Wire notoriety tuning from sphere.ini into Character statics so that
        // MakeCriminal() / TickNotorietyDecay() use the configured values.
        Character.HitpointPercentOnRez     = _config.HitpointPercentOnRez;
        Character.PacketDeathAnimationEnabled = _config.PacketDeathAnimation != 0;
        Character.CriminalTimerSeconds     = _config.CriminalTimer;
        Character.MurderMinCount           = _config.MurderMinCount;
        Character.MurderDecayTimeSeconds   = _config.MurderDecayTime;
        Character.PlayerKarmaEvil          = _config.PlayerKarmaEvil;
        Character.PlayerKarmaNeutral       = _config.PlayerKarmaNeutral;
        Character.AttackingIsACrimeEnabled        = _config.AttackingIsACrime;
        Character.HelpingCriminalsIsACrimeEnabled = _config.HelpingCriminalsIsACrime;
        Character.SnoopCriminalEnabled            = _config.SnoopCriminal;
        Character.ReagentsRequiredEnabled  = _config.ReagentsRequired;
        Character.SpellbookRequiredEnabled = _config.SpellbookRequired;
        // Source-X CServerConfig RC_COMBATFLAGS normalize: PREHIT and
        // SWING_NORANGE cannot coexist — SWING_NORANGE is turned off with a
        // warning. The runtime already treats PREHIT as dominant; clearing the
        // bit here keeps flag read-backs consistent with the behavior.
        int combatFlags = _config.CombatFlags;
        const int prehitAndNoRange =
            (int)(SphereNet.Game.Combat.CombatFlags.PreHit | SphereNet.Game.Combat.CombatFlags.SwingNoRange);
        if ((combatFlags & prehitAndNoRange) == prehitAndNoRange)
        {
            combatFlags &= ~(int)SphereNet.Game.Combat.CombatFlags.SwingNoRange;
            _log.LogWarning("CombatFlags: COMBAT_PREHIT and COMBAT_SWING_NORANGE cannot coexist. Turning off COMBAT_SWING_NORANGE.");
        }
        Character.CombatFlags              = combatFlags;
        Character.CombatDamageEra          = _config.CombatDamageEra;
        Character.CombatHitChanceEra       = _config.CombatHitChanceEra;
        Character.CombatSpeedEra           = _config.CombatSpeedEra;
        Character.CombatParryingEra        = _config.CombatParryingEra;
        Character.CombatSpeedScaleFactor   = _config.SpeedScaleFactor;
        Character.FeatureSE                = _config.FeatureSE;
        Character.FeatureAOS               = _config.FeatureAOS;
        Character.RacialFlags              = _config.RacialFlags;
        Character.ArcheryMinDist           = _config.ArcheryMinDist;
        Character.ArcheryMaxDist           = _config.ArcheryMaxDist;
        Character.CombatArcheryMovementDelay = _config.CombatArcheryMovementDelay;
        Character.CombatMeleeMovementDelay  = _config.CombatMeleeMovementDelay;
        Character.MagicFlags = _config.MagicFlags;
        Character.EquippedCastEnabled = _config.EquippedCast;
        Character.ReagentLossAbort = _config.ReagentLossAbort;
        Character.ReagentLossFail = _config.ReagentLossFail;
        Character.ManaLossAbort = _config.ManaLossAbort;
        Character.ManaLossFail = _config.ManaLossFail;
        Character.ManaLossPercent = _config.ManaLossPercent;
        Character.MapViewRadarTiles = _config.MapViewRadar > 0
            ? _config.MapViewRadar
            : _config.MapViewSize;
        Character.AttackerTimeoutSeconds = _config.AttackerTimeout;
        Character.RegenHitsSeconds = _config.RegenHits;
        Character.RegenStamSeconds = _config.RegenStam;
        Character.RegenManaSeconds = _config.RegenMana;
        Character.RegenFoodSeconds = _config.RegenFood;

        // Config-driven fame/karma limits (were hardcoded in DeathEngine).
        SphereNet.Game.Death.DeathEngine.MaxFame = _config.MaxFame;
        SphereNet.Game.Death.DeathEngine.MaxKarma = _config.MaxKarma;
        SphereNet.Game.Death.DeathEngine.MinKarma = _config.MinKarma;
        // Config-driven default network view range (sphere.ini MapViewSize); a client
        // may still request its own via 0xC8.
        SphereNet.Network.State.NetState.DefaultViewRange =
            (byte)Math.Clamp(_config.MapViewSize, 1, 255);

        // --- 5. World ---
        _world = new GameWorld(_loggerFactory);
        // Config-driven world clock (was a hardcoded 20s per game minute).
        _world.GameMinuteLengthMs = Math.Max(1, _config.GameMinuteLength) * 1000;
        _world.MaxContainerItems = _config.ContainerMaxItems;
        SphereNet.Game.Objects.Items.Item.ItemsMaxAmount = _config.ItemsMaxAmount;
        _world.MaxBankItems      = _config.BankMaxItems;
        _world.MaxBankWeight        = _config.BankMaxWeight;
        _world.MaxContainerWeight   = _config.ContainerMaxWeight;
        _world.ToolTipMode       = _config.ToolTipMode;
        _world.ToolTipCache      = _config.ToolTipCache;
        _world.LightDay          = _config.LightDay;
        _world.LightNight        = _config.LightNight;
        _world.DungeonLight      = _config.DungeonLight;
        PacketCharList.AosTooltipsEnabled = _config.ToolTipMode != 0;
        foreach (var mapDef in _config.Maps)
        {
            _world.InitMap(mapDef.MapSendId, mapDef.MaxX, mapDef.MaxY);
            try
            {
                // Read the MUL data by MapReadId (which map*.mul group to load); the
                // world/client id is MapSendId. They differ only when a shard remaps.
                _mapData.InitMap(mapDef.MapReadId, mapDef.MaxX, mapDef.MaxY);
            }
            catch (FileNotFoundException ex)
            {
                _log.LogCritical("Map {Id} data missing: {Message}", mapDef.MapSendId, ex.Message);
                throw;
            }
        }

        // --- 6. Accounts ---
        _accounts = new AccountManager(_loggerFactory);
        _accounts.AutoCreateAccounts = _config.AccApp != 0;
        _accounts.DefaultMaxChars = Math.Clamp(_config.MaxCharsPerAccount, 1, 7);
        _accounts.Md5Passwords = _config.Md5Passwords;
        _accounts.DefaultPrivLevel = (PrivLevel)_config.DefaultCommandLevel;
        if (_accounts.DefaultPrivLevel < PrivLevel.Counsel)
        {
            _log.LogWarning("DefaultCommandLevel={Level} ({Num}). GM commands require Counsel+.",
                _accounts.DefaultPrivLevel, (int)_accounts.DefaultPrivLevel);
        }
        string accountsDir = ResolvePath(basePath, _config.AccountDir);
        Directory.CreateDirectory(accountsDir);
        SphereNet.Persistence.Accounts.AccountPersistence.Load(
            _accounts, accountsDir, _loggerFactory.CreateLogger("AccountPersistence"));

        // --- 7. Persistence ---
        _saver = new WorldSaver(_loggerFactory)
        {
            Format = _config.SaveFormat,
            ShardCount = _config.SaveShards,
            ShardSizeBytes = _config.SaveShardSizeMb * 1024L * 1024L,
            BackupLevels = _config.BackupLevels,
        };
        _saver.ResolveResourceName = rid =>
        {
            if (rid.Type != ResType.Events)
                return null;
            var link = _resources.GetResource(rid);
            if (!string.IsNullOrWhiteSpace(link?.DefName))
                return link!.DefName!;
            return null;
        };
        _saver.ResolveItemDefName = baseId =>
        {
            var def = DefinitionLoader.GetItemDef(baseId);
            return def?.DefName;
        };
        _saver.ResolveCharDefName = charDefIndex =>
            CharDefHelper.ResolveDefName(charDefIndex);
        _loader = new WorldLoader(_loggerFactory);
        _loader.ApplyCharDefFromName = (ch, defname) =>
            CharDefHelper.TryApplyDefName(ch, defname, _resources);
        _loader.ResolveBodyFromCharDefIndex = idx =>
            CharDefHelper.ResolveBodyId(idx, _resources);
        // Classic Sphere saves store worn clothing under CONT with no LAYER and no
        // itemdef layer (TYPE=t_clothing); the equip layer comes from tiledata (the
        // graphic's Quality byte). tiledata is loaded before the world save, so the
        // loader can resolve it and dress NPCs instead of leaving them naked.
        _loader.ResolveEquipLayerFromTile = baseId =>
            _mapData?.GetItemTileData(baseId).Quality ?? 0;
        _loader.ResolveItemDef = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                // A def without an explicit ID override has DispIndex 0 —
                // `def?.DispIndex ?? rid.Index` returned that 0 (the null-
                // coalescing never fired), importing e.g. every 56T
                // i_worldgem_bit spawner with BaseId 0.
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };
        _loader.ResolveItemDefFullIndex = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
        };
        _loader.ResolveCharDef = defname =>
        {
            int idx = CharDefHelper.ResolveDefIndex(defname, _resources);
            return idx != 0 ? CharDefHelper.ResolveBodyId(idx, _resources) : (ushort)0;
        };
        SphereNet.Game.Objects.Items.Item.ResolveDefName = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            if (rid.IsValid && rid.Type == ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                return def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
            return 0;
        };

        // Wire the world resolver BEFORE the save loads. The Item.ContainedIn
        // setter only updates the container reverse index (what the client's
        // open-container 0x3C batch reads) when this resolver is set; the engine
        // wiring that also sets it runs AFTER the load, so without this every
        // saved container's contents were absent from the index (bags rendered
        // empty on the client while .edit still listed them). WorldLoader.Load
        // also rebuilds the index at the end as a backstop.
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => _world;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => _world;

        // MULTIDEF defname -> multi id. Ship/house deeds carry their multi
        // reference as MORE=<multidef defname> (e.g. m_small_ship_n); the id can
        // legitimately be 0, so callers must distinguish "not a multi" (-1).
        SphereNet.Game.Objects.Items.Item.ResolveMultiDefId = defname =>
        {
            var rid = _resources.ResolveDefName(defname);
            return rid.IsValid && rid.Type == ResType.MultiDef ? rid.Index : -1;
        };

        // Script-declared globals ([GLOBALS]/[WORLDVARS]/[LIST name]/[WORLDLISTS])
        // seed the world before the save loads, so persisted values win —
        // same net effect as Source-X loading scripts before the world file.
        int seededVars = 0, seededLists = 0;
        foreach (var kv in _resources.ScriptGlobalVars)
        {
            _world.SetGlobalVar(kv.Key, kv.Value);
            seededVars++;
        }
        foreach (var (listName, elements) in _resources.ScriptGlobalLists)
        {
            var list = _world.GetOrCreateList(listName);
            list.Clear();
            list.AddRange(elements);
            seededLists++;
        }
        if (seededVars > 0 || seededLists > 0)
            _log.LogInformation("Script globals seeded: {Vars} VARs, {Lists} LISTs", seededVars, seededLists);

        // Definitions MUST be built before the world save loads: legacy Sphere
        // saves store an NPC's body only via the [WORLDCHAR <defname>] header, and
        // the loader resolves it through DefinitionLoader (empty until LoadAll).
        // Loading defs afterwards left every legacy NPC at body 0 / invisible, and
        // left spawn items without ITEMDEF type inheritance.
        LoadDefinitions();

        string savePath = ResolvePath(basePath, _config.WorldSaveDir);
        if (Directory.Exists(savePath))
        {
            var (items, chars) = _loader.Load(_world, savePath, _accounts);
            _log.LogInformation("World loaded: {Items} items, {Chars} chars", items, chars);

            // A trade window cannot survive a restart: its session lived only in
            // memory. Give the goods back before anything else looks at the world.
            RecoverInterruptedTrades();

            // Initialize spawn components for IT_SPAWN_CHAR items
            InitializeSpawnItems();

            // Boot-time consistency canary: a single O(n) pass that flags the
            // "fine in .edit, broken on the client" inconsistencies (empty-looking
            // bags, unmaterialised item types, runaway spawners) right after load,
            // so they surface in the log instead of only in-game. Same checks the
            // WorldInvariantAuditor tests assert against the live data.
            AuditLoadedWorld();
        }

        // --- 7a. Teleporters from [TELEPORTERS] script sections ---
        PlaceTeleporters();

        InitializeGameEngines(basePath);

        // Heal NPCs whose base stats an older build zeroed and persisted.
        // Needs the trigger dispatcher (@Create re-roll), so it runs after
        // engine wiring rather than inside InitializeSpawnItems.
        RepairZeroStatNpcs();

        LoadRegionsAndRecipes();

        int restoredSpellEffects = _spellEngine.RestorePersistedEffectsFromWorld();
        if (restoredSpellEffects > 0)
            _log.LogInformation("Restored {Count} active spell effects from world save", restoredSpellEffects);

        if (!StartNetwork())
            return;

        InitializeAdminSurfaces();

        RunMainLoop();
    }
}
