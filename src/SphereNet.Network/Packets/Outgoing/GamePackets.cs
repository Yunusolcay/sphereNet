using SphereNet.Network.Packets;

namespace SphereNet.Network.Packets.Outgoing;

/// <summary>0xA8 — Server list (login server → client).</summary>
public sealed class PacketServerList : PacketWriter
{
    private readonly string _serverName;
    private readonly byte _percentFull;
    private readonly byte _timezone;
    private readonly uint _ip;

    public PacketServerList(string serverName, uint ip, byte percentFull = 0, byte timezone = 0)
        : base(0xA8)
    {
        _serverName = serverName;
        _percentFull = percentFull;
        _timezone = timezone;
        _ip = ip;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(128);
        buf.WriteByte(0x5D); // system info flag
        ushort count = 1;
        buf.WriteUInt16(count);

        // Server entry
        buf.WriteUInt16(0); // server index
        buf.WriteAsciiFixed(_serverName, 32);
        buf.WriteByte(_percentFull);
        buf.WriteByte(_timezone);
        buf.WriteUInt32(_ip);

        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x82 — Login denied.</summary>
public sealed class PacketLoginDenied : PacketWriter
{
    private readonly byte _reason;

    public PacketLoginDenied(byte reason) : base(0x82)
    {
        _reason = reason;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(2);
        buf.WriteByte(_reason);
        return buf;
    }
}

/// <summary>0x8C — Relay to game server. Tells client where to connect for game login.</summary>
public sealed class PacketRelay : PacketWriter
{
    private readonly uint _ip;
    private readonly ushort _port;
    private readonly uint _authId;

    public PacketRelay(uint ip, ushort port, uint authId) : base(0x8C)
    {
        _ip = ip;
        _port = port;
        _authId = authId;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(11);
        buf.WriteUInt32(_ip);
        buf.WriteUInt16(_port);
        buf.WriteUInt32(_authId);
        return buf;
    }
}

/// <summary>0xA9 — Character list + starting cities.</summary>
public sealed class PacketCharList : PacketWriter
{
    private readonly string[] _charNames;
    private readonly int _maxChars;

    public PacketCharList(string[] charNames, int maxChars = 7) : base(0xA9)
    {
        _charNames = charNames;
        _maxChars = maxChars;
    }

    // Default starting city coordinates (map 0 = Felucca)
    private static readonly (string Name, string Area, int X, int Y, int Z, int Map, uint Cliloc)[] Cities =
    [
        ("Yew",         "The Empath Abbey",  543, 976, 0, 0, 1075074),
        ("Minoc",       "Minoc Mines",       2476, 413, 15, 0, 1075075),
        ("Britain",     "The Wayfarer's Inn", 1496, 1628, 10, 0, 1075072),
        ("Moonglow",    "The Scholar's Inn", 4400, 1168, 0, 0, 1075076),
        ("Trinsic",     "The Honorable Inn", 1856, 2728, 0, 0, 1075073),
        ("Magincia",    "The Great Horns Tavern", 3563, 2139, 0, 0, 1075077),
        ("Jhelom",      "The Mercenary Inn", 1376, 3752, 0, 0, 1075078),
        ("Skara Brae",  "The Falconer's Inn", 576, 2216, 0, 0, 1075079),
        ("Vesper",      "The Ironwood Inn",  2771, 976, 0, 0, 1075080),
    ];

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(2048);

        // Character count — always send maxChars slots (empty names for unused)
        byte charCount = (byte)_maxChars;
        buf.WriteByte(charCount);

        for (int i = 0; i < charCount; i++)
        {
            string name = i < _charNames.Length ? _charNames[i] : "";
            buf.WriteAsciiFixed(name, 30);
            buf.WriteAsciiFixed("", 30); // password (blank)
        }

        // Starting cities — new format for 7.0+ clients
        byte cityCount = (byte)Cities.Length;
        buf.WriteByte(cityCount);

        for (byte i = 0; i < cityCount; i++)
        {
            var city = Cities[i];
            buf.WriteByte(i);
            buf.WriteAsciiFixed(city.Name, 32);
            buf.WriteAsciiFixed(city.Area, 32);
            buf.WriteInt32(city.X);
            buf.WriteInt32(city.Y);
            buf.WriteInt32(city.Z);
            buf.WriteInt32(city.Map);
            buf.WriteUInt32(city.Cliloc);
            buf.WriteUInt32(0); // padding
        }

        // CharList flags (NOT the same as 0xB9 feature flags!)
        // 0x0008 = context menus (popup)
        // 0x0020 = AOS classes + tooltips
        // 0x0040 = 6th char slot
        // 0x0080 = SE classes (samurai/ninja)
        // 0x0100 = ML elven race
        // 0x1000 = 7th char slot
        // 0x4000 = new movement packets
        uint flags = 0x11E8; // popup + AOS + 6th slot + SE + ML + 7th slot
        buf.WriteUInt32(flags);

        // Last character slot index (-1 = none)
        buf.WriteInt16(-1);

        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x1B — Login confirm (enters world).</summary>
public sealed class PacketLoginConfirm : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _bodyId;
    private readonly short _x, _y;
    private readonly short _z;
    private readonly byte _dir;
    private readonly ushort _mapWidth, _mapHeight;

    public PacketLoginConfirm(uint serial, ushort bodyId, short x, short y, short z, byte dir, ushort mapW, ushort mapH)
        : base(0x1B)
    {
        _serial = serial; _bodyId = bodyId;
        _x = x; _y = y; _z = z; _dir = dir;
        _mapWidth = mapW; _mapHeight = mapH;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(37);
        buf.WriteUInt32(_serial);
        buf.WriteUInt32(0); // unknown
        buf.WriteUInt16(_bodyId);
        buf.WriteInt16(_x);
        buf.WriteInt16(_y);
        buf.WriteInt16(_z);
        buf.WriteByte(_dir);
        buf.WriteByte(0); // unknown
        buf.WriteUInt32(0xFFFFFFFF); // unknown (-1)
        buf.WriteUInt16(0); // unknown
        buf.WriteUInt16(0); // unknown
        buf.WriteUInt16(_mapWidth);
        buf.WriteUInt16(_mapHeight);
        buf.WriteBytes(new byte[6]); // padding
        return buf;
    }
}

/// <summary>0x55 — Login complete.</summary>
public sealed class PacketLoginComplete : PacketWriter
{
    public PacketLoginComplete() : base(0x55) { }

    public override PacketBuffer Build()
    {
        return CreateFixed(1);
    }
}

/// <summary>0x22 — Move acknowledgement.</summary>
public sealed class PacketMoveAck : PacketWriter
{
    private readonly byte _sequence;
    private readonly byte _notoriety;

    public PacketMoveAck(byte sequence, byte notoriety) : base(0x22)
    {
        _sequence = sequence;
        _notoriety = notoriety;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(3);
        buf.WriteByte(_sequence);
        buf.WriteByte(_notoriety);
        return buf;
    }
}

/// <summary>0x21 — Move rejected.</summary>
public sealed class PacketMoveReject : PacketWriter
{
    private readonly byte _sequence;
    private readonly short _x, _y;
    private readonly byte _dir;
    private readonly sbyte _z;

    public PacketMoveReject(byte sequence, short x, short y, sbyte z, byte dir) : base(0x21)
    {
        _sequence = sequence; _x = x; _y = y; _z = z; _dir = dir;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(8);
        buf.WriteByte(_sequence);
        buf.WriteInt16(_x);
        buf.WriteInt16(_y);
        buf.WriteByte(_dir);
        buf.WriteSByte(_z);
        return buf;
    }
}

/// <summary>0x1C — ASCII speech message.</summary>
public sealed class PacketSpeechOut : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _bodyId;
    private readonly byte _type;
    private readonly ushort _hue;
    private readonly ushort _font;
    private readonly string _name;
    private readonly string _text;

    public PacketSpeechOut(uint serial, ushort bodyId, byte type, ushort hue, ushort font, string name, string text)
        : base(0x1C)
    {
        _serial = serial; _bodyId = bodyId; _type = type;
        _hue = hue; _font = font; _name = name; _text = text;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64 + _text.Length);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_bodyId);
        buf.WriteByte(_type);
        buf.WriteUInt16(_hue);
        buf.WriteUInt16(_font);
        buf.WriteAsciiFixed(_name, 30);
        buf.WriteAsciiNull(_text);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xAE — Unicode speech message.</summary>
public sealed class PacketSpeechUnicodeOut : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _bodyId;
    private readonly byte _type;
    private readonly ushort _hue;
    private readonly ushort _font;
    private readonly string _lang;
    private readonly string _name;
    private readonly string _text;

