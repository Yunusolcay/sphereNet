using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a ship carries, how it turns, and what a plank and a pilot are.
///
/// Source-X measures the deck from shipZ + max(3, height) and carries what sits from
/// two below it to a player's height above, leaving ATTR_STATIC items where they are
/// (ListObjs, CCMultiMovable.cpp:142/200). The search radius reaches the farthest edge
/// from the anchor, not half the width (GetDistanceMax, CItemBase.cpp:1958). A turn
/// runs @Ship_Turn on every item it moved with the new and old facings (:628) and keeps
/// an open plank open (:614). A magic ship stops short of the world's ceiling and floor
/// (MoveDelta, :284). The pilot wears a ship-pilot item (:212), and redeeding a ship
/// runs @Redeed on the old multi (CItemMulti.cpp:1225).
/// </summary>
public sealed class ShipParity10ABTests
{
    private const ushort HullNorth = 0x4000;
    private const ushort PlankClosedNorth = 0x3EB1;
    private const ushort PlankOpenNorth = 0x3ED5;
    private const ushort PlankClosedEast = 0x3E8A;
    private const ushort PlankOpenEast = 0x3E89;

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>A hull whose definition is NOT centred on its anchor: the deck runs
    /// from the anchor out to +10 in X.</summary>
    private static MultiRegistry OffCentreRegistry()
    {
        var registry = new MultiRegistry();
        foreach (ushort id in new ushort[] { HullNorth, 0x4001, 0x4002, 0x4003 })
        {
            var def = new MultiDef { Id = id, Name = "long ship" };
            for (short x = 0; x <= 10; x++)
                for (short y = -2; y <= 2; y++)
                    def.Components.Add(new MultiComponent
                    { TileId = 0x3E40, DeltaX = x, DeltaY = y, DeltaZ = 0, Visible = true });
            def.RecalcBounds();
            registry.Register(def);
        }
        return registry;
    }

    private static MultiRegistry SquareRegistry(bool withPlank = false)
    {
        var registry = new MultiRegistry();
        foreach (ushort id in new ushort[] { HullNorth, 0x4001, 0x4002, 0x4003 })
        {
            var def = new MultiDef { Id = id, Name = "test ship" };
            for (short x = -2; x <= 2; x++)
                for (short y = -2; y <= 2; y++)
                    def.Components.Add(new MultiComponent
                    { TileId = 0x3E40, DeltaX = x, DeltaY = y, DeltaZ = 0, Visible = true });
            if (withPlank)
                def.Components.Add(new MultiComponent
                {
                    TileId = id == HullNorth ? PlankClosedNorth : PlankClosedEast,
                    DeltaX = -2, DeltaY = 0, DeltaZ = 0, Visible = false,
                });
            def.RecalcBounds();
            registry.Register(def);
        }
        return registry;
    }

