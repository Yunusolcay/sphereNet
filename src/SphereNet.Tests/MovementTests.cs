using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Microsoft.Extensions.Logging;

namespace SphereNet.Tests;

public class MovementTests
{
    private static (GameWorld world, MovementEngine engine) CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var engine = new MovementEngine(world);
        return (world, engine);
    }

    [Fact]
    public void TryMove_NormalMove_Succeeds()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 50; ch.MaxStam = 50; ch.MaxMana = 50;
        ch.Hits = 50; ch.Stam = 50; ch.Mana = 50;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        bool moved = engine.TryMove(ch, Direction.North, false, 1);
        Assert.True(moved);
        Assert.Equal(999, ch.Y);
    }

    [Fact]
    public void TryMove_DeadChar_Fails()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.MaxStam = 50; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        ch.Kill();

        bool moved = engine.TryMove(ch, Direction.South, false, 1);
        Assert.False(moved);
    }

    [Fact]
    public void TryMove_Frozen_Fails()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.MaxStam = 50; ch.Stam = 50;
        ch.SetStatFlag(StatFlag.Freeze);
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        bool moved = engine.TryMove(ch, Direction.East, false, 1);
        Assert.False(moved);
    }

    [Fact]
    public void TryMove_AllDirections_UpdatesPosition()
    {
        var (world, engine) = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.MaxHits = 50; ch.MaxStam = 50;
        ch.Hits = 50; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        engine.TryMove(ch, Direction.East, false, 1);
        Assert.Equal(1001, ch.X);

        engine.TryMove(ch, Direction.South, false, 2);
        Assert.Equal(1001, ch.Y);

        engine.TryMove(ch, Direction.West, false, 3);
        Assert.Equal(1000, ch.X);

        engine.TryMove(ch, Direction.North, false, 4);
        Assert.Equal(1000, ch.Y);
    }

    [Fact]
    public void GetMoveDelay_Running_FasterThanWalking()
    {
        int walkFoot = MovementEngine.GetMoveDelay(false, false);
        int runFoot = MovementEngine.GetMoveDelay(false, true);
        int walkMount = MovementEngine.GetMoveDelay(true, false);
        int runMount = MovementEngine.GetMoveDelay(true, true);

        Assert.True(runFoot < walkFoot);
        Assert.True(runMount < walkMount);
        Assert.True(walkMount < walkFoot);
    }
}
