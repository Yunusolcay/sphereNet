using System.Diagnostics;
using SphereNet.Panel;
using SphereNet.Panel.Logging;

namespace SphereNet.Host;

/// <summary>
/// Manages the SphereNet.Server child process lifecycle.
/// Captures stdout for log forwarding; signals IpcBridge to connect.
/// </summary>
public sealed class ServerProcess : IDisposable
{
    private readonly string _exePath;
    private readonly IpcBridge _ipc;
    private readonly PanelLogSink _logSink;
    private Process? _process;
    private CancellationTokenSource? _connectCts;
    private readonly object _lock = new();
    private volatile bool _intentionalStop;

    /// <summary>Ticks of the last line the child wrote. A shutting-down server keeps
    /// logging while it drains its background save and writes the final one, so this
    /// is the progress signal that separates "slow" from "hung".</summary>
    private long _lastOutputTicks = Environment.TickCount64;

    /// <summary>How long a silent, still-running child may stay unresponsive before
    /// it is treated as hung. Resets on every line it logs.</summary>
    public int ShutdownQuietMs { get; set; } = 20_000;

    /// <summary>Absolute ceiling on a graceful shutdown, however chatty the child is.
    /// Generous on purpose: a large world on slow storage can legitimately spend
    /// minutes finishing its shutdown save, and killing it there loses every change
    /// since the last periodic save.</summary>
    public int ShutdownTimeoutMs { get; set; } = 180_000;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Fired when the running state changes. True = started, False = stopped.</summary>
    public event Action<bool>? RunningChanged;

    public ServerProcess(string exePath, IpcBridge ipc, PanelLogSink logSink)
    {
        _exePath = exePath;
        _ipc = ipc;
        _logSink = logSink;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning) return;
            _intentionalStop = false;

            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();

            var pipeName = "sn-" + Guid.NewGuid().ToString("N")[..12];

