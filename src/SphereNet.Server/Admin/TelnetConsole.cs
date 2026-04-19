using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Game.World;

namespace SphereNet.Server.Admin;

/// <summary>
/// Telnet admin console. Maps to CTextConsole telnet in Source-X.
/// Allows GM-level commands via a simple telnet session.
/// </summary>
public sealed class TelnetConsole : IDisposable
{
    private Socket? _listener;
    private readonly List<TelnetSession> _sessions = [];
    private readonly ILogger _logger;
    private readonly AdminCommandProcessor _processor;
    private bool _running;

    public TelnetConsole(GameWorld world, AccountManager accounts, SphereConfig config,
        Func<int> getActiveConnections, ILogger logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _processor = new AdminCommandProcessor(world, accounts, config, getActiveConnections, loggerFactory);
    }

    public AdminCommandProcessor Processor => _processor;

    public bool Start(int port)
    {
        try
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
            _listener.Listen(4);
            _listener.Blocking = false;
            _running = true;
            _logger.LogInformation("Telnet admin console on port {Port}", port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start telnet console on port {Port}", port);
            return false;
        }
    }

    public void Tick()
    {
        if (!_running || _listener == null) return;

        // Accept
        try
        {
            if (_listener.Poll(0, SelectMode.SelectRead))
            {
                var socket = _listener.Accept();
                var session = new TelnetSession(socket, this);
                _sessions.Add(session);
                session.SendLine("SphereNet Admin Console");
                session.SendLine("Type 'help' for commands.");
                session.SendPrompt();
                _logger.LogInformation("Admin telnet session from {EP}", socket.RemoteEndPoint);
            }
        }
        catch (SocketException) { }

        // Process existing sessions
        for (int i = _sessions.Count - 1; i >= 0; i--)
        {
            var session = _sessions[i];
            if (!session.IsAlive)
            {
                session.Dispose();
                _sessions.RemoveAt(i);
                continue;
            }

            string? line = session.TryReadLine();
            if (line != null)
                ProcessCommand(session, line.Trim());
        }
    }

    private void ProcessCommand(TelnetSession session, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            session.SendPrompt();
            return;
        }

        bool keepOpen = _processor.ProcessCommand(input, session.SendLine);

        if (!keepOpen)
        {
            session.Close();
            return;
        }

        session.SendPrompt();
    }

    // Expose events from processor for backward compatibility
    public event Action? OnSaveRequested
    {
        add => _processor.OnSaveRequested += value;
        remove => _processor.OnSaveRequested -= value;
    }

    public event Action? OnShutdownRequested
    {
        add => _processor.OnShutdownRequested += value;
        remove => _processor.OnShutdownRequested -= value;
    }

    public event Action? OnResyncRequested
    {
        add => _processor.OnResyncRequested += value;
        remove => _processor.OnResyncRequested -= value;
    }

    public event Action<string>? OnBroadcast
    {
        add => _processor.OnBroadcast += value;
        remove => _processor.OnBroadcast -= value;
    }

    public event Action<string, Core.Enums.PrivLevel>? OnAccountPrivLevelChanged
    {
        add => _processor.OnAccountPrivLevelChanged += value;
        remove => _processor.OnAccountPrivLevelChanged -= value;
    }

    public event Action<Action<string>>? OnDebugToggleRequested
    {
        add => _processor.OnDebugToggleRequested += value;
        remove => _processor.OnDebugToggleRequested -= value;
    }

    public void Dispose()
    {
        _running = false;
        foreach (var s in _sessions) s.Dispose();
        _sessions.Clear();
        _listener?.Close();
        _listener = null;
    }
}

internal sealed class TelnetSession : IDisposable
{
    private readonly Socket _socket;
    private readonly TelnetConsole _console;
    private readonly byte[] _buffer = new byte[4096];
    private readonly StringBuilder _lineBuffer = new();
    private bool _closed;

    public bool IsAlive => !_closed && _socket.Connected;

    public TelnetSession(Socket socket, TelnetConsole console)
    {
        _socket = socket;
        _console = console;
        _socket.Blocking = false;
    }

    public string? TryReadLine()
    {
        if (_closed) return null;

        try
        {
            if (_socket.Available > 0)
            {
                int read = _socket.Receive(_buffer, 0, _buffer.Length, SocketFlags.None);
                if (read <= 0) { _closed = true; return null; }

                string text = Encoding.ASCII.GetString(_buffer, 0, read);
                _lineBuffer.Append(text);
            }
        }
        catch { _closed = true; return null; }

        string buf = _lineBuffer.ToString();
        int nlIdx = buf.IndexOf('\n');
        if (nlIdx < 0) return null;

        string line = buf[..nlIdx].TrimEnd('\r');
        _lineBuffer.Clear();
        if (nlIdx + 1 < buf.Length)
            _lineBuffer.Append(buf[(nlIdx + 1)..]);

        return line;
    }

    public void SendLine(string text)
    {
        if (_closed) return;
        try
        {
            byte[] data = Encoding.ASCII.GetBytes(text + "\r\n");
            _socket.Send(data, 0, data.Length, SocketFlags.None);
        }
        catch { _closed = true; }
    }

    public void SendPrompt()
    {
        if (_closed) return;
        try
        {
            byte[] data = Encoding.ASCII.GetBytes("> ");
            _socket.Send(data, 0, data.Length, SocketFlags.None);
        }
        catch { _closed = true; }
    }

    public void Close()
    {
        _closed = true;
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket.Close(); } catch { }
    }

    public void Dispose() => Close();
}
