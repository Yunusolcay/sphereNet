namespace SphereNet.Core.Configuration;

public enum SeasonMode
{
    Auto = 0,
    Manual = 1,
}

public enum ClientEra
{
    Sphere56x = 0,
    Modern = 1,
}

/// <summary>
/// Server configuration model. Maps to CServerConfig in Source-X.
/// Holds all sphere.ini settings as strongly-typed properties.
/// </summary>
public sealed class SphereConfig
{
    // General
    public string ServName { get; set; } = "SphereNet";
    public string ServIP { get; set; } = "0.0.0.0";
    public int ServPort { get; set; } = 2593;
    public string AdminEmail { get; set; } = "";

    /// <summary>An extra login-list shard (Source-X [SERVERS] block entry) advertised
    /// in the 0xA8 packet in addition to this shard: display name, host/ip, game port.</summary>
    public sealed record ServerDef(string Name, string Ip, int Port);

    /// <summary>Extra shards advertised in the 0xA8 server list beyond this one
    /// (Source-X send.cpp:3299 config-driven entries). Parsed from the SERVERLIST ini
    /// key — "name,ip,port" entries separated by ';'. Empty → single-server list.</summary>
    public List<ServerDef> ServerList { get; } = new();

    // Client
    public string ClientVersion { get; set; } = "4.0.2";
    public ClientEra ClientEra { get; set; } = ClientEra.Modern;
    public bool UseCrypt { get; set; } = true;
    public bool UseNoCrypt { get; set; }
    public int ClientMax { get; set; } = 256;
    public int ClientMaxIP { get; set; } = 16;
    public int ConnectingMax { get; set; } = 32;
    public int ClientLinger { get; set; } = 60;

    // Paths
    public string ScpFilesDir { get; set; } = "scripts/";
    public string ScriptEncoding { get; set; } = "AUTO";
    public int ScriptLegacyCodePage { get; set; } = 1252;
    public string WorldSaveDir { get; set; } = "save/";
    public string AccountDir { get; set; } = "accounts/";
    public string MulFilesDir { get; set; } = "";
    public string LogDir { get; set; } = "logs/";

    // Maps
    public MapDefinition[] Maps { get; set; } = [
        new() { MaxX = 6144, MaxY = 4096, SectorSize = 64, MapReadId = 0, MapSendId = 0 }
    ];

    // World Save
    public int SavePeriodMinutes { get; set; } = 15;

    /// <summary>Persist the world during a clean shutdown so changes since the last
    /// periodic save survive a planned stop. Safe default on; set 0 to disable.</summary>
    public bool SaveOnShutdown { get; set; } = true;
    public int BackupLevels { get; set; } = 10;
    /// <summary>sphere.ini SAVEBACKGROUND. Any value > 0 moves the save's
    /// shard/encode/write phase onto a background worker (the world walk stays
    /// on the main loop, so the player-visible stall is just the capture).
    /// 0 = fully synchronous save, like Source-X with SAVEBACKGROUND=0.</summary>
    public int SaveBackgroundMinutes { get; set; }
    /// <summary>Source-X incremental-stage knobs, accepted for ini compat.
    /// SphereNet's background save writes from an immutable snapshot instead of
    /// staging sectors across ticks, so these two are currently no-ops.</summary>
    public int SaveSectorsPerTick { get; set; } = 1;
    public int SaveStepMaxComplexity { get; set; } = 500;
    public SaveFormat SaveFormat { get; set; } = SaveFormat.BinaryGz;
    /// <summary>Sharding mode.
    /// <list type="bullet">
    /// <item><c>0</c> = always a single file, rolling off.</item>
    /// <item><c>1</c> = Sphere-style rolling: one file until it crosses
    /// <see cref="SaveShardSizeMb"/>, then spill to the next.</item>
    /// <item><c>2-16</c> = fixed parallel hash shards (UID % N).</item>
    /// </list></summary>
    public int SaveShards { get; set; } = 3;
    /// <summary>Rolling threshold in MB. Only consulted when SaveShards=1.</summary>
    public int SaveShardSizeMb { get; set; } = 75;

    // Accounts
    public int AccApp { get; set; } = 2;
    public bool Md5Passwords { get; set; }
    public int MaxCharsPerAccount { get; set; } = 5;
    /// <summary>Days a character must exist before it can be deleted from the
    /// char-select screen (classic 7-day rule). 0 disables the gate.</summary>
    public int MinCharDeleteTime { get; set; } = 7;

    // Game Mechanics
    /// <summary>Real seconds per in-game minute. Default 20 matches the previous
    /// hardcoded world clock; sphere.ini GameMinuteLength overrides it.</summary>
    public int GameMinuteLength { get; set; } = 20;
    /// <summary>Minutes between Source-X f_onserver_timer calls. Zero disables it.</summary>
    public int TimerCallMinutes { get; set; }
    /// <summary>Minutes a clientless sector waits before sleeping (Source-X
    /// SECTORSLEEP / _iSectorSleepDelay, default 10). 0 disables sector sleeping.</summary>
    public int SectorSleep { get; set; } = 10;

    /// <summary>SECTORSLEEP as milliseconds for Sector.SleepDelayMs (Source-X stores
    /// value * 60 * MSECS_PER_SEC). 0 disables sleeping; negatives clamp to 0.</summary>
    public long SectorSleepMs => Math.Max(0, SectorSleep) * 60_000L;
    public int MapViewSize { get; set; } = 18;
    public int MapViewSizeMax { get; set; } = 18;
    /// <summary>Combat retreat distance (tiles). 0 = use MapViewSize. sphere.ini MAPVIEWRADAR.</summary>
    public int MapViewRadar { get; set; }
    /// <summary>Seconds before combat memory clears from inactivity. 0 = disabled. sphere.ini ATTACKERTIMEOUT.</summary>
    public int AttackerTimeout { get; set; }

    // Regen: SECONDS to recover one point (Source-X CServerConfig m_iRegenRate).
    // REGEN0=STAT_STR(hits), REGEN1=STAT_INT(mana), REGEN2=STAT_DEX(stam).
    public int RegenHits { get; set; } = 40;
    public int RegenMana { get; set; } = 20;
    public int RegenStam { get; set; } = 10;
    public int RegenFood { get; set; } = 60 * 60;

    // Combat
    public int CombatFlags { get; set; }
    public int CombatDamageEra { get; set; }
    public int CombatHitChanceEra { get; set; }
    public int CombatSpeedEra { get; set; }
    public int CombatParryingEra { get; set; } =
        (int)(SphereNet.Core.Enums.ParryEraFlags.PreSeFormula |
              SphereNet.Core.Enums.ParryEraFlags.ShieldBlock);
    public int SpeedScaleFactor { get; set; } = 15000;
    public int CombatArcheryMovementDelay { get; set; }
    public int CombatMeleeMovementDelay { get; set; }
    public int ArcheryMinDist { get; set; } = 1;
    public int ArcheryMaxDist { get; set; } = 12;
    public int MagicFlags { get; set; }
    public bool ReagentsRequired { get; set; } = true;
    public bool SpellbookRequired { get; set; } = true;
    public bool EquippedCast { get; set; }
    public bool ReagentLossAbort { get; set; }
    public bool ReagentLossFail { get; set; }
    public bool ManaLossAbort { get; set; }
    public bool ManaLossFail { get; set; }
    public int ManaLossPercent { get; set; } = 100;
    public int WalkBuffer { get; set; } = 75;
    public int WalkRegen { get; set; } = 25;
    // Movement Credit System (opt-in, disabled by default)
    public bool MovementCreditEnabled { get; set; }
    public int MovementCreditBaseMs { get; set; } = 200;
    public int MovementCreditMaxMs { get; set; } = 1400;
    public int MovementQueueCapacity { get; set; } = 10;

