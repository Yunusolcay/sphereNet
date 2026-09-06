using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What actually stands in an NPC's way, and whether it may walk at all.
///
/// Three of these are one shape: a collision test that compares X and Y and ignores Z.
/// Source-X skips a character more than five Z from the step's destination
/// (ShoveCharAtPosition, CCharAct.cpp:4622) and hands an item's own Z and height to
/// CheckTile_Item, which tells a floor below from a ceiling above (CServerMap.cpp:178).
/// SphereNet blocked the whole vertical column - a creature on the storey above, or a
/// wall on it, closed the ground underneath - and decided from an item's TYPE alone, so
/// an impassable object that was not a Wall or Door was walked straight into and a
/// ceiling too low to fit under was walked beneath.
///
/// The fourth is the permission to move at all: Source-X asks CanMove at the real step
/// (CCharAct.cpp:4716/4571), where a frozen or stoned creature is refused
/// (OnFreezeCheck, :4525) and a living one out of stamina with it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcStepParity07HKTests
{
    private const ushort FloorTile = 0x0500;    // synthetic Surface, h=0
    private const ushort BlockTile = 0x0510;    // synthetic Impassable, h=20
    private const ushort SlabTile = 0x0511;     // synthetic Impassable, h=2 (a ceiling)

    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private static (GameWorld World, NpcAI Ai) Setup()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(FloorTile, new ItemTileData { Flags = TileFlag.Surface });
        map.SetSyntheticItemTile(BlockTile, new ItemTileData
        { Flags = TileFlag.Impassable, Height = 20 });
        map.SetSyntheticItemTile(SlabTile, new ItemTileData
        { Flags = TileFlag.Impassable, Height = 2 });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ai = new NpcAI(world, new SphereConfig()) { Flags = NpcAIFlags.None };
        return (world, ai);
    }

    private static Character Npc(GameWorld world, Point3D at, bool player = false)
    {
        var npc = world.CreateCharacter();
        npc.IsPlayer = player;
        npc.Str = 100; npc.MaxHits = 100; npc.Hits = 100;
        npc.Dex = 100; npc.Stam = 100;
        npc.Int = 300;
        world.PlaceCharacter(npc, at);
        return npc;
    }

    private static Item Anchored(GameWorld world, ushort gfx, Point3D at,
        ItemType type = ItemType.Normal)
    {
        var item = world.CreateItem();
        item.BaseId = gfx;
        item.ItemType = type;
        item.SetAttr(ObjAttributes.Move_Never);
        world.PlaceItem(item, at);
        return item;
    }

    /// <summary>An upper storey over the tile the NPC is walking towards, so anything
    /// standing there has a real floor beneath it.</summary>
    private static void UpperFloor(GameWorld world, sbyte z) =>
        Anchored(world, FloorTile, new Point3D(101, 100, z, 0));

    private static Character Walk(GameWorld world, NpcAI ai)
    {
        var npc = Npc(world, new Point3D(100, 100, 0, 0));
        Invoke(ai, "MoveToward", npc, new Point3D(108, 100, 0, 0), false);
        return npc;
    }

    /// <summary>The tile the NPC is walking towards. Blocking it does not have to
    /// leave the NPC standing still - it may well step around - so the claim under
    /// test is that it never ENTERS the blocked tile.</summary>
    private static readonly Point3D Blocked = new(101, 100, 0, 0);

    private static void AssertRefused(Character npc) =>
        Assert.True(npc.X != Blocked.X || npc.Y != Blocked.Y,
            $"the NPC walked into the blocked tile: {npc.Position}");

    private static void AssertEntered(Character npc) =>
        Assert.True(npc.X == Blocked.X && npc.Y == Blocked.Y,
            $"the NPC did not take the step: {npc.Position}");

    // --- SX-07H-01: a character on another storey is not in the way ---------

    [Fact]
    public void AnEmptyUpperFloorDoesNotBlockTheGround()
    {
        var (world, ai) = Setup();
        UpperFloor(world, 20);

        AssertEntered(Walk(world, ai));
    }

    [Fact]
    public void SomeoneStandingOnTheSameTileStillBlocks()
    {
        var (world, ai) = Setup();
        Npc(world, new Point3D(101, 100, 0, 0));

        AssertRefused(Walk(world, ai));
    }

    [Theory]
    [InlineData((sbyte)20)]
    [InlineData((sbyte)40)]
    public void SomeoneStandingOnTheStoreyAboveDoesNot(sbyte z)
    {
        var (world, ai) = Setup();
        UpperFloor(world, z);
        Npc(world, new Point3D(101, 100, z, 0));

        AssertEntered(Walk(world, ai));
    }

    [Fact]
    public void SomeoneJustAStepAboveStillBlocks()
    {
        // Five Z is the reference's reach; a creature within it shares the space.
        var (world, ai) = Setup();
        Npc(world, new Point3D(101, 100, 4, 0));

        AssertRefused(Walk(world, ai));
    }

    // --- SX-07I-01: a wall on another storey is not in the way --------------

    [Fact]
    public void AWallOnTheGroundBlocks()
    {
        var (world, ai) = Setup();
        Anchored(world, BlockTile, new Point3D(101, 100, 0, 0), ItemType.Wall);

        AssertRefused(Walk(world, ai));
    }

    [Theory]
    [InlineData(ItemType.Wall)]
    [InlineData(ItemType.Door)]
    public void TheSameOnTheStoreyAboveDoesNot(ItemType type)
    {
        var (world, ai) = Setup();
        UpperFloor(world, 40);
        Anchored(world, BlockTile, new Point3D(101, 100, 40, 0), type);

        AssertEntered(Walk(world, ai));
    }

    // --- SX-07J-01: the step goes through the real walk geometry ------------

    [Fact]
    public void AnImpassableObjectBlocksWhateverTypeItCarries()
    {
        // The old test was the item's TYPE; this one is a plain Normal item whose
        // tiledata is impassable, which used to be walked straight into.
        var (world, ai) = Setup();
        Anchored(world, BlockTile, new Point3D(101, 100, 0, 0));

        AssertRefused(Walk(world, ai));
    }

    [Fact]
    public void ACeilingTooLowToFitUnderBlocks()
    {
        var (world, ai) = Setup();
        Anchored(world, SlabTile, new Point3D(101, 100, 8, 0));

        AssertRefused(Walk(world, ai));
    }

    [Fact]
    public void ACeilingHighEnoughToWalkUnderDoesNot()
    {
        var (world, ai) = Setup();
        Anchored(world, SlabTile, new Point3D(101, 100, 40, 0));

        AssertEntered(Walk(world, ai));
    }

    // --- SX-07K-01: the NPC has to be able to walk at all -------------------

    private static Character Restrained(GameWorld world, NpcAI ai, Action<Character> restrain)
    {
        var npc = Npc(world, new Point3D(100, 100, 0, 0));
        restrain(npc);
        Invoke(ai, "MoveToward", npc, new Point3D(108, 100, 0, 0), false);
        return npc;
    }

    [Fact]
    public void AnUnrestrainedNpcWalks()
    {
        var (world, ai) = Setup();
        Assert.Equal(101, Restrained(world, ai, _ => { }).X);
    }

    [Theory]
    [InlineData(StatFlag.Freeze)]
    [InlineData(StatFlag.Stone)]
    public void ARestrainedNpcDoesNot(StatFlag flag)
    {
        var (world, ai) = Setup();
        Assert.Equal(100, Restrained(world, ai, n => n.SetStatFlag(flag)).X);
    }

    [Fact]
    public void AnExhaustedNpcDoesNot()
    {
        var (world, ai) = Setup();
        Assert.Equal(100, Restrained(world, ai, n => n.Stam = 0).X);
    }

    [Fact]
    public void ACreatureWithNoStaminaPoolIsNotTreatedAsExhausted()
    {
        // No MaxStam means no stamina model at all - the Dex setter raises the ceiling
        // but never fills the pool - and an empty pool there is not exhaustion.
        var (world, ai) = Setup();
        var npc = world.CreateCharacter();
        npc.Str = 100; npc.MaxHits = 100; npc.Hits = 100; npc.Int = 300;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        Assert.Equal(0, npc.MaxStam);

        Invoke(ai, "MoveToward", npc, new Point3D(108, 100, 0, 0), false);

        Assert.Equal(101, npc.X);
    }

    [Fact]
    public void StaffAreExemptFromTheRestraints()
    {
        var (world, ai) = Setup();
        var npc = Restrained(world, ai, n =>
        {
            n.PrivLevel = PrivLevel.GM;
            n.SetStatFlag(StatFlag.Freeze);
            n.Stam = 0;
        });

        Assert.Equal(101, npc.X);
    }

    [Fact]
    public void AFreezeThatLandsAfterTheDecisionStillStopsTheWalk()
    {
        // The restraint is read where the step is applied, not where it was decided.
        var (world, ai) = Setup();
        var npc = Npc(world, new Point3D(100, 100, 0, 0));
        var decision = ai.BuildDecision(npc, Environment.TickCount64);
        Assert.NotNull(decision);

        npc.SetStatFlag(StatFlag.Freeze);
        ai.ApplyDecision(decision!.Value);

        Assert.Equal(100, npc.X);
    }
}
