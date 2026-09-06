using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;

namespace SphereNet.Game.Ships;

/// <summary>
/// Ship engine: placement, movement, rotation, save/load.
/// Maps to CCMultiMovable in Source-X CCMultiMovable.cpp and CItemShip in CItemShip.cpp.
/// </summary>
public sealed class ShipEngine
{
    private readonly GameWorld _world;
    private readonly MultiRegistry _multiDefs;
    private readonly MapDataManager? _mapData;
    private readonly Dictionary<Serial, Ship> _ships = [];
    private readonly Random _rand = new();

    public ShipEngine(GameWorld world, MultiRegistry multiDefs, MapDataManager? mapData)
    {
        _world = world;
        _multiDefs = multiDefs;
        _mapData = mapData;
        world.ObjectDeleting += OnWorldObjectDeleting;
    }

    /// <summary>Multi item deleted OUTSIDE the redeed path (.nuke, .remove,
    /// script REMOVE). Source-X ~CItemMulti tears the whole structure down:
    /// keys removed, components deleted, region unrealized, erased from
    /// g_World.m_Multis — otherwise the registry keeps a ghost ship that
    /// still counts against MaxShipsPerPlayer/Account. Engine-driven paths
    /// unregister before deleting the multi, so this only fires for external
    /// deletions.</summary>
    private void OnWorldObjectDeleting(SphereNet.Game.Objects.ObjBase obj)
    {
        if (obj is not Item it) return;
        if (!_ships.TryGetValue(it.Uid, out var ship) || !ReferenceEquals(ship.MultiItem, it))
            return;
        _ships.Remove(it.Uid);
        var owner = ship.Owner.IsValid ? _world.FindChar(ship.Owner) : null;
        RemoveShipKeys(owner, it.Uid);
        foreach (var compUid in ship.Components)
        {
            var comp = _world.FindItem(compUid);
            if (comp != null && !comp.IsDeleted)
                _world.RemoveItem(comp);
        }
        RemoveShipRegion(ship);
    }

    public Ship? GetShip(Serial multiItemUid) =>
        _ships.GetValueOrDefault(multiItemUid);

    public int ShipCount => _ships.Count;
    public IEnumerable<Ship> AllShips => _ships.Values;
    public int MaxShipsPerPlayer { get; set; } = 1;
    public int MaxShipsPerAccount { get; set; } = 1;

    /// <summary>Source-X CItemShip::Speak hook. Args: (Ship ship, string text).
    /// Program.cs delivers the tillerman line to nearby clients as a unicode
    /// speech packet so confirmations like "Aye, captain!" or "I cannot turn
    /// in such turbulent water" appear above the ship.</summary>
    public Action<Ship, string>? OnTillerSpeak { get; set; }

    /// <summary>Called after a ship moves so Program.cs can broadcast 0xF6.</summary>
    public Action<Ship>? OnShipMoved { get; set; }

    /// <summary>Called when a ship stops (Source-X @ShipStop fire site).</summary>
    public Action<Ship>? OnShipStopped { get; set; }

    /// <summary>Called when a ship turns to a new facing (Source-X @ShipTurn).</summary>
    /// <summary>A turn happened: the item that turned, the new facing and the old
    /// one. Source-X runs @Ship_Turn for EVERY item it moved - the hull, its components
    /// and the loose deck cargo - with ARGN1 the new direction and ARGN2 the previous
    /// one (Face, CCMultiMovable.cpp:628). Characters do not get an item trigger.</summary>
    public Action<Item, int, int>? OnShipTurned { get; set; }

    /// <summary>A ship is being turned back into its deed: the ship multi, the deed and
    /// the deed's graphic. Source-X runs @Redeed on the OLD multi with the new deed as
    /// the object argument and the deed id in ARGN1 (CItemMulti::Redeed,
    /// CItemMulti.cpp:1225).</summary>
    public Action<Item, Item, int>? OnShipRedeed { get; set; }

    /// <summary>Sailing sound (Source-X CCMultiMovable::SetMoveDir plays 0x12 or
    /// 0x13 on roughly one move command in ten). Args: ship, sound id.</summary>
    public Action<Ship, ushort>? OnShipSound { get; set; }

    /// <summary>Fired before a ship move crosses a region boundary (Source-X
    /// CCMultiMovable region check — item @RegionLeave/@RegionEnter live on
    /// movable multis, not on ordinary items). Args: ship, region being left
    /// (null = wilderness), region being entered (null = wilderness). Return
    /// false to block the move (script RETURN 1). Installed only when one of
    /// the two triggers is hooked, so unhooked shards pay a null check.</summary>
    public Func<Ship, World.Regions.Region?, World.Regions.Region?, bool>? OnShipRegionChange { get; set; }
    public Action<Character, Item, HousePriv>? OnAddMulti { get; set; }

    private void TillerSpeak(Ship ship, string key) =>
        OnTillerSpeak?.Invoke(ship, SphereNet.Game.Messages.ServerMessages.Get(key));

    // =====================================================================
    // Placement
    // =====================================================================

    /// <summary>
    /// Place a new ship at the given position.
    /// Returns null if placement is invalid.
    /// </summary>
    public Ship? PlaceShip(Character owner, ushort multiId, Point3D pos, Direction facing, bool magic = false)
        => PlaceShip(owner, multiId, pos, facing, out _, magic);

    /// <summary>Place a ship, reporting the specific failure reason (B4).</summary>
    public Ship? PlaceShip(Character owner, ushort multiId, Point3D pos, Direction facing,
        out SphereNet.Game.Housing.PlacementFailure failure, bool magic = false)
    {
        failure = SphereNet.Game.Housing.PlacementFailure.None;

        if (MaxShipsPerPlayer >= 0 && _ships.Values.Count(s => s.Owner == owner.Uid) >= MaxShipsPerPlayer)
        { failure = SphereNet.Game.Housing.PlacementFailure.PlayerLimitReached; return null; }
        if (MaxShipsPerAccount >= 0 && GetShipCountForAccount(owner) >= MaxShipsPerAccount)
        { failure = SphereNet.Game.Housing.PlacementFailure.AccountLimitReached; return null; }
        var normalizedFacing = Normalize4Dir(facing);
        ushort orientedId = (ushort)((multiId & ~3) | DirTo4Index(normalizedFacing));
        var def = _multiDefs.Get(orientedId);
        if (def == null)
        {
            orientedId = multiId;
            def = _multiDefs.Get(multiId);
        }
        if (def == null) { failure = SphereNet.Game.Housing.PlacementFailure.MultiDefinitionMissing; return null; }
        var (mapWidth, mapHeight) = _mapData?.GetMapSize(pos.Map) ?? (7168, 4096);
        if (pos.X + def.MinX < 0 || pos.Y + def.MinY < 0 ||
            pos.X + def.MaxX >= mapWidth || pos.Y + def.MaxY >= mapHeight)
        { failure = SphereNet.Game.Housing.PlacementFailure.OutOfMap; return null; }

        if (!magic && !CanPlaceShip(pos, def))
        { failure = SphereNet.Game.Housing.PlacementFailure.LocationBlocked; return null; }

        // Create multi item
        var multiItem = _world.CreateItem();
        multiItem.BaseId = orientedId;
        multiItem.Name = def.Name;
        multiItem.ItemType = ItemType.Ship;
        if (magic)
            multiItem.SetAttr(ObjAttributes.Magic);
        _world.PlaceItem(multiItem, pos);

        var ship = new Ship(multiItem)
        {
            Owner = owner.Uid,
            DirFace = normalizedFacing,
            DirMove = normalizedFacing,
            // A freshly placed ship is ready to sail — no "raise anchor" needed
            // before the first movement command. "Drop anchor" still parks it.
            Anchored = false,
        };
        ApplyDefSpeed(ship, def);

        // Materialize ONLY the invisible placeholder components (flags == 0):
        // tiller, planks, hatch, mast anchor — the working server items.
        // The client draws every visible (flags != 0) hull/deck part itself
        // from its multi.mul, because the multi item is sent with the multi
        // data type (Source-X CItemShip: components are dynamic items, the
        // hull is never materialized server-side).
        bool needsKey = false;
        foreach (var comp in def.Components)
        {
            if (comp.Visible) continue;

            var compItem = _world.CreateItem();
            compItem.BaseId = comp.TileId;

            // Determine item type from tile ID
            compItem.ItemType = ClassifyShipComponent(comp.TileId);
            compItem.SetAttr(ObjAttributes.Move_Never);
            compItem.Link = multiItem.Uid;
            needsKey |= compItem.ItemType is ItemType.ShipTiller or ItemType.ShipSideLocked or ItemType.ShipHoldLock;

            var compPos = new Point3D(
                (short)(pos.X + comp.DeltaX),
                (short)(pos.Y + comp.DeltaY),
                (sbyte)(pos.Z + comp.DeltaZ),
                pos.Map
            );
            _world.PlaceItem(compItem, compPos);
            ship.AddComponent(compItem);
        }

        _ships[multiItem.Uid] = ship;
        OnAddMulti?.Invoke(owner, multiItem, HousePriv.Owner);
        CreateShipRegion(ship);
        if (needsKey)
        {
            CreateShipKey(owner, multiItem, toBank: false);
            CreateShipKey(owner, multiItem, toBank: true);
        }
        return ship;
    }

