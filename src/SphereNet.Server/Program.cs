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
#if WINFORMS
using SphereNet.Server.Admin;
#endif
using Color = System.Drawing.Color;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SphereNet.Server;

public static class Program
{
    private static readonly AnsiConsoleTheme WarningConsoleTheme = new(
        new Dictionary<ConsoleThemeStyle, string>
        {
            [ConsoleThemeStyle.LevelWarning] = "\x1b[38;5;196m",
            [ConsoleThemeStyle.LevelError] = "\x1b[38;5;203m",
            [ConsoleThemeStyle.LevelFatal] = "\x1b[38;5;15m"
        });

    private static SphereConfig _config = null!;
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
    private static SphereNet.Game.Ships.ShipEngine? _shipEngine;
    private static SphereNet.Game.Mounts.MountEngine? _mountEngine;
    private static SphereNet.Game.Diagnostics.StressTestEngine? _stressEngine;
    private static SphereNet.Game.Diagnostics.BotEngine? _botEngine;
    private static long _lastBotRestockMs;
    private static SphereNet.Game.NPCs.StableEngine _stableEngine = new();
    private static SphereNet.Game.Scheduling.TimerWheel _npcTimerWheel = null!;
    private static SphereNet.Game.Recording.RecordingEngine _recordingEngine = null!;
    private static TriggerDispatcher _triggerDispatcher = null!;
    private static TriggerRunner _triggerRunner = null!;
    private static ScriptSystemHooks _systemHooks = null!;
    private static ScriptDbAdapter _scriptDb = null!;
    private static ScriptDbAdapter _scriptLdb = null!;
    private static ScriptFileHandle? _scriptFile;
    private static readonly ServerHookContext _serverHookContext = new();
    private static TelnetConsole? _telnet;
    private static WebStatusServer? _webStatus;
#if WINFORMS
    private static ConsoleForm? _consoleForm;
#endif
    private static byte _lastGlobalLight;
    private static AdminCommandProcessor? _consoleProcessor;

    private static bool _running;
    private static bool _multicoreRuntimeEnabled;
    private static int _tickCounter;
    private static DateTime _serverStartTime;
    private static int _saveCount;
    private static readonly List<GameClient> _reusableClientSnapshot = [];
    // Reuse compute-phase buffers across ticks to keep gen-0 GC pressure
    // low. The dictionary/bag allocations were measurable on low-core
    // VDS hosts (~30–100 ms slow-tick spikes when GC kicked in).
    private static readonly List<NpcAI.NpcDecision> _reusableDecisionList = [];
    private static readonly Dictionary<int, GameClient.ClientViewDelta> _reusableClientDeltas = [];
    private static readonly Dictionary<Serial, long> _summonedGuardExpiry = [];
    private static long _nextDecayCatchupTick;
    private static long _telemetrySnapshotUs;
    private static long _telemetryComputeUs;
    private static long _telemetryApplyUs;
    private static long _telemetryFlushUs;
    private static long _telemetryMaxTickUs;
    private static long _lastSlowTickWarningMs;
    private static long _lastTickStatsLogMs;
    private static long _tickStatsTotalUs;
    private static long _tickStatsMaxUs;
    private static int _tickStatsCount;
    private static bool _headless;