    public PacketSpeechUnicodeOut(
        uint serial,
        ushort bodyId,
        byte type,
        ushort hue,
        ushort font,
        string lang,
        string name,
        string text) : base(0xAE)
    {
        _serial = serial;
        _bodyId = bodyId;
        _type = type;
        _hue = hue;
        _font = font;
        _lang = string.IsNullOrWhiteSpace(lang) ? "ENU" : lang;
        _name = name ?? "";
        _text = text ?? "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(80 + (_text.Length * 2));
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_bodyId);
        buf.WriteByte(_type);
        buf.WriteUInt16(_hue);
        buf.WriteUInt16(_font);
        buf.WriteAsciiFixed(_lang, 4);
        buf.WriteAsciiFixed(_name, 30);
        buf.WriteUnicodeNullBE(_text);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x72 — War mode response (confirm war/peace toggle to client).</summary>
public sealed class PacketWarModeResponse : PacketWriter
{
    private readonly bool _warMode;

    public PacketWarModeResponse(bool warMode) : base(0x72)
    {
        _warMode = warMode;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(5);
        buf.WriteByte(_warMode ? (byte)1 : (byte)0);
        buf.WriteByte(0); // unknown
        buf.WriteByte(0x32); // unknown (standard value)
        buf.WriteByte(0); // unknown
        return buf;
    }
}

/// <summary>0x77 — Update mobile position/direction for other characters (not self).</summary>
public sealed class PacketMobileMoving : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _bodyId;
    private readonly short _x, _y;
    private readonly sbyte _z;
    private readonly byte _dir;
    private readonly ushort _hue;
    private readonly byte _flags;
    private readonly byte _notoriety;

