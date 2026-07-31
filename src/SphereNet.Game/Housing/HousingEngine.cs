using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Housing;

/// <summary>
/// House privilege levels. Maps to HOUSE_PRIV in Source-X CItemMulti.h.
/// </summary>
public enum HousePriv : byte
{
    None = 0,
    Owner,
    CoOwner,
    Friend,
    AccessOnly,
    Ban,
    Vendor,
    Guild,
    Qty,
}

/// <summary>
/// House type. Maps to HOUSE_TYPE in Source-X.
/// </summary>
public enum HouseType : byte
{
    Private = 0,
    Public,
    Guild,
}

/// <summary>
/// A multi component record (from multi.mul / scripts).
/// Maps to CUOMultiItemRec_HS in Source-X.
/// </summary>
public readonly struct MultiComponent
{
    public ushort TileId { get; init; }
    public short DeltaX { get; init; }
    public short DeltaY { get; init; }
    public short DeltaZ { get; init; }
    public bool Visible { get; init; }
}

/// <summary>
/// Multi definition (template). Maps to CItemBaseMulti in Source-X.
/// Loaded from multi.mul and/or [MULTIDEF] script sections.
/// </summary>
public sealed class MultiDef
{
    public ushort Id { get; init; }
    public string Name { get; set; } = "";

    /// <summary>Script [MULTIDEF] TYPE (t_multi / t_multi_custom / t_ship). Empty until
    /// the script metadata is merged in — see MultiRegistry.MergeScriptMetadata.</summary>
    public string MultiTypeName { get; set; } = "";

    /// <summary>Script [MULTIDEF] BaseStorage (secure/lockdown budget); 0 = unset.</summary>
    public int BaseStorage { get; set; }

    /// <summary>Script [MULTIDEF] BaseVendors (max house vendors); 0 = unset.</summary>
    public int BaseVendors { get; set; }

    /// <summary>Script [MULTIDEF] SHIPSPEED=period,tiles. Period is TENTHS of a
    /// second between movement steps (Source-X MSECS_PER_TENTH), tiles is the
    /// distance moved per step. 0 = unset (a placed ship keeps the engine default).</summary>
    public int ShipSpeedPeriodTenths { get; set; }
    public int ShipSpeedTiles { get; set; }

    public List<MultiComponent> Components { get; } = [];

    // Bounding rect
    public short MinX { get; set; }
    public short MinY { get; set; }
    public short MaxX { get; set; }
    public short MaxY { get; set; }

    public void RecalcBounds()
    {
        if (Components.Count == 0)
        {
            MinX = MinY = MaxX = MaxY = 0;
            return;
        }
        MinX = MinY = short.MaxValue;
        MaxX = MaxY = short.MinValue;
        foreach (var c in Components)
        {
            if (c.DeltaX < MinX) MinX = c.DeltaX;
            if (c.DeltaY < MinY) MinY = c.DeltaY;
            if (c.DeltaX > MaxX) MaxX = c.DeltaX;
            if (c.DeltaY > MaxY) MaxY = c.DeltaY;
        }
    }
}

/// <summary>Why a house/ship placement was rejected (B4 structured result). Lets the
/// deed handler show a specific message instead of one generic "Cannot place here".</summary>
public enum PlacementFailure
{
    None = 0,
    PlayerLimitReached,
    AccountLimitReached,
    MultiDefinitionMissing,
    OutOfMap,
    LocationBlocked,   // terrain / overlap / (ship) water — the CanPlace* checks
    ScriptVeto,        // @HouseCheck vetoed
}

/// <summary>
/// House decay stage. Maps to HOUSE_DECAY_STAGE in Source-X.
/// </summary>
public enum HouseDecayStage : byte
{
    LikeNew = 0,
    SlightlyWorn = 1,
    SomewhatWorn = 2,
    FairlyWorn = 3,
    GreatlyWorn = 4,
    InDangerOfCollapsing = 5, // IDOC
}

/// <summary>
/// House instance (placed multi item in the world).
/// Maps to CItemMulti in Source-X CItemMulti.h.
/// </summary>
public sealed class House
{
    private readonly Item _multiItem;
    private Serial _owner = Serial.Invalid;
    private Serial _guildStone = Serial.Invalid;
    private HouseType _houseType = HouseType.Private;

    private readonly HashSet<Serial> _coOwners = [];
    private readonly HashSet<Serial> _friends = [];
    private readonly HashSet<Serial> _bans = [];
    private readonly HashSet<Serial> _accessList = [];
    private readonly HashSet<Serial> _lockdowns = [];
    private readonly HashSet<Serial> _secureContainers = [];
    private readonly List<Serial> _components = [];
    private bool _redeeded;
    private readonly List<Serial> _vendors = [];

    private int _baseStorage = 400;
    private int _lockdownsPercent = 50;
    private int _baseVendors;
    private int _increasedStorage;

    // Decay tracking
    private long _lastRefreshTick;
    private HouseDecayStage _decayStage = HouseDecayStage.LikeNew;

    // Source-X CItemMulti::MultiRealizeRegion — the dynamic world region created
    // for this house's footprint (REGION_FLAG_HOUSE). 0 = none.
    private uint _regionUid;

    public Item MultiItem => _multiItem;
    public Serial Owner { get => _owner; set => _owner = value; }
    public HouseType Type { get => _houseType; set => _houseType = value; }
    public Serial GuildStone { get => _guildStone; set => _guildStone = value; }
    /// <summary>Source-X CItemMulti::GetMaxStorage — the base budget plus the
    /// percentage bonus an upgraded house carries.</summary>
    public int MaxStorage => _baseStorage + _baseStorage * _increasedStorage / 100;

    /// <summary>Source-X CItemMulti::GetCurrentStorage — lockdowns and secured
    /// containers draw on the same budget.</summary>
    public int CurrentStorage => _lockdowns.Count + _secureContainers.Count;

    /// <summary>Source-X CItemMulti::GetMaxLockdowns, whose expression reduces to
    /// maxStorage * lockdownsPercent / 100.</summary>
    public int MaxLockdowns => MaxStorage * _lockdownsPercent / 100;
    public int MaxSecure => MaxStorage - MaxLockdowns;

    /// <summary>Source-X CItemMulti::GetMaxVendors.</summary>
    public int MaxVendors => _baseVendors + _baseVendors * _increasedStorage / 100;

    public IReadOnlyList<Serial> Components => _components;
    public int BaseStorage { get => _baseStorage; set => _baseStorage = Math.Max(0, value); }

    /// <summary>Script [MULTIDEF] BaseVendors / the BASEVENDORS property.</summary>
    public int BaseVendors { get => _baseVendors; set => _baseVendors = Math.Max(0, value); }

    /// <summary>Percentage bonus applied to storage and vendor caps (Source-X
    /// INCREASEDSTORAGE). 0 = no bonus, so an unscripted house is unchanged.</summary>
    public int IncreasedStorage { get => _increasedStorage; set => _increasedStorage = Math.Max(0, value); }

