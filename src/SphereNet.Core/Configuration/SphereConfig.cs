namespace SphereNet.Core.Configuration;

public enum SeasonMode
{
    Auto = 0,
    Manual = 1,
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

    // Client
    public string ClientVersion { get; set; } = "4.0.2";
    public bool UseCrypt { get; set; } = true;
    public bool UseNoCrypt { get; set; }
    public int ClientMax { get; set; } = 256;
    public int ClientMaxIP { get; set; } = 16;
    public int ConnectingMax { get; set; } = 32;
    public int ClientLinger { get; set; } = 60;

    // Paths
    public string ScpFilesDir { get; set; } = "scripts/";
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
    public int BackupLevels { get; set; } = 10;
    public int SaveBackgroundMinutes { get; set; }
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
    public int MinCharDeleteTime { get; set; } = 7;

    // Game Mechanics
    public int GameMinuteLength { get; set; } = 60;
    public int SectorSleep { get; set; } = 7;
    public int MapViewSize { get; set; } = 18;
    public int MapViewSizeMax { get; set; } = 18;

    // Regen (seconds)
    public int RegenHits { get; set; } = 40;
    public int RegenStam { get; set; } = 20;
    public int RegenMana { get; set; } = 30;
    public int RegenFood { get; set; } = 60 * 60 * 24;

    // Combat
    public int CombatFlags { get; set; }
    public int CombatDamageEra { get; set; }
    public int CombatHitChanceEra { get; set; }
    public int CombatSpeedEra { get; set; }
    public int MagicFlags { get; set; }
    public bool ReagentsRequired { get; set; } = true;
    public int WalkBuffer { get; set; } = 75;
    public int WalkRegen { get; set; } = 25;

    // Crime & Notoriety
    public int CriminalTimer { get; set; } = 180;
    public int MurderMinCount { get; set; } = 5;
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

    // Death & Resurrection
    public int CorpseNpcDecay { get; set; } = 7;
    public int CorpsePlayerDecay { get; set; } = 7;
    public int HitPointPercentOnRez { get; set; } = 10;
    public bool DeadCannotSeeLiving { get; set; }

    // Stats
    public int MaxBaseSkill { get; set; } = 1200;
    public int MaxFame { get; set; } = 10000;
    public int MaxKarma { get; set; } = 10000;
    public int MinKarma { get; set; } = -10000;

    // Container
    public int ContainerMaxItems { get; set; } = 125;
    public int BankMaxItems { get; set; } = 125;
    public int BankMaxWeight { get; set; } = 1600;
    public int ContainerMaxWeight { get; set; } = 400;
    public int ItemsMaxAmount { get; set; } = 60000;

    // Housing
    public int MaxHousesPlayer { get; set; } = 1;
    public int MaxHousesAccount { get; set; } = 1;

    // NPC
    public int NpcTrainCost { get; set; } = 30;
    public int NpcTrainMax { get; set; } = 420;
    public int NpcDistanceHear { get; set; } = 16;

    // Light
    public int LightDay { get; set; }
    public int LightNight { get; set; } = 25;
    public int DungeonLight { get; set; } = 27;

    // Season
    public SeasonMode SeasonMode { get; set; } = SeasonMode.Auto;
    public byte SeasonDefault { get; set; } = 0;
    public int SeasonChangeIntervalMinutes { get; set; } = 30;

    // Misc
    public int DecayTimer { get; set; } = 30;

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

    // Tooltip
    public int ToolTipMode { get; set; } // 0=off (default), 1=AOS tooltips

    // Experimental / Option flags
    public int Experimental { get; set; }
    public int OptionFlags { get; set; }
    public const int OF_FileCommands = 0x0020;
    public bool HasFileCommands => (OptionFlags & OF_FileCommands) != 0;

    // Network
    public int MaxPacketsPerTick { get; set; } = 100;
    public int DeadSocketTime { get; set; } = 300;
    public int FreezeRestartTime { get; set; } = 60;
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

    // Main loop yield strategy between ticks.
    // 0 = spin   : Thread.SpinWait — lowest latency (<1ms), highest CPU usage.
    //              Best for dedicated servers with spare CPU cores.
    // 1 = sleep  : Thread.Sleep(1) — ~15ms latency on Windows, minimal CPU usage.
    //              Best for shared/low-end machines where CPU is precious.
    // 2 = hybrid : SpinWait + Sleep(0) — ~1ms latency, moderate CPU usage. (default)
    public int TickSleepMode { get; set; } = 2;

