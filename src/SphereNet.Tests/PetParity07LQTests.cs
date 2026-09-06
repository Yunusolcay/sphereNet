using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Movement;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Pets: where they may walk, who they may follow, and what their orders leave behind.
///
/// Source-X resolves the SURFACE a step lands on and only then asks for the ability
/// that surface needs - swimming for water, walking for a platform
/// (CCharStatus.cpp:1812/1858) - so a dry jetty over water is walkable. It takes a
/// follow point only from a target it can SEE (NPC_Act_Follow, CCharNPCAct.cpp:1386),
/// gives that trigger three outcomes and mutable arguments (:1357), keeps stepping
/// towards a GO target while a direction remains (NPC_WalkToPoint, :437), and scores a
/// candidate weapon through the strength requirement even for an NPC
/// (CCharNPCStatus.cpp:688 -> CCharStatus.cpp:297).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PetParity07LQTests
{
    private const ushort DeckTile = 0x0520;     // synthetic Surface, h=0
    private const ushort WaterLand = 0x00A9;    // synthetic Wet land
    private const ushort SwordTile = 0x0530;    // synthetic Wearable, one-handed slot

    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private static (GameWorld World, NpcAI Ai, MapDataManager Map) Setup(bool water = false)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: water ? WaterLand : (ushort)3);
        map.SetSyntheticItemTile(DeckTile, new ItemTileData { Flags = TileFlag.Surface });
        map.SetSyntheticItemTile(SwordTile, new ItemTileData
        { Flags = TileFlag.Wearable, Quality = (byte)Layer.OneHanded });
        map.SetSyntheticLandTile(WaterLand, new LandTileData
        { Flags = TileFlag.Impassable | TileFlag.Wet, Name = "water" });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ai = new NpcAI(world, new SphereConfig()) { Flags = NpcAIFlags.None };
        return (world, ai, map);
    }

    private static Character Being(GameWorld world, Point3D at, bool player = false)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;
        ch.Dex = 100; ch.Stam = 100; ch.Int = 100;
        world.PlaceCharacter(ch, at);
        return ch;
    }

    private static void Platform(GameWorld world, sbyte z)
    {
        for (short x = 98; x <= 110; x++)
            for (short y = 98; y <= 102; y++)
            {
                var tile = world.CreateItem();
                tile.BaseId = DeckTile;
                tile.SetAttr(ObjAttributes.Move_Never);
                world.PlaceItem(tile, new Point3D(x, y, z, 0));
            }
    }

    // --- SX-07L-01: a dry deck over water is dry ----------------------------

    private static Character WalkOnDeck(bool water)
    {
        var (world, ai, _) = Setup(water);
        Platform(world, 3);
        var pet = Being(world, new Point3D(100, 100, 3, 0));
        Invoke(ai, "MoveToward", pet, new Point3D(108, 100, 3, 0), false);
        return pet;
    }

    [Fact]
    public void APlatformOverDryGroundIsWalked()
    {
        Assert.Equal(101, WalkOnDeck(water: false).X);
    }

    [Fact]
    public void APlatformOverWaterIsWalkedToo()
    {
        // The creature stands on the deck, not in the sea; asking it to swim kept a
        // landlocked NPC off its own pier.
        Assert.Equal(101, WalkOnDeck(water: true).X);
    }

    [Fact]
    public void OpenWaterStillNeedsSwimming()
    {
        var (world, ai, _) = Setup(water: true);
        var pet = Being(world, new Point3D(100, 100, 0, 0));

        Invoke(ai, "MoveToward", pet, new Point3D(108, 100, 0, 0), false);

        Assert.Equal(100, pet.X);
    }

    [Fact]
    public void ThePathfinderAgreesAboutTheDeck()
    {
        // The search has to answer the same way, or a step it plans is refused.
        var (world, _, map) = Setup(water: true);
        Platform(world, 3);

        Assert.False(NpcAI.StandsOnWater(map, new Point3D(101, 100, 3, 0)));
        Assert.True(NpcAI.StandsOnWater(map, new Point3D(101, 100, 0, 0)));
    }

    // --- SX-07M-01: a follower tracks what it can see -----------------------

    private static (GameWorld World, NpcAI Ai, Character Pet, Character Owner) FollowBench()
    {
        var (world, ai, _) = Setup();
        var owner = Being(world, new Point3D(108, 100, 0, 0), player: true);
        var pet = Being(world, new Point3D(100, 100, 0, 0));
        pet.TryAssignOwnership(owner, owner);
        pet.PetAIMode = PetAIMode.Follow;
        return (world, ai, pet, owner);
    }

    private static void Tick(NpcAI ai, Character pet)
    {
        pet.NextNpcActionTime = 0;
        ai.OnTickAction(pet);
    }

    [Fact]
    public void AVisibleOwnerIsFollowedWhereverTheyGo()
    {
        var (_, ai, pet, owner) = FollowBench();
        Tick(ai, pet);
        Assert.Equal(101, pet.X);

        owner.MoveTo(new Point3D(100, 108, 0, 0));
        Tick(ai, pet);

        Assert.Equal(101, pet.Y);      // turned south after them
    }

    [Theory]
    [InlineData(StatFlag.Hidden)]
    [InlineData(StatFlag.Invisible)]
    public void AConcealedOwnerIsNotTrackedToTheirNewPlace(StatFlag flag)
    {
        var (_, ai, pet, owner) = FollowBench();
        Tick(ai, pet);
        var afterFirstStep = pet.Position;

        owner.SetStatFlag(flag);
        owner.MoveTo(new Point3D(100, 108, 0, 0));
        Tick(ai, pet);

        Assert.Equal(afterFirstStep.Y, pet.Y);      // never turned south
    }

    [Fact]
    public void AConcealedOwnerLeavesTheirLastKnownPlaceBehind()
    {
        // Not "stand still": the pet walks on to where they were last seen.
        var (_, ai, pet, owner) = FollowBench();
        Tick(ai, pet);

        owner.SetStatFlag(StatFlag.Hidden);
        owner.MoveTo(new Point3D(100, 108, 0, 0));
        int before = pet.X;
        Tick(ai, pet);

        Assert.True(pet.X > before, "the pet did not continue to the last seen spot");
    }

    [Fact]
    public void AnOwnerWhoRevealsIsFollowedAgain()
    {
        var (_, ai, pet, owner) = FollowBench();
        Tick(ai, pet);

        owner.SetStatFlag(StatFlag.Hidden);
        owner.MoveTo(new Point3D(100, 108, 0, 0));
        Tick(ai, pet);

        owner.ClearStatFlag(StatFlag.Hidden);
        Tick(ai, pet);

        Assert.True(pet.Y > 100, "the pet did not resume following once revealed");
    }

    // --- SX-07N-01: the follow trigger has three answers --------------------

    [Fact]
    public void AGiveUpAnswerParksThePet()
    {
        var (_, ai, pet, _) = FollowBench();
        ai.OnNpcActFollow = (_, _, _) => NpcAI.FollowTriggerResult.GiveUp;

        Tick(ai, pet);

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
        Assert.Equal(100, pet.X);
    }

    [Fact]
    public void AHandledAnswerTakesNoNativeStep()
    {
        // RETURN 0 is not a Stay order - the script simply dealt with this call.
        var (_, ai, pet, _) = FollowBench();
        ai.OnNpcActFollow = (_, _, _) => NpcAI.FollowTriggerResult.Handled;

        Tick(ai, pet);

        Assert.Equal(PetAIMode.Follow, pet.PetAIMode);
        Assert.Equal(100, pet.X);
    }

    [Fact]
    public void ContinueWalksAsBefore()
    {
        var (_, ai, pet, _) = FollowBench();
        ai.OnNpcActFollow = (_, _, _) => NpcAI.FollowTriggerResult.Continue;

        Tick(ai, pet);

        Assert.Equal(101, pet.X);
    }

    [Fact]
    public void AScriptedDistanceIsHonoured()
    {
        // Eight tiles away, told to keep ten: no approach is wanted.
        var (_, ai, pet, _) = FollowBench();
        ai.OnNpcActFollow = (_, _, args) =>
        {
            args.MaxDistance = 10;
            return NpcAI.FollowTriggerResult.Continue;
        };

        Tick(ai, pet);

        Assert.Equal(100, pet.X);
    }

    [Fact]
    public void TheTriggerIsHandedTheEngineDefaultDistance()
    {
        var (_, ai, pet, _) = FollowBench();
        int seen = -1;
        ai.OnNpcActFollow = (_, _, args) =>
        {
            seen = args.MaxDistance;
            return NpcAI.FollowTriggerResult.Continue;
        };

        Tick(ai, pet);

        Assert.Equal(2, seen);
    }

    // --- SX-07O: a GO order arrives, and restores what it saved -------------

    private static (NpcAI Ai, Character Pet) GoBench(GameWorld world, NpcAI ai,
        Point3D target, PetAIMode? previous)
    {
        var owner = Being(world, new Point3D(90, 100, 0, 0), player: true);
        var pet = Being(world, new Point3D(100, 100, 0, 0));
        pet.TryAssignOwnership(owner, owner);
        pet.PetAIMode = PetAIMode.Come;
        pet.SetTag("GO_TARGET", $"{target.X},{target.Y},{target.Z},{target.Map}");
        if (previous.HasValue)
            pet.SetTag("PREV_PET_MODE", ((int)previous.Value).ToString());
        return (ai, pet);
    }

    [Fact]
    public void ArrivingRestoresTheModeTheOrderSaved()
    {
        // Enum.IsDefined THROWS for a boxed Int32 against a byte-backed enum, so every
        // GO issued by the real command - which does save the mode - blew up here.
        var (world, ai, _) = Setup();
        var (_, pet) = GoBench(world, ai, new Point3D(104, 100, 0, 0), PetAIMode.Guard);

        for (int i = 0; i < 20 && pet.TryGetTag("GO_TARGET", out _); i++)
            Tick(ai, pet);

        Assert.False(pet.TryGetTag("GO_TARGET", out _));
        Assert.Equal(PetAIMode.Guard, pet.PetAIMode);
        Assert.False(pet.TryGetTag("PREV_PET_MODE", out _));
    }

    [Fact]
    public void AnOrderWithNothingSavedParksThePet()
    {
        var (world, ai, _) = Setup();
        var (_, pet) = GoBench(world, ai, new Point3D(104, 100, 0, 0), null);

        for (int i = 0; i < 20 && pet.TryGetTag("GO_TARGET", out _); i++)
            Tick(ai, pet);

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    [Theory]
    [InlineData(101, 100)]      // next door
    [InlineData(101, 101)]      // diagonally next door
    [InlineData(108, 100)]      // across the room
    public void ThePetWalksOntoTheOrderedTileItself(short x, short y)
    {
        var (world, ai, _) = Setup();
        var goal = new Point3D(x, y, 0, 0);
        var (_, pet) = GoBench(world, ai, goal, PetAIMode.Stay);

        for (int i = 0; i < 30 && pet.TryGetTag("GO_TARGET", out _); i++)
            Tick(ai, pet);

        Assert.Equal(goal.X, pet.X);
        Assert.Equal(goal.Y, pet.Y);
    }

    [Fact]
    public void AnUnreachableLastTileEndsTheOrderRatherThanRetryingForever()
    {
        var (world, ai, _) = Setup();
        var goal = new Point3D(101, 100, 0, 0);
        Being(world, goal);          // somebody is standing on it
        var (_, pet) = GoBench(world, ai, goal, PetAIMode.Stay);

        for (int i = 0; i < 10 && pet.TryGetTag("GO_TARGET", out _); i++)
            Tick(ai, pet);

        Assert.False(pet.TryGetTag("GO_TARGET", out _));
        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    // --- SX-07P-01 / SX-07Q-01: the spoken commands -------------------------

    private static (GameClient Client, Character Owner, Character Pet) CommandBench(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7701);
        var owner = Being(world, new Point3D(100, 100, 0, 0), player: true);
        owner.MaxFollower = 5;
        TestHarness.AttachCharacter(client, owner);

        var pet = Being(world, new Point3D(101, 100, 0, 0));
        pet.Name = "reviewpet";
        pet.TryAssignOwnership(owner, owner);
        return (client, owner, pet);
    }

    [Theory]
    [InlineData(PetAIMode.Guard)]
    [InlineData(PetAIMode.Stay)]
    public void AnAttackOrderKeepsTheModeItPromisedToRestore(PetAIMode start)
    {
        // The command saved the mode and then superseded the previous order, which
        // deleted the very value it had just written.
        var (world, _, _) = Setup();
        var (client, _, pet) = CommandBench(world);
        var enemy = Being(world, new Point3D(103, 100, 0, 0));
        pet.PetAIMode = start;

        // The target callback the command's cursor would run, reached directly so the
        // test does not depend on the cursor id echo.
        typeof(ClientItemUseHandler)
            .GetMethod("ApplyPetTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client.ItemUse,
                [pet, "attack", enemy.Uid, (short)0, (short)0, (sbyte)0]);

        Assert.Equal(PetAIMode.Attack, pet.PetAIMode);
        Assert.True(pet.TryGetTag("PREV_PET_MODE", out string? saved));
        Assert.Equal(((int)start).ToString(), saved);
    }

    [Fact]
    public void APetTooWeakForAWeaponLeavesItInThePack()
    {
        var (world, _, _) = Setup();
        var (client, _, pet) = CommandBench(world);
        pet.Str = 10;

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pet.Backpack = pack;
        pet.Equip(pack, Layer.Pack);

        var sword = world.CreateItem();
        sword.BaseId = SwordTile;
        sword.ItemType = ItemType.WeaponSword;
        sword.SetTag("OVERRIDE.REQSTR", "80");
        Assert.True(pack.TryAddItem(sword));
        Assert.False(pet.CanEquip(sword, Layer.OneHanded, out _));

        client.HandleSpeech(0, 0, 0, "reviewpet equip");

        Assert.Contains(sword, pack.Contents);
        Assert.Null(pet.GetEquippedItem(Layer.OneHanded));
    }

    [Fact]
    public void APetStrongEnoughStillEquipsIt()
    {
        var (world, _, _) = Setup();
        var (client, _, pet) = CommandBench(world);
        pet.Str = 100;

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pet.Backpack = pack;
        pet.Equip(pack, Layer.Pack);

        var sword = world.CreateItem();
        sword.BaseId = SwordTile;
        sword.ItemType = ItemType.WeaponSword;
        sword.SetTag("OVERRIDE.REQSTR", "80");
        Assert.True(pack.TryAddItem(sword));

        client.HandleSpeech(0, 0, 0, "reviewpet equip");

        Assert.Same(sword, pet.GetEquippedItem(Layer.OneHanded));
    }
}
