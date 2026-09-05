using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace SphereNet.Panel.Auth;

/// <summary>
/// Tracks which live SignalR connection belongs to which panel token so a token
/// that stops being valid can take its open connections down with it.
///
/// Validating only at handshake time is not enough: a WebSocket opened with a good
/// token kept executing admin commands after logout, and kept receiving the log and
/// stats broadcasts, because nothing closed it. Revocation and expiry now abort the
/// connection, which also removes it from <c>Clients.All</c>.
/// </summary>
public sealed class HubConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, HubCallerContext>> _byToken =
        new(StringComparer.Ordinal);

    public void Register(string token, HubCallerContext context)
    {
        if (string.IsNullOrEmpty(token)) return;
        _byToken.GetOrAdd(token, _ => new ConcurrentDictionary<string, HubCallerContext>(StringComparer.Ordinal))
                [context.ConnectionId] = context;
    }

    public void Unregister(string token, string connectionId)
    {
        if (string.IsNullOrEmpty(token)) return;
        if (!_byToken.TryGetValue(token, out var conns)) return;

        conns.TryRemove(connectionId, out _);
        if (conns.IsEmpty)
            _byToken.TryRemove(token, out _);
    }

    /// <summary>Abort every connection opened with <paramref name="token"/>.
    /// Returns how many were aborted.</summary>
    public int AbortToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return 0;
        if (!_byToken.TryRemove(token, out var conns)) return 0;

        int aborted = 0;
        foreach (var context in conns.Values)
        {
            // A connection can be tearing down already; that is a success here.
            try { context.Abort(); aborted++; }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        return aborted;
    }

    public int ConnectionCount => _byToken.Values.Sum(static c => c.Count);
}