    [STAThread]
    public static void Main(string[] args)
    {
        _headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase) ||
                                   a.Equals("--nogui", StringComparison.OrdinalIgnoreCase)) ||
                    !IsWinFormsAvailable();

        if (_headless)
        {
            RunHeadless(args);
        }
        else
        {
            RunWithGui(args);
        }
    }

    private static bool IsWinFormsAvailable()
    {
#if WINFORMS
        return true;
#else
        return false;
#endif
    }

    private static void RunHeadless(string[] args)
    {
        Console.WriteLine("SphereNet starting in headless mode...");
        // Console input thread — reads stdin and queues commands
        var inputThread = new Thread(() =>
        {
            while (_running)
            {
                try
                {
                    string? line = Console.ReadLine();
                    if (line == null) break; // stdin closed (e.g. nohup / systemd)
                    _headlessCommandQueue.Enqueue(line);
                }
                catch { break; }
            }
        })
        {
            IsBackground = true,
            Name = "ConsoleInput"
        };

        // Handle Ctrl+C gracefully
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _running = false;
        };

        _running = true;
        inputThread.Start();
        ServerMain(args);
    }

    private static readonly ConcurrentQueue<string> _headlessCommandQueue = new();

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
                ConsoleAppend(msg, Color.Red);

            // Keep form open so user can read the error
        }
    }

    private static void ServerMainInner(string[] args)
    {
        // --- 1. Configuration ---
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        string iniPath = FindConfigFile(basePath, "sphere.ini");
        if (iniPath == "")
        {
            ConsoleAppend("ERROR: sphere.ini not found.", Color.Red);
            return;
        }

        ConsoleAppend($"Loading config: {iniPath}", Color.LightGray);
        var iniParser = new IniParser();
        iniParser.Load(iniPath);
        _config = new SphereConfig();
        _config.LoadFromIni(iniParser);
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
        string filePath = Path.Combine(basePath, "logs", "spherenet-.log");
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
        ConsoleAppend("===========================================", Color.Cyan);
        ConsoleAppend("  SphereNet — Ultima Online Server", Color.Cyan);
        ConsoleAppend("  Source-X Architecture / .NET 9 Port", Color.Cyan);
        ConsoleAppend("===========================================", Color.Cyan);
        ConsoleAppend("", Color.LightGray);

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
        var scriptDirs = ResolveScriptDirectories(basePath, _config.ScpFilesDir);
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
        string mulPath = _config.MulFilesDir;
        if (string.IsNullOrEmpty(mulPath)) mulPath = FindDir(basePath, "mul");
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

        _mapData = new MapDataManager(mulPath);
        _mapData.OnMapFileLoaded += (id, path) =>
            _log.LogInformation("Map{Id} loaded from: {Path}", id, path);
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

        // Combine config FEATURE* values into one OR mask for 0xB9 SupportedFeatures.
        // GameClient reads this during game-login before sending the char list.
        GameClient.ServerFeatureFlags = (uint)(
            _config.FeatureT2A |
            _config.FeatureLBR |
            _config.FeatureAOS |
            _config.FeatureSE  |
            _config.FeatureML  |
            _config.FeatureKR  |
            _config.FeatureSA  |
            _config.FeatureTOL |
            _config.FeatureExtra);
        _log.LogInformation("Server feature flags (from sphere.ini): 0x{Flags:X8}",
            GameClient.ServerFeatureFlags);

        // Wire notoriety tuning from sphere.ini into Character statics so that
        // MakeCriminal() / TickNotorietyDecay() use the configured values.
        Character.CriminalTimerSeconds     = _config.CriminalTimer;
        Character.MurderMinCount           = _config.MurderMinCount;
        Character.MurderDecayTimeSeconds   = _config.MurderDecayTime;
        Character.AttackingIsACrimeEnabled        = _config.AttackingIsACrime;
        Character.HelpingCriminalsIsACrimeEnabled = _config.HelpingCriminalsIsACrime;
        Character.SnoopCriminalEnabled            = _config.SnoopCriminal;
        Character.ReagentsRequiredEnabled  = _config.ReagentsRequired;

        // --- 5. World ---
        _world = new GameWorld(_loggerFactory);
        _world.MaxContainerItems = _config.ContainerMaxItems;
        _world.MaxBankItems      = _config.BankMaxItems;
        _world.MaxBankWeight        = _config.BankMaxWeight;
        _world.MaxContainerWeight   = _config.ContainerMaxWeight;
        _world.ToolTipMode       = _config.ToolTipMode;
        PacketCharList.AosTooltipsEnabled = _config.ToolTipMode != 0;
        foreach (var mapDef in _config.Maps)
        {
            _world.InitMap(mapDef.MapSendId, mapDef.MaxX, mapDef.MaxY);
            try
            {
                _mapData.InitMap(mapDef.MapSendId, mapDef.MaxX, mapDef.MaxY);
            }
            catch (FileNotFoundException ex)
            {
                _log.LogCritical("Map {Id} data missing: {Message}", mapDef.MapSendId, ex.Message);
                throw;
            }
        }

        // --- 6. Accounts ---
        _accounts = new AccountManager(_loggerFactory);
        _accounts.AutoCreateAccounts = true;
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
        _loader = new WorldLoader(_loggerFactory);

        string savePath = ResolvePath(basePath, _config.WorldSaveDir);
        if (Directory.Exists(savePath))
        {
            var (items, chars) = _loader.Load(_world, savePath, _accounts);
            _log.LogInformation("World loaded: {Items} items, {Chars} chars", items, chars);

            // Initialize spawn components for IT_SPAWN_CHAR items
            InitializeSpawnItems();
        }

        // --- 7a. Teleporters from [TELEPORTERS] script sections ---
        PlaceTeleporters();

        // --- 7b. Game Engines ---
        _log.LogInformation("Initializing game engines...");
        _triggerDispatcher = new TriggerDispatcher();
        _triggerDispatcher.Resources = _resources;
        var exprParser = new ExpressionParser
        {
            DiagnosticLogger = msg =>
            {
                // Surface unresolved script expressions both in the log and in
                // the GUI console so the user spots missing properties while a
                // specific command/dialog is running.
                _log?.LogWarning("{Msg}", msg);
                ConsoleAppend(msg, Color.Orange);
            }
        };
        var scriptInterpreter = new ScriptInterpreter(exprParser, _loggerFactory.CreateLogger<ScriptInterpreter>());
        _triggerRunner = new TriggerRunner(scriptInterpreter, _resources, _loggerFactory.CreateLogger<TriggerRunner>());
        _triggerRunner.ScriptDebug = _config.ScriptDebug;
        _triggerDispatcher.ScriptDebug = _config.ScriptDebug;
        _triggerDispatcher.DebugLog = msg => _log?.LogDebug("{Msg}", msg);
        _systemHooks = new ScriptSystemHooks(_triggerRunner);
        RegisterDbProviders();
        _scriptDb = new ScriptDbAdapter(_loggerFactory.CreateLogger<ScriptDbAdapter>());
        _scriptLdb = new ScriptDbAdapter(_loggerFactory.CreateLogger<ScriptDbAdapter>());
        InitDbConnections(_config, _scriptDb);
        if (_config.HasFileCommands)
        {
            string fileBasePath = Path.Combine(Path.GetDirectoryName(_config.ScpFilesDir) ?? ".", "files");
            _scriptFile = new ScriptFileHandle(fileBasePath);
            _log.LogInformation("Script FILE commands enabled, base path: {Path}", fileBasePath);
        }
        scriptInterpreter.CallFunction = (name, target, source, args) =>
            _triggerRunner.TryRunFunction(name, target, source, args, out var callResult)
                ? callResult
                : TriggerResult.Default;
        scriptInterpreter.ResolveFunctionExpression = (name, argString, target, source, args) =>
            _triggerRunner.TryEvaluateFunction(name, argString, target, source, args, out var value)
                ? value
                : null;
        scriptInterpreter.ServerPropertyResolver = ResolveServerProperty;
        _triggerDispatcher.Runner = _triggerRunner;

        // Wire @SkillGain trigger via callback
        SkillEngine.OnSkillGain = (ch, skill, newVal) =>
        {
            _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillGain,
                new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = newVal });
        };
        // Wire scripted (custom) skill trigger chain
        SkillHandlers.OnScriptedSkillUse = (ch, skill) =>
        {
            var args = new TriggerArgs { CharSrc = ch, N1 = (int)skill };

            // @SkillSelect — return 1 cancels
            var selResult = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillSelect, args);
            if (selResult == TriggerResult.True)
                return false;

            // @SkillStart — scripts can set ACTDIFF via tags
            _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillStart, args);

            // Difficulty from ACTDIFF tag or default 50
            int difficulty = 50;
            if (ch.TryGetTag("ACTDIFF", out string? actDiff) && !string.IsNullOrEmpty(actDiff) && int.TryParse(actDiff, out int d))
                difficulty = d;

            bool success = SkillEngine.CheckSuccess(ch, skill, difficulty);

            if (success)
            {
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillSuccess,
                    new TriggerArgs { CharSrc = ch, N1 = (int)skill });
            }
            else
            {
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillFail,
                    new TriggerArgs { CharSrc = ch, N1 = (int)skill });
            }

            // Gain experience (fires @SkillGain via existing callback)
            SkillEngine.GainExperience(ch, skill, success ? difficulty : -difficulty);

            return success;
        };
        SkillHandlers.OnCraftSkillUsed = (ch, skill) =>
        {
            // Find the GameClient for this character and open crafting gump
            foreach (var client in _clients.Values)
            {
                if (client.Character == ch)
                {
                    client.OpenCraftingGump(skill);
                    break;
                }
            }
        };
        _terrain = new TerrainEngine(_mapData);
        _movement = new MovementEngine(_world, _triggerDispatcher);
        _movement.SpellEngine = _spellEngine;
        _movement.OnSysMessage = (mover, text) =>
        {
            // Source-X CClient::SysMessage routes region-enter/PvP/guard text
            // only to the moving player's own client.
            foreach (var c in _clients.Values)
            {
                if (c.Character == mover) { c.SysMessage(text); break; }
            }
        };

        _movement.OnTeleport = (mover, dest) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == mover)
                {
                    c.SendSelfRedraw();
                    var snd = new PacketSound(0x01FE, mover.X, mover.Y, mover.Z);
                    BroadcastNearby(mover.Position, 18, snd, 0);
                    break;
                }
            }
        };

        // Source-X CCharBase::Region_Notify centralised at world level so
        // walk, .go teleport, recall and gate all hit one notify path.
        _world.OnRegionChanged = (mover, oldRegion, newRegion) =>
        {
            _log.LogDebug(
                "[REGION_CHANGE] {Name} {Old} -> {New} player={IsPlayer}",
                mover.Name,
                oldRegion?.Name ?? "<null>",
                newRegion?.Name ?? "<null>",
                mover.IsPlayer);

            if (!mover.IsPlayer || newRegion == null) return;
            SphereNet.Game.Clients.GameClient? gc = null;
            foreach (var c in _clients.Values)
                if (c.Character == mover) { gc = c; break; }
            if (gc == null) { _log.LogDebug("[REGION_CHANGE] no GameClient for {Name}", mover.Name); return; }

            gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                SphereNet.Game.Messages.Msg.MsgRegionEnter, newRegion.Name));
            if (newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded))
            {
                // Source-X CChar::Region_Notify: SysMessagef(DEFMSG_REGION_GUARDS_1,
                // <GUARDOWNER tag value>). When the AREADEF omits TAG.GUARDOWNER
                // it falls back to DEFMSG_REGION_GUARD_ART ("the") so the literal
                // "%s" never reaches the player.
                string guardOwner;
                if (!newRegion.TryGetTag("GUARDOWNER", out var owner) || string.IsNullOrEmpty(owner))
                    guardOwner = SphereNet.Game.Messages.ServerMessages.Get(
                        SphereNet.Game.Messages.Msg.MsgRegionGuardArt);
                else
                    guardOwner = owner!;
                gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                    SphereNet.Game.Messages.Msg.MsgRegionGuards1, guardOwner));
            }
            if (newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.NoPvP))
                gc.SysMessage(SphereNet.Game.Messages.ServerMessages.Get(
                    SphereNet.Game.Messages.Msg.MsgRegionPvpsafe));

            // Source-X also fires DEFMSG_REGION_GUARDS_2 when leaving a guarded
            // zone for an unguarded one — keeps the "you have left the
            // protection of the city guards" callout symmetric.
            if (oldRegion != null &&
                oldRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded) &&
                !newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded))
            {
                string guardOwner;
                if (!oldRegion.TryGetTag("GUARDOWNER", out var owner) || string.IsNullOrEmpty(owner))
                    guardOwner = SphereNet.Game.Messages.ServerMessages.Get(
                        SphereNet.Game.Messages.Msg.MsgRegionGuardArt);
                else
                    guardOwner = owner!;
                gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                    SphereNet.Game.Messages.Msg.MsgRegionGuards2, guardOwner));
            }
        };

        // Source-X parity: spawn-driven NPCs (CItemSpawn) need the same
        // trigger sequence the manual ".add" pipeline runs. Without this
        // hook a vendor that comes from a SPAWN never fires @NPCRestock,
        // so its stock list stays empty and "buy" responds with "no
        // goods". Mirrors the GameClient.CreateNpcFromDef ordering.
        _world.OnNpcSpawned = npc =>
        {
            try
            {
                _triggerDispatcher?.FireCharTrigger(
                    npc, SphereNet.Core.Enums.CharTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

                if (npc.NpcBrain == SphereNet.Core.Enums.NpcBrainType.None)
                    npc.NpcBrain = SphereNet.Core.Enums.NpcBrainType.Animal;

                if (npc.NpcBrain == SphereNet.Core.Enums.NpcBrainType.Vendor)
                {
                    _triggerDispatcher?.FireCharTrigger(
                        npc, SphereNet.Core.Enums.CharTrigger.NPCRestock,
                        new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
                }

                _triggerDispatcher?.FireCharTrigger(
                    npc, SphereNet.Core.Enums.CharTrigger.CreateLoot,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

                var spawnCharDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
                if (spawnCharDef != null)
                {
                    foreach (int spellId in spawnCharDef.NpcSpells)
                    {
                        if (Enum.IsDefined(typeof(SpellType), spellId))
                            npc.NpcSpellAdd((SpellType)spellId);
                    }
                }

                npc.Hits = npc.MaxHits;
                npc.Stam = npc.MaxStam;
                npc.Mana = npc.MaxMana;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[spawn_hook] failed for {Name}", npc.Name);
            }
        };

        _partyManager = new PartyManager();
        _guildManager = new GuildManager();
        _guildManager.DeserializeFromWorld(_world);
        if (_guildManager.GuildCount > 0)
            _log.LogInformation("Restored {Count} guilds from world save", _guildManager.GuildCount);
        _speech = new SpeechEngine(_world);
        _speech.PartyManager = _partyManager;
        _speech.GuildManager = _guildManager;
        _speech.OnNpcHear += OnNpcHearSpeech;
        _speech.OnPlayerSpeech += OnPlayerSpeech;
        _commands = new CommandHandler();
        _commands.TriggerDispatcher = _triggerDispatcher;
        _commands.CommandPrefix = string.IsNullOrEmpty(_config.CommandPrefix) ? '.' : _config.CommandPrefix[0];
        _commands.Resources = _resources;
        _commands.ScriptFallbackExecutor = (gm, commandLine) =>
        {
            int spaceIdx = commandLine.IndexOf(' ');
            string verb = (spaceIdx > 0 ? commandLine[..spaceIdx] : commandLine).Trim();
            if (string.IsNullOrEmpty(verb))
                return false;
            string args = spaceIdx > 0 ? commandLine[(spaceIdx + 1)..].Trim() : "";

            // Run script function in the active client context when possible,
            // so client-bound verbs (DIALOG, TARGET*, SERV.ALLCLIENTS, etc.)
            // and compatibility vars (GETREFTYPE/ISDIALOGOPEN) resolve correctly.
            GameClient? scriptConsole = null;
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm ||
                    (c.Character != null && c.Character.Uid == gm.Uid))
                {
                    scriptConsole = c;
                    break;
                }
            }

            if (exprParser.DebugUnresolved)
            {
                _log.LogDebug("[script_call] verb={Verb} char=0x{Char:X8} sourceConsole={HasConsole} args='{Args}'",
                    verb, gm.Uid.Value, scriptConsole != null, args);
            }

            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(gm, 0, 0, args)
            {
                Object1 = gm,
                Object2 = gm
            };

            if (!_triggerRunner.TryRunFunction(verb, gm, scriptConsole, trigArgs, out var result))
            {
                if (exprParser.DebugUnresolved)
                    _log.LogDebug("[script_call] verb={Verb} not found", verb);
                return false;
            }

            if (exprParser.DebugUnresolved)
                _log.LogDebug("[script_call] verb={Verb} result={Result}", verb, result);

            // Sphere parity:
            // Script verbs are typically written as [FUNCTION ADMIN], [FUNCTION SHOW], etc.,
            // and many do not use explicit RETURN 1. If the function exists and ran, treat
            // it as handled. Built-ins are still preserved because SpeechEngine invokes
            // script fallback first, then built-ins only when script function is missing.
            if (verb.StartsWith("f_", StringComparison.OrdinalIgnoreCase) ||
                !verb.Contains('_'))
                return true;

            return result == TriggerResult.True;
        };
        _commands.RegisterDefaults(_world);
        int scriptCmdCount = _commands.LoadScriptCommandPrivileges(_resources);
        _log.LogInformation("Loaded {Count} script command privilege entries from [PLEVEL] sections.", scriptCmdCount);
        _commands.OnResyncCommand += PerformScriptResync;
        _commands.OnSysMessage += (ch, msg) =>
        {
            // Also log so diagnostic command output (e.g. .statics) is visible
            // in the log file without decoding the 0xAE unicode speech packet.
            _log.LogDebug("[sysmsg → {Name}] {Message}", ch.GetName(), msg);
            foreach (var c in _clients.Values)
            {
                if (c.Character == ch)
                {
                    c.SysMessage(msg);
                    break;
                }
            }
        };
        _commands.OnCharVisualUpdate += (ch) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == ch)
                {
                    c.SendSelfRedraw();
                    break;
                }
            }
        };
        _commands.OnScriptParityWarning += (ch, verb, reason) =>
        {
            _log.LogWarning("Script parity warning: char=0x{Char:X8} cmd={Cmd} reason={Reason}",
                ch.Uid.Value, verb, reason);
        };
        _commands.OnCharacterResyncRequested += target =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == target)
                {
                    c.Resync();
                    break;
                }
            }
        };
        _commands.OnCharacterMapChanged += target =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == target)
                {
                    c.HandleMapChanged();
                    break;
                }
            }
        };
        _commands.OnCharacterSelfRedraw += target =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == target)
                {
                    c.SendSelfRedraw();
                    break;
                }
            }
        };
        _commands.OnTeleportTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginTeleportTarget();
                    break;
                }
            }
        };
        _commands.OnAddTargetRequested += (gm, addToken) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginAddTarget(addToken);
                    break;
                }
            }
        };
        _commands.OnShowDialogRequested += (gm, title, lines) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.ShowTextDialog(title, lines);
                    break;
                }
            }
        };
        _commands.OnShowTargetRequested += (gm, args) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginShowTarget(args);
                    break;
                }
            }
        };
        _commands.OnEditTargetRequested += (gm, args) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginEditTarget(args);
                    break;
                }
            }
        };
        _commands.OnEditRequested += (gm, uid, page) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.ShowInspectDialog(uid, page);
                    break;
                }
            }
        };
        // Source-X parity (CClient.cpp:921) — generic X-prefix verb fallback.
        _commands.OnAddVerbTargetRequested += (gm, verb, verbArgs) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginXVerbTarget(verb, verbArgs);
                    break;
                }
            }
        };
        // Phase C: NUKE / NUKECHAR / NUDGE area-target verbs.
        _commands.OnAreaTargetRequested += (gm, verb, range) =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.BeginAreaTarget(verb, range); break; }
        };
        _commands.OnSummonToTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.BeginSummonToTarget(); break; }
        };
        _commands.OnControlTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.BeginControlTarget(); break; }
        };
        _commands.OnDupeTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.BeginDupeTarget(); break; }
        };
        _commands.OnHealTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.BeginHealTarget(); break; }
        };
        _commands.OnBankTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.BeginBankTarget(); break; }
        };
        _commands.OnBankSelfRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.OpenBankBox(); break; }
        };
        _commands.OnUnmountRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.UnmountSelf(); break; }
        };
        _commands.OnAnimRequested += (gm, animId) =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.PlayOwnAnimation(animId); break; }
        };
        _commands.OnMountTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.BeginMountTarget(); break; }
        };
        _commands.OnSummonCageTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
                if (c.Character == gm) { c.BeginSummonCageTarget(); break; }
        };
        _commands.OnInspectRequested += (gm, uid) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    var obj = _world.FindObject(new Serial(uid));
                    if (obj != null)
                        c.OpenInspectPropDialog(obj, 0);
                    break;
                }
            }
        };
        _commands.OnInspectTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginInspectTarget();
                    break;
                }
            }
        };
        _commands.OnRemoveTargetRequested += gm =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginRemoveTarget();
                    break;
                }
            }
        };
        _commands.OnKillRequested += (gm, targetUid) =>
        {
            var victim = !targetUid.HasValue || targetUid.Value.Value == 0
                ? gm
                : _world.FindChar(targetUid.Value);

            if (victim == null)
            {
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                    gmClient.SysMessage("Kill: target not found.");
                return;
            }

            if (victim.IsDead || victim.IsDeleted)
            {
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient2))
                    gmClient2.SysMessage($"'{victim.Name}' is already dead.");
                return;
            }

            BroadcastLightningStrike(victim);
            _deathEngine.ProcessDeath(victim, gm);
            if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient3))
                gmClient3.SysMessage($"Killed '{victim.Name}'.");
        };
        _commands.OnResurrectRequested += (gm, targetUid) =>
        {
            // No UID  → resurrect self.
            // With UID → resurrect that character (GM-only command, so we
            // don't gate on PrivLevel here; SpeechEngine.Register already
            // restricts the verb).
            var victim = !targetUid.HasValue || targetUid.Value.Value == 0
                ? gm
                : _world.FindChar(targetUid.Value);
            if (victim == null)
            {
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                    gmClient.SysMessage("Resurrect: target not found.");
                return;
            }

            if (!_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
            {
                // Offline / NPC — no client-side ghost transition needed,
                // just clear the dead state on the character object so the
                // next login or AI tick sees them alive.
                if (victim.IsDead)
                    victim.Resurrect();
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient2))
                    gmClient2.SysMessage($"Resurrected '{victim.Name}'.");
                return;
            }

            if (!victim.IsDead)
            {
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient3))
                    gmClient3.SysMessage($"'{victim.Name}' is not dead.");
                return;
            }

            victimClient.OnResurrect();
        };
        _commands.OnResurrectTargetRequested += gm =>
        {
            if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                gmClient.BeginResurrectTarget();
        };
        _commands.OnCastRequested += (ch, spellId) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == ch)
                {
                    c.HandleCastSpell((SpellType)spellId, 0);
                    break;
                }
            }
        };
        _commands.OnScriptDialogRequested += (ch, dialogName, page) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == ch)
                {
                    if (c.TryShowScriptDialog(dialogName, page))
                        break;

                    // Collect a few close-match suggestions so the user
                    // can tell if it's a typo ("d_moongate" vs
                    // "d_moongates") or a truly missing dialog.
                    var suggestions = CollectDialogSuggestions(dialogName, maxCount: 5);
                    string hint = suggestions.Count == 0
                        ? ""
                        : "  Similar: " + string.Join(", ", suggestions);
                    c.SysMessage($"Dialog '{dialogName}' not found.{hint}");
                    break;
                }
            }
        };
        _spellRegistry = new SpellRegistry();
        _spellEngine = new SpellEngine(_world, _spellRegistry);
        _spellEngine.OnPlaySound = (pos, soundId) =>
        {
            var pkt = new PacketSound(soundId, pos.X, pos.Y, pos.Z);
            BroadcastNearby(pos, 18, pkt, 0);
        };
        _spellEngine.OnPersonalLightChanged = target =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == target)
                {
                    c.SendPersonalLight();
                    break;
                }
            }
        };
        _spellEngine.OnSysMessage = (recipient, text) =>
        {
            // Route Recall/Mark/Gate/Poison spell messages to the recipient's
            // own client, mirroring Source-X CClientMsg::SysMessage semantics.
            foreach (var c in _clients.Values)
            {
                if (c.Character == recipient) { c.SysMessage(text); break; }
            }
        };
        _spellEngine.OnCasterFacingChanged = caster =>
        {
            // Source-X UpdateMove(GetTopPoint()) — broadcast new facing only.
            // Reuse the lightweight 0x77 MobileMoving so nearby clients
            // re-render the mobile in its new direction without a full
            // 0x78 char info refresh.
            byte dirByte = (byte)((byte)caster.Direction & 0x07);
            byte flags = 0;
            if (caster.IsInWarMode) flags |= 0x40;
            if (caster.IsDead) flags |= 0x02;
            byte noto = caster.IsPlayer ? (byte)1 : (byte)3;
            var pkt = new PacketMobileMoving(
                caster.Uid.Value, caster.BodyId,
                caster.X, caster.Y, caster.Z, dirByte,
                caster.Hue, flags, noto);
            BroadcastNearby(caster.Position, 18, pkt, 0);
        };
        _spellEngine.OnCastAnimation = (caster, animId) =>
        {
            ushort anim = caster.IsMounted ? MapAnimToMounted(animId) : animId;
            var animPkt = new PacketAnimation(caster.Uid.Value, anim);
            BroadcastNearby(caster.Position, 18, animPkt, 0);
        };
        GameRegion.ClientCountProvider = regionObj =>
        {
            int count = 0;
            foreach (var c in _clients.Values)
            {
                if (c.Character == null || !c.IsPlaying) continue;
                switch (regionObj)
                {
                    case GameRegion reg when reg.Contains(c.Character.Position):
                    case Room room when room.Contains(c.Character.Position):
                        count++;
                        break;
                }
            }
            return count;
        };
        _deathEngine = new DeathEngine(_world);
        // NOTE: DeathEngine.OnDeath fires from inside ProcessDeath, after the
        // corpse object is created but BEFORE the corpse spawn packets are
        // broadcast. We deliberately do NOT call GameClient.OnCharacterDeath
        // here, because:
        //   * Source-X order is: corpse appears → 0xAF death anim → ghost
        //     transition. Calling OnCharacterDeath inside this callback
        //     flips the player to a ghost body BEFORE the killer's client
        //     has even received the corpse packet, so observers briefly
        //     see "ghost without a corpse on the floor".
        //   * Both code paths that trigger player death (NpcAI.OnNpcKill
        //     in Program.cs and Player-vs-Player in GameClient.TrySwingAt)
        //     already invoke c.OnCharacterDeath() AFTER they finish sending
        //     corpse + 0xAF packets. Doing it here would call it twice.
        // This hook is kept for non-visual side effects (logging, party
        // bookkeeping, etc.) — currently nothing else needs it.
        _deathEngine.LootingIsACrime = _config.LootingIsACrime;
        _deathEngine.CorpseDecayNPC = _config.CorpseNpcDecay * 60;
        _deathEngine.CorpseDecayPlayer = _config.CorpsePlayerDecay * 60;
        _deathEngine.PartyManager = _partyManager;
        _tradeManager = new TradeManager();
        _npcAI = new NpcAI(_world, _config);
        _npcTimerWheel = new SphereNet.Game.Scheduling.TimerWheel(Environment.TickCount64);
        _recordingEngine = new SphereNet.Game.Recording.RecordingEngine(
            Path.Combine(ResolvePath(basePath, _config.WorldSaveDir), "recordings"));
        _recordingEngine.SnapshotNearbyCharacters = (center, range) =>
        {
            var result = new List<byte[]>();
            foreach (var ch in _world.GetCharsInRange(center, range))
            {
                if (ch.IsPlayer && !ch.IsOnline) continue;
                if (ch.IsInvisible || ch.IsStatFlag(StatFlag.Hidden)) continue;
                var equip = new List<(uint, ushort, byte, ushort)>();
                for (int layer = 1; layer <= (int)Layer.Horse; layer++)
                {
                    var item = ch.GetEquippedItem((Layer)layer);
                    if (item != null)
                        equip.Add((item.Uid.Value, item.DispIdFull, (byte)layer, item.Hue));
                }
                byte flags = 0;
                if (ch.IsInWarMode) flags |= 0x40;
                if (ch.IsInvisible) flags |= 0x80;
                var pkt = new PacketDrawObject(
                    ch.Uid.Value, ch.BodyId, ch.X, ch.Y, ch.Z,
                    (byte)ch.Direction, ch.Hue, flags, 0x01,
                    equip.ToArray());
                result.Add(pkt.Build().Span.ToArray());
            }
            return result;
        };
        _npcAI.OnNpcSay = (npc, text) =>
        {
            NpcSpeak(npc, text);
        };
        _npcAI.OnGuardLightningStrike = target =>
        {
            BroadcastLightningStrike(target);
        };
        _npcAI.OnNpcTeleport = npc =>
        {
            BroadcastCharacterAppear(npc);
        };
        _npcAI.OnNpcAttack = (attacker, target, damage) =>
        {
            // Broadcast the attacker's new facing first (Source-X
            // CChar::UpdateDir during Fight_Hit). Without a 0x77 here the
            // client keeps drawing the NPC facing its old direction even
            // though the AI has already turned it on the server side, and
            // the swing animation plays sideways.
            byte attackerDir = (byte)((byte)attacker.Direction & 0x07);
            byte attackerFlags = 0;
            if (attacker.IsInWarMode) attackerFlags |= 0x40;
            if (attacker.IsDead) attackerFlags |= 0x02;
            var movePkt = new PacketMobileMoving(
                attacker.Uid.Value, attacker.BodyId,
                attacker.X, attacker.Y, attacker.Z, attackerDir,
                attacker.Hue, attackerFlags, /*notoriety*/ 3);
            BroadcastNearby(attacker.Position, 18, movePkt, 0);

            ushort swingAnim = SphereNet.Game.Clients.GameClient.GetNpcSwingAction(attacker);
            var animPkt = new PacketAnimation(attacker.Uid.Value, swingAnim);
            BroadcastNearby(attacker.Position, 18, animPkt, 0);

            ushort getHitAction = target.IsMounted
                ? MapAnimToMounted((ushort)SphereNet.Core.Enums.AnimationType.GetHit)
                : (ushort)SphereNet.Core.Enums.AnimationType.GetHit;
            var getHitAnim = new PacketAnimation(target.Uid.Value, getHitAction);
            BroadcastNearby(target.Position, 18, getHitAnim, 0);

            var dmgPkt = new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue));
            BroadcastNearby(target.Position, 18, dmgPkt, 0);

            var healthPkt = new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits);
            BroadcastNearby(target.Position, 18, healthPkt, 0);

        };
        _npcAI.OnNpcKill = (killer, victim) =>
        {
            var effectiveKiller = ResolveEffectiveOffender(killer);

            _triggerDispatcher?.FireCharTrigger(effectiveKiller, CharTrigger.Kill,
                new TriggerArgs { CharSrc = effectiveKiller, O1 = victim });
            _triggerDispatcher?.FireCharTrigger(victim, CharTrigger.Death,
                new TriggerArgs { CharSrc = effectiveKiller });

            var victimPos = victim.Position;
            byte victimDir = (byte)((byte)victim.Direction & 0x07);
            var corpse = _deathEngine.ProcessDeath(victim, effectiveKiller);
            killer.FightTarget = Serial.Invalid;

            if (corpse != null)
            {
                uint corpseWireSerial = corpse.Uid.Value;
                if (corpse.Amount > 1)
                    corpseWireSerial |= 0x80000000u;

                if (victim.IsPlayer)
                {
                    var corpsePacket = new PacketWorldItem(
                        corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                        corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                        victimDir);
                    BroadcastNearby(victimPos, 18, corpsePacket, 0);

                    // Player corpse: send contents + equip map for paperdoll corpse rendering.
                    foreach (var corpseItem in corpse.Contents)
                    {
                        var containerItem = new PacketContainerItem(
                            corpseItem.Uid.Value,
                            corpseItem.DispIdFull,
                            0,
                            corpseItem.Amount,
                            corpseItem.X,
                            corpseItem.Y,
                            corpse.Uid.Value,
                            corpseItem.Hue,
                            useGridIndex: true);
                        BroadcastNearby(victimPos, 18, containerItem, 0);
                    }

                    var corpseEquipEntries = new List<(byte Layer, uint ItemSerial)>();
                    var usedLayers = new HashSet<byte>();
                    foreach (var item in corpse.Contents)
                    {
                        byte layer = (byte)item.EquipLayer;
                        if (layer == (byte)Layer.None || layer == (byte)Layer.Face || layer == (byte)Layer.Pack)
                            continue;
                        if (!usedLayers.Add(layer))
                            continue;
                        corpseEquipEntries.Add((layer, item.Uid.Value));
                    }

                    var corpseEquip = new PacketCorpseEquipment(corpse.Uid.Value, corpseEquipEntries);
                    BroadcastNearby(victimPos, 18, corpseEquip, 0);

                    // 0xAF is NOT broadcast here — OnCharacterDeath below
                    // runs a per-observer dispatch that sends 0xAF to plain
                    // players and 0x1D + 0x78 ghost mobile to staff. A
                    // blanket BroadcastNearby would hit staff with 0xAF
                    // BEFORE 0x1D+0x78, causing ClassicUO to remap the
                    // serial (0x80000000|serial) so the follow-up 0x1D
                    // becomes a no-op and the alive body lingers under the
                    // remapped key alongside the new ghost mobile.
                    // Mirrors the PvP (TrySwingAt) path which also defers
                    // 0xAF to OnCharacterDeath's per-observer dispatch.
                }
                else
                {
                    // NPC corpse — keep the same packet order the PvP path
                    // uses for non-player victims so the client sees:
                    //   1) corpse world item
                    //   2) death animation
                    //   3) delete dead mobile
                    var corpsePacket = new PacketWorldItem(
                        corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                        corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                        victimDir);
                    BroadcastNearby(victimPos, 18, corpsePacket, 0);

                    var dirToKiller = victim.Position.GetDirectionTo(killer.Position);
                    uint npcFallDir = (uint)dirToKiller <= 3 ? 1u : 0u;
                    var deathAnim = new PacketDeathAnimation(victim.Uid.Value, corpse.Uid.Value, npcFallDir);
                    BroadcastNearby(victimPos, 18, deathAnim, 0);

                    var removeMobile = new PacketDeleteObject(victim.Uid.Value);
                    BroadcastNearby(victimPos, 18, removeMobile, 0);
                }
            }

            foreach (var c in _clients.Values)
            {
                if (c.Character == victim)
                {
                    c.OnCharacterDeath();
                    break;
                }
            }
        };
        _npcAI.OnHealerAction = (healer, target, isResurrect) =>
        {
            ushort healAnim = healer.IsMounted ? MapAnimToMounted(16) : (ushort)16;
            var anim = new PacketAnimation(healer.Uid.Value, healAnim, 4, 1, false, false, 0);
            BroadcastNearby(healer.Position, 18, anim, 0);
            var sound = new PacketSound(isResurrect ? (ushort)0x0214 : (ushort)0x01F2,
                healer.X, healer.Y, healer.Z);
            BroadcastNearby(healer.Position, 18, sound, 0);
        };
        _npcAI.OnWakeNpc = WakeNpc;
        _npcAI.OnNpcSound = (npc, type) =>
        {
            var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            if (charDef == null) return;
            ushort soundId = type switch
            {
                CreatureSoundType.Idle => charDef.SoundIdle,
                CreatureSoundType.Notice => charDef.SoundNotice,
                CreatureSoundType.Hit => charDef.SoundHit,
                CreatureSoundType.GetHit => charDef.SoundGetHit,
                CreatureSoundType.Die => charDef.SoundDie,
                _ => 0
            };
            if (soundId == 0) return;
            var snd = new PacketSound(soundId, npc.X, npc.Y, npc.Z);
            BroadcastNearby(npc.Position, 18, snd, 0);
        };
        _npcAI.OnNpcBreath = (npc, target, damage) =>
        {
            // Fire breath effect: moving fireball from NPC to target
            var fx = new PacketEffect(0, npc.Uid.Value, target.Uid.Value, 0x36D4,
                npc.X, npc.Y, (short)(npc.Z + 10),
                target.X, target.Y, (short)(target.Z + 10),
                7, 10, true, true);
            BroadcastNearby(npc.Position, 18, fx, 0);
            var breathSound = new PacketSound(0x0227, npc.X, npc.Y, npc.Z);
            BroadcastNearby(npc.Position, 18, breathSound, 0);

            target.Hits -= (short)Math.Min(damage, target.Hits);
            if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
            {
                target.FightTarget = npc.Uid;
                target.NextNpcActionTime = 0;
                WakeNpc(target);
            }
            var dmgPkt = new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue));
            BroadcastNearby(target.Position, 18, dmgPkt, 0);
            var healthPkt = new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits);
            BroadcastNearby(target.Position, 18, healthPkt, 0);

            if (target.Hits <= 0 && !target.IsDead)
                _npcAI.OnNpcKill?.Invoke(npc, target);
        };
        _npcAI.OnNpcThrow = (npc, target, damage) =>
        {
            ushort throwGfx = 0x0F51;
            if (npc.TryGetTag("THROWOBJ", out string? objStr) && !string.IsNullOrWhiteSpace(objStr))
            {
                var cleanObj = objStr.Trim();
                if (cleanObj.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    cleanObj = cleanObj[2..];
                if (ushort.TryParse(cleanObj, System.Globalization.NumberStyles.HexNumber, null, out ushort gfx) && gfx > 0)
                    throwGfx = gfx;
            }
            var fx = new PacketEffect(0, npc.Uid.Value, target.Uid.Value, throwGfx,
                npc.X, npc.Y, (short)(npc.Z + 10),
                target.X, target.Y, (short)(target.Z + 10),
                10, 5, true, false);
            BroadcastNearby(npc.Position, 18, fx, 0);

            target.Hits -= (short)Math.Min(damage, target.Hits);
            if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
            {
                target.FightTarget = npc.Uid;
                target.NextNpcActionTime = 0;
                WakeNpc(target);
            }
            var dmgPkt = new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue));
            BroadcastNearby(target.Position, 18, dmgPkt, 0);
            var healthPkt = new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits);
            BroadcastNearby(target.Position, 18, healthPkt, 0);

            if (target.Hits <= 0 && !target.IsDead)
                _npcAI.OnNpcKill?.Invoke(npc, target);
        };
        _npcAI.OnNpcCastSpell = (npc, target, spell) =>
        {
            _spellEngine.CastStart(npc, spell, target.Uid, target.Position);
        };
        var gatheringEngine = new GatheringEngine(_world, _triggerDispatcher);
        _skillHandlers = new SkillHandlers(_world, gatheringEngine);
        _craftingEngine = new CraftingEngine(_world);
        _weatherEngine = new WeatherEngine(_world);
        _weatherEngine.Configure(
            _config.SeasonMode,
            (SeasonType)Math.Clamp(_config.SeasonDefault, (byte)SeasonType.Spring, (byte)SeasonType.Desolation),
            checked(_config.SeasonChangeIntervalMinutes * 60 * 1000));
        VendorEngine.World = _world;
        _world.ObjectCreated += OnWorldObjectCreated;
        _world.ObjectDeleting += OnWorldObjectDeleting;
        _accounts.AccountCreated += account => _systemHooks.DispatchAccount("create", account);
        _accounts.AccountLogin += account => _systemHooks.DispatchAccount("login", account);
        _accounts.AccountDeleted += account => _systemHooks.DispatchAccount("delete", account);
        _accounts.AccountPasswordChanged += account => _systemHooks.DispatchAccount("pwchange", account);
        _accounts.AccountBlocked += account => _systemHooks.DispatchAccount("block", account);
        _accounts.AccountUnblocked += account => _systemHooks.DispatchAccount("unblock", account);

        // Wire config values to engines
        SkillEngine.SkillSumMaxOverride = _config.MaxBaseSkill > 0 ? _config.MaxBaseSkill : 7000;
        _world.MapData = _mapData;

        // Wire combat weapon damage lookup from ItemDef definitions
        CombatEngine.WeaponDefLookup = (baseId) =>
        {
            var link = _resources.GetResource(ResType.ItemDef, baseId);
            if (link == null) return null;
            using var sf = link.OpenAtStoredPosition();
            if (sf == null) return null;
            var sections = sf.ReadAllSections();
            int damMin = 0, damMax = 0;
            foreach (var sec in sections)
            {
                foreach (var key in sec.Keys)
                {
                    if (key.Key.Equals("DAM", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = key.Arg.Split(',');
                        int.TryParse(parts[0].Trim(), out damMin);
                        if (parts.Length > 1) int.TryParse(parts[1].Trim(), out damMax);
                        else damMax = damMin;
                    }
                }
            }
            return damMax > 0 ? (damMin, damMax) : null;
        };

        CombatEngine.NpcDamageDefLookup = (defIndex) =>
        {
            var charDef = DefinitionLoader.GetCharDef(defIndex);
            if (charDef == null || (charDef.AttackMin == 0 && charDef.AttackMax == 0))
                return null;
            return (charDef.AttackMin, Math.Max(charDef.AttackMin, charDef.AttackMax));
        };

        // Housing — load multi definitions from multi.mul
        var multiRegistry = new SphereNet.Game.Housing.MultiRegistry();
        if (_mapData != null)
        {
            int multiCount = multiRegistry.LoadFromMapData(_mapData);
            _log.LogInformation("Loaded {Count} multi definitions from multi.mul", multiCount);
        }
        _housingEngine = new HousingEngine(_world, multiRegistry);
        _housingEngine.DeserializeFromWorld();
        if (_housingEngine.HouseCount > 0)
            _log.LogInformation("Restored {Count} houses from world save", _housingEngine.HouseCount);

        // Ships
        _shipEngine = new SphereNet.Game.Ships.ShipEngine(_world, multiRegistry, _mapData);
        _shipEngine.OnTillerSpeak = (ship, text) =>
        {
            // Source-X CItemShip::Speak uses CObjBase::Speak with COLOR_TEXT_DEF.
            // Broadcast as overhead unicode text from the tillerman item so
            // every client near the multi sees the line, mirroring upstream.
            var origin = ship.MultiItem.Position;
            var pkt = new PacketSpeechUnicodeOut(
                ship.MultiItem.Uid.Value,
                ship.MultiItem.DispIdFull,
                0x06, // ASCII speech
                0x0481, // grey hue
                3, // small font
                "ENU",
                ship.MultiItem.Name ?? "Tillerman",
                text);
            BroadcastNearby(origin, 18, pkt, 0);
        };
        _shipEngine.DeserializeFromWorld();
        if (_shipEngine.ShipCount > 0)
            _log.LogInformation("Restored {Count} ships from world save", _shipEngine.ShipCount);

        // Wire Item static delegates for ship resolution
        SphereNet.Game.Objects.Items.Item.ResolveShip = uid => _shipEngine.GetShip(uid);
        // MULTICREATE verb -> HousingEngine runtime registration
        SphereNet.Game.Objects.Items.Item.OnHouseRegister =
            item => _housingEngine?.RegisterExistingMulti(item);

        // Char UID -> GameClient index, used by BroadcastNearby /
        // SendPacketToChar to skip the full _clients.Values scan.
        SphereNet.Game.Clients.GameClient.OnCharacterOnline =
            (ch, client) => _clientsByCharUid[ch.Uid] = client;
        SphereNet.Game.Clients.GameClient.OnCharacterOffline =
            ch => _clientsByCharUid.Remove(ch.Uid);
        SphereNet.Game.Clients.GameClient.OnWakeNpc = WakeNpc;
        SphereNet.Game.Clients.GameClient.BotSpawnLocationProvider = acctName =>
        {
            if (_botEngine == null || !SphereNet.Game.Diagnostics.BotClient.IsBotAccountName(acctName))
                return null;
            var (x, y, z) = _botEngine.GetRandomSpawnLocation(new Random(acctName.GetHashCode()));
            return new Point3D(x, y, z, 0);
        };

        // Corpse decay -> drop contents to the ground. Invoked by the
        // per-item decay timer in Item.OnTick; replaces the old per-tick
        // full-world scan in DeathEngine.ProcessDecay.
        SphereNet.Game.Objects.Items.Item.OnCorpseDecay = corpse =>
        {
            bool isPlayerCorpse = false;
            if (corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
                uint.TryParse(ownerStr, out uint ownerUid))
            {
                var owner = _world.FindChar(new Serial(ownerUid));
                if (owner != null && owner.IsPlayer)
                    isPlayerCorpse = true;
            }

            foreach (var child in corpse.Contents.ToArray())
            {
                corpse.RemoveItem(child);
                if (isPlayerCorpse)
                {
                    _world.PlaceItem(child, corpse.Position);
                    if (child.DecayTime <= 0)
                        child.DecayTime = Environment.TickCount64 + GameWorld.DefaultDecayTimeMs;
                }
                else
                {
                    child.Delete();
                }
            }
        };
        SphereNet.Game.Objects.Items.Item.OnTimerExpired = item =>
        {
            return _triggerDispatcher?.FireItemTrigger(item,
                ItemTrigger.Timer,
                new TriggerArgs { ItemSrc = item });
        };
        SphereNet.Game.Objects.Items.Item.ResolveShipEngine = () => _shipEngine;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => _world;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => _world;
        SphereNet.Game.Objects.ObjBase.OnNameChangeWarning = msg =>
            _log.LogWarning("[NAME_CHANGE] {Details}", msg);

        // Route Character-emitted diagnostic lines (vendor restock,
        // trigger verb dispatch, etc.) into the main logger so they
        // appear next to the trig_runner / npc_spawn traces we already
        // rely on while debugging service NPC behaviour.
        SphereNet.Game.Objects.Characters.Character.Diagnostic = msg =>
            _log.LogDebug("{Details}", msg);
        SphereNet.Game.Definitions.TemplateEngine.Diagnostic = msg =>
            _log.LogDebug("{Details}", msg);

        // Guild member properties
        SphereNet.Game.Objects.Characters.Character.ResolveGuildManager = _ => _guildManager;

        // Party properties & commands
        SphereNet.Game.Objects.Characters.Character.ResolvePartyFinder = uid => _partyManager.FindParty(uid);
        SphereNet.Game.Objects.Characters.Character.ResolvePartyManager = () => _partyManager;

        // Character lookup by UID — used for ACCOUNT.CHAR.N.NAME chain and
        // other admin-dialog ref resolution paths.
        SphereNet.Game.Objects.Characters.Character.ResolveCharByUid = uid =>
            _world?.FindChar(uid);

        // Packet delivery back into the character's owning client, for
        // script verbs like ADDBUFF/REMOVEBUFF/SYSMESSAGELOC/ARROWQUEST.
        // No-op when the character has no connected client.
        SphereNet.Game.Objects.Characters.Character.SendPacketToOwner = (target, packet) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == target)
                {
                    c.Send(packet);
                    return;
                }
            }
        };

        // Account resolution from character UID
        SphereNet.Game.Objects.Characters.Character.ResolveAccountForChar = uid =>
        {
            var ch = _world.FindChar(uid);
            if (ch == null) return null;
            if (ch.TryGetTag("ACCOUNT", out string? acctName) && !string.IsNullOrEmpty(acctName))
                return _accounts.FindAccount(acctName);
            foreach (var acct in _accounts.GetAllAccounts())
            {
                for (int i = 0; i < 7; i++)
                {
                    if (acct.GetCharSlot(i) == uid)
                        return acct;
                }
            }
            return null;
        };
        SphereNet.Game.Objects.Items.Item.ResolveGuild = uid => _guildManager.GetGuild(uid);


        // --- Admin dialog verb hooks (DISCONNECT/KICK/RESENDTOOLTIP/...).
        // Each delegate routes a CChar verb back through the right
        // engine. They are nullable for tests that spin up Character
        // without a network/admin context.
        SphereNet.Game.Objects.Characters.Character.DisconnectClient = (target, ban) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == target)
                {
                    if (ban)
                    {
                        // Ban semantics: pull the account via the same
                        // resolver Sphere uses for ACCOUNT.* lookups.
                        var acct = SphereNet.Game.Objects.Characters.Character
                            .ResolveAccountForChar?.Invoke(target.Uid);
                        if (acct != null)
                            acct.IsBanned = true;
                    }
                    c.NetState.MarkClosing();
                    return;
                }
            }
        };

        SphereNet.Game.Objects.Characters.Character.ResendTooltipForAll = target =>
        {
            // No dedicated AOS tooltip-revision packet in the codebase
            // yet. Mark the character's stat flags dirty so the view
            // tick re-emits the status block on the next pass; that is
            // close enough for the admin dialog use case (refresh after
            // toggling Invul/Incognito/etc).
            target.MarkDirty(SphereNet.Core.Enums.DirtyFlag.StatFlags);
        };

        SphereNet.Game.Objects.Characters.Character.OpenInfoDialog = target =>
        {
            // Show inspect dialog on the GM watching the target. The
            // target here is who we want to inspect, not the requester:
            // walk every active client and dispatch the inspect dialog
            // for the target. Caller side (admin dialog) typically
            // chains <Src.info> from the GM's own session, so the GM
            // is the active speech-source — matching ShowInspectDialog
            // expectations.
            foreach (var c in _clients.Values)
            {
                if (c.Character == target)
                {
                    c.ShowInspectDialog(target.Uid.Value);
                    return;
                }
            }
        };

        SphereNet.Game.Objects.Characters.Character.BeginTeleTarget = gm =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginTeleportTarget();
                    return;
                }
            }
        };

        SphereNet.Game.Objects.Characters.Character.SummonCageAround = gm =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.BeginSummonCageTarget();
                    return;
                }
            }
        };

        SphereNet.Game.Objects.Characters.Character.ResolveClientInfo = ch =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == ch)
                    return ((int)c.NetState.ClientVersionNumber,
                        SphereNet.Game.Objects.Characters.Character.ClientType.ClassicWindows);
            }
            return (0, SphereNet.Game.Objects.Characters.Character.ClientType.ClassicWindows);
        };

        SphereNet.Game.Objects.Characters.Character.FollowUid = (gm, uid) =>
        {
            // No persistent follow engine yet — Source-X CChar follow
            // skill ticks aren't implemented. Best-effort fallback:
            // teleport the GM next to the target so the dialog row
            // ("Follow this player") still has a useful side effect.
            var targetSerial = new SphereNet.Core.Types.Serial(uid);
            var target = _world?.FindChar(targetSerial);
            if (target == null) return;
            gm.MoveTo(target.Position);
        };

        // --- Observer-visible verbs (ANIM/SOUND/EFFECT/BOW/SALUTE/BARK)
        // and per-owner UI verbs (DCLICK/PACK/BANK) ---
        SphereNet.Game.Objects.Characters.Character.BroadcastNearby = (origin, range, pkt, exclude) =>
        {
            BroadcastNearby(origin, range, pkt, exclude);
        };
        SphereNet.Game.Objects.Characters.Character.OpenPaperdollForOwner = ch =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == ch)
                {
                    // 0x88 paperdoll — title is the rendered char name
                    // plus any title; we use the plain name here as the
                    // GM-context paperdoll doesn't need the full title
                    // line composition that Source-X computes.
                    c.NetState.Send(new SphereNet.Network.Packets.Outgoing.PacketOpenPaperdoll(
                        ch.Uid.Value, ch.GetName(), 0));
                    return;
                }
            }
        };
        SphereNet.Game.Objects.Characters.Character.OpenBackpackForOwner = ch =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == ch)
                {
                    var pack = ch.Backpack;
                    if (pack != null)
                        c.NetState.Send(new SphereNet.Network.Packets.Outgoing.PacketOpenContainer(
                            pack.Uid.Value, 0x003C, c.NetState.IsClientPost7090));
                    return;
                }
            }
        };
        SphereNet.Game.Objects.Characters.Character.OpenBankboxForOwner = ch =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == ch) { c.OpenBankBox(); return; }
            }
        };

        // Mounts
        _mountEngine = new SphereNet.Game.Mounts.MountEngine(_world);

        // Stress-test harness (.stress / .stressreport / .stressclean)
        _stressEngine = new SphereNet.Game.Diagnostics.StressTestEngine(_world, _loggerFactory);
        _commands.OnStressGenerateRequested += (items, npcs) => _stressEngine.QueueGenerate(items, npcs);
        _commands.OnStressReportRequested   += () => _stressEngine.LogReport();
        _commands.OnStressCleanupRequested  += () => _stressEngine.QueueCleanup();

        // Bot stress test engine (.bot / .botmenu)
        _botEngine = new SphereNet.Game.Diagnostics.BotEngine(_loggerFactory.CreateLogger<SphereNet.Game.Diagnostics.BotEngine>());
        _commands.OnBotCommandRequested += HandleBotCommand;
        _commands.OnBotMenuRequested += ShowBotManagerDialog;

        _commands.OnRecordDialogRequested += ShowRecordingDialog;
        _commands.OnSaveFormatChangeRequested += HandleSaveFormatChange;
        _commands.OnScriptDebugToggleRequested += on =>
        {
            exprParser.DebugUnresolved = on;
            _log.LogInformation("Script debug logging: {State}", on ? "ON" : "OFF");
        };

        // Load spell/item/char definitions from scripts
        var defSw = Stopwatch.StartNew();
        // Wire diagnostic BEFORE the loader runs — script-load tracing
        // (template body inspection, etc.) only fires during LoadAll.
        DefinitionLoader.Diagnostic = msg => _log.LogDebug("{Details}", msg);
        var defLoader = new DefinitionLoader(_resources, _spellRegistry);
        defLoader.LoadAll();
        defSw.Stop();
        _log.LogInformation("Definitions loaded in {Ms}ms", defSw.ElapsedMilliseconds);

        // Load AREADEF definitions as regions from scripts
        LoadRegionDefs();

        // Load ROOMDEF definitions from scripts
        LoadRoomDefs();

        // --- 8. Network ---
        _log.LogInformation("Starting network...");
        int maxClients = _config.ClientMax > 0 ? _config.ClientMax : 256;
        _network = new NetworkManager(maxClients, _loggerFactory);
        _network.CryptConfig = _cryptConfig;
        _network.UseCrypt = _config.UseCrypt;
        _network.UseNoCrypt = _config.UseNoCrypt;
        _network.DebugPackets = _config.DebugPackets;
        _network.DebugPacketOpcodeFilter = ParseDebugPacketOpcodes(_config.DebugPacketOpcodes);
        _network.MaxPacketsPerTick = _config.MaxPacketsPerTick;
        _network.PacketScriptHook = HandlePacketScriptHook;
        _log.LogInformation("Crypto keys loaded: {Count}, UseCrypt={UC}, UseNoCrypt={UNC}",
            _cryptConfig.Keys.Count, _config.UseCrypt, _config.UseNoCrypt);
        _network.SetHandlers(
            loginRequest: OnLoginRequest,
            gameLogin: OnGameLogin,
            charSelect: OnCharSelect,
            moveRequest: OnMoveRequest,
            speech: OnSpeech,
            attackRequest: OnAttackRequest,
            warMode: OnWarMode,
            doubleClick: OnDoubleClick,
            singleClick: OnSingleClick,
            itemPickup: OnItemPickup,
            itemDrop: OnItemDrop,
            itemEquip: OnItemEquip,
            statusRequest: OnStatusRequest,
            targetResponse: OnTargetResponse,
            gumpResponse: OnGumpResponse,
            clientVersion: OnClientVersion,
            aosTooltip: OnAOSTooltip,
            textCommand: OnTextCommand,
            extendedCommand: OnExtendedCommand,
            resyncRequest: OnResyncRequest,
            logoutRequest: OnLogoutRequest,
            helpRequest: OnHelpRequest,
            serverSelect: OnServerSelect,
            charCreate: OnCharCreate,
            viewRange: OnViewRange,
            vendorBuy: OnVendorBuy,
            vendorSell: OnVendorSell,
            secureTrade: OnSecureTrade,
            rename: OnRename,
            profileRequest: OnProfileRequest,
            // Phase 1
            deathMenu: OnDeathMenu,
            charDelete: OnCharDelete,
            dyeResponse: OnDyeResponse,
            promptResponse: OnPromptResponse,
            menuChoice: OnMenuChoice,
            // Phase 2
            bookPage: OnBookPage,
            bookHeader: OnBookHeader,
            bulletinBoardRequestList: OnBulletinBoardRequestList,
            bulletinBoardRequestMessage: OnBulletinBoardRequestMessage,
            bulletinBoardPost: OnBulletinBoardPost,
            bulletinBoardDelete: OnBulletinBoardDelete,
            mapDetail: OnMapDetail,
            mapPinEdit: OnMapPinEdit,
            // Phase 3
            gumpTextEntry: OnGumpTextEntry,
            allNamesRequest: OnAllNamesRequest
        );

        _network.OnConnectionClosed += OnConnectionClosed;
        _network.OnUnknownPacket += OnUnknownPacket;
        _network.OnPacketQuotaExceeded += OnPacketQuotaExceeded;

        if (!_network.Start("0.0.0.0", _config.ServPort))
        {
            _log.LogError("Failed to start network listener. Exiting.");
            return;
        }

        _serverStartTime = DateTime.UtcNow;
