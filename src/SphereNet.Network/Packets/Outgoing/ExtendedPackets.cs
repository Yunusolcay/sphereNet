using System.IO.Compression;
using System.Text;
using SphereNet.Core.Enums;
using SphereNet.Network.Packets;

namespace SphereNet.Network.Packets.Outgoing;

/// <summary>0x11 — Full status bar info (extended for AOS+).</summary>
public sealed class PacketStatusFull : PacketWriter
{
    private readonly uint _serial;
    private readonly string _name;
    private readonly short _hits, _maxHits;
    private readonly short _str, _dex, _intel;
    private readonly short _stam, _maxStam, _mana, _maxMana;
    private readonly int _gold;
    private readonly ushort _armorRating;
    private readonly ushort _weight;
    private readonly short _fame, _karma;
    private readonly byte _flags;
    private readonly byte _expansionLevel;
    private readonly short _statCap;
    private readonly byte _followers, _maxFollowers;
    private readonly short _resFire, _resCold, _resPoison, _resEnergy;
    private readonly short _luck;
    private readonly short _damageMin, _damageMax;
    private readonly ushort _maxWeight;

    public PacketStatusFull(uint serial, string name,
        short hits, short maxHits, short str, short dex, short intel,
        short stam, short maxStam, short mana, short maxMana,
        int gold, ushort armor, ushort weight,
        short fame, short karma, byte flags, byte expansionLevel,
        short statCap = 225, byte followers = 0, byte maxFollowers = 5,
        short resFire = 0, short resCold = 0, short resPoison = 0, short resEnergy = 0,
        short luck = 0, short damageMin = 0, short damageMax = 0,
        ushort maxWeight = 0)
        : base(0x11)
    {
        _serial = serial; _name = name;
        _hits = hits; _maxHits = maxHits;
        _str = str; _dex = dex; _intel = intel;
        _stam = stam; _maxStam = maxStam; _mana = mana; _maxMana = maxMana;
        _gold = gold; _armorRating = armor; _weight = weight;
        _fame = fame; _karma = karma; _flags = flags;
        _expansionLevel = expansionLevel;
        _statCap = statCap; _followers = followers; _maxFollowers = maxFollowers;
        _resFire = resFire; _resCold = resCold; _resPoison = resPoison; _resEnergy = resEnergy;
        _luck = luck; _damageMin = damageMin; _damageMax = damageMax;
        _maxWeight = maxWeight;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(200);
        buf.WriteUInt32(_serial);
        buf.WriteAsciiFixed(_name, 30);
        buf.WriteInt16(_hits);
        buf.WriteInt16(_maxHits);
        buf.WriteBool(false); // can rename
        buf.WriteByte(_expansionLevel);

        buf.WriteByte(0); // gender+race
        buf.WriteInt16(_str);
        buf.WriteInt16(_dex);
        buf.WriteInt16(_intel);
        buf.WriteInt16(_stam);
        buf.WriteInt16(_maxStam);
        buf.WriteInt16(_mana);
        buf.WriteInt16(_maxMana);
        buf.WriteInt32(_gold);
        buf.WriteUInt16(_armorRating);
        buf.WriteUInt16(_weight);

        if (_expansionLevel >= 3)
        {
            buf.WriteInt16(_statCap);
            buf.WriteByte(_followers);
            buf.WriteByte(_maxFollowers);
        }

        if (_expansionLevel >= 4)
        {
            buf.WriteInt16(_resFire);
            buf.WriteInt16(_resCold);
            buf.WriteInt16(_resPoison);
            buf.WriteInt16(_resEnergy);
            buf.WriteInt16(_luck);
            buf.WriteInt16(_damageMin);
            buf.WriteInt16(_damageMax);
            buf.WriteInt32(0); // tithing points
        }

        if (_expansionLevel >= 5)
        {
            buf.WriteUInt16(_maxWeight);
            buf.WriteByte(1); // race (1=human)
        }

        if (_expansionLevel >= 7)
        {
            buf.WriteInt16(0); // hit chance increase
            buf.WriteInt16(0); // swing speed increase
            buf.WriteInt16(0); // damage chance increase
            buf.WriteInt16(0); // lower reagent cost
            buf.WriteInt16(0); // hit points regen
            buf.WriteInt16(0); // stam regen
            buf.WriteInt16(0); // mana regen
            buf.WriteInt16(0); // reflect phys damage
            buf.WriteInt16(0); // enhance potions
            buf.WriteInt16(0); // defense chance increase
            buf.WriteInt16(0); // spell damage increase
            buf.WriteInt16(0); // faster cast recovery
            buf.WriteInt16(0); // faster casting
            buf.WriteInt16(0); // lower mana cost
            buf.WriteInt16(0); // strength increase
            buf.WriteInt16(0); // dex increase
            buf.WriteInt16(0); // int increase
            buf.WriteInt16(0); // hit points increase
            buf.WriteInt16(0); // stam increase
            buf.WriteInt16(0); // mana increase
            buf.WriteInt16(0); // max hit points increase
            buf.WriteInt16(0); // max stam increase
            buf.WriteInt16(0); // max mana increase
        }

        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x2E — Worn item (equip item on mobile).</summary>
public sealed class PacketWornItem : PacketWriter
{
    private readonly uint _itemSerial;
    private readonly ushort _itemId;
    private readonly byte _layer;
    private readonly uint _mobileSerial;
    private readonly ushort _hue;

    public PacketWornItem(uint itemSerial, ushort itemId, byte layer, uint mobileSerial, ushort hue)
        : base(0x2E)
    {
        _itemSerial = itemSerial; _itemId = itemId; _layer = layer;
        _mobileSerial = mobileSerial; _hue = hue;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(15);
        buf.WriteUInt32(_itemSerial);
        // Plain graphic — NO 0x8000 hue-flag bit. That high-bit convention
        // belongs to the 0x78 equipment list, not 0x2E: ClassicUO's EquipItem
        // handler reads this field raw (graphic = ReadUInt16BE() + ReadInt8())
        // and does NOT mask it, so 0x1F03|0x8000 = 0x9F03 became an invalid
        // graphic and the worn item vanished whenever it had a non-zero hue.
        // The hue travels in its own field below.
        buf.WriteUInt16(_itemId);
        buf.WriteByte(0); // graphic increment (added to graphic by the client)
        buf.WriteByte(_layer);
        buf.WriteUInt32(_mobileSerial);
        buf.WriteUInt16(_hue);
        return buf;
    }
}

/// <summary>0x78 — Draw object (mobile with equipment).</summary>
public sealed class PacketDrawObject : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _bodyId;
    private readonly short _x, _y;
    private readonly sbyte _z;
    private readonly byte _dir;
    private readonly ushort _hue;
    private readonly byte _flags;
    private readonly byte _notoriety;
    private readonly (uint Serial, ushort ItemId, byte Layer, ushort Hue)[] _equipment;
    private readonly bool _newMobileIncoming;

    public PacketDrawObject(uint serial, ushort bodyId, short x, short y, sbyte z,
        byte dir, ushort hue, byte flags, byte notoriety,
        (uint Serial, ushort ItemId, byte Layer, ushort Hue)[] equipment,
        bool newMobileIncoming = false)
        : base(0x78)
    {
        _serial = serial; _bodyId = bodyId; _x = x; _y = y; _z = z;
        _dir = dir; _hue = hue; _flags = flags; _notoriety = notoriety;
        _equipment = equipment;
        _newMobileIncoming = newMobileIncoming;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64 + _equipment.Length * 9);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_bodyId);
        buf.WriteInt16(_x);
        buf.WriteInt16(_y);
        buf.WriteSByte(_z);
        buf.WriteByte(_dir);
        buf.WriteUInt16(_hue);
        buf.WriteByte(_flags);
        buf.WriteByte(_notoriety);

        foreach (var (serial, itemId, layer, hue) in _equipment)
        {
            buf.WriteUInt32(serial);
            if (_newMobileIncoming)
            {
                buf.WriteUInt16((ushort)(itemId & 0xFFFF));
                buf.WriteByte(layer);
                buf.WriteUInt16(hue);
            }
            else
            {
                if (hue != 0)
                {
                    buf.WriteUInt16((ushort)((itemId & 0x7FFF) | 0x8000));
                    buf.WriteByte(layer);
                    buf.WriteUInt16(hue);
                }
                else
                {
                    buf.WriteUInt16((ushort)(itemId & 0x7FFF));
                    buf.WriteByte(layer);
                }
            }
        }
        buf.WriteUInt32(0); // terminator

        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x6C — Target cursor request.</summary>
public sealed class PacketTarget : PacketWriter
{
    private readonly byte _type; // 0=object, 1=location
    private readonly uint _targetId;
    private readonly byte _flags; // 0=neutral, 1=harmful, 2=beneficial

    public PacketTarget(byte type, uint targetId, byte flags = 0) : base(0x6C)
    {
        _type = type; _targetId = targetId; _flags = flags;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(19);
        buf.WriteByte(_type);
        buf.WriteUInt32(_targetId);
        buf.WriteByte(_flags);
        buf.WriteBytes(new byte[12]); // padding
        return buf;
    }
}

/// <summary>0x99 — multi-placement target: raises a ground cursor with the
/// house/ship footprint rendered as a ghost at the cursor (Source-X
/// PacketAddTarget XCMD_TargetMulti, send.cpp:1772). The offsets shift the
/// preview so its anchor matches where the server will place the multi;
/// HS+ clients take a trailing 32-bit hue.</summary>
public sealed class PacketTargetMulti : PacketWriter
{
    private readonly uint _cursorId;
    private readonly ushort _multiId;
    private readonly short _xOff, _yOff, _zOff;
    private readonly uint _hue;
    private readonly bool _includeHue;

    public PacketTargetMulti(uint cursorId, ushort multiId, short xOff, short yOff, short zOff,
        uint hue, bool includeHue) : base(0x99)
    {
        _cursorId = cursorId; _multiId = multiId;
        _xOff = xOff; _yOff = yOff; _zOff = zOff;
        _hue = hue; _includeHue = includeHue;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(_includeHue ? 30 : 26);
        buf.WriteByte(1);              // allow ground target
        buf.WriteUInt32(_cursorId);
        buf.WriteByte(0);              // flags: neutral
        buf.WriteBytes(new byte[11]);  // unused (Source-X writes 4+4+2+1 zeros)
        buf.WriteUInt16(_multiId);     // raw multi.mul index
        buf.WriteUInt16((ushort)_xOff);
        buf.WriteUInt16((ushort)_yOff);
        buf.WriteUInt16((ushort)_zOff);
        if (_includeHue)
            buf.WriteUInt32(_hue);
        return buf;
    }
}

/// <summary>0x23 - Drag animation.</summary>
public sealed class PacketDragAnimation : PacketWriter
{
    private readonly ushort _itemId;
    private readonly ushort _hue;
    private readonly ushort _amount;
    private readonly uint _sourceSerial;
    private readonly short _sourceX, _sourceY;
    private readonly sbyte _sourceZ;
    private readonly uint _targetSerial;
    private readonly short _targetX, _targetY;
    private readonly sbyte _targetZ;

