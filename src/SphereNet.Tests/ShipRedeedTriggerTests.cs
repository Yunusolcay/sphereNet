using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the wired item triggers @ShipMove / @ShipStop / @ShipTurn (ShipEngine
// hooks fired by Move/Stop/Face) and @Redeed (House.OnRedeed fired when a house
// converts to a deed). Each runs through an engine hook driven directly here, the
// same way Program.EngineWiring routes the hook into FireItemTrigger; House.OnRedeed
// is nulled between tests by ResetEngineStatics.
public class ShipRedeedTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (ShipEngine engine, Ship ship) MakeShip(GameWorld world)
    {
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
        var registry = new MultiRegistry();
        foreach (ushort id in new ushort[] { 0x4000, 0x4001 })
        {
            var def = new MultiDef { Id = id, Name = "test ship" };
            def.Components.Add(new MultiComponent
            {
                TileId = 0x3E40,
                DeltaX = 0,
                DeltaY = 0,
                DeltaZ = 0,
                Visible = true,
            });
            def.RecalcBounds();
            registry.Register(def);
        }
        var engine = new ShipEngine(world, registry, null);
        return (engine, new Ship(multi));
    }

    [Fact]
    public void ShipMove_MoveDelta_FiresShipMovedHook()
    {
        var world = CreateWorld();
        var (engine, ship) = MakeShip(world);
        int moves = 0;
        engine.OnShipMoved = _ => moves++;

        engine.MoveDelta(ship, 1, 0, 0);

        Assert.Equal(1, moves);
    }

    [Fact]
    public void ShipStop_FromMoving_FiresShipStoppedHook()
    {
        var world = CreateWorld();
        var (engine, ship) = MakeShip(world);
        ship.MovementType = ShipMovementType.Normal; // moving
        int stops = 0;
        engine.OnShipStopped = _ => stops++;

        engine.Stop(ship);

        Assert.Equal(1, stops);
        Assert.Equal(ShipMovementType.Stop, ship.MovementType);

        engine.Stop(ship);          // already stopped → no second fire
        Assert.Equal(1, stops);
    }

    [Fact]
    public void ShipTurn_NewFacing_FiresShipTurnedHook()
    {
        var world = CreateWorld();
        var (engine, ship) = MakeShip(world);
        ship.DirFace = Direction.North;
        // The hook now reports every item that turned, with the new facing and the old
        // one (Source-X Face, CCMultiMovable.cpp:628), so the hull's own call is the
        // one this test counts.
        int hullTurns = 0;
        int lastNew = -1, lastOld = -1;
        engine.OnShipTurned = (item, newDir, oldDir) =>
        {
            if (item != ship.MultiItem) return;
            hullTurns++;
            lastNew = newDir;
            lastOld = oldDir;
        };

        Assert.True(engine.Face(ship, Direction.East));
        Assert.Equal(1, hullTurns);
        Assert.Equal((int)Direction.East, lastNew);
        Assert.Equal((int)Direction.North, lastOld);

        Assert.True(engine.Face(ship, Direction.East)); // same facing → no rotation
        Assert.Equal(1, hullTurns);
    }

    [Fact]
    public void Redeed_HouseToDeed_FiresRedeedWithDeed()
    {
        var world = CreateWorld();
        var multi = world.CreateItem();
        multi.Name = "small house";
        world.PlaceItem(multi, new Point3D(100, 100, 0, 0));
        var house = new House(multi);

        Item? redeededDeed = null;
        House.OnRedeed = d => redeededDeed = d;

        var deed = house.Redeed(world);

        Assert.NotNull(deed);
        Assert.Same(deed, redeededDeed);
    }
}
