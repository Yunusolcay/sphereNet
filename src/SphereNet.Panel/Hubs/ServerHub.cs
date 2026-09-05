using Microsoft.AspNetCore.SignalR;
using SphereNet.Panel.Auth;

namespace SphereNet.Panel.Hubs;

/// <summary>
/// SignalR hub for real-time panel communication.
/// Clients authenticate via ?access_token= query parameter (standard SignalR WS auth).
///
/// The handshake check is not the authorization boundary — every hub method
/// re-validates, because a connection outlives the token that opened it. Without
/// that, an admin command still ran on a WebSocket opened before logout.
/// </summary>
public sealed class ServerHub : Hub
{
    private const string TokenItemKey = "panel.token";

    private readonly PanelContext _ctx;
    private readonly TokenStore _tokens;
    private readonly HubConnectionRegistry _connections;

    public ServerHub(PanelContext ctx, TokenStore tokens, HubConnectionRegistry connections)
    {
        _ctx = ctx;
        _tokens = tokens;
        _connections = connections;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["access_token"].ToString() ?? "";
        if (!_tokens.Validate(token))
        {
            Context.Abort();
            return;
        }

        Context.Items[TokenItemKey] = token;
        _connections.Register(token, Context);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(TokenItemKey, out var stored) && stored is string token)
            _connections.Unregister(token, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    // Client → Server: execute a raw admin command, returns response lines
    public string[] ExecuteCommand(string command)
    {
        RequireValidToken();
        if (string.IsNullOrWhiteSpace(command)) return [];
        return _ctx.ExecuteCommand?.Invoke(command) ?? [];
    }

    /// <summary>Re-check the token this connection was opened with. A revoked or
    /// expired token drops the connection instead of merely failing the call, so it
    /// also stops receiving the log and stats broadcasts.</summary>
    private void RequireValidToken()
    {
        string token = Context.Items.TryGetValue(TokenItemKey, out var stored) && stored is string s ? s : "";
        if (_tokens.Validate(token))
            return;

        Context.Abort();
        throw new HubException("Unauthorized");
    }
}