    public int GetShipCountForAccount(Character owner)
    {
        var account = Character.ResolveAccountForChar?.Invoke(owner.Uid);
        if (account == null)
            return _ships.Values.Count(ship => ship.Owner == owner.Uid);
        var accountCharacters = new HashSet<Serial>();
        for (int slot = 0; slot < 7; slot++)
        {
            var uid = account.GetCharSlot(slot);
            if (uid.IsValid) accountCharacters.Add(uid);
        }
        return _ships.Values.Count(ship => accountCharacters.Contains(ship.Owner));
    }

    private void CreateShipKey(Character owner, Item multiItem, bool toBank)
    {
        var key = _world.CreateItem();
        key.BaseId = 0x100F;
        key.ItemType = ItemType.Key;
        key.Name = "a ship key";
        key.SetTag("LINK", multiItem.Uid.Value.ToString());
        key.Link = multiItem.Uid;
        var destination = toBank ? owner.GetEquippedItem(Layer.BankBox) : owner.Backpack;
        destination ??= owner.Backpack;
        if (destination == null || !destination.TryAddItem(key))
            _world.PlaceItemWithDecay(key, owner.Position);
    }

    private void RemoveShipKeys(Character? owner, Serial shipUid)
    {
        if (owner == null) return;
        RemoveShipKeys(owner.Backpack, shipUid);
        var bank = owner.GetEquippedItem(Layer.BankBox);
        if (bank != owner.Backpack)
            RemoveShipKeys(bank, shipUid);
    }

    private void RemoveShipKeys(Item? container, Serial shipUid)
    {
        if (container == null) return;
        foreach (var child in container.Contents.ToList())
        {
            RemoveShipKeys(child, shipUid);
            if (child.ItemType is not (ItemType.Key or ItemType.Keyring) || child.Link != shipUid)
                continue;
            container.RemoveItem(child);
            _world.RemoveItem(child);
        }
    }

    /// <summary>Check if a ship can be placed at position (all footprint must be water).</summary>
    public bool CanPlaceShip(Point3D pos, MultiDef def)
        => CanPlaceShip(pos, def, null);

    private bool CanPlaceShip(Point3D pos, MultiDef def, Ship? ignoreShip)
    {
        var (mapWidth, mapHeight) = _mapData?.GetMapSize(pos.Map) ?? (7168, 4096);
        for (short dx = def.MinX; dx <= def.MaxX; dx++)
        {
            for (short dy = def.MinY; dy <= def.MaxY; dy++)
            {
                short cx = (short)(pos.X + dx), cy = (short)(pos.Y + dy);
                if (cx < 0 || cy < 0 || cx >= mapWidth || cy >= mapHeight)
                    return false;
                if (!CanSailInto(ignoreShip, pos.Map, cx, cy, pos.Z))
                    return false;
                var region = _world.FindRegion(new Point3D(cx, cy, pos.Z, pos.Map));
                if (region != null && region.Uid != ignoreShip?.RegionUid && region.IsFlag(RegionFlag.House))
                    return false;
                foreach (var ch in _world.GetCharsInRange(new Point3D(cx, cy, pos.Z, pos.Map), 1))
                    if (ch.X == cx && ch.Y == cy && ch.PrivLevel < PrivLevel.GM &&
                        (ignoreShip == null || !IsOnDeck(ignoreShip,
                            _multiDefs.Get(ignoreShip.MultiItem.BaseId) ?? def, ch)))
                        return false;
                foreach (var item in _world.GetItemsInRange(new Point3D(cx, cy, pos.Z, pos.Map), 1))
                {
                    if (item.IsDeleted || item.ContainedIn.IsValid || item == ignoreShip?.MultiItem) continue;
                    if (ignoreShip != null && ignoreShip.Components.Contains(item.Uid)) continue;
                    if (ignoreShip != null && IsOnDeck(ignoreShip,
                        _multiDefs.Get(ignoreShip.MultiItem.BaseId) ?? def, item)) continue;
                    var itemDef = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
                    if (itemDef != null && itemDef.Can.HasFlag(CanFlags.I_Block))
                        return false;
                }
            }
        }
        return true;
    }

    /// <summary>Remove a ship and convert back to deed.</summary>
    public Item? RemoveShip(Serial multiItemUid, Character requestor)
    {
        if (!_ships.TryGetValue(multiItemUid, out var ship))
            return null;

        if (ship.Owner != requestor.Uid && requestor.PrivLevel < PrivLevel.GM)
            return null;

        var position = ship.MultiItem.Position;
        var owner = _world.FindChar(ship.Owner);
        var deed = RemoveShipCore(ship, multiItemUid);
        if (deed != null)
        {
            var recipient = owner ?? requestor;
            if (recipient.Backpack == null || !recipient.Backpack.TryAddItem(deed))
                _world.PlaceItemWithDecay(deed, position);
        }
        return deed;
    }

    /// <summary>Script/verb-driven dry-dock (server authority — no priv gate):
    /// the REDEED verb on a ship multi. The deed is delivered to the owner's
    /// backpack, else dropped (with decay) at the ship's spot.</summary>
    public Item? RedeedFromScript(Serial multiItemUid)
    {
        if (!_ships.TryGetValue(multiItemUid, out var ship))
            return null;
        var pos = ship.MultiItem.Position;
        var owner = ship.Owner.IsValid ? _world.FindChar(ship.Owner) : null;
        var deed = RemoveShipCore(ship, multiItemUid);
        if (deed != null)
        {
            if (owner?.Backpack == null || !owner.Backpack.TryAddItem(deed))
                _world.PlaceItemWithDecay(deed, pos);
        }
        return deed;
    }

    private Item? RemoveShipCore(Ship ship, Serial multiItemUid)
    {
        // Create deed — preserve ship UUID for identity continuity
        var deed = _world.CreateItem();
        deed.BaseId = 0x14F1; // ITEMID_SHIP_PLANS1
        deed.ItemType = ItemType.Deed;
        deed.More1 = ship.MultiItem.BaseId;
        deed.Hue = ship.MultiItem.Hue;
        if (ship.MultiItem.IsAttr(ObjAttributes.Magic))
            deed.SetAttr(ObjAttributes.Magic);
        deed.Name = ship.MultiItem.Name + " deed";
        deed.SetTag("SHIP_MULTI_UUID", ship.MultiItem.Uuid.ToString("D"));
        deed.SetTag("SHIP_MULTI_BASEID", ship.MultiItem.BaseId.ToString());

        // @Redeed belongs to the OLD multi, with the new deed as its object argument
        // and the deed's graphic in ARGN1 (CItemMulti::Redeed, CItemMulti.cpp:1225).
        // The server only wired the house redeed, so a ship's script never heard about
        // it. As upstream, this is a notification: it does not veto the redeed.
        OnShipRedeed?.Invoke(ship.MultiItem, deed, deed.BaseId);

        // Source-X ship Redeed: TransferAllItemsToMovingCrate — the hold's
        // cargo moves to a crate (owner's bank, else dropped with decay at
        // the ship's spot) instead of being deleted with the components.
        List<Item> cargo = [];
        foreach (var compUid in ship.Components)
        {
            var comp = _world.FindItem(compUid);
            if (comp == null || comp.Contents.Count == 0) continue;
            foreach (var it in new List<Item>(comp.Contents))
            {
                comp.RemoveItem(it);
                cargo.Add(it);
            }
        }
        foreach (var loose in ListDeckItems(ship))
        {
            if (ship.Components.Contains(loose.Uid) || loose.IsDeleted ||
                loose.IsAttr(ObjAttributes.Static | ObjAttributes.Move_Never))
                continue;
            _world.HideFromSector(loose);
            cargo.Add(loose);
        }
        while (cargo.Count > 0)
        {
            var crate = _world.CreateItem();
            crate.BaseId = 0x0E3D; // ITEMID_CRATE1
            crate.ItemType = ItemType.Container;
            crate.Name = "a moving crate";
            while (cargo.Count > 0 && crate.Contents.Count < Item.MaxContainerItems)
            {
                var it = cargo[^1];
                cargo.RemoveAt(cargo.Count - 1);
                crate.TryAddItem(it);
                it.DecayTime = 0; // protected inside the crate
            }
            var ownerCh = ship.Owner.IsValid ? _world.FindChar(ship.Owner) : null;
            var bank = ownerCh?.GetEquippedItem(Layer.BankBox);
            if (bank == null || !bank.TryAddItem(crate))
                _world.PlaceItemWithDecay(crate, ship.MultiItem.Position);
        }

        var owner = ship.Owner.IsValid ? _world.FindChar(ship.Owner) : null;
        RemoveShipKeys(owner, ship.MultiItem.Uid);
        ship.Pilot = Serial.Invalid;

        // Remove all component items
        foreach (var compUid in ship.Components)
        {
            var item = _world.FindItem(compUid);
            if (item != null) _world.RemoveItem(item);
        }

        RemoveShipRegion(ship);
        _ships.Remove(multiItemUid); // before RemoveItem: the ObjectDeleting handler must see no entry
        _world.RemoveItem(ship.MultiItem);
        return deed;
    }

