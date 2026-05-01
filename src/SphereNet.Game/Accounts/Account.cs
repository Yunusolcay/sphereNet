using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Scripting.Variables;

namespace SphereNet.Game.Accounts;

/// <summary>
/// Player account. Maps to CAccount in Source-X.
/// Stores credentials, character references, and account-level properties.
/// </summary>
public sealed class Account : IScriptObj
{
    private string _name = "";
    private string _passwordHash = "";
    private PrivLevel _privLevel = PrivLevel.Guest;
    private readonly Serial[] _charSlots = new Serial[7];
    private int _charCount;
    private DateTime _lastLogin;
    private DateTime _createDate;
    private string _lastIp = "";
    private uint _totalConnectTime;
    private bool _isBanned;

    // New fields for wiki-complete account property support
    private readonly VarMap _tags = new();
    private string _chatName = "";
    private DateTime _firstConnectDate;
    private string _firstIp = "";
    private Serial _lastCharUid = Serial.Invalid;
    private int _maxChars = 7;
    private bool _guest;
    private bool _jail;
    private string _lang = "";
    private uint _priv;
    private byte _resDisp;

    public string Name { get => _name; set => _name = value; }
    public string PasswordHash { get => _passwordHash; set => _passwordHash = value; }
    public PrivLevel PrivLevel { get => _privLevel; set => _privLevel = value; }
    public int CharCount => _charCount;
    public DateTime LastLogin { get => _lastLogin; set => _lastLogin = value; }
    public DateTime CreateDate { get => _createDate; set => _createDate = value; }
    public string LastIp { get => _lastIp; set => _lastIp = value; }
    public uint TotalConnectTime { get => _totalConnectTime; set => _totalConnectTime = value; }
    public bool IsBanned { get => _isBanned; set => _isBanned = value; }
    public VarMap Tags => _tags;
    public string ChatName { get => _chatName; set => _chatName = value; }
    public DateTime FirstConnectDate { get => _firstConnectDate; set => _firstConnectDate = value; }
    public string FirstIp { get => _firstIp; set => _firstIp = value; }
    public Serial LastCharUid { get => _lastCharUid; set => _lastCharUid = value; }
    public int MaxChars { get => _maxChars; set => _maxChars = Math.Clamp(value, 1, 7); }
    public bool Guest { get => _guest; set => _guest = value; }
    public bool Jail { get => _jail; set => _jail = value; }
    public string Lang { get => _lang; set => _lang = value; }
    public uint Priv { get => _priv; set => _priv = value; }
    public byte ResDisp { get => _resDisp; set => _resDisp = value; }

    public Account()
    {
        Array.Fill(_charSlots, Serial.Invalid);
        _createDate = DateTime.UtcNow;
    }

    public Serial GetCharSlot(int index) =>
        index >= 0 && index < _charSlots.Length ? _charSlots[index] : Serial.Invalid;

    public bool SetCharSlot(int index, Serial charUid)
    {
        if (index < 0 || index >= _charSlots.Length) return false;
        _charSlots[index] = charUid;
        _charCount = _charSlots.Count(s => s.IsValid);
        return true;
    }

    public int FindFreeSlot()
    {
        for (int i = 0; i < _charSlots.Length; i++)
        {
            if (!_charSlots[i].IsValid) return i;
        }
        return -1;
    }

    public string[] GetCharNames(Func<Serial, string?> nameResolver)
    {
        var names = new List<string>();
        foreach (var uid in _charSlots)
        {
            if (uid.IsValid)
                names.Add(nameResolver(uid) ?? "?");
            else
                names.Add("");
        }
        return names.ToArray();
    }

    // TAG methods
    public void SetTag(string key, string value) => _tags.Set(key, value);
    public bool TryGetTag(string key, out string value)
    {
        var val = _tags.Get(key);
        value = val ?? "";
        return val != null;
    }
    public bool RemoveTag(string key) => _tags.Remove(key);

    public bool UseMd5Passwords { get; set; } = true;

