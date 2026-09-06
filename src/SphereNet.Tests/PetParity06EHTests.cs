using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Death;
using SphereNet.Game.Mounts;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// One creature, one relationship; one owner, one bond; and one food pool reached
/// through one eating path.
///
/// A creature already carrying a rider, sitting in a stable or shrunk into a figurine
/// is DISCONNECTED in Source-X, and Make_Figurine refuses to build a second link to
/// one (CCharAct.cpp:3619) so Horse_Mount fails (:3989). SphereNet checked only that
/// the RIDER was free.
///
/// NPC_PetClearOwners drops the bond with the owner - "pets without owner cannot be
/// bonded" (CCharNPCPet.cpp:559). Leaving it set made a released pet's later death
/// take the bonded branch and stay in the world as an ownerless ghost.
///
/// Eating is one path for players and pets: Use_EatQty sizes the bite against the
/// free space (CCharUse.cpp:870) and EatAnim applies it and fires @Eat
/// (CCharAct.cpp:3436). SphereNet had two hand-written versions - the pet one fired
/// no event and destroyed whole stacks, the player one passed ARGN1=5 and applied a
/// flat five - over two food pools that did not agree with each other.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class PetParity06EHTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.MaxFollower = 10;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static Character Horse(GameWorld world, Character owner)
    {
        var horse = world.CreateCharacter();
        horse.BodyId = 0xC8;
        horse.NpcMaster = owner.Uid;
        world.PlaceCharacter(horse, owner.Position);
        return horse;
    }

    // --- SX-06E-01: one relationship per creature ---------------------------

    [Fact]
    public void ASecondRiderCannotMountACarriedCreature()
    {
        var world = CreateWorld();
        var engine = new MountEngine(world);
        var owner = Player(world, 100);
        var friend = Player(world, 100);
        var horse = Horse(world, owner);
        horse.AddFriend(friend);

        Assert.True(engine.TryMount(owner, horse));
        Assert.False(engine.TryMount(friend, horse));
        Assert.False(friend.IsMounted);
    }

    [Fact]
    public void AStabledPetCannotBeMountedThroughItsOldUid()
    {
        var world = CreateWorld();
        var engine = new MountEngine(world);
        var stable = new StableEngine();
        var owner = Player(world, 100);
        var horse = Horse(world, owner);

        Assert.True(stable.StablePet(owner, horse, world));
        Assert.False(engine.TryMount(owner, horse));

        // ...and the stable entry still hands the same creature back.
        Assert.Same(horse, stable.ClaimPet(owner, 0, world, owner.Position));
    }

    [Fact]
    public void AShrunkPetCannotBeMountedThroughItsOldUid()
    {
        var world = CreateWorld();
        var engine = new MountEngine(world);
        var owner = Player(world, 100);
        var horse = Horse(world, owner);

        var figurine = world.CreateItem();
        Assert.True(PetFigurine.Shrink(owner, horse, figurine, world));
        Assert.False(engine.TryMount(owner, horse));

        Assert.Same(horse, PetFigurine.Restore(owner, figurine, world, owner.Position));
    }

    [Fact]
    public void APlayerIsNeverMountable()
    {
        var world = CreateWorld();
        var engine = new MountEngine(world);
        var rider = Player(world, 100);
        var other = Player(world, 100);
        other.BodyId = 0xC8;

        Assert.False(engine.TryMount(rider, other));
    }

    [Fact]
    public void DismountingFreesTheCreatureForTheNextRider()
    {
        var world = CreateWorld();
        var engine = new MountEngine(world);
        var owner = Player(world, 100);
        var second = Player(world, 100);
        var horse = Horse(world, owner);
        horse.AddFriend(second);

        Assert.True(engine.TryMount(owner, horse));
        Assert.Same(horse, engine.Dismount(owner));

        Assert.True(engine.TryMount(second, horse));
    }

    // --- SX-06F-01: no owner, no bond ---------------------------------------

    [Fact]
    public void ReleasingABondedPetDropsTheBond()
    {
        var world = CreateWorld();
        var owner = Player(world, 100);
        var pet = Horse(world, owner);
        pet.IsBonded = true;

        pet.ClearOwnership(clearFriends: true);

        Assert.False(pet.IsBonded);
    }

    [Fact]
    public void AReleasedPetIsCleanedUpWhenItDies()
    {
        // The bonded branch keeps a dead pet in the world; an ownerless one has no
        // business taking it.
        var world = CreateWorld();
        var owner = Player(world, 100);
        var pet = Horse(world, owner);
        pet.IsBonded = true;
        pet.MaxHits = 10;
        pet.Hits = 10;

        pet.ClearOwnership(clearFriends: true);
        pet.Hits = 0;
        new DeathEngine(world).ProcessDeath(pet, owner);

        Assert.True(pet.IsDeleted);
    }

    [Fact]
    public void AnOwnedBondedPetStillGhostsOnDeath()
    {
        // The other side: bonding is what keeps an OWNED pet's body around, and that
        // is unchanged.
        var world = CreateWorld();
        var owner = Player(world, 100);
        var pet = Horse(world, owner);
        pet.IsBonded = true;
        pet.MaxHits = 10;
        pet.Hits = 0;

        new DeathEngine(world).ProcessDeath(pet, owner);

        Assert.False(pet.IsDeleted);
        Assert.True(pet.IsBonded);
    }

    // --- SX-06G-02: one food pool -------------------------------------------

    [Fact]
    public void FoodAndNpcFoodAreTheSameValue()
    {
        var world = CreateWorld();
        var pet = Horse(world, Player(world, 100));

        pet.NpcFood = 42;
        Assert.Equal(42, pet.Food);

        pet.Food = 7;
        Assert.Equal(7, pet.NpcFood);
    }

    [Fact]
    public void AScriptThatFillsFoodStopsThePetDeserting()
    {
        var world = CreateWorld();
        var owner = Player(world, 100);
        var pet = Horse(world, owner);
        pet.TryAssignOwnership(owner, owner);

        Assert.True(pet.TrySetProperty("FOOD", "60"));
        pet.SetTag("PET_NEXT_LOYALTY_TICK", "1");

        pet.TickPetOwnershipTimers(1_000_000);

        Assert.True(pet.OwnerSerial.IsValid);
        Assert.Equal(59, pet.Food);      // one loyalty tick, not a desertion
    }

    [Fact]
    public void MaxFoodComesFromTheCreatureNotAConstant()
    {
        var world = CreateWorld();
        var pet = Horse(world, Player(world, 100));

        Assert.Equal(60, pet.MaxFood);   // the classic default when unset

        pet.SetTag("MAXFOOD", "25");
        pet.Food = 60;
        Assert.Equal(25, pet.MaxFood);
        Assert.Equal(25, pet.Food);      // clamped by the creature's own ceiling
    }

    // --- SX-06G-01 / SX-06H-01: feeding a pet -------------------------------

    private static (GameClient Client, Character Owner, Character Pet, TriggerDispatcher Triggers)
        FeedingBench(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 6600);
        var owner = Player(world, 100);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, owner);

        var pet = Horse(world, owner);
        pet.NpcFood = 10;

        var triggers = new TriggerDispatcher();
        client.SetEngines(triggerDispatcher: triggers);
        return (client, owner, pet, triggers);
    }

    private static Item Ration(GameWorld world, Character owner, ushort amount)
    {
        var food = world.CreateItem();
        food.ItemType = ItemType.Food;
        food.Amount = amount;
        owner.Backpack!.AddItem(food);
        return food;
    }

    private static void Feed(GameClient client, Character pet, Item food)
    {
        client.HandleItemPickup(food.Uid.Value, food.Amount);
        client.HandleItemDrop(food.Uid.Value, 0, 0, 0, pet.Uid.Value);
    }

    [Fact]
    public void AFullPetKeepsTheWholeStack()
    {
        var world = CreateWorld();
        var (client, owner, pet, _) = FeedingBench(world);
        pet.NpcFood = pet.MaxFood;
        var food = Ration(world, owner, 100);

        Feed(client, pet, food);

        Assert.False(food.IsDeleted);
        Assert.Equal(100, food.Amount);
        Assert.Equal(pet.MaxFood, pet.NpcFood);
    }

    [Fact]
    public void ANearlyFullPetEatsOnlyWhatItNeeds()
    {
        var world = CreateWorld();
        var (client, owner, pet, _) = FeedingBench(world);
        pet.NpcFood = (ushort)(pet.MaxFood - 10);   // room for exactly one ration
        var food = Ration(world, owner, 100);

        Feed(client, pet, food);

        Assert.False(food.IsDeleted);
        Assert.Equal(99, food.Amount);
        Assert.Equal(pet.MaxFood, pet.NpcFood);
    }

    [Fact]
    public void AHungryPetEatsTheWholeStackWhenItFits()
    {
        var world = CreateWorld();
        var (client, owner, pet, _) = FeedingBench(world);
        pet.NpcFood = 10;
        var food = Ration(world, owner, 3);         // 30 of the 50 free

        Feed(client, pet, food);

        Assert.True(food.IsDeleted);
        Assert.Equal(40, pet.NpcFood);
    }

    [Fact]
    public void FeedingAPetFiresItsEatEvent()
    {
        var world = CreateWorld();
        var (client, owner, pet, triggers) = FeedingBench(world);
        int fires = 0;
        Item? seen = null;
        long seenN1 = -1;
        triggers.RegisterCharEvent("EVENTSPET", "Eat", (_, a) =>
        {
            fires++;
            seen = a.O1 as Item;
            seenN1 = a.N1;
            return TriggerResult.Default;
        });

        var food = Ration(world, owner, 1);
        Feed(client, pet, food);

        Assert.Equal(1, fires);
        Assert.Same(food, seen);
        Assert.Equal(0, seenN1);        // ARGN1 is a stat limit, and starts at zero
    }

    [Fact]
    public void AScriptMayRewriteTheMealThroughTheLocals()
    {
        var world = CreateWorld();
        var (client, owner, pet, triggers) = FeedingBench(world);
        triggers.RegisterCharEvent("EVENTSPET", "Eat", (_, a) =>
        {
            a.Locals!.SetInt("Food", 1);   // this ration is barely worth anything
            return TriggerResult.Default;
        });

        var food = Ration(world, owner, 1);
        Feed(client, pet, food);

        Assert.Equal(11, pet.NpcFood);  // 10 + the script's 1, not the default 10
    }

    [Fact]
    public void AVetoingScriptBlocksTheGainButNotTheMeal()
    {
        var world = CreateWorld();
        var (client, owner, pet, triggers) = FeedingBench(world);
        triggers.RegisterCharEvent("EVENTSPET", "Eat", (_, _) => TriggerResult.True);

        var food = Ration(world, owner, 1);
        Feed(client, pet, food);

        Assert.Equal(10, pet.NpcFood);  // no gain
        Assert.True(food.IsDeleted);    // but the ration is spent
    }
}
