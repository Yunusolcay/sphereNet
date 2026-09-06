using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Mounts;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Who may command a pet, what a new order does to the last one, and what a stored
/// pet is still attached to.
///
/// Authority: Source-X opens exactly PC_FOLLOW, PC_STAY and PC_STOP to a pet's
/// FRIENDS and sends every other verb to the arm that requires NPC_IsOwnedBy
/// (CCharNPCPet.cpp:129-152). SphereNet treated friendship as full authority, so a
/// friend could make the pet drop its cargo or transfer it to themselves.
///
/// Orders: each command starts a fresh NPC action in the reference -
/// NPCACT_FOLLOW_TARG at :183, NPCACT_GOTO at :504 - so a pending GO cannot outlive
/// the order that replaced it. SphereNet kept GO_TARGET in a tag the AI consults
/// first, so a Come after a Go walked the pet on to the old spot.
///
/// Identity: a stored reference that recorded a UUID must be answered by that UUID
/// alone. Serials are reassigned once the character holding one is deleted, and this
/// state outlives the character - so a serial fallback handed a new player the
/// previous owner's stable, and handed a rider whatever creature inherited their
/// mount's number, which the repair path then hid, moved and re-owned.
///
/// Lifecycle: destroying a figurine ends the life of the pet shrunk inside it, as
/// CItem::DeleteCleanup does for IT_FIGURINE (CItem.cpp:209). Leaving it behind
/// stranded a parked creature and its cargo in the world tables and in every save.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class PetParity06Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient Client, Character Actor) NewClient(GameWorld world, int id, int x)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        var actor = world.CreateCharacter();
        actor.IsPlayer = true;
        world.PlaceCharacter(actor, new Point3D((short)x, 100, 0, 0));
        TestHarness.AttachCharacter(client, actor);
        return (client, actor);
    }

    private static Character Pet(GameWorld world, Character owner, string name = "rex")
    {
        var pet = world.CreateCharacter();
        pet.Name = name;
        pet.NpcMaster = owner.Uid;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        return pet;
    }

    // --- SX-06A-01: a friend is not an owner --------------------------------

    [Fact]
    public void AFriendMayStopThePet()
    {
        var world = CreateWorld();
        var (_, owner) = NewClient(world, 6101, 100);
        var (friendClient, friend) = NewClient(world, 6102, 99);
        var pet = Pet(world, owner);
        pet.AddFriend(friend);

        friendClient.HandleSpeech(0, 0, 0, "rex stop");

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    [Fact]
    public void AFriendMayNotMakeThePetDropItsCargo()
    {
        var world = CreateWorld();
        var (_, owner) = NewClient(world, 6103, 100);
        var (friendClient, friend) = NewClient(world, 6104, 99);
        var pet = Pet(world, owner);
        pet.AddFriend(friend);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pet.Backpack = pack;
        pet.Equip(pack, Layer.Pack);
        var cargo = world.CreateItem();
        Assert.True(pack.TryAddItem(cargo));

        friendClient.HandleSpeech(0, 0, 0, "rex drop");

        Assert.Contains(cargo, pack.Contents);
    }

    [Fact]
    public void TheOwnerMayStillMakeThePetDropItsCargo()
    {
        // The other side of the gate: the verb itself still works.
        var world = CreateWorld();
        var (ownerClient, owner) = NewClient(world, 6105, 100);
        var pet = Pet(world, owner);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pet.Backpack = pack;
        pet.Equip(pack, Layer.Pack);
        var cargo = world.CreateItem();
        Assert.True(pack.TryAddItem(cargo));

        ownerClient.HandleSpeech(0, 0, 0, "rex drop");

        Assert.DoesNotContain(cargo, pack.Contents);
    }

    [Fact]
    public void AFriendMayNotCallThePetToThem()
    {
        // COME and FOLLOW ME are PC_COME / PC_FOLLOW_ME in the reference
        // (CCharNPCPet.cpp:38, :43) - separate commands that fall to the owner-only
        // arm, not members of the friend set.
        var world = CreateWorld();
        var (_, owner) = NewClient(world, 6106, 100);
        var (friendClient, friend) = NewClient(world, 6107, 99);
        var pet = Pet(world, owner);
        pet.AddFriend(friend);
        pet.PetAIMode = PetAIMode.Stay;

        friendClient.HandleSpeech(0, 0, 0, "rex come");

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    [Fact]
    public void AStrangerCommandsNothing()
    {
        var world = CreateWorld();
        var (_, owner) = NewClient(world, 6108, 100);
        var (strangerClient, _) = NewClient(world, 6109, 99);
        var pet = Pet(world, owner);
        pet.PetAIMode = PetAIMode.Follow;

        strangerClient.HandleSpeech(0, 0, 0, "rex stop");

        Assert.Equal(PetAIMode.Follow, pet.PetAIMode);
    }

    // --- SX-06A-02: a new order replaces the last -----------------------------

    [Fact]
    public void ComeCancelsAPendingGoOrder()
    {
        var world = CreateWorld();
        var (ownerClient, owner) = NewClient(world, 6110, 100);
        var pet = Pet(world, owner);
        pet.SetTag("GO_TARGET", "110,100,0,0");
        pet.SetTag("PREV_PET_MODE", ((int)PetAIMode.Follow).ToString());

        ownerClient.HandleSpeech(0, 0, 0, "rex come");

        Assert.False(pet.TryGetTag("GO_TARGET", out _));
        Assert.False(pet.TryGetTag("PREV_PET_MODE", out _));
        Assert.Equal(PetAIMode.Come, pet.PetAIMode);
    }

    [Theory]
    [InlineData("follow me")]
    [InlineData("stay")]
    [InlineData("stop")]
    public void EveryNewOrderCancelsAPendingGo(string verb)
    {
        var world = CreateWorld();
        var (ownerClient, owner) = NewClient(world, 6111, 100);
        var pet = Pet(world, owner);
        pet.SetTag("GO_TARGET", "110,100,0,0");

        ownerClient.HandleSpeech(0, 0, 0, $"rex {verb}");

        Assert.False(pet.TryGetTag("GO_TARGET", out _));
    }

    // --- SX-06B-01: a stable belongs to a character, not to a number ----------

    [Fact]
    public void ANewCharacterInheritingASerialSeesNoStable()
    {
        var world = CreateWorld();
        var stable = new StableEngine();

        var (_, owner) = NewClient(world, 6112, 100);
        owner.MaxFollower = 5;
        var pet = Pet(world, owner, "oldpet");
        Assert.True(stable.StablePet(owner, pet, world));
        Assert.Equal(1, stable.GetStabledCount(owner));

        // The service outlives the character, and the serial is handed on.
        world.DeleteObject(owner);
        owner.Delete();

        var heir = world.CreateCharacter();
        heir.IsPlayer = true;
        heir.MaxFollower = 5;
        world.PlaceCharacter(heir, new Point3D(100, 100, 0, 0));
        Assert.NotEqual(owner.Uuid, heir.Uuid);

        // Asserted, not skipped: if the world ever stops recycling serials this test
        // must fail loudly rather than pass without exercising anything.
        Assert.Equal(owner.Uid, heir.Uid);

        Assert.Equal(0, stable.GetStabledCount(heir));
        Assert.Null(stable.ClaimPet(heir, 0, world, heir.Position));
    }

    [Fact]
    public void TheOwnerStillGetsTheirOwnPetBack()
    {
        var world = CreateWorld();
        var stable = new StableEngine();
        var (_, owner) = NewClient(world, 6113, 100);
        owner.MaxFollower = 5;
        var pet = Pet(world, owner, "goodboy");

        Assert.True(stable.StablePet(owner, pet, world));
        var claimed = stable.ClaimPet(owner, 0, world, owner.Position);

        Assert.Same(pet, claimed);
    }

    // --- SX-06B-02: a stabled pet comes out of the stable idle ----------------

    [Fact]
    public void APetStabledMidFightDoesNotComeBackSwinging()
    {
        var world = CreateWorld();
        var stable = new StableEngine();
        var (_, owner) = NewClient(world, 6114, 100);
        owner.MaxFollower = 5;
        var pet = Pet(world, owner, "bruiser");

        var victim = world.CreateCharacter();
        world.PlaceCharacter(victim, new Point3D(103, 100, 0, 0));
        pet.PetAIMode = PetAIMode.Attack;
        pet.SetTag("ATTACK_TARGET", victim.Uid.Value.ToString());
        pet.FightTarget = victim.Uid;

        Assert.True(stable.StablePet(owner, pet, world));
        var claimed = stable.ClaimPet(owner, 0, world, owner.Position);

        Assert.Same(pet, claimed);
        Assert.False(pet.TryGetTag("ATTACK_TARGET", out _));
        Assert.Equal(PetAIMode.Follow, pet.PetAIMode);
        Assert.False(pet.FightTarget.IsValid);
    }

    // --- SX-06C-01: a mount link resolves by identity, or not at all ----------

    private static (GameWorld World, MountEngine Engine, Character Rider, Character Horse) Mounted()
    {
        var world = CreateWorld();
        var engine = new MountEngine(world);

        var rider = world.CreateCharacter();
        rider.IsPlayer = true;
        rider.MaxFollower = 5;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));

        var horse = world.CreateCharacter();
        horse.BodyId = 0xC8;
        horse.NpcMaster = rider.Uid;
        world.PlaceCharacter(horse, new Point3D(100, 100, 0, 0));

        Assert.True(engine.TryMount(rider, horse));
        return (world, engine, rider, horse);
    }

    [Fact]
    public void ANormalMountStillDismountsTheSameCreature()
    {
        var (_, engine, rider, horse) = Mounted();

        engine.EnsureMountedState(rider);

        Assert.Same(horse, engine.Dismount(rider));
    }

    [Fact]
    public void DeletingTheMountBreaksTheRidersLink()
    {
        var (world, engine, rider, horse) = Mounted();
        Character.MountedNpcDeletedHook = npc => engine.OnMountNpcDeleted(npc);
        try
        {
            world.DeleteObject(horse);
            horse.Delete();

            Assert.False(rider.IsMounted);
            Assert.False(rider.TryGetTag("MOUNT_NPC_UUID", out _));
            Assert.Null(rider.GetEquippedItem(Layer.Horse));
        }
        finally { Character.MountedNpcDeletedHook = null; }
    }

    [Fact]
    public void AStaleMountLinkNeverResolvesOntoAnotherCharacter()
    {
        // The serial is only consulted for a link that never recorded a UUID. With
        // one recorded and unresolvable, the repair must find nothing rather than
        // seize whatever inherited the number - which for a player meant a real
        // character flagged Ridden, teleported onto the rider and stamped as a pet.
        var (world, engine, rider, horse) = Mounted();
        Serial horseUid = horse.Uid;
        world.DeleteObject(horse);
        horse.Delete();

        var bystander = world.CreateCharacter();
        bystander.IsPlayer = true;
        world.PlaceCharacter(bystander, new Point3D(200, 100, 0, 0));
        Assert.Equal(horseUid, bystander.Uid);

        engine.EnsureMountedState(rider);

        Assert.False(bystander.IsStatFlag(StatFlag.Ridden));
        Assert.Equal(new Point3D(200, 100, 0, 0), bystander.Position);
        Assert.NotSame(bystander, engine.Dismount(rider));
    }

    [Fact]
    public void APlayerIsNeverAcceptedAsAMount()
    {
        var world = CreateWorld();
        var engine = new MountEngine(world);
        var rider = world.CreateCharacter();
        rider.IsPlayer = true;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));

        // A hand-written link, as a corrupted save would carry.
        rider.SetTag("MOUNT_NPC_SERIAL", player.Uid.Value.ToString());
        rider.SetStatFlag(StatFlag.OnHorse);

        engine.EnsureMountedState(rider);

        Assert.False(player.IsStatFlag(StatFlag.Ridden));
    }

    // --- SX-06D-01: a destroyed figurine takes its pet with it ---------------

    private static (GameWorld World, Character Owner, Item Figurine, Character Pet) Shrunk()
    {
        var world = CreateWorld();
        Item.FigurineDeletedHook = item => PetFigurine.OnFigurineDeleted(item, world);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 10;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "packhorse";
        pet.NpcMaster = owner.Uid;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        var figurine = world.CreateItem();
        Assert.True(PetFigurine.Shrink(owner, pet, figurine, world));
        return (world, owner, figurine, pet);
    }

    [Fact]
    public void DestroyingAFigurineEndsThePetInsideIt()
    {
        try
        {
            var (world, _, figurine, pet) = Shrunk();
            Assert.True(PetStorage.IsParked(pet));

            world.DeleteObject(figurine);
            figurine.Delete();

            Assert.True(pet.IsDeleted);
            Assert.Null(world.FindChar(pet.Uid));
        }
        finally { Item.FigurineDeletedHook = null; }
    }

    [Fact]
    public void ARestoredPetSurvivesTheFigurineBeingConsumed()
    {
        // The figurine is destroyed by a successful restore too; the pet that just
        // came out of it must not be taken along.
        try
        {
            var (world, owner, figurine, pet) = Shrunk();

            var restored = PetFigurine.Restore(owner, figurine, world, owner.Position);

            Assert.Same(pet, restored);
            Assert.False(pet.IsDeleted);
            Assert.True(figurine.IsDeleted);
        }
        finally { Item.FigurineDeletedHook = null; }
    }

    [Fact]
    public void AnOrdinaryItemIsUnaffected()
    {
        try
        {
            var (world, _, _, _) = Shrunk();
            var plain = world.CreateItem();

            world.DeleteObject(plain);
            plain.Delete();

            Assert.True(plain.IsDeleted);
        }
        finally { Item.FigurineDeletedHook = null; }
    }
}