    public bool CheckPassword(string password)
    {
        if (string.IsNullOrEmpty(_passwordHash)) return true;

        if (UseMd5Passwords)
        {
            string hash = ComputeMd5(password);
            return string.Equals(hash, _passwordHash, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(password, _passwordHash, StringComparison.Ordinal);
    }

    public void SetPassword(string password)
    {
        _passwordHash = UseMd5Passwords ? ComputeMd5(password) : password;
    }

    private static string ComputeMd5(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = System.Security.Cryptography.MD5.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public string GetName() => _name;

    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();

        // TAG.name
        if (upper.StartsWith("TAG."))
        {
            value = _tags.Get(key[4..]) ?? "0";
            return true;
        }

        switch (upper)
        {
            case "ACCOUNT":
            case "NAME": value = _name; return true;
            case "BLOCK":
            case "BANNED": value = _isBanned ? "1" : "0"; return true;
            case "CHARS":
            case "CHARCOUNT": value = _charCount.ToString(); return true;
            case "CHATNAME": value = _chatName; return true;
            case "CREATEDATE": value = _createDate.ToString("O"); return true;
            case "FIRSTCONNECTDATE": value = _firstConnectDate == default ? "" : _firstConnectDate.ToString("O"); return true;
            case "FIRSTIP": value = _firstIp; return true;
            case "GUEST": value = _guest ? "1" : "0"; return true;
            case "JAIL": value = _jail ? "1" : "0"; return true;
            case "LANG": value = _lang; return true;
            case "LASTCHARUID": value = _lastCharUid.IsValid ? $"0{_lastCharUid.Value:X8}" : "0"; return true;
            case "LASTCONNECTDATE":
            case "LASTLOGIN": value = _lastLogin.ToString("O"); return true;
            case "LASTIP": value = _lastIp; return true;
            case "MAXCHARS": value = _maxChars.ToString(); return true;
            case "PLEVEL": value = ((int)_privLevel).ToString(); return true;
            case "PRIV": value = _priv.ToString(); return true;
            case "RESDISP": value = _resDisp.ToString(); return true;
            case "TAGCOUNT": value = _tags.Count.ToString(); return true;
            case "TOTALCONNECTTIME": value = _totalConnectTime.ToString(); return true;
            default:
                // CHAR.n
                if (upper.StartsWith("CHAR.") && int.TryParse(upper.AsSpan(5), out int slotIdx))
                {
                    var uid = GetCharSlot(slotIdx);
                    value = uid.IsValid ? $"0{uid.Value:X8}" : "0";
                    return true;
                }
                return false;
        }
    }

    public bool TryExecuteCommand(string key, string args, ITextConsole source)
    {
        return false;
    }

    public bool TrySetProperty(string key, string value)
    {
        var upper = key.ToUpperInvariant();

        // TAG.name
        if (upper.StartsWith("TAG."))
        {
            _tags.Set(key[4..], value);
            return true;
        }

        switch (upper)
        {
            case "BLOCK":
            case "BANNED":
                _isBanned = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                return true;
            case "CHATNAME":
                _chatName = value;
                return true;
            case "FIRSTCONNECTDATE":
                if (DateTime.TryParse(value, out var fcd)) _firstConnectDate = fcd;
                return true;
            case "FIRSTIP":
                _firstIp = value;
                return true;
            case "GUEST":
                _guest = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                return true;
            case "JAIL":
                _jail = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                return true;
            case "LANG":
                _lang = value;
                return true;
            case "LASTCHARUID":
                if (TryParseHexOrDec(value, out uint lcuid))
                    _lastCharUid = new Serial(lcuid);
                return true;
            case "LASTCONNECTDATE":
            case "LASTLOGIN":
                if (DateTime.TryParse(value, out var lcd)) _lastLogin = lcd;
                return true;
            case "LASTIP":
                _lastIp = value;
                return true;
            case "MAXCHARS":
                if (int.TryParse(value, out int mc))
                    _maxChars = Math.Clamp(mc, 1, 7);
                return true;
            case "PASSWORD":
            case "NEWPASSWORD":
                SetPassword(value);
                return true;
            case "PLEVEL":
                if (int.TryParse(value, out int pl) && pl >= (int)PrivLevel.Guest && pl <= (int)PrivLevel.Owner)
                {
                    _privLevel = (PrivLevel)pl;
                    return true;
                }
                return false;
            case "PRIV":
                if (uint.TryParse(value, out uint pv)) _priv = pv;
                return true;
            case "RESDISP":
                if (byte.TryParse(value, out byte rd)) _resDisp = rd;
                return true;
            case "TOTALCONNECTTIME":
                if (uint.TryParse(value, out uint tct)) _totalConnectTime = tct;
                return true;
            default:
                return false;
        }
    }

    public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args)
    {
        return TriggerResult.Default;
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