    public PacketDragAnimation(ushort itemId, ushort hue, ushort amount,
        uint sourceSerial, short sourceX, short sourceY, sbyte sourceZ,
        uint targetSerial, short targetX, short targetY, sbyte targetZ) : base(0x23)
    {
        _itemId = itemId;
        _hue = hue;
        _amount = amount;
        _sourceSerial = sourceSerial;
        _sourceX = sourceX;
        _sourceY = sourceY;
        _sourceZ = sourceZ;
        _targetSerial = targetSerial;
        _targetX = targetX;
        _targetY = targetY;
        _targetZ = targetZ;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(26);
        buf.WriteUInt16(_itemId);
        buf.WriteByte(0);
        buf.WriteUInt16(_hue);
        buf.WriteUInt16(_amount == 0 ? (ushort)1 : _amount);
        buf.WriteUInt32(_sourceSerial);
        buf.WriteInt16(_sourceX);
        buf.WriteInt16(_sourceY);
        buf.WriteByte((byte)_sourceZ);
        buf.WriteUInt32(_targetSerial);
        buf.WriteInt16(_targetX);
        buf.WriteInt16(_targetY);
        buf.WriteByte((byte)_targetZ);
        return buf;
    }
}

/// <summary>0x54 — Play sound effect.</summary>
public sealed class PacketSound : PacketWriter
{
    private readonly byte _mode;
    private readonly ushort _soundId;
    private readonly short _x, _y;
    private readonly short _z;

    public PacketSound(ushort soundId, short x, short y, short z, byte mode = 1) : base(0x54)
    {
        _soundId = soundId; _x = x; _y = y; _z = z; _mode = mode;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(12);
        buf.WriteByte(_mode);
        buf.WriteUInt16(_soundId);
        buf.WriteUInt16(0); // volume
        buf.WriteInt16(_x);
        buf.WriteInt16(_y);
        buf.WriteInt16(_z);
        return buf;
    }
}

/// <summary>0x70 — Graphical effect (spell effects, explosions).</summary>
public sealed class PacketEffect : PacketWriter
{
    private readonly byte _type;
    private readonly uint _srcSerial, _dstSerial;
    private readonly ushort _effectId;
    private readonly short _srcX, _srcY, _srcZ;
    private readonly short _dstX, _dstY, _dstZ;
    private readonly byte _speed, _duration;
    private readonly bool _fixedDir, _explode;

    public PacketEffect(byte type, uint srcSerial, uint dstSerial, ushort effectId,
        short srcX, short srcY, short srcZ, short dstX, short dstY, short dstZ,
        byte speed, byte duration, bool fixedDir, bool explode) : base(0x70)
    {
        _type = type; _srcSerial = srcSerial; _dstSerial = dstSerial;
        _effectId = effectId;
        _srcX = srcX; _srcY = srcY; _srcZ = srcZ;
        _dstX = dstX; _dstY = dstY; _dstZ = dstZ;
        _speed = speed; _duration = duration;
        _fixedDir = fixedDir; _explode = explode;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(28);
        buf.WriteByte(_type);
        buf.WriteUInt32(_srcSerial);
        buf.WriteUInt32(_dstSerial);
        buf.WriteUInt16(_effectId);
        buf.WriteInt16(_srcX);
        buf.WriteInt16(_srcY);
        buf.WriteSByte((sbyte)_srcZ);
        buf.WriteInt16(_dstX);
        buf.WriteInt16(_dstY);
        buf.WriteSByte((sbyte)_dstZ);
        buf.WriteByte(_speed);
        buf.WriteByte(_duration);
        buf.WriteUInt16(0); // unknown
        buf.WriteBool(_fixedDir);
        buf.WriteBool(_explode);
        return buf;
    }
}

/// <summary>0xC0 - Hued graphical effect.</summary>
public sealed class PacketEffectHued : PacketWriter
{
    private readonly byte _type;
    private readonly uint _srcSerial, _dstSerial;
    private readonly ushort _effectId;
    private readonly short _srcX, _srcY, _srcZ;
    private readonly short _dstX, _dstY, _dstZ;
    private readonly byte _speed, _duration;
    private readonly bool _fixedDir, _explode;
    private readonly uint _hue, _renderMode;

    public PacketEffectHued(byte type, uint srcSerial, uint dstSerial, ushort effectId,
        short srcX, short srcY, short srcZ, short dstX, short dstY, short dstZ,
        byte speed, byte duration, bool fixedDir, bool explode, uint hue, uint renderMode) : base(0xC0)
    {
        _type = type; _srcSerial = srcSerial; _dstSerial = dstSerial;
        _effectId = effectId;
        _srcX = srcX; _srcY = srcY; _srcZ = srcZ;
        _dstX = dstX; _dstY = dstY; _dstZ = dstZ;
        _speed = speed; _duration = duration;
        _fixedDir = fixedDir; _explode = explode;
        _hue = hue; _renderMode = renderMode;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(36);
        WriteBaseEffect(buf, _type, _srcSerial, _dstSerial, _effectId,
            _srcX, _srcY, _srcZ, _dstX, _dstY, _dstZ,
            _speed, _duration, _fixedDir, _explode);
        WriteHuedEffect(buf, _hue, _renderMode);
        return buf;
    }

    internal static void WriteBaseEffect(PacketBuffer buf, byte type, uint srcSerial, uint dstSerial, ushort effectId,
        short srcX, short srcY, short srcZ, short dstX, short dstY, short dstZ,
        byte speed, byte duration, bool fixedDir, bool explode)
    {
        buf.WriteByte(type);
        buf.WriteUInt32(srcSerial);
        buf.WriteUInt32(dstSerial);
        buf.WriteUInt16(effectId);
        buf.WriteInt16(srcX);
        buf.WriteInt16(srcY);
        buf.WriteSByte((sbyte)srcZ);
        buf.WriteInt16(dstX);
        buf.WriteInt16(dstY);
        buf.WriteSByte((sbyte)dstZ);
        buf.WriteByte(speed);
        buf.WriteByte(duration);
        buf.WriteUInt16(0);
        buf.WriteBool(fixedDir);
        buf.WriteBool(explode);
    }

    internal static void WriteHuedEffect(PacketBuffer buf, uint hue, uint renderMode)
    {
        buf.WriteUInt32(hue != 0 ? hue - 1 : 0);
        buf.WriteUInt32(renderMode);
    }
}

/// <summary>0xC7 - Particle graphical effect.</summary>
public sealed class PacketEffectParticle : PacketWriter
{
    private readonly byte _type;
    private readonly uint _srcSerial, _dstSerial;
    private readonly ushort _effectId;
    private readonly short _srcX, _srcY, _srcZ;
    private readonly short _dstX, _dstY, _dstZ;
    private readonly byte _speed, _duration;
    private readonly bool _fixedDir, _explode;
    private readonly uint _hue, _renderMode;
    private readonly ushort _particleEffectId;
    private readonly uint _explodeId;
    private readonly ushort _explodeSound;
    private readonly uint _effectUid;
    private readonly byte _particleType;

    public PacketEffectParticle(byte type, uint srcSerial, uint dstSerial, ushort effectId,
        short srcX, short srcY, short srcZ, short dstX, short dstY, short dstZ,
        byte speed, byte duration, bool fixedDir, bool explode, uint hue, uint renderMode,
        ushort particleEffectId, uint explodeId, ushort explodeSound, uint effectUid, byte particleType) : base(0xC7)
    {
        _type = type; _srcSerial = srcSerial; _dstSerial = dstSerial;
        _effectId = effectId;
        _srcX = srcX; _srcY = srcY; _srcZ = srcZ;
        _dstX = dstX; _dstY = dstY; _dstZ = dstZ;
        _speed = speed; _duration = duration;
        _fixedDir = fixedDir; _explode = explode;
        _hue = hue; _renderMode = renderMode;
        _particleEffectId = particleEffectId;
        _explodeId = explodeId;
        _explodeSound = explodeSound;
        _effectUid = effectUid;
        _particleType = particleType;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(49);
        PacketEffectHued.WriteBaseEffect(buf, _type, _srcSerial, _dstSerial, _effectId,
            _srcX, _srcY, _srcZ, _dstX, _dstY, _dstZ,
            _speed, _duration, _fixedDir, _explode);
        PacketEffectHued.WriteHuedEffect(buf, _hue, _renderMode);
        buf.WriteUInt16(_particleEffectId);
        buf.WriteUInt16((ushort)_explodeId);
        buf.WriteUInt16(_explodeSound);
        buf.WriteUInt32(_effectUid);
        buf.WriteByte(_particleType == 0 ? (byte)0xFF : (byte)0x03);
        buf.WriteUInt16(0);
        return buf;
    }
}

/// <summary>0xB9 — Feature enable (expansion flags).
/// Pre-6.0.14.2: 3 bytes (ushort). Post-6.0.14.2: 5 bytes (uint).</summary>
public sealed class PacketFeatureEnable : PacketWriter
{
    private readonly uint _flags;
    private readonly bool _useExtendedFlags;

    public PacketFeatureEnable(uint flags, bool useExtendedFlags = true) : base(0xB9)
    {
        _flags = flags;
        _useExtendedFlags = useExtendedFlags;
    }

    public override PacketBuffer Build()
    {
        if (_useExtendedFlags)
        {
            var buf = CreateFixed(5);
            buf.WriteUInt32(_flags);
            return buf;
        }
        else
        {
            var buf = CreateFixed(3);
            buf.WriteUInt16((ushort)(_flags & 0xFFFF));
            return buf;
        }
    }
}

/// <summary>0xBD server request for the client's reported version.
/// Source-X sends the three-byte packet when login/relay detection did not
/// already identify the client version.</summary>
public sealed class PacketClientVersionRequest : PacketWriter
{
    public PacketClientVersionRequest() : base(0xBD) { }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(3);
        buf.WriteUInt16(3);
        return buf;
    }
}

/// <summary>0xBC — Season change.</summary>
public sealed class PacketSeason : PacketWriter
{
    private readonly byte _season;
    private readonly byte _playSound;

    public PacketSeason(byte season, bool playSound = true) : base(0xBC)
    {
        _season = season; _playSound = (byte)(playSound ? 1 : 0);
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(3);
        buf.WriteByte(_season);
        buf.WriteByte(_playSound);
        return buf;
    }
}

/// <summary>0x4E — Personal light level.</summary>
public sealed class PacketPersonalLight : PacketWriter
{
    private readonly uint _serial;
    private readonly byte _level;

    public PacketPersonalLight(uint serial, byte level) : base(0x4E)
    {
        _serial = serial; _level = level;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(6);
        buf.WriteUInt32(_serial);
        buf.WriteByte(_level);
        return buf;
    }
}