    /// <summary>Share of the storage budget usable for lockdowns rather than
    /// secured containers (Source-X LOCKDOWNSPERCENT).</summary>
    public int LockdownsPercent
    {
        get => _lockdownsPercent;
        set => _lockdownsPercent = Math.Clamp(value, 0, 100);
    }
    public long LastRefreshTick { get => _lastRefreshTick; set => _lastRefreshTick = value; }
    public HouseDecayStage DecayStage { get => _decayStage; set => _decayStage = value; }
    public uint RegionUid { get => _regionUid; set => _regionUid = value; }
    public IReadOnlyCollection<Serial> CoOwners => _coOwners;
    public IReadOnlyCollection<Serial> Friends => _friends;
    public IReadOnlyCollection<Serial> Bans => _bans;
    public IReadOnlyCollection<Serial> AccessList => _accessList;
    public IReadOnlyCollection<Serial> Lockdowns => _lockdowns;
    public IReadOnlyCollection<Serial> SecureContainers => _secureContainers;

    public House(Item multiItem)
    {
        _multiItem = multiItem;
        _lastRefreshTick = Environment.TickCount64;
    }

    public HousePriv GetPriv(Serial charUid)
    {
        if (charUid == _owner) return HousePriv.Owner;
        if (_bans.Contains(charUid)) return HousePriv.Ban;
        if (_coOwners.Contains(charUid)) return HousePriv.CoOwner;
        if (_friends.Contains(charUid)) return HousePriv.Friend;
        if (_accessList.Contains(charUid)) return HousePriv.AccessOnly;
        if (_vendors.Contains(charUid)) return HousePriv.Vendor;
        return HousePriv.None;
    }

    private void RevokeListedPrivileges(Serial uid)
    {
        _coOwners.Remove(uid);
        _friends.Remove(uid);
        _bans.Remove(uid);
        _accessList.Remove(uid);
        _vendors.Remove(uid);
    }

