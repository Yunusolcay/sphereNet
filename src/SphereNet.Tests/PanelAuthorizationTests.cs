using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Panel;
using SphereNet.Panel.Logging;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Authorization tests that run against a real Kestrel host and a real WebSocket.
///
/// The engine suite cannot see these: the panel's protection is a middleware
/// closure plus a hub lifetime, not engine state. Two defects lived behind that
/// gap: a case-different URL reached a protected route unauthenticated, and a
/// WebSocket opened before logout kept executing admin commands afterwards.
/// </summary>
public sealed class PanelAuthorizationTests : IAsyncLifetime
{
    private const string AdminPassword = "correct horse";

    private PanelHost? _host;
    private HttpClient? _http;
    private int _port;
    private int _commandsExecuted;
    private string _iniPath = "";
    private string _tmpDir = "";

    public async Task InitializeAsync()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"sphnet_panel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _iniPath = Path.Combine(_tmpDir, "sphere.ini");
        File.WriteAllText(_iniPath,
            "[SPHERE]\r\nServName=Test\r\nServPort=2593\r\nAdminPassword=\r\n", Encoding.UTF8);

        var ctx = new PanelContext
        {
            ServerName = "Test",
            IniPath = _iniPath,
            ScriptsPath = Path.Combine(_tmpDir, "scripts"),
            AdminPassword = Core.Configuration.PasswordHelper.Hash(AdminPassword),
            IsServerRunning = () => true,
            // Every mutation the tests can reach is counted, never performed.
            ExecuteCommand = _ => { Interlocked.Increment(ref _commandsExecuted); return ["mock-only"]; },
            OnResync = () => true,
        };

        _port = FreePort();
        _host = new PanelHost(ctx, _port, new PanelLogSink(),
            LoggerFactory.Create(b => { }).CreateLogger("panel-test"));
        _host.Start();

