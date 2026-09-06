using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The moment an NPC actually commits a step.
///
/// Source-X puts three conditions there that SphereNet applied to none of them. A
/// diagonal step needs both of the orthogonal tiles beside it, tested from the tile
/// being left (CheckValidMove, CCharStatus.cpp:1988), so a creature cannot cut the
/// corner where two walls meet - and the reference deliberately skips that test while
/// pathfinding, which is why the search was never the place for it. A stored route
/// whose next point is no longer one tile away is thrown away rather than applied
/// (NPC_WalkToPoint, CCharNPCAct.cpp:463). And the height comes from the surface
/// resolved at the step (CheckValidMove, :1972), not from the search's cheap guess -
/// Pathfinder's own comment says its Z is approximate.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcStepParity07Tests
{
    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    /// <summary>A synthetic Surface tile, the shape the standing-surface tests use
    /// for an addon floor.</summary>
    private const ushort FloorTile = 0x0500;

    private static (GameWorld World, NpcAI Ai) Setup(NpcAIFlags flags, bool withMap = false)
    {
        GameWorld world;
        if (withMap)
        {
            var map = new MapDataManager("");
            map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
            map.SetSyntheticItemTile(FloorTile, new ItemTileData { Flags = TileFlag.Surface });
            world = new GameWorld(LoggerFactory.Create(_ => { }));
            world.InitMap(0, 256, 256);
            world.MapData = map;
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;
        }
        else
        {
            world = TestHarness.CreateWorld();
        }

        var ai = new NpcAI(world, new SphereConfig()) { Flags = flags };
        return (world, ai);
    }

    private static Character Npc(GameWorld world, Point3D at)
    {
        var npc = world.CreateCharacter();
        npc.Str = 100; npc.MaxHits = 100; npc.Hits = 100;
        npc.Int = 300;                      // smart enough for the A* gate
        world.PlaceCharacter(npc, at);
        return npc;
    }

    private static Item Wall(GameWorld world, short x, short y, sbyte z = 0)
    {
        var wall = world.CreateItem();
        wall.ItemType = ItemType.Wall;
        world.PlaceItem(wall, new Point3D(x, y, z, 0));
        return wall;
    }

    // --- SX-07E-01: a diagonal step may not cut a corner --------------------

    /// <summary>Walls beside the diagonal, the diagonal tile itself left free.</summary>
    private static Character CornerWalk(GameWorld world, NpcAI ai, bool east, bool south)
    {
        var npc = Npc(world, new Point3D(100, 100, 0, 0));
        if (east) Wall(world, 101, 100);
        if (south) Wall(world, 100, 101);
        Invoke(ai, "MoveToward", npc, new Point3D(104, 104, 0, 0), false);
        return npc;
    }

    [Fact]
    public void AnOpenCornerIsStillWalkedDiagonally()
    {
        var (world, ai) = Setup(NpcAIFlags.None);
        var npc = CornerWalk(world, ai, east: false, south: false);

        Assert.Equal(new Point3D(101, 101, 0, 0), npc.Position);
    }

    [Theory]
    [InlineData(true, false)]   // a wall on one side
    [InlineData(false, true)]   // on the other
    [InlineData(true, true)]    // both
    public void AWallBesideTheDiagonalBlocksIt(bool east, bool south)
    {
        var (world, ai) = Setup(NpcAIFlags.None);
        var npc = CornerWalk(world, ai, east, south);

        Assert.NotEqual(new Point3D(101, 101, 0, 0), npc.Position);
    }

    [Fact]
    public void AWallOnTheDiagonalItselfStillBlocksIt()
    {
        // The control that bounds the fix: the destination check was never missing.
        var (world, ai) = Setup(NpcAIFlags.None);
        var npc = Npc(world, new Point3D(100, 100, 0, 0));
        Wall(world, 101, 101);

        Invoke(ai, "MoveToward", npc, new Point3D(104, 104, 0, 0), false);

        Assert.NotEqual(new Point3D(101, 101, 0, 0), npc.Position);
    }

    [Fact]
    public void AStraightStepIgnoresItsNeighbours()
    {
        // Only a diagonal has a corner; walls beside a straight step are irrelevant.
        var (world, ai) = Setup(NpcAIFlags.None);
        var npc = Npc(world, new Point3D(100, 100, 0, 0));
        Wall(world, 101, 99);
        Wall(world, 101, 101);

        Invoke(ai, "MoveToward", npc, new Point3D(108, 100, 0, 0), false);

        Assert.Equal(new Point3D(101, 100, 0, 0), npc.Position);
    }

    // --- SX-07F-01: a stored route is not a teleport ------------------------

    private const NpcAIFlags PathFlags =
        NpcAIFlags.Path | NpcAIFlags.AlwaysInt | NpcAIFlags.PersistentPath;

    /// <summary>Walk one A* step towards a goal blocked head-on, leaving the rest of
    /// the route cached the way the engine does.</summary>
    private static (GameWorld World, NpcAI Ai, Character Npc, Point3D Goal) Routed()
    {
        var (world, ai) = Setup(PathFlags);
        var npc = Npc(world, new Point3D(100, 100, 0, 0));
        Npc(world, new Point3D(101, 100, 0, 0));        // blocks the direct step
        var goal = new Point3D(108, 100, 0, 0);

        Invoke(ai, "MoveToward", npc, goal, false);
        Assert.NotEqual(new Point3D(100, 100, 0, 0), npc.Position);   // a route was taken
        return (world, ai, npc, goal);
    }

    [Fact]
    public void TheCachedRouteIsFollowedOneTileAtATime()
    {
        var (_, ai, npc, goal) = Routed();
        var before = npc.Position;

        Invoke(ai, "MoveToward", npc, goal, false);

        Assert.Equal(1, before.GetDistanceTo(npc.Position));
    }

    [Fact]
    public void AnNpcMovedAwayDoesNotSnapBackOntoItsOldRoute()
    {
        var (world, ai, npc, goal) = Routed();
        Npc(world, new Point3D(101, 104, 0, 0));        // block the new direct step too
        npc.MoveTo(new Point3D(100, 105, 0, 0));
        var before = npc.Position;

        Invoke(ai, "MoveToward", npc, goal, false);

        Assert.True(before.GetDistanceTo(npc.Position) <= 1,
            $"the NPC jumped from {before} to {npc.Position}");
    }

    [Fact]
    public void AnNpcMovedToAnotherMapDoesNotUseTheOldRoute()
    {
        var (world, ai, npc, goal) = Routed();
        world.InitMap(1, 6144, 4096);
        world.MoveCharacter(npc, new Point3D(100, 105, 0, 1));
        var before = npc.Position;

        Invoke(ai, "MoveToward", npc, goal, false);

        Assert.Equal(before, npc.Position);      // the goal is on another map entirely
    }

    // --- SX-07G-01: the height comes from the surface, not the search -------

    /// <summary>A raised platform of surface tiles, the shape the standing-surface
    /// tests already use for an addon floor.</summary>
    private static void Platform(GameWorld world, sbyte z)
    {
        for (short x = 98; x <= 110; x++)
            for (short y = 97; y <= 103; y++)
            {
                var tile = world.CreateItem();
                tile.BaseId = FloorTile;
                tile.SetAttr(ObjAttributes.Move_Never);   // anchored → movement geometry
                world.PlaceItem(tile, new Point3D(x, y, z, 0));
            }
    }

    [Fact]
    public void ARoutedStepStaysOnThePlatformItStartedOn()
    {
        var (world, ai) = Setup(PathFlags, withMap: true);
        Platform(world, 7);
        var npc = Npc(world, new Point3D(100, 100, 7, 0));
        Npc(world, new Point3D(101, 100, 7, 0));        // blocks the direct step

        Invoke(ai, "MoveToward", npc, new Point3D(108, 100, 7, 0), false);

        var resolved = world.Standing.ResolveStandingSurface(
            npc, 0, npc.X, npc.Y, 7, SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
        Assert.True(resolved.Found);
        Assert.Equal(resolved.Z, npc.Z);
    }

    [Fact]
    public void AnUnroutedStepOnThePlatformIsUnchanged()
    {
        // The control: the direct step already resolved its surface correctly.
        var (world, ai) = Setup(PathFlags, withMap: true);
        Platform(world, 7);
        var npc = Npc(world, new Point3D(100, 100, 7, 0));

        Invoke(ai, "MoveToward", npc, new Point3D(108, 100, 7, 0), false);

        Assert.Equal(101, npc.X);
        Assert.Equal((sbyte)7, npc.Z);
    }

    [Fact]
    public void ARoutedStepOnTerrainIsUnchanged()
    {
        // ...and routing on flat ground was never wrong, so the fix must not move it.
        var (world, ai) = Setup(PathFlags);
        var npc = Npc(world, new Point3D(100, 100, 0, 0));
        Npc(world, new Point3D(101, 100, 0, 0));

        Invoke(ai, "MoveToward", npc, new Point3D(108, 100, 0, 0), false);

        Assert.Equal((sbyte)0, npc.Z);
    }
}