    public bool AddCoOwner(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _coOwners.Contains(uid)) return false;
        RevokeListedPrivileges(uid);
        return _coOwners.Add(uid);
    }
    public bool RemoveCoOwner(Serial uid) => _coOwners.Remove(uid);
    public bool AddFriend(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _friends.Contains(uid)) return false;
        RevokeListedPrivileges(uid);
        return _friends.Add(uid);
    }
    public bool RemoveFriend(Serial uid) => _friends.Remove(uid);
    public bool AddBan(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _bans.Contains(uid)) return false;
        RevokeListedPrivileges(uid);
        return _bans.Add(uid);
    }
    public bool RemoveBan(Serial uid) => _bans.Remove(uid);
    public bool AddAccess(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _accessList.Contains(uid)) return false;
        RevokeListedPrivileges(uid);
        return _accessList.Add(uid);
    }
    public bool RemoveAccess(Serial uid) => _accessList.Remove(uid);

    public bool CanAccess(Serial charUid)
    {
        var priv = GetPriv(charUid);
        if (priv == HousePriv.Ban) return false;
        return _houseType == HouseType.Public || priv != HousePriv.None;
    }

    public bool CanLockdown(Serial charUid) =>
        GetPriv(charUid) is HousePriv.Owner or HousePriv.CoOwner;

    /// <summary>Lock down an item in the house. Source-X CItemMulti::LockItem:
    /// the item itself gains ATTR_LOCKEDDOWN and links back to the multi —
    /// previously only the house-side hash set was updated, so anything that
    /// reads the item's attributes (WalkCheck, scripts) saw it as loose.</summary>
    public bool Lockdown(Serial itemUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (_lockdowns.Contains(itemUid) || _secureContainers.Contains(itemUid)) return false;
        if (_lockdowns.Count >= MaxLockdowns) return false;
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(itemUid);
        if (item == null || item.IsDeleted || !item.IsOnGround || item.IsEquipped) return false;
        // Source-X: only items INSIDE the house region may be locked down —
        // an owner could previously lock items anywhere in the world.
        if (!IsInsideHouse(item.Position)) return false;
        _lockdowns.Add(itemUid);
        item.SetAttr(SphereNet.Core.Enums.ObjAttributes.LockedDown);
        item.Link = _multiItem.Uid;
        return true;
    }

    /// <summary>Inside-the-house-region test for lockdown/secure targets.
    /// A house with no realized region (bare tests) accepts everything.</summary>
    private bool IsInsideHouse(Core.Types.Point3D pos)
    {
        if (_regionUid == 0) return true;
        var region = Objects.ObjBase.ResolveWorld?.Invoke()?.FindRegionByUid(_regionUid);
        return region == null || region.Contains(pos);
    }

    /// <summary>Release a locked down item (Source-X UnlockItem).</summary>
    public bool ReleaseLockdown(Serial itemUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (!_lockdowns.Remove(itemUid)) return false;
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(itemUid);
        if (item != null)
        {
            item.ClearAttr(SphereNet.Core.Enums.ObjAttributes.LockedDown);
            if (item.Link == _multiItem.Uid)
                item.Link = Serial.Invalid;
        }
        return true;
    }

    /// <summary>Secure a container in the house (Source-X Secure: ATTR_SECURE
    /// on the container + link to the multi).</summary>
    public bool SecureContainer(Serial containerUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (_lockdowns.Contains(containerUid)) return false;
        if (_secureContainers.Count >= MaxSecure) return false;
        if (_secureContainers.Contains(containerUid)) return false;
        var secureItem = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(containerUid);
        if (secureItem == null || secureItem.IsDeleted || !secureItem.IsOnGround || secureItem.IsEquipped)
            return false;
        if (secureItem.ItemType is not (ItemType.Container or ItemType.ContainerLocked))
            return false;
        if (!IsInsideHouse(secureItem.Position)) return false;
        _secureContainers.Add(containerUid);
        secureItem.SetAttr(SphereNet.Core.Enums.ObjAttributes.Secure);
        secureItem.Link = _multiItem.Uid;
        return true;
    }

    /// <summary>Release a secured container.</summary>
    public bool ReleaseSecure(Serial containerUid, Serial byChar)
    {
        if (!CanLockdown(byChar)) return false;
        if (!_secureContainers.Remove(containerUid)) return false;
        var item = Objects.ObjBase.ResolveWorld?.Invoke()?.FindItem(containerUid);
        if (item != null)
        {
            item.ClearAttr(SphereNet.Core.Enums.ObjAttributes.Secure);
            if (item.Link == _multiItem.Uid)
                item.Link = Serial.Invalid;
        }
        return true;
    }

    /// <summary>Restore a persisted lockdown/secure on world load WITHOUT the priv
    /// or capacity checks. A saved house may legitimately hold more than the
    /// current MaxLockdowns/MaxSecure (config or storage changed since the save);
    /// re-running the normal checks would silently drop the overflow entries.</summary>
    public void LockdownForLoad(Serial itemUid) => _lockdowns.Add(itemUid);
    public void SecureForLoad(Serial containerUid) => _secureContainers.Add(containerUid);

    /// <summary>Check if an item is locked down or secured.</summary>
    public bool IsLockedDown(Serial itemUid) => _lockdowns.Contains(itemUid);
    public bool IsSecured(Serial itemUid) => _secureContainers.Contains(itemUid);

    /// <summary>Add a house component item UID.</summary>
    public void AddComponent(Serial uid)
    {
        if (uid.IsValid && !_components.Contains(uid))
            _components.Add(uid);
    }
    public bool RemoveComponent(Serial uid) => _components.Remove(uid);

    /// <summary>Add a vendor NPC to the house.</summary>
    public void AddVendor(Serial uid)
    {
        if (!uid.IsValid || uid == _owner || _vendors.Contains(uid)) return;
        RevokeListedPrivileges(uid);
        _vendors.Add(uid);
    }
    public bool RemoveVendor(Serial uid) => _vendors.Remove(uid);
    public IReadOnlyList<Serial> Vendors => _vendors;

    /// <summary>Transfer ownership to another character.</summary>
    public void TransferOwnership(Serial newOwnerUid)
    {
        if (!newOwnerUid.IsValid) return;
        RevokeListedPrivileges(newOwnerUid);
        _owner = newOwnerUid;
        Refresh();
    }

    /// <summary>Refresh the house (reset decay timer).</summary>
    public void Refresh()
    {
        _lastRefreshTick = Environment.TickCount64;
        _decayStage = HouseDecayStage.LikeNew;
    }

    /// <summary>Fired when a house is converted back to a deed (Source-X @Redeed).
    /// Arg: the freshly-created deed item.</summary>
    public static Action<Item>? OnRedeed { get; set; }

    /// <summary>Transfer ownership (deed back). Preserves multi UUID in deed tag.</summary>
    public Item? Redeed(GameWorld world)
    {
        if (_redeeded) return null;
        _redeeded = true;
        var deed = world.CreateItem();
        deed.BaseId = 0x14F0; // ITEMID_DEED1
        deed.ItemType = ItemType.Deed;
        deed.More1 = _multiItem.BaseId;
        deed.Hue = _multiItem.Hue;
        if (_multiItem.IsAttr(ObjAttributes.Magic))
            deed.SetAttr(ObjAttributes.Magic);
        if (_multiItem.ItemType == ItemType.MultiCustom)
            deed.SetTag("CUSTOMHOUSE", "1");
        deed.Name = _multiItem.Name + " deed";
        deed.SetTag("HOUSE_MULTI_UUID", _multiItem.Uuid.ToString("D"));
        deed.SetTag("HOUSE_MULTI_BASEID", _multiItem.BaseId.ToString());
        // @Redeed (Source-X) — the house is now a deed item.
        OnRedeed?.Invoke(deed);

        // Remove all component items (the structure itself)
        foreach (var compUid in _components)
        {
            var item = world.FindItem(compUid);
            if (item != null)
                world.RemoveItem(item);
        }
        _components.Clear();

        // Source-X collapse: locked-down / secured PLAYER items are NOT structure
        // components (must not be deleted) and must NOT be scattered on the ground
        // to decay individually (item loss). They go into a MOVING CRATE — each
        // item is reparented into the crate (a secured container moves whole), and
        // the crate is delivered to the owner's bank box when reachable, otherwise
        // dropped on the house tile. Reparenting (not copying) means no dupe.
        var protectedUids = _lockdowns.Concat(_secureContainers).ToList();

        // Source-X TransferAllItemsToMovingCrate(TRANSFER_ALL): LOOSE items left
        // inside the house footprint also go to the crate — previously they were
        // orphaned on the ground (lost their house link, decayed away).
        var footprint = _regionUid != 0 ? world.FindRegionByUid(_regionUid) : null;
        if (footprint != null)
        {
            var protectedSet = new HashSet<Serial>(protectedUids);
            foreach (var rect in footprint.Rects)
            {
                int range = Math.Max(rect.X2 - rect.X1, rect.Y2 - rect.Y1) / 2 + 1;
                var center = new Point3D(
                    (short)((rect.X1 + rect.X2) / 2), (short)((rect.Y1 + rect.Y2) / 2),
                    _multiItem.Z, _multiItem.MapIndex);
                foreach (var loose in world.GetItemsInRange(center, range).ToList())
                {
                    if (loose.IsDeleted || !loose.IsOnGround) continue;
                    if (loose == _multiItem || _components.Contains(loose.Uid)) continue;
                    if (loose.IsAttr(Core.Enums.ObjAttributes.Static) ||
                        loose.IsAttr(Core.Enums.ObjAttributes.Move_Never)) continue;
                    if (!footprint.Contains(loose.Position)) continue;
                    protectedSet.Add(loose.Uid);
                }
            }
            protectedUids = protectedSet.ToList();
        }

        if (protectedUids.Count > 0)
        {
            var protectedItems = new List<Item>();
            foreach (var protUid in protectedUids)
            {
                var item = world.FindItem(protUid);
                if (item == null || item.IsDeleted) continue;
                item.ClearAttr(Core.Enums.ObjAttributes.LockedDown | Core.Enums.ObjAttributes.Secure);
                if (item.Link == _multiItem.Uid)
                    item.Link = Serial.Invalid;
                // Detach from its current container or ground sector, then place it
                // in the crate (a fresh slot). Contents of a secured container ride
                // along inside it.
                if (item.ContainedIn.IsValid)
                    world.FindItem(item.ContainedIn)?.RemoveItem(item);
                else
                    world.HideFromSector(item);
                item.DecayTime = 0; // protected inside the crate
                protectedItems.Add(item);
            }

            var owner = _owner.IsValid ? world.FindChar(_owner) : null;
            var bank = owner?.GetEquippedItem(Core.Enums.Layer.BankBox);
            while (protectedItems.Count > 0)
            {
                var crate = world.CreateItem();
                crate.BaseId = 0x0E3D; // ITEMID_CRATE1 (wooden crate)
                crate.ItemType = Core.Enums.ItemType.Container;
                crate.Name = "a moving crate";
                while (protectedItems.Count > 0 && crate.Contents.Count < Item.MaxContainerItems)
                {
                    var item = protectedItems[^1];
                    protectedItems.RemoveAt(protectedItems.Count - 1);
                    item.Position = new Point3D(0, 0, 0, crate.MapIndex);
                    crate.TryAddItem(item);
                }
                if (bank == null || !bank.TryAddItem(crate))
                    world.PlaceItemWithDecay(crate, _multiItem.Position);
            }
        }
        _lockdowns.Clear();
        _secureContainers.Clear();

        // Remove the multi item itself
        world.RemoveItem(_multiItem);

        return deed;
    }
}

/// <summary>
/// Multi definition registry. Loads from multi.mul files.
/// </summary>
public sealed class MultiRegistry
{
    private readonly Dictionary<ushort, MultiDef> _defs = [];

    public void Register(MultiDef def) => _defs[def.Id] = def;
    public MultiDef? Get(ushort id) => _defs.GetValueOrDefault(id);
    public int Count => _defs.Count;

    /// <summary>
    /// Load multi definitions from MapData's MultiReader.
    /// House multis typically start at 0x0 in multi.mul.
    /// Common house IDs: small stone (0x64), small plaster (0x66), etc.
    /// </summary>
    public int LoadFromMapData(MapData.MapDataManager mapData, int maxId = 0x3000)
    {
        int loaded = 0;
        for (int id = 0; id < maxId; id++)
        {
            var mulDef = mapData.GetMulti(id);
            if (mulDef == null || mulDef.Components.Length == 0) continue;

            var def = new MultiDef { Id = (ushort)id };
            foreach (var comp in mulDef.Components)
            {
                def.Components.Add(new MultiComponent
                {
                    TileId = comp.TileId,
                    DeltaX = comp.XOffset,
                    DeltaY = comp.YOffset,
                    DeltaZ = comp.ZOffset,
                    Visible = comp.IsVisible
                });
            }
            def.RecalcBounds();
            Register(def);
            loaded++;
        }
        return loaded;
    }

