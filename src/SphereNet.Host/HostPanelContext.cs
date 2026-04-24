using SphereNet.Panel;

namespace SphereNet.Host;

/// <summary>
/// Builds a PanelContext whose delegates are backed by the IpcBridge and ServerProcess.
/// </summary>
public static class HostPanelContext
{
    public static PanelContext Build(ServerProcess proc, IpcBridge ipc, string? iniPath, string? scriptsPath)
    {
        return new PanelContext
        {
            IniPath     = iniPath,
            ScriptsPath = scriptsPath,

            // ── Lifecycle ──────────────────────────────────────────────────
            IsServerRunning = () => proc.IsRunning,
            StartServer  = () => proc.Start(),
            OnShutdown   = () => proc.Stop(),
            OnRestart    = () => proc.Restart(),

            // ── Stats ──────────────────────────────────────────────────────
            GetStats = () => ipc.LastStats ?? new ServerStats(
                "SphereNet", "—", 0, 0, 0, 0, 0, 0, 0, 0),

            // ── Players ────────────────────────────────────────────────────
            GetOnlinePlayers = () =>
                Run(() => ipc.QueryAsync<List<PlayerInfo>>("players")) ?? [],

            // ── Accounts ───────────────────────────────────────────────────
            GetAllAccounts = () =>
                Run(() => ipc.QueryAsync<List<AccountInfo>>("accounts")) ?? [],

            GetAccount = name =>
                Run(() => ipc.QueryAsync<AccountInfo>("account", new { name })),

            CreateAccount = (name, pass) =>
                Run(() => ipc.MutateAsync("account_create", new { name, pass })),

            DeleteAccount = name =>
                Run(() => ipc.MutateAsync("account_delete", new { name })),

            SetAccountBanned = (name, banned) =>
                RunVoid(() => ipc.MutateAsync("account_ban", new { name, banned })),

            SetAccountPassword = (name, pass) =>
                RunVoid(() => ipc.MutateAsync("account_set_pass", new { name, pass })),

            SetAccountPrivLevel = (name, level) =>
                RunVoid(() => ipc.MutateAsync("account_set_plevel", new { name, level })),

            // ── Commands ───────────────────────────────────────────────────
            OnSave    = () => ipc.SendCommand("save"),
            OnResync  = () => ipc.SendCommand("resync"),
            OnGc      = () => ipc.SendCommand("gc"),
            OnRespawn = () => ipc.SendCommand("respawn"),
            OnRestock = () => ipc.SendCommand("restock"),
            OnBroadcast = msg => ipc.SendBroadcast(msg),

            ExecuteCommand = cmd =>
                Run(() => ipc.QueryAsync<string[]>("exec", new { raw = cmd })) ?? [],

            // ── Debug ──────────────────────────────────────────────────────
            GetDebugState = () =>
                Run(() => ipc.QueryAsync<DebugState>("debug")) ?? new DebugState(false, false),

            SetPacketDebug = on =>
                RunVoid(() => ipc.MutateAsync("set_packet_debug", new { on })),

            SetScriptDebug = on =>
                RunVoid(() => ipc.MutateAsync("set_script_debug", new { on })),
        };
    }

    // Run async on a thread-pool thread to avoid SynchronizationContext deadlock
    private static T? Run<T>(Func<Task<T?>> fn)
    {
        try { return Task.Run(fn).GetAwaiter().GetResult(); }
        catch  { return default; }
    }

    private static void RunVoid(Func<Task> fn)
    {
        try { Task.Run(fn).GetAwaiter().GetResult(); }
        catch { }
    }
}
