using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Host;
using SphereNet.Panel;
using SphereNet.Panel.Logging;

// ── Locate SphereNet.Server.exe ─────────────────────────────────────────────

var baseDir   = AppDomain.CurrentDomain.BaseDirectory;
var serverExe = Path.Combine(baseDir, "SphereNet.Server.exe");

// Development fallback: look for server exe in the solution bin folder
if (!File.Exists(serverExe))
{
    // Old per-project output layout (pre-unified bin)
    foreach (var cfg in new[] { "Debug", "Release" })
    {
        var devPath = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "src", "SphereNet.Server", "bin", cfg, "net9.0", "SphereNet.Server.exe"));
        if (File.Exists(devPath)) { serverExe = devPath; break; }
    }
}

if (!File.Exists(serverExe))
{
    Console.Error.WriteLine($"[Host] SphereNet.Server.exe not found near: {baseDir}");
    return 1;
}

// ── Read sphere.ini (minimal — just what Host needs) ────────────────────────

string? iniPath     = FindIni(baseDir) ?? FindIni(Path.GetDirectoryName(serverExe)!);
string? scriptsPath = null;
int     panelPort   = 2599;
string  adminPass   = "";
string  serverName  = "SphereNet";

if (iniPath != null)
{
    var parser = new IniParser();
    parser.Load(iniPath);
    panelPort   = parser.GetInt ("SPHERE", "AdminPanelPort", 0);
    adminPass   = parser.GetValue("SPHERE", "AdminPassword") ?? "";
    serverName  = parser.GetValue("SPHERE", "ServName") ?? "SphereNet";

    // Determine scripts path from config
    var scpDir = parser.GetValue("SPHERE", "ScpFilesDir");
    if (scpDir != null)
    {
        var full = Path.IsPathRooted(scpDir) ? scpDir
            : Path.Combine(Path.GetDirectoryName(iniPath)!, scpDir);
        if (Directory.Exists(full)) scriptsPath = full;
    }

    if (scriptsPath == null)
    {
        // Fallback: look for a "scripts" folder near sphere.ini
        var fallback = Path.Combine(Path.GetDirectoryName(iniPath)!, "scripts");
        if (Directory.Exists(fallback)) scriptsPath = fallback;
    }

    if (panelPort <= 0)
    {
        var servPort = parser.GetInt("SPHERE", "ServPort", 2593);
        panelPort = servPort + 3;
    }
}
else
{
    Console.WriteLine("[Host] sphere.ini not found — using defaults.");
}

// ── Wire up components ───────────────────────────────────────────────────────

var logSink = new PanelLogSink();
var ipc     = new IpcBridge();
var proc    = new ServerProcess(serverExe, ipc, logSink);

var panelCtx = HostPanelContext.Build(proc, ipc, iniPath, scriptsPath);
panelCtx.AdminPassword = adminPass;
panelCtx.ServerName    = serverName;

// Keep PanelContext AdminPassword in sync when setup wizard saves changes
proc.RunningChanged += running =>
{
    if (!running)
        Console.WriteLine("[Host] Server stopped.");
    else
        Console.WriteLine("[Host] Server started.");
};

// ── Start panel web server ───────────────────────────────────────────────────

using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
using var panelHost     = new PanelHost(panelCtx, panelPort, logSink, loggerFactory.CreateLogger("Panel"));
panelHost.Start();

var panelUrl = $"http://localhost:{panelPort}";
Console.WriteLine($"[Host] Panel → {panelUrl}");
Console.WriteLine("[Host] Press Ctrl+C to stop. Type commands to forward to the game server.");

// Auto-open browser after a brief delay so the panel is ready
_ = Task.Delay(1200).ContinueWith(_ => OpenBrowser(panelUrl));

// ── Ctrl+C / SIGTERM ─────────────────────────────────────────────────────────

var done = new ManualResetEventSlim(false);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[Host] Shutting down…");
    if (proc.IsRunning) proc.Stop();
    done.Set();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    if (proc.IsRunning) proc.Stop();
};

// Host console: forward typed commands to the game server via IPC
var consoleThread = new Thread(() =>
{
    Console.WriteLine("[Host] Ready. Type commands to send to the game server (e.g. 'status', 'save').");
    while (!done.IsSet)
    {
        try
        {
            var line = Console.ReadLine();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!proc.IsRunning) { Console.WriteLine("[Host] Server is not running."); continue; }
            var result = Task.Run(() => ipc.QueryAsync<string[]>("exec", new { raw = line }))
                            .GetAwaiter().GetResult();
            if (result is { Length: > 0 })
                foreach (var l in result) Console.WriteLine($"  {l}");
        }
        catch { break; }
    }
}) { IsBackground = true, Name = "HostConsole" };
consoleThread.Start();

done.Wait();
return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────

static void OpenBrowser(string url)
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* ignore if no default browser */ }
}

static string? FindIni(string dir)
{
    foreach (var candidate in new[] {
        Path.Combine(dir, "config", "sphere.ini"),
        Path.Combine(dir, "sphere.ini"),
    })
        if (File.Exists(candidate)) return candidate;
    return null;
}