    /// <summary>Merge script [MULTIDEF] metadata (NAME / TYPE / BaseStorage / BaseVendors)
    /// onto the geometry loaded from multi.mul, keyed by the shared multi id. Source-X
    /// keeps both under one CItemBaseMulti; SphereNet had only the binary geometry, so
    /// placed structures used a blank name and a hardcoded storage default. Metadata for
    /// an id with no geometry is skipped (it cannot be placed). Returns the merge count.</summary>
    public int MergeScriptMetadata(ResourceHolder resources)
    {
        int merged = 0;
        foreach (var link in resources.GetAllResources())
        {
            if (link.Id.Type != Core.Enums.ResType.MultiDef || link.StoredKeys == null)
                continue;
            var def = Get((ushort)link.Id.Index);
            if (def == null)
                continue;

            foreach (var key in link.StoredKeys)
            {
                string arg = key.Arg.Trim();
                switch (key.Key.ToUpperInvariant())
                {
                    case "NAME":
                        if (arg.Length > 0) def.Name = arg;
                        break;
                    case "TYPE":
                        def.MultiTypeName = arg;
                        break;
                    case "BASESTORAGE":
                        if (int.TryParse(arg, out int st)) def.BaseStorage = st;
                        break;
                    case "BASEVENDORS":
                        if (int.TryParse(arg, out int bv)) def.BaseVendors = bv;
                        break;
                    case "SHIPSPEED":
                        {
                            // SHIPSPEED=period,tiles (period in tenths of a second).
                            var sp = arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            if (sp.Length >= 2 &&
                                int.TryParse(sp[0], out int period) && period > 0 &&
                                int.TryParse(sp[1], out int tiles) && tiles > 0)
                            {
                                def.ShipSpeedPeriodTenths = period;
                                def.ShipSpeedTiles = tiles;
                            }
                        }
                        break;
                }
            }
            merged++;
        }
        return merged;
    }
}

/// <summary>
/// Housing engine: placement, deed, access control.
/// Maps to CItemMulti::Multi_Create flow in Source-X.
/// </summary>
public sealed class HousingEngine
{
    private readonly GameWorld _world;
    private readonly MultiRegistry _multiDefs;
    private readonly Dictionary<Serial, House> _houses = [];

    public int MaxHousesPerPlayer { get; set; } = 1;
    public int MaxHousesPerAccount { get; set; } = 1;

    /// <summary>Source-X @AddMulti notification for the owner's scripted
    /// CMultiStorage. SphereNet's native registry remains authoritative.</summary>
    public Action<Character, Item, HousePriv>? OnAddMulti { get; set; }

    public HousingEngine(GameWorld world, MultiRegistry multiDefs)
    {
        _world = world;
        _multiDefs = multiDefs;
        world.ObjectDeleting += OnWorldObjectDeleting;
    }

    /// <summary>Multi item deleted OUTSIDE the redeed path (.nuke, .remove,
    /// script REMOVE). Source-X ~CItemMulti tears the whole structure down:
    /// keys removed, components deleted, region unrealized, erased from
    /// g_World.m_Multis — otherwise the registry keeps a ghost house that
    /// still counts against MaxHousesPerPlayer/Account. No deed is created
    /// (a destroyed house is not refunded). Engine-driven paths unregister
    /// before deleting the multi, so this only fires for external deletions.</summary>
    private void OnWorldObjectDeleting(SphereNet.Game.Objects.ObjBase obj)
    {
        if (obj is not Item it) return;
        if (!_houses.TryGetValue(it.Uid, out var house) || !ReferenceEquals(house.MultiItem, it))
            return;
        _houses.Remove(it.Uid);
        var owner = house.Owner.IsValid ? _world.FindChar(house.Owner) : null;
        RemoveStructureKeys(owner, it.Uid);
        var ownerMemory = owner?.Memory_FindObjTypes(it.Uid, MemoryType.Guard);
        if (owner != null && ownerMemory != null)
            owner.Memory_ClearTypes(ownerMemory, MemoryType.Guard);
        foreach (var compUid in house.Components)
        {
            var comp = _world.FindItem(compUid);
            if (comp != null && !comp.IsDeleted)
                _world.RemoveItem(comp);
        }
        RemoveHouseRegion(house);
    }

    public House? GetHouse(Serial multiItemUid) =>
        _houses.GetValueOrDefault(multiItemUid);

    /// <summary>Register an already-placed multi item as a house instance,
    /// reading ownership from its HOUSE.OWNER tag. Called by the
    /// MULTICREATE script verb right after SERV.NEWITEM so the region
    /// tracker knows about the new house before the next save cycle.</summary>
    public House? RegisterExistingMulti(Item multiItem)
    {
        if (multiItem.ItemType is not (ItemType.Multi or ItemType.MultiCustom))
            return null;
        if (_houses.ContainsKey(multiItem.Uid))
            return _houses[multiItem.Uid];
        var house = CreateHouseFromTags(multiItem);
        if (house == null) return null;
        _houses[multiItem.Uid] = house;
        CreateHouseRegion(house);
        return house;
    }

    /// <summary>
    /// Place a new house at the given position.
    /// Returns null if placement is invalid.
    /// When <paramref name="customFoundation"/> is true the multi becomes a
    /// customizable foundation (ItemType.MultiCustom): no real component items
    /// are materialized — the client renders the foundation multi and the
    /// committed design (0xD8) itself, and server walk geometry comes from
    /// WalkCheck's virtual multi/design components.
    /// </summary>
    /// <summary>Fired before a house is placed (Source-X @HouseCheck). Args: the
    /// placing character and the chosen anchor point. Return true to VETO the
    /// placement (the engine's own NoBuild/footprint/terrain checks ran first).</summary>
    public static Func<Character, Point3D, bool>? OnHouseCheck { get; set; }

    public House? PlaceHouse(Character owner, ushort multiId, Point3D position,
        bool customFoundation = false, bool magic = false)
        => PlaceHouse(owner, multiId, position, out _, customFoundation, magic);