    // Configurable movement delays (ms)
    public int WalkDelayFoot { get; set; } = 400;
    public int WalkDelayMount { get; set; } = 200;
    public int RunDelayFoot { get; set; } = 200;
    public int RunDelayMount { get; set; } = 100;

    // Speed hack detection (opt-in)
    public bool SpeedHackDetectionEnabled { get; set; }
    public double SpeedHackRateThreshold { get; set; } = 1.5;
    public int SpeedHackBurstWindow { get; set; } = 3;
    public int SpeedHackHistorySize { get; set; } = 20;
    public int SpeedHackCooldownMs { get; set; } = 60_000;

    // RTT Measurement. Disabled (0) by default: the server-initiated 0x73 ping is
    // unsolicited, and the standard ClassicUO client feeds EVERY incoming 0x73 into its
    // own ping meter (NetStatistics.PingReceived computes Time.Ticks - _startTickValue,
    // where _startTickValue only advances on a client-sent ping). So the server's pings
    // make the client display a bogus "ping" (its own uptime — hundreds of thousands of
    // ms). The server-side RttMs is not consumed anywhere, so keep this off unless a
    // client that ignores unsolicited pings is in use.
    public int RttPingIntervalMs { get; set; } = 0;

    /// <summary>Source-X HITPOINTPERCENTONREZ (m_iHitpointPercentOnRez): the %
    /// of max hits a character resurrects with. Reference default 10.</summary>
    public int HitpointPercentOnRez { get; set; } = 10;

    /// <summary>Source-X PACKETDEATHANIMATION (m_iPacketDeathAnimation): send
    /// the 0x2C death-screen packet to a dying client. 0 disables it — the
    /// client then skips ClassicUO's 1.5s death-screen freeze entirely.</summary>
    public int PacketDeathAnimation { get; set; } = 1;

    // Crime & Notoriety
    public int CriminalTimer { get; set; } = 180;
    public int MurderMinCount { get; set; } = 5;
    // Source-X PLAYEREVIL / PLAYERNEUTRAL (m_iPlayerKarmaEvil/-Neutral): karma
    // thresholds below which a player renders red / grey with zero murders.
    public int PlayerKarmaEvil { get; set; } = -8000;
    public int PlayerKarmaNeutral { get; set; } = -2000;
    public int MurderDecayTime { get; set; } = 28800;
    public bool LootingIsACrime { get; set; } = true;
    public bool AttackingIsACrime { get; set; } = true;
    public bool HelpingCriminalsIsACrime { get; set; }
    public int GuardLinger { get; set; } = 300;
    public bool GuardsInstantKill { get; set; } = true;
    public bool GuardsOnMurderers { get; set; } = true;
    public bool SnoopCriminal { get; set; } = true;
    public int NotoTimeout { get; set; } = 30;
    public bool MonsterFight { get; set; }
    public bool MonsterFear { get; set; } = true;
    public int AdvancedLos { get; set; }

    // NPC AI (Source-X NPCAI / NPCHEALTHRESHOLD / NPCWANDERLOOKAROUNDCHANCE).
    // NpcAi is the NPC_AI_* flag mask applied to every NPC unless a character
    // sets OVERRIDE.NPCAI. Source-X's bare default is 0; SphereNet keeps the
    // previously-shipped sensible default (PATH|COMBAT|PERSISTENTPATH|THREAT =
    // 0x0C41) so existing worlds don't lose pathfinding/threat behaviour. Set
    // NPCAI=0 in the .ini for exact bare-Source-X behaviour.
    public int NpcAi { get; set; } = 0x0C41;
    // Self-heal threshold (% HP) handed to the @NPCActCast trigger as
    // LOCAL.HealThreshold. Source-X m_iNPCHealthreshold default 30.
    public int NpcHealThreshold { get; set; } = 30;
    // Percent chance (0-100) that an idle wandering NPC runs a look-around
    // target scan on a given wander step. Source-X m_iNPCWanderLookAroundChance
    // default 30. OVERRIDE.LOOKAROUNDCHANCE overrides it per character.
    public int NpcWanderLookAroundChance { get; set; } = 30;
    public ushort ColorNotoGood { get; set; } = 0x0059;
    public ushort ColorNotoGoodNpc { get; set; } = 0x0059;
    public ushort ColorNotoGuildSame { get; set; } = 0x003F;
    public ushort ColorNotoNeutral { get; set; } = 0x03B2;
    public ushort ColorNotoCriminal { get; set; } = 0x03B2;
    public ushort ColorNotoGuildWar { get; set; } = 0x0090;
    public ushort ColorNotoEvil { get; set; } = 0x0022;
    public ushort ColorNotoInvul { get; set; } = 0x0035;
    public ushort ColorNotoInvulGameMaster { get; set; } = 0x000B;
    public ushort ColorNotoDefault { get; set; } = 0x03B2;
    public ushort ColorInvisItem { get; set; } = 1000;
    public ushort ColorInvis { get; set; }
    public ushort ColorInvisSpell { get; set; }
    public ushort ColorHidden { get; set; }
    public int PetsInheritNotoriety { get; set; }

    // Death & Resurrection
    public int CorpseNpcDecay { get; set; } = 7;
    public int CorpsePlayerDecay { get; set; } = 7;
    public int HitPointPercentOnRez { get; set; } = 10;
    /// <summary>Source-X m_fDeadCannotSeeLiving is a MODE, not a flag: 0 off,
    /// 1 hides living NPCs from a ghost, 2 additionally stops an NPC from seeing
    /// living players that are not its owner. Stored as bool before, which could
    /// never express mode 2.</summary>
    public int DeadCannotSeeLiving { get; set; }

    // Stats
    public int MaxBaseSkill { get; set; } = 1200;
    public int MaxFame { get; set; } = 10000;
    public int MaxKarma { get; set; } = 10000;
    public int MinKarma { get; set; } = -10000;

    // NPC paid training — Source-X CServerConfig defaults: percent 30,
    // absolute cap 420 (42.0), cost 1 gold per 0.1 point.
    public int TrainSkillPercent { get; set; } = 30;
    public int TrainSkillMax { get; set; } = 420;
    public int TrainSkillCost { get; set; } = 1;

    // Container — Source-X CServerConfig defaults: CONTAINERMAXITEMS =
    // MAX_ITEMS_CONT (255), BANKMAXITEMS = 1000, BANKMAXWEIGHT = 1000 stones.
    public int ContainerMaxItems { get; set; } = 255;
    public int BankMaxItems { get; set; } = 1000;
    public int BankMaxWeight { get; set; } = 1000;
    public int ContainerMaxWeight { get; set; } = 400;
    public int ItemsMaxAmount { get; set; } = 60000;

    // Housing
    public int MaxHousesPlayer { get; set; } = 1;
    public int MaxHousesAccount { get; set; } = 1;
    public int MaxShipsPlayer { get; set; } = 1;
    public int MaxShipsAccount { get; set; } = 1;

    // NPC
    public int NpcTrainCost { get; set; } = 30;
    public int NpcTrainMax { get; set; } = 420;
    public int NpcDistanceHear { get; set; } = 16;

