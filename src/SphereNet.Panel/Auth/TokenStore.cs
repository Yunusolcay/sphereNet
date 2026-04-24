using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SphereNet.Panel.Auth;

public sealed class TokenStore
{
    private readonly ConcurrentDictionary<string, DateTime> _tokens = new();

    public string Create()
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = DateTime.UtcNow.AddHours(24);
        return token;
    }

    public bool Validate(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        return _tokens.TryGetValue(token, out var expiry) && expiry > DateTime.UtcNow;
    }

    public void Revoke(string token) => _tokens.TryRemove(token, out _);

    public void PurgeExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _tokens.Keys.ToList())
            if (_tokens.TryGetValue(key, out var exp) && exp <= now)
                _tokens.TryRemove(key, out _);
    }
}