#if WINFORMS
        _consoleForm?.SetServerStartTime(_serverStartTime);
#endif
        _log.LogInformation("SphereNet ready. Listening on port {Port}.", _config.ServPort);
        _systemHooks.DispatchServer("start", _serverHookContext, _config.ServName);

        // --- 9. Admin Console ---
        int telnetPort = _config.ServPort + 1;
        _telnet = new TelnetConsole(_world, _accounts, _config,
            () => _network.ActiveConnections,
            _loggerFactory.CreateLogger("Telnet"), _loggerFactory);
        _telnet.Start(telnetPort);
        _telnet.OnSaveRequested += PerformSave;
        _telnet.OnShutdownRequested += () => _running = false;
        _telnet.OnResyncRequested += PerformScriptResync;
        _telnet.OnAccountPrivLevelChanged += SyncOnlineAccountPrivLevel;
        _telnet.OnDebugToggleRequested += ToggleDebugPackets;
        _telnet.OnScriptDebugToggleRequested += ToggleScriptDebug;

        // Console command processor (shares logic with telnet)
        _consoleProcessor = new AdminCommandProcessor(_world, _accounts, _config,
            () => _network.ActiveConnections, _loggerFactory);
        _consoleProcessor.OnSaveRequested += PerformSave;
        _consoleProcessor.OnShutdownRequested += () => _running = false;
        _consoleProcessor.OnResyncRequested += PerformScriptResync;
        _consoleProcessor.OnAccountPrivLevelChanged += SyncOnlineAccountPrivLevel;
        _consoleProcessor.OnDebugToggleRequested += ToggleDebugPackets;
        _consoleProcessor.OnScriptDebugToggleRequested += ToggleScriptDebug;

        int webPort = _config.ServPort + 2;
        _webStatus = new WebStatusServer(_world, _accounts,
            () => _network.ActiveConnections,
            _loggerFactory.CreateLogger("WebStatus"));
        _webStatus.Start(webPort);
        _log.LogInformation("Type 'help' for commands. Enter commands directly (e.g. save, status, quit).");

        // Schedule all existing NPCs into the timer wheel. Spread
        // the initial fire proportional to NPC count: ~50 NPCs per tick
        // (per 100ms) is comfortable, so stagger = count * 100ms / 50,
        // clamped to 1-30 seconds.
        if (_npcTimerWheel != null)
        {
            long now = Environment.TickCount64;
            var jitter = new Random(0);
            int scheduled = 0;
            int npcCount = 0;
            foreach (var obj in _world.GetAllObjects())
                if (obj is Character c && !c.IsPlayer && !c.IsDead && !c.IsDeleted)
                    npcCount++;
            int staggerMs = Math.Clamp(npcCount * 2, 1000, 30_000);
            foreach (var obj in _world.GetAllObjects())
            {
                if (obj is Character npc && !npc.IsPlayer && !npc.IsDead && !npc.IsDeleted)
                {
                    long when = npc.NextNpcActionTime > 0
                        ? npc.NextNpcActionTime
                        : now + jitter.Next(0, staggerMs);
                    _npcTimerWheel.Schedule(npc, when);
                    scheduled++;
                }
            }
            _log.LogInformation("NPC timer wheel initialized: {Count} NPCs scheduled ({StaggerMs}ms stagger)", scheduled, staggerMs);
        }

        // --- 9. Main Game Loop ---
        _running = true;
        var sw = Stopwatch.StartNew();
        long lastTickMs = 0;
        const int TickIntervalMs = 100; // 10 ticks per second (optimized for performance)

        while (_running)
        {
            long now = sw.ElapsedMilliseconds;

            // Console input (from WinForms command queue or headless stdin queue)
#if WINFORMS
            if (_consoleForm != null)
            {
                while (_consoleForm.CommandQueue.TryDequeue(out string? consoleCmd))
                    HandleConsoleCommand(consoleCmd);
            }
            else
#endif
            {
                while (_headlessCommandQueue.TryDequeue(out string? consoleCmd))
                    HandleConsoleCommand(consoleCmd);
            }

            // Network I/O runs every iteration for low latency
            _network.CheckNewConnections();
            _network.ProcessAllInput();

            // ServUO-style delta queue: when any object was mutated (dirty flag
            // set), push view updates to clients immediately instead of waiting
            // for the 250ms tick. Gated by HasDirty so idle iterations stay
            // cheap. Flip the flag to false to disable during diagnostics.
            const bool FastPathViewDeltaEnabled = true;
            if (FastPathViewDeltaEnabled && _world.HasDirty)
            {
                foreach (var client in _clients.Values)
                {
                    if (client.IsPlaying)
                        client.UpdateClientView();
                }
                _world.ConsumeDirtyObjects();
            }

            // Stress-test batch generation / cleanup — both are cooperative:
            // no-op when queues are empty. Runs every main-loop iteration so
            // long jobs finish quickly without starving the tick.
            if (_stressEngine != null)
            {
                if (_stressEngine.IsGenerating) _stressEngine.OnTick();
                if (_stressEngine.IsCleaning)   _stressEngine.TickCleanup();
            }

            // Bot restock — every 3 minutes, refresh bot inventories
            if (now - _lastBotRestockMs > 180_000)
            {
                _lastBotRestockMs = now;
                RestockBotCharacters();
            }

            _network.ProcessAllOutput();
            _network.Tick();

            if (now - lastTickMs >= TickIntervalMs)
            {
                lastTickMs = now;
                RunServerTick();
            }

            // Yield CPU between iterations. Mode set by sphere.ini TickSleepMode:
            //   0=spin (lowest latency, high CPU), 1=sleep (low CPU, ~15ms latency),
            //   2=hybrid (balanced, default)
            switch (_config.TickSleepMode)
            {
                case 0: // spin — busy-wait, <1ms latency, ~100% CPU core
                    Thread.SpinWait(100);
                    break;
                case 1: // sleep — OS scheduler yield, minimal CPU, ~15ms latency on Windows
                    Thread.Sleep(1);
                    break;
                default: // hybrid — SpinWait + Sleep(0), ~1ms latency, moderate CPU
                    Thread.SpinWait(100);
                    Thread.Sleep(0);
                    break;
            }
        }

        // --- 10. Shutdown ---
        _log.LogInformation("Shutting down...");
        _systemHooks.DispatchServer("exit", _serverHookContext);

        _log.LogInformation("Auto-save on shutdown is disabled. Use 'save' command before quitting to persist world state.");

        _telnet?.Dispose();
        _webStatus?.Dispose();
        _network.Dispose();
        _mapData?.Dispose();
        _scriptDb.Close();
        _scriptLdb.Close();
        _scriptFile?.Dispose();

        _log.LogInformation("SphereNet stopped.");
        Log.CloseAndFlush();

#if WINFORMS
        // Close the WinForms window if still open
        if (_consoleForm != null && !_consoleForm.IsDisposed)
        {
            _consoleForm.BeginInvoke(() => _consoleForm.Close());
        }