    // Global script hooks
    public string SpeechSelf { get; set; } = "";
    public string SpeechPet { get; set; } = "";
    public string EventsPet { get; set; } = "";
    public string EventsPlayer { get; set; } = "";
    public string EventsRegion { get; set; } = "";

    /// <summary>Source-X CommandTrigger: a global function fired before every
    /// command with ARGS = the command line; RETURN 1 consumes it. Lets a
    /// pack add/override commands (f_oncommand).</summary>
    public string CommandTrigger { get; set; } = "";
    public string EventsItem { get; set; } = "";

    // Light
    public int LightDay { get; set; }
    public int LightNight { get; set; } = 25;
    public int DungeonLight { get; set; } = 27;

    // Season
    public SeasonMode SeasonMode { get; set; } = SeasonMode.Auto;
    public byte SeasonDefault { get; set; } = 0;
    public int SeasonChangeIntervalMinutes { get; set; } = 30;

    // Item Durability
    public bool ItemDurabilityEnabled { get; set; } = true;
    public int ItemDurabilityLossChance { get; set; } = 25;
    public int ItemDurabilityLossMin { get; set; } = 1;
    public int ItemDurabilityLossMax { get; set; } = 1;
    public bool ItemBreakOnZeroHits { get; set; } = true;
    public int ItemDefaultHits { get; set; } = 50;

    // Misc
    public int DecayTimer { get; set; } = 30;

    // State Recording
    // Off by default: it is an optional diagnostic/replay feature whose periodic
    // full-world snapshots cost every deployment unless explicitly wanted. Set
    // StateRecordingEnabled=1 in sphere.ini to turn it back on.
    public bool StateRecordingEnabled { get; set; } = false;
    public bool StateRecordPlayersOnly { get; set; } = true;
    public int StateRecordMoveScanMs { get; set; } = 2000;
    public int StateRecordSnapshotMs { get; set; } = 15000;

    // Player Macro
    public bool MacroEnabled { get; set; } = true;
    public int MacroMaxSteps { get; set; } = 50;
    public int MacroMaxLoopMinutes { get; set; } = 120;

    // Features
    public int FeatureT2A { get; set; } = 0x01;
    public int FeatureLBR { get; set; }
    public int FeatureAOS { get; set; }
    public int FeatureSE { get; set; }
    public int FeatureML { get; set; }
    public int FeatureKR { get; set; }
    public int FeatureSA { get; set; }
    public int FeatureTOL { get; set; }
    public int FeatureExtra { get; set; }
    public int RacialFlags { get; set; }
    public bool AutoResDisp { get; set; } = true;

    // Tooltip
    public int ToolTipMode { get; set; } = 0; // 0=off, 1=revision/request, 2=force full
    public int ToolTipCache { get; set; } = 30;

    // Experimental / Option flags
    public int Experimental { get; set; }
    public int OptionFlags { get; set; }
    public const int OF_FileCommands = (int)SphereNet.Core.Enums.OptionFlags.FileCommands;
    public bool HasFileCommands => ((SphereNet.Core.Enums.OptionFlags)(uint)OptionFlags & SphereNet.Core.Enums.OptionFlags.FileCommands) != 0;

    // Network
    public int MaxPacketsPerTick { get; set; } = 100;
    public int FloodDetectionCount { get; set; } = 5;
    public int FloodDetectionWindowMs { get; set; } = 10_000;
    public int DeadSocketTime { get; set; } = 300;
    public int FreezeRestartTime { get; set; } = 60;
    /// <summary>Accepted for sphere.ini compat, deliberately a NO-OP: SphereNet
    /// runs the network on the main loop with non-blocking sockets + per-pass
    /// budgets (see NetworkManager.MaxAcceptsPerPass) instead of Source-X's
    /// thread pool. USEASYNCNETWORK is likewise not modeled.</summary>
    public int NetworkThreads { get; set; }
    public int NetTTL { get; set; } = 300;
    
    // Multicore pipeline (determinism-first). Always on — the sector
    // tick is trivially parallel and Program.cs falls back to single
    // thread on any failure, so there is no configuration surface for
    // it. Tuning knobs (WorkerCount, PhaseTimeoutMs) stay.
    public bool MulticoreDeterminismDebug { get; set; }
    public string MulticoreDeterminismExpectedHash { get; set; } = "";
    public int MulticoreWorkerCount { get; set; } = 0; // 0 => auto
    public int MulticorePhaseTimeoutMs { get; set; } = 5000;

    // World-loop tick interval in milliseconds (ini key: ServerTickMs).
    // 100 = 10 ticks/s (default), matching Source-X exactly: TICKS_PER_SEC=10 =>
    // MSECS_PER_TICK = 1000/10 = 100ms (CServerTime.h), the unit its regen/timer
    // cadence advances by. Lowering it (e.g. 50 = 20 ticks/s) gives finer world-sim
    // timing at the cost of per-tick budget under heavy active-AI loads. Network I/O
    // is not gated by this (it runs every main-loop iteration). Clamped to [20,250].
    public int ServerTickMs { get; set; } = 100;

    // Diagnostic warning thresholds in milliseconds (ini keys: SlowTickWarnMs,
    // LoopStallWarnMs). A server tick slower than SlowTickWarnMs logs
    // [slow_tick]; a main-loop iteration slower than LoopStallWarnMs logs
    // [loop_stall]. 0 disables that warning. Defaults sit at half / one full
    // 100ms tick budget so routine GC and flush blips stay out of the log.
    public int SlowTickWarnMs { get; set; } = 50;
    public int LoopStallWarnMs { get; set; } = 100;
    // A single inbound packet handler slower than this logs [slow_packet] with
    // the opcode (ini key: SlowPacketWarnMs, 0 disables). Names the culprit
    // when [loop_stall] attributes a stall to the net_in phase.
    public int SlowPacketWarnMs { get; set; } = 20;

    // Main loop yield strategy between ticks.
    // 0 = spin   : Thread.SpinWait — lowest latency (<1ms), highest CPU usage.
    //              Best for dedicated servers with spare CPU cores.
    // 1 = sleep  : Thread.Sleep(1) — ~15ms latency on Windows, minimal CPU usage.
    //              Best for shared/low-end machines where CPU is precious.
    // 2 = hybrid : SpinWait + Sleep(0) — ~1ms latency, moderate CPU usage. (default)
    // 3 = adaptive : sleep out the slack until the next tick deadline, capped at a
    //              few ms — near-zero idle CPU with bounded input latency. Best for
    //              small/shared hosts. Watch the [tick_stats] loops-per-tick (lpt)
    //              gauge to confirm the drop before/after switching.
    public int TickSleepMode { get; set; } = 2;

    // Sentry
    public string SentryDsn { get; set; } = "";