        _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
        await WaitUntilListeningAsync();
    }

    public Task DisposeAsync()
    {
        _http?.Dispose();
        _host?.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    // --- F01: URL casing must not change the authorization scope -------------

    [Theory]
    [InlineData("/api/server/running")]
    [InlineData("/API/server/running")]
    [InlineData("/Api/Server/Running")]
    [InlineData("/aPi/server/running")]
    public async Task ProtectedGet_RejectsEveryCasingWithoutAToken(string path)
    {
        var res = await _http!.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/setup/apply")]
    [InlineData("/API/setup/apply")]
    [InlineData("/Api/Setup/Apply")]
    public async Task ProtectedPost_RejectsEveryCasingAndChangesNothing(string path)
    {
        string before = File.ReadAllText(_iniPath);

        var res = await _http!.PostAsJsonAsync(path, new
        {
            serverName = "pwned",
            servPort = 2593,
            adminPassword = "",
            adminPanelPort = 0,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(before, File.ReadAllText(_iniPath));
    }

    [Fact]
    public async Task AnonymousEndpointsStayReachable()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http!.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/setup/needed")).StatusCode);
    }

    [Fact]
    public async Task ValidTokenReachesTheProtectedRoute()
    {
        string token = await LoginAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/server/running");
        req.Headers.Add("Authorization", $"Bearer {token}");

        var res = await _http!.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // --- F08: setup validation ---------------------------------------------

    [Fact]
    public async Task SetupApply_RejectsAnEmptyAdminPassword()
    {
        string token = await LoginAsync();
        string before = File.ReadAllText(_iniPath);

        var res = await PostAsync(token, "/api/setup/apply", new
        {
            serverName = "Test",
            servPort = 2593,
            adminPassword = "",
            adminPanelPort = 0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(before, File.ReadAllText(_iniPath));
    }

    [Fact]
    public async Task SetupApply_TreatsTheMaskAsKeepTheCurrentPassword()
    {
        string token = await LoginAsync();

        var res = await PostAsync(token, "/api/setup/apply", new
        {
            serverName = "Renamed",
            servPort = 2593,
            adminPassword = "********",   // what /api/setup/config hands back
            adminPanelPort = 0,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // The original password must still work; storing the mask verbatim would
        // have locked the operator out of their own panel.
        Assert.False(string.IsNullOrEmpty(await LoginAsync()));
        Assert.DoesNotContain(
            "AdminPassword=" + Core.Configuration.PasswordHelper.Hash("********"),
            File.ReadAllText(_iniPath));
    }

    [Fact]
    public async Task SetupApply_RejectsAPortCollision()
    {
        string token = await LoginAsync();
        var res = await PostAsync(token, "/api/setup/apply", new
        {
            serverName = "Test",
            servPort = 2593,
            adminPassword = "another one",
            adminPanelPort = 2593,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // --- F02: logout must reach the open WebSocket --------------------------

    [Fact]
    public async Task LogoutTerminatesAnAlreadyOpenHubConnection()
    {
        string token = await LoginAsync();

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{_port}/hubs/server?access_token={token}"),
            CancellationToken.None);
        await SendFrameAsync(ws, "{\"protocol\":\"json\",\"version\":1}");
        await ReceiveFrameAsync(ws);   // handshake response

        // Authorized while the token is valid.
        int before = Volatile.Read(ref _commandsExecuted);
        await InvokeAsync(ws, "0", "ping");
        await ReceiveFrameAsync(ws);
        Assert.Equal(before + 1, Volatile.Read(ref _commandsExecuted));

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Authorization", $"Bearer {token}");
        Assert.Equal(HttpStatusCode.OK, (await _http!.SendAsync(logout)).StatusCode);

        // HTTP is rejected...
        using var afterHttp = new HttpRequestMessage(HttpMethod.Get, "/api/server/running");
        afterHttp.Headers.Add("Authorization", $"Bearer {token}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(afterHttp)).StatusCode);

        // ...and so is the connection that was already open.
        int afterLogout = Volatile.Read(ref _commandsExecuted);
        try
        {
            await InvokeAsync(ws, "1", "ping");
            await ReceiveFrameAsync(ws);
        }
        catch (WebSocketException) { /* aborted mid-flight is also a pass */ }
        catch (OperationCanceledException) { }

        Assert.Equal(afterLogout, Volatile.Read(ref _commandsExecuted));
    }

    [Fact]
    public async Task HubHandshakeStillRejectsAnUnknownToken()
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{_port}/hubs/server?access_token=nope"),
            CancellationToken.None);
        await SendFrameAsync(ws, "{\"protocol\":\"json\",\"version\":1}");

        int before = Volatile.Read(ref _commandsExecuted);
        try
        {
            await InvokeAsync(ws, "0", "ping");
            await ReceiveFrameAsync(ws);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }

        Assert.Equal(before, Volatile.Read(ref _commandsExecuted));
    }

    // --- helpers ------------------------------------------------------------

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task WaitUntilListeningAsync()
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                var res = await _http!.GetAsync("/health");
                if (res.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("panel host did not start listening");
    }

    private async Task<string> LoginAsync()
    {
        var res = await _http!.PostAsJsonAsync("/api/auth/login", new { password = AdminPassword });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.Token;
    }

    private async Task<HttpResponseMessage> PostAsync(string token, string path, object payload)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        req.Headers.Add("Authorization", $"Bearer {token}");
        return await _http!.SendAsync(req);
    }

    private static Task InvokeAsync(ClientWebSocket ws, string id, string command) =>
        SendFrameAsync(ws,
            $"{{\"type\":1,\"invocationId\":\"{id}\",\"target\":\"ExecuteCommand\",\"arguments\":[\"{command}\"]}}");

    /// <summary>SignalR JSON frames are terminated by the 0x1e record separator.</summary>
    private static Task SendFrameAsync(ClientWebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json + ""),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReceiveFrameAsync(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[8192];
        var res = await ws.ReceiveAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, res.Count);
    }

    private sealed record LoginResponse(string Token, string ServerName);
}