    private static (ShipEngine Engine, Ship Ship, Character Owner) Bench(MultiRegistry registry)
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(50, 50, 0, 0));

        var engine = new ShipEngine(world, registry, null)
        {
            MaxShipsPerPlayer = -1,
            MaxShipsPerAccount = -1,
        };
        var ship = engine.PlaceShip(owner, HullNorth, new Point3D(100, 100, 0, 0), Direction.North)!;
        ship.Anchored = false;
        return (engine, ship, owner);
    }

    private static GameWorld WorldOf(Ship ship) =>
        SphereNet.Game.Objects.ObjBase.ResolveWorld!.Invoke()!;

    private static Item Cargo(Ship ship, short x, short y, sbyte z)
    {
        var world = WorldOf(ship);
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(x, y, z, 0));
        return item;
    }

    // --- 10A-1: a fixed item is not carried ------------------------------

    [Fact]
    public void AFixedItemStaysWhereItIs()
    {
        var (engine, ship, _) = Bench(SquareRegistry());
        var deco = Cargo(ship, 101, 100, 3);
        deco.SetAttr(ObjAttributes.Static);

        Assert.True(engine.MoveDelta(ship, 1, 0, 0));

        Assert.Equal(101, deco.X);
    }

    [Fact]
    public void AFixedItemDoesNotTurnEither()
    {
        var (engine, ship, _) = Bench(SquareRegistry());
        var deco = Cargo(ship, 101, 100, 3);
        deco.SetAttr(ObjAttributes.Static);

        Assert.True(engine.Face(ship, Direction.East));

        Assert.Equal(101, deco.X);
        Assert.Equal(100, deco.Y);
    }

    [Fact]
    public void AnImmovableItemIsStillCarried()
    {
        // MOVE_NEVER is not STATIC: the reference carries a locked-down crate.
        var (engine, ship, _) = Bench(SquareRegistry());
        var crate = Cargo(ship, 101, 100, 3);
        crate.SetAttr(ObjAttributes.Move_Never);

        Assert.True(engine.MoveDelta(ship, 1, 0, 0));

        Assert.Equal(102, crate.X);
    }

    // --- 10A-2: the deck plane decides who is aboard ---------------------

    [Theory]
    [InlineData(-2, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(19, true)]
    [InlineData(20, false)]
    public void OnlyWhatStandsOnTheDeckIsCarried(int z, bool carried)
    {
        var (engine, ship, _) = Bench(SquareRegistry());
        var crate = Cargo(ship, 101, 100, (sbyte)z);

        Assert.True(engine.MoveDelta(ship, 1, 0, 0));

        Assert.Equal(carried ? 102 : 101, crate.X);
    }

    // --- 10A-3: the far end of an off-centre hull ------------------------

    [Fact]
    public void CargoOnTheFarDeckOfAnOffCentreHullIsCarried()
    {
        var (engine, ship, _) = Bench(OffCentreRegistry());
        var crate = Cargo(ship, 110, 100, 3);

        Assert.True(engine.MoveDelta(ship, 1, 0, 0));

        Assert.Equal(111, crate.X);
    }

    // --- 10A-4: the turn trigger -----------------------------------------

    [Fact]
    public void ATurnTellsEveryItemItMoved()
    {
        var (engine, ship, _) = Bench(SquareRegistry(withPlank: true));
        var crate = Cargo(ship, 101, 100, 3);

        var told = new List<(Serial Uid, int New, int Old)>();
        engine.OnShipTurned = (item, newDir, oldDir) => told.Add((item.Uid, newDir, oldDir));

        Assert.True(engine.Face(ship, Direction.East));

        Assert.Contains(told, t => t.Uid == ship.MultiItem.Uid);
        Assert.Contains(told, t => t.Uid == ship.Components[0]);
        Assert.Contains(told, t => t.Uid == crate.Uid);
        Assert.All(told, t =>
        {
            Assert.Equal((int)Direction.East, t.New);
            Assert.Equal((int)Direction.North, t.Old);
        });
    }

    // --- 10A-5: a magic ship keeps clear of the world's edges ------------

    private static Ship MagicShip(out ShipEngine engine)
    {
        var (e, ship, _) = Bench(SquareRegistry());
        ship.MultiItem.SetAttr(ObjAttributes.Magic);
        engine = e;
        return ship;
    }

    [Theory]
    [InlineData("SHIPUP", (sbyte)100, false)]
    [InlineData("SHIPUP", (sbyte)80, true)]
    [InlineData("SHIPDOWN", (sbyte)-110, false)]
    [InlineData("SHIPDOWN", (sbyte)-80, true)]
    public void AMagicShipStopsShortOfTheCeilingAndTheFloor(string command, sbyte startZ, bool allowed)
    {
        var ship = MagicShip(out var engine);
        var world = WorldOf(ship);
        world.PlaceItem(ship.MultiItem,
            new Point3D(ship.MultiItem.X, ship.MultiItem.Y, startZ, ship.MultiItem.MapIndex));

        bool ok = engine.ExecuteCommand(ship, command, "");

        Assert.Equal(allowed, ok);
        Assert.Equal(allowed ? startZ + (command == "SHIPUP" ? 16 : -16) : startZ,
            ship.MultiItem.Z);
    }

    // --- 10B-3: an open plank stays open through a turn ------------------

    [Fact]
    public void AnOpenPlankStaysOpenWhenTheShipTurns()
    {
        var (engine, ship, _) = Bench(SquareRegistry(withPlank: true));
        var world = WorldOf(ship);

        var plank = world.FindItem(ship.Components[0])!;
        plank.ItemType = ItemType.ShipSideLocked;
        plank.BaseId = PlankClosedNorth;
        plank.More1 = 0;
        DefineOpenArt(PlankClosedNorth, PlankOpenNorth);
        DefineOpenArt(PlankClosedEast, PlankOpenEast);
        Assert.True(plank.OpenPlank());
        Assert.Equal(PlankOpenNorth, plank.BaseId);

        Assert.True(engine.Face(ship, Direction.East));

        Assert.Equal(ItemType.ShipPlank, plank.ItemType);
        Assert.Equal(PlankOpenEast, plank.BaseId);

        // And closing it now shows the EAST side, not the heading it used to have.
        Assert.True(plank.ClosePlank());
        Assert.Equal(PlankClosedEast, plank.BaseId);
    }

    private static void DefineOpenArt(ushort closedId, ushort openId)
    {
        var def = new SphereNet.Scripting.Definitions.ItemDef(
            new ResourceId(ResType.ItemDef, closedId)) { TData1 = openId };
        var table = (Dictionary<int, SphereNet.Scripting.Definitions.ItemDef>)
            typeof(SphereNet.Game.Definitions.DefinitionLoader)
                .GetField("_itemDefs", System.Reflection.BindingFlags.Static |
                                       System.Reflection.BindingFlags.NonPublic)!
                .GetValue(null)!;
        table[closedId] = def;
    }

    // --- 10B-1 / 10B-2: closing a plank, and stepping aboard -------------

    private sealed record PlankBench(ShipEngine Engine, Ship Ship, GameWorld World,
        SphereNet.Game.Clients.GameClient Client, Character Player, Item Plank);

    private static PlankBench PlankAt(short playerX, short playerY, sbyte playerZ,
        bool locked = false, bool hidden = false)
    {
        var (engine, ship, _) = Bench(SquareRegistry(withPlank: true));
        var world = WorldOf(ship);
        Item.ResolveShipEngine = () => engine;

        DefineOpenArt(PlankClosedNorth, PlankOpenNorth);
        DefineOpenArt(PlankClosedEast, PlankOpenEast);

        var plank = world.FindItem(ship.Components[0])!;
        plank.ItemType = locked ? ItemType.ShipSideLocked : ItemType.ShipSide;
        plank.BaseId = PlankClosedNorth;
        plank.More1 = 0;
        plank.Link = ship.MultiItem.Uid;
        Assert.True(plank.OpenPlank());

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world,
            new SphereNet.Game.Accounts.AccountManager(lf), 8801);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(playerX, playerY, playerZ, 0));
        if (hidden)
            player.SetStatFlag(StatFlag.Hidden);
        TestHarness.AttachCharacter(client, player);

        return new PlankBench(engine, ship, world, client, player, plank);
    }

    [Fact]
    public void APassengerClosingThePlankIsNotDraggedOntoIt()
    {
        // Standing anywhere ON THE SHIP closes it; the old code compared coordinates
        // and walked the passenger onto the plank instead.
        var bench = PlankAt(100, 100, 3);

        bench.Client.HandleDoubleClick(bench.Plank.Uid.Value);

        Assert.Equal(100, bench.Player.X);
        Assert.NotEqual(ItemType.ShipPlank, bench.Plank.ItemType);
    }

    [Fact]
    public void ClosingALockedPlankStillNeedsTheKey()
    {
        var bench = PlankAt(98, 100, 3, locked: true);

        bench.Client.HandleDoubleClick(bench.Plank.Uid.Value);

        Assert.Equal(ItemType.ShipPlank, bench.Plank.ItemType);   // still open
    }

    [Fact]
    public void SteppingAboardFromTheDockRevealsYou()
    {
        var bench = PlankAt(95, 100, 0, hidden: true);

        bench.Client.HandleDoubleClick(bench.Plank.Uid.Value);

        Assert.Equal(bench.Plank.X, bench.Player.X);
        Assert.False(bench.Player.IsStatFlag(StatFlag.Hidden));
    }

    // --- 10B-4: the pilot wears the wheel --------------------------------

    [Fact]
    public void TakingTheWheelIsWorn()
    {
        var (engine, ship, _) = Bench(SquareRegistry());
        var world = WorldOf(ship);
        var pilot = world.CreateCharacter();
        pilot.IsPlayer = true;
        world.PlaceCharacter(pilot, new Point3D(100, 100, 3, 0));

        Assert.True(engine.SetPilot(ship, pilot));

        var worn = pilot.GetEquippedItem(Layer.Horse);
        Assert.NotNull(worn);
        Assert.Equal(ship.MultiItem.Uid, worn!.Link);
    }

    [Fact]
    public void StandingDownTakesTheWheelBack()
    {
        var (engine, ship, _) = Bench(SquareRegistry());
        var world = WorldOf(ship);
        var pilot = world.CreateCharacter();
        pilot.IsPlayer = true;
        world.PlaceCharacter(pilot, new Point3D(100, 100, 3, 0));
        Assert.True(engine.SetPilot(ship, pilot));

        Assert.True(engine.SetPilot(ship, null));

        Assert.Null(pilot.GetEquippedItem(Layer.Horse));
    }

    // --- 10B-5: redeeding a ship runs @Redeed ---------------------------

    [Fact]
    public void RedeedingAShipTellsTheShip()
    {
        var (engine, ship, owner) = Bench(SquareRegistry());
        var pack = WorldOf(ship).CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);

        Serial toldAbout = Serial.Invalid;
        Item? toldDeed = null;
        int toldId = -1;
        engine.OnShipRedeed = (multi, deed, deedId) =>
        {
            toldAbout = multi.Uid;
            toldDeed = deed;
            toldId = deedId;
        };

        var made = engine.RemoveShip(ship.MultiItem.Uid, owner);

        Assert.NotNull(made);
        Assert.Equal(ship.MultiItem.Uid, toldAbout);
        Assert.Same(made, toldDeed);
        Assert.Equal(made!.BaseId, toldId);
    }
}