    // Logging
    public const int LogMaskPlayerSpeak = 0x002000;
    public int LogMask { get; set; } = 0x03F00;
    public bool DebugPackets { get; set; }
    public bool ScriptDebug { get; set; }
    /// <summary>
    /// Severity filter for the rolling file sink (logs/spherenet-*.log).
    /// The console sink keeps following <c>DebugPackets</c> (Information
    /// by default) so live operators still see commands / connections /
    /// script loads, while the on-disk log stays small and focused on
    /// anomalies.
    ///
    /// Two syntaxes are supported:
    ///   1) <b>Single value</b> = minimum-level threshold. Everything at
    ///      that severity or higher reaches the file.
    ///        <c>LogFileLevel=Warning</c>  → WRN, ERR, FTL
    ///        <c>LogFileLevel=Error</c>    → ERR, FTL only
    ///   2) <b>Pipe-separated whitelist</b> (or comma) = keep ONLY the
    ///      listed levels — useful when you want, say, verbose traces
    ///      plus errors but no Information/Warning noise.
    ///        <c>LogFileLevel=Verbose | Warning | Error | Fatal</c>
    ///
    /// Accepted level names (case-insensitive): Verbose, Debug,
    /// Information, Warning, Error, Fatal.  Default = "Warning".
    /// </summary>
    public string LogFileLevel { get; set; } = "Warning";
    public string DebugPacketOpcodes { get; set; } = "";
    public string CommandPrefix { get; set; } = ".";
    public int DefaultCommandLevel { get; set; }

    // Chat / sound compatibility flags
    public int ChatFlags { get; set; }
    public bool GenericSounds { get; set; } = true;

    // Source-X style MySQL settings (legacy single-connection)
    public int MySQL { get; set; }
    public string MySQLHost { get; set; } = "";
    public string MySQLUser { get; set; } = "";
    public string MySQLPassword { get; set; } = "";
    public string MySQLDatabase { get; set; } = "";

    // Multi-connection database settings
    public List<DbConnectionConfig> DbConnections { get; set; } = [];

    // Distances (tiles). Defaults match the previous hardcoded speech ranges; sphere.ini
    // Distance* overrides them.
    public int DistanceWhisper { get; set; } = 3;
    public int DistanceTalk { get; set; } = 18;
    public int DistanceYell { get; set; } = 48;

    // Web
    /// <summary>Serve the web status / admin HTTP endpoint. Default on preserves the
    /// previous unconditional behavior; UseHttp=0 disables it.</summary>
    public bool UseHttp { get; set; } = true;

    // Admin Panel
    public string AdminPassword { get; set; } = "1234";
    public int AdminPanelPort { get; set; } = 0; // 0 = ServPort + 3

