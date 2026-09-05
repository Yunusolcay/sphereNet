using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Stabling and shrinking must not cost the pet anything.
///
/// Both used to delete the creature and rebuild it from a hand-listed snapshot, so
/// whatever the snapshot did not name was gone: every TAG (BONDED and the bonding
/// timer among them), the follower-slot override, the live mana/stamina pools, and
/// the pet's whole inventory - a loaded pack animal lost its cargo outright.
///
/// Source-X CChar::Make_Figurine parks the creature instead (STATF_RIDDEN +
/// disconnected) and links the figurine to its UID, so the same CChar comes back.
/// These tests assert the properties that model gives for free, which is exactly
/// why they were previously untested one field at a time.
/// </summary>
public sealed class PetStorageRoundTripTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeOwner(GameWorld world)
    {
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        return owner;
    }

    /// <summary>A pet carrying the state the old snapshot silently dropped.</summary>
    private static Character MakeRichPet(GameWorld world, Character owner)
    {
        var pet = world.CreateCharacter();
        pet.Name = "Rex";
        pet.NpcBrain = NpcBrainType.Animal;
        pet.BodyId = 0xC9;
        pet.Str = 200;
        pet.MaxHits = 150;
        pet.Hits = 150;
        pet.MaxMana = 80;
        pet.Mana = 70;
        pet.MaxStam = 90;
        pet.Stam = 80;
        pet.SetSkill(SkillType.Magery, 1200);
        pet.IsBonded = true;
        pet.SetTag("SHARD_STATE", "custom");
        pet.TrySetProperty("FOLLOWERSLOTS", "3");
        pet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        return pet;
    }

    private static void AssertRichStateIntact(Character pet)
    {
        Assert.Equal("Rex", pet.Name);
        Assert.Equal(0xC9, pet.BodyId);
        Assert.Equal(200, pet.Str);
        Assert.Equal(150, pet.MaxHits);
        Assert.Equal(1200, pet.GetSkill(SkillType.Magery));

        // The fields the snapshot never carried.
        Assert.True(pet.IsBonded, "BONDED was lost");
        Assert.True(pet.TryGetTag("SHARD_STATE", out string? shard) && shard == "custom",
            "script tags were lost");
        Assert.Equal(3, pet.ControlSlots);
        Assert.Equal(80, pet.MaxMana);
        Assert.Equal(70, pet.Mana);
        Assert.Equal(90, pet.MaxStam);
        Assert.Equal(80, pet.Stam);
    }

    // --- shrink -------------------------------------------------------------

    [Fact]
    public void Shrink_KeepsBondedTagsSlotsAndPools()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);

        var figurine = world.CreateItem();
        Assert.True(PetFigurine.Shrink(owner, pet, figurine, world));

        var restored = PetFigurine.Restore(owner, figurine, world, new Point3D(100, 100, 0, 0));
        Assert.NotNull(restored);
        Assert.Same(pet, restored);
        AssertRichStateIntact(restored!);
    }

    [Fact]
    public void Shrink_KeepsAPackAnimalsCargo()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pet.Equip(pack, Layer.Pack);

        var cargo = world.CreateItem();
        cargo.BaseId = 0x0EED;   // gold
        cargo.Amount = 1000;
        Assert.True(pack.TryAddItem(cargo));
        var cargoUid = cargo.Uid;

        var figurine = world.CreateItem();
        Assert.True(PetFigurine.Shrink(owner, pet, figurine, world));

        var restored = PetFigurine.Restore(owner, figurine, world, new Point3D(100, 100, 0, 0));
        Assert.NotNull(restored);

        // The cargo was destroyed with the pet before; nothing carried inventory.
        Assert.False(cargo.IsDeleted, "the pack animal's cargo was destroyed");
        Assert.NotNull(world.FindItem(cargoUid));
        Assert.Equal(1, restored!.Backpack?.ContentCount ?? 0);
    }

    [Fact]
    public void AParkedPetIsOutOfPlay()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);

        owner.InvalidateFollowerCount();
        int followersBefore = owner.CurFollower;
        Assert.Equal(3, followersBefore);

        Assert.True(PetFigurine.Shrink(owner, pet, world.CreateItem(), world));

        // Invisible: gone from its sector...
        var sector = world.GetSector(pet.Position);
        Assert.DoesNotContain(pet, sector!.Characters);

        // ...and no longer spending the owner's follower budget.
        owner.InvalidateFollowerCount();
        Assert.Equal(0, owner.CurFollower);
    }

    [Fact]
    public void RestoreFails_AndKeepsTheFigurine_WhenTheOwnerIsAtTheFollowerCap()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);   // 3 slots

        var figurine = world.CreateItem();
        Assert.True(PetFigurine.Shrink(owner, pet, figurine, world));

        // Fill the budget while the pet is away.
        var other = world.CreateCharacter();
        other.NpcBrain = NpcBrainType.Animal;
        other.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(other, new Point3D(102, 100, 0, 0));
        owner.MaxFollower = 3;
        owner.InvalidateFollowerCount();

        Assert.Null(PetFigurine.Restore(owner, figurine, world, new Point3D(100, 100, 0, 0)));
        Assert.False(figurine.IsDeleted, "a failed restore must not consume the figurine");
        Assert.True(pet.IsStatFlag(StatFlag.Ridden), "the pet must stay parked");
    }

    [Fact]
    public void RestoreFails_WhenTheParkedPetIsGone()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);

        var figurine = world.CreateItem();
        Assert.True(PetFigurine.Shrink(owner, pet, figurine, world));

        // A GM removed the creature while it was parked.
        world.DeleteObject(pet);
        pet.Delete();

        Assert.Null(PetFigurine.Restore(owner, figurine, world, new Point3D(100, 100, 0, 0)));
        Assert.False(figurine.IsDeleted);
    }

    // --- stable -------------------------------------------------------------

    [Fact]
    public void Stable_KeepsBondedTagsSlotsAndPools()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);
        var stable = new StableEngine();

        Assert.True(stable.StablePet(owner, pet, world));
        Assert.Equal(1, stable.GetStabledCount(owner));
        Assert.Equal("Rex", stable.GetStabledPetNames(owner)[0]);

        var claimed = stable.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0));
        Assert.NotNull(claimed);
        Assert.Same(pet, claimed);
        AssertRichStateIntact(claimed!);
        Assert.Equal(0, stable.GetStabledCount(owner));
    }

    [Fact]
    public void Stable_StillRefusesALoadedPet()
    {
        // Source-X CClientTarg tells the owner to unload first; that message stays,
        // even though the cargo would now survive.
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pet.Equip(pack, Layer.Pack);
        var cargo = world.CreateItem();
        cargo.BaseId = 0x0EED;
        Assert.True(pack.TryAddItem(cargo));

        Assert.False(new StableEngine().StablePet(owner, pet, world));
        Assert.False(pet.IsStatFlag(StatFlag.Ridden), "a refused stable must not park the pet");
    }

    [Fact]
    public void ClaimFails_AndKeepsTheStableEntry_WhenTheOwnerIsAtTheFollowerCap()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);
        var stable = new StableEngine();

        Assert.True(stable.StablePet(owner, pet, world));

        var other = world.CreateCharacter();
        other.NpcBrain = NpcBrainType.Animal;
        other.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(other, new Point3D(102, 100, 0, 0));
        owner.MaxFollower = 3;
        owner.InvalidateFollowerCount();

        Assert.Null(stable.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0)));
        Assert.Equal(1, stable.GetStabledCount(owner));
        Assert.True(pet.IsStatFlag(StatFlag.Ridden));
    }

    [Fact]
    public void StableEntriesSurviveAReloadOfTheOwnersTags()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeRichPet(world, owner);

        Assert.True(new StableEngine().StablePet(owner, pet, world));

        // A fresh engine reads the owner's persisted STABLED_PET tags, as it does
        // after a restart.
        var reloaded = new StableEngine();
        Assert.Equal(1, reloaded.GetStabledCount(owner));

        var claimed = reloaded.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0));
        Assert.NotNull(claimed);
        Assert.Same(pet, claimed);
        AssertRichStateIntact(claimed!);
    }
}
