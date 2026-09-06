using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Registering a ship, owning one, and what a turn or a gate command is allowed to
/// refuse.
///
/// Source-X keeps a ship's owner in the plain OWNER field and routes a write to it
/// through SetOwner (CItemMulti.cpp:2572/3068), rebuilds the plank list from the
/// components that are actually there (CItemShip.cpp:285), and reports one Ship_Move
/// per completed Move command with the direction in ARGN1 and whether it stopped in
/// ARGN2 (CCMultiMovable.cpp:863). A turn starts from the definition's own REGIONFLAGS
/// (CItemMulti.cpp:232), skips every point the hull already occupies (:545), asks
/// GetHeightPoint2 about the one cell and not its neighbours (CanMoveTo, :481), and
/// accepts only the four hull facings (:510). SHIPGATE hands its delta straight to
/// MoveDelta with no sailing check (:1041).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ShipParity10CDTests
{
    private const ushort HullNorth = 0x4000;
    private const ushort HullEast = 0x4001;
    private const ushort PlankClosedNorth = 0x3EB1;
    private const ushort BlockerId = 0x1234;
    private const ushort DryLand = 3;
    private const ushort WaterStatic = 0x1796;

    // ------------------------------------------------------------------ fixtures

    private static GameWorld CreateWorld(MapDataManager? map = null)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        if (map != null) world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>All four facings occupy the SAME square, so a turn claims no new
    /// ground at all — the shape the reference's "already overlaps" skip is about.</summary>
    private static MultiRegistry SquareRegistry(bool withPlank = false)
    {
        var registry = new MultiRegistry();
        foreach (ushort id in new ushort[] { HullNorth, HullEast, 0x4002, 0x4003 })
        {
            var def = new MultiDef { Id = id, Name = "test ship" };
            for (short x = -2; x <= 2; x++)
                for (short y = -2; y <= 2; y++)
                    def.Components.Add(new MultiComponent
                    { TileId = 0x3E40, DeltaX = x, DeltaY = y, DeltaZ = 0, Visible = true });
            if (withPlank)
                for (int i = 0; i < 2; i++)
                    def.Components.Add(new MultiComponent
                    { TileId = PlankClosedNorth, DeltaX = -2, DeltaY = (short)i, DeltaZ = 0, Visible = false });
            def.RecalcBounds();
            registry.Register(def);
        }
        return registry;
    }

    /// <summary>North runs long north-south, east runs long east-west: a turn really
    /// does claim ground it did not hold.</summary>
    private static MultiRegistry CrossRegistry()
    {
        var registry = new MultiRegistry();
        foreach (ushort id in new ushort[] { HullNorth, HullEast, 0x4002, 0x4003 })
        {
            bool eastWest = id is HullEast or 0x4003;
            var def = new MultiDef { Id = id, Name = "cross ship" };
            short spanX = (short)(eastWest ? 2 : 1);
            short spanY = (short)(eastWest ? 1 : 2);
            for (short x = (short)-spanX; x <= spanX; x++)
                for (short y = (short)-spanY; y <= spanY; y++)
                    def.Components.Add(new MultiComponent
                    { TileId = 0x3E40, DeltaX = x, DeltaY = y, DeltaZ = 0, Visible = true });
            def.RecalcBounds();
            registry.Register(def);
        }
        return registry;
    }

    private static (ShipEngine Engine, Ship Ship, Character Owner, GameWorld World) Bench(
        MultiRegistry registry, MapDataManager? map = null)
    {
        var world = CreateWorld(map);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var engine = new ShipEngine(world, registry, map)
        {
            MaxShipsPerPlayer = -1,
            MaxShipsPerAccount = -1,
        };
        Item.ResolveShipEngine = () => engine;
        var ship = engine.PlaceShip(owner, HullNorth, new Point3D(100, 100, 0, 0), Direction.North)!;
        ship.Anchored = false;
        return (engine, ship, owner, world);
    }

    /// <summary>An item the placement check treats as an obstacle through the
    /// definition's CAN=I_BLOCK, the path that has no cell guard of its own.</summary>
    private static Item Blocker(GameWorld world, short x, short y, sbyte z)
    {
        var table = (Dictionary<int, ItemDef>)typeof(SphereNet.Game.Definitions.DefinitionLoader)
            .GetField("_itemDefs", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        table[BlockerId] = new ItemDef(new ResourceId(ResType.ItemDef, BlockerId))
        { Can = CanFlags.I_Block };

        var item = world.CreateItem();
        item.BaseId = BlockerId;
        world.PlaceItem(item, new Point3D(x, y, z, 0));
        return item;
    }

    /// <summary>The component classifier reads the tile's ITEMDEF TYPE, so a plank
    /// is only a plank once its definition says so.</summary>
    private static void DefinePlankType()
    {
        var table = (Dictionary<int, ItemDef>)typeof(SphereNet.Game.Definitions.DefinitionLoader)
            .GetField("_itemDefs", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        table[PlankClosedNorth] = new ItemDef(new ResourceId(ResType.ItemDef, PlankClosedNorth))
        { Type = ItemType.ShipPlank };
    }

    private static MapDataManager DryMap()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: DryLand);
        return map;
    }

    /// <summary>Dry land with a harbour laid over it as WET statics, the way Source-X
    /// reads one: GetHeightPoint2 takes a wet static as sailable water even where the
    /// land beneath it is not. Water runs x = 90..<paramref name="waterMaxX"/>,
    /// y = 90..110; everything else is dry.</summary>
    private static MapDataManager HarbourMap(short waterMaxX)
    {
        var map = DryMap();
        map.SetSyntheticItemTile(WaterStatic, new ItemTileData { Flags = TileFlag.Wet });
        for (short x = 90; x <= waterMaxX; x++)
            for (short y = 90; y <= 110; y++)
                map.AddSyntheticStatic(0, x, y, WaterStatic, 0);
        return map;
    }

    // =================================================================== 10C-1

    [Fact]
    public void AShipOutOfAClassicSaveIsRegisteredFromItsOwnerField()
    {
        var (engine, ship, owner, world) = Bench(SquareRegistry());
        var hull = ship.MultiItem;
        // What a Source-X save leaves behind: the plain OWNER field, no SHIP.* tags.
        engine.SerializeAllToTags();
        foreach (string key in new[] { "SHIP.OWNER", "SHIP.OWNER_UUID" })
            hull.RemoveTag(key);
        hull.SetTag("OWNER", $"0{owner.Uid.Value:X}");

        engine.DeserializeFromWorld();

        var reloaded = engine.GetShip(hull.Uid);
        Assert.NotNull(reloaded);
        Assert.Equal(owner.Uid, reloaded!.Owner);
        Assert.NotEqual(0u, reloaded.RegionUid);
        Assert.NotNull(world.FindRegionByUid(reloaded.RegionUid));
    }

    // =================================================================== 10C-2

    [Fact]
    public void WritingOwnerOnALiveShipHandsOverTheShip()
    {
        var (engine, ship, oldOwner, world) = Bench(SquareRegistry());
        var newOwner = world.CreateCharacter();
        newOwner.IsPlayer = true;
        world.PlaceCharacter(newOwner, new Point3D(60, 60, 0, 0));

        Assert.True(ship.MultiItem.TrySetProperty("OWNER", $"0{newOwner.Uid.Value:X}"));

        Assert.Equal(newOwner.Uid, ship.Owner);
        // and the authority moved with it: the old owner can no longer redeed.
        Assert.Null(engine.RemoveShip(ship.MultiItem.Uid, oldOwner));
        Assert.NotNull(engine.RemoveShip(ship.MultiItem.Uid, newOwner));
    }

    [Fact]
    public void TheOwnerTagStillRoundTripsOnTheItem()
    {
        var (_, ship, _, world) = Bench(SquareRegistry());
        var newOwner = world.CreateCharacter();
        world.PlaceCharacter(newOwner, new Point3D(60, 60, 0, 0));

        ship.MultiItem.TrySetProperty("OWNER", $"0{newOwner.Uid.Value:X}");

        Assert.True(ship.MultiItem.TryGetTag("OWNER", out string? raw));
        Assert.Equal($"0{newOwner.Uid.Value:X}", raw);
    }

    [Fact]
    public void AnUnparseableOwnerLeavesTheShipWhereItWas()
    {
        var (_, ship, oldOwner, _) = Bench(SquareRegistry());

        ship.MultiItem.TrySetProperty("OWNER", "");

        Assert.Equal(oldOwner.Uid, ship.Owner);
    }

    // =================================================================== 10C-3

    [Fact]
    public void ADeletedPlankLeavesTheList()
    {
        DefinePlankType();
        var (_, ship, _, world) = Bench(SquareRegistry(withPlank: true));
        Assert.Equal(2, ship.GetPlankCount(world));
        var first = ship.GetPlank(0, world)!;

        world.RemoveItem(first);

        Assert.Equal(1, ship.GetPlankCount(world));
        Assert.NotNull(ship.GetPlank(0, world));
    }

    // =================================================================== 10C-4

    [Fact]
    public void AMoveOrderIsOneEventCarryingItsDirection()
    {
        var (engine, ship, _, _) = Bench(SquareRegistry());
        var seen = new List<int>();
        engine.OnShipMoveCommand = (_, dir) => seen.Add(dir);

        Assert.True(engine.Move(ship, Direction.East, 3));

        Assert.Equal((int)Direction.East, Assert.Single(seen));
    }

    [Fact]
    public void AnOrderThatRunsIntoSomethingReportsNothing()
    {
        // The harbour ends at x = 102, so the leading column of an eastward step is
        // dry land. Upstream speaks through the tiller and returns BEFORE the trigger
        // when the order is cut short (:851).
        var (engine, ship, _, _) = Bench(SquareRegistry(), HarbourMap(102));
        int fired = 0;
        engine.OnShipMoveCommand = (_, _) => fired++;

        Assert.False(engine.Move(ship, Direction.East, 1));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void ShovingTheHullDirectlyIsNotAMoveOrder()
    {
        var (engine, ship, _, _) = Bench(SquareRegistry());
        int fired = 0;
        engine.OnShipMoveCommand = (_, _) => fired++;

        Assert.True(engine.MoveDelta(ship, 1, 0, 0));

        Assert.Equal(0, fired);
    }

    // =================================================================== 10D-1

    [Fact]
    public void MergeScriptMetadata_CarriesRegionFlags()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_rf_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [MULTIDEF 04000]
            DEFNAME=m_test_safe_ship
            NAME=protected ship
            TYPE=t_ship
            REGIONFLAGS=02080
            """);
        stack.Resources.LoadResourceFile(path);

        var registry = SquareRegistry();
        registry.MergeScriptMetadata(stack.Resources);

        var def = registry.Get(HullNorth)!;
        // Sphere writes region flags as leading-zero hex: 02080 = Safe | NoBuild.
        Assert.True(def.RegionFlags.HasFlag(RegionFlag.Safe));
        Assert.True(def.RegionFlags.HasFlag(RegionFlag.NoBuild));
    }

    [Fact]
    public void APlacedHullStartsFromItsDefinitionsRegionFlags()
    {
        var registry = SquareRegistry();
        foreach (ushort id in new ushort[] { HullNorth, HullEast, 0x4002, 0x4003 })
            registry.Get(id)!.RegionFlags = RegionFlag.Safe | RegionFlag.NoBuild;

        var (_, ship, _, world) = Bench(registry);

        var region = world.FindRegionByUid(ship.RegionUid)!;
        Assert.True(region.IsFlag(RegionFlag.Safe));
        Assert.True(region.IsFlag(RegionFlag.NoBuild));
        Assert.True(region.IsFlag(RegionFlag.Ship));
    }

    [Fact]
    public void SailingDoesNotWashTheDefinitionsFlagsOff()
    {
        var registry = SquareRegistry();
        foreach (ushort id in new ushort[] { HullNorth, HullEast, 0x4002, 0x4003 })
            registry.Get(id)!.RegionFlags = RegionFlag.Safe | RegionFlag.NoBuild;

        var (engine, ship, _, world) = Bench(registry);
        Assert.True(engine.MoveDelta(ship, 1, 0, 0));
        Assert.True(engine.Face(ship, Direction.East));

        var region = world.FindRegionByUid(ship.RegionUid)!;
        Assert.True(region.IsFlag(RegionFlag.Safe));
        Assert.True(region.IsFlag(RegionFlag.NoBuild));
    }

    // =================================================================== 10D-2

    [Fact]
    public void GroundTheHullAlreadyFloatsOnDoesNotRefuseATurn()
    {
        var (engine, ship, _, world) = Bench(SquareRegistry());
        // Right under the deck, inside the footprint the hull holds now and will
        // still hold afterwards: the turn claims nothing new.
        Blocker(world, 100, 100, 0);

        Assert.True(engine.Face(ship, Direction.East));
        Assert.Equal(Direction.East, ship.DirFace);
    }

    [Fact]
    public void GroundTheTurnNewlyClaimsIsStillChecked()
    {
        var (engine, ship, _, world) = Bench(CrossRegistry());
        // The north hull is X -1..1; turning east claims X = 2. The blocker sits in
        // that new cell.
        Blocker(world, 102, 100, 0);

        Assert.False(engine.Face(ship, Direction.East));
        Assert.Equal(Direction.North, ship.DirFace);
    }

    // =================================================================== 10D-3

    [Fact]
    public void ABlockerBesideTheHullIsNotInTheHullsWay()
    {
        var (engine, ship, _, world) = Bench(CrossRegistry());
        // One tile PAST the east hull's X = 2 edge — geometrically clear.
        Blocker(world, 103, 100, 0);

        Assert.True(engine.Face(ship, Direction.East));
        Assert.Equal(Direction.East, ship.DirFace);
    }

    // =================================================================== 10D-4

    [Fact]
    public void ShipGateCarriesTheHullOntoDryLand()
    {
        // Water where the ship is; (200,200) is open ground.
        var (engine, ship, _, _) = Bench(SquareRegistry(), HarbourMap(110));

        Assert.True(engine.ExecuteCommand(ship, "SHIPGATE", "200,200,0,0"));

        Assert.Equal(200, ship.MultiItem.X);
        Assert.Equal(200, ship.MultiItem.Y);
    }

    [Fact]
    public void SailingItselfStillNeedsWater()
    {
        var (engine, ship, _, _) = Bench(SquareRegistry(), HarbourMap(102));

        Assert.False(engine.Move(ship, Direction.East, 1));
        Assert.Equal(100, ship.MultiItem.X);
    }

    [Fact]
    public void ShipGateStillRefusesAPointOffTheMap()
    {
        var (engine, ship, _, _) = Bench(SquareRegistry());

        Assert.False(engine.ExecuteCommand(ship, "SHIPGATE", "1,1,0,0"));
        Assert.Equal(100, ship.MultiItem.X);
    }

    // =================================================================== 10D-5

    [Theory]
    [InlineData("1")] // NE
    [InlineData("3")] // SE
    [InlineData("5")] // SW
    [InlineData("7")] // NW
    public void AHullWillNotFaceADiagonal(string arg)
    {
        var (engine, ship, _, _) = Bench(SquareRegistry());

        Assert.False(engine.ExecuteCommand(ship, "SHIPFACE", arg));
        Assert.Equal(Direction.North, ship.DirFace);
    }

    [Fact]
    public void AHullStillFacesTheFourSides()
    {
        var (engine, ship, _, _) = Bench(SquareRegistry());

        Assert.True(engine.ExecuteCommand(ship, "SHIPFACE", "2"));
        Assert.Equal(Direction.East, ship.DirFace);
    }

    [Fact]
    public void DiagonalSailingIsUntouched()
    {
        var (engine, ship, _, _) = Bench(SquareRegistry());

        Assert.True(engine.ExecuteCommand(ship, "SHIPMOVE", "1")); // NE
        Assert.Equal(101, ship.MultiItem.X);
        Assert.Equal(99, ship.MultiItem.Y);
    }
}