    /// <summary>Parse the SERVERLIST ini value: ';'-separated "name,ip,port" entries.
    /// Malformed entries are skipped. Clears any previously-parsed list first so a
    /// reload replaces rather than appends.</summary>
    private void ParseServerList(string? raw)
    {
        ServerList.Clear();
        if (string.IsNullOrWhiteSpace(raw))
            return;
        foreach (var entry in raw.Split(';', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(',', System.StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0)
                continue;
            int port = parts.Length >= 3 && int.TryParse(parts[2], out int p) ? p : ServPort;
            ServerList.Add(new ServerDef(parts[0], parts[1], port));
        }
    }

    public void LoadFromIni(IniParser ini)
    {
        string section = "SPHERE";

        ServName = ini.GetValue(section, "ServName") ?? ServName;
        ServIP = ini.GetValue(section, "ServIP") ?? ServIP;
        ServPort = ini.GetInt(section, "ServPort", ServPort);
        ParseServerList(ini.GetValue(section, "SERVERLIST"));
        AdminEmail = ini.GetValue(section, "AdminEmail") ?? AdminEmail;

        ClientVersion = ini.GetValue(section, "ClientVersion") ?? ClientVersion;
        string? clientEraRaw = ini.GetValue(section, "ClientEra");
        if (!string.IsNullOrWhiteSpace(clientEraRaw))
        {
            if (Enum.TryParse<ClientEra>(clientEraRaw, ignoreCase: true, out var parsedEra))
                ClientEra = parsedEra;
            else if (int.TryParse(clientEraRaw, out int parsedEraNum) &&
                     Enum.IsDefined(typeof(ClientEra), parsedEraNum))
                ClientEra = (ClientEra)parsedEraNum;
        }
        UseCrypt = ini.GetBool(section, "UseCrypt", UseCrypt);
        UseNoCrypt = ini.GetBool(section, "UseNoCrypt", UseNoCrypt);
        ClientMax = ini.GetInt(section, "ClientMax", ClientMax);
        ClientMaxIP = ini.GetInt(section, "ClientMaxIP", ClientMaxIP);
        ConnectingMax = ini.GetInt(section, "ConnectingMax", ConnectingMax);
        ClientLinger = ini.GetInt(section, "ClientLinger", ClientLinger);

        ScpFilesDir = ini.GetValue(section, "ScpFiles") ?? ScpFilesDir;
        ScriptEncoding = ini.GetValue(section, "ScriptEncoding") ?? ScriptEncoding;
        ScriptLegacyCodePage = ini.GetInt(section, "ScriptLegacyCodePage", ScriptLegacyCodePage);
        WorldSaveDir = ini.GetValue(section, "WorldSave") ?? WorldSaveDir;
        AccountDir = ini.GetValue(section, "AcctFiles") ?? AccountDir;
        MulFilesDir = ini.GetValue(section, "MulFiles") ?? MulFilesDir;
        LogDir = ini.GetValue(section, "Log") ?? LogDir;

        LoadMapDefinitions(ini, section);

        SavePeriodMinutes = ini.GetInt(section, "SavePeriod", SavePeriodMinutes);
        SaveOnShutdown = ini.GetBool(section, "SaveOnShutdown", SaveOnShutdown);
        BackupLevels = ini.GetInt(section, "BackupLevels", BackupLevels);
        SaveBackgroundMinutes = ini.GetInt(section, "SaveBackground", SaveBackgroundMinutes);
        SaveSectorsPerTick = ini.GetInt(section, "SaveSectorsPerTick", SaveSectorsPerTick);
        SaveStepMaxComplexity = ini.GetInt(section, "SaveStepMaxComplexity", SaveStepMaxComplexity);

        // SaveFormat: accepts enum name (Text/TextGz/Binary/BinaryGz) or numeric.
        // Unknown values fall back to Text — misconfigured servers still save.
        string? sfRaw = ini.GetValue(section, "SaveFormat");
        if (!string.IsNullOrWhiteSpace(sfRaw))
        {
            if (Enum.TryParse<SaveFormat>(sfRaw, ignoreCase: true, out var sf))
                SaveFormat = sf;
            else if (int.TryParse(sfRaw, out int sfNum) && Enum.IsDefined(typeof(SaveFormat), sfNum))
                SaveFormat = (SaveFormat)sfNum;
        }
        SaveShards = Math.Clamp(ini.GetInt(section, "SaveShards", SaveShards), 0, 16);
        SaveShardSizeMb = Math.Max(0, ini.GetInt(section, "SaveShardSizeMb", SaveShardSizeMb));

        AccApp = ini.GetInt(section, "AccApp", AccApp);
        Md5Passwords = ini.GetBool(section, "Md5Passwords", Md5Passwords);
        MaxCharsPerAccount = ini.GetInt(section, "MaxCharsPerAccount", MaxCharsPerAccount);
        MinCharDeleteTime = ini.GetInt(section, "MinCharDeleteTime", MinCharDeleteTime);

        GameMinuteLength = ini.GetInt(section, "GameMinuteLength", GameMinuteLength);
        TimerCallMinutes = Math.Max(0, ini.GetInt(section, "TimerCall", TimerCallMinutes));
        SectorSleep = ini.GetInt(section, "SectorSleep", SectorSleep);
        MapViewSize = ini.GetInt(section, "MapViewSize", MapViewSize);
        MapViewSizeMax = ini.GetInt(section, "MapViewSizeMax", MapViewSizeMax);
        MapViewRadar = ini.GetInt(section, "MapViewRadar", MapViewRadar);
        AttackerTimeout = ini.GetInt(section, "AttackerTimeout", AttackerTimeout);

        // Source-X stat order: REGEN0=STR(hits), REGEN1=INT(mana), REGEN2=DEX(stam).
        RegenHits = ini.GetInt(section, "Regen0", RegenHits);
        RegenMana = ini.GetInt(section, "Regen1", RegenMana);
        RegenStam = ini.GetInt(section, "Regen2", RegenStam);
        RegenFood = ini.GetInt(section, "Regen3", RegenFood);

        CombatFlags = ini.GetInt(section, "CombatFlags", CombatFlags);
        CombatDamageEra = ini.GetInt(section, "CombatDamageEra", CombatDamageEra);
        CombatHitChanceEra = ini.GetInt(section, "CombatHitChanceEra", CombatHitChanceEra);
        CombatSpeedEra = ini.GetInt(section, "CombatSpeedEra", CombatSpeedEra);
        CombatParryingEra = ini.GetInt(section, "CombatParryingEra", CombatParryingEra);
        SpeedScaleFactor = Math.Max(1, ini.GetInt(section, "SpeedScaleFactor", SpeedScaleFactor));
        CombatArcheryMovementDelay = ini.GetInt(section, "CombatArcheryMovementDelay", CombatArcheryMovementDelay);
        CombatMeleeMovementDelay = ini.GetInt(section, "CombatMeleeMovementDelay", CombatMeleeMovementDelay);
        ArcheryMinDist = ini.GetInt(section, "ArcheryMinDist", ArcheryMinDist);
        ArcheryMaxDist = ini.GetInt(section, "ArcheryMaxDist", ArcheryMaxDist);
        MagicFlags = ini.GetInt(section, "MagicFlags", MagicFlags);
        ReagentsRequired = ini.GetBool(section, "ReagentsRequired", ReagentsRequired);
        SpellbookRequired = ini.GetBool(section, "SpellbookRequired", SpellbookRequired);
        EquippedCast = ini.GetBool(section, "EquippedCast", EquippedCast);
        ReagentLossAbort = ini.GetBool(section, "ReagentLossAbort", ReagentLossAbort);
        ReagentLossFail = ini.GetBool(section, "ReagentLossFail", ReagentLossFail);
        ManaLossAbort = ini.GetBool(section, "ManaLossAbort", ManaLossAbort);
        ManaLossFail = ini.GetBool(section, "ManaLossFail", ManaLossFail);
        ManaLossPercent = ini.GetInt(section, "ManaLossPercent", ManaLossPercent);
        WalkBuffer = ini.GetInt(section, "WalkBuffer", WalkBuffer);
        WalkRegen = ini.GetInt(section, "WalkRegen", WalkRegen);
        MovementCreditEnabled = ini.GetBool(section, "MovementCreditEnabled", MovementCreditEnabled);
        MovementCreditBaseMs = ini.GetInt(section, "MovementCreditBaseMs", MovementCreditBaseMs);
        MovementCreditMaxMs = ini.GetInt(section, "MovementCreditMaxMs", MovementCreditMaxMs);
        MovementQueueCapacity = ini.GetInt(section, "MovementQueueCapacity", MovementQueueCapacity);
        WalkDelayFoot = ini.GetInt(section, "WalkDelayFoot", WalkDelayFoot);
        WalkDelayMount = ini.GetInt(section, "WalkDelayMount", WalkDelayMount);
        RunDelayFoot = ini.GetInt(section, "RunDelayFoot", RunDelayFoot);
        RunDelayMount = ini.GetInt(section, "RunDelayMount", RunDelayMount);
        SpeedHackDetectionEnabled = ini.GetBool(section, "SpeedHackDetectionEnabled", SpeedHackDetectionEnabled);
        if (double.TryParse(ini.GetValue(section, "SpeedHackRateThreshold"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double shrt))
            SpeedHackRateThreshold = shrt;
        SpeedHackBurstWindow = ini.GetInt(section, "SpeedHackBurstWindow", SpeedHackBurstWindow);
        SpeedHackHistorySize = ini.GetInt(section, "SpeedHackHistorySize", SpeedHackHistorySize);
        SpeedHackCooldownMs = ini.GetInt(section, "SpeedHackCooldownMs", SpeedHackCooldownMs);
        RttPingIntervalMs = ini.GetInt(section, "RttPingIntervalMs", RttPingIntervalMs);

        HitpointPercentOnRez = ini.GetInt(section, "HitpointPercentOnRez", HitpointPercentOnRez);
        PacketDeathAnimation = ini.GetInt(section, "PacketDeathAnimation", PacketDeathAnimation);
        CriminalTimer = ini.GetInt(section, "CriminalTimer", CriminalTimer);
        MurderMinCount = ini.GetInt(section, "MurderMinCount", MurderMinCount);
        PlayerKarmaEvil = ini.GetInt(section, "PlayerEvil", ini.GetInt(section, "PLAYEREVIL", PlayerKarmaEvil));
        PlayerKarmaNeutral = ini.GetInt(section, "PlayerNeutral", ini.GetInt(section, "PLAYERNEUTRAL", PlayerKarmaNeutral));
        MurderDecayTime = ini.GetInt(section, "MurderDecayTime", MurderDecayTime);
        LootingIsACrime = ini.GetBool(section, "LootingIsACrime", LootingIsACrime);
        AttackingIsACrime = ini.GetBool(section, "AttackingIsACrime", AttackingIsACrime);
        HelpingCriminalsIsACrime = ini.GetBool(section, "HelpingCriminalsIsACrime", HelpingCriminalsIsACrime);
        GuardLinger = ini.GetInt(section, "GuardLinger", GuardLinger);
        GuardsInstantKill = ini.GetBool(section, "GuardsInstantKill", GuardsInstantKill);
        GuardsOnMurderers = ini.GetBool(section, "GuardsOnMurderers", GuardsOnMurderers);
        SnoopCriminal = ini.GetBool(section, "SnoopCriminal", SnoopCriminal);
        NotoTimeout = ini.GetInt(section, "NotoTimeout", NotoTimeout);
        MonsterFight = ini.GetBool(section, "MonsterFight", MonsterFight);
        MonsterFear = ini.GetBool(section, "MonsterFear", MonsterFear);
        AdvancedLos = ini.GetInt(section, "AdvancedLos", AdvancedLos);
        NpcAi = GetIntOrHex(ini, section, "NpcAi",
            GetIntOrHex(ini, section, "NPCAI", NpcAi));
        NpcHealThreshold = ini.GetInt(section, "NpcHealThreshold", ini.GetInt(section, "NPCHealthreshold", NpcHealThreshold));
        NpcWanderLookAroundChance = ini.GetInt(section, "NpcWanderLookAroundChance",
            ini.GetInt(section, "NPCWanderLookAroundChance", NpcWanderLookAroundChance));
        // Notoriety name hues: a configured 0 means "use the built-in default" (hue 0
        // would render the overhead label as black, which is never wanted).
        ColorNotoGood = GetHue(ini, section, "ColorNotoGood", ColorNotoGood, zeroMeansDefault: true);
        ColorNotoGoodNpc = GetHue(ini, section, "ColorNotoGoodNPC", ColorNotoGoodNpc, zeroMeansDefault: true);
        ColorNotoGuildSame = GetHue(ini, section, "ColorNotoGuildSame", ColorNotoGuildSame, zeroMeansDefault: true);
        ColorNotoNeutral = GetHue(ini, section, "ColorNotoNeutral", ColorNotoNeutral, zeroMeansDefault: true);
        ColorNotoCriminal = GetHue(ini, section, "ColorNotoCriminal", ColorNotoCriminal, zeroMeansDefault: true);
        ColorNotoGuildWar = GetHue(ini, section, "ColorNotoGuildWar", ColorNotoGuildWar, zeroMeansDefault: true);
        ColorNotoEvil = GetHue(ini, section, "ColorNotoEvil", ColorNotoEvil, zeroMeansDefault: true);
        ColorNotoInvul = GetHue(ini, section, "ColorNotoInvul", ColorNotoInvul, zeroMeansDefault: true);
        ColorNotoInvulGameMaster = GetHue(ini, section, "ColorNotoInvulGameMaster", ColorNotoInvulGameMaster, zeroMeansDefault: true);
        ColorNotoDefault = GetHue(ini, section, "ColorNotoDefault", ColorNotoDefault, zeroMeansDefault: true);
        ColorInvisItem = GetHue(ini, section, "ColorInvisItem", ColorInvisItem);
        ColorInvis = GetHue(ini, section, "ColorInvis", ColorInvis);
        ColorInvisSpell = GetHue(ini, section, "ColorInvisSpell", ColorInvisSpell);
        ColorHidden = GetHue(ini, section, "ColorHidden", ColorHidden);
        PetsInheritNotoriety = GetIntOrHex(ini, section, "PetsInheritNotoriety", PetsInheritNotoriety);

        CorpseNpcDecay = ini.GetInt(section, "CorpseNpcDecay", CorpseNpcDecay);
        CorpsePlayerDecay = ini.GetInt(section, "CorpsePlayerDecay", CorpsePlayerDecay);
        HitPointPercentOnRez = ini.GetInt(section, "HitPointPercentOnRez", HitPointPercentOnRez);
        // Numeric mode, but an ini that still writes true/yes keeps meaning "on".
        DeadCannotSeeLiving = ini.GetBool(section, "DeadCannotSeeLiving", false)
            ? Math.Max(1, ini.GetInt(section, "DeadCannotSeeLiving", 1))
            : ini.GetInt(section, "DeadCannotSeeLiving", DeadCannotSeeLiving);

        MaxBaseSkill = ini.GetInt(section, "MaxBaseSkill", MaxBaseSkill);
        MaxFame = ini.GetInt(section, "MaxFame", MaxFame);
        MaxKarma = ini.GetInt(section, "MaxKarma", MaxKarma);
        MinKarma = ini.GetInt(section, "MinKarma", MinKarma);

        ContainerMaxItems = ini.GetInt(section, "ContainerMaxItems", ContainerMaxItems);
        TrainSkillPercent = Math.Clamp(ini.GetInt(section, "TrainSkillPercent", TrainSkillPercent), 0, 100);
        TrainSkillMax = Math.Max(0, ini.GetInt(section, "TrainSkillMax", TrainSkillMax));
        TrainSkillCost = Math.Max(0, ini.GetInt(section, "TrainSkillCost", TrainSkillCost));
        BankMaxItems = ini.GetInt(section, "BankMaxItems", BankMaxItems);
        BankMaxWeight = ini.GetInt(section, "BankMaxWeight", BankMaxWeight);
        ContainerMaxWeight = ini.GetInt(section, "ContainerMaxWeight", ContainerMaxWeight);
        ItemsMaxAmount = ini.GetInt(section, "ItemsMaxAmount", ItemsMaxAmount);

        MaxHousesPlayer = ini.GetInt(section, "MaxHousesPlayer", MaxHousesPlayer);
        MaxHousesAccount = ini.GetInt(section, "MaxHousesAccount", MaxHousesAccount);
        MaxShipsPlayer = ini.GetInt(section, "MaxShipsPlayer", MaxShipsPlayer);
        MaxShipsAccount = ini.GetInt(section, "MaxShipsAccount", MaxShipsAccount);

        NpcTrainCost = ini.GetInt(section, "NpcTrainCost", NpcTrainCost);
        NpcTrainMax = ini.GetInt(section, "NpcTrainMax", NpcTrainMax);
        NpcDistanceHear = ini.GetInt(section, "NpcDistanceHear", NpcDistanceHear);

        SpeechSelf = ini.GetValue(section, "SpeechSelf") ?? SpeechSelf;
        SpeechPet = ini.GetValue(section, "SpeechPet") ?? SpeechPet;
        EventsPet = ini.GetValue(section, "EventsPet") ?? EventsPet;
        EventsPlayer = ini.GetValue(section, "EventsPlayer") ?? EventsPlayer;
        CommandTrigger = ini.GetValue(section, "CommandTrigger") ?? CommandTrigger;
        EventsRegion = ini.GetValue(section, "EventsRegion") ?? EventsRegion;
        EventsItem = ini.GetValue(section, "EventsItem") ?? EventsItem;

        LightDay = ini.GetInt(section, "LightDay", LightDay);
        LightNight = ini.GetInt(section, "LightNight", LightNight);
        DungeonLight = ini.GetInt(section, "DungeonLight", DungeonLight);
        string? seasonModeRaw = ini.GetValue(section, "SeasonMode");
        if (!string.IsNullOrWhiteSpace(seasonModeRaw))
        {
            if (Enum.TryParse<SeasonMode>(seasonModeRaw, ignoreCase: true, out var parsedMode))
                SeasonMode = parsedMode;
            else if (int.TryParse(seasonModeRaw, out int parsedModeNum) &&
                     Enum.IsDefined(typeof(SeasonMode), parsedModeNum))
                SeasonMode = (SeasonMode)parsedModeNum;
        }
        SeasonDefault = (byte)Math.Clamp(ini.GetInt(section, "SeasonDefault", SeasonDefault), 0, 4);
        SeasonChangeIntervalMinutes = Math.Max(0,
            ini.GetInt(section, "SeasonChangeIntervalMinutes", SeasonChangeIntervalMinutes));

        DecayTimer = ini.GetInt(section, "DecayTimer", DecayTimer);

        ItemDurabilityEnabled = ini.GetBool(section, "ItemDurabilityEnabled", ItemDurabilityEnabled);
        ItemDurabilityLossChance = Math.Clamp(ini.GetInt(section, "ItemDurabilityLossChance", ItemDurabilityLossChance), 0, 100);
        ItemDurabilityLossMin = Math.Max(0, ini.GetInt(section, "ItemDurabilityLossMin", ItemDurabilityLossMin));
        ItemDurabilityLossMax = Math.Max(ItemDurabilityLossMin, ini.GetInt(section, "ItemDurabilityLossMax", ItemDurabilityLossMax));
        ItemBreakOnZeroHits = ini.GetBool(section, "ItemBreakOnZeroHits", ItemBreakOnZeroHits);
        ItemDefaultHits = Math.Max(1, ini.GetInt(section, "ItemDefaultHits", ItemDefaultHits));

        StateRecordingEnabled = ini.GetBool(section, "StateRecordingEnabled", StateRecordingEnabled);
        StateRecordPlayersOnly = ini.GetBool(section, "StateRecordPlayersOnly", StateRecordPlayersOnly);
        StateRecordMoveScanMs = Math.Max(500, ini.GetInt(section, "StateRecordMoveScanMs", StateRecordMoveScanMs));
        StateRecordSnapshotMs = Math.Max(5000, ini.GetInt(section, "StateRecordSnapshotMs", StateRecordSnapshotMs));
        MacroEnabled = ini.GetBool(section, "MacroEnabled", MacroEnabled);
        MacroMaxSteps = Math.Clamp(ini.GetInt(section, "MacroMaxSteps", MacroMaxSteps), 5, 200);
        MacroMaxLoopMinutes = Math.Clamp(ini.GetInt(section, "MacroMaxLoopMinutes", MacroMaxLoopMinutes), 1, 1440);

        FeatureT2A = ini.GetInt(section, "FeatureT2A", FeatureT2A);
        FeatureLBR = ini.GetInt(section, "FeatureLBR", FeatureLBR);
        FeatureAOS = ini.GetInt(section, "FeatureAOS", FeatureAOS);
        FeatureSE = ini.GetInt(section, "FeatureSE", FeatureSE);
        FeatureML = ini.GetInt(section, "FeatureML", FeatureML);
        FeatureKR  = ini.GetInt(section, "FeatureKR",  FeatureKR);
        FeatureSA  = ini.GetInt(section, "FeatureSA",  FeatureSA);
        FeatureTOL = ini.GetInt(section, "FeatureTOL", FeatureTOL);
        FeatureExtra = ini.GetInt(section, "FeatureExtra", FeatureExtra);
        RacialFlags = ini.GetInt(section, "RacialFlags", RacialFlags);
        AutoResDisp = ini.GetBool(section, "AutoResDisp", AutoResDisp);

        ToolTipMode = ini.GetInt(section, "ToolTipMode", ToolTipMode);
        ToolTipCache = ini.GetInt(section, "ToolTipCache", ToolTipCache);

        Experimental = ini.GetInt(section, "Experimental", Experimental);
        OptionFlags = ini.GetInt(section, "OptionFlags", OptionFlags);

        MaxPacketsPerTick = ini.GetInt(section, "MaxPacketsPerTick", MaxPacketsPerTick);
        FloodDetectionCount = ini.GetInt(section, "FloodDetectionCount", FloodDetectionCount);
        FloodDetectionWindowMs = ini.GetInt(section, "FloodDetectionWindowMs", FloodDetectionWindowMs);
        DeadSocketTime = ini.GetInt(section, "DeadSocketTime", DeadSocketTime);
        FreezeRestartTime = ini.GetInt(section, "FreezeRestartTime", FreezeRestartTime);
        NetworkThreads = ini.GetInt(section, "NetworkThreads", NetworkThreads);
        NetTTL = ini.GetInt(section, "NetTTL", NetTTL);
        MulticoreDeterminismDebug = ini.GetBool(section, "MulticoreDeterminismDebug", MulticoreDeterminismDebug);
        MulticoreDeterminismExpectedHash = ini.GetValue(section, "MulticoreDeterminismExpectedHash") ?? MulticoreDeterminismExpectedHash;
        MulticoreWorkerCount = ini.GetInt(section, "MulticoreWorkerCount", MulticoreWorkerCount);
        MulticorePhaseTimeoutMs = ini.GetInt(section, "MulticorePhaseTimeoutMs", MulticorePhaseTimeoutMs);
        TickSleepMode = ini.GetInt(section, "TickSleepMode", TickSleepMode);
        // ServerTickMs is canonical; TICKPERIOD is accepted as a legacy alias (the
        // classic Sphere key) when ServerTickMs is absent.
        ServerTickMs = Math.Clamp(
            ini.GetInt(section, "ServerTickMs", ini.GetInt(section, "TICKPERIOD", ServerTickMs)),
            20, 250);
        SlowTickWarnMs = Math.Max(0, ini.GetInt(section, "SlowTickWarnMs", SlowTickWarnMs));
        LoopStallWarnMs = Math.Max(0, ini.GetInt(section, "LoopStallWarnMs", LoopStallWarnMs));
        SlowPacketWarnMs = Math.Max(0, ini.GetInt(section, "SlowPacketWarnMs", SlowPacketWarnMs));

        LogMask = ini.GetInt(section, "LogMask", LogMask);
        DebugPackets = ini.GetBool(section, "DebugPackets", DebugPackets);
        ScriptDebug = ini.GetBool(section, "ScriptDebug", ScriptDebug);
        LogFileLevel = ini.GetValue(section, "LogFileLevel") ?? LogFileLevel;
        DebugPacketOpcodes = ini.GetValue(section, "DebugPacketOpcodes") ?? DebugPacketOpcodes;
        CommandPrefix = ini.GetValue(section, "CommandPrefix") ?? CommandPrefix;
        DefaultCommandLevel = ini.GetInt(section, "DefaultCommandLevel", DefaultCommandLevel);
        ChatFlags = ini.GetInt(section, "ChatFlags", ChatFlags);
        GenericSounds = ini.GetBool(section, "GenericSounds", GenericSounds);
        if (ini.GetBool(section, "HearAll", (LogMask & LogMaskPlayerSpeak) != 0))
            LogMask |= LogMaskPlayerSpeak;
        else
            LogMask &= ~LogMaskPlayerSpeak;
        MySQL = ini.GetInt(section, "MySQL", MySQL);
        MySQLHost = ini.GetValue(section, "MySQLHost") ?? MySQLHost;
        MySQLUser = ini.GetValue(section, "MySQLUser") ?? MySQLUser;
        MySQLPassword = ini.GetValue(section, "MySQLPassword") ?? MySQLPassword;
        MySQLDatabase = ini.GetValue(section, "MySQLDatabase") ?? MySQLDatabase;
        DistanceWhisper = ini.GetInt(section, "DistanceWhisper", DistanceWhisper);
        DistanceTalk = ini.GetInt(section, "DistanceTalk", DistanceTalk);
        DistanceYell = ini.GetInt(section, "DistanceYell", DistanceYell);

        UseHttp = ini.GetBool(section, "UseHttp", UseHttp);

        AdminPassword = ini.GetValue(section, "AdminPassword") ?? AdminPassword;
        AdminPanelPort = ini.GetInt(section, "AdminPanelPort", AdminPanelPort);

        SentryDsn = ini.GetValue(section, "SentryDsn") ?? SentryDsn;

        LoadDbConnections(ini);
    }

    /// <param name="zeroMeansDefault">When true, a configured value of 0 falls back to
    /// <paramref name="defaultValue"/> instead of being applied literally. Used for the
    /// notoriety name hues, where hue 0 renders as black text (never a desired colour) and
    /// "0" is documented as "use the built-in default".</param>
    private static ushort GetHue(IniParser ini, string section, string key, ushort defaultValue,
        bool zeroMeansDefault = false)
    {
        var raw = ini.GetValue(section, key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        if (!ushort.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ushort value))
            return defaultValue;

        return zeroMeansDefault && value == 0 ? defaultValue : value;
    }

    private static int GetIntOrHex(IniParser ini, string section, string key, int defaultValue)
    {
        var raw = ini.GetValue(section, key);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];
        else if (!(raw.Length > 1 && raw[0] == '0') &&
                 int.TryParse(raw, out int decimalValue))
            return decimalValue;

        return int.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out int hexValue)
            ? hexValue
            : defaultValue;
    }