    /// <summary>Whether a multi id is a customizable foundation (script MULTIDEF
    /// TYPE=t_multi_custom). Lets deed handling detect custom houses from the resolved
    /// definition rather than only a CUSTOMHOUSE deed tag (B13).</summary>
    public bool IsCustomFoundation(ushort multiId) =>
        _multiDefs.Get(multiId)?.MultiTypeName.Equals("t_multi_custom", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Place a house, reporting the specific failure reason (B4).</summary>
    public House? PlaceHouse(Character owner, ushort multiId, Point3D position,
        out PlacementFailure failure, bool customFoundation = false, bool magic = false)
    {
        failure = PlacementFailure.None;

        if (MaxHousesPerPlayer >= 0 && GetHousesByOwner(owner.Uid).Count >= MaxHousesPerPlayer)
        { failure = PlacementFailure.PlayerLimitReached; return null; }

        if (MaxHousesPerAccount >= 0 && GetHouseCountForAccount(owner) >= MaxHousesPerAccount)
        { failure = PlacementFailure.AccountLimitReached; return null; }

        var def = _multiDefs.Get(multiId);
        if (def == null) { failure = PlacementFailure.MultiDefinitionMissing; return null; }
        var (mapWidth, mapHeight) = _world.MapData?.GetMapSize(position.Map) ?? (7168, 4096);
        if (position.X + def.MinX < 0 || position.Y + def.MinY < 0 ||
            position.X + def.MaxX >= mapWidth || position.Y + def.MaxY >= mapHeight)
        { failure = PlacementFailure.OutOfMap; return null; }

        // Check placement area
        if (!magic && !CanPlaceHouse(position, def))
        { failure = PlacementFailure.LocationBlocked; return null; }

        // @HouseCheck (Source-X) — a script may veto placement after the engine's
        // built-in checks pass (e.g. custom land claims / faction rules).
        if (OnHouseCheck != null && OnHouseCheck(owner, position))
        { failure = PlacementFailure.ScriptVeto; return null; }

        // Create multi item
        var multiItem = _world.CreateItem();
        multiItem.BaseId = multiId;
        multiItem.Name = def.Name;
        multiItem.ItemType = customFoundation ? ItemType.MultiCustom : ItemType.Multi;
        if (magic)
            multiItem.SetAttr(ObjAttributes.Magic);
        _world.PlaceItem(multiItem, position);

        // Create house instance
        var house = new House(multiItem) { Owner = owner.Uid };

        // Apply script [MULTIDEF] BaseStorage when defined, instead of the flat default
        // (small house 489, keep/castle larger, etc.).
        if (def.BaseStorage > 0)
            house.BaseStorage = def.BaseStorage;
        // BaseVendors was parsed off the [MULTIDEF] but never reached the placed
        // house, so the vendor budget was always zero and MAXVENDORS unreadable.
        if (def.BaseVendors > 0)
            house.BaseVendors = def.BaseVendors;

        if (customFoundation)
        {
            // Empty committed design at revision 1 — clients that query the
            // design (0xBF 0x1E) get a valid, empty 0xD8 stream.
            multiItem.Tags.Set(HouseDesign.RevisionTag, "1");
        }
        else
        {
            // Materialize ONLY the invisible placeholder components
            // (flags == 0): doors, the house sign — the working server items.
            // The client draws every visible (flags != 0) wall/roof part
            // itself from its multi.mul, because the multi item is sent with
            // the multi data type (Source-X CItemMulti: components are
            // dynamic items, the walls are never materialized server-side).
            foreach (var comp in def.Components)
            {
                if (comp.Visible) continue;

                var compItem = _world.CreateItem();
                compItem.BaseId = comp.TileId;
                var compPos = new Point3D(
                    (short)(position.X + comp.DeltaX),
                    (short)(position.Y + comp.DeltaY),
                    (sbyte)(position.Z + comp.DeltaZ),
                    position.Map
                );
                // Source-X links every component back to the multi (m_uidLink);
                // the door lock code IS the house link, so the house key opens it.
                compItem.Link = multiItem.Uid;
                compItem.SetAttr(ObjAttributes.Move_Never);
                compItem.SetTag("HOUSE_UID", multiItem.Uid.Value.ToString());
                if (compItem.ItemType == ItemType.SignGump)
                    multiItem.Link = compItem.Uid;
                _world.PlaceItem(compItem, compPos);
                house.AddComponent(compItem.Uid);
            }
        }

        _houses[multiItem.Uid] = house;
        OnAddMulti?.Invoke(owner, multiItem, HousePriv.Owner);
        CreateHouseRegion(house);
        owner.Memory_AddObjTypes(multiItem.Uid, MemoryType.Guard);

        // Source-X Multi_Setup GenerateKey: a house key into the owner's pack
        // and a spare copy into the bank. There was previously NO key at all —
        // the "you have the key" messages could never be true for houses.
        CreateHouseKey(owner, multiItem, toBank: false);
        CreateHouseKey(owner, multiItem, toBank: true);
        return house;
    }

    /// <summary>Create one house key linked to the multi (TAG.LINK is the code
    /// the lock-matching uses; Link mirrors it for scripts).</summary>
    private void CreateHouseKey(Character owner, Item multiItem, bool toBank)
    {
        var key = _world.CreateItem();
        key.BaseId = 0x100F; // gold key
        key.ItemType = ItemType.Key;
        key.Name = "a house key";
        key.SetTag("LINK", multiItem.Uid.Value.ToString());
        key.Link = multiItem.Uid;

        var dest = toBank ? owner.GetEquippedItem(Layer.BankBox) : owner.Backpack;
        dest ??= owner.Backpack;
        if (dest == null || !dest.TryAddItem(key))
            _world.PlaceItemWithDecay(key, owner.Position);
    }

    /// <summary>Transfer through the engine so authority, ownership caps,
    /// persistent guard memory and structure keys change atomically.</summary>
    public bool TransferHouse(House house, Character requestor, Character newOwner)
    {
        if (!_houses.TryGetValue(house.MultiItem.Uid, out var registered) || registered != house)
            return false;
        if (requestor.PrivLevel < PrivLevel.GM && house.Owner != requestor.Uid)
            return false;
        if (!newOwner.IsPlayer || newOwner.Uid == house.Owner)
            return false;

        int playerCount = _houses.Values.Count(h => h != house && h.Owner == newOwner.Uid);
        if (MaxHousesPerPlayer >= 0 && playerCount >= MaxHousesPerPlayer)
            return false;
        int accountCount = GetHouseCountForAccount(newOwner, house);
        if (MaxHousesPerAccount >= 0 && accountCount >= MaxHousesPerAccount)
            return false;

        var oldOwner = _world.FindChar(house.Owner);
        RemoveStructureKeys(oldOwner, house.MultiItem.Uid);
        var oldMemory = oldOwner?.Memory_FindObjTypes(house.MultiItem.Uid, MemoryType.Guard);
        if (oldOwner != null && oldMemory != null)
            oldOwner.Memory_ClearTypes(oldMemory, MemoryType.Guard);

        house.TransferOwnership(newOwner.Uid);
        OnAddMulti?.Invoke(newOwner, house.MultiItem, HousePriv.Owner);
        newOwner.Memory_AddObjTypes(house.MultiItem.Uid, MemoryType.Guard);
        CreateHouseKey(newOwner, house.MultiItem, toBank: false);
        CreateHouseKey(newOwner, house.MultiItem, toBank: true);
        return true;
    }

    private void RemoveStructureKeys(Character? owner, Serial structureUid)
    {
        if (owner == null) return;
        RemoveStructureKeys(owner.Backpack, structureUid);
        var bank = owner.GetEquippedItem(Layer.BankBox);
        if (bank != owner.Backpack)
            RemoveStructureKeys(bank, structureUid);
    }

    private void RemoveStructureKeys(Item? container, Serial structureUid)
    {
        if (container == null) return;
        foreach (var child in container.Contents.ToList())
        {
            RemoveStructureKeys(child, structureUid);
            if (child.ItemType is not (ItemType.Key or ItemType.Keyring) || child.Link != structureUid)
                continue;
            container.RemoveItem(child);
            _world.RemoveItem(child);
        }
    }

    /// <summary>Host hook: is another SHIP hull at this point? Wired to the
    /// ship engine so house placement can't overlap a docked hull.</summary>
    public Func<Point3D, bool>? IsShipAt { get; set; }

    /// <summary>Check if a house can be placed at the given position
    /// (Source-X CItemMulti::Multi_Create intensive loop).</summary>
    public bool CanPlaceHouse(Point3D position, MultiDef def)
    {
        var md = _world.MapData;

        // Source-X re-checks REGION_FLAG_NOBUILDING over the footprint plus a
        // +5-tile margin (CItemMulti.cpp:3404-3429) — not just the anchor.
        for (short mx = (short)(def.MinX - 5); mx <= def.MaxX + 5; mx++)
        {
            for (short my = (short)(def.MinY - 5); my <= def.MaxY + 5; my++)
            {
                var marginPos = new Point3D(
                    (short)(position.X + mx), (short)(position.Y + my),
                    position.Z, position.Map);
                var region = _world.FindRegion(marginPos);
                if (region != null && region.IsFlag(RegionFlag.NoBuild))
                    return false;
            }
        }

        for (short dx = def.MinX; dx <= def.MaxX; dx++)
        {
            for (short dy = def.MinY; dy <= def.MaxY; dy++)
            {
                var checkPos = new Point3D(
                    (short)(position.X + dx),
                    (short)(position.Y + dy),
                    position.Z,
                    position.Map
                );

                if (FindHouseAt(checkPos) != null)
                    return false;
                // A docked ship hull blocks the footprint too.
                if (IsShipAt != null && IsShipAt(checkPos))
                    return false;

                // Reject blocked terrain (water, mountains, impassable statics)
                // in the footprint — a house can't sit on un-walkable ground.
                if (md != null && !md.IsPassable(checkPos.Map, checkPos.X, checkPos.Y, checkPos.Z))
                    return false;

                // Source-X CItemMulti::Multi_Create — every footprint cell's surface
                // must be flat relative to the placement Z (abs(cellZ - z) > 4 rejects).
                // A house cannot straddle a slope/cliff.
                if (md != null)
                {
                    md.GetAverageZ(checkPos.Map, checkPos.X, checkPos.Y, out _, out int cellZ, out _);
                    if (Math.Abs(cellZ - position.Z) > 4)
                        return false;
                }

                // Living chars block the cell — GM staff pass through
                // (Source-X CItemMulti.cpp:3330). NOTE: range 1 + exact X/Y
                // filter; the old GetCharsInRange(pos, 0) returned nothing,
                // so the char check was silently a no-op.
                foreach (var ch in _world.GetCharsInRange(checkPos, 1))
                {
                    if (ch.X != checkPos.X || ch.Y != checkPos.Y) continue;
                    if (ch.IsDead || ch.PrivLevel >= PrivLevel.GM) continue;
                    return false;
                }

                // Blocking loose items reject the cell (Source-X rejects
                // CAN_I_BLOCK statics in the intensive loop).
                foreach (var it in _world.GetItemsInRange(checkPos, 1))
                {
                    if (it.X != checkPos.X || it.Y != checkPos.Y) continue;
                    if (it.IsDeleted || it.ContainedIn.IsValid) continue;
                    var idef = Definitions.DefinitionLoader.GetItemDef(it.BaseId);
                    if (idef != null && idef.Can.HasFlag(CanFlags.I_Block))
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>Remove a house (redeed or demolish).</summary>
    public Item? RemoveHouse(Serial multiItemUid, Character requestor)
    {
        if (!_houses.TryGetValue(multiItemUid, out var house))
            return null;

        if (house.GetPriv(requestor.Uid) != HousePriv.Owner &&
            requestor.PrivLevel < PrivLevel.GM)
            return null;

        var position = house.MultiItem.Position;
        var owner = _world.FindChar(house.Owner);
        RemoveStructureKeys(owner, house.MultiItem.Uid);
        var ownerMemory = owner?.Memory_FindObjTypes(house.MultiItem.Uid, MemoryType.Guard);
        if (owner != null && ownerMemory != null)
            owner.Memory_ClearTypes(ownerMemory, MemoryType.Guard);
        RemoveHouseRegion(house);
        _houses.Remove(multiItemUid); // before Redeed: it deletes the multi item, and the ObjectDeleting handler must see no entry
        var deed = house.Redeed(_world);
        if (deed != null)
        {
            var recipient = owner ?? requestor;
            if (recipient.Backpack == null || !recipient.Backpack.TryAddItem(deed))
                _world.PlaceItemWithDecay(deed, position);
        }
        return deed;
    }

    /// <summary>Script/verb-driven redeed (server authority — no priv gate):
    /// the FULL teardown, so the registry entry and the dynamic house region
    /// never leak (the REDEED verb previously called house.Redeed directly).</summary>
    public Item? RedeedFromScript(Serial multiItemUid)
    {
        if (!_houses.TryGetValue(multiItemUid, out var house))
            return null;
        var owner = _world.FindChar(house.Owner);
        RemoveStructureKeys(owner, house.MultiItem.Uid);
        var ownerMemory = owner?.Memory_FindObjTypes(house.MultiItem.Uid, MemoryType.Guard);
        if (owner != null && ownerMemory != null)
            owner.Memory_ClearTypes(ownerMemory, MemoryType.Guard);
        RemoveHouseRegion(house);
        _houses.Remove(multiItemUid); // before Redeed: it deletes the multi item, and the ObjectDeleting handler must see no entry
        var deed = house.Redeed(_world);
        return deed;
    }

    /// <summary>Find the house that contains the given position.</summary>
    public House? FindHouseAt(Point3D pos)
    {
        foreach (var house in _houses.Values)
        {
            var mi = house.MultiItem;
            var def = _multiDefs.Get(mi.BaseId);
            if (def == null) continue;
            if (mi.MapIndex != pos.Map) continue;

            if (pos.X >= mi.X + def.MinX && pos.X <= mi.X + def.MaxX &&
                pos.Y >= mi.Y + def.MinY && pos.Y <= mi.Y + def.MaxY)
                return house;
        }
        return null;
    }

    /// <summary>Add a mutually-exclusive ban and move an occupant outside the
    /// footprint immediately; the movement gate prevents re-entry.</summary>
    public bool BanFromHouse(House house, Serial targetUid)
    {
        if (!_houses.TryGetValue(house.MultiItem.Uid, out var registered) || registered != house)
            return false;
        if (!house.AddBan(targetUid)) return false;
        var target = _world.FindChar(targetUid);
        if (target != null && FindHouseAt(target.Position) == house)
            EjectFromHouse(house, target);
        return true;
    }

    private void EjectFromHouse(House house, Character target)
    {
        var mi = house.MultiItem;
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return;
        var candidates = new[]
        {
            new Point3D(mi.X, (short)(mi.Y + def.MaxY + 1), mi.Z, mi.MapIndex),
            new Point3D((short)(mi.X + def.MaxX + 1), mi.Y, mi.Z, mi.MapIndex),
            new Point3D(mi.X, (short)(mi.Y + def.MinY - 1), mi.Z, mi.MapIndex),
            new Point3D((short)(mi.X + def.MinX - 1), mi.Y, mi.Z, mi.MapIndex),
        };
        var destination = candidates.FirstOrDefault(p =>
            _world.MapData == null || _world.MapData.IsPassable(p.Map, p.X, p.Y, p.Z));
        if (destination == default)
            destination = candidates[0];
        _world.MoveCharacter(target, destination);
    }

    /// <summary>Source-X CItemMulti::MultiRealizeRegion — give the house a dynamic
    /// world region matching its footprint, flagged REGION_FLAG_HOUSE and inheriting
    /// the containing region's flags (guarded / pvp / safe carry through, with House
    /// added). The region is the smallest-area one over the footprint, so FindRegion
    /// resolves to it inside the house. Static (houses never move), so it is created
    /// once on placement/load and torn down on collapse/redeed. Idempotent.</summary>
    private void CreateHouseRegion(House house)
    {
        if (house.RegionUid != 0) return; // already realized
        var mi = house.MultiItem;
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return;

        short x1 = (short)(mi.X + def.MinX);
        short y1 = (short)(mi.Y + def.MinY);
        short x2 = (short)(mi.X + def.MaxX);
        short y2 = (short)(mi.Y + def.MaxY);
        var center = new Point3D((short)(mi.X), (short)(mi.Y), mi.Z, mi.MapIndex);

        // Parent = the region this footprint sits in BEFORE the house region exists.
        var parent = _world.FindRegion(center);

        var region = new Region
        {
            Name = string.IsNullOrEmpty(mi.Name) ? "house" : mi.Name,
            MapIndex = mi.MapIndex,
            Flags = RegionFlag.House | RegionFlag.InheritParentFlags,
            P = center,
        };
        region.AddRect(x1, y1, x2, y2);
        if (parent != null)
            region.InheritFromParent(parent);
        // Apply the multi's REGION.EVENTS tag so the house region's @Enter/@Step
        // scripts fire (the tag round-trips on the item but was never realized).
        if (mi.TryGetTag("REGION.EVENTS", out string? regionEvents))
            region.AddEventsFromTag(regionEvents);

        _world.AddRegion(region);
        house.RegionUid = region.Uid;
    }

    /// <summary>Tear down the dynamic region created by <see cref="CreateHouseRegion"/>.</summary>
    private void RemoveHouseRegion(House house)
    {
        if (house.RegionUid == 0) return;
        _world.RemoveRegion(house.RegionUid);
        house.RegionUid = 0;
    }

    public bool CanPickupHouseItem(Character actor, Item item)
    {
        if (actor.PrivLevel >= PrivLevel.GM)
            return true;

        var check = item;
        int depth = 0;
        while (check != null && depth < 16)
        {
            foreach (var house in _houses.Values)
            {
                if (house.IsLockedDown(check.Uid) || house.IsSecured(check.Uid))
                    return house.CanLockdown(actor.Uid);
            }
            if (!check.ContainedIn.IsValid) break;
            check = _world.FindItem(check.ContainedIn);
            depth++;
        }

        return true;
    }

    /// <summary>Find all houses owned by a character.</summary>
    public List<House> GetHousesByOwner(Serial ownerUid)
    {
        var result = new List<House>();
        foreach (var house in _houses.Values)
        {
            if (house.Owner == ownerUid)
                result.Add(house);
        }
        return result;
    }

    /// <summary>Count houses owned by any character on the owner's account.</summary>
    public int GetHouseCountForAccount(Character owner) => GetHouseCountForAccount(owner, null);

    private int GetHouseCountForAccount(Character owner, House? exclude)
    {
        var account = Character.ResolveAccountForChar?.Invoke(owner.Uid);
        if (account == null)
            return _houses.Values.Count(h => h != exclude && h.Owner == owner.Uid);

        var accountChars = new HashSet<Serial>();
        for (int i = 0; i < 7; i++)
        {
            var uid = account.GetCharSlot(i);
            if (uid.IsValid)
                accountChars.Add(uid);
        }

        int count = 0;
        foreach (var house in _houses.Values)
        {
            if (house != exclude && accountChars.Contains(house.Owner))
                count++;
        }
        return count;
    }

    /// <summary>Get all registered houses.</summary>
    public IEnumerable<House> AllHouses => _houses.Values;

    /// <summary>Total house count.</summary>
    public int HouseCount => _houses.Count;

    public MultiRegistry MultiDefs => _multiDefs;

    // --- Decay System ---

    /// <summary>Decay stage interval in ms (default: 24 hours real time).</summary>
    public long DecayStageIntervalMs { get; set; } = 24L * 60 * 60 * 1000;

    /// <summary>
    /// Tick house decay. Called periodically from game loop.
    /// Returns list of houses that collapsed (IDOC → destroyed).
    /// </summary>
    public List<House> OnTickDecay()
    {
        if (DecayStageIntervalMs <= 0)
            return [];
        long now = Environment.TickCount64;
        var collapsed = new List<House>();

        foreach (var house in _houses.Values)
        {
            long elapsed = now - house.LastRefreshTick;
            int stages = (int)(elapsed / DecayStageIntervalMs);

            var newStage = stages switch
            {
                0 => HouseDecayStage.LikeNew,
                1 => HouseDecayStage.SlightlyWorn,
                2 => HouseDecayStage.SomewhatWorn,
                3 => HouseDecayStage.FairlyWorn,
                4 => HouseDecayStage.GreatlyWorn,
                _ => HouseDecayStage.InDangerOfCollapsing
            };

            house.DecayStage = newStage;

            // IDOC exceeded — collapse after one more interval
            if (stages >= 6)
                collapsed.Add(house);
        }

        // Remove collapsed houses
        foreach (var house in collapsed)
        {
            var owner = _world.FindChar(house.Owner);
            RemoveStructureKeys(owner, house.MultiItem.Uid);
            var ownerMemory = owner?.Memory_FindObjTypes(house.MultiItem.Uid, MemoryType.Guard);
            if (owner != null && ownerMemory != null)
                owner.Memory_ClearTypes(ownerMemory, MemoryType.Guard);
            RemoveHouseRegion(house);
            _houses.Remove(house.MultiItem.Uid); // before Redeed: it deletes the multi item, and the ObjectDeleting handler must see no entry
            var deed = house.Redeed(_world);
            if (deed != null && house.Owner.IsValid)
            {
                if (owner?.Backpack == null || !owner.Backpack.TryAddItem(deed))
                    _world.PlaceItem(deed, house.MultiItem.Position);
            }
        }

        return collapsed;
    }

    /// <summary>Refresh a house (owner enters). Resets decay timer.</summary>
    public void RefreshHouse(House house)
    {
        house.Refresh();
    }

    /// <summary>
    /// Called when a character enters a house area.
    /// Auto-refreshes if the character is the owner.
    /// </summary>
    public void OnCharacterEnterHouse(Character ch, House house)
    {
        if (ch.Uid == house.Owner)
            house.Refresh();
    }

    // --- Save/Load via item TAGs ---

    /// <summary>
    /// Serialize house metadata to the multi item's TAGs for persistence.
    /// Called before world save.
    /// </summary>
    public void SerializeAllToTags()
    {
        foreach (var (uid, house) in _houses)
        {
            var item = house.MultiItem;
            item.SetTag("HOUSE.OWNER", $"0{house.Owner.Value:X}");
            var ownerObj = _world.FindObject(house.Owner);
            if (ownerObj != null)
                item.SetTag("HOUSE.OWNER_UUID", ownerObj.Uuid.ToString("D"));
            else
                item.RemoveTag("HOUSE.OWNER_UUID");
            item.SetTag("HOUSE.TYPE", ((byte)house.Type).ToString());
            item.SetTag("HOUSE.STORAGE", house.BaseStorage.ToString());
            // Written only when set, so an untouched house adds no new tags and
            // an older save keeps loading exactly as before.
            if (house.BaseVendors > 0)
                item.SetTag("HOUSE.BASEVENDORS", house.BaseVendors.ToString());
            else
                item.RemoveTag("HOUSE.BASEVENDORS");
            if (house.IncreasedStorage > 0)
                item.SetTag("HOUSE.INCREASEDSTORAGE", house.IncreasedStorage.ToString());
            else
                item.RemoveTag("HOUSE.INCREASEDSTORAGE");
            if (house.LockdownsPercent != 50)
                item.SetTag("HOUSE.LOCKDOWNSPERCENT", house.LockdownsPercent.ToString());
            else
                item.RemoveTag("HOUSE.LOCKDOWNSPERCENT");
            if (house.GuildStone.IsValid)
                item.SetTag("HOUSE.GUILD", $"0{house.GuildStone.Value:X}");
            else
                item.RemoveTag("HOUSE.GUILD");
            item.SetTag("HOUSE.DECAY_STAGE", ((byte)house.DecayStage).ToString());
            long elapsed = Environment.TickCount64 - house.LastRefreshTick;
            item.SetTag("HOUSE.DECAY_ELAPSED", Math.Max(0, elapsed).ToString());

            if (house.CoOwners.Count > 0)
                item.SetTag("HOUSE.COOWNERS", string.Join(",", house.CoOwners.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.COOWNERS");
            if (house.Friends.Count > 0)
                item.SetTag("HOUSE.FRIENDS", string.Join(",", house.Friends.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.FRIENDS");
            if (house.Bans.Count > 0)
                item.SetTag("HOUSE.BANS", string.Join(",", house.Bans.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.BANS");
            if (house.AccessList.Count > 0)
                item.SetTag("HOUSE.ACCESS", string.Join(",", house.AccessList.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.ACCESS");
            if (house.Vendors.Count > 0)
                item.SetTag("HOUSE.VENDORS", string.Join(",", house.Vendors.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.VENDORS");
            if (house.Lockdowns.Count > 0)
                item.SetTag("HOUSE.LOCKDOWNS", string.Join(",", house.Lockdowns.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.LOCKDOWNS");
            if (house.SecureContainers.Count > 0)
                item.SetTag("HOUSE.SECURE", string.Join(",", house.SecureContainers.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.SECURE");
            if (house.Components.Count > 0)
                item.SetTag("HOUSE.COMPONENTS", string.Join(",", house.Components.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("HOUSE.COMPONENTS");
        }
    }

    /// <summary>
    /// Rebuild house instances from multi items after world load.
    /// Scans all items of type Multi and reads their HOUSE.* TAGs.
    /// </summary>
    public void DeserializeFromWorld()
    {
        foreach (var existing in _houses.Values.ToList())
            RemoveHouseRegion(existing);
        _houses.Clear();
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not Item item) continue;
            // Both classic (Multi) and customizable-foundation (MultiCustom)
            // houses must be rebuilt — PlaceHouse stamps custom foundations as
            // MultiCustom, so reading only Multi dropped every custom house from
            // the registry after a restart (it then decayed/escaped management).
            if (item.ItemType is not (ItemType.Multi or ItemType.MultiCustom)) continue;
            var house = CreateHouseFromTags(item);
            if (house == null) continue;

            _houses[item.Uid] = house;
            CreateHouseRegion(house);
        }
    }

    private House? CreateHouseFromTags(Item item)
    {
        if (!item.TryGetTag("HOUSE.OWNER", out string? ownerStr)) return null;
        uint ownerVal = ParseHexSerial(ownerStr);
        if (ownerVal == 0) return null;

        var house = new House(item) { Owner = new Serial(ownerVal) };
        if (item.TryGetTag("HOUSE.TYPE", out string? typeStr) && byte.TryParse(typeStr, out byte ht) &&
            Enum.IsDefined(typeof(HouseType), ht))
            house.Type = (HouseType)ht;
        if (item.TryGetTag("HOUSE.STORAGE", out string? storStr) && int.TryParse(storStr, out int stor) && stor > 0)
            house.BaseStorage = stor;
        if (item.TryGetTag("HOUSE.BASEVENDORS", out string? bvStr) && int.TryParse(bvStr, out int bv))
            house.BaseVendors = bv;
        if (item.TryGetTag("HOUSE.INCREASEDSTORAGE", out string? incStr) && int.TryParse(incStr, out int inc))
            house.IncreasedStorage = inc;
        if (item.TryGetTag("HOUSE.LOCKDOWNSPERCENT", out string? lpStr) && int.TryParse(lpStr, out int lp))
            house.LockdownsPercent = lp;
        if (item.TryGetTag("HOUSE.GUILD", out string? guildStr))
            house.GuildStone = new Serial(ParseHexSerial(guildStr));
        if (item.TryGetTag("HOUSE.DECAY_STAGE", out string? dsStr) && byte.TryParse(dsStr, out byte ds) &&
            ds <= (byte)HouseDecayStage.InDangerOfCollapsing)
            house.DecayStage = (HouseDecayStage)ds;
        if (item.TryGetTag("HOUSE.DECAY_ELAPSED", out string? elStr) && long.TryParse(elStr, out long el) && el > 0)
            house.LastRefreshTick = Environment.TickCount64 - el;

        ParseSerialList(item, "HOUSE.COOWNERS", uid => house.AddCoOwner(uid));
        ParseSerialList(item, "HOUSE.FRIENDS", uid => house.AddFriend(uid));
        ParseSerialList(item, "HOUSE.ACCESS", uid => house.AddAccess(uid));
        ParseSerialList(item, "HOUSE.VENDORS", uid => house.AddVendor(uid));
        ParseSerialList(item, "HOUSE.BANS", uid => house.AddBan(uid));
        ParseSerialList(item, "HOUSE.LOCKDOWNS", uid =>
        {
            var locked = _world.FindItem(uid);
            if (locked == null || locked.IsDeleted) return;
            house.LockdownForLoad(uid);
            locked.SetAttr(ObjAttributes.LockedDown);
            locked.Link = item.Uid;
        });
        ParseSerialList(item, "HOUSE.SECURE", uid =>
        {
            var secure = _world.FindItem(uid);
            if (secure == null || secure.IsDeleted) return;
            house.SecureForLoad(uid);
            secure.SetAttr(ObjAttributes.Secure);
            secure.Link = item.Uid;
        });
        ParseSerialList(item, "HOUSE.COMPONENTS", uid =>
        {
            var component = _world.FindItem(uid);
            if (component == null) return;
            component.Link = item.Uid;
            component.SetAttr(ObjAttributes.Move_Never);
            component.SetTag("HOUSE_UID", item.Uid.Value.ToString());
            if (component.ItemType == ItemType.SignGump && !item.Link.IsValid)
                item.Link = component.Uid;
            house.AddComponent(uid);
        });
        return house;
    }

    private static uint ParseHexSerial(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return 0;
        str = str.Trim();
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || str.StartsWith('0'))
        {
            str = str.TrimStart('0').TrimStart('x', 'X');
            if (uint.TryParse(str, System.Globalization.NumberStyles.HexNumber, null, out uint val))
                return val;
        }
        if (uint.TryParse(str, out uint dec)) return dec;
        return 0;
    }

    private static void ParseSerialList(Item item, string tagName, Action<Serial> action)
    {
        if (!item.TryGetTag(tagName, out string? listStr) || string.IsNullOrWhiteSpace(listStr))
            return;
        foreach (var part in listStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            uint val = ParseHexSerial(part);
            if (val != 0) action(new Serial(val));
        }
    }
}
