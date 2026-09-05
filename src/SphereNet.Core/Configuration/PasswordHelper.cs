using System.Security.Cryptography;
using System.Text;

namespace SphereNet.Core.Configuration;

public static class PasswordHelper
{
    private const string Sha256Prefix = "SHA256:";

    /// <summary>
    /// The value to persist for <paramref name="plaintext"/> under the shard's
    /// MD5PASSWORDS setting: an MD5 digest when hashing is on, the password verbatim
    /// when it is off. Mirrors Source-X CAccount::SetPassword, where MD5PASSWORDS=0
    /// really does mean plaintext storage.
    /// </summary>
    public static string StoreForm(string plaintext, bool useMd5) =>
        useMd5 ? Hash(plaintext) : plaintext;

    /// <summary>Hash using MD5 (bare uppercase hex) for Sphere account file compatibility.</summary>
    public static string Hash(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Check <paramref name="plaintext"/> against a stored value written under the
    /// shard's MD5PASSWORDS setting.
    ///
    /// The comparison form follows the SETTING, not the shape of the stored string
    /// (Source-X CAccount::CheckPassword branches on g_Cfg.m_fMd5Passwords). Guessing
    /// from the shape locked people out: with hashing off, a password that happens to
    /// be 32 hex characters - exactly what a password manager emits - was stored
    /// verbatim and then compared as if it were an MD5 digest, so the account could
    /// never log in again.
    ///
    /// The one deliberate addition is migration: with hashing ON, a stored value that
    /// is not a hash at all is a classic plaintext account file, and is accepted once
    /// so AccountManager can upgrade it on the way through.
    /// </summary>
    public static bool Verify(string plaintext, string stored, bool useMd5)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(plaintext))
            return false;

        // Plaintext mode interprets NOTHING: the value was stored verbatim, so it is
        // compared verbatim. Any shape test here is a lockout waiting to happen - a
        // password that merely looks like a digest, or merely starts with the
        // versioned prefix, is still just a password.
        if (!useMd5)
            return string.Equals(stored, plaintext, StringComparison.Ordinal);

        // SphereNet's own versioned form is self-describing.
        if (stored.StartsWith(Sha256Prefix, StringComparison.Ordinal))
        {
            var sha = Sha256Prefix + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
            return string.Equals(sha, stored, StringComparison.Ordinal);
        }

        if (string.Equals(Hash(plaintext), stored, StringComparison.OrdinalIgnoreCase))
            return true;

        // Legacy plaintext in a hashing shard: accept, then let the caller upgrade.
        return NeedsUpgrade(stored) && string.Equals(stored, plaintext, StringComparison.Ordinal);
    }

    /// <summary>Verify against a value that is always MD5 — the panel's own
    /// AdminPassword, which is not a Source-X account and has no MD5PASSWORDS
    /// setting of its own.</summary>
    public static bool Verify(string plaintext, string stored) =>
        Verify(plaintext, stored, useMd5: true);

    public static bool IsHashed(string stored) =>
        !string.IsNullOrEmpty(stored) &&
        (stored.StartsWith(Sha256Prefix, StringComparison.Ordinal) || IsMd5Hex(stored));

    public static bool NeedsUpgrade(string stored) =>
        !string.IsNullOrEmpty(stored) && !IsHashed(stored);

    private static bool IsMd5Hex(string value) =>
        value.Length == 32 && value.All(static c => "0123456789abcdefABCDEF".Contains(c));
}