    // Sentry
    public string SentryDsn { get; set; } = "";

    // Logging
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

    // Source-X style MySQL settings (legacy single-connection)
    public int MySQL { get; set; }
    public string MySQLHost { get; set; } = "";
    public string MySQLUser { get; set; } = "";
    public string MySQLPassword { get; set; } = "";
    public string MySQLDatabase { get; set; } = "";

    // Multi-connection database settings
    public List<DbConnectionConfig> DbConnections { get; set; } = [];

    // Distances
    public int DistanceWhisper { get; set; } = 3;
    public int DistanceTalk { get; set; } = 18;
    public int DistanceYell { get; set; } = 60;

    // Web
    public bool UseHttp { get; set; }

    // Admin Panel
    public string AdminPassword { get; set; } = "";
    public int AdminPanelPort { get; set; } = 0; // 0 = ServPort + 3

    public void LoadFromIni(IniParser ini)
    {
        string section = "SPHERE";

        ServName = ini.GetValue(section, "ServName") ?? ServName;
        ServIP = ini.GetValue(section, "ServIP") ?? ServIP;
        ServPort = ini.GetInt(section, "ServPort", ServPort);
        AdminEmail = ini.GetValue(section, "AdminEmail") ?? AdminEmail;

        ClientVersion = ini.GetValue(section, "ClientVersion") ?? ClientVersion;
        UseCrypt = ini.GetBool(section, "UseCrypt", UseCrypt);
        UseNoCrypt = ini.GetBool(section, "UseNoCrypt", UseNoCrypt);
        ClientMax = ini.GetInt(section, "ClientMax", ClientMax);
        ClientMaxIP = ini.GetInt(section, "ClientMaxIP", ClientMaxIP);
        ConnectingMax = ini.GetInt(section, "ConnectingMax", ConnectingMax);
        ClientLinger = ini.GetInt(section, "ClientLinger", ClientLinger);

        ScpFilesDir = ini.GetValue(section, "ScpFiles") ?? ScpFilesDir;
        WorldSaveDir = ini.GetValue(section, "WorldSave") ?? WorldSaveDir;
        AccountDir = ini.GetValue(section, "AcctFiles") ?? AccountDir;
        MulFilesDir = ini.GetValue(section, "MulFiles") ?? MulFilesDir;

        LoadMapDefinitions(ini, section);

        SavePeriodMinutes = ini.GetInt(section, "SavePeriod", SavePeriodMinutes);
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
        SectorSleep = ini.GetInt(section, "SectorSleep", SectorSleep);
        MapViewSize = ini.GetInt(section, "MapViewSize", MapViewSize);
        MapViewSizeMax = ini.GetInt(section, "MapViewSizeMax", MapViewSizeMax);

        RegenHits = ini.GetInt(section, "Regen0", RegenHits);
        RegenStam = ini.GetInt(section, "Regen1", RegenStam);
        RegenMana = ini.GetInt(section, "Regen2", RegenMana);

        CombatFlags = ini.GetInt(section, "CombatFlags", CombatFlags);
        CombatDamageEra = ini.GetInt(section, "CombatDamageEra", CombatDamageEra);
        CombatHitChanceEra = ini.GetInt(section, "CombatHitChanceEra", CombatHitChanceEra);
        CombatSpeedEra = ini.GetInt(section, "CombatSpeedEra", CombatSpeedEra);
        MagicFlags = ini.GetInt(section, "MagicFlags", MagicFlags);
        ReagentsRequired = ini.GetBool(section, "ReagentsRequired", ReagentsRequired);
        WalkBuffer = ini.GetInt(section, "WalkBuffer", WalkBuffer);
        WalkRegen = ini.GetInt(section, "WalkRegen", WalkRegen);

        CriminalTimer = ini.GetInt(section, "CriminalTimer", CriminalTimer);
        MurderMinCount = ini.GetInt(section, "MurderMinCount", MurderMinCount);
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

        CorpseNpcDecay = ini.GetInt(section, "CorpseNpcDecay", CorpseNpcDecay);
        CorpsePlayerDecay = ini.GetInt(section, "CorpsePlayerDecay", CorpsePlayerDecay);
        HitPointPercentOnRez = ini.GetInt(section, "HitPointPercentOnRez", HitPointPercentOnRez);
        DeadCannotSeeLiving = ini.GetBool(section, "DeadCannotSeeLiving", DeadCannotSeeLiving);

        MaxBaseSkill = ini.GetInt(section, "MaxBaseSkill", MaxBaseSkill);
        MaxFame = ini.GetInt(section, "MaxFame", MaxFame);
        MaxKarma = ini.GetInt(section, "MaxKarma", MaxKarma);
        MinKarma = ini.GetInt(section, "MinKarma", MinKarma);

        ContainerMaxItems = ini.GetInt(section, "ContainerMaxItems", ContainerMaxItems);
        BankMaxItems = ini.GetInt(section, "BankMaxItems", BankMaxItems);
        BankMaxWeight = ini.GetInt(section, "BankMaxWeight", BankMaxWeight);
        ContainerMaxWeight = ini.GetInt(section, "ContainerMaxWeight", ContainerMaxWeight);
        ItemsMaxAmount = ini.GetInt(section, "ItemsMaxAmount", ItemsMaxAmount);

        MaxHousesPlayer = ini.GetInt(section, "MaxHousesPlayer", MaxHousesPlayer);
        MaxHousesAccount = ini.GetInt(section, "MaxHousesAccount", MaxHousesAccount);

        NpcTrainCost = ini.GetInt(section, "NpcTrainCost", NpcTrainCost);
        NpcTrainMax = ini.GetInt(section, "NpcTrainMax", NpcTrainMax);
        NpcDistanceHear = ini.GetInt(section, "NpcDistanceHear", NpcDistanceHear);

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

        FeatureT2A = ini.GetInt(section, "FeatureT2A", FeatureT2A);
        FeatureLBR = ini.GetInt(section, "FeatureLBR", FeatureLBR);
        FeatureAOS = ini.GetInt(section, "FeatureAOS", FeatureAOS);
        FeatureSE = ini.GetInt(section, "FeatureSE", FeatureSE);
        FeatureML = ini.GetInt(section, "FeatureML", FeatureML);
        FeatureKR  = ini.GetInt(section, "FeatureKR",  FeatureKR);
        FeatureSA  = ini.GetInt(section, "FeatureSA",  FeatureSA);
        FeatureTOL = ini.GetInt(section, "FeatureTOL", FeatureTOL);
        FeatureExtra = ini.GetInt(section, "FeatureExtra", FeatureExtra);

        ToolTipMode = ini.GetInt(section, "ToolTipMode", ToolTipMode);

        Experimental = ini.GetInt(section, "Experimental", Experimental);
        OptionFlags = ini.GetInt(section, "OptionFlags", OptionFlags);

        MaxPacketsPerTick = ini.GetInt(section, "MaxPacketsPerTick", MaxPacketsPerTick);
        DeadSocketTime = ini.GetInt(section, "DeadSocketTime", DeadSocketTime);
        FreezeRestartTime = ini.GetInt(section, "FreezeRestartTime", FreezeRestartTime);
        NetworkThreads = ini.GetInt(section, "NetworkThreads", NetworkThreads);
        NetTTL = ini.GetInt(section, "NetTTL", NetTTL);
        MulticoreDeterminismDebug = ini.GetBool(section, "MulticoreDeterminismDebug", MulticoreDeterminismDebug);
        MulticoreDeterminismExpectedHash = ini.GetValue(section, "MulticoreDeterminismExpectedHash") ?? MulticoreDeterminismExpectedHash;
        MulticoreWorkerCount = ini.GetInt(section, "MulticoreWorkerCount", MulticoreWorkerCount);
        MulticorePhaseTimeoutMs = ini.GetInt(section, "MulticorePhaseTimeoutMs", MulticorePhaseTimeoutMs);
        TickSleepMode = ini.GetInt(section, "TickSleepMode", TickSleepMode);

        LogMask = ini.GetInt(section, "LogMask", LogMask);
        DebugPackets = ini.GetBool(section, "DebugPackets", DebugPackets);
        ScriptDebug = ini.GetBool(section, "ScriptDebug", ScriptDebug);
        LogFileLevel = ini.GetValue(section, "LogFileLevel") ?? LogFileLevel;
        DebugPacketOpcodes = ini.GetValue(section, "DebugPacketOpcodes") ?? DebugPacketOpcodes;
        CommandPrefix = ini.GetValue(section, "CommandPrefix") ?? CommandPrefix;
        DefaultCommandLevel = ini.GetInt(section, "DefaultCommandLevel", DefaultCommandLevel);
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
