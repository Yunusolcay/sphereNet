using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a definition's MAXFOOD is worth, and what a pet keeps when it changes hands.
///
/// Source-X falls back to the definition's food maximum whenever the instance one is
/// below 1 (Stat_GetMax, CCharStat.cpp:276), and a maximum of zero means the creature
/// cannot be fed at all - Use_Eat refuses it outright (CCharUse.cpp:934). SphereNet
/// read a zero as "nothing was said" and handed out the classic ceiling instead, so a
/// creature declared to eat nothing had a full appetite. The spawner also wrote the
/// starting food before the ceiling, so the setter clamped a MAXFOOD=100 creature to
/// 60 on the way in.
///
/// A transfer is a real change of owner, and Source-X sends it through
/// NPC_PetSetOwner, which clears the old owner's memories and the bond first
/// (CCharNPCPet.cpp:600 -> :553). SphereNet rewrote the owner alone, so a transferred
/// pet still answered to the previous owner's friends and carried a bond its new
/// owner never earned.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PetParity06NOTests
{
    // --- SX-06N-01 / SX-06N-02: the definition's ceiling --------------------

    private const string Creatures = """
        [CHARDEF 0c8]
        DEFNAME=c_review_starving
        NAME=Starving thing
        MAXFOOD=0

        [CHARDEF 0e2]
        DEFNAME=c_review_normal
        NAME=Normal thing
        MAXFOOD=30

        [CHARDEF 0e4]
        DEFNAME=c_review_hearty
        NAME=Hearty thing
        MAXFOOD=100

        [CHARDEF 0e6]
        DEFNAME=c_review_silent
        NAME=Silent thing
        """;

    private static GameWorld LoadWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_06n_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, Creatures);

        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(path) ?? ""
        };
        resources.LoadResourceFile(path);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        return world;
    }

    /// <summary>A creature carrying the definition, set up the way the spawner does.</summary>
    private static Character Spawn(GameWorld world, ushort defId)
    {
        var ch = world.CreateCharacter();
        ch.BodyId = defId;
        ch.CharDefIndex = defId;
        var def = DefinitionLoader.GetCharDef(defId);
        Assert.NotNull(def);
        if (def!.MaxFoodExplicit || def.MaxFood > 0)
        {
            ch.SetTag("MAXFOOD", def.MaxFood.ToString());
            ch.Food = def.MaxFood;
        }
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    private static Item Ration(GameWorld world)
    {
        var food = world.CreateItem();
        food.ItemType = ItemType.Food;
        food.Amount = 1;
        return food;
    }

    [Fact]
    public void ADefinitionThatEatsNothingHasNoRoomForFood()
    {
        var world = LoadWorld();
        var ch = Spawn(world, 0x00C8);

        Assert.Equal(0, ch.MaxFood);
        Assert.Equal(0, ch.Food);
    }

    [Fact]
    public void ADefinitionThatEatsNothingIsNeverFed()
    {
        var world = LoadWorld();
        var ch = Spawn(world, 0x00C8);

        Assert.Equal(0, EatEngine.Eat(ch, Ration(world), null, 1));
        Assert.Equal(0, ch.Food);
    }

    [Fact]
    public void AnOrdinaryDefinitionKeepsItsOwnCeiling()
    {
        var world = LoadWorld();
        var ch = Spawn(world, 0x00E2);

        Assert.Equal(30, ch.MaxFood);
        Assert.Equal(30, ch.Food);
    }

    [Fact]
    public void AHeartyDefinitionSpawnsAtItsFullCeiling()
    {
        // The reported symptom: a MAXFOOD=100 creature used to spawn at 60 because
        // the Food setter clamped to whatever MaxFood was at the time of the write.
        // Resolving the ceiling from the definition removes the ordering hazard from
        // this path entirely - the clamp is already 100 before the tag is written -
        // and the spawner's write order was corrected as well, which is what still
        // protects a creature whose ceiling comes only from an instance tag.
        var world = LoadWorld();
        var ch = Spawn(world, 0x00E4);

        Assert.Equal(100, ch.MaxFood);
        Assert.Equal(100, ch.Food);
    }

    [Fact]
    public void AnInstanceCeilingMustBeSetBeforeTheValueItCaps()
    {
        // The ordering hazard on its own, with no definition to fall back on: writing
        // the value first clamps it to the classic ceiling and the raise is lost.
        var world = LoadWorld();
        var wrongOrder = world.CreateCharacter();
        wrongOrder.Food = 100;
        wrongOrder.SetTag("MAXFOOD", "100");
        Assert.Equal(60, wrongOrder.Food);

        var rightOrder = world.CreateCharacter();
        rightOrder.SetTag("MAXFOOD", "100");
        rightOrder.Food = 100;
        Assert.Equal(100, rightOrder.Food);
    }

    [Fact]
    public void ADefinitionThatSaysNothingKeepsTheClassicCeiling()
    {
        // "Nothing was said" is not "eats nothing" - the live pack's creatures mostly
        // say nothing, and they must keep an appetite.
        var world = LoadWorld();
        var ch = Spawn(world, 0x00E6);

        Assert.Equal(60, ch.MaxFood);
        Assert.True(EatEngine.Eat(ch, Ration(world), null, 1) > 0);
    }

    [Fact]
    public void AnInstanceCeilingStillOverridesTheDefinition()
    {
        var world = LoadWorld();
        var ch = Spawn(world, 0x00E2);
        ch.SetTag("MAXFOOD", "45");

        Assert.Equal(45, ch.MaxFood);
    }

    [Fact]
    public void AnInstanceZeroFallsBackToTheDefinition()
    {
        // Source-X falls back whenever the instance maximum is below 1, so a zero
        // there means "unset", not "no capacity".
        var world = LoadWorld();
        var ch = Spawn(world, 0x00E2);
        ch.SetTag("MAXFOOD", "0");

        Assert.Equal(30, ch.MaxFood);
    }

    // --- SX-06O-01: a transfer is a change of owner -------------------------

    private static (GameWorld World, Character A, Character B, Character Friend, Character Pet)
        TransferBench()
    {
        var world = TestHarness.CreateWorld();
        Character Player(int x)
        {
            var p = world.CreateCharacter();
            p.IsPlayer = true;
            p.MaxFollower = 5;
            world.PlaceCharacter(p, new Point3D((short)x, 100, 0, 0));
            return p;
        }

        var a = Player(100);
        var b = Player(99);
        var friend = Player(98);

        var pet = world.CreateCharacter();
        pet.Name = "reviewpet";
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        pet.TryAssignOwnership(a, a);
        pet.AddFriend(friend);
        pet.IsBonded = true;
        return (world, a, b, friend, pet);
    }

    [Fact]
    public void ATransferredPetForgetsTheOldOwnersFriends()
    {
        var (_, _, b, friend, pet) = TransferBench();

        Assert.True(pet.TryAssignOwnership(b, b, enforceFollowerCap: true));

        Assert.True(pet.HasOwner(b.Uid));
        Assert.False(pet.IsFriendOf(friend.Uid));
        Assert.False(pet.CanAcceptPetCommandFrom(friend));
    }

    [Fact]
    public void ATransferredPetLosesTheBondItNeverGaveItsNewOwner()
    {
        var (_, _, b, _, pet) = TransferBench();

        Assert.True(pet.TryAssignOwnership(b, b, enforceFollowerCap: true));

        Assert.False(pet.IsBonded);
    }

    [Fact]
    public void ReassigningTheSameOwnerChangesNothing()
    {
        // Stable retrieval, figurine restore and dismount all re-assign the same
        // owner; none of them may reset a thing.
        var (_, a, _, friend, pet) = TransferBench();

        Assert.True(pet.TryAssignOwnership(a, a, enforceFollowerCap: true));

        Assert.True(pet.IsBonded);
        Assert.True(pet.IsFriendOf(friend.Uid));
    }

    [Fact]
    public void ARefusedTransferLeavesThePetExactlyAsItWas()
    {
        var (_, a, b, friend, pet) = TransferBench();
        b.MaxFollower = 0;

        Assert.False(pet.TryAssignOwnership(b, b, enforceFollowerCap: true));

        Assert.True(pet.HasOwner(a.Uid));
        Assert.True(pet.IsBonded);
        Assert.True(pet.IsFriendOf(friend.Uid));
    }

    [Fact]
    public void TheNewOwnerCanMakeTheirOwnFriends()
    {
        var (world, _, b, _, pet) = TransferBench();
        Assert.True(pet.TryAssignOwnership(b, b, enforceFollowerCap: true));

        var newFriend = world.CreateCharacter();
        newFriend.IsPlayer = true;
        world.PlaceCharacter(newFriend, new Point3D(97, 100, 0, 0));
        pet.AddFriend(newFriend);

        Assert.True(pet.CanAcceptPetCommandFrom(b));
        Assert.True(pet.IsFriendOf(newFriend.Uid));
    }
}
