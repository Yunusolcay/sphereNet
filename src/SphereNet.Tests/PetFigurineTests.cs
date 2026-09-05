using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X pet figurine (shrink/restore): a controlled pet packs into a figurine item
// carrying a full snapshot, and is recreated from it on use.
public class PetFigurineTests
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

    private static Character MakeOwnedPet(GameWorld world, Character owner, string name)
    {
        var pet = world.CreateCharacter();
        pet.Name = name;
        pet.NpcBrain = NpcBrainType.Animal;
        pet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        return pet;
    }

    [Fact]
    public void ShrinkAndRestore_RoundTripsPetState()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);

        var pet = MakeOwnedPet(world, owner, "Rex");
        pet.BodyId = 0xC9;
        pet.Str = 200; pet.MaxHits = 150;
        pet.SetSkill(SkillType.Magery, 1200);
        var petUid = pet.Uid;

        var figurine = world.CreateItem();
        Assert.True(PetFigurine.Shrink(owner, pet, figurine, world));
        Assert.True(PetFigurine.IsPetFigurine(figurine));
        Assert.Equal(ItemType.Figurine, figurine.ItemType);
        // Source-X Make_Figurine parks the creature (STATF_RIDDEN + disconnected)
        // instead of destroying it, so the same object is still there - out of its
        // sector, out of the follower count, and not ticking.
        var parked = world.FindChar(petUid);
        Assert.NotNull(parked);
        Assert.True(parked!.IsStatFlag(SphereNet.Core.Enums.StatFlag.Ridden));

        var restored = PetFigurine.Restore(owner, figurine, world, new Point3D(100, 100, 0, 0));
        Assert.NotNull(restored);
        Assert.Same(pet, restored); // the same creature, not a rebuilt copy
        Assert.False(restored!.IsStatFlag(SphereNet.Core.Enums.StatFlag.Ridden));
        Assert.Equal("Rex", restored.Name);
        Assert.Equal(0xC9, restored.BodyId);
        Assert.Equal(200, restored.Str);
        Assert.Equal(150, restored.MaxHits);
        Assert.Equal(1200, restored.GetSkill(SkillType.Magery));
        Assert.True(restored.HasOwner(owner.Uid));
        Assert.True(figurine.IsDeleted); // figurine consumed
    }

    [Fact]
    public void Shrink_RejectsNonOwnedAndSummonedPets()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);

        // Not owned by `owner`.
        var stray = world.CreateCharacter();
        stray.NpcBrain = NpcBrainType.Animal;
        world.PlaceCharacter(stray, new Point3D(101, 100, 0, 0));
        Assert.False(PetFigurine.Shrink(owner, stray, world.CreateItem(), world));

        // Owned but summoned.
        var summon = MakeOwnedPet(world, owner, "Imp");
        summon.SetTag("SUMMON_MASTER", owner.Uid.Value.ToString());
        Assert.True(summon.IsSummoned);
        Assert.False(PetFigurine.Shrink(owner, summon, world.CreateItem(), world));
    }

    [Fact]
    public void Restore_FollowerCapFull_KeepsFigurine()
    {
        var world = CreateWorld();
        var owner = MakeOwner(world);
        var pet = MakeOwnedPet(world, owner, "Rex");

        var figurine = world.CreateItem();
        Assert.True(PetFigurine.Shrink(owner, pet, figurine, world));

        owner.MaxFollower = 0; // cap full -> restore must fail without losing the figurine
        var restored = PetFigurine.Restore(owner, figurine, world, new Point3D(100, 100, 0, 0));
        Assert.Null(restored);
        Assert.False(figurine.IsDeleted);
        Assert.True(PetFigurine.IsPetFigurine(figurine)); // snapshot intact
    }

    [Fact]
    public void Figurine_DoubleClick_RestoresStoredPet()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1860);

        var player = world.CreateCharacter();
        player.IsPlayer = true; player.MaxFollower = 5;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container; pack.BaseId = 0x0E75;
        player.Backpack = pack; player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);

        var pet = MakeOwnedPet(world, player, "Rex");
        var figurine = world.CreateItem();
        figurine.BaseId = 0x2106;
        Assert.True(PetFigurine.Shrink(player, pet, figurine, world));
        pack.AddItem(figurine);

        client.HandleDoubleClick(figurine.Uid.Value);

        Assert.True(figurine.IsDeleted); // figurine consumed
        var restored = world.GetCharsInRange(player.Position, 2)
            .FirstOrDefault(c => !c.IsPlayer && c.Name == "Rex");
        Assert.NotNull(restored);
        Assert.True(restored!.HasOwner(player.Uid));
    }
}
