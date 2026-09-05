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

    public static bool Verify(string plaintext, string stored)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(plaintext))
            return false;

        if (stored.StartsWith(Sha256Prefix, StringComparison.Ordinal))
        {
            var sha = Sha256Prefix + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
            return string.Equals(sha, stored, StringComparison.Ordinal);
        }

        if (IsMd5Hex(stored))
            return string.Equals(Hash(plaintext), stored, StringComparison.OrdinalIgnoreCase);

        return stored == plaintext;
    }

    public static bool IsHashed(string stored) =>
        !string.IsNullOrEmpty(stored) &&
        (stored.StartsWith(Sha256Prefix, StringComparison.Ordinal) || IsMd5Hex(stored));

    public static bool NeedsUpgrade(string stored) =>
        !string.IsNullOrEmpty(stored) && !IsHashed(stored);

    private static bool IsMd5Hex(string value) =>
        value.Length == 32 && value.All(static c => "0123456789abcdefABCDEF".Contains(c));
}