    private void LoadDbConnections(IniParser ini)
    {
        // Synthesize a "default" connection from legacy [SPHERE] MySQL* keys
        if (MySQL != 0 && !string.IsNullOrWhiteSpace(MySQLHost))
        {
            DbConnections.Add(new DbConnectionConfig
            {
                Name = "default",
                Host = MySQLHost,
                User = MySQLUser,
                Password = MySQLPassword,
                Database = MySQLDatabase,
                AutoConnect = true
            });
        }

        // Parse [MYSQL name] and [SQLITE name] sections
        foreach (var kvp in ini.Sections)
        {
            bool isMySql = kvp.Key.StartsWith("MYSQL ", StringComparison.OrdinalIgnoreCase);
            bool isSqlite = kvp.Key.StartsWith("SQLITE ", StringComparison.OrdinalIgnoreCase);
            if (!isMySql && !isSqlite)
                continue;

            string connName = kvp.Key[(isMySql ? 6 : 7)..].Trim();
            if (string.IsNullOrWhiteSpace(connName)) continue;

            // Skip if legacy default already added with same name
            if (connName.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                DbConnections.Count > 0 && DbConnections[0].Name == "default")
            {
                // Override the legacy one with explicit section values
                ApplyDbSectionValues(DbConnections[0], ini, kvp.Key);
                if (isSqlite)
                    DbConnections[0].Provider = "Microsoft.Data.Sqlite";
                continue;
            }

            var cfg = new DbConnectionConfig { Name = connName };
            if (isSqlite)
                cfg.Provider = "Microsoft.Data.Sqlite";
            ApplyDbSectionValues(cfg, ini, kvp.Key);
            DbConnections.Add(cfg);
        }
    }