/// <summary>0x4F — Global light level.</summary>
public sealed class PacketGlobalLight : PacketWriter
{
    private readonly byte _level;

    public PacketGlobalLight(byte level) : base(0x4F)
    {
        _level = level;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(2);
        buf.WriteByte(_level);
        return buf;
    }
}

/// <summary>0x65 — Weather effect.</summary>
public sealed class PacketWeather : PacketWriter
{
    private readonly byte _type;  // 0=rain, 1=storm, 2=snow, 0xFF=none
    private readonly byte _count; // particle count (0-70)
    private readonly byte _temp;  // temperature

    public PacketWeather(byte type, byte count, byte temp) : base(0x65)
    {
        _type = type; _count = count; _temp = temp;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(4);
        buf.WriteByte(_type);
        buf.WriteByte(_count);
        buf.WriteByte(_temp);
        return buf;
    }
}

/// <summary>0xDC — AOS tooltip revision (object property list hash).</summary>
public sealed class PacketOPLInfo : PacketWriter
{
    private readonly uint _serial;
    private readonly uint _hash;

    public PacketOPLInfo(uint serial, uint hash) : base(0xDC)
    {
        _serial = serial; _hash = hash;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(9);
        buf.WriteUInt32(_serial);
        buf.WriteUInt32(_hash);
        return buf;
    }
}

/// <summary>0xD6 — AOS tooltip data (object property list).</summary>
public sealed class PacketOPLData : PacketWriter
{
    private readonly uint _serial;
    private readonly uint _hash;
    private readonly (uint ClilocId, string Args)[] _properties;

    public PacketOPLData(uint serial, uint hash, (uint ClilocId, string Args)[] properties) : base(0xD6)
    {
        _serial = serial; _hash = hash; _properties = properties;
    }

    public override PacketBuffer Build()
    {
        // Reject an over-budget tooltip before allocating: too many properties, a
        // single args string long enough to wrap its ushort byte-length field, or a
        // total packet size that would exceed the ushort wire ceiling (the per-
        // property caps alone don't bound the product — ~8 MB otherwise).
        string? reason = PacketBudget.CheckOpl(_properties);
        if (reason != null)
            return RejectOversize(reason);

        var buf = CreateVariable(64 + _properties.Length * 32);
        buf.WriteUInt16(1); // unknown
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(0); // unknown
        buf.WriteUInt32(_hash);

        foreach (var (clilocId, args) in _properties)
        {
            buf.WriteUInt32(clilocId);
            if (string.IsNullOrEmpty(args))
            {
                buf.WriteUInt16(0);
            }
            else
            {
                ushort argsLen = (ushort)(args.Length * 2);
                buf.WriteUInt16(argsLen);
                buf.WriteUnicodeLE(args);
            }
        }

        buf.WriteUInt32(0); // terminator

        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x24 — Open container gump.
/// 7.0.9+ clients expect 9 bytes (extra ushort container type 0x7D).
/// Pre-7.0.9 clients expect 7 bytes (no container type field).</summary>
public sealed class PacketOpenContainer : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _gumpId;
    private readonly bool _useNewFormat;

    public PacketOpenContainer(uint serial, ushort gumpId, bool useNewFormat = true) : base(0x24)
    {
        _serial = serial; _gumpId = gumpId; _useNewFormat = useNewFormat;
    }

    public override PacketBuffer Build()
    {
        if (_useNewFormat)
        {
            var buf = CreateFixed(9);
            buf.WriteUInt32(_serial);
            buf.WriteUInt16(_gumpId);
            buf.WriteUInt16(0x007D); // container type for 7.0.9+ (HS grid)
            return buf;
        }
        else
        {
            var buf = CreateFixed(7);
            buf.WriteUInt32(_serial);
            buf.WriteUInt16(_gumpId);
            return buf;
        }
    }
}

/// <summary>Vendor buy list item entry (for outgoing packets).</summary>
public readonly struct VendorItem
{
    public uint Serial { get; init; }
    public ushort ItemId { get; init; }
    public ushort Hue { get; init; }
    public ushort Amount { get; init; }
    public int Price { get; init; }
    public string Name { get; init; }
}

/// <summary>0x74 — Vendor buy item list. Sent to client to display purchasable items.
/// The container serial MUST be the real serial of the buy/stock container the
/// vendor has equipped (Source-X: LAYER_VENDOR_STOCK; SphereNet: vendor backpack)
/// — the same serial used by the preceding 0x3C container contents and trailing
/// 0x24 open container packets. Otherwise the client cannot bind the prices to
/// the inventory items and silently drops the buy gump.</summary>
public sealed class PacketVendorBuyList : PacketWriter
{
    // 254 name bytes + 1 NUL terminator == 255, the largest value the single-byte
    // per-row length prefix can hold without overflowing.
    private const int MaxBuyNameChars = 254;

    private readonly uint _containerSerial;
    private readonly IReadOnlyList<VendorItem> _items;

