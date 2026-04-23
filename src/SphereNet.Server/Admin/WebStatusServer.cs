using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.World;

namespace SphereNet.Server.Admin;

/// <summary>
/// Lightweight HTTP status endpoint for monitoring.
/// Serves JSON status on GET / and GET /status.
/// </summary>
public sealed class WebStatusServer : IDisposable
{
    private HttpListener? _listener;
    private readonly ILogger _logger;
    private readonly GameWorld _world;
    private readonly AccountManager _accounts;
    private readonly Func<int> _getActiveConnections;
    private readonly DateTime _startTime;
    private bool _running;

    public WebStatusServer(GameWorld world, AccountManager accounts,
        Func<int> getActiveConnections, ILogger logger)
    {
        _world = world;
        _accounts = accounts;
        _getActiveConnections = getActiveConnections;
        _logger = logger;
        _startTime = DateTime.UtcNow;
    }

    public bool Start(int port)
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _running = true;
            _logger.LogInformation("Web status endpoint on http://localhost:{Port}/", port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start web status on port {Port}", port);
            return false;
        }
    }

    public void Tick()
    {
        if (!_running || _listener == null) return;

        while (_listener.IsListening)
        {
            HttpListenerContext? ctx;
            try
            {
                var result = _listener.BeginGetContext(null, null);
                if (!result.AsyncWaitHandle.WaitOne(0)) return;
                ctx = _listener.EndGetContext(result);
            }
            catch { return; }

            try
            {
                HandleRequest(ctx);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web status request error");
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        var response = ctx.Response;

        string path = request.Url?.AbsolutePath ?? "/";

        if (path is "/" or "/status")
        {
            ServeStatus(response);
        }
        else if (path == "/health")
        {
            ServeHealth(response);
        }
        else
        {
            response.StatusCode = 404;
            byte[] body = Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body);
        }

        response.Close();
    }

    private void ServeStatus(HttpListenerResponse response)
    {
        var (chars, items, sectors) = _world.GetStats();
        var uptime = DateTime.UtcNow - _startTime;

        var status = new
        {
            server = "SphereNet",
            uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
            uptimeSeconds = (int)uptime.TotalSeconds,
            characters = chars,
            items = items,
            sectors = sectors,
            connections = _getActiveConnections(),
            accounts = _accounts.Count,
            ticks = _world.TickCount,
            memoryMB = GC.GetTotalMemory(false) / (1024 * 1024)
        };

        string json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
        byte[] body = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
    }

    private static void ServeHealth(HttpListenerResponse response)
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
        response.ContentType = "application/json";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); }
        catch (Exception ex) { _logger.LogDebug(ex, "WebStatus listener stop error"); }
        try { _listener?.Close(); }
        catch (Exception ex) { _logger.LogDebug(ex, "WebStatus listener close error"); }
    }
}
