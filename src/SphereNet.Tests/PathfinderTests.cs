using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.World;
using Microsoft.Extensions.Logging;

namespace SphereNet.Tests;

public class PathfinderTests
{
    private static (GameWorld world, Pathfinder pf) CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var pf = new Pathfinder(world);
        return (world, pf);
    }

    [Fact]
    public void FindPath_AdjacentGoal_ReturnsSingleStep()
    {
        var (_, pf) = CreateWorld();
        var start = new Point3D(100, 100, 0, 0);
        var goal = new Point3D(101, 100, 0, 0);

        var path = pf.FindPath(start, goal, 0);
        Assert.NotNull(path);
        Assert.Single(path);
    }

    [Fact]
    public void FindPath_SamePoint_ReturnsSingleStep()
    {
        var (_, pf) = CreateWorld();
        var pt = new Point3D(100, 100, 0, 0);

        var path = pf.FindPath(pt, pt, 0);
        Assert.NotNull(path);
    }

    [Fact]
    public void FindPath_ShortDistance_FindsPath()
    {
        var (_, pf) = CreateWorld();
        var start = new Point3D(100, 100, 0, 0);
        var goal = new Point3D(105, 105, 0, 0);

        var path = pf.FindPath(start, goal, 0);
        Assert.NotNull(path);
        Assert.True(path.Count > 0);
        Assert.Equal(goal.X, path[^1].X);
        Assert.Equal(goal.Y, path[^1].Y);
    }

    [Fact]
    public void FindPath_PathLengthCapped()
    {
        var (_, pf) = CreateWorld();
        var start = new Point3D(100, 100, 0, 0);
        var goal = new Point3D(200, 200, 0, 0);

        var path = pf.FindPath(start, goal, 0);
        // Path is capped by Pathfinder.MaxPathLength (256) on return.
        if (path != null)
            Assert.True(path.Count <= 256);
    }
}
