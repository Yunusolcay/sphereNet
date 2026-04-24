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
    private readonly object _lock = new();
    private volatile bool _intentionalStop;

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

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Connect IPC asynchronously — server opens the pipe, we connect after it's ready
            _ = ConnectWithRetryAsync(pipeName);

            RunningChanged?.Invoke(true);
        }
    }

    private async Task ConnectWithRetryAsync(string pipeName)
    {
        // Server needs a moment to open the pipe
        for (int i = 0; i < 15; i++)
        {
            try
            {
                await _ipc.ConnectAsync(pipeName);
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        _logSink.AddEntry(new LogEntry(DateTime.UtcNow, "ERR", "IPC: could not connect to server pipe.", "Host"));
    }

    public void Stop()
    {
        lock (_lock)
        {
            _intentionalStop = true;
            if (_process is { HasExited: false })
            {
                _ipc.SendCommand("shutdown");
                if (!_process.WaitForExit(8_000))
                    _process.Kill(entireProcessTree: true);
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

    private void OnOutput(object _, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        _logSink.AddEntry(ParseLine(e.Data));
    }

    private void OnExited(object? _, EventArgs e)
    {
        _ipc.Disconnect();
        if (!_intentionalStop)
        {
            RunningChanged?.Invoke(false);
            _logSink.AddEntry(new LogEntry(DateTime.UtcNow, "WRN",
                $"Server process exited unexpectedly (code {_process?.ExitCode}).", "Host"));
        }
    }

    private static LogEntry ParseLine(string line)
    {
        // Serilog console format: "[HH:mm:ss LVL] Message"
        var level = "INF";
        var message = line;
        if (line.Length > 14 && line[0] == '[')
        {
            var close = line.IndexOf(']');
            if (close > 1)
            {
                var header = line[1..close];
                var sp = header.LastIndexOf(' ');
                if (sp > 0) level = header[(sp + 1)..];
                message = close + 2 < line.Length ? line[(close + 2)..] : "";
            }
        }
        return new LogEntry(DateTime.UtcNow, level, message, "Server");
    }

    public void Dispose()
    {
        _intentionalStop = true;
        try { _process?.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
    }
}