    public PacketVendorBuyList(uint containerSerial, IReadOnlyList<VendorItem> items) : base(0x74)
    {
        _containerSerial = containerSerial;
        _items = items;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(8 + _items.Count * 16);
        buf.WriteUInt32(_containerSerial);
        buf.WriteByte((byte)_items.Count);
        foreach (var item in _items)
        {
            buf.WriteUInt32((uint)item.Price);
            // ClassicUO's BuyList handler treats an empty string as a
            // signal to fall back to the static tiledata name
            // (`it.Name = it.ItemData.Name`). Writing nameLen=1 with
            // a single null byte yields ReadASCII(1) == "" on the
            // client side and triggers exactly that fallback — which
            // is what we want when the server-side ItemDef has no
            // explicit NAME= directive (vast majority of stock items
            // inherit their display name from tiledata).
            string name = item.Name ?? string.Empty;
            // The length prefix is a SINGLE byte and must cover the name plus the
            // trailing NUL WriteAsciiNull appends (one byte per char + 0). A name of
            // 255+ chars would overflow the byte — e.g. 255 chars -> (byte)256 == 0 —
            // while 256 bytes are still written, desyncing every following row on the
            // client. Truncate deterministically so the prefix always equals the
            // bytes actually written (max 254 name bytes + 1 NUL == 255).
            if (name.Length > MaxBuyNameChars) name = name[..MaxBuyNameChars];
            byte nameLen = (byte)(name.Length + 1);
            buf.WriteByte(nameLen);
            buf.WriteAsciiNull(name);
        }
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x9E — Vendor sell list. Sent to client to display items vendor will buy.</summary>
public sealed class PacketVendorSellList : PacketWriter
{
    // 65534 name bytes + 1 NUL == 65535, the largest value the word length prefix
    // can hold without overflowing.
    private const int MaxSellNameChars = 65534;

    private readonly uint _vendorSerial;
    private readonly IReadOnlyList<VendorItem> _items;

    public PacketVendorSellList(uint vendorSerial, IReadOnlyList<VendorItem> items) : base(0x9E)
    {
        _vendorSerial = vendorSerial;
        _items = items;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(256);
        buf.WriteUInt32(_vendorSerial);
        buf.WriteUInt16((ushort)_items.Count);
        foreach (var item in _items)
        {
            buf.WriteUInt32(item.Serial);
            buf.WriteUInt16(item.ItemId);
            buf.WriteUInt16(item.Hue);
            buf.WriteUInt16(item.Amount);
            buf.WriteUInt16((ushort)item.Price);
            // The length prefix here is a word, so the overflow bound is far higher
            // than the buy list's byte, but the same rule holds: the prefix must
            // equal the bytes written (name chars + NUL). Clamp so 65535+ char names
            // can't wrap the word and desync the row stream.
            string name = item.Name ?? string.Empty;
            if (name.Length > MaxSellNameChars) name = name[..MaxSellNameChars];
            ushort nameLen = (ushort)(name.Length + 1);
            buf.WriteUInt16(nameLen);
            buf.WriteAsciiNull(name);
        }
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x1D — Delete object.</summary>
public sealed class PacketDeleteObject : PacketWriter
{
    private readonly uint _serial;

    public PacketDeleteObject(uint serial) : base(0x1D)
    {
        _serial = serial;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(5);
        buf.WriteUInt32(_serial);
        return buf;
    }
}

/// <summary>0xAF — Death animation. Tells the client a mobile has died and
/// links it to its corpse container. ClassicUO uses this to reparent
/// equipped items, play the body-specific death animation, and register
/// the corpse for looting.</summary>
public sealed class PacketDeathAnimation : PacketWriter
{
    private readonly uint _mobileSerial;
    private readonly uint _corpseSerial;
    private readonly uint _running;

    public PacketDeathAnimation(uint mobileSerial, uint corpseSerial, uint running = 0)
        : base(0xAF)
    {
        _mobileSerial = mobileSerial;
        _corpseSerial = corpseSerial;
        _running = running;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(13);
        buf.WriteUInt32(_mobileSerial);
        buf.WriteUInt32(_corpseSerial);
        buf.WriteUInt32(_running);
        return buf;
    }
}

/// <summary>0x2C — Death status / death menu. Sent to the dying player's own
/// client to trigger the client-side death state (death music, death screen
/// fade, war-mode reset). ClassicUO's <c>DeathScreen</c> handler treats any
/// action != 1 as "you are dead" — Source-X uses <c>PacketDeathMenu::Dead</c>
/// (action 0x00) for that. The reverse path (action 1 = resurrect, 2 = stay
/// ghost) comes back to the server as the client's reply.
/// Reference: Source-X PacketDeathMenu (send.cpp), ClassicUO PacketHandlers
/// DeathScreen (line 1745).
/// Fixed length: 1 (opcode) + 1 (action) = 2 bytes.</summary>
public sealed class PacketDeathStatus : PacketWriter
{
    public const byte ActionDead = 0x00;
    public const byte ActionResurrect = 0x01;
    public const byte ActionGhost = 0x02;

    private readonly byte _action;

    public PacketDeathStatus(byte action) : base(0x2C)
    {
        _action = action;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(2);
        buf.WriteByte(_action);
        return buf;
    }
}

/// <summary>0x89 — Corpse equipment list.
/// Associates equipped item serials to layers on a corpse.</summary>
public sealed class PacketCorpseEquipment : PacketWriter
{
    private readonly uint _corpseSerial;
    private readonly IReadOnlyList<(byte Layer, uint ItemSerial)> _entries;

    public PacketCorpseEquipment(uint corpseSerial, IReadOnlyList<(byte Layer, uint ItemSerial)> entries)
        : base(0x89)
    {
        _corpseSerial = corpseSerial;
        _entries = entries;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(8 + (_entries.Count * 5));
        buf.WriteUInt32(_corpseSerial);

        foreach (var (layer, itemSerial) in _entries)
        {
            buf.WriteByte(layer);
            buf.WriteUInt32(itemSerial);
        }

        buf.WriteByte(0); // terminator
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x6D — Play music.</summary>
public sealed class PacketPlayMusic : PacketWriter
{
    private readonly ushort _musicId;

    public PacketPlayMusic(ushort musicId) : base(0x6D)
    {
        _musicId = musicId;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(3);
        buf.WriteUInt16(_musicId);
        return buf;
    }
}

/// <summary>0x1A — World item (object info).</summary>
public sealed class PacketWorldItem : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _itemId;
    private readonly ushort _amount;
    private readonly short _x, _y;
    private readonly sbyte _z;
    private readonly ushort _hue;
    private readonly byte _direction;
    private readonly byte _flags;
    private readonly bool _isMulti;

    public PacketWorldItem(uint serial, ushort itemId, ushort amount, short x, short y, sbyte z,
        ushort hue, byte direction = 0, byte flags = 0, bool isMulti = false)
        : base(0x1A)
    {
        _serial = serial; _itemId = itemId; _amount = amount;
        _x = x; _y = y; _z = z; _hue = hue;
        _direction = direction; _flags = flags; _isMulti = isMulti;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(24);
        uint ser = _serial;
        if (_amount > 1) ser |= 0x80000000;

        buf.WriteUInt32(ser);
        // 0x1A has no data-type field: the client treats a graphic in the 0x4000+
        // range as a multi (index = graphic & 0x3FFF). Our multi ids are raw multi.mul
        // block indices (< 0x4000), so set the multi bit for multi bodies.
        buf.WriteUInt16(_isMulti ? (ushort)(_itemId | 0x4000) : _itemId);

        if (_amount > 1)
            buf.WriteUInt16(_amount);

        int xVal = _x & 0x7FFF;
        if (_direction != 0) xVal |= 0x8000;
        buf.WriteUInt16((ushort)xVal);

        int yVal = _y & 0x3FFF;
        if (_hue != 0) yVal |= 0x8000;
        if (_flags != 0) yVal |= 0x4000;
        buf.WriteUInt16((ushort)yVal);

        if (_direction != 0)
            buf.WriteByte(_direction);

        buf.WriteSByte(_z);

        if (_hue != 0)
            buf.WriteUInt16(_hue);

        if (_flags != 0)
            buf.WriteByte(_flags);

        buf.WriteLengthAt(1);

        return buf;
    }
}

/// <summary>0x3C — Container contents. Sent when a container is opened so the
/// client knows every item to draw inside it. Also used by the vendor buy gump
/// (Source-X CClient::addContents → PacketContainer); without this packet the
/// 0x74 vendor buy list has no items to bind to and the menu never appears.
/// Pre-6.0.1.7 clients expect 19 bytes per item, 6.0.1.7+ expect 20 bytes
/// (extra grid index byte before the container serial).</summary>
public sealed class PacketContainerContents : PacketWriter
{
    public readonly struct Entry
    {
        public Entry(uint serial, ushort itemId, byte stackOffset, ushort amount,
            short x, short y, uint containerSerial, ushort hue, byte gridIndex = 0)
        {
            Serial = serial; ItemId = itemId; StackOffset = stackOffset; Amount = amount;
            X = x; Y = y; ContainerSerial = containerSerial; Hue = hue; GridIndex = gridIndex;
        }
        public uint Serial { get; }
        public ushort ItemId { get; }
        public byte StackOffset { get; }
        public ushort Amount { get; }
        public short X { get; }
        public short Y { get; }
        public uint ContainerSerial { get; }
        public ushort Hue { get; }
        public byte GridIndex { get; }
    }

    private readonly IReadOnlyList<Entry> _items;
    private readonly bool _useGridIndex;

    public PacketContainerContents(IReadOnlyList<Entry> items, bool useGridIndex = true)
        : base(0x3C)
    {
        _items = items;
        _useGridIndex = useGridIndex;
    }

    public override PacketBuffer Build()
    {
        int perItem = _useGridIndex ? 20 : 19;
        var buf = CreateVariable(5 + _items.Count * perItem);
        buf.WriteUInt16((ushort)_items.Count);
        foreach (var it in _items)
        {
            buf.WriteUInt32(it.Serial);
            buf.WriteUInt16(it.ItemId);
            buf.WriteByte(it.StackOffset);
            buf.WriteUInt16(it.Amount);
            buf.WriteInt16(it.X);
            buf.WriteInt16(it.Y);
            if (_useGridIndex)
                buf.WriteByte(it.GridIndex);
            buf.WriteUInt32(it.ContainerSerial);
            buf.WriteUInt16(it.Hue);
        }
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x25 — Container item update (add item to container display).
/// 6.0.1.7+ clients expect 21 bytes (extra grid index byte).
/// Pre-6.0.1.7 clients expect 20 bytes (no grid index).</summary>
public sealed class PacketContainerItem : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _itemId;
    private readonly byte _offset;
    private readonly ushort _amount;
    private readonly short _x, _y;
    private readonly uint _containerSerial;
    private readonly ushort _hue;
    private readonly bool _useGridIndex;

    public PacketContainerItem(uint serial, ushort itemId, byte offset, ushort amount,
        short x, short y, uint containerSerial, ushort hue, bool useGridIndex = true)
        : base(0x25)
    {
        _serial = serial; _itemId = itemId; _offset = offset;
        _amount = amount; _x = x; _y = y;
        _containerSerial = containerSerial; _hue = hue;
        _useGridIndex = useGridIndex;
    }

    public override PacketBuffer Build()
    {
        if (_useGridIndex)
        {
            var buf = CreateFixed(21);
            buf.WriteUInt32(_serial);
            buf.WriteUInt16(_itemId);
            buf.WriteByte(_offset);
            buf.WriteUInt16(_amount);
            buf.WriteInt16(_x);
            buf.WriteInt16(_y);
            buf.WriteByte(0); // grid index (6.0.1.7+)
            buf.WriteUInt32(_containerSerial);
            buf.WriteUInt16(_hue);
            return buf;
        }
        else
        {
            var buf = CreateFixed(20);
            buf.WriteUInt32(_serial);
            buf.WriteUInt16(_itemId);
            buf.WriteByte(_offset);
            buf.WriteUInt16(_amount);
            buf.WriteInt16(_x);
            buf.WriteInt16(_y);
            buf.WriteUInt32(_containerSerial);
            buf.WriteUInt16(_hue);
            return buf;
        }
    }
}

/// <summary>0x29 — Drop item acknowledged.</summary>
public sealed class PacketDropAck : PacketWriter
{
    public PacketDropAck() : base(0x29) { }

    public override PacketBuffer Build()
    {
        return CreateFixed(1);
    }
}

/// <summary>0x88 — Open paperdoll. Fixed 66 bytes: serial(4) + title(60) + flags(1).</summary>
public sealed class PacketOpenPaperdoll : PacketWriter
{
    private readonly uint _serial;
    private readonly string _title;
    private readonly byte _flags; // bit 0=can edit, bit 1=can equip

    public PacketOpenPaperdoll(uint serial, string title, byte flags = 0) : base(0x88)
    {
        _serial = serial;
        _title = title;
        _flags = flags;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(66);
        buf.WriteUInt32(_serial);
        buf.WriteAsciiFixed(_title, 60);
        buf.WriteByte(_flags);
        return buf;
    }
}

/// <summary>0xB8 — Profile response (variable length).</summary>
public sealed class PacketProfileResponse : PacketWriter
{
    private readonly uint _serial;
    private readonly string _title;
    private readonly string _profileText;

    public PacketProfileResponse(uint serial, string title, string profileText = "")
        : base(0xB8)
    {
        _serial = serial;
        _title = title ?? "";
        _profileText = profileText ?? "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64 + (_title.Length * 2) + (_profileText.Length * 2));
        buf.WriteUInt32(_serial);
        buf.WriteAsciiNull(_title);
        buf.WriteUnicodeNullBE(_profileText);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x3A — Skill list (outgoing, variable length).
/// type 0x02 = full list, type 0x00 = single skill update.
/// Each entry: skillId(2) + value(2) + rawValue(2) + lock(1) + cap(2).</summary>
public sealed class PacketSkillList : PacketWriter
{
    private readonly (ushort Id, ushort Value, ushort RawValue, byte Lock, ushort Cap)[] _skills;

    public PacketSkillList((ushort Id, ushort Value, ushort RawValue, byte Lock, ushort Cap)[] skills)
        : base(0x3A)
    {
        _skills = skills;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(4 + _skills.Length * 9 + 2);
        buf.WriteByte(0x02); // type: full list with caps

        foreach (var (id, value, rawValue, lockState, cap) in _skills)
        {
            buf.WriteUInt16((ushort)(id + 1)); // 1-based skill ID
            buf.WriteUInt16(value);
            buf.WriteUInt16(rawValue);
            buf.WriteByte(lockState);
            buf.WriteUInt16(cap);
        }

        buf.WriteUInt16(0); // terminator
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xA1 — Update health bar for a mobile (seen by others).</summary>
public sealed class PacketUpdateHealth : PacketWriter
{
    private readonly uint _serial;
    private readonly short _maxHits, _hits;

    public PacketUpdateHealth(uint serial, short maxHits, short hits) : base(0xA1)
    {
        _serial = serial;
        _maxHits = maxHits;
        _hits = hits;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(9);
        buf.WriteUInt32(_serial);
        buf.WriteInt16(_maxHits);
        buf.WriteInt16(_hits);
        return buf;
    }
}

/// <summary>0xA2 — Update mana bar for a mobile.</summary>
public sealed class PacketUpdateMana : PacketWriter
{
    private readonly uint _serial;
    private readonly short _maxMana, _mana;

    public PacketUpdateMana(uint serial, short maxMana, short mana) : base(0xA2)
    {
        _serial = serial;
        _maxMana = maxMana;
        _mana = mana;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(9);
        buf.WriteUInt32(_serial);
        buf.WriteInt16(_maxMana);
        buf.WriteInt16(_mana);
        return buf;
    }
}

/// <summary>0xA3 — Update stamina bar for a mobile.</summary>
public sealed class PacketUpdateStamina : PacketWriter
{
    private readonly uint _serial;
    private readonly short _maxStam, _stam;

    public PacketUpdateStamina(uint serial, short maxStam, short stam) : base(0xA3)
    {
        _serial = serial;
        _maxStam = maxStam;
        _stam = stam;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(9);
        buf.WriteUInt32(_serial);
        buf.WriteInt16(_maxStam);
        buf.WriteInt16(_stam);
        return buf;
    }
}

/// <summary>0x0B — Damage notification (combat damage number popup).
/// Fixed length: 1 (opcode) + 4 (defender serial) + 2 (damage) = 7 bytes.
/// Reference: Source-X PacketCombatDamage (send.cpp). Sending a length
/// header here desyncs the client packet stream and freezes the UI.</summary>
public sealed class PacketDamage : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _damage;

    public PacketDamage(uint serial, ushort damage) : base(0x0B)
    {
        _serial = serial;
        _damage = damage;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(7);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_damage);
        return buf;
    }
}

/// <summary>0xBF sub 0x22 — Legacy damage notification for pre-4.0.7a clients that
/// do not understand the 7-byte 0x0B packet. Source-X PacketCombatDamageOld
/// (send.cpp): 11 bytes total — a leading 0x01, the defender serial, and a single
/// damage byte. Selected per recipient by NetState.SendsOldDamagePacket.</summary>
public sealed class PacketDamageOld : PacketWriter
{
    private readonly uint _serial;
    private readonly byte _damage;

    public PacketDamageOld(uint serial, byte damage) : base(0xBF)
    {
        _serial = serial;
        _damage = damage;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(11);
        buf.WriteUInt16(0x0022); // subcommand
        buf.WriteByte(0x01);     // Source-X writes a constant leading 0x01
        buf.WriteUInt32(_serial);
        buf.WriteByte(_damage);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x6E — Character animation packet.</summary>
public sealed class PacketAnimation : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _action;
    private readonly ushort _frameCount;
    private readonly ushort _repeatCount;
    private readonly bool _repeat;
    private readonly bool _forward;
    private readonly byte _delay;

    public PacketAnimation(uint serial, ushort action, ushort frameCount = 7, ushort repeatCount = 1,
        bool forward = true, bool repeat = false, byte delay = 0) : base(0x6E)
    {
        _serial = serial;
        _action = action;
        _frameCount = frameCount;
        _repeatCount = repeatCount;
        _forward = forward;
        _repeat = repeat;
        _delay = delay;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(14);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_action);
        buf.WriteUInt16(_frameCount);
        buf.WriteUInt16(_repeatCount);
        buf.WriteByte((byte)(_forward ? 0 : 1));
        buf.WriteByte((byte)(_repeat ? 1 : 0));
        buf.WriteByte(_delay);
        return buf;
    }
}

/// <summary>0xD1 — Logout acknowledge (server → client). Second byte is 0x01 to
/// accept the client's logout request (sent when player clicks "return to
/// character select"). Client will then reconnect for char-list.</summary>
public sealed class PacketLogoutAck : PacketWriter
{
    public PacketLogoutAck() : base(0xD1) { }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(2);
        buf.WriteByte(0x01); // 0x01 = accept logout
        return buf;
    }
}

/// <summary>0xBF sub 0x01 — Initialize fast-walk prevention key stack (6 keys).</summary>
public sealed class PacketFastWalkStackInit : PacketWriter
{
    private readonly uint[] _keys;

    public PacketFastWalkStackInit(ReadOnlySpan<uint> keys) : base(0xBF)
    {
        if (keys.Length < 6)
            throw new ArgumentException("FastWalk stack requires 6 keys.", nameof(keys));
        _keys = keys[..6].ToArray();
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt16(0x0001);
        for (int i = 0; i < 6; i++)
            buf.WriteUInt32(_keys[i]);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xBF sub 0x02 — Push one key onto the fast-walk stack.</summary>
public sealed class PacketFastWalkStackPush : PacketWriter
{
    private readonly uint _key;

    public PacketFastWalkStackPush(uint key) : base(0xBF)
    {
        _key = key;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt16(0x0002);
        buf.WriteUInt32(_key);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xBF sub 0x26 - Set client movement speed mode.</summary>
public sealed class PacketSpeedMode : PacketWriter
{
    private readonly byte _mode;

    public PacketSpeedMode(byte mode) : base(0xBF)
    {
        _mode = mode;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(6);
        buf.WriteUInt16(0x0026);
        buf.WriteByte(_mode);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xBF sub 0x08 — Map change. Tells client which map to display.</summary>
public sealed class PacketMapChange : PacketWriter
{
    private readonly byte _mapId;

    public PacketMapChange(byte mapId) : base(0xBF)
    {
        _mapId = mapId;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt16(0x0008); // sub-command
        buf.WriteByte(_mapId);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x71 server→client — bulletin board (Source-X PacketBulletinBoard).
/// Sub 0 opens the board (name), sub 1 sends a message header, sub 2 the
/// full message body. Strings are length-prefixed fixed ASCII where the
/// length byte counts the null terminator (Source-X convention).</summary>
public sealed class PacketBulletinBoardOut : PacketWriter
{
    private readonly byte _sub;
    private readonly uint _board, _msg;
    private readonly string _name, _author, _subject, _time;
    private readonly string[]? _body;

    /// <summary>Sub 0 — open the board.</summary>
    public PacketBulletinBoardOut(uint board, string name) : base(0x71)
    {
        _sub = 0;
        _board = board;
        _name = name;
        _author = _subject = _time = "";
    }

    /// <summary>Sub 1 (header, body = null) or sub 2 (full message).</summary>
    public PacketBulletinBoardOut(uint board, uint msg, string author, string subject,
        string time, string[]? body) : base(0x71)
    {
        _sub = body == null ? (byte)1 : (byte)2;
        _board = board;
        _msg = msg;
        _author = author;
        _subject = subject;
        _time = time;
        _body = body;
        _name = "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(1024);
        buf.WriteByte(_sub);
        buf.WriteUInt32(_board);
        if (_sub == 0)
        {
            buf.WriteAsciiNull(_name);
        }
        else
        {
            buf.WriteUInt32(_msg);
            if (_sub == 1)
                buf.WriteUInt32(0); // parent/reply-to slot, header form only
            WriteLenString(buf, _author);
            WriteLenString(buf, _subject);
            WriteLenString(buf, _time);
            if (_sub == 2)
            {
                buf.WriteUInt32(0);
                buf.WriteUInt16((ushort)_body!.Length);
                foreach (var line in _body)
                    WriteLenString(buf, line, extraPad: 1);
            }
        }
        buf.WriteLengthAt(1);
        return buf;
    }

    private static void WriteLenString(PacketBuffer buf, string s, int extraPad = 0)
    {
        int len = Math.Min(255, s.Length + 1 + extraPad);
        buf.WriteByte((byte)len);
        buf.WriteAsciiFixed(s, len);
    }
}

/// <summary>0xF5 — display a map gump on a client that understands facets
/// (Source-X PacketDisplayMapNew, send.cpp:5358). Same as 0x90 plus the map the
/// rectangle belongs to, so two maps of the same coordinates on different facets are
/// no longer indistinguishable on the wire.
///
/// The size fields are HEIGHT then WIDTH here - inverted against the older packet, as
/// upstream notes in the same place - and only this packet reads the item's
/// OVERRIDE.MAPWIDTH / OVERRIDE.MAPHEIGHT.</summary>
public sealed class PacketMapDisplayNew : PacketWriter
{
    private readonly uint _mapSerial;
    private readonly ushort _left, _top, _right, _bottom, _width, _height;
    private readonly byte _facet;

    public PacketMapDisplayNew(uint mapSerial, ushort left, ushort top, ushort right, ushort bottom,
        ushort width, ushort height, byte facet) : base(0xF5)
    {
        _mapSerial = mapSerial;
        _left = left; _top = top; _right = right; _bottom = bottom;
        _width = width; _height = height; _facet = facet;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(21);
        buf.WriteUInt32(_mapSerial);
        buf.WriteUInt16(0x139D); // GUMP_MAP_2_NORTH
        buf.WriteUInt16(_left);
        buf.WriteUInt16(_top);
        buf.WriteUInt16(_right);
        buf.WriteUInt16(_bottom);
        buf.WriteUInt16(_height);
        buf.WriteUInt16(_width);
        buf.WriteUInt16(_facet);
        return buf;
    }
}

/// <summary>0x90 — display a map gump (Source-X PacketDisplayMap):
/// map item serial, the north-facing map gump art, world-coordinate bounds
/// and the gump pixel size.</summary>
public sealed class PacketMapDisplay : PacketWriter
{
    private readonly uint _mapSerial;
    private readonly ushort _left, _top, _right, _bottom, _width, _height;

    public PacketMapDisplay(uint mapSerial, ushort left, ushort top, ushort right, ushort bottom,
        ushort width = 200, ushort height = 200) : base(0x90)
    {
        _mapSerial = mapSerial;
        _left = left; _top = top; _right = right; _bottom = bottom;
        _width = width; _height = height;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(19);
        buf.WriteUInt32(_mapSerial);
        buf.WriteUInt16(0x139D); // GUMP_MAP_2_NORTH
        buf.WriteUInt16(_left);
        buf.WriteUInt16(_top);
        buf.WriteUInt16(_right);
        buf.WriteUInt16(_bottom);
        buf.WriteUInt16(_width);
        buf.WriteUInt16(_height);
        return buf;
    }
}

/// <summary>0x56 server→client — map plot command (Source-X PacketMapPlot).
/// Modes: 1 = draw pin, 5 = clear, 7 = plot-mode reply (edit flag carries
/// the new state).</summary>
public sealed class PacketMapPlot : PacketWriter
{
    private readonly uint _mapSerial;
    private readonly byte _mode;
    private readonly bool _edit;
    private readonly ushort _x, _y;

    public PacketMapPlot(uint mapSerial, byte mode, bool edit, ushort x = 0, ushort y = 0) : base(0x56)
    {
        _mapSerial = mapSerial;
        _mode = mode;
        _edit = edit;
        _x = x; _y = y;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(11);
        buf.WriteUInt32(_mapSerial);
        buf.WriteByte(_mode);
        buf.WriteByte(_edit ? (byte)1 : (byte)0);
        buf.WriteUInt16(_x);
        buf.WriteUInt16(_y);
        return buf;
    }
}

/// <summary>0xBF sub 0x19 — Stat lock info. Tells client current lock
/// state for STR/DEX/INT (0=up, 1=down, 2=locked).</summary>
public sealed class PacketStatLockInfo : PacketWriter
{
    private readonly uint _serial;
    private readonly byte _strLock, _dexLock, _intLock;

    public PacketStatLockInfo(uint serial, byte strLock, byte dexLock, byte intLock) : base(0xBF)
    {
        _serial = serial;
        _strLock = strLock; _dexLock = dexLock; _intLock = intLock;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt16(0x0019); // sub-command
        buf.WriteByte(2); // type 2 = full update (all 3 stats)
        buf.WriteUInt32(_serial);
        buf.WriteByte(0); // unknown
        byte lockFlags = (byte)((_strLock & 0x03) | ((_dexLock & 0x03) << 2) | ((_intLock & 0x03) << 4));
        buf.WriteByte(lockFlags);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xBF sub 0x18 — Map patches (enhanced map diff counts).</summary>
public sealed class PacketMapPatches : PacketWriter
{
    private readonly int[] _staticPatches;
    private readonly int[] _mapPatches;

    /// <param name="mapPatches">Map patch count per map (index 0-5).</param>
    /// <param name="staticPatches">Statics patch count per map (index 0-5).</param>
    public PacketMapPatches(int[]? mapPatches = null, int[]? staticPatches = null) : base(0xBF)
    {
        _mapPatches = mapPatches ?? new int[6];
        _staticPatches = staticPatches ?? new int[6];
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt16(0x0018); // sub-command
        for (int i = 0; i < 6; i++)
        {
            buf.WriteInt32(_staticPatches.Length > i ? _staticPatches[i] : 0);
            buf.WriteInt32(_mapPatches.Length > i ? _mapPatches[i] : 0);
        }
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xBF sub 0x14 — Context menu (popup menu) sent to client.</summary>
public sealed class PacketContextMenu : PacketWriter
{
    private readonly uint _serial;
    private readonly (ushort EntryTag, uint ClilocId, ushort Flags)[] _entries;

    public PacketContextMenu(uint serial, (ushort EntryTag, uint ClilocId, ushort Flags)[] entries)
        : base(0xBF)
    {
        _serial = serial;
        _entries = entries;
    }

    public override PacketBuffer Build()
    {
        int count = Math.Min(_entries.Length, 255);
        var buf = CreateVariable(16 + count * 8);
        buf.WriteUInt16(0x14); // sub-command
        buf.WriteUInt16(0x0001); // new-style context menu
        buf.WriteUInt32(_serial);
        buf.WriteByte((byte)count);

        for (int i = 0; i < count; i++)
        {
            var (entryTag, clilocId, flags) = _entries[i];
            buf.WriteUInt16(entryTag);
            buf.WriteUInt32(clilocId);
            buf.WriteUInt16(flags); // 0x00=enabled, 0x01=disabled, 0x20=highlighted
        }

        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xDD — Compressed gump dialog. Layout and text are zlib-compressed.</summary>
public sealed class PacketGumpDialog : PacketWriter
{
    private readonly uint _serial;
    private readonly uint _gumpId;
    private readonly int _x, _y;
    private readonly string _layout;
    private readonly IReadOnlyList<string> _texts;

    public PacketGumpDialog(uint serial, uint gumpId, int x, int y,
        string layout, IReadOnlyList<string> texts) : base(0xDD)
    {
        _serial = serial;
        _gumpId = gumpId;
        _x = x;
        _y = y;
        _layout = layout;
        _texts = texts;
    }

    public override PacketBuffer Build()
    {
        // Compress layout string
        byte[] layoutBytes = Encoding.ASCII.GetBytes(_layout + "\0");

        // Budget check BEFORE compressing: a highly compressible layout/text block
        // is tiny on the wire (so the 65535 wire guard never trips) yet announces a
        // huge decompressed size the client must allocate — a compression bomb.
        long textBlockBytes = 0;
        foreach (var t in _texts) textBlockBytes += 2 + (long)t.Length * 2;
        string? reject = PacketBudget.CheckGump(layoutBytes.Length, _texts, textBlockBytes);
        if (reject != null)
            return RejectOversize(reject);

        byte[] layoutCompressed = ZlibCompress(layoutBytes);

        // Build text block: count(4) + for each text: len(2) + unicode_be
        using var textMs = new MemoryStream();
        using (var textBw = new BinaryWriter(textMs))
        {
            foreach (var text in _texts)
            {
                textBw.Write((byte)(text.Length >> 8));
                textBw.Write((byte)(text.Length & 0xFF));
                foreach (char c in text)
                {
                    textBw.Write((byte)(c >> 8));
                    textBw.Write((byte)(c & 0xFF));
                }
            }
        }
        byte[] textBytes = textMs.ToArray();
        byte[] textCompressed = ZlibCompress(textBytes);

        int totalSize = 1 + 2 + 4 + 4 + 4 + 4 + 4 + 4 + layoutCompressed.Length + 4 + 4 + textCompressed.Length;
        var buf = CreateVariable(totalSize);
        buf.WriteUInt32(_serial);
        buf.WriteUInt32(_gumpId);
        buf.WriteInt32(_x);
        buf.WriteInt32(_y);
        buf.WriteInt32(layoutCompressed.Length + 4); // compressed layout length + 4 for decompressed size
        buf.WriteInt32(layoutBytes.Length);           // decompressed layout length
        buf.WriteBytes(layoutCompressed);
        buf.WriteInt32(_texts.Count);
        buf.WriteInt32(textCompressed.Length + 4);    // compressed text length + 4 for decompressed size
        buf.WriteInt32(textBytes.Length);              // decompressed text length
        buf.WriteBytes(textCompressed);

        buf.WriteLengthAt(1);
        return buf;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        // Write zlib header (RFC 1950): CMF=0x78, FLG=0x9C (deflate, default compression)
        output.WriteByte(0x78);
        output.WriteByte(0x9C);
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }
        // Write Adler32 checksum
        uint adler = Adler32(data);
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)(adler & 0xFF));
        return output.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}

/// <summary>0xB0 — Display gump (uncompressed). The pre-compression fallback Source-X
/// sends to clients below MINCLIVER_COMPRESSDIALOG (send.cpp:3713, writeStandardControls),
/// i.e. very old (pre-3.0) clients that do not understand the 0xDD compressed form. Same
/// content — plain-ASCII layout + a Unicode text block — just uncompressed.</summary>
public sealed class PacketGumpDialogStandard : PacketWriter
{
    private readonly uint _serial, _gumpId;
    private readonly int _x, _y;
    private readonly string _layout;
    private readonly IReadOnlyList<string> _texts;

    public PacketGumpDialogStandard(uint serial, uint gumpId, int x, int y,
        string layout, IReadOnlyList<string> texts) : base(0xB0)
    {
        _serial = serial;
        _gumpId = gumpId;
        _x = x;
        _y = y;
        _layout = layout;
        _texts = texts;
    }

    public override PacketBuffer Build()
    {
        byte[] layoutBytes = Encoding.ASCII.GetBytes(_layout);

        // Same budget as the compressed form: the ushort layout/count/per-text
        // fields here would silently wrap past their caps, desyncing the client.
        long textBlockBytes = 0;
        foreach (var t in _texts) textBlockBytes += 2 + (long)t.Length * 2;
        string? reject = PacketBudget.CheckGump(layoutBytes.Length, _texts, textBlockBytes);
        if (reject != null)
            return RejectOversize(reject);

        var buf = CreateVariable(64 + layoutBytes.Length + _texts.Count * 8);
        buf.WriteUInt32(_serial);
        buf.WriteUInt32(_gumpId);
        buf.WriteInt32(_x);
        buf.WriteInt32(_y);
        buf.WriteUInt16((ushort)layoutBytes.Length);
        buf.WriteBytes(layoutBytes);
        buf.WriteUInt16((ushort)_texts.Count);
        foreach (var text in _texts)
        {
            buf.WriteUInt16((ushort)text.Length);
            foreach (char c in text)
            {
                buf.WriteByte((byte)(c >> 8)); // UTF-16 big-endian
                buf.WriteByte((byte)(c & 0xFF));
            }
        }
        buf.WriteLengthAt(1);
        return buf;
    }
}

// ==================== Party Packets ====================

/// <summary>
/// 0xBF sub 0x06 — Party command (server → client).
/// Used to send party member list, add/remove notifications, and party messages.
/// </summary>
public sealed class PacketPartyMemberList : PacketWriter
{
    private readonly uint[] _memberSerials;

    public PacketPartyMemberList(uint[] memberSerials) : base(0xBF)
    {
        _memberSerials = memberSerials;
    }

    public override PacketBuffer Build()
    {
        int count = Math.Min(_memberSerials.Length, 255);
        int len = 7 + count * 4;
        var buf = CreateVariable(len);
        buf.WriteUInt16(0x0006); // sub-command: party
        buf.WriteByte(0x01);    // sub-sub: member list
        buf.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
            buf.WriteUInt32(_memberSerials[i]);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>
/// 0xBF sub 0x06 — Party remove member notification.
/// </summary>
public sealed class PacketPartyRemoveMember : PacketWriter
{
    private readonly uint[] _remainingMembers;
    private readonly uint _removedSerial;

    public PacketPartyRemoveMember(uint removedSerial, uint[] remainingMembers) : base(0xBF)
    {
        _removedSerial = removedSerial;
        _remainingMembers = remainingMembers;
    }

    public override PacketBuffer Build()
    {
        int count = Math.Min(_remainingMembers.Length, 255);
        int len = 11 + count * 4;
        var buf = CreateVariable(len);
        buf.WriteUInt16(0x0006);
        buf.WriteByte(0x02); // sub-sub: remove member
        buf.WriteByte((byte)count);
        buf.WriteUInt32(_removedSerial);
        for (int i = 0; i < count; i++)
            buf.WriteUInt32(_remainingMembers[i]);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>
/// 0xBF sub 0x06 — Party chat message from server to all members.
/// </summary>
public sealed class PacketPartyMessage : PacketWriter
{
    private readonly uint _fromSerial;
    private readonly string _message;
    private readonly bool _isPrivate;

    public PacketPartyMessage(uint fromSerial, string message, bool isPrivate = false) : base(0xBF)
    {
        _fromSerial = fromSerial;
        _message = message;
        _isPrivate = isPrivate;
    }

    public override PacketBuffer Build()
    {
        byte[] textBytes = Encoding.BigEndianUnicode.GetBytes(_message);
        int len = 11 + textBytes.Length + 2; // sub(2) + subSub(1) + serial(4) + text + null
        var buf = CreateVariable(len);
        buf.WriteUInt16(0x0006);
        buf.WriteByte(_isPrivate ? (byte)0x03 : (byte)0x04); // sub-sub: private or public
        buf.WriteUInt32(_fromSerial);
        buf.WriteBytes(textBytes);
        buf.WriteUInt16(0); // null terminator
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>
/// 0xBF sub 0x06 — Party invitation from server.
/// </summary>
public sealed class PacketPartyInvitation : PacketWriter
{
    private readonly uint _leaderSerial;

    public PacketPartyInvitation(uint leaderSerial) : base(0xBF)
    {
        _leaderSerial = leaderSerial;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(7);
        buf.WriteUInt16(0x0006);
        buf.WriteByte(0x07); // sub-sub: invite
        buf.WriteUInt32(_leaderSerial);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xDF — Add/Remove buff or debuff icon.
/// ClassicUO: BuffDebuff packet.</summary>
public sealed class PacketBuffIcon : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _iconId;
    private readonly bool _add;
    private readonly ushort _durationSeconds;
    private readonly uint _titleCliloc;
    private readonly uint _descriptionCliloc;
    private readonly IReadOnlyList<string> _arguments;

    public PacketBuffIcon(uint serial, SphereNet.Core.Enums.BuffIcon iconId, bool add,
        ushort durationSeconds = 0, uint titleCliloc = 0, uint descriptionCliloc = 0,
        params string[] arguments)
        : this(serial, (ushort)iconId, add, durationSeconds, titleCliloc,
            descriptionCliloc, arguments)
    {
    }

    public PacketBuffIcon(uint serial, ushort iconId, bool add, ushort durationSeconds = 0,
        uint titleCliloc = 0, uint descriptionCliloc = 0, params string[] arguments) : base(0xDF)
    {
        _serial = serial;
        _iconId = iconId;
        _add = add;
        _durationSeconds = durationSeconds;
        _titleCliloc = titleCliloc;
        _descriptionCliloc = descriptionCliloc;
        _arguments = arguments ?? [];
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_iconId);
        buf.WriteUInt16(_add ? (ushort)0x0001 : (ushort)0x0000); // type: 0=remove, 1=add

        if (_add)
        {
            // Source-X PacketBuff layout. The repeated icon/type fields and
            // reserved values are required by the client protocol.
            buf.WriteBytes(new byte[4]);          // reserved
            buf.WriteUInt16(_iconId);             // icon id (repeated)
            buf.WriteUInt16(0x0001);              // type flag (again)
            buf.WriteBytes(new byte[4]);          // reserved
            buf.WriteUInt16(_durationSeconds);    // seconds
            buf.WriteBytes(new byte[3]);          // reserved

            // Title and description are client cliloc identifiers.
            buf.WriteUInt32(_titleCliloc);
            buf.WriteUInt32(_descriptionCliloc);

            if (_arguments.Count == 0)
            {
                // Source-X sends one whitespace argument so an empty cliloc
                // placeholder doesn't appear as raw ~1_NOTHING~ text.
                buf.WriteBytes(new byte[4]);
                buf.WriteUInt16(0x0001);
                buf.WriteUInt16(0);
                WriteLittleUniNull(buf, "\t ");
            }
            else
            {
                buf.WriteBytes(new byte[4]);      // reserved
                buf.WriteUInt16(0x0001);          // "more data" marker
                buf.WriteBytes(new byte[2]);      // reserved
                // Args — ServUO prefixes "\t" so the cliloc formatter
                // treats the rest as a single argument. Null-terminated
                // little-endian unicode.
                WriteLittleUniNull(buf, "\t" + string.Join("\t", _arguments) + "\t");
                buf.WriteUInt16(0x0001);          // "more data" marker
                buf.WriteBytes(new byte[2]);      // reserved
            }
        }

        // Variable-length packet — fill in the 2-byte length field after
        // the opcode. Omitting this leaves the length as the
        // CreateVariable placeholder (0), which hard-freezes ClassicUO.
        buf.WriteLengthAt(1);
        return buf;
    }

    private static void WriteLittleUniNull(PacketBuffer buf, string s)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(s);
        buf.WriteBytes(bytes);
        buf.WriteUInt16(0); // null terminator (2 bytes for UTF-16)
    }
}

/// <summary>0xE5 - add a KR/SA/modern-client radar waypoint.</summary>
public sealed class PacketWaypointAdd : PacketWriter
{
    private readonly uint _serial;
    private readonly short _x;
    private readonly short _y;
    private readonly sbyte _z;
    private readonly byte _map;
    private readonly ushort _type;
    private readonly string _name;

    public PacketWaypointAdd(uint serial, short x, short y, sbyte z, byte map,
        ushort type, string name) : base(0xE5)
    {
        _serial = serial;
        _x = x;
        _y = y;
        _z = z;
        _map = map;
        _type = type;
        _name = name ?? "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(25 + _name.Length * 2);
        buf.WriteUInt32(_serial);
        buf.WriteInt16(_x);
        buf.WriteInt16(_y);
        buf.WriteSByte(_z);
        buf.WriteByte(_map);
        buf.WriteUInt16(_type);
        buf.WriteUInt16(0);
        buf.WriteUInt32(_type == 1 ? 1028198u : 1062613u);
        buf.WriteUnicodeLeNullTerminated(_name);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xE6 - remove a radar waypoint.</summary>
public sealed class PacketWaypointRemove : PacketWriter
{
    private readonly uint _serial;

    public PacketWaypointRemove(uint serial) : base(0xE6) => _serial = serial;

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(5);
        buf.WriteUInt32(_serial);
        return buf;
    }
}

/// <summary>0xC1 - localized (cliloc) message used by SYSMESSAGELOC.</summary>
public sealed class PacketClilocMessage : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _body;
    private readonly byte _type;
    private readonly ushort _hue;
    private readonly ushort _font;
    private readonly uint _cliloc;
    private readonly string _name;
    private readonly string _args;

    public PacketClilocMessage(uint serial, ushort body, byte type, ushort hue, ushort font,
        uint cliloc, string name, string args) : base(0xC1)
    {
        _serial = serial;
        _body = body;
        _type = type;
        _hue = hue;
        _font = font;
        _cliloc = cliloc;
        _name = name ?? "";
        _args = args ?? "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_body);
        buf.WriteByte(_type);
        buf.WriteUInt16(_hue);
        buf.WriteUInt16(_font);
        buf.WriteUInt32(_cliloc);
        // 30-byte fixed-width speaker name, zero-padded
        var nameBytes = new byte[30];
        var src = System.Text.Encoding.ASCII.GetBytes(_name);
        Array.Copy(src, nameBytes, Math.Min(src.Length, 29));
        buf.WriteBytes(nameBytes);
        // Little-endian UTF-16 args, terminated by \0\0
        var argBytes = System.Text.Encoding.Unicode.GetBytes(_args);
        buf.WriteBytes(argBytes);
        buf.WriteUInt16(0);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xBF sub 0x1B — New Spellbook Content. Tells the client which
/// spells exist in a spellbook using a 64-bit bitmask stored in More1/More2.</summary>
public sealed class PacketSpellbookContent : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _graphic;
    private readonly ushort _scrollOffset;
    private readonly ulong _spellBits;

    public PacketSpellbookContent(uint serial, ushort graphic, ushort scrollOffset, ulong spellBits)
        : base(0xBF)
    {
        _serial = serial;
        _graphic = graphic;
        _scrollOffset = scrollOffset;
        _spellBits = spellBits;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(23);
        buf.WriteUInt16(0x001B);
        buf.WriteUInt16(1);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_graphic);
        buf.WriteUInt16(_scrollOffset);
        buf.WriteUInt32((uint)(_spellBits & 0xFFFFFFFF));
        buf.WriteUInt32((uint)(_spellBits >> 32));
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xBA — Quest arrow. Active=1 draws an arrow on the client's
/// screen pointing toward (x, y); active=0 clears it.</summary>
public sealed class PacketArrowQuest : PacketWriter
{
    private readonly bool _active;
    private readonly ushort _x, _y;
    private readonly bool _extended;

    /// <param name="extended">7.0.9+/KR/EC take a 10-byte variant with a trailing
    /// serial dword; older clients use the 6-byte form. Sending the 10-byte form
    /// to a 6-byte client leaves 4 bytes the client reads as the next opcode,
    /// desyncing its stream — so this must match the recipient's version.</param>
    public PacketArrowQuest(bool active, ushort x, ushort y, bool extended = true) : base(0xBA)
    {
        _active = active;
        _x = x;
        _y = y;
        _extended = extended;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(_extended ? 10 : 6);
        buf.WriteByte((byte)(_active ? 1 : 0));
        buf.WriteUInt16(_x);
        buf.WriteUInt16(_y);
        if (_extended)
            buf.WriteUInt32(0); // serial placeholder — scripts don't bind one
        return buf;
    }
}

// ==================== Menu / Input Packets ====================

/// <summary>Entry describing a single menu item for the 0x7C packet.</summary>
public record MenuItemEntry(ushort ModelId, ushort Hue, string Text);

/// <summary>0x7C — Item List Menu (old-style menu display).</summary>
public sealed class PacketMenuDisplay : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _menuId;
    private readonly string _question;
    private readonly IReadOnlyList<MenuItemEntry> _items;

    public PacketMenuDisplay(uint serial, ushort menuId, string question, IReadOnlyList<MenuItemEntry> items)
        : base(0x7C)
    {
        _serial = serial;
        _menuId = menuId;
        _question = question;
        _items = items;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(256);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_menuId);

        byte qLen = (byte)Math.Min(_question.Length, 255);
        buf.WriteByte(qLen);
        buf.WriteAsciiFixed(_question, qLen);

        // Only write as many items as the 1-byte count advertises; iterating the
        // full list when it exceeds 255 would desync the client's read cursor.
        int count = Math.Min(_items.Count, 255);
        buf.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
        {
            var item = _items[i];
            buf.WriteUInt16(item.ModelId);
            buf.WriteUInt16(item.Hue);
            byte nameLen = (byte)Math.Min(item.Text.Length, 255);
            buf.WriteByte(nameLen);
            buf.WriteAsciiFixed(item.Text, nameLen);
        }

        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>
/// 0xAB — Gump Value Input dialog (server → client).
/// Used by the script INPDLG verb to prompt a property edit on a target object.
/// </summary>
public sealed class PacketGumpValueInput : PacketWriter
{
    /// <summary>Style hint for the input dialog.</summary>
    public enum InputStyle : byte
    {
        NoEdit = 0,
        TextEdit = 1,
        NumericEdit = 2,
    }

    private readonly uint _targetSerial;
    private readonly ushort _context;
    private readonly bool _cancel;
    private readonly InputStyle _style;
    private readonly uint _maxLength;
    private readonly string _caption;
    private readonly string _description;

    public PacketGumpValueInput(
        uint targetSerial,
        ushort context,
        string caption,
        string description,
        uint maxLength,
        InputStyle style = InputStyle.TextEdit,
        bool cancel = true)
        : base(0xAB)
    {
        _targetSerial = targetSerial;
        _context = context;
        _caption = caption ?? "";
        _description = description ?? "";
        _maxLength = maxLength;
        _style = style;
        _cancel = cancel;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64 + _caption.Length + _description.Length);
        buf.WriteUInt32(_targetSerial);
        buf.WriteUInt16(_context);

        int captionLen = Math.Min(_caption.Length + 1, 255);
        buf.WriteUInt16((ushort)captionLen);
        buf.WriteAsciiFixed(_caption, captionLen);

        buf.WriteByte(_cancel ? (byte)1 : (byte)0);
        buf.WriteByte((byte)_style);
        buf.WriteUInt32(_maxLength);

        int descLen = Math.Min(_description.Length + 1, 255);
        buf.WriteUInt16((ushort)descLen);
        buf.WriteAsciiFixed(_description, descLen);

        buf.WriteLengthAt(1);
        return buf;
    }
}

// ==================== Secure Trade Packets ====================

/// <summary>0x6F action 0 — Open secure trade window.</summary>
public sealed class PacketSecureTradeOpen : PacketWriter
{
    private readonly uint _partnerSerial;
    private readonly uint _myContainerSerial;
    private readonly uint _theirContainerSerial;
    private readonly string _partnerName;

    public PacketSecureTradeOpen(uint partnerSerial, uint myContainer, uint theirContainer, string partnerName)
        : base(0x6F)
    {
        _partnerSerial = partnerSerial;
        _myContainerSerial = myContainer;
        _theirContainerSerial = theirContainer;
        _partnerName = partnerName ?? "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(48);
        buf.WriteByte(0x00);
        buf.WriteUInt32(_partnerSerial);
        buf.WriteUInt32(_myContainerSerial);
        buf.WriteUInt32(_theirContainerSerial);
        buf.WriteBool(true);
        buf.WriteAsciiFixed(_partnerName, 30);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x6F action 1 — Close/cancel secure trade.</summary>
public sealed class PacketSecureTradeClose : PacketWriter
{
    private readonly uint _containerSerial;

    public PacketSecureTradeClose(uint containerSerial) : base(0x6F)
    {
        _containerSerial = containerSerial;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(8);
        buf.WriteByte(0x01);
        buf.WriteUInt32(_containerSerial);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0x6F action 2 — Update acceptance status.</summary>
public sealed class PacketSecureTradeUpdate : PacketWriter
{
    private readonly uint _containerSerial;
    private readonly bool _myAccepted;
    private readonly bool _theirAccepted;

    public PacketSecureTradeUpdate(uint containerSerial, bool myAccepted, bool theirAccepted) : base(0x6F)
    {
        _containerSerial = containerSerial;
        _myAccepted = myAccepted;
        _theirAccepted = theirAccepted;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(17);
        buf.WriteByte(0x02);
        buf.WriteUInt32(_containerSerial);
        buf.WriteUInt32(_myAccepted ? 1u : 0u);
        buf.WriteUInt32(_theirAccepted ? 1u : 0u);
        buf.WriteLengthAt(1);
        return buf;
    }
}

// ==================== SA/HS+ Packets ====================

/// <summary>0xF3 — SA+ world entity packet. Replaces 0x1A for clients with
/// ProtocolChanges.StygianAbyss. HS clients expect 2 extra trailing bytes.</summary>
public sealed class PacketWorldItemSA : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _itemId;
    private readonly ushort _amount;
    private readonly short _x, _y;
    private readonly sbyte _z;
    private readonly ushort _hue;
    private readonly byte _direction;
    private readonly byte _light;
    private readonly byte _flags;
    private readonly bool _highSeas;
    private readonly bool _isMulti;

    public PacketWorldItemSA(uint serial, ushort itemId, ushort amount,
        short x, short y, sbyte z, ushort hue,
        bool highSeas = false, byte light = 0, byte flags = 0, byte direction = 0,
        bool isMulti = false)
        : base(0xF3)
    {
        _serial = serial;
        _itemId = itemId;
        _amount = amount;
        _x = x; _y = y; _z = z;
        _hue = hue;
        _direction = direction;
        _highSeas = highSeas;
        _light = light;
        _flags = flags;
        _isMulti = isMulti;
    }

    public override PacketBuffer Build()
    {
        int size = _highSeas ? 26 : 24;
        var buf = CreateFixed(size);
        buf.WriteUInt16(0x0001);
        // Data type: 0 = item, 2 = multi. The client renders a multi (and accepts the
        // 0xD8 custom-house design) only when this is 2; the graphic stays the raw
        // multi index (client masks it with & 0x3FFF).
        buf.WriteByte(_isMulti ? (byte)0x02 : (byte)0x00);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16((ushort)(_itemId & (_highSeas ? 0xFFFF : 0x7FFF)));
        buf.WriteByte(_direction);
        buf.WriteInt16((short)_amount);
        buf.WriteInt16((short)_amount);
        buf.WriteInt16((short)(_x & 0x7FFF));
        buf.WriteInt16((short)(_y & 0x3FFF));
        buf.WriteSByte(_z);
        buf.WriteByte(_light);
        buf.WriteInt16((short)_hue);
        buf.WriteByte(_flags);
        if (_highSeas)
            buf.WriteUInt16(0);
        return buf;
    }
}

/// <summary>0xCC — Localized message with prefix/suffix affix.</summary>
public sealed class PacketClilocMessageAffix : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _body;
    private readonly byte _type;
    private readonly ushort _hue;
    private readonly ushort _font;
    private readonly uint _clilocId;
    private readonly byte _flags;
    private readonly string _name;
    private readonly string _affix;
    private readonly string _args;

    public PacketClilocMessageAffix(uint serial, ushort body, byte type,
        ushort hue, ushort font, uint clilocId,
        string name, string affix, string args,
        bool prepend = false, bool system = false)
        : base(0xCC)
    {
        _serial = serial;
        _body = body;
        _type = type;
        _hue = hue;
        _font = font;
        _clilocId = clilocId;
        _flags = (byte)((prepend ? 0x01 : 0) | (system ? 0x02 : 0));
        _name = name ?? "";
        _affix = affix ?? "";
        _args = args ?? "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64 + _affix.Length + _args.Length * 2);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_body);
        buf.WriteByte(_type);
        buf.WriteUInt16(_hue);
        buf.WriteUInt16(_font);
        buf.WriteUInt32(_clilocId);
        buf.WriteByte(_flags);
        buf.WriteAsciiFixed(_name, 30);
        buf.WriteAsciiNull(_affix);
        buf.WriteUnicodeLeNullTerminated(_args);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xE2 — New (body-agnostic) animation packet for High Seas+ clients.
/// The client resolves the body-specific animation group from the gesture
/// <paramref name="gesture"/>, sub-action and mode, so a single gesture plays a
/// sensible animation on human, monster and gargoyle bodies alike. The legacy
/// 0x6E packet (raw action index) is still used for pre-HS clients.</summary>
public sealed class PacketNewAnimation : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _gesture;
    private readonly ushort _subAction;
    private readonly byte _mode;

    public PacketNewAnimation(uint serial, NewAnimationGesture gesture, ushort subAction = 0, byte mode = 0)
        : base(0xE2)
    {
        _serial = serial;
        _gesture = (ushort)gesture;
        _subAction = subAction;
        _mode = mode;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(10);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_gesture);
        buf.WriteUInt16(_subAction);
        buf.WriteByte(_mode);
        return buf;
    }
}


/// <summary>Close a client-side UI window (0xBF sub 0x16, Source-X
/// PacketCloseUIWindow). Window types: 1 = paperdoll, 2 = status,
/// 8 = character profile.</summary>
public sealed class PacketCloseUIWindow : PacketWriter
{
    private readonly uint _windowType;
    private readonly uint _serial;

    public PacketCloseUIWindow(uint windowType, uint serial) : base(0xBF)
    {
        _windowType = windowType;
        _serial = serial;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(13);
        buf.WriteUInt16(0x0016);
        buf.WriteUInt32(_windowType);
        buf.WriteUInt32(_serial);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>Open the Codex of Wisdom entry on Enhanced/KR clients
/// (0xBF sub 0x17, Source-X PacketCodexOfWisdom).</summary>
public sealed class PacketCodexOfWisdom : PacketWriter
{
    private readonly uint _topicId;
    private readonly bool _forceOpen;

    public PacketCodexOfWisdom(uint topicId, bool forceOpen) : base(0xBF)
    {
        _topicId = topicId;
        _forceOpen = forceOpen;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(11);
        buf.WriteUInt16(0x0017);
        buf.WriteByte(1);
        buf.WriteUInt32(_topicId);
        buf.WriteByte(_forceOpen ? (byte)1 : (byte)0);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>Open a scroll gump with text (0xA6, Source-X PacketOpenScroll).
/// Types: 0 = tips window, 2 = updates window.</summary>
public sealed class PacketOpenScroll : PacketWriter
{
    private readonly byte _type;
    private readonly uint _context;
    private readonly string _text;

    public PacketOpenScroll(byte type, uint context, string text) : base(0xA6)
    {
        _type = type;
        _context = context;
        _text = text ?? "";
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(10 + _text.Length + 1);
        buf.WriteByte(_type);
        buf.WriteUInt32(_context);
        buf.WriteUInt16((ushort)(_text.Length + 1));
        buf.WriteAsciiFixed(_text, _text.Length + 1);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>Show the dye vat hue-picker window (0x95, Source-X
/// PacketShowDyeWindow). The client answers with the same opcode, handled by
/// GameClient.HandleDyeResponse.</summary>
public sealed class PacketDyeWindow : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _hue;
    private readonly ushort _dispId;

    public PacketDyeWindow(uint serial, ushort hue, ushort dispId) : base(0x95)
    {
        _serial = serial;
        _hue = hue;
        _dispId = dispId;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateFixed(9);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_hue);
        buf.WriteUInt16(_dispId);
        return buf;
    }
}

/// <summary>Health-bar colour update (0x17, Source-X PacketHealthBarUpdate) —
/// tells SA/KR clients to tint a character's health bar green while poisoned and
/// yellow while frozen/invulnerable. Two status entries (GreenBar=1, YellowBar=2),
/// each with an on/off flag.</summary>
public sealed class PacketHealthBarStatus : PacketWriter
{
    private readonly uint _serial;
    private readonly bool _poisoned;
    private readonly bool _yellow;

    public PacketHealthBarStatus(uint serial, bool poisoned, bool yellow) : base(0x17)
    {
        _serial = serial;
        _poisoned = poisoned;
        _yellow = yellow;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(15);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(2);          // status entry count
        buf.WriteUInt16(1);          // GreenBar — poison
        buf.WriteBool(_poisoned);
        buf.WriteUInt16(2);          // YellowBar — frozen / invulnerable
        buf.WriteBool(_yellow);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>Health-bar colour update, Enhanced-Client variant (0x16, Source-X
/// PacketHealthBarUpdateNew) — a single colour entry (green poison / yellow
/// frozen) with an on/off flag.</summary>
public sealed class PacketHealthBarStatusNew : PacketWriter
{
    private readonly uint _serial;
    private readonly ushort _color;

    public PacketHealthBarStatusNew(uint serial, bool poisoned, bool yellow) : base(0x16)
    {
        _serial = serial;
        _color = poisoned ? (ushort)1 : yellow ? (ushort)2 : (ushort)0;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(12);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(1);          // status entry count
        buf.WriteUInt16(_color);
        buf.WriteBool(_color != 0);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>Single-skill update (0x3A modes 0xFF / 0xDF-with-cap, Source-X
/// PacketSkills single-skill branch) — the full-list variant lives in
/// PacketSkillList.</summary>
public sealed class PacketSkillSingle : PacketWriter
{
    private readonly ushort _id;
    private readonly ushort _value;
    private readonly ushort _rawValue;
    private readonly byte _lock;
    private readonly ushort _cap;
    private readonly bool _withCap;

    public PacketSkillSingle(ushort id, ushort value, ushort rawValue, byte skillLock, ushort cap, bool withCap = true)
        : base(0x3A)
    {
        _id = id;
        _value = value;
        _rawValue = rawValue;
        _lock = skillLock;
        _cap = cap;
        _withCap = withCap;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(16);
        buf.WriteByte(_withCap ? (byte)0xDF : (byte)0xFF);
        buf.WriteUInt16(_id);
        buf.WriteUInt16(_value);
        buf.WriteUInt16(_rawValue);
        buf.WriteByte(_lock);
        if (_withCap)
            buf.WriteUInt16(_cap);
        buf.WriteLengthAt(1);
        return buf;
    }
}