            var psi = new ProcessStartInfo(_exePath)
            {
                Arguments            = $"--managed --pipe {pipeName}",
                UseShellExecute      = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                WorkingDirectory     = Path.GetDirectoryName(_exePath)!,
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutput;
            _process.ErrorDataReceived  += OnOutput;
            _process.Exited             += OnExited;

            Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Connect IPC asynchronously — server opens the pipe, we connect after it's ready
            _ = ConnectWithRetryAsync(pipeName, _connectCts.Token);

            RunningChanged?.Invoke(true);
        }
    }

    private async Task ConnectWithRetryAsync(string pipeName, CancellationToken ct)
    {
        // Server needs a moment to open the pipe
        for (int i = 0; i < 15; i++)
        {
            if (ct.IsCancellationRequested)
                return;
            try
            {
                await _ipc.ConnectAsync(pipeName);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                // Pipe not up yet (server still booting) — retry.
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }
        if (!ct.IsCancellationRequested)
            _logSink.AddEntry(new LogEntry(DateTime.UtcNow, "Error", "IPC: could not connect to server pipe.", "Host"));
    }

    public void Stop()
    {
        lock (_lock)
        {
            _intentionalStop = true;
            _connectCts?.Cancel();
            if (_process is { HasExited: false })
            {
                try
                {
                    _ipc.SendCommandAsync("shutdown").Wait(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex) when (ex is AggregateException or PanelBackendException)
                {
                    _logSink.AddEntry(new LogEntry(DateTime.UtcNow, "Warning",
                        $"IPC shutdown request failed: {ex.GetBaseException().Message}", "Host"));
                }
                if (!WaitForGracefulExit(_process))
                {
                    _logSink.AddEntry(new LogEntry(DateTime.UtcNow, "Error",
                        "Server did not exit; terminating it. World changes since the last " +
                        "save may be lost.", "Host"));
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5_000);
                }
            }
            _ipc.Disconnect();
            RunningChanged?.Invoke(false);
        }
    }

    public void Restart()
    {
        Task.Run(() =>
        {
            Stop();
            Thread.Sleep(2_000);
            Start();
        });
    }

    /// <summary>
    /// Wait for the child to finish on its own. A shutdown save is allowed to take
    /// as long as it needs as long as the child keeps reporting progress; only
    /// silence past <see cref="ShutdownQuietMs"/>, or the absolute
    /// <see cref="ShutdownTimeoutMs"/> ceiling, counts as stuck.
    /// Returns false when the caller has to escalate to a kill.
    /// </summary>
    private bool WaitForGracefulExit(Process process)
    {
        long start = Environment.TickCount64;
        bool announced = false;

        while (true)
        {
            if (process.WaitForExit(250))
                return true;

            long now = Environment.TickCount64;
            // Measure silence from the shutdown request, not from the child's last
            // log line. A quiet shard legitimately logs nothing for minutes, so
            // using the raw last-output stamp declared it hung on the very first
            // evaluation and killed it after 250ms - worse than the flat timeout
            // this replaced.
            long silenceSince = Math.Max(Interlocked.Read(ref _lastOutputTicks), start);

            var decision = ShutdownWaitPolicy.Evaluate(
                elapsedMs:      now - start,
                silentMs:       now - silenceSince,
                quietLimitMs:   ShutdownQuietMs,
                timeoutMs:      ShutdownTimeoutMs);

            if (decision == ShutdownWaitPolicy.Decision.Hung)
                return false;

            // Tell the operator why the panel is still spinning, once.
            if (!announced && now - start >= 5_000)
            {
                announced = true;
                _logSink.AddEntry(new LogEntry(DateTime.UtcNow, "Information",
                    "Waiting for the server to finish its shutdown save...", "Host"));
            }
        }
    }

    private void OnOutput(object _, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);
        var entry = ParseLine(e.Data);
        _logSink.AddEntry(entry);
        WriteToConsole(e.Data, entry.Level);
    }

    private static readonly object _consoleLock = new();

    private static void WriteToConsole(string raw, string level)
    {
        var color = level switch
        {
            "Fatal"       => ConsoleColor.Red,
            "Error"       => ConsoleColor.DarkRed,
            "Warning"     => ConsoleColor.Yellow,
            "Information" => ConsoleColor.Gray,
            "Debug"       => ConsoleColor.DarkGray,
            _             => ConsoleColor.DarkGray,
        };

        lock (_consoleLock)
        {
            // Console mirroring is best-effort and runs on a background
            // AsyncStreamReader thread (no surrounding try/catch). On a
            // server/VDS the console handle can become invalid mid-run when
            // the interactive session that owned it is disconnected or
            // logged off (e.g. an RDP session dropping overnight), or when
            // stdout is redirected. The color get/set and WriteLine then
            // throw IOException on this thread, which — being unhandled —
            // would terminate the entire Host process and tear down the
            // child game server with it. Swallow any console failure so log
            // forwarding can never crash the host.
            try
            {
                if (Console.IsOutputRedirected)
                {
                    Console.WriteLine(raw);
                    return;
                }
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(raw);
                Console.ForegroundColor = prev;
            }
            catch (IOException) { /* console handle gone — drop the line */ }
            catch (InvalidOperationException) { /* no console attached */ }
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (sender is Process exited && !ReferenceEquals(exited, _process))
            return;

        _connectCts?.Cancel();
        _ipc.Disconnect();
        if (!_intentionalStop)
        {
            RunningChanged?.Invoke(false);
            _logSink.AddEntry(new LogEntry(DateTime.UtcNow, "Warning",
                $"Server process exited unexpectedly (code {_process?.ExitCode}).", "Host"));
        }
    }

    private static LogEntry ParseLine(string line)
    {
        // Serilog console format: "[HH:mm:ss LVL] Message"
        var level = "Information";
        var message = line;
        if (line.Length > 14 && line[0] == '[')
        {
            var close = line.IndexOf(']');
            if (close > 1)
            {
                var header = line[1..close];
                var sp = header.LastIndexOf(' ');
                if (sp > 0)
                {
                    var code = header[(sp + 1)..];
                    level = code switch
                    {
                        "VRB" => "Verbose",
                        "DBG" => "Debug",
                        "INF" => "Information",
                        "WRN" => "Warning",
                        "ERR" => "Error",
                        "FTL" => "Fatal",
                        _     => "Information",
                    };
                }
                message = close + 2 < line.Length ? line[(close + 2)..] : "";
            }
        }
        return new LogEntry(DateTime.UtcNow, level, message, "Server");
    }

    public void Dispose()
    {
        _intentionalStop = true;
        _connectCts?.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _connectCts?.Dispose();
        _connectCts = null;
    }
}