    private static void ApplyDbSectionValues(DbConnectionConfig cfg, IniParser ini, string section)
    {
        cfg.Provider = ini.GetValue(section, "Provider") ?? cfg.Provider;
        cfg.Host = ini.GetValue(section, "Host") ?? cfg.Host;
        cfg.Port = ini.GetInt(section, "Port", cfg.Port);
        cfg.User = ini.GetValue(section, "User") ?? cfg.User;
        cfg.Password = ini.GetValue(section, "Password") ?? cfg.Password;
        cfg.Database = ini.GetValue(section, "Database") ?? cfg.Database;
        cfg.KeepAlive = ini.GetBool(section, "KeepAlive", cfg.KeepAlive);
        cfg.ConnectTimeout = ini.GetInt(section, "ConnectTimeout", cfg.ConnectTimeout);
        cfg.ReadTimeout = ini.GetInt(section, "ReadTimeout", cfg.ReadTimeout);
        cfg.WriteTimeout = ini.GetInt(section, "WriteTimeout", cfg.WriteTimeout);
        cfg.UseThread = ini.GetBool(section, "UseThread", cfg.UseThread);
        cfg.AutoConnect = ini.GetBool(section, "AutoConnect", cfg.AutoConnect);
    }

    private void LoadMapDefinitions(IniParser ini, string section)
    {
        var maps = new List<MapDefinition>();
        for (int i = 0; i < 6; i++)
        {
            string? mapVal = ini.GetValue(section, $"Map{i}");
            if (mapVal == null) continue;

            string[] parts = mapVal.Split(',');
            if (parts.Length < 5) continue;

            maps.Add(new MapDefinition
            {
                MaxX = int.Parse(parts[0].Trim()),
                MaxY = int.Parse(parts[1].Trim()),
                SectorSize = int.Parse(parts[2].Trim()),
                MapReadId = int.Parse(parts[3].Trim()),
                MapSendId = int.Parse(parts[4].Trim())
            });
        }

        if (maps.Count > 0)
            Maps = maps.ToArray();
    }

