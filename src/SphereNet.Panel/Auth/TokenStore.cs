using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SphereNet.Panel.Auth;

public sealed class TokenStore
{
    private readonly ConcurrentDictionary<string, DateTime> _tokens = new();
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTime> _clock;

    public TokenStore(TimeSpan? lifetime = null, Func<DateTime>? clock = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromHours(24);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public int Count => _tokens.Count;

    public string Create()
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = _clock().Add(_lifetime);
        return token;
    }

    /// <summary>Raised whenever a token stops being usable — logout or expiry.
    /// Consumers use it to tear down anything the token is still holding open,
    /// such as a live SignalR connection.</summary>
    public event Action<string>? TokenInvalidated;

    public bool Validate(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_tokens.TryGetValue(token, out var expiry))
            return false;
        if (expiry > _clock())
            return true;

        Drop(token);
        return false;
    }

    public void Revoke(string token) => Drop(token);

    public void PurgeExpired()
    {
        var now = _clock();
        foreach (var pair in _tokens)
            if (pair.Value <= now)
                Drop(pair.Key);
    }

    /// <summary>Remove the token and announce it exactly once — the removal is the
    /// gate, so a racing caller cannot fire the event a second time.</summary>
    private void Drop(string token)
    {
        if (_tokens.TryRemove(token, out _))
            TokenInvalidated?.Invoke(token);
    }
}
