using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a deleted follower leaves behind on its owner's books.
///
/// SphereNet recomputes CurFollower by scanning the world and caches the answer
/// for two seconds. Removing a creature does not rewrite its ownership fields, so
/// nothing marked that cache dirty: for up to CurFollowerCacheMs after a summon
/// was dispelled or expired, the owner's own capacity check still counted it, and
/// their very next summon was refused for a creature no longer in the world.
///
/// Source-X hands the slots back instead of recomputing them. Its NPC teardown
/// calls NPC_PetClearOwners (CChar.cpp:364), which subtracts the creature's cost
/// from the owner outright - FollowersUpdate(this, -iFollowerSlots),
/// CCharNPCPet.cpp:597. That lives in the shared cleanup, not in any one removal
/// path, which is why the fix here sits in Character.Delete rather than in the
/// dispel branch alone.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class SummonCleanupParity04DTests
{
    private static (GameWorld World, Character Owner) Setup(byte maxFollower = 1)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = maxFollower;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        return (world, owner);
    }

    private static Character Pet(GameWorld world, Character owner, int slots = 1)
    {
        var pet = world.CreateCharacter();
        pet.SetStatFlag(StatFlag.Conjured);
        if (slots != 1) pet.TrySetProperty("FOLLOWERSLOTS", slots.ToString());
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: true));
        return pet;
    }

    /// <summary>Read CurFollower so the two-second cache is populated - the state
    /// the defect needs, and the state any status-bar refresh leaves behind.</summary>
    private static void PrimeCache(Character owner, int expected) =>
        Assert.Equal(expected, owner.CurFollower);

    private static void DeleteThroughWorld(GameWorld world, Character ch)
    {
        world.DeleteObject(ch);
        ch.Delete();
    }

    // --- SX-04D-01: a removal frees the slot at once ------------------------

    [Fact]
    public void ADispelledSummonStopsCountingImmediately()
    {
        var (world, owner) = Setup();
        var registry = new SpellRegistry();
        var engine = new SpellEngine(world, registry);
        var summon = Pet(world, owner);
        PrimeCache(owner, 1);

        // The production dispel branch, reached the way the spell reaches it.
        typeof(SpellEngine).GetMethod("DispelConjured",
            BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, new object[] { owner, summon });

        Assert.True(summon.IsDeleted);
        Assert.Equal(0, owner.CurFollower);
    }

    [Fact]
    public void AnExpiredSummonStopsCountingImmediately()
    {
        // Expiry removes the creature through the same two calls the AI makes.
        var (world, owner) = Setup();
        var summon = Pet(world, owner);
        PrimeCache(owner, 1);

        DeleteThroughWorld(world, summon);

        Assert.Equal(0, owner.CurFollower);
    }

    [Fact]
    public void TheOwnerCanSummonAgainRightAway()
    {
        // The symptom the player meets: a refusal for a creature already gone.
        var (world, owner) = Setup();
        var summon = Pet(world, owner);
        PrimeCache(owner, 1);

        DeleteThroughWorld(world, summon);

        var replacement = world.CreateCharacter();
        replacement.SetStatFlag(StatFlag.Conjured);
        world.PlaceCharacter(replacement, new Point3D(102, 100, 0, 0));
        Assert.True(replacement.TryAssignOwnership(owner, owner,
            summoned: true, enforceFollowerCap: true));
    }

    [Fact]
    public void OnlyTheDeletedCreaturesOwnCostComesOff()
    {
        var (world, owner) = Setup(maxFollower: 10);
        var light = Pet(world, owner);
        var heavy = Pet(world, owner, slots: 3);
        PrimeCache(owner, 4);

        DeleteThroughWorld(world, heavy);

        Assert.Equal(1, owner.CurFollower);
        Assert.False(light.IsDeleted);
    }

    [Fact]
    public void ASeparateControllerIsCreditedToo()
    {
        // Owner and controller can be different characters; both keep a count.
        var (world, owner) = Setup(maxFollower: 5);
        var controller = world.CreateCharacter();
        controller.IsPlayer = true;
        controller.MaxFollower = 5;
        world.PlaceCharacter(controller, new Point3D(103, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.SetStatFlag(StatFlag.Conjured);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, controller, summoned: true));

        PrimeCache(owner, 1);

        DeleteThroughWorld(world, pet);

        Assert.Equal(0, owner.CurFollower);
    }

    // --- the removal must stay safe everywhere else -------------------------

    [Fact]
    public void DeletingAnOwnerlessNpcIsHarmless()
    {
        var (world, owner) = Setup(maxFollower: 5);
        var pet = Pet(world, owner);
        PrimeCache(owner, 1);

        var stray = world.CreateCharacter();
        world.PlaceCharacter(stray, new Point3D(104, 100, 0, 0));
        DeleteThroughWorld(world, stray);

        Assert.Equal(1, owner.CurFollower);
        Assert.False(pet.IsDeleted);
    }

    [Fact]
    public void DeletingTheSameCreatureTwiceIsHarmless()
    {
        var (world, owner) = Setup(maxFollower: 5);
        var pet = Pet(world, owner);
        PrimeCache(owner, 1);

        DeleteThroughWorld(world, pet);
        DeleteThroughWorld(world, pet);

        Assert.Equal(0, owner.CurFollower);
    }

    [Fact]
    public void AStaleReferenceDeletedLaterDoesNotDisturbALivePet()
    {
        // Deleting an already-removed creature a second time, after the owner has
        // taken on a new one, must not take the new one off the books.
        var (world, owner) = Setup(maxFollower: 5);
        var first = Pet(world, owner);
        DeleteThroughWorld(world, first);

        var second = Pet(world, owner);
        PrimeCache(owner, 1);

        DeleteThroughWorld(world, first);   // the stale reference

        Assert.Equal(1, owner.CurFollower);
        Assert.False(second.IsDeleted);
    }

    [Fact]
    public void OwnershipSurvivesTheDeleteForATriggerToRead()
    {
        // Only the CACHE is dropped. A delete trigger still has to be able to ask
        // who owned the creature, so the ownership fields are left standing.
        var (world, owner) = Setup();
        var pet = Pet(world, owner);

        DeleteThroughWorld(world, pet);

        Assert.Equal(owner.Uid, pet.OwnerSerial);
    }
}