    // =====================================================================
    // Movement — Source: CCMultiMovable.cpp
    // =====================================================================

    /// <summary>
    /// Set movement direction and type.
    /// Source: CCMultiMovable.cpp:60-105
    /// </summary>
    /// <param name="wheelMove">True when the order came from the 0xBF.0x33
    /// wheel/tiller packet rather than a script or tillerman command. Source-X
    /// treats the two differently: a wheel click that arrives while the move
    /// timer is still running is dropped, and only a wheel order re-selects the
    /// speed mode.</param>
    public bool SetMoveDir(Ship ship, Direction dir, ShipMovementType moveType,
        bool wheelMove = false)
    {
        dir = (Direction)((byte)dir & 0x07);
        if (moveType == ShipMovementType.Stop)
        {
            ship.DirMove = dir;
            Stop(ship);
            return true;
        }
        if (ship.Anchored || moveType is not (ShipMovementType.OneTile or ShipMovementType.Normal))
            return false;

        // Source-X CCMultiMovable.cpp:78 — "otherwise for each click with mouse
        // it will do 1 move". A wheel order that lands while the ship is still
        // counting down to its next step is dropped outright; without this the
        // click would push NextMoveTick a full period further out, so holding
        // the wheel down stalls the ship instead of sailing it.
        if (wheelMove && ship.MovementType != ShipMovementType.Stop &&
            Environment.TickCount64 < ship.NextMoveTick)
            return false;

        // Source-X CCMultiMovable.cpp:81 — repeating the order the ship is
        // already following, while it faces that way and is still stepping one
        // tile at a time, promotes it to continuous sailing. (Upstream also
        // compares the move direction there, but it assigned it a few lines
        // earlier, so that half of the condition is always true.)
        if (ship.MovementType == ShipMovementType.OneTile && ship.DirFace == dir)
            moveType = ShipMovementType.Normal;

        // Ship MOVEMENT keeps all 8 directions (a ship sails diagonally for the
        // SHIPFORELEFT/BACKRIGHT/etc. commands); only the FACING is 4-direction.
        // Normalizing the move direction here collapsed every diagonal command to
        // a cardinal one.
        ship.DirMove = dir;
        ship.MovementType = moveType;

        // Source-X CCMultiMovable.cpp:89 — the creak of the rigging, skipped on
        // a magic (ATTR_MAGIC) vessel.
        if (!ship.MultiItem.IsAttr(ObjAttributes.Magic) && _rand.Next(10) == 0)
            OnShipSound?.Invoke(ship, _rand.Next(2) != 0 ? (ushort)0x12 : (ushort)0x13);

        // Source-X CCMultiMovable::SetMoveDir (CCMultiMovable.cpp:101): one-tile
        // steering (SMT_SLOW) keeps the slow speed mode, continuous ("normal")
        // sailing runs in the fast mode, which halves the tick interval below.
        // Only a wheel order re-selects it; a scripted command leaves the mode
        // the pilot last chose alone.
        if (wheelMove)
            ship.SpeedMode = moveType == ShipMovementType.Normal
                ? ShipSpeedMode.Fast : ShipSpeedMode.Slow;

        ship.NextMoveTick = Environment.TickCount64 + GetMoveDelay(ship);
        return true;
    }

    /// <summary>Source-X CCMultiMovable::SetNextMove (CCMultiMovable.cpp:119/124):
    /// the slow (one-tile) speed mode runs at the full period; every faster mode
    /// halves the tick interval so continuous sailing advances twice as fast.</summary>
    private static long GetMoveDelay(Ship ship)
        => ship.SpeedMode == ShipSpeedMode.Slow ? ship.SpeedPeriod : ship.SpeedPeriod / 2;

    /// <summary>Apply a ship's script-defined speed ([MULTIDEF] SHIPSPEED=period,tiles).
    /// Source-X stores period in TENTHS of a second and the per-step delay is
    /// period * MSECS_PER_TENTH (100); continuous (fast) sailing halves it in
    /// <see cref="GetMoveDelay"/>. When the def has no SHIPSPEED the ship keeps the
    /// engine default (500 ms / 1 tile), so unscripted ships are unchanged.</summary>
    private static void ApplyDefSpeed(Ship ship, SphereNet.Game.Housing.MultiDef? def)
    {
        if (def == null || def.ShipSpeedPeriodTenths <= 0) return;
        ship.SpeedPeriod = (ushort)Math.Clamp(def.ShipSpeedPeriodTenths * 100, 1, ushort.MaxValue);
        if (def.ShipSpeedTiles > 0)
            ship.SpeedTiles = (byte)Math.Clamp(def.ShipSpeedTiles, 1, 16);
    }

    /// <summary>
    /// Move ship in direction by given distance.
    /// Source: CCMultiMovable.cpp:654-871
    /// </summary>
    public bool Move(Ship ship, Direction dir, int distance = 1)
    {
        if (ship.Anchored || distance <= 0 || ((byte)dir & 0xF8) != 0) return false;

        // Keep the full 8-direction move (GetDirDelta yields diagonal deltas and
        // CanMoveShipTo/MoveDelta handle any dx/dy) so diagonal sailing works.
        GetDirDelta(dir, out short dx, out short dy);

        for (int i = 0; i < distance; i++)
        {
            short newX = (short)(ship.MultiItem.X + dx);
            short newY = (short)(ship.MultiItem.Y + dy);

            // Leading-edge water check in the move direction (Source-X)
            if (!CanMoveShipTo(ship, newX, newY, dx, dy))
                return false;

            MoveDelta(ship, dx, dy, 0);
        }
        return true;
    }

