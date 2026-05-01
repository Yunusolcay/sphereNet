using System.IO.Compression;
using System.Text;
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
        var buf = CreateVariable(128);
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
        buf.WriteUInt16((ushort)(_hue != 0 ? _itemId | 0x8000 : _itemId));
        buf.WriteByte(0); // padding
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

    public PacketDrawObject(uint serial, ushort bodyId, short x, short y, sbyte z,
        byte dir, ushort hue, byte flags, byte notoriety,
        (uint Serial, ushort ItemId, byte Layer, ushort Hue)[] equipment)
        : base(0x78)
    {
        _serial = serial; _bodyId = bodyId; _x = x; _y = y; _z = z;
        _dir = dir; _hue = hue; _flags = flags; _notoriety = notoriety;
        _equipment = equipment;
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
            if (hue != 0)
            {
                buf.WriteUInt16((ushort)(itemId | 0x8000));
                buf.WriteByte(layer);
                buf.WriteUInt16(hue);
            }
            else
            {
                buf.WriteUInt16(itemId);
                buf.WriteByte(layer);
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
            ushort nameLen = (ushort)(item.Name.Length + 1);
            buf.WriteUInt16(nameLen);
            buf.WriteAsciiNull(item.Name);
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

    public PacketWorldItem(uint serial, ushort itemId, ushort amount, short x, short y, sbyte z,
        ushort hue, byte direction = 0, byte flags = 0)
        : base(0x1A)
    {
        _serial = serial; _itemId = itemId; _amount = amount;
        _x = x; _y = y; _z = z; _hue = hue;
        _direction = direction; _flags = flags;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(24);
        uint ser = _serial;
        if (_amount > 1) ser |= 0x80000000;

        buf.WriteUInt32(ser);
        buf.WriteUInt16(_itemId);

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
        var buf = CreateVariable(16 + _entries.Length * 8);
        buf.WriteUInt16(0x14); // sub-command
        buf.WriteUInt16(0x0001); // new-style context menu
        buf.WriteUInt32(_serial);
        buf.WriteByte((byte)_entries.Length);

        foreach (var (entryTag, clilocId, flags) in _entries)
        {
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
        int len = 7 + _memberSerials.Length * 4; // cmd(2) + sub(2) + subSub(1) + count(1) + serials
        var buf = CreateVariable(len);
        buf.WriteUInt16(0x0006); // sub-command: party
        buf.WriteByte(0x01);    // sub-sub: member list
        buf.WriteByte((byte)_memberSerials.Length);
        foreach (var s in _memberSerials)
            buf.WriteUInt32(s);
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
        int len = 11 + _remainingMembers.Length * 4;
        var buf = CreateVariable(len);
        buf.WriteUInt16(0x0006);
        buf.WriteByte(0x02); // sub-sub: remove member
        buf.WriteByte((byte)_remainingMembers.Length);
        buf.WriteUInt32(_removedSerial);
        foreach (var s in _remainingMembers)
            buf.WriteUInt32(s);
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
    private readonly string _title;
    private readonly string _desc;

    public PacketBuffIcon(uint serial, ushort iconId, bool add, ushort durationSeconds = 0,
        string title = "", string desc = "") : base(0xDF)
    {
        _serial = serial;
        _iconId = iconId;
        _add = add;
        _durationSeconds = durationSeconds;
        _title = title;
        _desc = desc;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(64);
        buf.WriteUInt32(_serial);
        buf.WriteUInt16(_iconId);
        buf.WriteUInt16(_add ? (ushort)0x0001 : (ushort)0x0000); // type: 0=remove, 1=add

        if (_add)
        {
            // Layout mirrors ServUO AddBuffPacket (Scripts/Misc/BuffIcons.cs).
            // The fields the client actually reads are iconID x2, the
            // duration short, the title/description clilocs, and the
            // argument string; everything else is reserved zero padding.
            buf.WriteBytes(new byte[4]);          // reserved
            buf.WriteUInt16(_iconId);             // icon id (repeated)
            buf.WriteUInt16(0x0001);              // type flag (again)
            buf.WriteBytes(new byte[4]);          // reserved
            buf.WriteUInt16(_durationSeconds);    // seconds
            buf.WriteBytes(new byte[3]);          // reserved

            // Title / secondary clilocs. We don't have a cliloc table for
            // custom spells, so use 1114778 ("<a href ...>") for title and
            // 0 for description — same trick ServUO uses for scripted
            // buffs that want raw text via the args field.
            buf.WriteUInt32(1114778);             // titleCliloc
            buf.WriteUInt32(0);                   // secondaryCliloc

            if (string.IsNullOrEmpty(_title))
            {
                // No args — 10 bytes of padding per ServUO.
                buf.WriteBytes(new byte[10]);
            }
            else
            {
                buf.WriteBytes(new byte[4]);      // reserved
                buf.WriteUInt16(0x0001);          // "more data" marker
                buf.WriteBytes(new byte[2]);      // reserved
                // Args — ServUO prefixes "\t" so the cliloc formatter
                // treats the rest as a single argument. Null-terminated
                // little-endian unicode.
                WriteLittleUniNull(buf, "\t" + _title);
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

/// <summary>0xC1 — Localized (cliloc) message. Used by SYSMESSAGELOC and
/// any overhead text that needs cliloc placeholder substitution. Args are
/// '\t'-delimited unicode, terminated by an extra '\t'.</summary>
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

    public PacketArrowQuest(bool active, ushort x, ushort y) : base(0xBA)
    {
        _active = active;
        _x = x;
        _y = y;
    }

    public override PacketBuffer Build()
    {
        // Modern clients (7.0.x) accept a 10-byte variant with a trailing
        // serial; legacy 0.21/older use the 6-byte form. We emit 10 so
        // arrows survive KR/EC targeting too.
        var buf = CreateFixed(10);
        buf.WriteByte((byte)(_active ? 1 : 0));
        buf.WriteUInt16(_x);
        buf.WriteUInt16(_y);
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

        buf.WriteByte((byte)Math.Min(_items.Count, 255));
        foreach (var item in _items)
        {
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

