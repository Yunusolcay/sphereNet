namespace SphereNet.Panel;

/// <summary>
/// Bridge between the game server (Program.cs) and the panel web host.
/// Program.cs fills in all delegates; Panel code only calls them.
/// </summary>
public sealed class PanelContext
{
    // Server identity
    public string ServerName { get; set; } = "SphereNet";
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public string AdminPassword { get; set; } = "";

    // Paths — set by Program.cs at startup
    public string? IniPath { get; set; }
    public string? ScriptsPath { get; set; }

    // Live stats — called on each request / stats push
    public Func<ServerStats>? GetStats { get; set; }

    // Online players snapshot
    public Func<IReadOnlyList<PlayerInfo>>? GetOnlinePlayers { get; set; }

    // Account CRUD
    public Func<IReadOnlyList<AccountInfo>>? GetAllAccounts { get; set; }
    public Func<string, AccountInfo?>? GetAccount { get; set; }
    public Func<string, string, bool>? CreateAccount { get; set; }
    public Func<string, bool>? DeleteAccount { get; set; }
    public Action<string, bool>? SetAccountBanned { get; set; }
    public Action<string, string>? SetAccountPassword { get; set; }
    public Action<string, int>? SetAccountPrivLevel { get; set; }

    // Server commands
    public Action? OnSave { get; set; }
    public Action? OnShutdown { get; set; }
    public Action? OnResync { get; set; }
    public Action? OnGc { get; set; }
    public Action? OnRespawn { get; set; }
    public Action? OnRestock { get; set; }
    public Action<string>? OnBroadcast { get; set; }
    public Action? OnRestart { get; set; }
    public Action? StartServer { get; set; }

    // Raw command — returns response lines
    public Func<string, string[]>? ExecuteCommand { get; set; }

    // Server lifecycle state
    public Func<bool>? IsServerRunning { get; set; }

    // Debug toggles
    public Func<DebugState>? GetDebugState { get; set; }
    public Action<bool>? SetPacketDebug { get; set; }
    public Action<bool>? SetScriptDebug { get; set; }
}

// ---------------------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------------------

public record ServerStats(
    string ServerName,
    string Uptime,
    int UptimeSeconds,
    int OnlinePlayers,
    int TotalChars,
    int TotalItems,
    int TotalSectors,
    long TickCount,
    long MemoryMB,
    int Accounts,
    double CpuPercent = 0
);

public record PlayerInfo(
    string CharName,
    string AccountName,
    int MapId,
    int X,
    int Y,
    string Ip
);

public record AccountInfo(
    string Name,
    int PrivLevel,
    bool IsBanned,
    string LastIp,
    DateTime LastLogin,
    DateTime CreateDate,
    int CharCount
);

public record LogEntry(
    DateTime Timestamp,
    string Level,
    string Message,
    string Source
);

public record DebugState(bool PacketDebug, bool ScriptDebug);

public record ScriptFileInfo(
    string Name,
    string RelativePath,
    long SizeBytes,
    DateTime LastModified
);

public record SetupConfig(
    string ServerName,
    int ServPort,
    string AdminPassword,
    int AdminPanelPort,
    // Advanced
    int TickSleepMode = 2,
    bool DebugPackets = false,
    bool ScriptDebug = false
);