    /// <summary>
    /// Rotate ship to new facing direction. Changes multi ID by direction offset.
    /// Source: CCMultiMovable.cpp:498-652
    /// </summary>
    public bool Face(Ship ship, Direction newFacing)
    {
        newFacing = Normalize4Dir(newFacing);
        var oldFacing = ship.DirFace;
        if (newFacing == oldFacing) return true;

        // Calculate rotation
        int oldIdx = DirTo4Index(oldFacing);
        int newIdx = DirTo4Index(newFacing);
        int rotSteps = ((newIdx - oldIdx) + 4) % 4; // 1=right90, 2=180, 3=left90

        ushort baseId = (ushort)(ship.MultiItem.BaseId & ~3);
        ushort newBaseId = (ushort)(baseId | newIdx);
        var oldDef = _multiDefs.Get(ship.MultiItem.BaseId);
        var newDef = _multiDefs.Get(newBaseId);
        if (oldDef == null || newDef == null)
            return false;

        // Preflight the rotated footprint (Source-X CCMultiMovable turn fit-check):
        // if the new facing's bounding rect would not fit (any non-water tile),
        // abort the turn BEFORE mutating anything — otherwise the ship is left
        // half-rotated with no rollback. Magic ships turn anywhere.
        if (!ship.MultiItem.IsAttr(ObjAttributes.Magic))
        {
            if (!CanPlaceShip(ship.MultiItem.Position, newDef, ship))
                return false;
        }

        // Capture riders and loose deck items against the old footprint before
        // changing the multi id or moving any component.
        var deckChars = ListDeckCharacters(ship);
        var deckItems = ListDeckItems(ship);

        // Update multi item ID: baseId & ~3 | newIdx
        ship.MultiItem.BaseId = newBaseId;

        // Rotate component positions around the multi center
        short cx = ship.MultiItem.X;
        short cy = ship.MultiItem.Y;

        // Same subset PlaceShip materializes: the invisible placeholders
        // (tiller, planks, hatch) — index-aligned with ship.Components.
        var newComponents = newDef.Components.Where(c => !c.Visible).ToList();
        for (int componentIndex = 0; componentIndex < ship.Components.Count; componentIndex++)
        {
            var item = _world.FindItem(ship.Components[componentIndex]);
            if (item == null) continue;
            if (componentIndex < newComponents.Count)
            {
                bool plankWasOpen = item.ItemType == ItemType.ShipPlank;
                uint plankSideType = plankWasOpen ? item.More2 : 0;

                var component = newComponents[componentIndex];
                item.BaseId = component.TileId;

                // A plank standing OPEN stays open through the turn: Source-X re-picks
                // the new side's open graphic and keeps the type (Face,
                // CCMultiMovable.cpp:614). Writing the new heading's CLOSED art over it
                // left an open plank looking shut - and the remembered graphic, still
                // the old heading's, came back the next time it was closed.
                if (plankWasOpen)
                {
                    item.More1 = item.BaseId;   // the new side's closed art to return to
                    uint openArt = Definitions.DefinitionLoader.GetItemDef(item.BaseId)?.TData1 ?? 0;
                    if (openArt is > 0 and <= ushort.MaxValue)
                        item.BaseId = (ushort)openArt;
                    item.ItemType = ItemType.ShipPlank;
                    item.More2 = plankSideType;
                }

                _world.PlaceItem(item, new Point3D(
                    (short)(cx + component.DeltaX), (short)(cy + component.DeltaY),
                    (sbyte)(ship.MultiItem.Z + component.DeltaZ), ship.MultiItem.MapIndex));
            }
            else
            {
                short rx = (short)(item.X - cx);
                short ry = (short)(item.Y - cy);
                RotatePoint(ref rx, ref ry, rotSteps);
                _world.PlaceItem(item, new Point3D((short)(cx + rx), (short)(cy + ry), item.Z, item.MapIndex));
            }
        }

        // Rotate characters on deck
        foreach (var ch in deckChars)
        {
            short rx = (short)(ch.X - cx);
            short ry = (short)(ch.Y - cy);
            RotatePoint(ref rx, ref ry, rotSteps);

            var p = ch.Position;
            _world.MoveCharacter(ch, new Point3D((short)(cx + rx), (short)(cy + ry), p.Z, p.Map), fireRegionEvents: false);
            byte running = (byte)((byte)ch.Direction & (byte)Direction.Running);
            ch.Direction = (Direction)(running | (((byte)ch.Direction + rotSteps * 2) & 0x07));
        }

        // Rotate loose items on deck
        foreach (var item in deckItems)
        {
            if (ship.Components.Contains(item.Uid)) continue;

            short rx = (short)(item.X - cx);
            short ry = (short)(item.Y - cy);
            RotatePoint(ref rx, ref ry, rotSteps);

            var p = item.Position;
            _world.PlaceItem(item, new Point3D((short)(cx + rx), (short)(cy + ry), p.Z, p.Map));
        }

        // The rotated hull has a new footprint shape — re-rect the ship region.
        UpdateShipRegion(ship);

        ship.DirFace = newFacing;

        if (OnShipTurned != null)
        {
            int newDir = (int)newFacing;
            int oldDir = (int)oldFacing;
            OnShipTurned(ship.MultiItem, newDir, oldDir);
            foreach (var compUid in ship.Components)
            {
                if (_world.FindItem(compUid) is { IsDeleted: false } comp)
                    OnShipTurned(comp, newDir, oldDir);
            }
            foreach (var cargo in deckItems)
            {
                if (cargo.IsDeleted || ship.Components.Contains(cargo.Uid)) continue;
                OnShipTurned(cargo, newDir, oldDir);
            }
        }
        return true;
    }

    /// <summary>Stop ship movement.</summary>
    public void Stop(Ship ship)
    {
        bool wasMoving = ship.MovementType != ShipMovementType.Stop;
        ship.MovementType = ShipMovementType.Stop;
        ship.NextMoveTick = 0;
        if (wasMoving)
            OnShipStopped?.Invoke(ship);
    }

    /// <summary>
    /// Ship tick — called from game loop. Moves ship if speed period elapsed.
    /// Source: CCMultiMovable.cpp:881-905
    /// </summary>
    public void OnShipTick(Ship ship)
    {
        if (ship.MovementType == ShipMovementType.Stop) return;
        if (ship.Anchored) return;

        long now = Environment.TickCount64;
        if (now < ship.NextMoveTick) return;

        if (!Move(ship, ship.DirMove, ship.SpeedTiles))
        {
            Stop(ship);
            return;
        }

        if (ship.MovementType == ShipMovementType.OneTile)
        {
            Stop(ship);
            return;
        }

        ship.NextMoveTick = now + GetMoveDelay(ship);
    }

    /// <summary>Tick all active ships.</summary>
    public void OnTickAll()
    {
        foreach (var ship in _ships.Values.ToList())
            if (_ships.ContainsKey(ship.MultiItem.Uid))
                OnShipTick(ship);
    }

    // =====================================================================
    // Ship Commands (called from Item.TryExecuteCommand)
    // =====================================================================

