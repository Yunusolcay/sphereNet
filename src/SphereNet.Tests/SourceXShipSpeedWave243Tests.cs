using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 243 — Source-X CCMultiMovable::SetNextMove (CCMultiMovable.cpp:119/124):
/// one-tile steering (SMT_SLOW) runs at the full period, while continuous
/// ("normal") sailing runs in the fast speed mode and halves the tick interval,
/// so a ship sailing forward advances twice as fast as click-by-click steering.
/// </summary>
public sealed class SourceXShipSpeedWave243Tests
{
    private static (ShipEngine engine, Ship ship) MakeShip()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        world.PlaceItem(multi, new Point3D(200, 200, 0, 0));
        var engine = new ShipEngine(world, new MultiRegistry(), null);
        var ship = new Ship(multi) { SpeedPeriod = 1000 };
        return (engine, ship);
    }

    [Fact]
    public void SetMoveDir_ContinuousSailing_HalvesTheInterval()
    {
        var (engine, ship) = MakeShip();

        long before = Environment.TickCount64;
        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.Normal,
            wheelMove: true));

        Assert.Equal(ShipSpeedMode.Fast, ship.SpeedMode);
        long delay = ship.NextMoveTick - before;
        Assert.InRange(delay, 500, 700); // ~SpeedPeriod / 2 (+ scheduling slack)
    }

    [Fact]
    public void SetMoveDir_OneTileSteering_UsesFullPeriod()
    {
        var (engine, ship) = MakeShip();

        long before = Environment.TickCount64;
        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.OneTile,
            wheelMove: true));

        Assert.Equal(ShipSpeedMode.Slow, ship.SpeedMode);
        long delay = ship.NextMoveTick - before;
        Assert.InRange(delay, 1000, 1200); // full SpeedPeriod (+ scheduling slack)
    }

    /// <summary>Only a wheel order re-selects the speed mode (Source-X guards
    /// that assignment with fWheelMove). A ship that has never been steered from
    /// the wheel therefore keeps the constructor's mode, which upstream sets to
    /// SMS_NORMAL — not the SLOW mode — so a tillerman/script command sails at
    /// the halved interval, not the full period.</summary>
    [Fact]
    public void ScriptedCommand_KeepsTheDefaultMode_AndSailsAtTheHalvedInterval()
    {
        var (engine, ship) = MakeShip();
        Assert.Equal(ShipSpeedMode.OneTile, ship.SpeedMode); // Source-X SMS_NORMAL

        long before = Environment.TickCount64;
        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.Normal));

        Assert.Equal(ShipSpeedMode.OneTile, ship.SpeedMode); // untouched by a script order
        long delay = ship.NextMoveTick - before;
        Assert.InRange(delay, 500, 700);
    }

    /// <summary>Source-X CCMultiMovable.cpp:78 drops a wheel order that arrives
    /// while the ship is still counting down to its next step — "otherwise for
    /// each click with mouse it will do 1 move". Without it every extra click
    /// pushes the next step a full period further out, so holding the wheel
    /// stalls the ship instead of sailing it.</summary>
    [Fact]
    public void WheelOrder_DuringTheCountdown_IsDroppedInsteadOfDelayingTheShip()
    {
        var (engine, ship) = MakeShip();

        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.Normal,
            wheelMove: true));
        long firstStep = ship.NextMoveTick;

        // A second click a moment later must not move the goalposts.
        Assert.False(engine.SetMoveDir(ship, Direction.North, ShipMovementType.Normal,
            wheelMove: true));
        Assert.Equal(firstStep, ship.NextMoveTick);

        // A scripted order is not wheel-rate-limited (Source-X only guards the
        // wheel path), so it still re-arms.
        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.Normal));
    }

    /// <summary>Source-X CCMultiMovable.cpp:81 — repeating the order while the
    /// ship still steps one tile at a time and faces that way promotes it to
    /// continuous sailing.</summary>
    [Fact]
    public void RepeatingAOneTileOrder_WhileFacingIt_PromotesToContinuousSailing()
    {
        var (engine, ship) = MakeShip();
        ship.DirFace = Direction.North;

        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.OneTile));
        Assert.Equal(ShipMovementType.OneTile, ship.MovementType);

        Assert.True(engine.SetMoveDir(ship, Direction.North, ShipMovementType.OneTile));
        Assert.Equal(ShipMovementType.Normal, ship.MovementType);
    }

    /// <summary>A repeat in a direction the ship is NOT facing stays one-tile.</summary>
    [Fact]
    public void RepeatingAOneTileOrder_FacingElsewhere_StaysOneTile()
    {
        var (engine, ship) = MakeShip();
        ship.DirFace = Direction.North;

        Assert.True(engine.SetMoveDir(ship, Direction.East, ShipMovementType.OneTile));
        Assert.True(engine.SetMoveDir(ship, Direction.East, ShipMovementType.OneTile));

        Assert.Equal(ShipMovementType.OneTile, ship.MovementType);
    }
}
