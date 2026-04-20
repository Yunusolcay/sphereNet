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
using SphereNet.Scripting.Resources;
#if WINFORMS
using SphereNet.Server.Admin;
#endif
using Color = System.Drawing.Color;
using System.Security.Cryptography;
using System.Text;

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
    private static SphereNet.Game.NPCs.StableEngine _stableEngine = new();
    private static SphereNet.Game.Scheduling.TimerWheel _npcTimerWheel = null!;
    private static TriggerDispatcher _triggerDispatcher = null!;
    private static TriggerRunner _triggerRunner = null!;
    private static ScriptSystemHooks _systemHooks = null!;
    private static ScriptDbAdapter _scriptDb = null!;
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
    private static long _telemetrySnapshotUs;
    private static long _telemetryComputeUs;
    private static long _telemetryApplyUs;
    private static long _telemetryFlushUs;
    private static long _telemetryMaxTickUs;
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

        serilogConfig = serilogConfig.WriteTo.File(
                Path.Combine(basePath, "logs", "spherenet-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

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
        Character.AttackingIsACrimeEnabled = _config.AttackingIsACrime;
        Character.SnoopCriminalEnabled     = _config.SnoopCriminal;
        Character.ReagentsRequiredEnabled  = _config.ReagentsRequired;

        // --- 5. World ---
        _world = new GameWorld(_loggerFactory);
        _world.MaxContainerItems = _config.ContainerMaxItems;
        _world.MaxBankItems      = _config.BankMaxItems;
        _world.MaxBankWeight     = _config.BankMaxWeight;
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
        _systemHooks = new ScriptSystemHooks(_triggerRunner, _loggerFactory.CreateLogger<ScriptSystemHooks>());
        _scriptDb = new ScriptDbAdapter(_loggerFactory.CreateLogger<ScriptDbAdapter>());
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
        _partyManager = new PartyManager();
        _guildManager = new GuildManager();
        _guildManager.DeserializeFromWorld(_world);
        if (_guildManager.GuildCount > 0)
            _log.LogInformation("Restored {Count} guilds from world save", _guildManager.GuildCount);
        _speech = new SpeechEngine(_world);
        _speech.PartyManager = _partyManager;
        _speech.GuildManager = _guildManager;
        _speech.OnNpcHear += OnNpcHearSpeech;
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
                if (c.Character == gm)
                {
                    scriptConsole = c;
                    break;
                }
            }

            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(gm, 0, 0, args)
            {
                Object1 = gm,
                Object2 = gm
            };

            if (!_triggerRunner.TryRunFunction(verb, gm, scriptConsole, trigArgs, out var result))
                return false;

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
        _commands.OnInspectRequested += (gm, uid) =>
        {
            foreach (var c in _clients.Values)
            {
                if (c.Character == gm)
                {
                    c.ShowInspectDialog(uid);
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
                    if (!c.TryShowScriptDialog(dialogName, page))
                    {
                        // Collect a few close-match suggestions so the user
                        // can tell if it's a typo ("d_moongate" vs
                        // "d_moongates") or a truly missing dialog.
                        var suggestions = CollectDialogSuggestions(dialogName, maxCount: 5);
                        string hint = suggestions.Count == 0
                            ? ""
                            : "  Similar: " + string.Join(", ", suggestions);
                        c.SysMessage($"Dialog '{dialogName}' not found.{hint}");
                    }
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
        _npcAI = new NpcAI(_world);
        _npcTimerWheel = new SphereNet.Game.Scheduling.TimerWheel(Environment.TickCount64);
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

            // Broadcast attack animation and damage to nearby clients
            var animPkt = new PacketAnimation(attacker.Uid.Value, 4); // swing anim
            BroadcastNearby(attacker.Position, 18, animPkt, 0);

            // Broadcast damage number
            var dmgPkt = new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue));
            BroadcastNearby(target.Position, 18, dmgPkt, 0);

            // Update target health bar for the victim's client
            var healthPkt = new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits);
            BroadcastNearby(target.Position, 18, healthPkt, 0);

        };
        _npcAI.OnNpcKill = (killer, victim) =>
        {
            // TODO: NPC death/corpse packet sync still has edge cases; revisit with
            // Source-X/ServUO/ClassicUO side-by-side traces.
            _triggerDispatcher?.FireCharTrigger(killer, CharTrigger.Kill,
                new TriggerArgs { CharSrc = killer, O1 = victim });
            _triggerDispatcher?.FireCharTrigger(victim, CharTrigger.Death,
                new TriggerArgs { CharSrc = killer });

            var victimPos = victim.Position;
            byte victimDir = (byte)((byte)victim.Direction & 0x07);
            var corpse = _deathEngine.ProcessDeath(victim, killer);
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
                    // NPC corpse — matches both Source-X (PacketDeath +
                    // RemoveFromView) and ServUO (DeathAnimation + Delete ->
                    // RemovePacket) reference flow:
                    //   1) 0x1A WorldItem  (corpse appears in world)
                    //   2) 0xAF DeathAnim  (mobile -> corpse transition)
                    //   3) 0x1D DeleteObj  (remove the dead mobile)
                    var corpsePacket = new PacketWorldItem(
                        corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                        corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                        victimDir);
                    BroadcastNearby(victimPos, 18, corpsePacket, 0);

                    uint npcFallDir = (uint)Random.Shared.Next(2);
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
        var gatheringEngine = new GatheringEngine(_world, _triggerDispatcher);
        _skillHandlers = new SkillHandlers(_world, gatheringEngine);
        _craftingEngine = new CraftingEngine(_world);
        _weatherEngine = new WeatherEngine(_world);
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

        // Corpse decay -> drop contents to the ground. Invoked by the
        // per-item decay timer in Item.OnTick; replaces the old per-tick
        // full-world scan in DeathEngine.ProcessDecay.
        SphereNet.Game.Objects.Items.Item.OnCorpseDecay = corpse =>
        {
            var pos = corpse.Position;
            foreach (var child in corpse.Contents.ToArray())
            {
                corpse.RemoveItem(child);
                _world.PlaceItem(child, pos);
            }
        };
        SphereNet.Game.Objects.Items.Item.ResolveShipEngine = () => _shipEngine;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => _world;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => _world;
        SphereNet.Game.Objects.ObjBase.OnNameChangeWarning = msg =>
            _log.LogWarning("[NAME_CHANGE] {Details}", msg);

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
            // Fallback: search accounts for matching char slot
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

        // Mounts
        _mountEngine = new SphereNet.Game.Mounts.MountEngine(_world);

        // Stress-test harness (.stress / .stressreport / .stressclean)
        _stressEngine = new SphereNet.Game.Diagnostics.StressTestEngine(_world, _loggerFactory);
        _commands.OnStressGenerateRequested += (items, npcs) => _stressEngine.QueueGenerate(items, npcs);
        _commands.OnStressReportRequested   += () => _stressEngine.LogReport();
        _commands.OnStressCleanupRequested  += () => _stressEngine.QueueCleanup();
        _commands.OnSaveFormatChangeRequested += HandleSaveFormatChange;
        _commands.OnScriptDebugToggleRequested += on =>
        {
            exprParser.DebugUnresolved = on;
            _log.LogInformation("Script debug logging: {State}", on ? "ON" : "OFF");
        };

        // Load spell/item/char definitions from scripts
        var defSw = Stopwatch.StartNew();
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

        // Console command processor (shares logic with telnet)
        _consoleProcessor = new AdminCommandProcessor(_world, _accounts, _config,
            () => _network.ActiveConnections, _loggerFactory);
        _consoleProcessor.OnSaveRequested += PerformSave;
        _consoleProcessor.OnShutdownRequested += () => _running = false;
        _consoleProcessor.OnResyncRequested += PerformScriptResync;
        _consoleProcessor.OnAccountPrivLevelChanged += SyncOnlineAccountPrivLevel;
        _consoleProcessor.OnDebugToggleRequested += ToggleDebugPackets;

        int webPort = _config.ServPort + 2;
        _webStatus = new WebStatusServer(_world, _accounts,
            () => _network.ActiveConnections,
            _loggerFactory.CreateLogger("WebStatus"));
        _webStatus.Start(webPort);
        _log.LogInformation("Type 'help' for commands. Enter commands directly (e.g. save, status, quit).");

        // Schedule all existing NPCs into the timer wheel. Without jitter,
        // 750K+ NPCs would all be "due" on the first tick after load and drain
        // in one massive batch — pinning the main loop for 300-500ms. Spread
        // the initial fire over 60s so at most ~3K NPCs come due per tick.
        if (_npcTimerWheel != null)
        {
            long now = Environment.TickCount64;
            var jitter = new Random(0);
            int scheduled = 0;
            foreach (var obj in _world.GetAllObjects())
            {
                if (obj is Character npc && !npc.IsPlayer && !npc.IsDead && !npc.IsDeleted)
                {
                    long when = npc.NextNpcActionTime > 0
                        ? npc.NextNpcActionTime
                        : now + jitter.Next(0, 60_000);
                    _npcTimerWheel.Schedule(npc, when);
                    scheduled++;
                }
            }
            _log.LogInformation("NPC timer wheel initialized: {Count} NPCs scheduled (60s stagger)", scheduled);
        }

        // --- 9. Main Game Loop ---
        _running = true;
        var sw = Stopwatch.StartNew();
        long lastTickMs = 0;
        const int TickIntervalMs = 250; // 4 ticks per second (Source-X default)

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

        _housingEngine?.SerializeAllToTags();
        _shipEngine?.SerializeAllToTags();
        _guildManager?.SerializeAllToTags(_world);
        string shutdownSavePath = ResolvePath(basePath, _config.WorldSaveDir);
        _saver.Save(_world, shutdownSavePath);
        string shutdownAccDir = ResolvePath(basePath, _config.AccountDir);
        SphereNet.Persistence.Accounts.AccountPersistence.Save(
            _accounts, shutdownAccDir, _saver.Format,
            _loggerFactory.CreateLogger("AccountPersistence"));

        _telnet?.Dispose();
        _webStatus?.Dispose();
        _network.Dispose();
        _mapData?.Dispose();
        _scriptDb.Close();
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
            "TICKPERIOD" => "250",

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

            // --- Reference lookups via SERV.xxx ---
            "LASTNEWITEM" => _world?.LastNewItem.Value.ToString() ?? "0",
            "LASTNEWCHAR" => _world?.LastNewChar.Value.ToString() ?? "0",

            // --- SERV.MAP* ---
            _ when upper.StartsWith("MAPLIST.") => ResolveMapListProperty(upper[8..]),

            // --- SERV.SKILL.n.KEY / SERV.SKILL.n.NAME — skill table lookup.
            // Admin dialogs iterate all 58 skills using <Serv.Skill.<idx>.Key>
            // to discover defnames at runtime.
            _ when upper.StartsWith("SKILL.") => ResolveServSkill(upper[6..]),

            // --- ISEVENT.name — 1 if the named event script is loaded.
            // Used by admin dialogs to grey out delete buttons for missing events.
            _ when upper.StartsWith("ISEVENT.") => ResolveIsEvent(upper[8..]),

            // --- SERV.ACCOUNT.name or SERV.ACCOUNT.n ---
            _ when upper.StartsWith("ACCOUNT.") => ResolveServAccount(upper[8..]),

            // --- SERV.MAP.n.SECTOR.n.property ---
            _ when upper.StartsWith("MAP.") => ResolveServMapSector(upper[4..]),

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

            // REF object property access
            _ when upper.StartsWith("_REF_GET=") => HandleRefGet(property[9..]),
            _ when upper.StartsWith("_REF_EXEC=") => HandleRefExec(property[10..]),

            // serv.allclients <function> — invoke <function> once per
            // online player, with the caller as src. Protocol format:
            // _ALLCLIENTS=<srcUid>|<funcName>.
            _ when upper.StartsWith("_ALLCLIENTS=") => HandleAllClients(property[12..]),

            // Region property access
            _ when upper.StartsWith("_REGION_GET=") => HandleRegionGet(property[12..]),

            // Room property access
            _ when upper.StartsWith("_ROOM_GET=") => HandleRoomGet(property[10..]),

            _ => null
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
        obj.TryExecuteCommand(cmd, cmdArgs, new RefExecConsole());
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
        if (pipe <= 0 || _world == null || _triggerRunner == null) return "";
        string uidStr = data[..pipe].Trim();
        string funcName = data[(pipe + 1)..].Trim();
        if (string.IsNullOrEmpty(funcName)) return "";

        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint srcUid))
            return "";

        var srcObj = _world.FindObject(new SphereNet.Core.Types.Serial(srcUid));
        if (srcObj is not SphereNet.Game.Objects.Characters.Character srcChar)
            return "";

        // Snapshot so a function that mutates CTags on src (common
        // "serv.allclients f_count" pattern) doesn't interfere with
        // iteration.
        var snapshot = _clients.Values.Where(c => c.IsPlaying && c.Character != null).ToList();
        foreach (var client in snapshot)
        {
            var target = client.Character!;
            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(srcChar)
            {
                Object1 = target,
                Object2 = srcChar,
            };
            _triggerRunner.TryRunFunction(funcName, target, client, trigArgs, out _);
        }
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
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string sp = ResolvePath(basePath, _config.WorldSaveDir);
            _saver.Save(_world, sp);
            string accDir = ResolvePath(basePath, _config.AccountDir);
            SphereNet.Persistence.Accounts.AccountPersistence.Save(
                _accounts, accDir, _saver.Format,
                _loggerFactory.CreateLogger("AccountPersistence"));
            _saveCount++;
            sw.Stop();
            _log.LogInformation("Save complete. (#{Count}, {Ms} ms)", _saveCount, sw.ElapsedMilliseconds);
            BroadcastToAllPlayers(
                ServerMessages.GetFormatted("worldsave_complete", _saveCount, sw.ElapsedMilliseconds),
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

            client.Character.PrivLevel = level;
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
                if (db.Connect(connCfg.Name, out string err))
                    _log.LogInformation("DB '{Name}' connected ({Host}/{Db})",
                        connCfg.Name, connCfg.Host, connCfg.Database);
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
            client.SetScriptServices(_systemHooks, _scriptDb, ResolveDefMessage, _scriptFile);
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
        // Walk the sector window that covers `range` tiles around
        // `center`. Range is almost always 18 (view range), sector
        // size is 64, so a 1-sector radius (3x3 window) is enough —
        // worst case a player on a sector boundary is still within
        // one neighbour sector. This replaces a full _clients.Values
        // iteration that scaled linearly with online count.
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
            client.HandleWarMode(warMode);
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

    private static void OnGumpTextEntry(NetState state, uint serial, uint gumpId, uint buttonId, string text)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleGumpTextEntry(serial, gumpId, buttonId, text);
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
    private static void OnNpcHearSpeech(Character speaker, Character npc, string text, TalkMode mode)
    {
        string lower = text.ToLowerInvariant();

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
            return; // Script handled it

        // Script-driven SPEECH triggers (from CHARDEF SPEECH/TSPEECH)
        var speechResult = _triggerDispatcher?.FireSpeechTrigger(npc, speaker, text);
        if (speechResult == TriggerResult.True)
            return; // Speech script handled it

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
                if (lower.Contains("buy") || lower.Contains("vendor") || lower.Contains("purchase"))
                    response = $"Take a look at my goods.";
                else if (lower.Contains("sell"))
                    response = $"Show me what you have to sell.";
                break;

            case NpcBrainType.Banker:
                if (lower.Contains("bank") || lower.Contains("balance"))
                {
                    // Open bank box
                    foreach (var c in _clients.Values)
                    {
                        if (c.Character == speaker)
                        {
                            c.OpenBankBox();
                            break;
                        }
                    }
                    response = "Here is your bank box.";
                }
                else if (lower.Contains("check"))
                    response = "I can issue you a bank check.";
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
                _log.LogError(ex, "Multicore tick failure. Falling back to single-thread mode.");
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

            // Slow-tick detector: anything over 50ms will show up as ping jitter
            // on a 250ms tick budget. Log per-phase breakdown so the cause is
            // visible without running a profiler.
            if (totalUs > 50_000)
            {
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
            client.TickCombat();
            client.TickSpellCast();
            client.TickStatUpdate();
            client.UpdateClientView();
        }

        // Reset dirty flags so objects can be re-notified on next change
        _world.ConsumeDirtyObjects();

        _telemetryApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p2 = Stopwatch.GetTimestamp();
        RunPostTickMaintenance();
        _telemetryFlushUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2);

        MaybeRunDeterminismGuardrail();
    }

    private static void RunMulticoreTick()
    {
        int workerCount = _config.MulticoreWorkerCount > 0 ? _config.MulticoreWorkerCount : Environment.ProcessorCount;
        int timeoutMs = Math.Max(100, _config.MulticorePhaseTimeoutMs);
        using var cts = new CancellationTokenSource(timeoutMs);
        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = cts.Token
        };

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
        var npcDecisions = new ConcurrentBag<NpcAI.NpcDecision>();
        Parallel.ForEach(npcSnapshot, po, npc =>
        {
            var decision = _npcAI.BuildDecision(npc, nowTick);
            if (decision.HasValue)
                npcDecisions.Add(decision.Value);
        });

        foreach (var client in clientSnapshot)
        {
            client.TickCombat();
            client.TickSpellCast();
            client.TickStatUpdate();
        }

        // Full scan: parallel build, sequential apply
        var clientDeltas = new ConcurrentDictionary<int, GameClient.ClientViewDelta>();
        Parallel.ForEach(clientSnapshot, po, client =>
        {
            var delta = client.BuildViewDelta();
            if (delta != null)
                clientDeltas[client.NetState.Id] = delta;
        });
        _telemetryComputeUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p2 = Stopwatch.GetTimestamp();
        var sortedDecisions = npcDecisions.ToArray();
        Array.Sort(sortedDecisions, (a, b) => a.NpcUid.CompareTo(b.NpcUid));
        foreach (var decision in sortedDecisions)
            _npcAI.ApplyDecision(decision);

        foreach (var client in clientSnapshot)
        {
            if (clientDeltas.TryGetValue(client.NetState.Id, out var delta))
                client.ApplyViewDelta(delta);
        }
        _telemetryApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2);

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

    private static void RunPostTickMaintenance()
    {
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
        {
            var seasonPacket = new PacketSeason((byte)_weatherEngine.CurrentSeason);
            foreach (var client in _clients.Values)
            {
                if (client.IsPlaying)
                    client.Send(seasonPacket);
            }
        }

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
                using var sf = link.OpenAtStoredPosition();
                if (sf == null) continue;
                var sections = sf.ReadAllSections();
                keys = [];
                foreach (var sec in sections)
                    keys.AddRange(sec.Keys);
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
                        }
                        break;
                    case "FLAGS":
                        if (uint.TryParse(key.Arg, out uint flagsVal))
                            region.Flags = (SphereNet.Core.Enums.RegionFlag)flagsVal;
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
                using var sf = link.OpenAtStoredPosition();
                if (sf == null) continue;
                var sections = sf.ReadAllSections();
                keys = [];
                foreach (var sec in sections)
                    keys.AddRange(sec.Keys);
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
        // Reserved for core DEFNAME bootstrap values.
        // Script packs provide most defnames at startup.
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