    /// <summary>
    /// Execute a ship command. Uses Source-X DirMoveChange offsets:
    /// GetDirTurn(DirFace, offset) where dir is 8-directional (0-7).
    /// FORE=0, FORERIGHT=+1, DRIFTRIGHT=+2, BACKRIGHT=+3,
    /// BACK=+4, BACKLEFT=-3, DRIFTLEFT=-2, FORELEFT=-1
    /// Source: CCMultiMovable.cpp:976-1277
    /// </summary>
    public bool ExecuteCommand(Ship ship, string command, string args)
    {
        var dirFace = ship.DirFace;

        switch (command)
        {
            // --- Movement commands (DirMoveChange via dodirmovechange) ---
            case "SHIPFORE":
                return DirMoveChange(ship, dirFace, 0);
            case "SHIPFORELEFT":
                return DirMoveChange(ship, dirFace, -1);
            case "SHIPFORERIGHT":
                return DirMoveChange(ship, dirFace, 1);
            case "SHIPDRIFTLEFT":
                return DirMoveChange(ship, dirFace, -2);
            case "SHIPDRIFTRIGHT":
                return DirMoveChange(ship, dirFace, 2);
            case "SHIPBACK":
                return DirMoveChange(ship, dirFace, 4);
            case "SHIPBACKLEFT":
                return DirMoveChange(ship, dirFace, -3);
            case "SHIPBACKRIGHT":
                return DirMoveChange(ship, dirFace, 3);

            // --- Aliases for drift (SHIPLEFT/SHIPRIGHT = SHIPDRIFTLEFT/RIGHT) ---
            case "SHIPLEFT":
                return DirMoveChange(ship, dirFace, -2);
            case "SHIPRIGHT":
                return DirMoveChange(ship, dirFace, 2);

            // --- Turn commands (anchor check, Face call) ---
            case "SHIPTURNLEFT":
                return TurnShip(ship, dirFace, -2);
            case "SHIPTURNRIGHT":
                return TurnShip(ship, dirFace, 2);
            case "SHIPTURNAROUND":
            case "SHIPTURN":
                return TurnShip(ship, dirFace, 4);

            // --- Anchor ---
            case "SHIPANCHORDROP":
                if (ship.Anchored)
                {
                    TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsAllDown);
                    return true;
                }
                ship.Anchored = true;
                Stop(ship);
                TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsDown);
                return true;
            case "SHIPANCHORRAISE":
                if (!ship.Anchored)
                {
                    TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsAllUp);
                    return true;
                }
                ship.Anchored = false;
                TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerReply1);
                return true;

            // --- Stop ---
            case "SHIPSTOP":
                Stop(ship);
                TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerStopped);
                return true;

            // --- Direct move/face ---
            case "SHIPMOVE":
            {
                if (!TryParseDir(args, out var moveDir)) return false;
                ship.DirMove = moveDir;
                Move(ship, moveDir, ship.SpeedTiles);
                return true;
            }
            case "SHIPFACE":
            {
                if (!TryParseDir(args, out var faceDir)) return false;
                return Face(ship, faceDir);
            }

            // --- Gate (teleport) ---
            case "SHIPGATE":
                return HandleShipGate(ship, args);

            // --- Vertical movement (requires ATTR_MAGIC) ---
            case "SHIPUP":
                if (!ship.MultiItem.IsAttr(ObjAttributes.Magic)) return false;
                return MoveDelta(ship, 0, 0, PlayerHeight);
            case "SHIPDOWN":
                if (!ship.MultiItem.IsAttr(ObjAttributes.Magic)) return false;
                return MoveDelta(ship, 0, 0, -PlayerHeight);

            // --- Land (return to ground level, requires ATTR_MAGIC) ---
            case "SHIPLAND":
                if (!ship.MultiItem.IsAttr(ObjAttributes.Magic)) return false;
                return HandleShipLand(ship);

            // --- Pilot (Source-X SetPilot): the assigned pilot steers via the
            // 0xBF 0x33 wheel-move packet; empty arg releases the wheel. ---
            case "SETPILOT":
            {
                string pilotArg = args.Trim();
                if (pilotArg.Length == 0)
                    return SetPilot(ship, null);
                uint pilotUid = ParseHexSerial(pilotArg);
                if (pilotUid == 0) return false;
                return SetPilot(ship, _world.FindChar(new Serial(pilotUid)));
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Source-X dodirmovechange pattern: anchor check + SetMoveDir with rotated direction.
    /// </summary>
    private bool DirMoveChange(Ship ship, Direction dirFace, int offset)
    {
        if (ship.Anchored)
        {
            // Source-X CItemShip::OnSpeak: tillerman complains when player
            // requests motion with the anchor down.
            TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsDown);
            return true;
        }
        return SetMoveDir(ship, GetDirTurn(dirFace, offset), ShipMovementType.Normal);
    }

    private bool TurnShip(Ship ship, Direction dirFace, int offset)
    {
        if (ship.Anchored)
        {
            TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerAnchorIsDown);
            return true;
        }
        var oldMove = ship.DirMove;
        ship.DirMove = GetDirTurn(dirFace, offset);
        if (Face(ship, ship.DirMove)) return true;
        ship.DirMove = oldMove;
        TillerSpeak(ship, SphereNet.Game.Messages.Msg.TillerCantTurn);
        return true;
    }

    /// <summary>Assign or release the HS wheel pilot. Source-X only permits an
    /// unmounted, non-hovering character already aboard an unanchored ship.</summary>
    public bool SetPilot(Ship ship, Character? pilot)
    {
        Stop(ship);
        if (pilot == null || ship.Pilot == pilot.Uid)
        {
            ReleasePilotMarker(ship);
            ship.Pilot = Serial.Invalid;
            return true;
        }
        if (ship.Anchored || pilot.IsMounted || pilot.IsStatFlag(StatFlag.Hovering) ||
            !ship.CanBoard(pilot.Uid) || FindShipAt(pilot.Position) != ship)
            return false;

        ReleasePilotMarker(ship);
        ship.Pilot = pilot.Uid;

        // Taking the wheel is worn, not just recorded: Source-X equips the pilot with a
        // ship-pilot item linked back to the ship, on the mount layer (SetPilot,
        // CCMultiMovable.cpp:212). Only the ship's own uid field was set here, so
        // anything reading the pilot off the character - script or client - saw nobody
        // at the wheel.
        var marker = _world.CreateItem();
        marker.BaseId = ShipPilotItemId;
        marker.ItemType = ItemType.EqHorse;
        marker.Name = ship.MultiItem.Name;
        marker.Link = ship.MultiItem.Uid;
        marker.SetAttr(ObjAttributes.Newbie | ObjAttributes.Move_Never);
        pilot.Equip(marker, Layer.Horse);
        return true;
    }

    /// <summary>Take the wheel marker off whoever is holding it. The reference deletes
    /// the item when the same pilot stands down (:212).</summary>
    private void ReleasePilotMarker(Ship ship)
    {
        if (!ship.Pilot.IsValid) return;
        if (_world.FindChar(ship.Pilot) is not { } previous) return;

        var worn = previous.GetEquippedItem(Layer.Horse);
        if (worn == null || worn.BaseId != ShipPilotItemId) return;

        previous.Unequip(Layer.Horse);
        _world.RemoveItem(worn);
    }

    /// <summary>
    /// SHIPLAND: return to ground level. Source: CCMultiMovable.cpp:1219-1242
    /// </summary>
    private bool HandleShipLand(Ship ship)
    {
        if (_mapData == null) return false;
        var mi = ship.MultiItem;
        sbyte groundZ = _mapData.GetEffectiveZ(mi.MapIndex, mi.X, mi.Y);
        sbyte dz = (sbyte)(groundZ - mi.Z);
        if (dz == 0) return false;
        return MoveDelta(ship, 0, 0, dz);
    }

    // =====================================================================
    // Helpers — Source: CCMultiMovable.cpp
    // =====================================================================

    /// <summary>
    /// List all objects on ship: multi + components + deck chars/items.
    /// Source: CCMultiMovable.cpp:129-210
    /// </summary>
    public List<ObjBase> ListShipObjects(Ship ship)
    {
        var result = new List<ObjBase> { ship.MultiItem };

        foreach (var uid in ship.Components)
        {
            var item = _world.FindItem(uid);
            if (item != null) result.Add(item);
        }

        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def != null)
        {
            foreach (var obj in _world.GetObjectsInRange(ship.MultiItem.Position, DeckSearchRange(def)))
            {
                if (obj == ship.MultiItem) continue;
                if (obj is Item it)
                {
                    if (ship.Components.Contains(it.Uid)) continue;
                    if (RidesNowhere(it)) continue;
                }
                if (IsOnDeck(ship, def, obj))
                    result.Add(obj);
            }
        }

        return result;
    }

    private List<Character> ListDeckCharacters(Ship ship)
    {
        var result = new List<Character>();
        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def == null) return result;

        foreach (var ch in _world.GetCharsInRange(ship.MultiItem.Position, DeckSearchRange(def)))
        {
            if (IsOnDeck(ship, def, ch))
                result.Add(ch);
        }
        return result;
    }

    private List<Item> ListDeckItems(Ship ship)
    {
        var result = new List<Item>();
        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def == null) return result;

        foreach (var item in _world.GetItemsInRange(ship.MultiItem.Position, DeckSearchRange(def)))
        {
            if (item == ship.MultiItem) continue;
            if (item.ContainedIn.IsValid) continue;
            if (RidesNowhere(item)) continue;
            if (IsOnDeck(ship, def, item))
                result.Add(item);
        }
        return result;
    }

    /// <summary>How far from the anchor the deck can reach.
    ///
    /// Source-X measures the distance to the FARTHEST edge from the anchor
    /// (Multi_GetDistanceMax -> GetDistanceMax, CItemBase.cpp:1958). Halving the
    /// width instead assumed the anchor sits in the middle, so on a hull whose
    /// definition is not centred - anchor at one end - the passengers and cargo out on
    /// the far deck fell outside the search and were left behind when the ship moved
    /// or turned.</summary>
    private static int DeckSearchRange(MultiDef def)
    {
        int dist = Math.Abs(def.MinX);
        dist = Math.Max(dist, Math.Abs(def.MinY));
        dist = Math.Max(dist, Math.Abs(def.MaxX + 1));
        dist = Math.Max(dist, Math.Abs(def.MaxY + 1));
        return dist + 1;
    }

    /// <summary>An item that stays where it is when the ship sails. Source-X excludes
    /// ATTR_STATIC from the objects a multi carries (ListObjs,
    /// CCMultiMovable.cpp:200) - and only that flag: a MOVE_NEVER item is still
    /// carried, so the two must not be confused.</summary>
    private static bool RidesNowhere(Item item) => item.IsAttr(ObjAttributes.Static);

    private bool IsOnDeck(Ship ship, MultiDef def, ObjBase obj)
    {
        if (obj.MapIndex != ship.MultiItem.MapIndex) return false;
        short dx = (short)(obj.X - ship.MultiItem.X);
        short dy = (short)(obj.Y - ship.MultiItem.Y);
        if (dx < def.MinX || dx > def.MaxX || dy < def.MinY || dy > def.MaxY)
            return false;

        // Source-X measures from the DECK PLANE, not from the hull's anchor:
        // shipZ + max(3, height), with everything from two below it to a player's
        // height above riding along (ListObjs, CCMultiMovable.cpp:142). Measuring from
        // the anchor with a fixed window dragged along whatever sat under the deck and
        // let a tall ship lose what stood on it.
        int deckZ = ship.MultiItem.Z + Math.Max(3, (int)ship.MultiItem.DefHeight);
        int zdiff = obj.Z - deckZ;
        return zdiff >= -2 && zdiff <= PlayerHeight;
    }

    /// <summary>PLAYER_HEIGHT (uofiles_macros.h:36).</summary>
    private const int PlayerHeight = 16;

    /// <summary>ITEMID_SHIP_PILOT - the marker Source-X hangs on the pilot.</summary>
    private const ushort ShipPilotItemId = 0x3E96;

    /// <summary>The world's vertical bounds (uofiles_macros.h:28).</summary>
    private const int UoSizeZ = 127;
    private const int UoSizeMinZ = -127;


    /// <summary>
    /// Check if terrain at position is water.
    /// Source: CCMultiMovable.cpp:481-496
    /// </summary>
    public bool IsWaterAt(int mapId, short x, short y)
    {
        if (_mapData == null) return true;
        var terrain = _mapData.GetTerrainTile(mapId, x, y);
        var landData = _mapData.GetLandTileData(terrain.TileId);
        if (landData.IsWet)
            return true;

        // Source-X GetHeightPoint2: a WET static contributes CAN_I_WATER, so
        // static water tiles (coast fills, river/harbour water laid as statics)
        // are sailable even where the underlying land tile is not wet.
        foreach (var st in _mapData.GetStatics(mapId, x, y))
        {
            var td = _mapData.GetItemTileData(st.TileId);
            if ((td.Flags & SphereNet.MapData.Tiles.TileFlag.Wet) != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Source-X CanMoveTo (GetHeightPoint2 with CAN_I_WATER): the cell must be
    /// pure sailable water — water terrain AND no blocking STATIC (dock, rock,
    /// bridge piling) AND no other ship's hull occupying it. The old land-only
    /// check let ships sail straight through docks and other vessels.
    /// </summary>
    public bool CanSailInto(Ship? ship, int mapId, short x, short y, sbyte z)
    {
        if (!IsWaterAt(mapId, x, y))
            return false;

        // Blocking statics near the water plane (a bridge far above is fine).
        if (_mapData != null)
        {
            foreach (var st in _mapData.GetStatics(mapId, x, y))
            {
                var td = _mapData.GetItemTileData(st.TileId);
                bool blocks = (td.Flags & SphereNet.MapData.Tiles.TileFlag.Impassable) != 0;
                bool wetStatic = (td.Flags & SphereNet.MapData.Tiles.TileFlag.Wet) != 0;
                if (blocks && !wetStatic && Math.Abs(st.Z - z) <= 10)
                    return false;
            }
        }

        // Blocking dynamic items near the water plane (Source-X GetHeightPoint2
        // evaluates dynamics too): a lockdown/deco blocker or dock fixture laid
        // as a world item stops the hull the same way a static does.
        foreach (var wi in _world.GetItemsInRange(new Point3D(x, y, z, (byte)mapId), 0))
        {
            if (wi.IsDeleted || !wi.IsOnGround) continue;
            if (wi.X != x || wi.Y != y) continue;
            if (Math.Abs(wi.Z - z) > 10) continue;
            if (wi.IsStaticBlock) return false;
        }

        // Another ship's hull in the cell blocks (Source-X: multis participate
        // in GetHeightPoint2's block flags).
        foreach (var other in AllShips)
        {
            if (other == ship) continue;
            var odef = _multiDefs.Get(other.MultiItem.BaseId);
            if (odef == null) continue;
            if (other.MultiItem.MapIndex != mapId) continue;
            int rdx = x - other.MultiItem.X;
            int rdy = y - other.MultiItem.Y;
            if (rdx >= odef.MinX && rdx <= odef.MaxX && rdy >= odef.MinY && rdy <= odef.MaxY)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Move all ship objects by delta.
    /// Source: CCMultiMovable.cpp:274-421
    /// </summary>
    public bool MoveDelta(Ship ship, short dx, short dy, sbyte dz)
    {
        var anchor = ship.MultiItem.Position;
        int x = anchor.X + dx;
        int y = anchor.Y + dy;
        int z = anchor.Z + dz;
        if (x is < short.MinValue or > short.MaxValue ||
            y is < short.MinValue or > short.MaxValue ||
            z is < sbyte.MinValue or > sbyte.MaxValue)
            return false;

        // Source-X keeps a margin at the top and the bottom of the world before it
        // moves anything (MoveDelta, CCMultiMovable.cpp:284): a rise must stay clear
        // of the ceiling by a player's height, and a descent must leave room under
        // the hull. SphereNet only guarded the numeric overflow, so a magic ship
        // could be flown into the reserved space at either end.
        if (dz > 0 && z >= UoSizeZ - PlayerHeight - 1) return false;
        if (dz < 0 && z <= UoSizeMinZ + 3) return false;

        return MoveTo(ship, new Point3D((short)x, (short)y, (sbyte)z, anchor.Map));
    }

    private bool MoveTo(Ship ship, Point3D destination)
    {
        var anchor = ship.MultiItem.Position;
        int dx = destination.X - anchor.X;
        int dy = destination.Y - anchor.Y;
        int dz = destination.Z - anchor.Z;
        // Region boundary check BEFORE anything moves (Source-X order): the
        // multi's anchor decides the region; @RegionLeave/@RegionEnter fire on
        // the multi item and RETURN 1 cancels the whole step.
        var regionHook = OnShipRegionChange;
        if (regionHook != null)
        {
            var newAnchor = destination;
            // Resolve the BACKGROUND region the hull sits in (excluding the ship's
            // own region, which now wins FindRegion by smallest area) so a sail
            // across a sea/harbour boundary still fires @RegionLeave/@RegionEnter.
            var shipRegion = ship.RegionUid != 0 ? _world.FindRegionByUid(ship.RegionUid) : null;
            var oldRegion = shipRegion != null ? _world.FindParentRegion(shipRegion, anchor) : _world.FindRegion(anchor);
            var newRegion = shipRegion != null ? _world.FindParentRegion(shipRegion, newAnchor) : _world.FindRegion(newAnchor);
            if (!ReferenceEquals(oldRegion, newRegion) && !regionHook(ship, oldRegion, newRegion))
                return false;
            if (ship.MultiItem.IsDeleted)
                return false;
        }

        // Collect deck objects before moving (positions change during move)
        var deckChars = ListDeckCharacters(ship);
        var deckItems = ListDeckItems(ship);
        bool CanTranslate(Point3D p) =>
            p.X + dx is >= short.MinValue and <= short.MaxValue &&
            p.Y + dy is >= short.MinValue and <= short.MaxValue &&
            p.Z + dz is >= sbyte.MinValue and <= sbyte.MaxValue;
        if (ship.Components.Select(_world.FindItem).Any(item => item != null && !CanTranslate(item.Position)) ||
            deckChars.Any(ch => !CanTranslate(ch.Position)) || deckItems.Any(item => !CanTranslate(item.Position)))
            return false;

        // Move multi item
        var mi = ship.MultiItem;
        _world.PlaceItem(mi, destination);

        // Move components
        foreach (var uid in ship.Components)
        {
            var item = _world.FindItem(uid);
            if (item == null) continue;
            var p = item.Position;
            _world.PlaceItem(item, new Point3D(
                (short)(p.X + dx), (short)(p.Y + dy),
                (sbyte)(p.Z + dz), destination.Map));
        }

        // Move deck characters. The ship's region moves WITH them, so suppress
        // region enter/exit — they are not crossing a boundary, the boundary
        // travels with the hull.
        foreach (var ch in deckChars)
        {
            var p = ch.Position;
            _world.MoveCharacter(ch, new Point3D(
                (short)(p.X + dx), (short)(p.Y + dy),
                (sbyte)(p.Z + dz), destination.Map), fireRegionEvents: false);
        }

        // Move loose items on deck (not in containers, not components)
        foreach (var item in deckItems)
        {
            if (ship.Components.Contains(item.Uid)) continue;
            var p = item.Position;
            _world.PlaceItem(item, new Point3D(
                (short)(p.X + dx), (short)(p.Y + dy),
                (sbyte)(p.Z + dz), destination.Map));
        }

        // Re-position the ship's region to follow the hull to its new footprint.
        UpdateShipRegion(ship);

        OnShipMoved?.Invoke(ship);
        return true;
    }

    private bool CanMoveShipTo(Ship ship, short newX, short newY, short dx, short dy)
    {
        // ATTR_MAGIC ships can fly over land (Source-X CanMoveTo)
        if (ship.MultiItem.IsAttr(ObjAttributes.Magic))
            return true;

        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def == null) return false;

        byte map = ship.MultiItem.MapIndex;
        int sx = Math.Sign(dx);
        int sy = Math.Sign(dy);

        // Source-X ptFore.IsValidPoint(): the hull may not sail off the map —
        // a negative coordinate would reach the map readers as a bad index.
        var (mw, mh) = _mapData?.GetMapSize(map) ?? (7168, 4096);
        if (newX + def.MinX < 0 || newY + def.MinY < 0 ||
            newX + def.MaxX >= mw || newY + def.MaxY >= mh)
            return false;

        // Source-X CCMultiMovable::Move tests only the LEADING EDGE in the move
        // direction (both perpendicular edges for a diagonal) — the trailing cells
        // the ship already occupies are water by definition. Each leading tile must
        // be water (CanMoveTo); any non-water tile stops the ship.
        sbyte shipZ = ship.MultiItem.Position.Z;
        if (sy != 0) // leading N/S row
        {
            short ly = (short)(newY + (sy > 0 ? def.MaxY : def.MinY));
            for (short lx = (short)(newX + def.MinX); lx <= newX + def.MaxX; lx++)
                if (!CanSailInto(ship, map, lx, ly, shipZ))
                    return false;
        }
        if (sx != 0) // leading E/W column
        {
            short lx = (short)(newX + (sx > 0 ? def.MaxX : def.MinX));
            for (short ly = (short)(newY + def.MinY); ly <= newY + def.MaxY; ly++)
                if (!CanSailInto(ship, map, lx, ly, shipZ))
                    return false;
        }
        return true;
    }

    // =====================================================================
    // Dynamic ship region (Source-X CItemMulti::MultiRealizeRegion + CRegionWorld
    // repositioning as the multi moves). A ship gets a world region matching its
    // hull footprint, flagged Ship and inheriting the surrounding region's flags
    // (a guarded harbour / no-pvp zone carries through, with Ship added). Because
    // the ship sails between regions, inheritance is RECOMPUTED every move — reset
    // to base flags, then re-inherit from the current parent — never accumulated.
    // =====================================================================

    /// <summary>Realize the ship's region once (placement / load). Idempotent.</summary>
    private void CreateShipRegion(Ship ship)
    {
        if (ship.RegionUid != 0) return;
        var mi = ship.MultiItem;
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return;

        var region = new Region
        {
            Name = string.IsNullOrEmpty(mi.Name) ? "ship" : mi.Name,
            MapIndex = mi.MapIndex,
            Flags = RegionFlag.Ship | RegionFlag.InheritParentFlags,
        };
        region.AddRect(
            (short)(mi.X + def.MinX), (short)(mi.Y + def.MinY),
            (short)(mi.X + def.MaxX), (short)(mi.Y + def.MaxY));
        // Apply the multi's REGION.EVENTS tag so the ship region's @Enter/@Step
        // scripts fire (the tag round-trips on the item but was never realized).
        if (mi.TryGetTag("REGION.EVENTS", out string? regionEvents))
            region.AddEventsFromTag(regionEvents);
        _world.AddRegion(region);
        ship.RegionUid = region.Uid;
        UpdateShipRegion(ship); // set P + inherit from current surroundings
    }

    /// <summary>Re-position the ship region to the current hull footprint and
    /// recompute its inherited flags. Called after every move / turn.</summary>
    private void UpdateShipRegion(Ship ship)
    {
        if (ship.RegionUid == 0) return;
        var region = _world.FindRegionByUid(ship.RegionUid);
        if (region == null) return;
        var mi = ship.MultiItem;
        var def = _multiDefs.Get(mi.BaseId);
        if (def == null) return;

        region.MapIndex = mi.MapIndex;
        region.SetFootprint(
            (short)(mi.X + def.MinX), (short)(mi.Y + def.MinY),
            (short)(mi.X + def.MaxX), (short)(mi.Y + def.MaxY));
        var center = new Point3D(mi.X, mi.Y, mi.Z, mi.MapIndex);
        region.P = center;

        // Reset to base flags, then re-inherit from the region the hull now sits
        // in. Flags-only (like houses) so the deck never double-fires @Enter/@Exit.
        region.Flags = RegionFlag.Ship | RegionFlag.InheritParentFlags;
        var parent = _world.FindParentRegion(region, center);
        if (parent != null)
            region.InheritFromParent(parent);

        _world.InvalidateRegionCache();
    }

    /// <summary>Tear down the ship's region (dry-dock / decommission).</summary>
    private void RemoveShipRegion(Ship ship)
    {
        if (ship.RegionUid == 0) return;
        _world.RemoveRegion(ship.RegionUid);
        ship.RegionUid = 0;
    }

    /// <summary>The ship whose dynamic region contains <paramref name="pt"/>, or null.
    /// Deck membership is resolved through the Ship-flag region (region-bound, so it
    /// matches exactly the footprint the engine maintains) rather than a separate
    /// bounding-box test. Used by the boarding gate and eject.</summary>
    public Ship? FindShipAt(Point3D pt)
    {
        foreach (var ship in _ships.Values)
        {
            var region = ship.RegionUid != 0 ? _world.FindRegionByUid(ship.RegionUid) : null;
            if (region != null && region.IsFlag(RegionFlag.Ship) && region.Contains(pt))
                return ship;
        }
        return null;
    }

    /// <summary>Ban a player from the ship and, if they are currently aboard, eject
    /// them. The boarding gate (CanBoard) then keeps them off. Owner-proof.</summary>
    public bool BanFromShip(Ship ship, Serial uid)
    {
        if (uid == ship.Owner) return false; // never ban the owner
        bool added = ship.AddBan(uid);
        if (ship.Pilot == uid)
        {
            Stop(ship);
            ship.Pilot = Serial.Invalid;
        }
        // Eject any matching character already standing on the deck.
        foreach (var ch in ListDeckCharacters(ship))
            if (ch.Uid == uid)
                EjectFromShip(ship, ch);
        return added;
    }

    /// <summary>Lift a ship ban.</summary>
    public bool UnbanFromShip(Ship ship, Serial uid) => ship.RemoveBan(uid);

    /// <summary>Put a character off the ship onto the tile just beyond the starboard
    /// edge of the hull (Source-X eject drops the target at the multi's edge). The
    /// move fires region enter/exit normally — they really are leaving the ship.</summary>
    public void EjectFromShip(Ship ship, Character ch)
    {
        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        var mi = ship.MultiItem;
        short offX = def != null ? (short)(mi.X + def.MaxX + 1) : (short)(mi.X + 1);
        _world.MoveCharacter(ch, new Point3D(offX, ch.Y, ch.Z, mi.MapIndex));
    }

    private bool HandleShipGate(Ship ship, string args)
    {
        // SHIPGATE x,y,z,map — teleport entire ship
        if (!Point3D.TryParse(args.Trim(), out var dest))
            return false;
        var def = _multiDefs.Get(ship.MultiItem.BaseId);
        if (def == null) return false;
        if (!ship.MultiItem.IsAttr(ObjAttributes.Magic) && !CanPlaceShip(dest, def, ship))
            return false;
        var (width, height) = _mapData?.GetMapSize(dest.Map) ?? (7168, 4096);
        if (dest.X + def.MinX < 0 || dest.Y + def.MinY < 0 ||
            dest.X + def.MaxX >= width || dest.Y + def.MaxY >= height)
            return false;
        return MoveTo(ship, dest);
    }

    // =====================================================================
    // Save/Load (TAG-based, House pattern)
    // =====================================================================

    /// <summary>Serialize all ship metadata to item TAGs for persistence.</summary>
    public void SerializeAllToTags()
    {
        foreach (var (_, ship) in _ships)
        {
            var item = ship.MultiItem;
            item.SetTag("SHIP.OWNER", $"0{ship.Owner.Value:X}");
            var ownerObj = _world.FindObject(ship.Owner);
            if (ownerObj != null)
                item.SetTag("SHIP.OWNER_UUID", ownerObj.Uuid.ToString("D"));
            else
                item.RemoveTag("SHIP.OWNER_UUID");
            item.SetTag("SHIP.ANCHORED", ship.Anchored ? "1" : "0");
            item.SetTag("SHIP.DIRFACE", ((byte)ship.DirFace).ToString());
            item.SetTag("SHIP.DIRMOVE", ((byte)ship.DirMove).ToString());
            item.SetTag("SHIP.SPEEDPERIOD", ship.SpeedPeriod.ToString());
            item.SetTag("SHIP.SPEEDTILES", ship.SpeedTiles.ToString());
            item.SetTag("SHIP.SPEEDMODE", ((byte)ship.SpeedMode).ToString());

            if (ship.Pilot.IsValid)
                item.SetTag("SHIP.PILOT", $"0{ship.Pilot.Value:X}");
            else
                item.RemoveTag("SHIP.PILOT");

            if (ship.Components.Count > 0)
                item.SetTag("SHIP.COMPONENTS", string.Join(",", ship.Components.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("SHIP.COMPONENTS");

            if (ship.Bans.Count > 0)
                item.SetTag("SHIP.BANS", string.Join(",", ship.Bans.Select(s => $"0{s.Value:X}")));
            else
                item.RemoveTag("SHIP.BANS");
        }
    }

    /// <summary>
    /// Rebuild ship instances from IT_SHIP items after world load.
    /// </summary>
    public void DeserializeFromWorld()
    {
        foreach (var existing in _ships.Values.ToList())
            RemoveShipRegion(existing);
        _ships.Clear();
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not Item item) continue;
            if (item.ItemType != ItemType.Ship) continue;
            if (!item.TryGetTag("SHIP.OWNER", out string? ownerStr)) continue;

            uint ownerVal = ParseHexSerial(ownerStr);
            if (ownerVal == 0) continue;

            var ship = new Ship(item) { Owner = new Serial(ownerVal) };

            if (item.TryGetTag("SHIP.ANCHORED", out string? ancStr))
                ship.Anchored = ancStr == "1";
            if (item.TryGetTag("SHIP.DIRFACE", out string? dirStr) && byte.TryParse(dirStr, out byte df))
                ship.DirFace = Normalize4Dir((Direction)(df & 0x07));
            if (item.TryGetTag("SHIP.DIRMOVE", out string? moveStr) && byte.TryParse(moveStr, out byte dm))
                ship.DirMove = (Direction)(dm & 0x07);
            if (item.TryGetTag("SHIP.SPEEDPERIOD", out string? spStr) && ushort.TryParse(spStr, out ushort sp))
                ship.SpeedPeriod = Math.Max((ushort)1, sp);
            if (item.TryGetTag("SHIP.SPEEDTILES", out string? stStr) && byte.TryParse(stStr, out byte st))
                ship.SpeedTiles = Math.Clamp(st, (byte)1, (byte)16);
            // The ship class's script SHIPSPEED is authoritative (Source-X derives
            // the speed from the base def on load), so it overrides any stale saved
            // period — existing ships pick up the current script speed. Ships whose
            // def has no SHIPSPEED keep the value restored above.
            ApplyDefSpeed(ship, _multiDefs.Get(item.BaseId));
            if (item.TryGetTag("SHIP.SPEEDMODE", out string? smStr) && byte.TryParse(smStr, out byte sm) &&
                sm is >= (byte)ShipSpeedMode.OneTile and <= (byte)ShipSpeedMode.Fast)
                ship.SpeedMode = (ShipSpeedMode)sm;
            if (item.TryGetTag("SHIP.PILOT", out string? pilotStr))
                ship.Pilot = new Serial(ParseHexSerial(pilotStr));

            // Rebuild component list and categorize
            if (item.TryGetTag("SHIP.COMPONENTS", out string? compStr) && !string.IsNullOrWhiteSpace(compStr))
            {
                foreach (var part in compStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    uint val = ParseHexSerial(part);
                    if (val == 0) continue;
                    var compItem = _world.FindItem(new Serial(val));
                    if (compItem != null)
                    {
                        compItem.Link = item.Uid;
                        compItem.SetAttr(ObjAttributes.Move_Never);
                        ship.AddComponent(compItem);
                    }
                }
            }

            if (item.TryGetTag("SHIP.BANS", out string? bansStr) && !string.IsNullOrWhiteSpace(bansStr))
            {
                foreach (var part in bansStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    uint val = ParseHexSerial(part);
                    if (val != 0) ship.AddBan(new Serial(val));
                }
            }

            _ships[item.Uid] = ship;
            CreateShipRegion(ship);
            if (ship.Pilot.IsValid)
            {
                var pilot = _world.FindChar(ship.Pilot);
                if (pilot == null || ship.Anchored || pilot.IsMounted || pilot.IsStatFlag(StatFlag.Hovering) ||
                    !ship.CanBoard(pilot.Uid) || FindShipAt(pilot.Position) != ship)
                    ship.Pilot = Serial.Invalid;
            }
        }
    }

    // =====================================================================
    // Direction Helpers
    // =====================================================================

    /// <summary>Normalize to 4-direction (N/E/S/W).</summary>
    private static Direction Normalize4Dir(Direction dir)
    {
        return ((byte)dir & 0x07) switch
        {
            0 or 1 => Direction.North,
            2 or 3 => Direction.East,
            4 or 5 => Direction.South,
            _ => Direction.West,
        };
    }

    /// <summary>Convert 4-direction to 0-3 index (N=0, E=1, S=2, W=3).</summary>
    private static int DirTo4Index(Direction dir)
    {
        return dir switch
        {
            Direction.North => 0,
            Direction.East => 1,
            Direction.South => 2,
            Direction.West => 3,
            _ => 0,
        };
    }

    /// <summary>4-direction from index.</summary>
    private static Direction IndexTo4Dir(int idx)
    {
        return (idx & 3) switch
        {
            0 => Direction.North,
            1 => Direction.East,
            2 => Direction.South,
            3 => Direction.West,
            _ => Direction.North,
        };
    }

    /// <summary>Rotate a 4-direction by steps (1=clockwise 90°).</summary>
    private static Direction RotateDir4(Direction dir, int steps)
    {
        int idx = DirTo4Index(dir);
        return IndexTo4Dir((idx + steps) % 4);
    }

    /// <summary>
    /// 8-directional turn. Maps to GetDirTurn in Source-X.
    /// offset: +2 = right 90°, -2 = left 90°, +4 = 180°, ±1 = 45° diagonal
    /// </summary>
    private static Direction GetDirTurn(Direction dir, int offset)
    {
        return (Direction)(((byte)dir + offset + 8) % 8);
    }

    private static Direction OppositeDir(Direction dir)
    {
        return GetDirTurn(dir, 4);
    }

    /// <summary>Try parse direction from string (name or numeric).</summary>
    private static bool TryParseDir(string args, out Direction dir)
    {
        var s = args.Trim();
        if (Enum.TryParse(s, true, out dir))
            return true;
        if (byte.TryParse(s, out byte b) && b < 8)
        {
            dir = (Direction)b;
            return true;
        }
        dir = Direction.North;
        return false;
    }

    private static void GetDirDelta(Direction dir, out short dx, out short dy)
    {
        dx = dy = 0;
        switch (dir)
        {
            case Direction.North: dy = -1; break;
            case Direction.NorthEast: dx = 1; dy = -1; break;
            case Direction.East: dx = 1; break;
            case Direction.SouthEast: dx = 1; dy = 1; break;
            case Direction.South: dy = 1; break;
            case Direction.SouthWest: dx = -1; dy = 1; break;
            case Direction.West: dx = -1; break;
            case Direction.NorthWest: dx = -1; dy = -1; break;
        }
    }

    /// <summary>
    /// Rotate point around origin by rotation steps.
    /// Source: CCMultiMovable.cpp — right90 (x,y)→(-y,x), left90 (x,y)→(y,-x), 180° (x,y)→(-x,-y)
    /// </summary>
    private static void RotatePoint(ref short x, ref short y, int rotSteps)
    {
        for (int i = 0; i < rotSteps; i++)
        {
            short ox = x;
            x = (short)(-y);
            y = ox;
        }
    }

    /// <summary>
    /// Classify a ship component tile ID to an ItemType.
    /// Common ship component IDs in UO.
    /// </summary>
    private static ItemType ClassifyShipComponent(ushort tileId)
    {
        // Source-X: a ship component's category comes from its ITEMDEF TYPE
        // (t_ship_tiller / t_ship_plank / t_ship_side / t_ship_hold, …). Honor
        // that when the tile's itemdef carries a ship-component type; otherwise
        // it's deck/other. Without this every component was ShipOther, so the
        // tiller (steering), hold (cargo) and planks (boarding) never worked on a
        // freshly-placed ship.
        var def = Definitions.DefinitionLoader.GetItemDef(tileId);
        if (def != null)
        {
            switch (def.Type)
            {
                case ItemType.ShipTiller:
                case ItemType.ShipPlank:
                case ItemType.ShipSide:
                case ItemType.ShipSideLocked:
                case ItemType.ShipHold:
                case ItemType.ShipHoldLock:
                    return def.Type;
            }
        }
        return ItemType.ShipOther;
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
}
