using System.Globalization;
using SphereNet.Core.Enums;

namespace SphereNet.Core.Configuration;

/// <summary>
/// Encryption key configuration from sphereCrypt.ini.
/// Each entry maps a client version to encryption keys and type.
/// Parses both Source-X space-separated format and old key=value format.
/// </summary>
public sealed class CryptConfig
{
    private readonly List<CryptoClientKey> _keys = [];

    public IReadOnlyList<CryptoClientKey> Keys => _keys;

    public void Load(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        bool inSection = false;

        foreach (string rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.AsSpan().Trim();
            if (line.IsEmpty || line.StartsWith("//"))
                continue;

            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end > 1)
                {
                    var sectionName = line[1..end].Trim();
                    inSection = sectionName.Equals("SPHERECRYPT", StringComparison.OrdinalIgnoreCase);
                }
                continue;
            }

            if (!inSection) continue;

            int commentIdx = line.IndexOf("//");
            if (commentIdx >= 0)
                line = line[..commentIdx].TrimEnd();

            if (line.IsEmpty) continue;

            // Source-X format: "70011400 037062ADD 0ACCA227F ENC_TFISH"
            // Old format:      "40000=2D13A5FD 2D13A5FD ENC_BFISH"
            // Some entries use TAB as separator
            ReadOnlySpan<char> rest;
            ReadOnlySpan<char> verStr;

            int eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                verStr = line[..eqIdx].Trim();
                rest = line[(eqIdx + 1)..].Trim();
            }
            else
            {
                int spIdx = line.IndexOfAny(' ', '\t');
                if (spIdx <= 0) continue;
                verStr = line[..spIdx].Trim();
                rest = line[(spIdx + 1)..].TrimStart();
            }

            if (!uint.TryParse(verStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint clientVer))
            {
                if (!uint.TryParse(verStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out clientVer))
                    continue;
            }

            // Split rest into parts (space or tab separated)
            var partsStr = rest.ToString().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (partsStr.Length < 3) continue;

            var key1Str = partsStr[0].AsSpan();
            var key2Str = partsStr[1].AsSpan();
            var encStr = partsStr[2].AsSpan();

            if (!TryParseHexKey(key1Str, out uint key1)) continue;
            if (!TryParseHexKey(key2Str, out uint key2)) continue;

            EncryptionType encType = ParseEncType(encStr);

            _keys.Add(new CryptoClientKey(clientVer, key1, key2, encType));
        }
    }

    private static bool TryParseHexKey(ReadOnlySpan<char> text, out uint value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        else if (text.Length > 0 && text[0] == '0' && text.Length > 1)
            text = text[1..]; // strip leading 0 used in Source-X format

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static EncryptionType ParseEncType(ReadOnlySpan<char> text)
    {
        if (text.Equals("ENC_NONE", StringComparison.OrdinalIgnoreCase) || text.SequenceEqual("0"))
            return EncryptionType.None;
        if (text.Equals("ENC_BFISH", StringComparison.OrdinalIgnoreCase) || text.SequenceEqual("1"))
            return EncryptionType.Blowfish;
        if (text.Equals("ENC_BTFISH", StringComparison.OrdinalIgnoreCase) || text.SequenceEqual("2"))
            return EncryptionType.BlowfishTwofish;
        if (text.Equals("ENC_TFISH", StringComparison.OrdinalIgnoreCase) || text.SequenceEqual("3"))
            return EncryptionType.Twofish;
        if (text.Equals("ENC_LOGIN", StringComparison.OrdinalIgnoreCase) || text.SequenceEqual("4"))
            return EncryptionType.Login;
        return EncryptionType.None;
    }

    public CryptoClientKey? FindKey(uint clientVersion)
    {
        foreach (var key in _keys)
        {
            if (key.ClientVersion == clientVersion)
                return key;
        }
        return null;
    }
}

public sealed record CryptoClientKey(
    uint ClientVersion,
    uint Key1,
    uint Key2,
    EncryptionType EncType
);
