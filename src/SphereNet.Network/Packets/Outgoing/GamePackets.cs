using System.Collections.Generic;
using SphereNet.Network.Packets;

namespace SphereNet.Network.Packets.Outgoing;

/// <summary>A single 0xA8 server-list entry. Port is NOT part of the 0xA8 wire
/// format (the game port is delivered later in the 0x8C relay); it rides here only
/// so the select handler can pick the right relay target by list index.</summary>
public readonly record struct ServerListEntry(string Name, uint Ip, ushort Port,
    byte PercentFull = 0, byte Timezone = 0);

/// <summary>0xA8 — Server list (login server → client). Source-X send.cpp:3289 lists
/// self first, then config-defined extra shards, capped at MAX_SERVERS_LIST = 32.</summary>
public sealed class PacketServerList : PacketWriter
{
    private readonly IReadOnlyList<ServerListEntry> _servers;

    public PacketServerList(IReadOnlyList<ServerListEntry> servers) : base(0xA8)
    {
        _servers = servers;
    }

    // Backward-compatible single-entry convenience (used by tests / fallback).
    public PacketServerList(string serverName, uint ip, byte percentFull = 0, byte timezone = 0)
        : this(new[] { new ServerListEntry(serverName, ip, 0, percentFull, timezone) })
    {
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(128);
        buf.WriteByte(0x5D); // system info flag
        int count = _servers.Count < 32 ? _servers.Count : 32;
        buf.WriteUInt16((ushort)count);

        for (int i = 0; i < count; i++)
        {
            var s = _servers[i];
            buf.WriteUInt16((ushort)i); // server index
            buf.WriteAsciiFixed(s.Name, 32);
            buf.WriteByte(s.PercentFull);
            buf.WriteByte(s.Timezone);
            buf.WriteUInt32(s.Ip);
        }

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
    private readonly bool _newCharacterList;
    private readonly uint _flags;

    /// <summary>When false, the 0x20 (AOS tooltips) bit is stripped from char
    /// list flags so the client sends 0x09 single-click instead of 0xD6.</summary>
    public static bool AosTooltipsEnabled { get; set; } = true;

    public PacketCharList(string[] charNames, int maxChars = 7, bool newCharacterList = false,
        uint flags = 0x11E8)
        : base(0xA9)
    {
        _charNames = charNames;
        _maxChars = maxChars;
        _newCharacterList = newCharacterList;
        _flags = flags;
    }

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

    /// <summary>Spawn coordinates for the starting city the client picked, by its
    /// index into the list sent above. Lets character creation place the new
    /// character where the player chose instead of always at city 0.</summary>
    public static (int X, int Y, int Z, int Map)? GetCity(int index)
    {
        if (index < 0 || index >= Cities.Length)
            return null;
        var c = Cities[index];
        return (c.X, c.Y, c.Z, c.Map);
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(2048);

        byte charCount = (byte)_maxChars;
        buf.WriteByte(charCount);

        for (int i = 0; i < charCount; i++)
        {
            string name = i < _charNames.Length ? _charNames[i] : "";
            buf.WriteAsciiFixed(name, 30);
            buf.WriteAsciiFixed("", 30); // password (blank)
        }

        byte cityCount = (byte)Cities.Length;
        buf.WriteByte(cityCount);

        for (byte i = 0; i < cityCount; i++)
        {
            var city = Cities[i];
            buf.WriteByte(i);

            if (_newCharacterList)
            {
                buf.WriteAsciiFixed(city.Name, 32);
                buf.WriteAsciiFixed(city.Area, 32);
                buf.WriteInt32(city.X);
                buf.WriteInt32(city.Y);
                buf.WriteInt32(city.Z);
                buf.WriteInt32(city.Map);
                buf.WriteUInt32(city.Cliloc);
                buf.WriteUInt32(0); // padding
            }
            else
            {
                buf.WriteAsciiFixed(city.Name, 31);
                buf.WriteAsciiFixed(city.Area, 31);
            }
        }

        // CharList flags
        // 0x0008 = context menus (popup)
        // 0x0020 = AOS classes + tooltips
        // 0x0040 = 6th char slot
        // 0x0080 = SE classes (samurai/ninja)
        // 0x0100 = ML elven race
        // 0x1000 = 7th char slot
        // 0x4000 = new movement packets
        uint flags = _flags;
        if (!AosTooltipsEnabled)
            flags &= ~0x0020u;
        buf.WriteUInt32(flags);

        if (_newCharacterList)
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

/// <summary>0x2F - Combat swing/fight notification sent to the attacker.</summary>
public sealed class PacketSwing : PacketWriter
{
    private readonly uint _attackerSerial;
    private readonly uint _defenderSerial;

    public PacketSwing(uint attackerSerial, uint defenderSerial) : base(0x2F)
    {
        _attackerSerial = attackerSerial;
        _defenderSerial = defenderSerial;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(10);
        buf.WriteByte(0);
        buf.WriteUInt32(_attackerSerial);
        buf.WriteUInt32(_defenderSerial);
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

/// <summary>0x27 — pickup failed / drag cancel. Sending it while the client
/// is dragging an item cancels the drag cursor.</summary>
public sealed class PacketPickupFailed : PacketWriter
{
    private readonly byte _reason;

    public PacketPickupFailed(byte reason = 0) : base(0x27)
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

/// <summary>0xBF sub 0x04 — close a generic gump on the client, optionally
/// replaying a button response.</summary>
public sealed class PacketCloseGump : PacketWriter
{
    private readonly uint _gumpId;
    private readonly uint _buttonId;

    public PacketCloseGump(uint gumpId, uint buttonId = 0) : base(0xBF)
    {
        _gumpId = gumpId;
        _buttonId = buttonId;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(13);
        buf.WriteUInt16(0x0004);
        buf.WriteUInt32(_gumpId);
        buf.WriteUInt32(_buttonId);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xA5 — open web browser at the given URL on the client.</summary>
public sealed class PacketWebLink : PacketWriter
{
    private readonly string _url;

    public PacketWebLink(string url) : base(0xA5)
    {
        _url = url ?? "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(4 + _url.Length);
        buf.WriteAsciiNull(_url);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xAA - Attack target confirmation. Serial 0 means attack refused.</summary>
public sealed class PacketAttackResponse : PacketWriter
{
    private readonly uint _targetSerial;

    public PacketAttackResponse(uint targetSerial) : base(0xAA)
    {
        _targetSerial = targetSerial;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(5);
        buf.WriteUInt32(_targetSerial);
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

/// <summary>0x97 — Server-initiated walk (force client to walk in a direction).</summary>
public sealed class PacketWalkForce : PacketWriter
{
    private readonly byte _direction;

    public PacketWalkForce(byte direction) : base(0x97)
    {
        _direction = direction;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(2);
        buf.WriteByte(_direction);
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

/// <summary>0x93 — Book header (outgoing: displays book gump with title/author).
///
/// FIXED 99 bytes, with fixed-width strings: uid(4), writable(1), writable(1),
/// pages(2), title(60), author(30). Source-X writes exactly that
/// (PacketDisplayBook, send.cpp:2871) and the client reads exactly that — for
/// opcode 0x93 ClassicUO takes the 60/30 fixed fields and never looks for a
/// length field (PacketHandlers.OpenBook). It was being built as a VARIABLE packet
/// with a length header and length-prefixed strings, which is the layout of the
/// newer 0xD4 header: every field after the uid landed at the wrong offset.</summary>
public sealed class PacketBookHeaderOut : PacketWriter
{
    private const int TitleFieldLength = 60;
    private const int AuthorFieldLength = 30;

    private readonly uint _serial;
    private readonly bool _writable;
    private readonly ushort _pageCount;
    private readonly string _title;
    private readonly string _author;

    public PacketBookHeaderOut(uint serial, bool writable, ushort pageCount, string title, string author)
        : base(0x93)
    {
        _serial = serial;
        _writable = writable;
        _pageCount = pageCount;
        _title = title;
        _author = author;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(99);
        buf.WriteUInt32(_serial);
        // Upstream writes the writable flag twice; the client keeps the first and
        // skips the second on this opcode.
        buf.WriteByte((byte)(_writable ? 1 : 0));
        buf.WriteByte((byte)(_writable ? 1 : 0));
        buf.WriteUInt16(_pageCount);
        // Truncate one short of the field so the string is always NUL-terminated
        // inside it, as writeStringFixedASCII does.
        buf.WriteAsciiFixed(Clamp(_title, TitleFieldLength), TitleFieldLength);
        buf.WriteAsciiFixed(Clamp(_author, AuthorFieldLength), AuthorFieldLength);
        return buf;
    }

    private static string Clamp(string text, int field) =>
        text.Length > field - 1 ? text[..(field - 1)] : text;
}

/// <summary>0x66 — Book page content (outgoing: sends page text to client).</summary>
public sealed class PacketBookPageContent : PacketWriter
{
    private readonly uint _serial;
    private readonly (ushort PageNum, string[] Lines)[] _pages;

    public PacketBookPageContent(uint serial, (ushort PageNum, string[] Lines)[] pages)
        : base(0x66)
    {
        _serial = serial;
        _pages = pages;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(256);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16((ushort)_pages.Length);

        foreach (var (pageNum, lines) in _pages)
        {
            buf.WriteUInt16(pageNum);
            buf.WriteUInt16((ushort)lines.Length);
            foreach (var line in lines)
                buf.WriteAsciiNull(line);
        }

        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x28 — Drop reject (item cannot be dropped at target location).</summary>
public sealed class PacketDropReject : PacketWriter
{
    private readonly byte _reason; // 0=cannot lift, 5=rejected

    public PacketDropReject(byte reason = 5) : base(0x28)
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

/// <summary>
/// 0xF6 — Smooth boat movement (server → client).
/// ClassicUO expects serial, speed, move/facing dirs, and destination tile.
/// </summary>
public sealed class PacketBoatSmoothMove : PacketWriter
{
    private readonly uint _serial;
    private readonly byte _boatSpeed;
    private readonly byte _moveDir;
    private readonly byte _faceDir;
    private readonly short _x;
    private readonly short _y;
    private readonly ushort _z;

    private readonly (uint Serial, short X, short Y, sbyte Z)[] _objects;

    public PacketBoatSmoothMove(
        uint serial, byte boatSpeed, byte moveDir, byte faceDir,
        short x, short y, ushort z,
        (uint Serial, short X, short Y, sbyte Z)[]? objects = null)
        : base(0xF6)
    {
        _serial = serial;
        _boatSpeed = boatSpeed;
        _moveDir = moveDir;
        _faceDir = faceDir;
        _x = x;
        _y = y;
        _z = z;
        _objects = objects ?? [];
    }

    public override PacketBuffer Build()
    {
        // Source-X PacketMoveShip (send.cpp:5402): after the ship header comes
        // a u16 count and one {serial, x, y, z} entry per deck object
        // (passengers, components, cargo — the ship itself excluded). Smooth
        // clients ride each entry as a relative offset from the hull instead of
        // waiting for individual object resends.
        var buf = CreateVariable(18 + _objects.Length * 10);
        buf.WriteUInt32(_serial);
        buf.WriteByte(_boatSpeed);
        buf.WriteByte(_moveDir);
        buf.WriteByte(_faceDir);
        buf.WriteUInt16((ushort)_x);
        buf.WriteUInt16((ushort)_y);
        buf.WriteUInt16(_z);
        buf.WriteUInt16((ushort)_objects.Length);
        foreach (var (objSerial, ox, oy, oz) in _objects)
        {
            buf.WriteUInt32(objSerial);
            buf.WriteUInt16((ushort)ox);
            buf.WriteUInt16((ushort)oy);
            buf.WriteUInt16((ushort)(sbyte)oz);
        }
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x53 — Popup warning/error message (login reject, idle timeout, etc.).</summary>
public sealed class PacketPopupMessage : PacketWriter
{
    private readonly byte _reason;

    public PacketPopupMessage(byte reason) : base(0x53)
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