#endif
    }

    // --- Console Commands ---

    /// <summary>
    /// Write a line to the console form (GUI) or stdout (headless).
    /// </summary>
    private static void ConsoleAppend(string text, Color color)
    {
#if WINFORMS
        if (_consoleForm != null)
        {
            _consoleForm.AppendLine(text, color);
            return;
        }
#endif
        // Headless mode uses Serilog console sink — no extra stdout needed
        // unless logging isn't initialized yet.
        if (_log == null)
            Console.WriteLine(text);
    }

    private static void HandleConsoleCommand(string input)
    {
        if (_consoleProcessor == null) return;
        _consoleProcessor.ProcessCommand(input, line =>
            ConsoleAppend(line, Color.LightGray));
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
    private static string? ResolveServerProperty(string property)
    {
        string upper = property.ToUpperInvariant();
        return upper switch
        {
            // --- Read-only server stats ---
            "CLIENTS" => _network?.ActiveConnections.ToString() ?? "0",
            "ACCOUNTS" => _accounts?.Count.ToString() ?? "0",
            "CHARS" => _world?.TotalChars.ToString() ?? "0",
            "ITEMS" => _world?.TotalItems.ToString() ?? "0",
            "VERSION" => "SphereNet 1.0",
            "SERVNAME" or "NAME" => _config?.ServName ?? "SphereNet",

            // --- Time properties ---
            "TIME" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "TIMEUP" => ((int)(DateTime.UtcNow - _serverStartTime).TotalSeconds).ToString(),
            "RTIME" => DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy"),
            "RTICKS" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "TICKPERIOD" => "100",

            // --- Save ---
            "SAVECOUNT" => _saveCount.ToString(),

            // --- Memory ---
            "MEM" => (GC.GetTotalMemory(false) / 1024).ToString(),

            // --- Regeneration rates (tenths of a second in Sphere) ---
            "REGEN0" => (_config?.RegenHits ?? 40).ToString(),
            "REGEN1" => (_config?.RegenStam ?? 20).ToString(),
            "REGEN2" => (_config?.RegenMana ?? 30).ToString(),
            "REGEN3" => (_config?.RegenFood ?? 86400).ToString(),

            // --- Misc ---
            "HEARALL" => "0",
            "GMPAGES" => "0",
            "GUILDS" => "0",
            "SEASON" => ((int)(_weatherEngine?.CurrentSeason ?? SeasonType.Spring)).ToString(),
            "SEASONMODE" => (_weatherEngine?.CurrentSeasonMode ?? SeasonMode.Auto).ToString(),
            "FEATURET2A" => (_config?.FeatureT2A ?? 0).ToString(),
            // Chat system flags are script-visible even when chat is disabled.
            // Return 0 until a fuller chat subsystem is wired.
            "CHATFLAGS" => "0",

            // --- Reference lookups via SERV.xxx ---
            "LASTNEWITEM" => _world?.LastNewItem.Value.ToString() ?? "0",
            "LASTNEWCHAR" => _world?.LastNewChar.Value.ToString() ?? "0",

            // --- SERV.MAP* ---
            _ when upper.StartsWith("MAPLIST.") => ResolveMapListProperty(upper[8..]),

            // --- SERV.SKILL.n.KEY / SERV.SKILL.n.NAME — skill table lookup.
            // Admin dialogs iterate all 58 skills using <Serv.Skill.<idx>.Key>
            // to discover defnames at runtime.
            _ when upper.StartsWith("SKILL.") => ResolveServSkill(upper[6..]),

            // --- SERV.CHARDEF.<defname>.<prop> / SERV.ITEMDEF.<defname>.<prop>
            // Used by script dialogs like d_spawn to render names/jobs from
            // definition data without instantiating the object.
            _ when upper.StartsWith("CHARDEF.") => ResolveServCharDef(property[8..]),
            _ when upper.StartsWith("ITEMDEF.") => ResolveServItemDef(property[8..]),

            // --- ISEVENT.name — 1 if the named event script is loaded.
            // Used by admin dialogs to grey out delete buttons for missing events.
            _ when upper.StartsWith("ISEVENT.") => ResolveIsEvent(upper[8..]),

            // --- SERV.ACCOUNT.name or SERV.ACCOUNT.n ---
            _ when upper.StartsWith("ACCOUNT.") => ResolveServAccount(upper[8..]),

            // --- SERV.MAP.n.SECTOR.n.property ---
            _ when upper.StartsWith("MAP.") => ResolveServMapSector(upper[4..]),

            // --- SERV.MAP(x,y,z,m).property — Source-X function form
            // Used by d_admin houses/ships row to look up the
            // region name at a given world point:
            //   <Serv.Map(<REF1.P.X>,<REF1.P.Y>,0,<REF1.P.M>).Region.Name>
            _ when upper.StartsWith("MAP(") => ResolveServMapPoint(property[4..]),

            // --- RTIME.FORMAT / RTICKS.FORMAT / RTICKS.FROMTIME (standalone, forwarded here) ---
            _ when upper.StartsWith("RTIME.FORMAT") => ResolveRtimeFormat(upper),
            _ when upper.StartsWith("RTICKS.FORMAT") => ResolveRticksFormat(upper),
            _ when upper.StartsWith("RTICKS.FROMTIME") => ResolveRticksFromTime(upper),

            // --- SERV.LOOKUPSKILL <name> — reverse lookup skill id by name.
            // Returns -1 on miss to match Source-X behaviour.
            _ when upper.StartsWith("LOOKUPSKILL ") => ResolveLookupSkill(property[12..]),
            _ when upper.StartsWith("LOOKUPSKILL(") => ResolveLookupSkill(
                property.EndsWith(")") ? property[12..^1] : property[12..]),

            // --- Global variables: VAR.name / VAR0.name ---
            _ when upper.StartsWith("VAR0.") => _world?.GetGlobalVar0(property[5..]) ?? "0",
            _ when upper.StartsWith("VAR.") => _world?.GetGlobalVar(property[4..]) ?? "",

            // --- OBJ / OBJ.property — global object reference ---
            "OBJ" => _world?.ObjReference.Value != 0 ? $"0{_world!.ObjReference.Value:X}" : "0",
            _ when upper.StartsWith("OBJ.") => ResolveObjProperty(property[4..]),

            // --- NEW / NEW.property — last created object ---
            "NEW" => _world?.LastNewItem.Value != 0 ? $"0{_world!.LastNewItem.Value:X}" :
                      _world?.LastNewChar.Value != 0 ? $"0{_world!.LastNewChar.Value:X}" : "0",
            _ when upper.StartsWith("NEW.") => ResolveNewProperty(property[4..]),

            // --- UID.0xHEX.property — direct object access ---
            _ when upper.StartsWith("UID.") => ResolveUidProperty(property[4..]),

            // --- DEFMSG.name — default message lookup ---
            _ when upper.StartsWith("DEFMSG.") => ResolveDefMsg(property[7..]),

            // --- Commands (write operations, prefixed with _SET_/_CLEARVARS/_NEWDUPE) ---
            _ when upper.StartsWith("_SET_VAR.") => HandleSetGlobalVar(property[9..]),
            _ when upper.StartsWith("_SET_OBJ=") => HandleSetObj(property[9..]),
            _ when upper.StartsWith("_SET_OBJ.") => HandleSetObjProperty(property[9..]),
            _ when upper.StartsWith("_CLEARVARS=") => HandleClearVars(property[11..]),
            _ when upper.StartsWith("_NEWDUPE=") => HandleNewDupe(property[9..]),
            _ when upper.StartsWith("_SET_DEFMSG=") => HandleSetDefMsg(property[12..]),
            _ when upper.StartsWith("_SET_SEASON=") => HandleSetSeason(property[12..]),

            // REF object property access
            _ when upper.StartsWith("_REF_GET=") => HandleRefGet(property[9..]),
            _ when upper.StartsWith("_REF_EXEC=") => HandleRefExec(property[10..]),

            // serv.allclients <function> — invoke <function> once per
            // online player, with the caller as src. Protocol format:
            // _ALLCLIENTS=<srcUid>|<funcName>.
            _ when upper.StartsWith("_ALLCLIENTS=") => HandleAllClients(property[12..]),

            // serv.resync / serv.save / serv.shutdown — admin write
            // verbs reachable from dialog buttons (d_admin_function).
            // Sphere fires them as bare property reads on the server
            // resolver; we hijack and run the matching engine action.
            "RESYNC" => HandleServResync(),
            "SAVE" => HandleServSave(),
            "SHUTDOWN" => HandleServShutdown(""),
            _ when upper.StartsWith("SHUTDOWN ") => HandleServShutdown(property[9..]),

            // Region property access
            _ when upper.StartsWith("_REGION_GET=") => HandleRegionGet(property[12..]),

            // Room property access
            _ when upper.StartsWith("_ROOM_GET=") => HandleRoomGet(property[10..]),

            // Bare defname constants (e.g. statf_insubstantial) used by
            // script expressions without DEF./DEF0. prefix.
            _ => ResolveDefConstant(upper)
        };
    }

    /// <summary>Resolve <c>ISEVENT.name</c>. Returns "1" when a script event
    /// with this defname exists in the loaded resource set, else "0".</summary>
    private static string? ResolveIsEvent(string eventName)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(eventName)) return "0";
        var rid = _resources.ResolveDefName(eventName);
        if (!rid.IsValid)
            rid = _resources.ResolveDefName("e_" + eventName);
        return rid.IsValid ? "1" : "0";
    }

    /// <summary>Resolve <c>SERV.SKILL.n[.KEY|NAME]</c>. Returns the enum name
    /// (e.g. "Alchemy") which matches Source-X defname for that skill slot.</summary>
    private static string? ResolveServSkill(string sub)
    {
        int dot = sub.IndexOf('.');
        string idxStr = dot < 0 ? sub : sub[..dot];
        string field = dot < 0 ? "" : sub[(dot + 1)..].ToUpperInvariant();
        if (!int.TryParse(idxStr, out int idx)) return null;
        if (!Enum.IsDefined(typeof(SphereNet.Core.Enums.SkillType), (short)idx))
            return "";
        string skillName = ((SphereNet.Core.Enums.SkillType)idx).ToString();
        if (string.IsNullOrEmpty(field) || field == "KEY" || field == "NAME" || field == "DEFNAME")
            return skillName;
        return "";
    }

    private static string HandleSetSeason(string raw)
    {
        if (_weatherEngine == null)
            return "0";

        string trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "0";

        SeasonType season;
        if (byte.TryParse(trimmed, out byte seasonNum) &&
            Enum.IsDefined(typeof(SeasonType), (int)seasonNum))
        {
            season = (SeasonType)seasonNum;
        }
        else if (!Enum.TryParse(trimmed, ignoreCase: true, out season))
        {
            return "0";
        }

        bool changed = _weatherEngine.SetSeason(season, resetCycleTimer: true);
        if (changed)
            BroadcastSeasonChange(playSound: true);
        return ((int)_weatherEngine.CurrentSeason).ToString();
    }

    private static string? ResolveServCharDef(string sub)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(sub))
            return "";

        int dot = sub.IndexOf('.');
        if (dot <= 0)
            return "";

        string defName = sub[..dot].Trim();
        string field = sub[(dot + 1)..].Trim().ToUpperInvariant();
        var rid = _resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != SphereNet.Core.Enums.ResType.CharDef)
            return "";

        var def = SphereNet.Game.Definitions.DefinitionLoader.GetCharDef(rid.Index);
        if (def == null)
            return "";

        return field switch
        {
            "NAME" => def.Name ?? "",
            "JOB" => def.Job ?? "",
            "DEFNAME" => def.DefName ?? "",
            "ID" or "DISPID" => $"0{def.DispIndex:X}",
            "ICON" => def.Icon ?? "",
            "NPC" or "NPCBRAIN" => def.NpcBrain.ToString(),
            _ => ""
        };
    }

    private static string? ResolveServItemDef(string sub)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(sub))
            return "";

        int dot = sub.IndexOf('.');
        if (dot <= 0)
            return "";

        string defName = sub[..dot].Trim();
        string field = sub[(dot + 1)..].Trim().ToUpperInvariant();
        var rid = _resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != SphereNet.Core.Enums.ResType.ItemDef)
            return "";

        var def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(rid.Index);
        if (def == null)
            return "";

        return field switch
        {
            "NAME" => def.Name ?? "",
            "DEFNAME" => def.DefName ?? "",
            "ID" or "DISPID" => $"0{def.DispIndex:X}",
            "TYPE" => def.Type.ToString(),
            _ => ""
        };
    }

    private static string? ResolveObjProperty(string subProp)
    {
        if (_world == null) return "0";
        var obj = _world.FindObject(_world.ObjReference);
        if (obj == null) return "0";
        return obj.TryGetProperty(subProp, out string val) ? val : "0";
    }

    private static string? ResolveNewProperty(string subProp)
    {
        if (_world == null) return "0";
        // Try last new item first, then last new char
        var obj = _world.FindObject(_world.LastNewItem) ?? _world.FindObject(_world.LastNewChar);
        if (obj == null) return "0";
        return obj.TryGetProperty(subProp, out string val) ? val : "0";
    }

    private static string? ResolveUidProperty(string uidAndProp)
    {
        if (_world == null) return null;
        // Format: 0xHEXVALUE.property or HEXVALUE.property
        int dot = uidAndProp.IndexOf('.');
        if (dot <= 0) return null;
        string uidStr = uidAndProp[..dot].Trim();
        string prop = uidAndProp[(dot + 1)..].Trim();
        // Strip leading 0 or 0x
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return null;
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "0";
        return obj.TryGetProperty(prop, out string val) ? val : "0";
    }

    private static string? ResolveDefMsg(string msgName)
    {
        if (_resources != null && _resources.TryGetDefMessage(msgName, out string defMsg))
            return defMsg;
        return "";
    }

    private static string? ResolveDefConstant(string upperToken)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(upperToken))
            return null;
        // Only allow plain identifier-like tokens here so arithmetic and
        // command payloads don't accidentally route into defname lookups.
        for (int i = 0; i < upperToken.Length; i++)
        {
            char ch = upperToken[i];
            bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch is '_' or '.';
            if (!ok) return null;
        }
        var rid = _resources.ResolveDefName(upperToken);
        if (rid.IsValid) return rid.Index.ToString();

        // Built-in STATF_* fallback when scripts reference status-flag
        // constants directly and the defname pack doesn't provide them.
        // Example: (<Flags> & statf_insubstantial)
        if (upperToken.StartsWith("STATF_", StringComparison.Ordinal))
        {
            string suffix = upperToken[6..];
            foreach (SphereNet.Core.Enums.StatFlag flag in Enum.GetValues(typeof(SphereNet.Core.Enums.StatFlag)))
            {
                string enumName = flag.ToString().ToUpperInvariant();
                if (enumName == suffix || enumName.Replace("_", "", StringComparison.Ordinal) == suffix)
                    return ((uint)flag).ToString();
            }
        }
        return null;
    }

    private static string? HandleSetGlobalVar(string assignment)
    {
        // Format: name=value
        int eq = assignment.IndexOf('=');
        if (eq <= 0) return "";
        string name = assignment[..eq].Trim();
        string value = assignment[(eq + 1)..].Trim();
        _world?.SetGlobalVar(name, value);
        return "";
    }

    private static string? HandleSetObj(string uidStr)
    {
        if (_world == null) return "";
        string v = uidStr.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        else if (v.StartsWith("0", StringComparison.Ordinal) && v.Length > 1)
            v = v[1..];
        if (uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            _world.ObjReference = new SphereNet.Core.Types.Serial(uid);
        else
            _world.ObjReference = SphereNet.Core.Types.Serial.Invalid;
        return "";
    }

    private static string? HandleSetObjProperty(string propAssignment)
    {
        if (_world == null) return "";
        // Format: property=value
        int eq = propAssignment.IndexOf('=');
        if (eq <= 0) return "";
        string prop = propAssignment[..eq].Trim();
        string val = propAssignment[(eq + 1)..].Trim();
        var obj = _world.FindObject(_world.ObjReference);
        obj?.TrySetProperty(prop, val);
        return "";
    }

    private static string? HandleClearVars(string prefix)
    {
        _world?.ClearGlobalVars(string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim());
        return "";
    }

    private static string? HandleNewDupe(string uidStr)
    {
        if (_world == null) return "";
        string v = uidStr.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        else if (v.StartsWith("0", StringComparison.Ordinal) && v.Length > 1)
            v = v[1..];
        if (!uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return "";
        var original = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (original is SphereNet.Game.Objects.Items.Item origItem)
        {
            var clone = _world.CreateItem();
            clone.BaseId = origItem.BaseId;
            clone.Name = origItem.Name;
            clone.Hue = origItem.Hue;
            clone.Amount = origItem.Amount;
            // Copy TAGs
            foreach (var kvp in origItem.Tags.GetAll())
                clone.Tags.Set(kvp.Key, kvp.Value);
        }
        else if (original is SphereNet.Game.Objects.Characters.Character origChar)
        {
            var clone = _world.CreateCharacter();
            clone.BaseId = origChar.BaseId;
            clone.Name = origChar.Name;
            clone.Hue = origChar.Hue;
            clone.Position = origChar.Position;
            foreach (var kvp in origChar.Tags.GetAll())
                clone.Tags.Set(kvp.Key, kvp.Value);
        }
        return "";
    }

    private static string? HandleSetDefMsg(string assignment)
    {
        // DEFMSG name=value — we just log it, no persistent message override in our impl yet
        return "";
    }

    /// <summary>Get property from object referenced by UID. Format: "uidHex|property"</summary>
    private static string? HandleRefGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return "0";
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "0";
        return obj.TryGetProperty(prop, out string val) ? val : "0";
    }

    /// <summary>Execute command on object referenced by UID. Format: "uidHex|command|args"</summary>
    private static string? HandleRefExec(string data)
    {
        var parts = data.Split('|', 3);
        if (parts.Length < 2 || _world == null) return "";
        string uidStr = parts[0].Trim();
        string cmd = parts[1].Trim();
        string cmdArgs = parts.Length > 2 ? parts[2].Trim() : "";
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return "";
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "";
        // Try set property first, then execute command with a minimal console
        if (cmdArgs.Length > 0 && obj.TrySetProperty(cmd, cmdArgs))
            return "";
        if (obj.TryExecuteCommand(cmd, cmdArgs, new RefExecConsole()))
            return "";

        // Client-scoped verbs (DIALOG, SDIALOG, MENU, INPDLG, GUMPDISPLAY, ...)
        // are not implemented on the object itself; they live on the
        // owning player's GameClient. Imported sphere admin scripts use
        //   UID.<player>.Dialog d_X
        // to push a dialog to a specific player. Route the verb to that
        // player's client so the gump actually goes out the wire.
        if (obj is Character ch)
        {
            var gc = FindGameClient(ch);
            if (gc != null)
                gc.TryExecuteScriptCommand(obj, cmd, cmdArgs, null);
        }
        return "";
    }

    /// <summary>Iterate all online players and invoke a script function
    /// on each. Format: "srcUid|funcName". Caller stays as src; each
    /// iterated player becomes the function's target. Sphere admin
    /// scripts use this pattern to tally online clients or push a
    /// system message to everyone.</summary>
    private static string? HandleAllClients(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "";
        string uidStr = data[..pipe].Trim();
        string payload = data[(pipe + 1)..].Trim();
        if (string.IsNullOrEmpty(payload)) return "";

        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint srcUid))
            return "";

        var srcObj = _world.FindObject(new SphereNet.Core.Types.Serial(srcUid));
        if (srcObj is not SphereNet.Game.Objects.Characters.Character srcChar)
            return "";

        // Source-X allows two payload shapes:
        //   serv.allclients f_count_players          → call function f_count_players
        //   serv.allclients sysmessage @0481 Booting → execute verb on every client
        // Detect verb form by splitting first token and asking the
        // target object whether it can execute it. Works for SYSMESSAGE,
        // MESSAGE, EVENTS, SHRINK, KICK, ... — the standard CChar verbs.
        int sp = payload.IndexOfAny(new[] { ' ', '\t' });
        string head = sp > 0 ? payload[..sp] : payload;
        string tail = sp > 0 ? payload[(sp + 1)..].TrimStart() : "";

        var snapshot = _clients.Values.Where(c => c.IsPlaying && c.Character != null).ToList();
        foreach (var client in snapshot)
        {
            var target = client.Character!;
            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(srcChar)
            {
                Object1 = target,
                Object2 = srcChar,
            };

            bool dispatched = false;
            // Try as verb first — TryExecuteCommand returns false for
            // unknown verbs, falling through to function-call form.
            if (target.TryExecuteCommand(head, tail, client))
                dispatched = true;
            else if (client.TryExecuteScriptCommand(target, head, tail, trigArgs))
                dispatched = true;

            if (!dispatched && _triggerRunner != null)
                _triggerRunner.TryRunFunction(payload, target, client, trigArgs, out _);
        }
        return "";
    }

    /// <summary>Source-X <c>serv.resync</c> from a script: trigger the
    /// same hot-reload pipeline used by the telnet/console RESYNC verb.</summary>
    private static string? HandleServResync()
    {
        try { PerformScriptResync(); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Script-driven serv.resync failed");
        }
        return "";
    }

    /// <summary>Source-X <c>serv.save</c>: route to the standard world
    /// save path (<see cref="PerformSave"/>) so dialog-driven backups
    /// produce identical .scp output.</summary>
    private static string? HandleServSave()
    {
        try { PerformSave(); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Script-driven serv.save failed");
        }
        return "";
    }

    /// <summary>Source-X <c>serv.shutdown [seconds]</c>. Without an arg,
    /// shut down immediately; with a delay, schedule a single-shot
    /// timer. The scheduling is best-effort — the server tick loop is
    /// what actually exits when <c>_running</c> flips to false.</summary>
    private static string? HandleServShutdown(string args)
    {
        int delaySec = 0;
        if (!string.IsNullOrWhiteSpace(args))
            int.TryParse(args.Trim(), out delaySec);

        if (delaySec <= 0)
        {
            _running = false;
            return "";
        }

        // Detached delay; we don't await it because the resolver runs on
        // the script tick path and must return synchronously.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delaySec * 1000);
                _running = false;
            }
            catch { /* ignore */ }
        });
        return "";
    }

    /// <summary>Get property from region referenced by UID. Format: "uid|property"</summary>
    private static string? HandleRegionGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (!uint.TryParse(uidStr, out uint regionUid))
            return "0";
        var region = _world.FindRegionByUid(regionUid);
        if (region == null) return "0";
        return region.TryGetProperty(prop, out string val) ? val : "0";
    }

    /// <summary>Get property from room referenced by UID. Format: "uid|property"</summary>
    private static string? HandleRoomGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (!uint.TryParse(uidStr, out uint roomUid))
            return "0";
        var room = _world.FindRoomByUid(roomUid);
        if (room == null) return "0";
        return room.TryGetProperty(prop, out string val) ? val : "0";
    }

    private static string? ResolveRtimeFormat(string property)
    {
        // RTIME.FORMAT <format> — format current time
        // Property arrives as "RTIME.FORMAT <format>" or just "RTIME.FORMAT"
        int spaceIdx = property.IndexOf(' ');
        if (spaceIdx < 0)
            return DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy");
        string fmt = property[(spaceIdx + 1)..].Trim();
        try { return DateTime.Now.ToString(fmt); }
        catch { return DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy"); }
    }

    private static string? ResolveRticksFormat(string property)
    {
        // RTICKS.FORMAT <timestamp>,<format>
        var parts = property.Split(' ', 2);
        if (parts.Length < 2) return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var args = parts[1].Split(',', 2);
        if (args.Length < 2 || !long.TryParse(args[0].Trim(), out long ts))
            return "0";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
            return dt.ToString(args[1].Trim());
        }
        catch { return "0"; }
    }

    private static string? ResolveRticksFromTime(string property)
    {
        // RTICKS.FROMTIME <year>,<month>,<day>,<hour>,<min>,<sec>
        var parts = property.Split(' ', 2);
        if (parts.Length < 2) return "0";
        var args = parts[1].Split(',');
        if (args.Length < 3) return "0";
        try
        {
            int year = int.Parse(args[0].Trim());
            int month = int.Parse(args[1].Trim());
            int day = int.Parse(args[2].Trim());
            int hour = args.Length > 3 ? int.Parse(args[3].Trim()) : 0;
            int min = args.Length > 4 ? int.Parse(args[4].Trim()) : 0;
            int sec = args.Length > 5 ? int.Parse(args[5].Trim()) : 0;
            var dt = new DateTimeOffset(year, month, day, hour, min, sec, TimeSpan.Zero);
            return dt.ToUnixTimeSeconds().ToString();
        }
        catch { return "0"; }
    }

    /// <summary>Resolve <c>SERV.MAP(x,y,z,m).Region.Name</c> and similar
    /// Source-X function-form lookups. Splits the args inside parens on
    /// commas, looks up the region containing the point, then forwards
    /// the trailing <c>.Region.X</c> sub-property chain to the region
    /// object via <see cref="HandleRegionGet"/>.</summary>
    private static string? ResolveServMapPoint(string rest)
    {
        // rest is e.g. "1455,1612,0,0).Region.Name" — strip up to ')'.
        int close = rest.IndexOf(')');
        if (close < 0) return "0";
        string argList = rest[..close];
        string trailing = rest[(close + 1)..];
        if (trailing.StartsWith('.')) trailing = trailing[1..];

        var parts = argList.Split(',');
        if (parts.Length < 2) return "0";

        if (!int.TryParse(parts[0].Trim(), out int x)) return "0";
        if (!int.TryParse(parts[1].Trim(), out int y)) return "0";
        int z = parts.Length > 2 && int.TryParse(parts[2].Trim(), out int parsedZ) ? parsedZ : 0;
        int m = parts.Length > 3 && int.TryParse(parts[3].Trim(), out int parsedM) ? parsedM : 0;

        var pt = new SphereNet.Core.Types.Point3D((short)x, (short)y, (sbyte)z, (byte)m);
        if (_world == null) return "0";

        // Bare "MAP(x,y,z,m)" or trailing "P": just echo the point.
        if (string.IsNullOrEmpty(trailing) || trailing.Equals("P", StringComparison.OrdinalIgnoreCase))
            return pt.ToString();

        // REGION[.prop] — delegate to the existing HandleRegionGet path.
        if (trailing.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
        {
            var region = _world.FindRegion(pt);
            if (region == null) return "";
            string regionRest = trailing.Length > 6 && trailing[6] == '.' ? trailing[7..] : "";
            if (string.IsNullOrEmpty(regionRest)) return region.Name;
            return region.TryGetProperty(regionRest, out string rv) ? rv : "0";
        }

        // ROOM[.prop] — same idea but no per-point room lookup engine yet.
        return null;
    }

    private static string? ResolveServMapSector(string rest)
    {
        // MAP.0.SECTOR.n or MAP.0.SECTOR.n.property or MAP.0.ALLSECTORS
        int firstDot = rest.IndexOf('.');
        if (firstDot < 0) return null;

        string mapStr = rest[..firstDot];
        if (!int.TryParse(mapStr, out int mapNum)) return null;

        string sub = rest[(firstDot + 1)..];

        if (sub.StartsWith("SECTOR.", StringComparison.OrdinalIgnoreCase))
        {
            string sectorPart = sub[7..]; // after "SECTOR."
            int propDot = sectorPart.IndexOf('.');
            string sectorIdxStr = propDot >= 0 ? sectorPart[..propDot] : sectorPart;
            if (!int.TryParse(sectorIdxStr, out int sectorIdx)) return null;

            // Convert linear index to x,y (assuming 96 cols for map 0)
            int cols = 96;
            int sx = sectorIdx % cols;
            int sy = sectorIdx / cols;
            var sector = _world?.GetSector(mapNum, sx, sy);
            if (sector == null) return "0";

            if (propDot < 0) return sector.GetName(); // just "MAP.0.SECTOR.n" — return name

            string prop = sectorPart[(propDot + 1)..];
            if (sector.TryGetProperty(prop, out string val))
                return val;
            return "0";
        }

        return null;
    }

    private static string? ResolveMapListProperty(string rest)
    {
        // MAPLIST.0 → 1 (valid), MAPLIST.0.BOUND.X → max X, etc.
        int dotIdx = rest.IndexOf('.');
        string mapStr = dotIdx >= 0 ? rest[..dotIdx] : rest;
        if (!int.TryParse(mapStr, out int mapNum))
            return null;

        // Only map 0 (Felucca) supported currently
        if (mapNum != 0)
            return "0";

        if (dotIdx < 0)
            return "1"; // map exists

        string sub = rest[(dotIdx + 1)..];
        return sub switch
        {
            "BOUND.X" => "6144",
            "BOUND.Y" => "4096",
            "CENTER.X" => "3072",
            "CENTER.Y" => "2048",
            "SECTOR.SIZE" => "64",
            "SECTOR.COLS" => "96",
            "SECTOR.ROWS" => "64",
            "SECTOR.QTY" => "6144",
            _ => null
        };
    }

    /// <summary>Resolve <c>SERV.LOOKUPSKILL &lt;name&gt;</c>. Accepts either the
    /// enum name ("Alchemy") or the defname stored in a loaded SKILL block.
    /// Returns the numeric skill id, or "-1" if no match.</summary>
    private static string? ResolveLookupSkill(string name)
    {
        string trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return "-1";

        // Enum name match first (case-insensitive)
        if (Enum.TryParse<SphereNet.Core.Enums.SkillType>(trimmed, true, out var sk)
            && sk != (SphereNet.Core.Enums.SkillType)(-1))
        {
            return ((int)sk).ToString();
        }

        // Defname lookup via the resource holder: SKILLs register under
        // their defname ("Skill_Alchemy" or "Alchemy") so the resolver
        // can map script names back to the enum slot.
        if (_resources != null)
        {
            var rid = _resources.ResolveDefName(trimmed);
            if (rid.IsValid && rid.Type == SphereNet.Core.Enums.ResType.SkillDef)
                return rid.Index.ToString();
        }

        return "-1";
    }

    private static string? ResolveServAccount(string rest)
    {
        if (_accounts == null) return null;

        // SERV.ACCOUNT.name → account reference
        // For script property lookups we return the account name or "0" if not found
        var acct = _accounts.FindAccount(rest);
        if (acct != null)
            return acct.Name;

        // SERV.ACCOUNT.n (zero-based index) — not easily supported with dictionary, return "0"
        if (int.TryParse(rest, out _))
            return "0";

        return null;
    }

    private static void PerformSave()
    {
        // Source-X DEFMSG_WORLDSAVE_S behaviour: tell every online player a
        // save is happening so they don't blame momentary lag on the server
        // crashing. We use the world-event hue (0x0040, light red) which
        // matches the colour OSI/Source-X uses for global system events.
        const ushort SaveHue = 0x0040;
        BroadcastToAllPlayers(ServerMessages.Get("worldsave_started"), SaveHue);

        _log.LogInformation("Saving world...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _systemHooks.DispatchServer("save", _serverHookContext);
            _housingEngine?.SerializeAllToTags();
            _shipEngine?.SerializeAllToTags();
            _guildManager?.SerializeAllToTags(_world);
            _spellEngine.RevertAllForSave();
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string sp = ResolvePath(basePath, _config.WorldSaveDir);
            _saver.Save(_world, sp);
            _spellEngine.ReapplyAllAfterSave();
            string accDir = ResolvePath(basePath, _config.AccountDir);
            SphereNet.Persistence.Accounts.AccountPersistence.Save(
                _accounts, accDir, _saver.Format,
                _loggerFactory.CreateLogger("AccountPersistence"));
            _saveCount++;
            sw.Stop();
            double secs = sw.Elapsed.TotalSeconds;
            _log.LogInformation("Save complete. ({Secs:F2} sec)", secs);
            BroadcastToAllPlayers(
                ServerMessages.GetFormatted("worldsave_complete", _saveCount, $"{secs:F2}"),
                SaveHue);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "World save failed");
            BroadcastToAllPlayers(
                ServerMessages.GetFormatted("worldsave_failed", ex.Message),
                SaveHue);
        }
    }

    /// <summary>Send a sysmessage to every logged-in player. Used for global
    /// events (world save start/complete, shutdown countdown, etc.) where
    /// Source-X uses g_World.Broadcast() / addBarkParse(...,
    /// CCharBase::ALLCHARS, ...).</summary>
    private static void BroadcastToAllPlayers(string text, ushort hue)
    {
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var c in _clients.Values)
        {
            if (!c.IsPlaying)
                continue;
            try
            {
                c.SysMessage(text, hue);
            }
            catch
            {
                // Don't let a single dead socket abort the broadcast — a
                // disconnected client during save is normal at server tick
                // boundaries; the connection will be reaped shortly.
            }
        }
    }

    /// <summary>Handle a <c>.SAVEFORMAT</c> request: parse format name, update
    /// the saver, then immediately persist so the user can confirm the new
    /// files land on disk. Invalid format strings are rejected without any
    /// state change so a typo can't nuke the save path.</summary>
    private static void HandleSaveFormatChange(string fmtName, int shards)
    {
        if (!Enum.TryParse<SphereNet.Core.Configuration.SaveFormat>(fmtName, ignoreCase: true, out var fmt))
        {
            _log.LogWarning("SAVEFORMAT: unknown format '{Name}'. Valid: Text, TextGz, Binary, BinaryGz",
                fmtName);
            return;
        }
        _saver.Format = fmt;
        _config.SaveFormat = fmt;
        if (shards >= 1)
        {
            _saver.ShardCount = shards;
            _config.SaveShards = shards;
        }
        _log.LogInformation("SAVEFORMAT: switching to {Format} (shards={Shards}) and saving now",
            fmt, _saver.ShardCount);
        PerformSave();
    }

    private static void ShowRecordingDialog(Character gm)
    {
        if (!_clientsByCharUid.TryGetValue(gm.Uid, out var client)) return;

        bool isRecording = _recordingEngine.IsRecording(gm.Uid.Value);
        var recordings = _recordingEngine.ListRecordings();

        var gump = SphereNet.Game.Recording.RecordingDialog.Build(gm.Uid.Value, isRecording, recordings);
        client.SendGump(gump, (buttonId, _, _) =>
        {
            var action = SphereNet.Game.Recording.RecordingDialog.ParseResponse(buttonId);
            HandleRecordDialogAction(gm, action);
        });
    }

    private static void HandleRecordDialogAction(Character gm, SphereNet.Game.Recording.RecordDialogAction action)
    {
        switch (action.Type)
        {
            case SphereNet.Game.Recording.RecordActionType.StartRecord:
                _recordingEngine.StartRecording(gm);
                SendSysMessage(gm, "Recording started.");
                ShowRecordingDialog(gm);
                break;

            case SphereNet.Game.Recording.RecordActionType.StopRecord:
                var session = _recordingEngine.StopRecording(gm.Uid.Value);
                if (session != null)
                    SendSysMessage(gm, $"Recording saved: {session.Packets.Count} packets, {session.DurationMs / 1000.0:F1}s");
                ShowRecordingDialog(gm);
                break;

            case SphereNet.Game.Recording.RecordActionType.Play:
                StartReplayForPlayer(gm, action.SelectedIndex);
                break;

            case SphereNet.Game.Recording.RecordActionType.Delete:
                _recordingEngine.DeleteRecording(action.SelectedIndex);
                SendSysMessage(gm, "Recording deleted.");
                ShowRecordingDialog(gm);
                break;

            case SphereNet.Game.Recording.RecordActionType.Refresh:
                ShowRecordingDialog(gm);
                break;
        }
    }

    private static void StartReplayForPlayer(Character gm, int index)
    {
        if (_recordingEngine.IsReplaying(gm.Uid.Value))
        {
            SendSysMessage(gm, "Already replaying.");
            return;
        }
        var session = _recordingEngine.LoadRecording(index);
        if (session == null)
        {
            SendSysMessage(gm, "Recording not found.");
            return;
        }
        var state = _recordingEngine.StartReplay(gm, session);
        if (state == null) return;

        gm.SetStatFlag(StatFlag.Invisible);
        gm.SetStatFlag(StatFlag.Freeze);
        gm.IsReplaySpectator = true;
        _world.MoveCharacter(gm, session.Center);

        if (_clientsByCharUid.TryGetValue(gm.Uid, out var client))
        {
            var center = session.Center;
            client.NetState.Send(new PacketDrawObject(
                gm.Uid.Value, 0, center.X, center.Y, center.Z,
                0, 0, 0, 0, []).Build());
        }

        SendReplayOverlay(gm);
    }

    private static void SendReplayOverlay(Character viewer)
    {
        uint uid = viewer.Uid.Value;
        var state = _recordingEngine.GetReplayState(uid);
        if (state == null) return;
        if (!_clientsByCharUid.TryGetValue(viewer.Uid, out var client)) return;

        int currentMs = _recordingEngine.GetElapsedMs(uid);
        var overlay = SphereNet.Game.Recording.RecordingDialog.BuildReplayOverlay(
            uid, state.Session.RecorderName, state.Session.DurationMs,
            currentMs, state.IsPaused, state.PlaybackSpeed);

        client.SendGump(overlay, (btnId, _, _) => HandleReplayControl(viewer, btnId));
    }

    private static void HandleReplayControl(Character viewer, uint btnId)
    {
        uint uid = viewer.Uid.Value;

        switch (btnId)
        {
            case 0:
            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnStop:
                FinishReplay(viewer);
                SendSysMessage(viewer, "Replay stopped.");
                return;

            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnPlayPause:
            {
                var st = _recordingEngine.GetReplayState(uid);
                if (st == null) return;
                if (st.IsPaused)
                    _recordingEngine.ResumeReplay(uid);
                else
                    _recordingEngine.PauseReplay(uid);
                break;
            }

            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnRewind:
            {
                int current = _recordingEngine.GetElapsedMs(uid);
                _recordingEngine.SeekReplay(uid, Math.Max(0, current - 10_000),
                    ReplaySendPacket, ReplayCameraUpdate);
                break;
            }

            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnForward:
            {
                int current = _recordingEngine.GetElapsedMs(uid);
                var st = _recordingEngine.GetReplayState(uid);
                if (st == null) return;
                _recordingEngine.SeekReplay(uid, Math.Min(st.Session.DurationMs, current + 10_000),
                    ReplaySendPacket, ReplayCameraUpdate);
                break;
            }

            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnSpeed1x:
                _recordingEngine.SetPlaybackSpeed(uid, 1f);
                break;
            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnSpeed2x:
                _recordingEngine.SetPlaybackSpeed(uid, 2f);
                break;
            case SphereNet.Game.Recording.RecordingDialog.OverlayBtnSpeed4x:
                _recordingEngine.SetPlaybackSpeed(uid, 4f);
                break;

            default:
                return;
        }

        SendReplayOverlay(viewer);
    }

    private static void ReplaySendPacket(uint uid, byte[] data)
    {
        if (_clientsByCharUid.TryGetValue(new Serial(uid), out var c))
            c.NetState.Send(new PacketBuffer(data));
    }

    private static void ReplayCameraUpdate(uint viewerUid, short x, short y, sbyte z, byte dir)
    {
        var ch = _world.FindChar(new Serial(viewerUid));
        if (ch == null) return;
        byte map = ch.Position.Map;
        _world.MoveCharacter(ch, new Point3D(x, y, z, map));
        if (_clientsByCharUid.TryGetValue(new Serial(viewerUid), out var c))
        {
            c.NetState.Send(new PacketDrawPlayer(
                ch.Uid.Value, 0, 0, 0,
                x, y, z, dir).Build());
        }
    }

    private static void FinishReplay(Character gm)
    {
        var phantoms = _recordingEngine.GetPhantomSerials(gm.Uid.Value);
        var state = _recordingEngine.GetReplayState(gm.Uid.Value);
        if (state != null)
        {
            _world.MoveCharacter(gm, state.OriginalPosition);
            if (!state.WasInvisible)
                gm.ClearStatFlag(StatFlag.Invisible);
            gm.ClearStatFlag(StatFlag.Freeze);
            gm.IsReplaySpectator = false;
        }
        _recordingEngine.StopReplay(gm.Uid.Value);

        if (_clientsByCharUid.TryGetValue(gm.Uid, out var client))
        {
            foreach (uint phantom in phantoms)
                client.NetState.Send(new PacketDeleteObject(phantom).Build());
            client.Resync();
        }
    }

    private static ushort MapAnimToMounted(ushort action)
    {
        return action switch
        {
            (ushort)AnimationType.CastDirected or
            (ushort)AnimationType.CastArea => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.AttackWeapon or
            (ushort)AnimationType.Attack1HPierce or
            (ushort)AnimationType.Attack1HBash or
            (ushort)AnimationType.Attack2HBash or
            (ushort)AnimationType.Attack2HSlash or
            (ushort)AnimationType.Attack2HPierce or
            (ushort)AnimationType.AttackWrestle => (ushort)AnimationType.HorseAttack,
            (ushort)AnimationType.AttackBow => (ushort)AnimationType.HorseAttackBow,
            (ushort)AnimationType.AttackXBow => (ushort)AnimationType.HorseAttackXBow,
            (ushort)AnimationType.GetHit => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.Block => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.Bow or
            (ushort)AnimationType.Salute or
            (ushort)AnimationType.Eat => (ushort)AnimationType.HorseSlap,
            _ => action
        };
    }

    private static void TickRecordingReplays()
    {
        _recordingEngine.TickReplays(ReplaySendPacket,
            uid =>
            {
                var ch = _world.FindChar(new Serial(uid));
                if (ch != null)
                {
                    FinishReplay(ch);
                    SendSysMessage(ch, "Replay finished.");
                }
            },
            ReplayCameraUpdate);

        long now = Environment.TickCount64;
        foreach (var uid in _recordingEngine.GetActiveReplayUids())
        {
            var state = _recordingEngine.GetReplayState(uid);
            if (state == null) continue;
            if (now - state.LastOverlayTick < 1000) continue;
            state.LastOverlayTick = now;

            var ch = _world.FindChar(new Serial(uid));
            if (ch != null)
                SendReplayOverlay(ch);
        }
    }

    private static void ResyncCharacterClient(Character ch)
    {
        if (_clientsByCharUid.TryGetValue(ch.Uid, out var client))
            client.Resync();
    }

    private static void SendSysMessage(Character ch, string text)
    {
        if (_clientsByCharUid.TryGetValue(ch.Uid, out var client))
            client.SysMessage(text);
    }

    private static void HandleBotCommand(int count, string behavior, bool isStop)
    {
        if (_botEngine == null) return;

        if (isStop)
        {
            _botEngine.StopAllBots();
            return;
        }

        if (behavior.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
        {
            _botEngine.LogStats();
            return;
        }

        if (behavior.Equals("START", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    int port = _config?.ServPort ?? 2593;
                    await _botEngine.RestartBotsAsync("127.0.0.1", port);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "[BOT] Failed to restart bots");
                }
            });
            return;
        }

        if (behavior.Equals("CLEAN", StringComparison.OrdinalIgnoreCase))
        {
            CleanBotCharacters();
            return;
        }

        if (behavior.StartsWith("SPAWN:", StringComparison.OrdinalIgnoreCase))
        {
            string cityName = behavior[6..];
            var city = cityName.ToUpperInvariant() switch
            {
                "BRITAIN" => SphereNet.Game.Diagnostics.BotSpawnCity.Britain,
                "TRINSIC" => SphereNet.Game.Diagnostics.BotSpawnCity.Trinsic,
                "MOONGLOW" => SphereNet.Game.Diagnostics.BotSpawnCity.Moonglow,
                "YEW" => SphereNet.Game.Diagnostics.BotSpawnCity.Yew,
                "MINOC" => SphereNet.Game.Diagnostics.BotSpawnCity.Minoc,
                "VESPER" => SphereNet.Game.Diagnostics.BotSpawnCity.Vesper,
                "SKARA" => SphereNet.Game.Diagnostics.BotSpawnCity.Skara,
                "JHELOM" => SphereNet.Game.Diagnostics.BotSpawnCity.Jhelom,
                _ => SphereNet.Game.Diagnostics.BotSpawnCity.All
            };
            _botEngine.SetSpawnCity(city);
            return;
        }

        // Parse behavior and optional city from "BEHAVIOR:CITY" format
        string behaviorPart = behavior;
        string? cityPart = null;
        int colonIdx = behavior.IndexOf(':');
        if (colonIdx > 0)
        {
            behaviorPart = behavior[..colonIdx];
            cityPart = behavior[(colonIdx + 1)..];
        }

        var botBehavior = behaviorPart.ToUpperInvariant() switch
        {
            "WALK" => SphereNet.Game.Diagnostics.BotBehavior.RandomWalk,
            "COMBAT" => SphereNet.Game.Diagnostics.BotBehavior.Combat,
            "IDLE" => SphereNet.Game.Diagnostics.BotBehavior.Idle,
            "SMART" => SphereNet.Game.Diagnostics.BotBehavior.SmartAI,
            _ => SphereNet.Game.Diagnostics.BotBehavior.SmartAI
        };

        // Set city if specified
        if (!string.IsNullOrEmpty(cityPart))
        {
            var city = cityPart.ToUpperInvariant() switch
            {
                "BRITAIN" => SphereNet.Game.Diagnostics.BotSpawnCity.Britain,
                "TRINSIC" => SphereNet.Game.Diagnostics.BotSpawnCity.Trinsic,
                "MOONGLOW" => SphereNet.Game.Diagnostics.BotSpawnCity.Moonglow,
                "YEW" => SphereNet.Game.Diagnostics.BotSpawnCity.Yew,
                "MINOC" => SphereNet.Game.Diagnostics.BotSpawnCity.Minoc,
                "VESPER" => SphereNet.Game.Diagnostics.BotSpawnCity.Vesper,
                "SKARA" => SphereNet.Game.Diagnostics.BotSpawnCity.Skara,
                "JHELOM" => SphereNet.Game.Diagnostics.BotSpawnCity.Jhelom,
                _ => SphereNet.Game.Diagnostics.BotSpawnCity.All
            };
            _botEngine.SetSpawnCity(city);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                int port = _config?.ServPort ?? 2593;
                await _botEngine.SpawnBotsAsync(count, botBehavior, "127.0.0.1", port);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[BOT] Failed to spawn bots");
            }
        });
    }

    private static void RestockBotCharacters()
    {
        if (_world == null) return;
        foreach (var ch in _world.OnlinePlayers)
        {
            if (!SphereNet.Game.Diagnostics.BotClient.IsBotCharName(ch.Name ?? "")) continue;
            var pack = ch.Backpack;
            if (pack == null) continue;

            RestockItem(pack, 0x0E21, "Bandage", 30);  // bandages
            RestockItem(pack, 0x0F0C, "Heal Potion", 5); // heal potions

            var weapon = ch.GetEquippedItem(Layer.OneHanded) ?? ch.GetEquippedItem(Layer.TwoHanded);
            if (weapon == null)
            {
                var sword = _world.CreateItem();
                sword.BaseId = 0x0F5E; // broadsword
                sword.Name = "Broadsword";
                ch.Equip(sword, Layer.TwoHanded);
            }

            // Spawn a mount nearby if none exists within 5 tiles
            bool hasMountNearby = ch.GetEquippedItem(Layer.Horse) != null;
            if (!hasMountNearby)
            {
                foreach (var nearby in _world.GetCharsInRange(ch.Position, 5))
                {
                    if (nearby != ch && nearby.BodyId is >= 0x00C8 and <= 0x00E4)
                    { hasMountNearby = true; break; }
                }
            }
            if (!hasMountNearby && !ch.IsDead)
            {
                var horse = _world.CreateCharacter();
                horse.BodyId = 0x00C8;
                horse.Name = "Horse";
                horse.NpcBrain = NpcBrainType.Animal;
                horse.Hits = 50;
                horse.MaxHits = 50;
                horse.SetTag("STRESS_TEST", "1");
                var horsePos = new Point3D(
                    (short)(ch.X + 1), (short)(ch.Y + 1), ch.Z, ch.MapIndex);
                _world.PlaceCharacter(horse, horsePos);
            }
        }
    }

    private static void RestockItem(Item pack, ushort baseId, string name, int targetAmount)
    {
        if (_world == null) return;
        int current = 0;
        foreach (var item in pack.Contents)
        {
            if (item.BaseId == baseId)
                current += item.Amount;
        }
        if (current >= targetAmount / 2) return;
        int toAdd = targetAmount - current;
        if (toAdd <= 0) return;
        var newItem = _world.CreateItem();
        newItem.BaseId = baseId;
        newItem.Name = name;
        newItem.Amount = (ushort)toAdd;
        pack.AddItem(newItem);
    }

    private static void CleanBotCharacters()
    {
        if (_botEngine == null || _world == null) return;

        _log.LogInformation("[BOT] Cleaning bot characters and accounts...");

        // Step 1: Find and delete bot characters (and their items)
        int charsDeleted = 0;
        int itemsDeleted = 0;
        var toDelete = new List<SphereNet.Game.Objects.Characters.Character>();

        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is SphereNet.Game.Objects.Characters.Character ch && 
                ch.Name != null && 
                SphereNet.Game.Diagnostics.BotClient.IsBotCharName(ch.Name))
            {
                toDelete.Add(ch);
            }
        }

        foreach (var ch in toDelete)
        {
            try
            {
                // Delete items in backpack/equipment first
                var backpack = ch.Backpack;
                if (backpack != null)
                {
                    var items = _world.GetContainerContents(backpack.Uid).ToList();
                    foreach (var item in items)
                    {
                        _world.DeleteObject(item);
                        itemsDeleted++;
                    }
                    _world.DeleteObject(backpack);
                    itemsDeleted++;
                }

                _world.DeleteObject(ch);
                charsDeleted++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[BOT] Failed to delete character {Name}", ch.Name);
            }
        }

        // Step 2: Delete bot accounts
        int accountsDeleted = 0;
        var botAccounts = _accounts.GetAllAccounts()
            .Where(a => SphereNet.Game.Diagnostics.BotClient.IsBotAccountName(a.Name))
            .Select(a => a.Name)
            .ToList();

        foreach (var accName in botAccounts)
        {
            if (_accounts.DeleteAccount(accName))
                accountsDeleted++;
        }

        _botEngine.ResetBotCounter();
        _log.LogInformation("[BOT] Cleanup complete: {Chars} characters, {Items} items, {Accounts} accounts deleted.",
            charsDeleted, itemsDeleted, accountsDeleted);
    }

    private static void ShowBotManagerDialog(SphereNet.Game.Objects.Characters.Character gm)
    {
        if (_botEngine == null) return;

        // Find the GameClient for this character
        if (!_clientsByCharUid.TryGetValue(gm.Uid, out var client)) return;

        var stats = _botEngine.GetStats();
        var currentCity = _botEngine.SpawnCity;
        int lastCount = _botEngine.GetMaxBotId() > 0 ? _botEngine.GetMaxBotId() : 100;

        var gump = SphereNet.Game.Diagnostics.BotManagerDialog.Build(
            gm.Uid.Value, stats, currentCity, lastCount);

        client.SendGump(gump, (buttonId, switches, textEntries) =>
        {
            var action = SphereNet.Game.Diagnostics.BotManagerDialog.ParseResponse(buttonId, switches, textEntries);
            HandleBotDialogAction(gm, action);
        });
    }

    private static void HandleBotDialogAction(SphereNet.Game.Objects.Characters.Character gm, 
        SphereNet.Game.Diagnostics.BotDialogAction action)
    {
        if (_botEngine == null) return;

        switch (action.ActionType)
        {
            case SphereNet.Game.Diagnostics.BotActionType.Start:
                if (action.BotCount <= 0) action.BotCount = 100;
                _botEngine.SetSpawnCity(action.City);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        int port = _config?.ServPort ?? 2593;
                        await _botEngine.SpawnBotsAsync(action.BotCount, action.Behavior, "127.0.0.1", port);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "[BOT] Failed to spawn bots");
                    }
                });
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var startClient))
                    startClient.SysMessage($"Starting {action.BotCount} bots with {action.Behavior} in {action.City}...");
                break;

            case SphereNet.Game.Diagnostics.BotActionType.Stop:
                _botEngine.StopAllBots();
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var stopClient))
                    stopClient.SysMessage("All bots stopped.");
                break;

            case SphereNet.Game.Diagnostics.BotActionType.Clean:
                CleanBotCharacters();
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var cleanClient))
                    cleanClient.SysMessage("Bot characters cleaned.");
                break;

            case SphereNet.Game.Diagnostics.BotActionType.Refresh:
                ShowBotManagerDialog(gm);
                return;
        }

        // Reopen dialog after action (except for refresh which already does it)
        if (action.ActionType != SphereNet.Game.Diagnostics.BotActionType.Refresh &&
            action.ActionType != SphereNet.Game.Diagnostics.BotActionType.None)
        {
            Task.Delay(500).ContinueWith(_ => ShowBotManagerDialog(gm));
        }
    }

    /// <summary>
    /// Script hot-reload (Source-X RESYNC). Reloads all modified .scp files
    /// from disk without restarting the server. Triggered via:
    ///   - Console key 'R'
    ///   - GM command ".RESYNC"
    ///   - Telnet "RESYNC"
    /// After reload, re-processes definitions (spells, items, chars).
    /// </summary>
    private static void PerformScriptResync()
    {
        _log.LogInformation("ReSync: scanning for modified script files...");
        _systemHooks.DispatchServer("resync", _serverHookContext);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int reloaded = _resources.Resync();

        if (reloaded == 0)
        {
            _log.LogInformation("ReSync: no modified files found.");
            BroadcastSysMessage("ReSync: no changes detected.");
            return;
        }

        // Re-process definitions from reloaded resources
        var defLoader = new DefinitionLoader(_resources, _spellRegistry);
        defLoader.LoadAll();
        _commands?.InvalidateAreaCache();
        if (_commands != null)
        {
            int scriptCmdCount = _commands.LoadScriptCommandPrivileges(_resources);
            _log.LogInformation("ReSync: reloaded {Count} script command privilege entries.", scriptCmdCount);
        }

        sw.Stop();
        _log.LogInformation(
            "ReSync complete: {Files} files reloaded, {Spells} spells, {Items} itemdefs, {Chars} chardefs ({Ms}ms)",
            reloaded, defLoader.SpellsLoaded, defLoader.ItemDefsLoaded, defLoader.CharDefsLoaded,
            sw.ElapsedMilliseconds);

        BroadcastSysMessage($"ReSync: {reloaded} script files reloaded in {sw.ElapsedMilliseconds}ms.");
        SphereNet.Scripting.Parsing.ScriptFile.ClearFileCache();
    }

    private static void BroadcastSysMessage(string message)
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsPlaying)
                client.SysMessage(message);
        }
    }

    private static void SyncOnlineAccountPrivLevel(string accountName, PrivLevel level)
    {
        foreach (var client in _clients.Values)
        {
            if (!client.IsPlaying || client.Account == null || client.Character == null) continue;
            if (!client.Account.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase)) continue;

            client.Account.PrivLevel = level;
            client.SysMessage($"Your privilege level is now {level} ({(int)level}).");
            _log.LogInformation("Online privilege sync: account={Account} char=0x{Char:X8} -> {Level}",
                accountName, client.Character.Uid.Value, level);
        }
    }

    private static void InitializeSpawnItems()
    {
        int spawns = 0;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is SphereNet.Game.Objects.Items.Item item &&
                item.ItemType == SphereNet.Core.Enums.ItemType.SpawnChar)
            {
                item.InitializeSpawnComponent(_world, _resources);
                spawns++;
            }
        }
        if (spawns > 0)
            _log.LogInformation("Initialized {Count} spawn items", spawns);
    }

    private static void PlaceTeleporters()
    {
        var teleporters = _resources.Teleporters;
        if (teleporters.Count == 0) return;

        // Remove previously placed script teleporters (Static + Telepad)
        var toRemove = new List<SphereNet.Game.Objects.Items.Item>();
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is SphereNet.Game.Objects.Items.Item item &&
                item.ItemType == ItemType.Telepad &&
                item.IsAttr(ObjAttributes.Static))
            {
                toRemove.Add(item);
            }
        }
        foreach (var item in toRemove)
            item.Delete();

        int placed = 0;
        foreach (var (src, dest, name) in teleporters)
        {
            var item = _world.CreateItem();
            item.BaseId = 0x1BC3;
            item.ItemType = ItemType.Telepad;
            item.MoreP = dest;
            item.Name = string.IsNullOrEmpty(name) ? "teleporter" : name;
            item.SetAttr(ObjAttributes.Invis | ObjAttributes.Static | ObjAttributes.Move_Never);
            _world.PlaceItem(item, src);
            placed++;
        }

        _log.LogInformation("Placed {Count} teleporters from scripts ({Removed} old removed)",
            placed, toRemove.Count);
    }

    private static void OnWorldObjectCreated(SphereNet.Game.Objects.ObjBase obj)
    {
        _systemHooks.DispatchObject("create", obj);
        if (obj.IsItem)
            _systemHooks.DispatchItem("create", obj);

        // Schedule new NPCs into the timer wheel
        if (_npcTimerWheel != null && obj is Character npc && !npc.IsPlayer)
            _npcTimerWheel.Schedule(npc, Environment.TickCount64 + 500);
    }

    private static void OnWorldObjectDeleting(SphereNet.Game.Objects.ObjBase obj)
    {
        _systemHooks.DispatchObject("delete", obj);
        if (obj.IsItem)
            _systemHooks.DispatchItem("delete", obj);
    }

    private static void OnUnknownPacket(NetState state, byte opcode, byte[] raw)
    {
        if (!_clients.TryGetValue(state.Id, out var client))
            return;
        IScriptObj? src = client.Character ?? (IScriptObj?)client.Account;
        if (src == null)
            return;
        _systemHooks.DispatchClient("unkdata", src, client.Character, $"0x{opcode:X2}", opcode, raw.Length);
    }

    private static void OnPacketQuotaExceeded(NetState state, int processed)
    {
        if (!_clients.TryGetValue(state.Id, out var client))
            return;
        IScriptObj? src = client.Character ?? (IScriptObj?)client.Account;
        if (src == null)
            return;
        _systemHooks.DispatchClient("quotaexceed", src, client.Character, processed.ToString(), processed);
    }

    private static bool HandlePacketScriptHook(NetState state, byte opcode, byte[] packet)
    {
        if (opcode != 0x03 && opcode != 0xAD && opcode != 0x6C && opcode != 0x72 && opcode != 0x22)
            return false;

        if (!_clients.TryGetValue(state.Id, out var client))
            return false;

        IScriptObj? src = client.Character ?? (IScriptObj?)client.Account;
        if (src == null)
            return false;

        string payloadHex = Convert.ToHexString(packet);
        bool handled = _systemHooks.DispatchPacket(opcode, src, client.Character, payloadHex);

        // Keep script hook visibility for war/peace packets, but do not allow
        // script short-circuit to block core war mode state changes.
        if (opcode == 0x72)
            return false;

        return handled;
    }

    private static string? ResolveDefMessage(string key)
    {
        return _resources.TryGetDefMessage(key, out var message) ? message : null;
    }

    private static void RegisterDbProviders()
    {
        // Register SQLite provider for ADO.NET DbProviderFactories
        if (!DbProviderFactories.TryGetFactory("Microsoft.Data.Sqlite", out _))
        {
            DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
            _log.LogDebug("Registered SQLite database provider");
        }
    }

    private static void InitDbConnections(SphereConfig config, ScriptDbAdapter db)
    {
        if (config.DbConnections.Count == 0)
        {
            _log.LogDebug("No DB connections configured.");
            return;
        }

        foreach (var connCfg in config.DbConnections)
        {
            db.RegisterConnection(connCfg);

            if (connCfg.AutoConnect)
            {
                string displayInfo = connCfg.IsSqlite
                    ? connCfg.Database
                    : $"{connCfg.Host}/{connCfg.Database}";

                if (db.Connect(connCfg.Name, out string err))
                    _log.LogInformation("DB '{Name}' connected ({Info})",
                        connCfg.Name, displayInfo);
                else
                    _log.LogWarning("DB '{Name}' auto-connect failed: {Error}", connCfg.Name, err);
            }
        }

        _log.LogInformation("Registered {Count} DB connection(s)", config.DbConnections.Count);
    }

    private static HashSet<byte>? ParseDebugPacketOpcodes(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var set = new HashSet<byte>();
        foreach (var token in raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            string part = token.Trim();
            if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                part = part[2..];

            if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte opcode))
            {
                set.Add(opcode);
            }
        }

        return set.Count > 0 ? set : null;
    }

    // --- Network Event Handlers ---

    private static void OnConnectionClosed(int stateId)
    {
        if (_clients.TryGetValue(stateId, out var client))
        {
            client.OnDisconnect();
            _clients.Remove(stateId);
        }
    }

    private static GameClient GetOrCreateClient(NetState state)
    {
        if (!_clients.TryGetValue(state.Id, out var client))
        {
            client = new GameClient(state, _world, _accounts,
                _loggerFactory.CreateLogger<GameClient>());
            client.SetEngines(_movement, _speech, _commands, _spellEngine, _deathEngine, _partyManager, _tradeManager,
                _skillHandlers, _craftingEngine, _housingEngine, _triggerDispatcher, _guildManager, _mountEngine);
            client.SetScriptServices(_systemHooks, _scriptDb, ResolveDefMessage, _scriptFile, _scriptLdb);
            client.BroadcastNearby = BroadcastNearby;
            client.BroadcastMoveNearby = BroadcastMoveNearby;
            client.ForEachClientInRange = ForEachClientInRange;
            client.SendToChar = SendPacketToChar;
            client.BroadcastCharacterAppear = BroadcastCharacterAppear;
            client.OnCharacterDeathOfOther = victim =>
            {
                // Resolve the victim's own client and run its death sequence
                // (ghost transition, 0x77 broadcast, 0x20/0x2C self packets).
                if (_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                    victimClient.OnCharacterDeath();
            };
            client.OnResurrectOther = victim =>
            {
                if (_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                    victimClient.OnResurrect();
                else if (victim.IsDead)
                    victim.Resurrect(); // offline / NPC fallback
            };
            _clients[state.Id] = client;
        }
        return client;
    }

    private static void BroadcastNearby(Point3D center, int range, PacketWriter packet, uint excludeUid)
    {
        int secRadius = (range / SphereNet.Game.World.Sectors.Sector.SectorSize) + 1;
        int cx = center.X / SphereNet.Game.World.Sectors.Sector.SectorSize;
        int cy = center.Y / SphereNet.Game.World.Sectors.Sector.SectorSize;

        if (_recordingEngine.HasActiveRecordings)
        {
            var built = packet.Build();
            _recordingEngine.CaptureFromBroadcast(center, range, built.Span.ToArray());
        }

        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(center.Map, sx, sy);
            if (sector == null) continue;
            foreach (var ch in sector.Characters)
            {
                if (!ch.IsPlayer || !ch.IsOnline) continue;
                if (ch.Uid.Value == excludeUid) continue;
                if (center.GetDistanceTo(ch.Position) > range) continue;
                if (_clientsByCharUid.TryGetValue(ch.Uid, out var c) && c.IsPlaying)
                    c.Send(packet);
            }
        }
    }

    /// <summary>
    /// Per-observer dispatch helper. Walks every online player whose character
    /// is within <paramref name="range"/> tiles of <paramref name="center"/>
    /// and invokes <paramref name="action"/> with both the observer Character
    /// and its GameClient. Used by the death/resurrect pipeline where the
    /// packet sent depends on the observer (plain player vs Counsel+ staff
    /// vs the dying player itself) — the standard BroadcastNearby helper
    /// can only dispatch a single packet to everyone.
    ///
    /// <paramref name="excludeUid"/> behaves like BroadcastNearby — pass 0
    /// to include everyone (the action can decide what to send to the
    /// dying player), or a specific UID to skip a single character.
    /// </summary>
    private static void ForEachClientInRange(Point3D center, int range, uint excludeUid,
        Action<Character, GameClient> action)
    {
        int secRadius = (range / SphereNet.Game.World.Sectors.Sector.SectorSize) + 1;
        int cx = center.X / SphereNet.Game.World.Sectors.Sector.SectorSize;
        int cy = center.Y / SphereNet.Game.World.Sectors.Sector.SectorSize;
        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(center.Map, sx, sy);
            if (sector == null) continue;
            foreach (var ch in sector.Characters)
            {
                if (!ch.IsPlayer || !ch.IsOnline) continue;
                if (ch.Uid.Value == excludeUid) continue;
                if (center.GetDistanceTo(ch.Position) > range) continue;
                if (_clientsByCharUid.TryGetValue(ch.Uid, out var c) && c.IsPlaying)
                    action(ch, c);
            }
        }
    }

    /// <summary>
    /// Movement-specific broadcast: sends 0x77 AND updates each receiving client's
    /// _lastKnownPos so the view delta won't send a duplicate 0x77 for the same step.
    /// Only sends to clients that already know this mobile — new-in-range receivers
    /// get a 0x78 (DrawObject) from the view delta instead, avoiding a race where
    /// 0x77 arrives before the client has spawned the mobile.
    /// </summary>
    private static void BroadcastMoveNearby(Point3D center, int range, PacketWriter packet,
        uint excludeUid, Character movingChar)
    {
        if (_recordingEngine.HasActiveRecordings)
        {
            var built = packet.Build();
            _recordingEngine.CaptureFromBroadcast(center, range, built.Span.ToArray(), movingChar.Uid.Value);
        }

        uint movingUid = movingChar.Uid.Value;
        int secRadius = (range / SphereNet.Game.World.Sectors.Sector.SectorSize) + 1;
        int cx = center.X / SphereNet.Game.World.Sectors.Sector.SectorSize;
        int cy = center.Y / SphereNet.Game.World.Sectors.Sector.SectorSize;
        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(center.Map, sx, sy);
            if (sector == null) continue;
            foreach (var ch in sector.Characters)
            {
                if (!ch.IsPlayer || !ch.IsOnline) continue;
                if (ch.Uid.Value == excludeUid) continue;
                if (center.GetDistanceTo(ch.Position) > range) continue;
                if (!_clientsByCharUid.TryGetValue(ch.Uid, out var c) || !c.IsPlaying) continue;
                if (!c.HasKnownChar(movingUid)) continue; // spawn via view-delta 0x78
                c.Send(packet);
                c.UpdateKnownCharPosition(movingChar);
            }
        }
    }

    /// <summary>
    /// Notify all nearby clients that a character appeared (login/teleport).
    /// Each client renders from its own perspective (notoriety, equipment, etc.).
    /// </summary>
    private static void BroadcastCharacterAppear(Character ch)
    {
        // Login/teleport notification — limited to the view-range sector
        // window around the character. Distant clients wouldn't render
        // the mobile anyway and their view-delta pass would drop the
        // entry on the next tick.
        const int Range = 18;
        int secRadius = (Range / SphereNet.Game.World.Sectors.Sector.SectorSize) + 1;
        int cx = ch.Position.X / SphereNet.Game.World.Sectors.Sector.SectorSize;
        int cy = ch.Position.Y / SphereNet.Game.World.Sectors.Sector.SectorSize;
        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(ch.Position.Map, sx, sy);
            if (sector == null) continue;
            foreach (var other in sector.Characters)
            {
                if (other == ch) continue;
                if (!other.IsPlayer || !other.IsOnline) continue;
                if (ch.Position.GetDistanceTo(other.Position) > Range) continue;
                if (_clientsByCharUid.TryGetValue(other.Uid, out var c) && c.IsPlaying)
                    c.NotifyCharacterAppear(ch);
            }
        }
    }

    /// <summary>Send a packet to a specific character by UID.</summary>
    private static void SendPacketToChar(Serial charUid, PacketWriter packet)
    {
        if (_clientsByCharUid.TryGetValue(charUid, out var c) && c.IsPlaying)
            c.Send(packet);
    }

    private static void OnLoginRequest(NetState state, string account, string password)
    {
        var client = GetOrCreateClient(state);
        client.HandleLoginRequest(account, password);
    }

    private static void OnServerSelect(NetState state, ushort serverIndex)
    {
        uint ip;
        if (_config.ServIP == "0.0.0.0" || string.IsNullOrEmpty(_config.ServIP))
        {
            var localEp = state.LocalEndPoint;
            if (localEp != null)
            {
                var bytes = localEp.Address.GetAddressBytes();
                ip = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
            }
            else
            {
                ip = 0x7F000001; // 127.0.0.1
            }
        }
        else
        {
            if (System.Net.IPAddress.TryParse(_config.ServIP, out var addr))
            {
                var bytes = addr.GetAddressBytes();
                ip = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
            }
            else
            {
                ip = 0x7F000001;
            }
        }

        ushort port = (ushort)_config.ServPort;
        uint authId = (uint)Random.Shared.Next(1, int.MaxValue);
        state.AuthId = authId;

        // Store login crypto keys for the game connection (Source-X RelayGameCryptStart)
        SphereNet.Network.Encryption.CryptoState.StoreRelayKeys(authId, state.Crypto.Key1, state.Crypto.Key2, state.ClientVersionNumber);
        _log.LogDebug("Relay #{Id}: ip=0x{IP:X8}, port={Port}, authId=0x{AuthId:X8}",
            state.Id, ip, port, authId);

        state.Send(new PacketRelay(ip, port, authId));

        // Login connection is no longer needed after relay — the client will open
        // a new TCP connection for the game server.  Mark this one for closure so it
        // doesn't linger until the idle-timeout fires.
        state.MarkClosing();
    }

    private static void OnGameLogin(NetState state, string account, string password, uint authId)
    {
        var client = GetOrCreateClient(state);
        client.HandleGameLogin(account, password, authId);
        if (client.Account != null)
            _systemHooks.DispatchAccount("connect", client.Account, client.Character);
    }

    /// <summary>
    /// Kick any existing client playing the same character.
    /// Allows multi-client with different characters on the same account.
    /// </summary>
    private static void KickDuplicateCharacter(uint charUid, int excludeStateId)
    {
        foreach (var kvp in _clients.ToArray())
        {
            if (kvp.Key == excludeStateId) continue;
            var existing = kvp.Value;
            if (existing.Character != null &&
                existing.Character.Uid.Value == charUid)
            {
                _log.LogInformation("Kicking duplicate character 0x{Uid:X8} (old connection #{Id})",
                    charUid, kvp.Key);
                existing.OnDisconnect();
                _clients.Remove(kvp.Key);
                existing.NetState.MarkClosing();
            }
        }
    }

    private static void OnCharCreate(NetState state, string name)
    {
        var client = GetOrCreateClient(state);
        // Find a free slot and treat creation as selecting that slot
        client.HandleCharSelect(-1, name);
    }

    private static void OnCharSelect(NetState state, int slot, string name)
    {
        var client = GetOrCreateClient(state);

        // Aynı karakter zaten online ise eski bağlantıyı kick et
        if (client.Account != null && slot >= 0)
        {
            var charUid = client.Account.GetCharSlot(slot);
            if (charUid.IsValid)
                KickDuplicateCharacter(charUid.Value, state.Id);
        }

        client.HandleCharSelect(slot, name);
    }

    private static void OnMoveRequest(NetState state, byte dir, byte seq, uint fastWalkKey)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMove(dir, seq, fastWalkKey);
    }

    private static void OnSpeech(NetState state, byte type, ushort hue, ushort font, string text)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleSpeech(type, hue, font, text);
    }

    private static void OnAttackRequest(NetState state, uint targetUid)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleAttack(targetUid);
    }

    /// <summary>Scan loaded scripts for [DIALOG &lt;name&gt;] section names
    /// and return up to <paramref name="maxCount"/> that share a prefix
    /// with the (case-insensitive) query. Used by the ".dialog" admin
    /// command's not-found message so singular/plural typos can be
    /// fixed from the hint instead of grepping scripts by hand.</summary>
    private static List<string> CollectDialogSuggestions(string query, int maxCount)
    {
        var results = new List<string>();
        if (_resources == null || string.IsNullOrEmpty(query))
            return results;

        string q = query.ToLowerInvariant();
        string qPrefix = q.Length > 3 ? q[..3] : q;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var script in _resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                foreach (var section in file.ReadAllSections())
                {
                    if (results.Count >= maxCount) break;
                    if (!section.Name.Equals("DIALOG", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string name = section.Argument.Split(' ', 2)[0].Trim();
                    if (name.Length == 0 || !seen.Add(name)) continue;
                    if (name.ToLowerInvariant().Contains(qPrefix))
                        results.Add(name);
                }
            }
            finally { script.Close(); }
            if (results.Count >= maxCount) break;
        }
        return results;
    }

    private static void OnWarMode(NetState state, bool warMode)
    {
        if (_clients.TryGetValue(state.Id, out var client))
        {
            var ch = client.Character;
            if (ch != null && _recordingEngine.IsReplaying(ch.Uid.Value))
            {
                FinishReplay(ch);
                SendSysMessage(ch, "Replay stopped.");
                return;
            }
            client.HandleWarMode(warMode);
        }
    }

    private static void OnDoubleClick(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleDoubleClick(serial);
    }

    private static void OnSingleClick(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleSingleClick(serial);
    }

    private static void OnItemPickup(NetState state, uint serial, ushort amount)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleItemPickup(serial, amount);
    }

    private static void OnItemDrop(NetState state, uint serial, short x, short y, sbyte z, uint container)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleItemDrop(serial, x, y, z, container);
    }

    private static void OnItemEquip(NetState state, uint serial, byte layer, uint charSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleItemEquip(serial, layer, charSerial);
    }

    private static void OnStatusRequest(NetState state, byte type, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleStatusRequest(type, serial);
    }

    private static void OnProfileRequest(NetState state, byte mode, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleProfileRequest(mode, serial);
    }

    private static void OnTargetResponse(NetState state, byte type, uint targetId, uint serial,
        short x, short y, sbyte z, ushort graphic)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleTargetResponse(type, targetId, serial, x, y, z, graphic);
    }

    private static void OnGumpResponse(NetState state, uint serial, uint gumpId, uint buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleGumpResponse(serial, gumpId, buttonId, switches, textEntries);
    }

    private static void OnClientVersion(NetState state, string version)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleClientVersion(version);
    }

    private static void OnAOSTooltip(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleAOSTooltip(serial);
    }

    private static void OnTextCommand(NetState state, byte type, string command)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;

        switch (type)
        {
            case 0x24: // UseSkill
                if (int.TryParse(command.Split(' ')[0], out int skillId))
                    client.HandleUseSkill(skillId);
                break;
            case 0x56: // CastSpell
                break;
            case 0x58: // OpenDoor
                break;
            case 0xF4: // SKILLLOCK
                var parts = command.Split(' ');
                if (parts.Length >= 3 && parts[0] == "SKILLLOCK" &&
                    ushort.TryParse(parts[1], out ushort sid) &&
                    byte.TryParse(parts[2], out byte lockVal))
                {
                    client.Character?.SetSkillLock((SkillType)sid, lockVal);
                }
                break;
        }
    }

    private static void OnExtendedCommand(NetState state, ushort subCmd, PacketBuffer buffer)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;

        byte[] remaining = buffer.ReadBytes(buffer.Remaining);
        client.HandleExtendedCommand(subCmd, remaining);
    }

    private static void OnResyncRequest(NetState state)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;
        client.Resync();
    }

    /// <summary>
    /// 0xD1 — Client requested to return to character select. Send the accept
    /// reply and tear down the in-world client state (mark offline, notify
    /// nearby players) while keeping the TCP connection alive so the client
    /// can receive the char-list without reconnecting.
    /// </summary>
    private static void OnLogoutRequest(NetState state)
    {
        // Always acknowledge so the client transitions out of world.
        state.Send(new PacketLogoutAck());

        if (_clients.TryGetValue(state.Id, out var client))
        {
            client.OnDisconnect();
            // Client object is recycled on next login/char-select; leave the
            // NetState entry in _clients so future packets still route.
        }
    }

    private static void OnHelpRequest(NetState state)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleHelpRequest();
    }

    private static void BroadcastSeasonChange(bool playSound)
    {
        var seasonPacket = new PacketSeason((byte)_weatherEngine.CurrentSeason, playSound);
        foreach (var client in _clients.Values)
        {
            if (client.IsPlaying)
                client.Send(seasonPacket);
        }
    }

    private static void OnViewRange(NetState state, byte range)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleViewRange(range);
    }

    private static void OnVendorBuy(NetState state, uint vendorSerial, byte flag, List<VendorBuyEntry> items)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleVendorBuy(vendorSerial, flag, items);
    }

    private static void OnVendorSell(NetState state, uint vendorSerial, List<VendorSellEntry> items)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleVendorSell(vendorSerial, items);
    }

    private static void OnSecureTrade(NetState state, byte action, uint sessionId, uint param)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleSecureTrade(action, sessionId, param);
    }

    private static void OnRename(NetState state, uint serial, string name)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleRename(serial, name);
    }

    // ==================== Phase 1: Critical Stability ====================

    private static void OnDeathMenu(NetState state, byte action)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleDeathMenu(action);
    }

    private static void OnCharDelete(NetState state, int charIndex, string password)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleCharDelete(charIndex, password);
    }

    private static void OnDyeResponse(NetState state, uint itemSerial, ushort hue)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleDyeResponse(itemSerial, hue);
    }

    private static void OnPromptResponse(NetState state, uint serial, uint promptId, uint type, string text)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandlePromptResponse(serial, promptId, type, text);
    }

    private static void OnMenuChoice(NetState state, uint serial, ushort menuId, ushort index, ushort modelId)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMenuChoice(serial, menuId, index, modelId);
    }

    // ==================== Phase 2: Content Features ====================

    private static void OnBookPage(NetState state, uint serial, List<(ushort PageNum, string[] Lines)> pages)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBookPage(serial, pages);
    }

    private static void OnBookHeader(NetState state, uint serial, bool writable, string title, string author)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBookHeader(serial, writable, title, author);
    }

    private static void OnBulletinBoardRequestList(NetState state, uint boardSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBulletinBoardRequestList(boardSerial);
    }

    private static void OnBulletinBoardRequestMessage(NetState state, uint boardSerial, uint msgSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBulletinBoardRequestMessage(boardSerial, msgSerial);
    }

    private static void OnBulletinBoardPost(NetState state, uint boardSerial, uint replyTo, string subject, string[] bodyLines)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBulletinBoardPost(boardSerial, replyTo, subject, bodyLines);
    }

    private static void OnBulletinBoardDelete(NetState state, uint boardSerial, uint msgSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBulletinBoardDelete(boardSerial, msgSerial);
    }

    private static void OnMapDetail(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMapDetail(serial);
    }

    private static void OnMapPinEdit(NetState state, uint serial, byte action, byte pinId, ushort x, ushort y)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMapPinEdit(serial, action, pinId, x, y);
    }

    // ==================== Phase 3: Client Compatibility ====================

    private static void OnGumpTextEntry(NetState state, uint serial, ushort context, byte action, string text)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleGumpTextEntry(serial, context, action, text);
    }

    private static void OnAllNamesRequest(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleAllNamesRequest(serial);
    }

    /// <summary>
    /// NPC keyword/conversation handler. Routes speech to NPCs for keyword responses.
    /// Maps to Source-X NPC_OnHear / @NPCHearGreeting / @NPCHearUnknown triggers.
    /// </summary>
    /// <summary>Look up the GameClient that owns the given character (player only).</summary>
    private static SphereNet.Game.Clients.GameClient? FindGameClient(Character ch)
    {
        if (!ch.IsPlayer) return null;
        foreach (var c in _clients.Values)
            if (c.Character == ch) return c;
        return null;
    }

    /// <summary>Resolve a defmessage by key, returning empty string if missing.</summary>
    private static string SafeMsg(string key)
    {
        try { return SphereNet.Game.Messages.ServerMessages.Get(key) ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Pre-empt service-NPC well-known keywords before the SPEECH script
    /// chain runs. Returns true when a service action was dispatched
    /// (vendor menu opened, bank box opened, withdrawal completed, ...);
    /// false when the brain doesn't match or none of the keywords applied
    /// — in which case OnNpcHearSpeech keeps walking the chain.
    /// We also send the matching defmessage (NpcVendorBuyfast / "Here are
    /// thy N gold piece(s)." / ...) so the NPC speaks the same line a
    /// real Source-X server would.
    /// </summary>
    private static bool TryDispatchServiceKeyword(Character speaker, Character npc, string text)
    {
        string lower = text.ToLowerInvariant();
        string lowerName = (npc.Name ?? "").ToLowerInvariant();
        NpcBrainType brain = npc.NpcBrain;

        // Mirror the legacy widening: NPC=NPC_HUMAN service NPCs whose
        // names carry the role keyword should still respond as the
        // matching brain (banker / vendor / healer / stable).
        if (brain is NpcBrainType.Human or NpcBrainType.None)
        {
            if (lowerName.Contains("banker")) brain = NpcBrainType.Banker;
            else if (lowerName.Contains("vendor") || lowerName.Contains("shopkeep") ||
                     lowerName.Contains("merchant")) brain = NpcBrainType.Vendor;
        }

        if (brain == NpcBrainType.Vendor)
        {
            if (lower.Contains("buy") || lower.Contains("purchase"))
            {
                var gc = FindGameClient(speaker);
                _log.LogDebug(
                    "[svc_kw] VENDOR_BUY speaker={Speaker} npc={Npc} client={HasClient}",
                    speaker.Name, npc.Name, gc != null);
                gc?.OpenVendorBuy(npc);
                NpcSpeak(npc, SafeMsg(SphereNet.Game.Messages.Msg.NpcVendorBuyfast)
                    ?? "Take a look at my goods.");
                return true;
            }
            if (lower.Contains("sell"))
            {
                var gc = FindGameClient(speaker);
                _log.LogDebug(
                    "[svc_kw] VENDOR_SELL speaker={Speaker} npc={Npc} client={HasClient}",
                    speaker.Name, npc.Name, gc != null);
                gc?.OpenVendorSell(npc);
                NpcSpeak(npc, SafeMsg(SphereNet.Game.Messages.Msg.NpcVendorSellfast)
                    ?? "Show me what you have to sell.");
                return true;
            }
        }

        if (brain == NpcBrainType.Banker)
        {
            int withdrawAmount = TryParseAmountAfter(lower, "withdraw");
            int checkAmount = TryParseAmountAfter(lower, "check");
            bool wantBank = lower.Contains("bank") || lower == "deposit"
                            || lower.StartsWith("deposit ");

            if (lower.Contains("balance"))
            {
                long banked = CountBankGold(speaker);
                NpcSpeak(npc, $"Thou hast {banked} gold piece(s) in our care.");
                return true;
            }
            if (withdrawAmount > 0)
            {
                long banked = CountBankGold(speaker);
                if (banked < withdrawAmount)
                {
                    NpcSpeak(npc, $"You have only {banked} gold piece(s) in our care.");
                    return true;
                }
                RemoveBankGold(speaker, withdrawAmount);
                DepositGoldToBackpack(speaker, withdrawAmount);
                NpcSpeak(npc, $"Here are thy {withdrawAmount} gold piece(s).");
                FindGameClient(speaker)?.OpenBankBox();
                return true;
            }
            if (checkAmount > 0)
            {
                long banked = CountBankGold(speaker);
                if (banked < checkAmount)
                {
                    NpcSpeak(npc, $"You have only {banked} gold piece(s) in our care.");
                    return true;
                }
                if (!DepositBankCheckToBackpack(speaker, checkAmount))
                {
                    NpcSpeak(npc, "I am unable to issue a check for that amount right now.");
                    return true;
                }
                RemoveBankGold(speaker, checkAmount);
                NpcSpeak(npc, $"Here is thy check for {checkAmount} gold piece(s).");
                return true;
            }
            if (wantBank)
            {
                _log.LogDebug("[svc_kw] BANK_OPEN speaker={Speaker} npc={Npc}",
                    speaker.Name, npc.Name);
                FindGameClient(speaker)?.OpenBankBox();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Service NPC keyword response. We don't yet have a dedicated NPC
    /// overhead-speech broadcast, so the line is delivered as a system
    /// </summary>
    private static void NpcSpeak(Character npc, string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var speechPacket = new PacketSpeechUnicodeOut(
            npc.Uid.Value,
            npc.BodyId,
            0x06,
            npc.SpeechColor != 0 ? npc.SpeechColor : (ushort)0x03B2,
            3,
            "TRK",
            npc.Name ?? "",
            line);
        BroadcastNearby(npc.Position, 14, speechPacket, 0);
    }

    /// <summary>
    /// Parse an integer amount that follows a keyword in a speech string,
    /// e.g. TryParseAmountAfter("withdraw 100", "withdraw") returns 100.
    /// Returns 0 when the keyword is missing, no amount follows, or the
    /// amount is non-positive. Tolerant of extra whitespace and trailing
    /// punctuation ("withdraw 100 gold").
    /// </summary>
    private static int TryParseAmountAfter(string text, string keyword)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) return 0;
        int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        int cur = idx + keyword.Length;
        while (cur < text.Length && !char.IsDigit(text[cur])) cur++;
        int start = cur;
        while (cur < text.Length && char.IsDigit(text[cur])) cur++;
        if (cur == start) return 0;
        if (!int.TryParse(text.AsSpan(start, cur - start), out int amount)) return 0;
        return amount > 0 ? amount : 0;
    }

    /// <summary>Total gold (item type Gold or 0x0EED) inside a character's bank box.</summary>
    private static long CountBankGold(Character ch)
    {
        var bank = ch.GetEquippedItem(SphereNet.Core.Enums.Layer.BankBox);
        if (bank == null) return 0;
        long total = 0;
        foreach (var item in _world.GetContainerContents(bank.Uid))
        {
            if (item.ItemType == SphereNet.Core.Enums.ItemType.Gold || item.BaseId == 0x0EED)
                total += item.Amount;
        }
        return total;
    }

    /// <summary>
    /// Withdraw N gold from a character's bank box. Walks gold piles from
    /// largest first (mirrors Source-X behaviour where the smallest number
    /// of stacks is consumed). Caller must check CountBankGold first.
    /// </summary>
    private static void RemoveBankGold(Character ch, int amount)
    {
        var bank = ch.GetEquippedItem(SphereNet.Core.Enums.Layer.BankBox);
        if (bank == null || amount <= 0) return;
        int remaining = amount;
        foreach (var item in _world.GetContainerContents(bank.Uid).ToList())
        {
            if (remaining <= 0) break;
            if (item.ItemType != SphereNet.Core.Enums.ItemType.Gold && item.BaseId != 0x0EED)
                continue;

            if (item.Amount <= remaining)
            {
                remaining -= item.Amount;
                item.Delete();
            }
            else
            {
                item.Amount -= (ushort)remaining;
                remaining = 0;
            }
        }
    }

    /// <summary>
    /// Drop a fresh gold pile into a character's backpack. Splits into 60k
    /// stacks (UO max amount per pile) so very large withdrawals still fit.
    /// </summary>
    private static void DepositGoldToBackpack(Character ch, int amount)
    {
        var pack = ch.Backpack;
        if (pack == null || amount <= 0) return;
        while (amount > 0)
        {
            ushort slice = (ushort)Math.Min(amount, 60000);
            var gold = _world.CreateItem();
            gold.BaseId = 0x0EED;
            gold.Name = "Gold";
            gold.ItemType = SphereNet.Core.Enums.ItemType.Gold;
            gold.Amount = slice;
            pack.AddItem(gold);
            amount -= slice;
        }
    }

    private static bool DepositBankCheckToBackpack(Character ch, int amount)
    {
        var pack = ch.Backpack;
        if (pack == null || amount <= 0)
            return false;

        var rid = _resources.ResolveDefName("i_bankcheck");
        if (!rid.IsValid || rid.Type != ResType.ItemDef)
            return false;

        var item = _world.CreateItem();
        var itemDef = DefinitionLoader.GetItemDef(rid.Index);
        ushort dispId = 0;
        if (itemDef != null)
        {
            if (itemDef.DispIndex != 0) dispId = itemDef.DispIndex;
            else if (itemDef.DupItemId != 0) dispId = itemDef.DupItemId;
        }
        if (dispId == 0 && rid.Index <= 0xFFFF)
            dispId = (ushort)rid.Index;
        if (dispId == 0)
            return false;

        item.BaseId = dispId;
        item.Name = string.IsNullOrWhiteSpace(itemDef?.Name) ? $"Bank check ({amount})" : itemDef!.Name;
        item.Price = amount;
        item.SetTag("BANKCHECK_AMOUNT", amount.ToString());
        pack.AddItem(item);
        return true;
    }

    /// <summary>
    /// Source-X CClient::Event_TalkBroadcast region keyword check. Fires exactly
    /// once per player utterance — currently handles "guards" / "help guards"
    /// inside REGION_FLAG_GUARDED zones. Future global keywords (e.g. "i resign
    /// from my guild" outside guild stones) hook in here too.
    /// </summary>
    private static void OnPlayerSpeech(Character speaker, string text, TalkMode mode)
    {
        if (string.IsNullOrEmpty(text)) return;
        string lower = text.ToLowerInvariant();
        bool calledGuards = lower.Contains("guards") || lower == "help" || lower.Contains("help guards");
        if (!calledGuards) return;

        var region = _world.FindRegion(speaker.Position);
        if (region == null || !region.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded))
        {
            _log.LogDebug("[guards] {Speaker} called guards but region={Region} guarded={Guarded} at {Pos}",
                speaker.Name, region?.Name ?? "(none)",
                region?.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded) ?? false,
                speaker.Position);
            return;
        }

        var hostiles = FindAllGuardTargets(speaker);

        var gc = FindGameClient(speaker);
        if (hostiles.Count == 0)
        {
            gc?.SysMessage("All looks quiet here.");
            return;
        }

        int killCount = 0;
        foreach (var hostile in hostiles)
        {
            if (hostile.IsDeleted || hostile.IsDead) continue;
            var summonedGuard = FindNearbyGuardResponder(speaker.Position) ?? SummonCityGuardNear(hostile, region.Name);
            if (summonedGuard != null && !summonedGuard.IsDeleted)
            {
                summonedGuard.FightTarget = hostile.Uid;
                if (summonedGuard.Position.GetDistanceTo(hostile.Position) > 1)
                {
                    _world.MoveCharacter(summonedGuard, hostile.Position);
                    BroadcastCharacterAppear(summonedGuard);
                }
            }
            if (_config.GuardsInstantKill)
            {
                BroadcastLightningStrike(hostile);
                _deathEngine.ProcessDeath(hostile, summonedGuard);
                if (summonedGuard != null)
                {
                    summonedGuard.FightTarget = Serial.Invalid;
                    summonedGuard.RemoveTag("GUARD_YELLED");
                }
                killCount++;
            }
        }

        gc?.SysMessage(killCount > 0 ? "Guards strike down your attacker." : "The guards have been called.");
    }

    private static List<Character> FindAllGuardTargets(Character speaker)
    {
        var results = new List<Character>();
        foreach (var ch in _world.GetCharsInRange(speaker.Position, 14))
        {
            if (ch == speaker || ch.IsDead || ch.IsDeleted) continue;
            if (ch.PrivLevel >= PrivLevel.Counsel) continue;
            if (ch.NpcBrain == NpcBrainType.Guard) continue;

            if (ch.IsPlayer)
            {
                bool isCriminal = ch.IsCriminal || ch.IsStatFlag(StatFlag.Criminal);
                bool isMurderer = _config.GuardsOnMurderers && ch.IsMurderer;
                if (isCriminal || isMurderer) results.Add(ch);
                continue;
            }
            if (ch.NpcMaster.IsValid) continue;

            bool hostileNpc = ch.NpcBrain is NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon;
            if (hostileNpc) results.Add(ch);
        }
        return results;
    }

    private static void BroadcastLightningStrike(Character target)
    {
        var effect = new PacketEffect(1, 0, target.Uid.Value, 0,
            target.X, target.Y, (short)(target.Z + 20),
            target.X, target.Y, target.Z,
            6, 15, true, false);
        var sound = new PacketSound(0x0029, target.X, target.Y, target.Z);
        BroadcastNearby(target.Position, 18, effect, 0);
        BroadcastNearby(target.Position, 18, sound, 0);
    }

    private static Character? FindGuardTarget(Character speaker)
    {
        Character? bestPlayer = null;
        int bestPlayerDist = int.MaxValue;
        Character? bestNpc = null;
        int bestNpcDist = int.MaxValue;
        int scanned = 0;

        foreach (var ch in _world.GetCharsInRange(speaker.Position, 14))
        {
            scanned++;
            if (ch == speaker || ch.IsDead || ch.IsDeleted)
            {
                _log.LogDebug("[guard_scan] skip 0x{Uid:X} '{Name}' reason={Reason}",
                    ch.Uid.Value, ch.Name ?? "?",
                    ch == speaker ? "self" : ch.IsDead ? "dead" : "deleted");
                continue;
            }
            if (ch.PrivLevel >= PrivLevel.Counsel)
            {
                _log.LogDebug("[guard_scan] skip 0x{Uid:X} '{Name}' reason=staff", ch.Uid.Value, ch.Name ?? "?");
                continue;
            }

            int dist = ch.Position.GetDistanceTo(speaker.Position);
            _log.LogDebug("[guard_scan] eval 0x{Uid:X} '{Name}' brain={Brain} isPlayer={IsPlayer} master={Master} dist={Dist}",
                ch.Uid.Value, ch.Name ?? "?", ch.NpcBrain, ch.IsPlayer, ch.NpcMaster.Value, dist);

            if (ch.IsPlayer)
            {
                bool isCriminal = ch.IsCriminal || ch.IsStatFlag(StatFlag.Criminal);
                bool isMurderer = _config.GuardsOnMurderers && ch.IsMurderer;
                if ((isCriminal || isMurderer) && dist < bestPlayerDist)
                {
                    bestPlayer = ch;
                    bestPlayerDist = dist;
                }
                continue;
            }

            if (ch.NpcMaster.IsValid)
            {
                var owner = _world.FindChar(ch.NpcMaster);
                if (owner != null && !owner.IsDeleted && !owner.IsDead && owner.IsPlayer &&
                    owner.PrivLevel < PrivLevel.Counsel)
                {
                    bool petAggressingSpeaker = ch.FightTarget == speaker.Uid;
                    if (!petAggressingSpeaker && ch.TryGetTag("ATTACK_TARGET", out string? attackUid) &&
                        uint.TryParse(attackUid, out uint auid))
                    {
                        petAggressingSpeaker = auid == speaker.Uid.Value;
                    }

                    bool ownerCriminal = owner.IsCriminal || owner.IsStatFlag(StatFlag.Criminal);
                    bool ownerMurderer = _config.GuardsOnMurderers && owner.IsMurderer;
                    if ((petAggressingSpeaker || ownerCriminal || ownerMurderer) && dist < bestPlayerDist)
                    {
                        bestPlayer = owner;
                        bestPlayerDist = dist;
                    }
                }
                continue;
            }
            if (ch.NpcBrain == NpcBrainType.Guard)
                continue;

            bool hostileNpc = ch.NpcBrain is NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon;
            if (hostileNpc && dist < bestNpcDist)
            {
                bestNpc = ch;
                bestNpcDist = dist;
            }
        }

        _log.LogDebug("[guard_scan] scanned={Scanned} bestPlayer={BP} bestNpc={BN}",
            scanned, bestPlayer?.Name ?? "(none)", bestNpc?.Name ?? "(none)");
        return bestPlayer ?? bestNpc;
    }

    private static Character ResolveEffectiveOffender(Character offender)
    {
        if (offender.NpcMaster.IsValid)
        {
            var owner = _world.FindChar(offender.NpcMaster);
            if (owner != null && !owner.IsDeleted)
                return owner;
        }
        return offender;
    }

    private static Character? FindNearbyGuardResponder(Point3D center)
    {
        Character? nearest = null;
        int bestDist = int.MaxValue;
        foreach (var ch in _world.GetCharsInRange(center, 18))
        {
            if (ch.IsDeleted || ch.IsDead || ch.IsPlayer)
                continue;
            if (ch.NpcBrain != NpcBrainType.Guard)
                continue;
            int dist = ch.Position.GetDistanceTo(center);
            if (dist < bestDist)
            {
                nearest = ch;
                bestDist = dist;
            }
        }
        return nearest;
    }

    private static Character? SummonCityGuardNear(Character hostile, string regionName)
    {
        if (hostile.IsDeleted)
            return null;

        string defName = Random.Shared.Next(2) == 0 ? "C_GUARD" : "C_GUARD_F";
        var rid = _resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != ResType.CharDef)
        {
            rid = _resources.ResolveDefName("C_GUARD");
            if (!rid.IsValid || rid.Type != ResType.CharDef)
            {
                _log.LogWarning("[guard] CHARDEF C_GUARD / C_GUARD_F not found in scripts, cannot summon guard");
                return null;
            }
        }

        var charDef = DefinitionLoader.GetCharDef(rid.Index);
        if (charDef == null)
        {
            _log.LogWarning("[guard] CharDef index 0x{Index:X} resolved but definition missing", rid.Index);
            return null;
        }

        var guard = _world.CreateCharacter();
        guard.IsPlayer = false;
        guard.CharDefIndex = rid.Index;

        ushort bodyId = charDef.DispIndex;
        if (bodyId == 0 && !string.IsNullOrWhiteSpace(charDef.DisplayIdRef))
        {
            var bodyRid = _resources.ResolveDefName(charDef.DisplayIdRef.Trim());
            if (bodyRid.IsValid)
            {
                var refDef = DefinitionLoader.GetCharDef(bodyRid.Index);
                if (refDef?.DispIndex > 0)
                    bodyId = refDef.DispIndex;
                else if (bodyRid.Index >= 0 && bodyRid.Index <= ushort.MaxValue)
                    bodyId = (ushort)bodyRid.Index;
            }
        }
        if (bodyId == 0) bodyId = 0x0190;
        guard.BodyId = bodyId;
        guard.BaseId = bodyId;

        if (!string.IsNullOrWhiteSpace(charDef.Name))
            guard.Name = DefinitionLoader.ResolveNames(charDef.Name);
        else
            guard.Name = string.IsNullOrWhiteSpace(regionName) ? "city guard" : $"{regionName} guard";

        int strVal = charDef.StrMax > 0 ? charDef.StrMax : Math.Max(1, charDef.StrMin);
        int dexVal = charDef.DexMax > 0 ? charDef.DexMax : Math.Max(1, charDef.DexMin);
        int intVal = charDef.IntMax > 0 ? charDef.IntMax : Math.Max(1, charDef.IntMin);
        guard.Str = (short)Math.Clamp(strVal, 1, short.MaxValue);
        guard.Dex = (short)Math.Clamp(dexVal, 1, short.MaxValue);
        guard.Int = (short)Math.Clamp(intVal, 1, short.MaxValue);
        int hits = charDef.HitsMax > 0 ? charDef.HitsMax : Math.Max(1, strVal);
        guard.MaxHits = (short)Math.Clamp(hits, 1, short.MaxValue);
        guard.Hits = guard.MaxHits;
        guard.MaxStam = guard.Dex;
        guard.Stam = guard.Dex;
        guard.MaxMana = guard.Int;
        guard.Mana = guard.Int;

        if (charDef.NpcBrain != NpcBrainType.None)
            guard.NpcBrain = charDef.NpcBrain;
        else
            guard.NpcBrain = NpcBrainType.Guard;

        guard.SetStatFlag(StatFlag.Invul);
        guard.SetTag("IS_CITY_GUARD", "1");
        guard.SetTag("GUARD_SPAWNED_AT", Environment.TickCount64.ToString());
        guard.FightTarget = hostile.Uid;

        _world.PlaceCharacter(guard, hostile.Position);

        _triggerDispatcher?.FireCharTrigger(guard, CharTrigger.Create, new TriggerArgs { CharSrc = guard });

        if (guard.NpcBrain == NpcBrainType.None)
            guard.NpcBrain = NpcBrainType.Guard;

        EquipGuardNewbieItems(guard, charDef);

        long lingerMs = Math.Max(1, _config.GuardLinger) * 1000L;
        long expireAt = Environment.TickCount64 + lingerMs;
        guard.SetTag("GUARD_EXPIRE_AT", expireAt.ToString());
        _summonedGuardExpiry[guard.Uid] = expireAt;

        BroadcastCharacterAppear(guard);
        return guard;
    }

    private static void EquipGuardNewbieItems(Character guard, CharDef charDef)
    {
        foreach (var entry in charDef.NewbieItems)
        {
            string defName = entry.DefName?.Trim() ?? "";
            if (defName.Length == 0) continue;

            var rid = _resources.ResolveDefName(defName);
            if (!rid.IsValid || rid.Type != ResType.ItemDef)
                continue;

            var itemDef = DefinitionLoader.GetItemDef(rid.Index);
            ushort dispId = 0;
            if (itemDef != null)
            {
                if (itemDef.DispIndex != 0) dispId = itemDef.DispIndex;
                else if (itemDef.DupItemId != 0) dispId = itemDef.DupItemId;
            }
            if (dispId == 0 && rid.Index <= 0xFFFF) dispId = (ushort)rid.Index;
            if (dispId == 0) continue;

            var item = _world.CreateItem();
            item.BaseId = dispId;
            if (itemDef != null && !string.IsNullOrWhiteSpace(itemDef.Name))
                item.Name = itemDef.Name;

            if (!string.IsNullOrWhiteSpace(entry.Color))
            {
                string cv = entry.Color!.Trim();
                var colorRid = _resources.ResolveDefName(cv);
                if (colorRid.IsValid)
                    item.Hue = new Core.Types.Color((ushort)colorRid.Index);
                else if (cv.StartsWith("0", StringComparison.Ordinal) &&
                         ushort.TryParse(cv, System.Globalization.NumberStyles.HexNumber, null, out ushort hue))
                    item.Hue = new Core.Types.Color(hue);
            }

            Layer layer = itemDef?.Layer ?? Layer.None;
            if (layer == Layer.None && _world.MapData != null)
            {
                var tile = _world.MapData.GetItemTileData(item.BaseId);
                if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Wearable) != 0 &&
                    tile.Quality > 0 && tile.Quality <= (byte)Layer.Horse)
                    layer = (Layer)tile.Quality;
            }

            if (layer != Layer.None)
                guard.Equip(item, layer);
        }
    }

    private static void OnNpcHearSpeech(Character speaker, Character npc, string text, TalkMode mode)
    {
        string lower = text.ToLowerInvariant();
        _log.LogDebug(
            "[npc_hear] {Speaker} -> {Npc} brain={Brain} text='{Text}'",
            speaker.Name, npc.Name, npc.NpcBrain, text);

        // Source-X global speech function hook — silent when missing.
        // Many imported script packs don't define this; warning on every
        // spoken line would drown the log.
        _triggerDispatcher?.Runner?.TryRunFunction(
            "f_onchar_speech",
            npc,
            null,
            new SphereNet.Scripting.Execution.TriggerArgs(speaker, (int)mode, 0, text)
            {
                Object1 = npc,
                Object2 = speaker
            },
            out _);

        // Fire trigger first — let scripts handle custom keywords
        var trigResult = _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCHearGreeting,
            new TriggerArgs { CharSrc = speaker, S1 = text });
        if (trigResult == TriggerResult.True)
        {
            _log.LogDebug("[npc_hear] {Npc} @NPCHearGreeting consumed text='{Text}'", npc.Name, text);
            return;
        }

        // Service-NPC well-known keywords (buy/sell/bank/balance/withdraw/
        // heal/stable/...) are handled by the built-in dispatcher BEFORE
        // the SPEECH script chain. Imported sphere packs ship TSPEECH=spk_jobSHOPKEEP
        // / spk_jobBANKER bodies whose verbs (actserv.dialog, ...) aren't
        // fully wired in our interpreter yet — letting the script "handle"
        // those keywords would silently swallow the request and the
        // vendor / bank window would never open. Pre-empting them here
        // keeps service NPCs functional until SPEECH bodies execute end
        // to end. Other speech (greetings, custom keywords) still flows
        // through FireSpeechTrigger below.
        if (TryDispatchServiceKeyword(speaker, npc, text))
            return;

        // Script-driven SPEECH triggers (from CHARDEF SPEECH/TSPEECH)
        var speechResult = _triggerDispatcher?.FireSpeechTrigger(npc, speaker, text);
        if (speechResult == TriggerResult.True)
        {
            _log.LogDebug("[npc_hear] {Npc} SPEECH trigger consumed text='{Text}'", npc.Name, text);
            return;
        }

        // Built-in keyword responses. Legacy Sphere saves commonly set
        // NPC=NPC_HUMAN on bankers/vendors/healers/stablemasters and
        // defer the real behaviour to a TSPEECH script block. When that
        // block isn't present on the shard, the service NPC becomes
        // mute. We widen the brain match so a Human-brain NPC whose
        // name contains the role keyword ("banker", "vendor"...) still
        // responds. InferredRole below collapses brain + name into a
        // single dispatch key.
        string? response = null;
        string lowerName = (npc.Name ?? "").ToLowerInvariant();
        NpcBrainType inferredBrain = npc.NpcBrain;
        if (inferredBrain is NpcBrainType.Human or NpcBrainType.None)
        {
            if (lowerName.Contains("banker")) inferredBrain = NpcBrainType.Banker;
            else if (lowerName.Contains("healer")) inferredBrain = NpcBrainType.Healer;
            else if (lowerName.Contains("stable") || lowerName.Contains("stablemaster"))
                inferredBrain = NpcBrainType.Stable;
            else if (lowerName.Contains("guard")) inferredBrain = NpcBrainType.Guard;
            else if (lowerName.Contains("vendor") || lowerName.Contains("shopkeep") ||
                     lowerName.Contains("merchant")) inferredBrain = NpcBrainType.Vendor;
        }

        switch (inferredBrain)
        {
            case NpcBrainType.Vendor:
                if (lower.Contains("buy") || lower.Contains("vendor buy") || lower.Contains("purchase"))
                {
                    // Source-X CClient::Event_TalkBroadcast → Cmd_VendorBuy:
                    // open the vendor buy window on the speaker's client.
                    var gc = FindGameClient(speaker);
                    _log.LogDebug(
                        "[vendor_speech] BUY speaker={Speaker} npc={Npc} brain={Brain} client={HasClient}",
                        speaker.Name, npc.Name, npc.NpcBrain, gc != null);
                    if (gc != null)
                        gc.OpenVendorBuy(npc);
                    response = SafeMsg(SphereNet.Game.Messages.Msg.NpcVendorBuyfast);
                    if (string.IsNullOrEmpty(response))
                        response = "Take a look at my goods.";
                }
                else if (lower.Contains("sell") || lower.Contains("vendor sell"))
                {
                    var gc = FindGameClient(speaker);
                    _log.LogDebug(
                        "[vendor_speech] SELL speaker={Speaker} npc={Npc} brain={Brain} client={HasClient}",
                        speaker.Name, npc.Name, npc.NpcBrain, gc != null);
                    if (gc != null)
                        gc.OpenVendorSell(npc);
                    response = SafeMsg(SphereNet.Game.Messages.Msg.NpcVendorSellfast);
                    if (string.IsNullOrEmpty(response))
                        response = "Show me what you have to sell.";
                }
                break;

            case NpcBrainType.Banker:
                {
                    // Source-X CCharNPC::OnTriggerSpeech banker brain handles
                    // a small set of keywords:
                    //   bank / deposit  -> open the bank box
                    //   balance         -> report the gold currently banked
                    //   withdraw N      -> move N gold from the bank into the
                    //                      speaker's backpack
                    //   check N         -> issue a bank check into the
                    //                      speaker's backpack
                    var gc = FindGameClient(speaker);
                    int withdrawAmount = TryParseAmountAfter(lower, "withdraw");
                    int checkAmount = TryParseAmountAfter(lower, "check");
                    bool wantBank = lower.Contains("bank") || lower == "deposit" || lower.StartsWith("deposit ");

                    if (lower.Contains("balance"))
                    {
                        long banked = CountBankGold(speaker);
                        response = $"Thou hast {banked} gold piece(s) in our care.";
                    }
                    else if (withdrawAmount > 0)
                    {
                        long banked = CountBankGold(speaker);
                        if (banked < withdrawAmount)
                            response = $"You have only {banked} gold piece(s) in our care.";
                        else
                        {
                            RemoveBankGold(speaker, withdrawAmount);
                            DepositGoldToBackpack(speaker, withdrawAmount);
                            response = $"Here are thy {withdrawAmount} gold piece(s).";
                            // Ensure the backpack-side delta is visible immediately.
                            gc?.OpenBankBox();
                        }
                    }
                    else if (checkAmount > 0)
                    {
                        long banked = CountBankGold(speaker);
                        if (banked < checkAmount)
                            response = $"You have only {banked} gold piece(s) in our care.";
                        else if (!DepositBankCheckToBackpack(speaker, checkAmount))
                            response = "I am unable to issue a check for that amount right now.";
                        else
                        {
                            RemoveBankGold(speaker, checkAmount);
                            response = $"Here is thy check for {checkAmount} gold piece(s).";
                            gc?.OpenBankBox();
                        }
                    }
                    else if (wantBank)
                    {
                        gc?.OpenBankBox();
                        response = "Here is your bank box.";
                    }
                }
                break;

            case NpcBrainType.Healer:
                if (lower.Contains("heal") || lower.Contains("resurrect") || lower.Contains("cure"))
                {
                    // Check if speaker is dead → resurrect
                    if (speaker.IsDead)
                    {
                        response = "Let me help you return to the living.";
                        foreach (var c in _clients.Values)
                        {
                            if (c.Character == speaker)
                            {
                                c.OnResurrect();
                                break;
                            }
                        }
                    }
                    else if (speaker.Hits < speaker.MaxHits)
                    {
                        speaker.Hits = speaker.MaxHits;
                        response = "You look much better now.";
                    }
                    else
                    {
                        response = "You look healthy to me.";
                    }
                }
                break;

            case NpcBrainType.Guard:
                if (lower.Contains("help") || lower.Contains("guards"))
                    response = "I shall protect this area.";
                break;

            case NpcBrainType.Stable:
                if (lower.Contains("stable"))
                {
                    // Find a pet near the player
                    Character? pet = null;
                    foreach (var ch in _world.GetCharsInRange(speaker.Position, 8))
                    {
                        if (!ch.IsPlayer && !ch.IsDead && ch.NpcMaster == speaker.Uid)
                        {
                            pet = ch;
                            break;
                        }
                    }
                    if (pet != null && _stableEngine.StablePet(speaker, pet, _world))
                        response = $"Your pet {pet.Name} has been stabled.";
                    else
                        response = "I don't see any of your pets nearby.";
                }
                else if (lower.Contains("claim"))
                {
                    var claimed = _stableEngine.ClaimPet(speaker, 0, _world, speaker.Position);
                    if (claimed != null)
                        response = $"Here is your pet {claimed.Name}.";
                    else
                        response = "You have no stabled pets.";
                }
                else
                {
                    int count = _stableEngine.GetStabledCount(speaker);
                    response = count > 0
                        ? $"You have {count} pet(s) stabled. Say 'claim' to retrieve one."
                        : "I can stable your pets for you. Just say 'stable'.";
                }
                break;
        }

        // Fallback: fire @NPCHearUnknown if no built-in response
        if (response == null)
        {
            _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCHearUnknown,
                new TriggerArgs { CharSrc = speaker, S1 = text });
            return;
        }

        // Send NPC speech response to nearby clients
        var speechPacket = new PacketSpeechUnicodeOut(
            npc.Uid.Value, npc.BodyId, 0, 0x03B2, 3, "TRK", npc.Name ?? "", response);
        BroadcastNearby(npc.Position, 18, speechPacket, 0);
    }

    private static void RunServerTick()
    {
        _tickCounter++;
        long tickStart = Stopwatch.GetTimestamp();
        try
        {
            if (_multicoreRuntimeEnabled)
                RunMulticoreTick();
            else
                RunSingleThreadTick();
        }
        catch (OperationCanceledException oce)
        {
            _log.LogWarning(oce, "Multicore tick timeout. Falling back to single-thread mode.");
            _multicoreRuntimeEnabled = false;
            RunSingleThreadTick();
        }
        catch (Exception ex)
        {
            if (_multicoreRuntimeEnabled)
            {
                _log.LogWarning(ex, "Multicore tick failure. Falling back to single-thread mode.");
                _multicoreRuntimeEnabled = false;
                RunSingleThreadTick();
            }
            else
            {
                throw;
            }
        }
        finally
        {
            long totalUs = ToMicroseconds(Stopwatch.GetTimestamp() - tickStart);
            if (totalUs > _telemetryMaxTickUs)
                _telemetryMaxTickUs = totalUs;

            // Slow-tick detector: anything over 25ms will show up as ping jitter
            // on a 100ms tick budget. Log per-phase breakdown so the cause is
            // visible without running a profiler. Throttled to max 1 per 10 seconds
            // to avoid flooding console during stress tests.
            long nowMs = Environment.TickCount64;
            if (totalUs > 25_000 && nowMs - _lastSlowTickWarningMs > 10_000)
            {
                _lastSlowTickWarningMs = nowMs;
                _log.LogWarning(
                    "[slow_tick] mode={Mode} tick={Tick} total={TotalMs}ms snapshot={SnapshotMs}ms compute={ComputeMs}ms apply={ApplyMs}ms flush={FlushMs}ms",
                    _multicoreRuntimeEnabled ? "multicore" : "single",
                    _tickCounter,
                    (totalUs / 1000.0).ToString("F1"),
                    (_telemetrySnapshotUs / 1000.0).ToString("F1"),
                    (_telemetryComputeUs / 1000.0).ToString("F1"),
                    (_telemetryApplyUs / 1000.0).ToString("F1"),
                    (_telemetryFlushUs / 1000.0).ToString("F1"));
            }

            // Periodic tick stats: log average and max tick time every 30 seconds
            _tickStatsTotalUs += totalUs;
            if (totalUs > _tickStatsMaxUs) _tickStatsMaxUs = totalUs;
            _tickStatsCount++;

            if (nowMs - _lastTickStatsLogMs >= 30_000)
            {
                double avgMs = _tickStatsCount > 0 ? (_tickStatsTotalUs / _tickStatsCount / 1000.0) : 0;
                double maxMs = _tickStatsMaxUs / 1000.0;
                int onlinePlayers = _clients.Values.Count(c => c.IsPlaying);
                var (chars, items, _) = _world.GetStats();

                // Include bot stats if bots are active
                if (_botEngine != null && _botEngine.TotalBots > 0)
                {
                    var botStats = _botEngine.GetStats();
                    _log.LogInformation(
                        "[tick_stats] ticks={Count} avg={AvgMs:F1}ms max={MaxMs:F1}ms players={Players} chars={Chars} items={Items} bots={Bots}/{BotTotal} pps_in={PpsIn:F0} pps_out={PpsOut:F0}",
                        _tickStatsCount, avgMs, maxMs, onlinePlayers, chars, items,
                        botStats.ActiveBots, botStats.TotalBots, botStats.PacketsPerSecIn, botStats.PacketsPerSecOut);
                }
                else
                {
                    _log.LogInformation(
                        "[tick_stats] ticks={Count} avg={AvgMs:F1}ms max={MaxMs:F1}ms players={Players} chars={Chars} items={Items}",
                        _tickStatsCount, avgMs, maxMs, onlinePlayers, chars, items);
                }

                _tickStatsTotalUs = 0;
                _tickStatsMaxUs = 0;
                _tickStatsCount = 0;
                _lastTickStatsLogMs = nowMs;
            }
        }
    }

    private static void RunSingleThreadTick()
    {
        long p0 = Stopwatch.GetTimestamp();

        _world.OnTick();
        _spellEngine.ProcessExpirations(Environment.TickCount64);

        // NPC AI via timer wheel
        {
            long now = Environment.TickCount64;
            var dueNpcs = _npcTimerWheel.Advance(now);
            foreach (var npc in dueNpcs)
            {
                _npcAI.OnTickAction(npc);
                _npcTimerWheel.Schedule(npc, npc.NextNpcActionTime);
            }
        }

        _telemetrySnapshotUs = ToMicroseconds(Stopwatch.GetTimestamp() - p0);
        _telemetryComputeUs = 0;

        long p1 = Stopwatch.GetTimestamp();

        foreach (var client in _clients.Values)
        {
            client.TickClientState();
            client.UpdateClientView();
        }

        // Replay engine: send recorded packets to spectating clients
        if (_recordingEngine.HasActiveReplays)
        {
            TickRecordingReplays();
        }

        // Reset dirty flags so objects can be re-notified on next change
        _world.ConsumeDirtyObjects();

        _telemetryApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p2 = Stopwatch.GetTimestamp();
        RunPostTickMaintenance();
        _telemetryFlushUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2);

        MaybeRunDeterminismGuardrail();
    }

    // Below this many work items, Parallel.ForEach is pure overhead
    // (partitioner + worker ramp-up + lambda capture cost more than
    // the actual loop body). Empirically a single 1-vCPU VDS handles
    // 4 ticks/sec at ~5 ms when this threshold short-circuits the
    // 1-client / 0-NPC steady state, vs 50–300 ms when forced through
    // ParallelForEach. Mirrors GameWorld.OnTickParallel sector cutoff.
    private const int ParallelComputeMinBatch = 16;

    private static void RunMulticoreTick()
    {
        int workerCount = _config.MulticoreWorkerCount > 0 ? _config.MulticoreWorkerCount : Environment.ProcessorCount;
        int timeoutMs = Math.Max(100, _config.MulticorePhaseTimeoutMs);
        using var cts = new CancellationTokenSource(timeoutMs);

        long p0 = Stopwatch.GetTimestamp();
        _world.OnTickParallel(workerCount, cts.Token);
        _spellEngine.ProcessExpirations(Environment.TickCount64);

        // NPC AI via timer wheel
        var npcSnapshot = _npcTimerWheel.Advance(Environment.TickCount64);

        _reusableClientSnapshot.Clear();
        foreach (var c in _clients.Values)
        {
            if (c.IsPlaying)
                _reusableClientSnapshot.Add(c);
        }
        var clientSnapshot = _reusableClientSnapshot;
        _telemetrySnapshotUs = ToMicroseconds(Stopwatch.GetTimestamp() - p0);

        long p1 = Stopwatch.GetTimestamp();
        long nowTick = Environment.TickCount64;

        // Reuse buffers — both lists are cleared at end of tick. Avoids
        // ConcurrentBag/ConcurrentDictionary allocation churn that was
        // visible as slow_tick spikes on light loads.
        var decisionList = _reusableDecisionList;
        decisionList.Clear();
        if (npcSnapshot.Count >= ParallelComputeMinBatch)
        {
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cts.Token
            };
            var bag = new ConcurrentBag<NpcAI.NpcDecision>();
            Parallel.ForEach(npcSnapshot, po, npc =>
            {
                var decision = _npcAI.BuildDecision(npc, nowTick);
                if (decision.HasValue)
                    bag.Add(decision.Value);
            });
            decisionList.AddRange(bag);
        }
        else
        {
            foreach (var npc in npcSnapshot)
            {
                var decision = _npcAI.BuildDecision(npc, nowTick);
                if (decision.HasValue)
                    decisionList.Add(decision.Value);
            }
        }

        foreach (var client in clientSnapshot)
            client.TickClientState();

        // Apply NPC decisions BEFORE building view deltas so that other
        // players see up-to-date NPC positions in the same tick. Previously
        // BuildViewDelta ran first, causing a one-tick position lag.
        // Determinism: NPCs apply in UID order. Sort in-place — the
        // List<T>.Sort overload with Comparison<T> uses introsort and
        // doesn't allocate (the lambda is cached by the JIT here).
        decisionList.Sort(static (a, b) => a.NpcUid.CompareTo(b.NpcUid));
        foreach (var decision in decisionList)
            _npcAI.ApplyDecision(decision);
        _npcAI.PurgeStalePaths();

        // View delta build: parallel only when there's enough work to
        // amortize the partitioner setup. Single-client steady state
        // (~95% of real shards) hits the sequential fast path.
        // Now runs AFTER ApplyDecision so NPC positions are current.
        var clientDeltas = _reusableClientDeltas;
        clientDeltas.Clear();
        if (clientSnapshot.Count >= ParallelComputeMinBatch)
        {
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cts.Token
            };
            var concurrent = new ConcurrentDictionary<int, GameClient.ClientViewDelta>();
            Parallel.ForEach(clientSnapshot, po, client =>
            {
                var delta = client.BuildViewDelta();
                if (delta != null)
                    concurrent[client.NetState.Id] = delta;
            });
            foreach (var kv in concurrent)
                clientDeltas[kv.Key] = kv.Value;
        }
        else
        {
            foreach (var client in clientSnapshot)
            {
                var delta = client.BuildViewDelta();
                if (delta != null)
                    clientDeltas[client.NetState.Id] = delta;
            }
        }
        _telemetryComputeUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p2 = Stopwatch.GetTimestamp();
        foreach (var client in clientSnapshot)
        {
            if (clientDeltas.TryGetValue(client.NetState.Id, out var delta))
                client.ApplyViewDelta(delta);
        }
        _telemetryApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2);

        if (_recordingEngine.HasActiveReplays)
        {
            TickRecordingReplays();
        }

        // Reset dirty flags
        _world.ConsumeDirtyObjects();

        // Re-schedule NPCs into timer wheel after decisions applied
        if (_npcTimerWheel != null)
        {
            foreach (var npc in npcSnapshot)
            {
                if (!npc.IsDeleted && !npc.IsPlayer)
                    _npcTimerWheel.Schedule(npc, npc.NextNpcActionTime);
            }
        }

        long p3 = Stopwatch.GetTimestamp();
        RunPostTickMaintenance();
        _telemetryFlushUs = ToMicroseconds(Stopwatch.GetTimestamp() - p3);

        MaybeRunDeterminismGuardrail();
    }

    private static void WakeNpc(Character npc)
    {
        if (npc.IsPlayer || npc.IsDeleted || npc.IsDead) return;
        npc.NextNpcActionTime = 0;
        _npcTimerWheel?.Remove(npc);
        _npcTimerWheel?.Schedule(npc, Environment.TickCount64 + 100);
    }

    private static void CleanupSummonedGuards(long now)
    {
        if (_summonedGuardExpiry.Count == 0)
            return;

        foreach (var (uid, expireAt) in _summonedGuardExpiry.ToArray())
        {
            var guard = _world.FindChar(uid);
            if (guard == null || guard.IsDeleted || guard.IsDead)
            {
                _summonedGuardExpiry.Remove(uid);
                continue;
            }

            if (now < expireAt)
                continue;

            var removePacket = new PacketDeleteObject(uid.Value);
            BroadcastNearby(guard.Position, 18, removePacket, 0);
            _world.DeleteObject(guard);
            guard.Delete();
            _summonedGuardExpiry.Remove(uid);
        }
    }

    private static void RunDecayCatchup(long now)
    {
        if (now < _nextDecayCatchupTick)
            return;
        _nextDecayCatchupTick = now + 5000;

        // Sector sleep skips far-away sectors. This catch-up sweep ensures
        // decaying ground items/corpses still expire on time even when
        // nobody is nearby.
        var expired = _world.GetAllObjects()
            .OfType<Item>()
            .Where(it => !it.IsDeleted && it.IsOnGround && it.DecayTime > 0 && it.DecayTime <= now)
            .Take(256)
            .ToList();

        foreach (var item in expired)
        {
            // Drive the normal item decay path first (corpse spill, spawn cleanup).
            _ = item.OnTick();
            if (!item.IsDeleted)
                continue;

            var removePacket = new PacketDeleteObject(item.Uid.Value);
            BroadcastNearby(item.Position, 18, removePacket, 0);
            _world.DeleteObject(item);
            item.Delete();
        }
    }

    private static void RunPostTickMaintenance()
    {
        long now = Environment.TickCount64;
        CleanupSummonedGuards(now);
        RunDecayCatchup(now);

        byte newLight = _world.GlobalLight;
        if (newLight != _lastGlobalLight)
        {
            _lastGlobalLight = newLight;
            var lightPacket = new PacketGlobalLight(newLight);
            foreach (var client in _clients.Values)
            {
                if (client.IsPlaying)
                    client.Send(lightPacket);
            }
        }

        // Weather & season update
        bool seasonChanged = _weatherEngine.OnTick();
        if (seasonChanged)
            BroadcastSeasonChange(playSound: true);

        // Ship movement ticks
        _shipEngine?.OnTickAll();

        // House decay (check every ~60 ticks to avoid per-tick cost)
        if (_world.TickCount % 60 == 0 && _housingEngine != null)
        {
            var collapsed = _housingEngine.OnTickDecay();
            foreach (var house in collapsed)
                _log.LogInformation("House 0x{Uid:X} collapsed from decay", house.MultiItem.Uid.Value);
        }

        ProcessIdleTimeout();
        _telnet?.Tick();
        _webStatus?.Tick();
    }

    private static void ProcessIdleTimeout()
    {
        long idleThresholdMs = _config.NetTTL * 1000L;
        if (idleThresholdMs <= 0)
            return;

        long tickNow = Environment.TickCount64;
        foreach (var state in _network.GetActiveStates())
        {
            if (state.LastActivityTick > 0 &&
                tickNow - state.LastActivityTick > idleThresholdMs)
            {
                _log.LogInformation("Idle timeout for connection #{Id} ({Account})",
                    state.Id, state.AccountName);
                state.MarkClosing();
            }
        }
    }

    private static void MaybeRunDeterminismGuardrail()
    {
        if (!_config.MulticoreDeterminismDebug || _tickCounter > 2000)
            return;

        string hash = ComputeDeterminismHash();
        if (_tickCounter == 2000)
        {
            _log.LogInformation("[determinism] hash at tick {Tick}: {Hash}", _tickCounter, hash);

            if (!string.IsNullOrWhiteSpace(_config.MulticoreDeterminismExpectedHash) &&
                !string.Equals(hash, _config.MulticoreDeterminismExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogError(
                    "[determinism] hash mismatch! expected={Expected} actual={Actual}",
                    _config.MulticoreDeterminismExpectedHash,
                    hash);
            }
        }
    }

    private static string ComputeDeterminismHash()
    {
        var sb = new StringBuilder();
        sb.Append("tick:").Append(_tickCounter).Append('\n');
        sb.Append("world:").Append(_world.ComputeStateHash()).Append('\n');
        foreach (var client in _clients.Values.OrderBy(c => c.NetState.Id))
        {
            sb.Append(client.NetState.Id).Append(':');
            if (client.Character != null)
                sb.Append(client.Character.Uid.Value).Append('@').Append(client.Character.X).Append(',').Append(client.Character.Y).Append(',').Append(client.Character.Z);
            sb.Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private static long ToMicroseconds(long stopwatchTicks)
    {
        return (long)(stopwatchTicks * (1_000_000.0 / Stopwatch.Frequency));
    }

    /// <summary>
    /// Parse the [SPHERE] LogFileLevel knob into either a single-value
    /// minimum threshold OR a discrete whitelist set.
    ///   "Warning"                            → minLevel=Warning, whitelist=null
    ///   "Verbose | Warning | Error | Fatal"  → minLevel=Verbose,
    ///                                          whitelist={Verbose, Warning,
    ///                                                     Error, Fatal}
    /// Unknown / empty input falls back to Warning-threshold.  When a
    /// whitelist is returned the caller must wire a Serilog
    /// <c>Filter.ByIncludingOnly</c> against it; the min-level is set to
    /// the lowest whitelisted entry so the sink doesn't pre-drop those
    /// events upstream.
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

    /// <summary>Load ROOMDEF sections from script resources into GameWorld.</summary>
    private static void LoadRegionDefs()
    {
        int count = 0;
        foreach (var link in _resources.GetAllResources())
        {
            if (link.Id.Type != ResType.Area) continue;

            var region = new SphereNet.Game.World.Regions.Region
            {
                ResourceId = link.Id,
                Name = link.DefName ?? link.Id.ToString(),
                DefName = link.DefName
            };

            var keys = link.StoredKeys;
            if (keys == null)
            {
                // CRITICAL: ReadAllSections() walks from the section's start
                // line to EOF and returns *every* section in between. We must
                // consume only the first one — the resource link points at
                // exactly one [AREADEF/ROOMDEF a_xxx] block. The previous code
                // merged every following section's keys into this definition,
                // so an early AREADEF inherited the RECTs of every later one
                // in the file (observed: a single "Minax Stronghold" reported
                // with 36/37/40/41/42 rects depending on file position,
                // swallowing Britain because the cumulative rect set covered
                // the city coordinates).
                using var sf = link.OpenAtStoredPosition();
                if (sf == null) continue;
                var sections = sf.ReadAllSections();
                if (sections.Count == 0) continue;
                keys = sections[0].Keys;
            }

            foreach (var key in keys)
            {
                var upper = key.Key.ToUpperInvariant();
                switch (upper)
                {
                    case "NAME":
                        region.Name = key.Arg;
                        break;
                    case "P":
                        var pp = key.Arg.Split(',');
                        if (pp.Length >= 3 &&
                            short.TryParse(pp[0].Trim(), out short px) &&
                            short.TryParse(pp[1].Trim(), out short py) &&
                            sbyte.TryParse(pp[2].Trim(), out sbyte pz))
                        {
                            byte pm = pp.Length > 3 && byte.TryParse(pp[3].Trim(), out byte pmap) ? pmap : (byte)0;
                            region.P = new SphereNet.Core.Types.Point3D(px, py, pz, pm);
                            // Source-X CRegionWorld treats P's 4th component as the
                            // map index; AREADEFs almost never carry an explicit MAP
                            // line, so without this fallback every region defaulted
                            // to map 0 and AREADEFs from Malas/Tokuno/Ilshenar leaked
                            // into Felucca/Trammel lookups (e.g. Hanse's Hostel
                            // shadowing Britain because both ended up on map 0).
                            if (pm != 0)
                                region.MapIndex = pm;
                        }
                        break;
                    case "MAP":
                        if (byte.TryParse(key.Arg, out byte mapIdx))
                            region.MapIndex = mapIdx;
                        break;
                    case "RECT":
                        var parts = key.Arg.Split(',');
                        if (parts.Length >= 4 &&
                            short.TryParse(parts[0].Trim(), out short x1) &&
                            short.TryParse(parts[1].Trim(), out short y1) &&
                            short.TryParse(parts[2].Trim(), out short x2) &&
                            short.TryParse(parts[3].Trim(), out short y2))
                        {
                            region.AddRect(x1, y1, x2, y2);
                            // Source-X RECT syntax: x1,y1,x2,y2[,m]. The optional
                            // 5th value is the rect's map and also pins the region's
                            // map when no MAP/P key was provided.
                            if (parts.Length >= 5 &&
                                byte.TryParse(parts[4].Trim(), out byte rectMap) &&
                                rectMap != 0 && region.MapIndex == 0)
                            {
                                region.MapIndex = rectMap;
                            }
                        }
                        break;
                    case "FLAGS":
                        region.Flags = ParseRegionFlags(key.Arg);
                        break;
                    case "GROUP":
                        region.Group = key.Arg;
                        break;
                    case "EVENTS":
                        if (!string.IsNullOrEmpty(key.Arg))
                        {
                            foreach (var ev in key.Arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                region.AddEvent(ResourceId.FromString(ev, ResType.Events));
                        }
                        break;
                    case "RESOURCES":
                        if (!string.IsNullOrEmpty(key.Arg))
                        {
                            foreach (var res in key.Arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                region.AddRegionType(ResourceId.FromString(res, ResType.RegionType));
                        }
                        break;
                    default:
                        if (upper.StartsWith("TAG.", StringComparison.Ordinal))
                            region.SetTag(upper[4..], key.Arg);
                        break;
                }
            }

            _world.AddRegion(region);
            count++;
        }

        if (count > 0)
            _log.LogInformation("Loaded {Count} AREADEF definitions as regions", count);
    }

    /// <summary>
    /// Parse a Source-X FLAGS expression. Accepts numeric (decimal/hex) values
    /// and pipe-separated symbol lists like
    /// "REGION_FLAG_NOBUILDING|REGION_FLAG_GUARDED" used by sphere scripts.
    /// </summary>
    private static SphereNet.Core.Enums.RegionFlag ParseRegionFlags(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return SphereNet.Core.Enums.RegionFlag.None;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(trimmed.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out uint hexVal))
            return (SphereNet.Core.Enums.RegionFlag)hexVal;
        if (uint.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out uint decVal))
            return (SphereNet.Core.Enums.RegionFlag)decVal;

        SphereNet.Core.Enums.RegionFlag result = SphereNet.Core.Enums.RegionFlag.None;
        foreach (var token in trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Source-X DEF macro names are REGION_FLAG_<NAME>; the C# enum uses
            // PascalCase. Strip the prefix and try a case-insensitive Enum.Parse
            // with a couple of well known aliases.
            var name = token;
            if (name.StartsWith("REGION_FLAG_", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("REGION_FLAG_".Length);
            name = name.Replace("_", string.Empty);

            // Common aliases between Source-X define names and our enum members.
            name = name switch
            {
                "NOBUILDING" => "NoBuild",
                "GUARDEDOFF" => "GuardedOff",
                "NOMAGIC" => "NoMagic",
                "NOPVP" => "NoPvP",
                "NOPERACRIME" => "NoPeraCrime",
                "SAFEZONE" => "SafeZone",
                _ => name
            };

            if (Enum.TryParse<SphereNet.Core.Enums.RegionFlag>(name, true, out var flag))
                result |= flag;
        }
        return result;
    }

    private static void LoadRoomDefs()
    {
        int count = 0;
        foreach (var link in _resources.GetAllResources())
        {
            if (link.Id.Type != ResType.RoomDef) continue;

            var room = new SphereNet.Game.World.Regions.Room
            {
                ResourceId = link.Id,
                Name = link.DefName ?? link.Id.ToString()
            };

            // Read stored keys or re-open the script file
            var keys = link.StoredKeys;
            if (keys == null)
            {
                // CRITICAL: ReadAllSections() walks from the section's start
                // line to EOF and returns *every* section in between. We must
                // consume only the first one — the resource link points at
                // exactly one [AREADEF/ROOMDEF a_xxx] block. The previous code
                // merged every following section's keys into this definition,
                // so an early AREADEF inherited the RECTs of every later one
                // in the file (observed: a single "Minax Stronghold" reported
                // with 36/37/40/41/42 rects depending on file position,
                // swallowing Britain because the cumulative rect set covered
                // the city coordinates).
                using var sf = link.OpenAtStoredPosition();
                if (sf == null) continue;
                var sections = sf.ReadAllSections();
                if (sections.Count == 0) continue;
                keys = sections[0].Keys;
            }

            foreach (var key in keys)
            {
                var upper = key.Key.ToUpperInvariant();
                switch (upper)
                {
                    case "NAME":
                        room.Name = key.Arg;
                        break;
                    case "MAP":
                        if (byte.TryParse(key.Arg, out byte mapIdx))
                            room.MapIndex = mapIdx;
                        break;
                    case "RECT":
                        // Format: x1,y1,x2,y2
                        var parts = key.Arg.Split(',');
                        if (parts.Length >= 4 &&
                            short.TryParse(parts[0].Trim(), out short x1) &&
                            short.TryParse(parts[1].Trim(), out short y1) &&
                            short.TryParse(parts[2].Trim(), out short x2) &&
                            short.TryParse(parts[3].Trim(), out short y2))
                        {
                            room.AddRect(x1, y1, x2, y2);
                        }
                        break;
                    case "EVENTS":
                        if (!string.IsNullOrEmpty(key.Arg))
                        {
                            foreach (var ev in key.Arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                room.AddEvent(ResourceId.FromString(ev, ResType.Events));
                        }
                        break;
                    default:
                        if (upper.StartsWith("TAG.", StringComparison.Ordinal))
                            room.SetTag(upper[4..], key.Arg);
                        break;
                }
            }

            _world.AddRoom(room);
            count++;
        }

        if (count > 0)
            _log.LogInformation("Loaded {Count} ROOMDEF definitions", count);
    }

    // --- Script Loading ---

    private static int LoadAllScripts(string dir)
    {
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.scp", SearchOption.AllDirectories))
        {
            _resources.LoadResourceFile(file);
            count++;
        }
        return count;
    }

    private static void RegisterBuiltinDefNames()
    {
        // Engine baseline DEFNAMEs — script packs (defs.scp) typically
        // override these on load. We register them up-front so admin
        // dialogs and core scripts that depend on them still resolve
        // even when a barebones scripts/ directory is shipped.
        if (_resources == null) return;

        // [DEFNAME ref_types] from Source-X core defs.scp.
        // Used by <GetRefType> == <Def.TRef_*> guards.
        var refTypes = new (string Name, int Value)[]
        {
            ("tref_serv",          0x000001),
            ("tref_file",          0x000002),
            ("tref_newfile",       0x000004),
            ("tref_db",            0x000008),
            ("tref_resdef",        0x000010),
            ("tref_resbase",       0x000020),
            ("tref_functionargs",  0x000040),
            ("tref_fileobj",       0x000080),
            ("tref_fileobjcont",   0x000100),
            ("tref_account",       0x000200),
            ("tref_stonemember",   0x000800),
            ("tref_serverdef",     0x001000),
            ("tref_sector",        0x002000),
            ("tref_world",         0x004000),
            ("tref_gmpage",        0x008000),
            ("tref_client",        0x010000),
            ("tref_object",        0x020000),
            ("tref_char",          0x040000),
            ("tref_item",          0x080000),
        };

        foreach (var (name, val) in refTypes)
        {
            _resources.RegisterDefName(name,
                new SphereNet.Core.Types.ResourceId(
                    SphereNet.Core.Enums.ResType.DefName, val));
        }
    }

    private static List<string> ResolveScriptDirectories(string basePath, string scpConfig)
    {
        var dirs = new List<string>();
        if (string.IsNullOrWhiteSpace(scpConfig))
            return dirs;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = scpConfig.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            string dir = ResolvePath(basePath, part.Trim());
            if (!Directory.Exists(dir))
                continue;
            if (seen.Add(dir))
                dirs.Add(dir);
        }

        return dirs;
    }

    // --- Helpers ---

    private static string FindConfigFile(string basePath, string fileName)
    {
        string[] searchPaths =
        [
            Path.Combine(Directory.GetCurrentDirectory(), fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "config", fileName),
            Path.Combine(basePath, fileName),
            Path.Combine(basePath, "config", fileName),
        ];

        foreach (string path in searchPaths)
        {
            if (File.Exists(path))
                return path;
        }
        return "";
    }

    private static string FindDir(string basePath, string dirName)
    {
        string[] searchPaths =
        [
            Path.Combine(Directory.GetCurrentDirectory(), dirName),
            Path.Combine(basePath, dirName),
        ];

        foreach (string path in searchPaths)
        {
            if (Directory.Exists(path))
                return path;
        }
        return "";
    }

    /// <summary>
    /// Resolve a config path: if absolute, use as-is; if relative, resolve from basePath.
    /// </summary>
    private static string ResolvePath(string basePath, string configPath)
    {
        if (Path.IsPathRooted(configPath))
            return configPath;
        return Path.Combine(basePath, configPath);
    }

    /// <summary>Minimal ITextConsole for REF command execution.</summary>
    private sealed class RefExecConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
    }

    private sealed class ServerHookContext : IScriptObj
    {
        public string GetName() => "SERVER";

        public bool TryGetProperty(string key, out string value)
        {
            value = key.Equals("NAME", StringComparison.OrdinalIgnoreCase) ? "SERVER" : "";
            return key.Equals("NAME", StringComparison.OrdinalIgnoreCase);
        }

        public bool TryExecuteCommand(string key, string args, ITextConsole source) => false;

        public bool TrySetProperty(string key, string value) => false;

        public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) => TriggerResult.Default;
    }
}
