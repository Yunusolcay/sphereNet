using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Ships;

/// <summary>
/// Ship instance. Maps to CItemShip in Source-X CItemShip.h.
/// Tracks multi item, hold, planks, tiller and movement state.
/// </summary>
public sealed class Ship
{
    private readonly Item _multiItem;
    private Serial _hold = Serial.Invalid;
    private readonly List<Serial> _planks = [];
    private Serial _owner = Serial.Invalid;
    private Serial _pilot = Serial.Invalid;
    private bool _anchored;
    private Direction _dirFace = Direction.North;
    private Direction _dirMove = Direction.North;
    private ShipMovementType _movementType = ShipMovementType.Stop;
    private ushort _speedPeriod = 500;
    private byte _speedTiles = 1;
    // Source-X CCMultiMovable's constructor starts a ship at SMS_NORMAL, which
    // is the value 1 its own comment labels "one tile" (SphereNet spells that
    // ShipSpeedMode.OneTile). The distinction matters because the move delay
    // only stretches to the full period in the SLOW mode: starting at Slow made
    // every ship driven by a tillerman/script command sail at half the reference
    // speed, since only a wheel order ever re-selected the mode.
    private ShipSpeedMode _speedMode = ShipSpeedMode.OneTile;
    private long _nextMoveTick;
    private readonly List<Serial> _components = [];

    // Source-X CItemShip / CCMultiMovable — the dynamic world region tracking the
    // hull's footprint (REGION_FLAG_SHIP). Recomputed as the ship sails. 0 = none.
    private uint _regionUid;

    // Players barred from boarding (Source-X multi ban list, enforced through the
    // ship region). The owner always boards; everyone else is blocked only when banned.
    private readonly HashSet<Serial> _bans = [];

    public Item MultiItem => _multiItem;
    public Serial Owner { get => _owner; set => _owner = value; }
    public Serial Pilot { get => _pilot; set => _pilot = value; }
    public bool Anchored { get => _anchored; set => _anchored = value; }
    public Direction DirFace { get => _dirFace; set => _dirFace = value; }
    public Direction DirMove { get => _dirMove; set => _dirMove = value; }
    public ShipMovementType MovementType { get => _movementType; set => _movementType = value; }
    public ushort SpeedPeriod { get => _speedPeriod; set => _speedPeriod = value; }
    public byte SpeedTiles { get => _speedTiles; set => _speedTiles = value; }
    public ShipSpeedMode SpeedMode { get => _speedMode; set => _speedMode = value; }
    public long NextMoveTick { get => _nextMoveTick; set => _nextMoveTick = value; }
    public IReadOnlyList<Serial> Components => _components;
    public uint RegionUid { get => _regionUid; set => _regionUid = value; }
    public IReadOnlyCollection<Serial> Bans => _bans;

    public bool AddBan(Serial uid) => _bans.Add(uid);
    public bool RemoveBan(Serial uid) => _bans.Remove(uid);
    public bool IsBanned(Serial uid) => _bans.Contains(uid);

    /// <summary>Whether a character may board / stay on the ship. The owner always
    /// may; everyone else is barred only when on the ban list (Source-X multi ban).</summary>
    public bool CanBoard(Serial uid) => uid == _owner || !_bans.Contains(uid);

    public Ship(Item multiItem)
    {
        _multiItem = multiItem;
    }

    /// <summary>
    /// Get tiller item via multiItem.Link (Source-X m_uidLink pattern).
    /// </summary>
    public Item? GetTiller(GameWorld world)
    {
        if (_multiItem.Link.IsValid)
            return world.FindItem(_multiItem.Link);
        // Fallback: search components for IT_SHIP_TILLER
        foreach (var uid in _components)
        {
            var item = world.FindItem(uid);
            if (item?.ItemType == ItemType.ShipTiller)
                return item;
        }
        return null;
    }

    /// <summary>Get the ship hold container item.</summary>
    public Item? GetHold(GameWorld world)
    {
        if (_hold.IsValid)
            return world.FindItem(_hold);
        // Fallback: search components
        foreach (var uid in _components)
        {
            var item = world.FindItem(uid);
            if (item?.ItemType is ItemType.ShipHold or ItemType.ShipHoldLock)
            {
                _hold = uid;
                return item;
            }
        }
        return null;
    }

    /// <summary>Get plank item by index.</summary>
    public Item? GetPlank(int index, GameWorld world)
    {
        if (index < 0 || index >= _planks.Count) return null;
        return world.FindItem(_planks[index]);
    }

    /// <summary>Number of plank items.</summary>
    public int GetPlankCount() => _planks.Count;

    /// <summary>
    /// Add a component item. Automatically categorizes hold/plank by ItemType.
    /// Maps to OnComponentCreate pattern in Source-X CItemShip.cpp.
    /// </summary>
    public void AddComponent(Item item)
    {
        if (!_components.Contains(item.Uid))
            _components.Add(item.Uid);

        switch (item.ItemType)
        {
            case ItemType.ShipHold:
            case ItemType.ShipHoldLock:
                _hold = item.Uid;
                break;
            case ItemType.ShipPlank:
            case ItemType.ShipSide:
            case ItemType.ShipSideLocked:
                if (!_planks.Contains(item.Uid))
                    _planks.Add(item.Uid);
                break;
            case ItemType.ShipTiller:
                _multiItem.Link = item.Uid;
                break;
        }
    }

    /// <summary>Add a component by UID only (used during deserialization).</summary>
    public void AddComponentUid(Serial uid)
    {
        if (uid.IsValid && !_components.Contains(uid))
            _components.Add(uid);
    }
}
