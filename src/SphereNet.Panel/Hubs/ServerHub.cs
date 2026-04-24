using Microsoft.AspNetCore.SignalR;
using SphereNet.Panel.Auth;

namespace SphereNet.Panel.Hubs;

/// <summary>
/// SignalR hub for real-time panel communication.
/// Clients authenticate via ?access_token= query parameter (standard SignalR WS auth).
/// </summary>
public sealed class ServerHub : Hub
{
    private readonly PanelContext _ctx;
    private readonly TokenStore _tokens;

    public ServerHub(PanelContext ctx, TokenStore tokens)
    {
        _ctx = ctx;
        _tokens = tokens;
    }

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Query["access_token"].ToString() ?? "";
        if (!_tokens.Validate(token))
        {
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync();
    }

    // Client → Server: execute a raw admin command, returns response lines
    public string[] ExecuteCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        return _ctx.ExecuteCommand?.Invoke(command) ?? [];
    }
}
