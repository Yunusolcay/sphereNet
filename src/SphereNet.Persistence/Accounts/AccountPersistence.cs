using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Formats;

namespace SphereNet.Persistence.Accounts;

/// <summary>
/// Account file save/load. Uses the same <see cref="ISaveWriter"/> /
/// <see cref="ISaveReader"/> abstraction as the world saver so format
/// selection (text / gzip / binary) applies uniformly to every persistent
/// file in the save directory. Written as <c>sphereaccu</c> + the format
/// extension; the loader auto-detects by probing all known extensions.
/// </summary>
public static class AccountPersistence
{
    private const string BaseName = "sphereaccu";

    /// <summary>Write every account into <c>sphereaccu.{ext}</c>. Stale files
    /// in the other formats are removed so changing SaveFormat doesn't leave
    /// two snapshots next to each other.</summary>
    public static int Save(AccountManager accounts, string dir, SaveFormat fmt, ILogger? log = null)
    {
        Directory.CreateDirectory(dir);

        string ext = SaveIO.ExtensionFor(fmt);
        string finalPath = Path.Combine(dir, BaseName + ext);
        string tmpPath = finalPath + ".tmp";

        int count = 0;
        using (var w = SaveIO.OpenWriter(tmpPath, fmt))
        {
            w.WriteHeaderComment("SphereNet Account File");
            w.WriteHeaderComment($"Saved at {DateTime.UtcNow:u}");
            foreach (var acc in accounts.GetAllAccounts())
            {
                WriteAccount(w, acc);
                count++;
            }
        }

        // Atomic promote: .tmp → final (overwrite).
        File.Move(tmpPath, finalPath, overwrite: true);

        // Drop stale files in other formats so the directory shows only one
        // canonical account snapshot. Also cleans up any ancient .bak left
        // by pre-refactor code.
        string[] knownExts = { ".scp", ".scp.gz", ".sbin", ".sbin.gz" };
        foreach (string otherExt in knownExts)
        {
            string candidate = Path.Combine(dir, BaseName + otherExt);
            if (!candidate.Equals(finalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                try { File.Delete(candidate); }
                catch (Exception ex) { log?.LogWarning(ex, "Could not remove stale account file {File}", candidate); }
            }

            string stale = Path.Combine(dir, BaseName + otherExt + ".bak");
            if (File.Exists(stale))
            {
                try { File.Delete(stale); } catch { /* best effort */ }
            }
        }

        log?.LogInformation("Saved {Count} accounts to {Path}", count, finalPath);
        return count;
    }

    /// <summary>Load accounts from <paramref name="dir"/>. Probes all known
    /// extensions; returns 0 if no account file is present (first-run).</summary>
    public static int Load(AccountManager accounts, string dir, ILogger? log = null)
    {
        // Priority order: prefer compressed binary (most-specific) when two
        // snapshots somehow co-exist, fall back to classic .scp last.
        string[] exts = { ".sbin.gz", ".sbin", ".scp.gz", ".scp" };
        foreach (string ext in exts)
        {
            string path = Path.Combine(dir, BaseName + ext);
            if (File.Exists(path))
                return LoadFile(accounts, path, log);
        }
        log?.LogWarning("No account file found in {Dir}", dir);
        return 0;
    }

    private static int LoadFile(AccountManager accounts, string path, ILogger? log)
    {
        int count = 0;
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            string? name = ExtractAccountName(section);
            if (name == null)
            {
                while (reader.NextProperty(out _, out _)) { /* skip */ }
                continue;
            }

            var account = new Account { Name = name };
            while (reader.NextProperty(out string key, out string value))
                ApplyProperty(account, key, value);

            accounts.AddLoaded(account);
            count++;
        }

        log?.LogInformation("Loaded {Count} accounts from {Path}", count, path);
        return count;
    }

    private static string? ExtractAccountName(string section)
    {
        if (section.StartsWith("ACCOUNT ", StringComparison.OrdinalIgnoreCase))
            return section[8..].Trim();
        if (section.Equals("EOF", StringComparison.OrdinalIgnoreCase))
            return null;
        // Sphere/Source-X bare format: [username] — accept any non-keyword section
        if (section.Length > 0 &&
            !section.StartsWith("WORLD", StringComparison.OrdinalIgnoreCase) &&
            !section.StartsWith("SPHERE", StringComparison.OrdinalIgnoreCase) &&
            !section.StartsWith("LIST ", StringComparison.OrdinalIgnoreCase) &&
            !section.StartsWith("GLOBALS", StringComparison.OrdinalIgnoreCase))
            return section;
        return null;
    }