    public PacketMobileMoving(uint serial, ushort bodyId, short x, short y, sbyte z, byte dir, ushort hue, byte flags, byte notoriety)
        : base(0x77)
    {
        _serial = serial; _bodyId = bodyId;
        _x = x; _y = y; _z = z; _dir = dir;
        _hue = hue; _flags = flags; _notoriety = notoriety;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(17);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_bodyId);
        buf.WriteInt16(_x);
        buf.WriteInt16(_y);
        buf.WriteSByte(_z);
        buf.WriteByte(_dir);
        buf.WriteUInt16(_hue);
        buf.WriteByte(_flags);
        buf.WriteByte(_notoriety);
        return buf;
    }
}

/// <summary>0x20 — Draw player (update character appearance).</summary>
public sealed class PacketDrawPlayer : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _bodyId;
    private readonly ushort _hue;
    private readonly byte _flags;
    private readonly short _x, _y;
    private readonly sbyte _z;
    private readonly byte _dir;

    public PacketDrawPlayer(uint serial, ushort bodyId, ushort hue, byte flags, short x, short y, sbyte z, byte dir)
        : base(0x20)
    {
        _serial = serial; _bodyId = bodyId; _hue = hue;
        _flags = flags; _x = x; _y = y; _z = z; _dir = dir;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(19);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_bodyId);
        buf.WriteByte(0); // unknown
        buf.WriteUInt16(_hue);
        buf.WriteByte(_flags);
        buf.WriteInt16(_x);
        buf.WriteInt16(_y);
        buf.WriteUInt16(0); // unknown
        buf.WriteByte(_dir);
        buf.WriteSByte(_z);
        return buf;
    }
}

/// <summary>0x85 — Character delete result.</summary>
public sealed class PacketCharDeleteResult : PacketWriter
{
    private readonly byte _reason; // 0=success

    public PacketCharDeleteResult(byte reason = 0) : base(0x85)
    {
        _reason = reason;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(2);
        buf.WriteByte(_reason);
        return buf;
    }
}

/// <summary>0x98 — All names response (mobile name for single-click overhead).</summary>
public sealed class PacketAllNamesResponse : PacketWriter
{
    private readonly uint _serial;
    private readonly string _name;

    public PacketAllNamesResponse(uint serial, string name) : base(0x98)
    {
        _serial = serial;
        _name = name;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(40);
        buf.WriteUInt32(_serial);
        buf.WriteAsciiFixed(_name, 30);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x9A — Prompt request (ask client for text input).</summary>
public sealed class PacketPromptRequest : PacketWriter
{
    private readonly uint _serial;
    private readonly uint _promptId;
    private readonly uint _type; // 0=ASCII, 1=Unicode
    private readonly string _message;

    public PacketPromptRequest(uint serial, uint promptId, string message, uint type = 0) : base(0x9A)
    {
        _serial = serial;
        _promptId = promptId;
        _type = type;
        _message = message;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt32(_serial);
        buf.WriteUInt32(_promptId);
        buf.WriteUInt32(_type);
        buf.WriteAsciiNull(_message);
        buf.WriteLengthAt(1);
        return buf;
    }
}
