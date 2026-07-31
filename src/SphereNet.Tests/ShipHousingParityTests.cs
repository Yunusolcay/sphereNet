using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

// Source-X ship/housing (multi) parity (wiki/9.txt audit): diagonal ship movement
// is preserved, custom-foundation houses are restored on load, and persisted
// lockdown/secure entries are loaded without re-running capacity checks.
public class ShipHousingParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    // ---- house storage budget (Source-X CItemMulti storage getters) ----

    /// <summary>Source-X derives every cap from GetMaxStorage, which is the base
    /// budget plus the INCREASEDSTORAGE percentage: MaxLockdowns is that total
    /// times LOCKDOWNSPERCENT, MaxVendors scales the vendor budget by the same
    /// percentage, and lockdowns plus secured containers share one pool.</summary>
    [Fact]
    public void HouseStorage_DerivesEveryCapFromMaxStorage()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(300, 300, 0, 0));

        var house = new House(multi)
        {
            BaseStorage = 400,
            LockdownsPercent = 50,
            BaseVendors = 10,
        };

        // No bonus: max storage is the base budget.
        Assert.Equal(400, house.MaxStorage);
        Assert.Equal(200, house.MaxLockdowns);
        Assert.Equal(200, house.MaxSecure);
        Assert.Equal(10, house.MaxVendors);

        // A 50% storage increase lifts the total, the lockdown share and the
        // vendor budget together.
        house.IncreasedStorage = 50;
        Assert.Equal(600, house.MaxStorage);
        Assert.Equal(300, house.MaxLockdowns);
        Assert.Equal(300, house.MaxSecure);
        Assert.Equal(15, house.MaxVendors);
    }

    /// <summary>BASEVENDORS is read off the [MULTIDEF] but used to stop there:
    /// the placed house never received it, so its vendor budget was always
    /// zero.</summary>
    [Fact]
    public void PlacedHouse_InheritsBaseVendorsFromTheMultiDef()
    {
        var world = CreateWorld();
        var registry = new MultiRegistry();
        var def = new MultiDef { Id = 0x4001, BaseStorage = 500, BaseVendors = 6 };
        registry.Register(def);

        var engine = new HousingEngine(world, registry);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(320, 320, 0, 0));

        var house = engine.PlaceHouse(owner, 0x4001, new Point3D(330, 330, 0, 0));
        Assert.NotNull(house);
        Assert.Equal(500, house!.BaseStorage);
        Assert.Equal(6, house.BaseVendors);
        Assert.Equal(6, house.MaxVendors);
    }

    /// <summary>The access list was maintained by ADDACCESS/DELACCESS but had no
    /// readout, so a script could add to it and never see it again.</summary>
    [Fact]
    public void HouseAccessList_IsReadableFromScript()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(340, 340, 0, 0));

        var guest = world.CreateCharacter();
        world.PlaceCharacter(guest, new Point3D(341, 340, 0, 0));

        var house = new House(multi);
        house.AddAccess(guest.Uid);
        Item.ResolveHouse = uid => uid == multi.Uid ? house : null;
        try
        {
            Assert.True(multi.TryGetProperty("HOUSE.ACCESSES", out string? count));
            Assert.Equal("1", count);
            Assert.True(multi.TryGetProperty("HOUSE.ACCESS.0", out string? first));
            Assert.Equal($"0{guest.Uid.Value:X8}", first);
        }
        finally { Item.ResolveHouse = null; }
    }

    // ---- #9: ship movement keeps all 8 directions ----

    [Fact]
    public void SetMoveDir_PreservesDiagonalMovement()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        world.PlaceItem(multi, new Point3D(200, 200, 0, 0));
        var engine = new ShipEngine(world, new MultiRegistry(), null);
        var ship = new Ship(multi);

        Assert.True(engine.SetMoveDir(ship, Direction.NorthEast, ShipMovementType.Normal));
        Assert.Equal(Direction.NorthEast, ship.DirMove); // not collapsed to East

        Assert.True(engine.SetMoveDir(ship, Direction.SouthWest, ShipMovementType.Normal));
        Assert.Equal(Direction.SouthWest, ship.DirMove);
    }

    // ---- #2: custom-foundation houses are rebuilt on load ----

    [Fact]
    public void DeserializeFromWorld_RestoresCustomFoundationHouse()
    {
        var world = CreateWorld();
        var engine = new HousingEngine(world, new MultiRegistry());

        var custom = world.CreateItem();
        custom.BaseId = 0x1BC0;
        custom.ItemType = ItemType.MultiCustom; // PlaceHouse stamps custom foundations this way
        world.PlaceItem(custom, new Point3D(300, 300, 0, 0));
        custom.SetTag("HOUSE.OWNER", $"0{new Serial(0x401).Value:x8}");

        engine.DeserializeFromWorld();

        Assert.NotNull(engine.GetHouse(custom.Uid)); // was dropped (only Multi was read) before
    }

    // ---- #7: persisted lockdown/secure load past the current limit ----

    [Fact]
    public void LockdownForLoad_BypassesCapacityLimit()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.BaseId = 0x1BC0;
        world.PlaceItem(multi, new Point3D(400, 400, 0, 0));

        var house = new House(multi) { Owner = new Serial(0x401), BaseStorage = 4 };
        // MaxLockdowns = 4 * 50% = 2, MaxSecure = 4 - 2 = 2.
        Assert.Equal(2, house.MaxLockdowns);

        // A saved house with 4 lockdowns must load all 4 even though the live cap is 2.
        house.LockdownForLoad(new Serial(0x1001));
        house.LockdownForLoad(new Serial(0x1002));
        house.LockdownForLoad(new Serial(0x1003));
        house.LockdownForLoad(new Serial(0x1004));
        Assert.Equal(4, house.Lockdowns.Count);

        house.SecureForLoad(new Serial(0x2001));
        house.SecureForLoad(new Serial(0x2002));
        house.SecureForLoad(new Serial(0x2003));
        Assert.Equal(3, house.SecureContainers.Count);
    }

    // ---- #5: dynamic ship region (CItemMulti::MultiRealizeRegion + CRegionWorld move) ----

    private static MultiRegistry MakeShipRegistry(ushort id = 0x4000)
    {
        var def = new MultiDef { Id = id, Name = "small boat" };
        // 1x1 hull so the footprint is a single tile — keeps region geometry simple.
        def.Components.Add(new MultiComponent { TileId = 0x3E40, DeltaX = 0, DeltaY = 0, DeltaZ = 0, Visible = true });
        def.RecalcBounds();
        var reg = new MultiRegistry();
        reg.Register(def);
        return reg;
    }

    [Fact]
    public void PlaceShip_CreatesShipRegion_WithShipFlag()
    {
        var world = CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        Assert.NotNull(ship);
        Assert.NotEqual(0u, ship!.RegionUid);

        var r = world.FindRegion(new Point3D(200, 200, 0, 0));
        Assert.NotNull(r);
        Assert.True(r!.IsFlag(RegionFlag.Ship));

        // Dry-dock tears the region down.
        engine.RemoveShip(ship.MultiItem.Uid, owner);
        var after = world.FindRegion(new Point3D(200, 200, 0, 0));
        Assert.True(after == null || !after.IsFlag(RegionFlag.Ship));
    }

    [Fact]
    public void ShipRegion_FollowsHull_OnMove()
    {
        var world = CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null); // null map = open water
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        Assert.NotNull(ship);
        ship!.Anchored = false;

        Assert.True(engine.Move(ship, Direction.East, 5));

        // Region followed the hull east; the vacated tile is no longer the ship region.
        var atNew = world.FindRegion(new Point3D(205, 200, 0, 0));
        Assert.NotNull(atNew);
        Assert.True(atNew!.IsFlag(RegionFlag.Ship));

        var atOld = world.FindRegion(new Point3D(200, 200, 0, 0));
        Assert.True(atOld == null || !atOld.IsFlag(RegionFlag.Ship));
    }

    [Fact]
    public void ShipRegion_RecomputesInheritedFlags_AsItSails()
    {
        var world = CreateWorld();

        // Two adjacent background regions: a guarded harbour (west) and an open
        // no-pvp sea (east). Both far larger than the 1x1 hull.
        var harbour = new Region { Name = "harbour", MapIndex = 0, Flags = RegionFlag.Guarded };
        harbour.AddRect(100, 100, 199, 300);
        world.AddRegion(harbour);
        var sea = new Region { Name = "sea", MapIndex = 0, Flags = RegionFlag.NoPvP };
        sea.AddRect(200, 100, 300, 300);
        world.AddRegion(sea);

        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        // Start in the harbour: ship region inherits Guarded, not NoPvP.
        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(150, 200, 0, 0), Direction.North);
        Assert.NotNull(ship);
        ship!.Anchored = false;

        var rWest = world.FindRegion(new Point3D(150, 200, 0, 0));
        Assert.True(rWest!.IsFlag(RegionFlag.Ship));
        Assert.True(rWest.IsFlag(RegionFlag.Guarded));
        Assert.False(rWest.IsFlag(RegionFlag.NoPvP));

        // Sail east into the sea — inheritance is RECOMPUTED, not accumulated:
        // Guarded drops, NoPvP is picked up.
        Assert.True(engine.Move(ship, Direction.East, 50));
        var rEast = world.FindRegion(new Point3D(200, 200, 0, 0));
        Assert.True(rEast!.IsFlag(RegionFlag.Ship));
        Assert.True(rEast.IsFlag(RegionFlag.NoPvP));
        Assert.False(rEast.IsFlag(RegionFlag.Guarded));
    }

    // ---- ban / eject bound to the ship region ----

    [Fact]
    public void FindShipAt_ResolvesShipThroughRegion()
    {
        var world = CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        Assert.NotNull(ship);

        Assert.Same(ship, engine.FindShipAt(new Point3D(200, 200, 0, 0)));
        Assert.Null(engine.FindShipAt(new Point3D(220, 220, 0, 0)));
    }

    [Fact]
    public void BanFromShip_EjectsAboardPlayer_AndBlocksReboard()
    {
        var world = CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        Assert.NotNull(ship);

        // A player standing on the deck.
        var intruder = world.CreateCharacter();
        world.PlaceCharacter(intruder, new Point3D(200, 200, 0, 0));
        Assert.Same(ship, engine.FindShipAt(intruder.Position)); // aboard

        Assert.True(engine.BanFromShip(ship!, intruder.Uid));
        Assert.True(ship!.IsBanned(intruder.Uid));
        // Ejected: no longer resolves to the ship region.
        Assert.Null(engine.FindShipAt(intruder.Position));
        // And the boarding gate now refuses them.
        Assert.False(ship.CanBoard(intruder.Uid));
        // The owner is never barred.
        Assert.True(ship.CanBoard(owner.Uid));
    }

    [Fact]
    public void Owner_CannotBeBanned()
    {
        var world = CreateWorld();
        var engine = new ShipEngine(world, MakeShipRegistry(), null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));
        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);

        Assert.False(engine.BanFromShip(ship!, owner.Uid));
        Assert.False(ship!.IsBanned(owner.Uid));
        Assert.True(ship.CanBoard(owner.Uid));
    }

    [Fact]
    public void ShipBans_Persist_AcrossSaveLoad()
    {
        var world = CreateWorld();
        var registry = MakeShipRegistry();
        var engine = new ShipEngine(world, registry, null);
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));
        var ship = engine.PlaceShip(owner, 0x4000, new Point3D(200, 200, 0, 0), Direction.North);
        Assert.NotNull(ship);

        var banned = new Serial(0x9001);
        ship!.AddBan(banned);
        engine.SerializeAllToTags();

        // A fresh engine rebuilds ships (and their regions) from the world tags.
        var engine2 = new ShipEngine(world, registry, null);
        engine2.DeserializeFromWorld();
        var loaded = engine2.GetShip(ship.MultiItem.Uid);
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsBanned(banned));
    }
}