    private static void WriteAccount(ISaveWriter w, Account acc)
    {
        w.BeginRecord(acc.Name);
        if (acc.PrivLevel > SphereNet.Core.Enums.PrivLevel.Player)
            w.WriteProperty("PLEVEL", acc.PrivLevel.ToString());
        if (acc.Priv != 0) w.WriteProperty("PRIV", $"0{acc.Priv:x}");
        if (acc.ResDisp != 0) w.WriteProperty("RESDISP", acc.ResDisp.ToString());
        w.WriteProperty("PASSWORD", acc.PasswordHash ?? string.Empty);
        if (acc.TotalConnectTime != 0) w.WriteProperty("TOTALCONNECTTIME", acc.TotalConnectTime.ToString());
        if (acc.LastCharUid.IsValid) w.WriteProperty("LASTCHARUID", $"0{acc.LastCharUid.Value:x}");

        for (int i = 0; i < 7; i++)
        {
            var charUid = acc.GetCharSlot(i);
            if (charUid.IsValid)
                w.WriteProperty($"CHARUID{i}", $"0{charUid.Value:x}");
        }

        if (acc.FirstConnectDate != default)
            w.WriteProperty("FIRSTCONNECTDATE", acc.FirstConnectDate.ToString("yyyy/MM/dd HH:mm:ss"));
        if (acc.LastLogin != default)
            w.WriteProperty("LASTCONNECTDATE", acc.LastLogin.ToString("yyyy/MM/dd HH:mm:ss"));
        if (!string.IsNullOrEmpty(acc.FirstIp)) w.WriteProperty("FIRSTIP", acc.FirstIp);
        if (!string.IsNullOrEmpty(acc.LastIp)) w.WriteProperty("LASTIP", acc.LastIp);
        if (!string.IsNullOrEmpty(acc.ChatName)) w.WriteProperty("CHATNAME", acc.ChatName);
        if (!string.IsNullOrEmpty(acc.Lang)) w.WriteProperty("LANG", acc.Lang);
        if (acc.MaxChars != 7) w.WriteProperty("MAXCHARS", acc.MaxChars.ToString());
        if (acc.IsBanned) w.WriteProperty("BANNED", "1");
        if (acc.Guest) w.WriteProperty("GUEST", "1");
        if (acc.Jail) w.WriteProperty("JAIL", "1");

        foreach (var tag in acc.Tags.GetAll())
            w.WriteProperty("TAG." + tag.Key, tag.Value);

        w.EndRecord();
    }

    private static void ApplyProperty(Account acc, string key, string val)
    {
        string upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "PASSWORD":
                if (val.Length == 32 && val.All(c => "0123456789abcdefABCDEF".Contains(c)))
                    acc.PasswordHash = val;
                else
                    acc.SetPassword(val);
                break;
            case "PLEVEL":
                if (int.TryParse(val, out int pl))
                    acc.PrivLevel = NormalizePrivLevel(pl);
                else if (Enum.TryParse<SphereNet.Core.Enums.PrivLevel>(val, true, out var plv))
                    acc.PrivLevel = plv;
                break;
            case "LASTCONNECTDATE":
                if (DateTime.TryParse(val, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var lcd))
                    acc.LastLogin = lcd;
                break;
            case "LASTCONNECTTIME":
                // Sphere last session length — store as TAG for round-trip
                acc.SetTag("LASTCONNECTTIME", val);
                break;
            case "MAXHOUSES":
                acc.SetTag("MaxHouses", val);
                break;
            case "LASTIP": acc.LastIp = val; break;
            case "TOTALCONNECTTIME":
                if (uint.TryParse(val, out uint ct))
                    acc.TotalConnectTime = ct;
                break;
            case "BANNED":
                acc.IsBanned = val == "1";
                break;
            case "CHATNAME": acc.ChatName = val; break;
            case "FIRSTCONNECTDATE":
                if (DateTime.TryParse(val, out var fcd)) acc.FirstConnectDate = fcd;
                break;
            case "FIRSTIP": acc.FirstIp = val; break;
            case "LASTCHARUID":
                if (TryParseHexOrDec(val, out uint lcuid))
                    acc.LastCharUid = new Serial(lcuid);
                break;
            case "MAXCHARS":
                if (int.TryParse(val, out int mc)) acc.MaxChars = mc;
                break;
            case "GUEST": acc.Guest = val == "1"; break;
            case "JAIL": acc.Jail = val == "1"; break;
            case "LANG": acc.Lang = val; break;
            case "PRIV":
                if (TryParseHexOrDec(val, out uint pv)) acc.Priv = pv;
                break;
            case "RESDISP":
                if (byte.TryParse(val, out byte rd)) acc.ResDisp = rd;
                break;
            default:
                if (upper.StartsWith("TAG.") && upper.Length > 4)
                {
                    acc.SetTag(upper[4..], val);
                }
                else if (upper == "CHARUID")
                {
                    // Sphere bare CHARUID=serial (no index) — append to next free slot
                    if (TryParseHexOrDec(val, out uint cs))
                    {
                        int free = acc.FindFreeSlot();
                        if (free >= 0) acc.SetCharSlot(free, new Serial(cs));
                    }
                }
                else if (upper.StartsWith("CHARUID") && upper.Length == 8
                    && int.TryParse(upper.AsSpan(7), out int slotIdx))
                {
                    if (TryParseHexOrDec(val, out uint charSerial))
                        acc.SetCharSlot(slotIdx, new Serial(charSerial));
                }
                break;
        }
    }

    private static Core.Enums.PrivLevel NormalizePrivLevel(int value)
    {
        if (value < (int)Core.Enums.PrivLevel.Guest) value = (int)Core.Enums.PrivLevel.Guest;
        if (value > (int)Core.Enums.PrivLevel.Owner) value = (int)Core.Enums.PrivLevel.Owner;
        return (Core.Enums.PrivLevel)value;
    }

    private static bool TryParseHexOrDec(string val, out uint result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (val.StartsWith('0') && val.Length > 1 && !val.Contains('.'))
            return uint.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);
        return uint.TryParse(val, out result);
    }
}