    public List<string> Validate()
    {
        var warnings = new List<string>();
        if (ClientMax <= 0) warnings.Add($"ClientMax={ClientMax} — no clients can connect");
        if (ServPort <= 0 || ServPort > 65535) warnings.Add($"ServPort={ServPort} — invalid port");
        if (MulticorePhaseTimeoutMs < 100) warnings.Add($"MulticorePhaseTimeoutMs={MulticorePhaseTimeoutMs} — too aggressive, will constantly fallback to single-thread");
        if (NetTTL < 10) warnings.Add($"NetTTL={NetTTL} — very short idle timeout");
        if (SavePeriodMinutes < 0) warnings.Add($"SavePeriodMinutes={SavePeriodMinutes} — negative save period");
        if (AccApp != 0) warnings.Add($"AccApp={AccApp} — public shards should disable automatic account creation");
        if (DefaultCommandLevel > 0) warnings.Add($"DefaultCommandLevel={DefaultCommandLevel} — auto-created accounts may receive elevated commands");
        if (string.IsNullOrWhiteSpace(AdminPassword)) warnings.Add("AdminPassword is empty — admin panel/telnet must remain disabled");
        if (Md5Passwords) warnings.Add("Md5Passwords=1 — MD5 is legacy-only and weak for public shards");
        if (FloodDetectionCount <= 0) warnings.Add($"FloodDetectionCount={FloodDetectionCount} — flood detection disabled");
        if (FloodDetectionWindowMs < 1000) warnings.Add($"FloodDetectionWindowMs={FloodDetectionWindowMs} — too small, may cause false positives");
        if (ScriptEncoding.ToUpperInvariant() is not ("AUTO" or "UTF8" or "UTF-8" or "LEGACY" or "ANSI"))
            warnings.Add($"ScriptEncoding={ScriptEncoding} — expected AUTO, UTF8 or LEGACY; AUTO will be used");
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            _ = System.Text.Encoding.GetEncoding(ScriptLegacyCodePage);
        }
        catch (ArgumentException)
        {
            warnings.Add($"ScriptLegacyCodePage={ScriptLegacyCodePage} — unavailable; Windows-1252 will be used");
        }
        if (MovementCreditEnabled && MovementCreditBaseMs < 50) warnings.Add($"MovementCreditBaseMs={MovementCreditBaseMs} — too small, may reject legitimate movement");
        // WalkRegen is tokens-per-SECOND (a rate). A running player needs ~5 steps/s,
        // so a regen below that starves the walk-token bucket and rejects legitimate
        // steps (walk_buffer), causing client rubber-band/stutter. See WalkBufferMax.
        if (!MovementCreditEnabled && WalkRegen < 5) warnings.Add($"WalkRegen={WalkRegen} — tokens/sec below running speed (~5), will throttle legitimate movement to walk_buffer rejects (recommended ≥25)");
        if (!MovementCreditEnabled && WalkBuffer < 5) warnings.Add($"WalkBuffer={WalkBuffer} — walk-token bucket too small, drains after the first steps and stutters movement (recommended ≥25)");
        if (MovementQueueCapacity > 50) warnings.Add($"MovementQueueCapacity={MovementQueueCapacity} — large queue may mask speed hacks");
        if (WalkDelayFoot <= 0) warnings.Add($"WalkDelayFoot={WalkDelayFoot} — invalid movement delay");
        foreach (var map in Maps)
        {
            if (map.MaxX <= 0 || map.MaxY <= 0) warnings.Add($"Map {map.MapReadId}: MaxX={map.MaxX} MaxY={map.MaxY} — invalid dimensions");
            if (map.SectorSize <= 0) warnings.Add($"Map {map.MapReadId}: SectorSize={map.SectorSize} — invalid sector size");
        }
        return warnings;
    }
}

public sealed class MapDefinition
{
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    public int SectorSize { get; set; } = 64;
    public int MapReadId { get; set; }
    public int MapSendId { get; set; }

    public int SectorCountX => MaxX / SectorSize;
    public int SectorCountY => MaxY / SectorSize;
    public int TotalSectors => SectorCountX * SectorCountY;
}
