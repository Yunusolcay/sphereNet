using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Death;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Objects;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Poisoned meals, and everything a bandage has to be sure of before it raises the
/// dead.
///
/// The Poisoning skill writes POISON_SKILL onto food, and nothing read it back: a
/// successfully poisoned meal was eaten with no effect. Source-X applies it inside
/// Use_EatQty, ahead of the meal itself (CCharUse.cpp:905).
///
/// The rest is one missing precheck. Source-X gathers every corpse-side condition
/// into CItemCorpse::IsCorpseResurrectable and runs it before a bandage is spent
/// (CCharSkill.cpp:2796): the owner must still be dead, the GHOST must be able to see
/// its corpse and stand within two tiles of it, the corpse must be top-level, and the
/// corpse's region must carry none of the antimagic flags (CItemCorpse.cpp:28-75).
/// SphereNet had only the container half, checked the healer's reach where the ghost's
/// was wanted, let an unresolvable corpse fall through to a self-heal, and held a dead
/// NPC to a war-mode test the reference applies only to an unmanifested player ghost.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class HealParity06IMTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        world.InitMap(1, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    // --- SX-06I-01: a poisoned meal poisons the eater ------------------------

    private static (GameClient Client, Character Owner, Character Pet) EatBench(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 6901);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 10;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, owner);

        var pet = world.CreateCharacter();
        pet.BodyId = 0xC8;
        pet.NpcMaster = owner.Uid;
        pet.NpcFood = 10;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        return (client, owner, pet);
    }

    private static Item Meal(GameWorld world, Character owner, int poisonSkill = 0)
    {
        var food = world.CreateItem();
        food.ItemType = ItemType.Food;
        food.Amount = 1;
        if (poisonSkill > 0) food.SetTag("POISON_SKILL", poisonSkill.ToString());
        owner.Backpack!.AddItem(food);
        return food;
    }

    [Fact]
    public void APoisonedMealPoisonsThePlayerWhoEatsIt()
    {
        var world = CreateWorld();
        var (client, owner, _) = EatBench(world);
        var food = Meal(world, owner, poisonSkill: 500);

        client.HandleDoubleClick(food.Uid.Value);

        Assert.True(owner.IsPoisoned);
    }

    [Fact]
    public void APoisonedMealPoisonsThePetItIsFedTo()
    {
        var world = CreateWorld();
        var (client, owner, pet) = EatBench(world);
        var food = Meal(world, owner, poisonSkill: 500);

        client.HandleItemPickup(food.Uid.Value, food.Amount);
        client.HandleItemDrop(food.Uid.Value, 0, 0, 0, pet.Uid.Value);

        Assert.True(pet.IsPoisoned);
    }

    [Fact]
    public void ACleanMealPoisonsNobody()
    {
        var world = CreateWorld();
        var (client, owner, _) = EatBench(world);
        var food = Meal(world, owner);

        client.HandleDoubleClick(food.Uid.Value);

        Assert.False(owner.IsPoisoned);
    }

    [Fact]
    public void AMealAFullPetNeverEatsPoisonsNothing()
    {
        // No bite, no poison: the reference applies it inside Use_EatQty, which has
        // already refused a full eater by then.
        var world = CreateWorld();
        var (client, owner, pet) = EatBench(world);
        pet.NpcFood = pet.MaxFood;
        var food = Meal(world, owner, poisonSkill: 500);

        client.HandleItemPickup(food.Uid.Value, food.Amount);
        client.HandleItemDrop(food.Uid.Value, 0, 0, 0, pet.Uid.Value);

        Assert.False(pet.IsPoisoned);
        Assert.False(food.IsDeleted);
    }

    // --- the bandage bench --------------------------------------------------

    /// <summary>The sink the engine talks through, counting what it was asked to do.
    /// Resurrection goes through the same Character.Resurrect the server's own
    /// offline/NPC fallback uses.</summary>
    private sealed class HealSink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(4242);
        public List<string> Messages { get; } = [];
        public int Resurrections { get; private set; }
        public Item? Bandages { get; set; }

        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Messages.Add(text);
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) =>
            type == ItemType.Bandage ? Bandages : null;
        public void ConsumeAmount(Item item, ushort amount = 1) =>
            item.Amount = (ushort)Math.Max(0, item.Amount - amount);
        public void DeliverItem(Item item) { }
        public void ResurrectTarget(Character target)
        {
            Resurrections++;
            target.Resurrect();
        }
    }

    private sealed record Bench(GameWorld World, SkillHandlers Skills, HealSink Sink,
        Character Healer, Item Bandages);

    private static Bench HealBench(GameWorld world, int healerId = 6902)
    {
        var healer = world.CreateCharacter();
        healer.IsPlayer = true;
        healer.MaxHits = 100;
        healer.Hits = 50;
        healer.SetSkill(SkillType.Healing, 1000);
        healer.SetSkill(SkillType.Anatomy, 1000);
        healer.SetSkill(SkillType.Veterinary, 1000);
        healer.SetSkill(SkillType.AnimalLore, 1000);
        world.PlaceCharacter(healer, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        healer.Backpack = pack;
        healer.Equip(pack, Layer.Pack);
        var bandages = world.CreateItem();
        bandages.ItemType = ItemType.Bandage;
        bandages.Amount = 2;
        pack.AddItem(bandages);

        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;

        var sink = new HealSink(healer, world) { Bandages = bandages };
        return new Bench(world, new SkillHandlers(world), sink, healer, bandages);
    }

    private static Item KillForCorpse(GameWorld world, Character victim)
    {
        victim.MaxHits = 10;
        victim.Hits = 0;
        new DeathEngine(world).ProcessDeath(victim, null);
        return world.GetItemsInRange(victim.Position, 2)
            .First(i => i.ItemType == ItemType.Corpse);
    }

    // --- SX-06J-01: a dead pet is not held to a war-mode test ---------------

    [Fact]
    public void ABondedPetThatDiedOutOfCombatCanBeRevived()
    {
        var world = CreateWorld();
        var b = HealBench(world);
        var pet = world.CreateCharacter();
        pet.BodyId = 0xC8;
        pet.NpcBrain = NpcBrainType.Animal;
        pet.NpcMaster = b.Healer.Uid;
        pet.IsBonded = true;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        KillForCorpse(world, pet);
        Assert.False(pet.IsInWarMode);

        Assert.True(b.Skills.UseActiveSkill(b.Sink, SkillType.Veterinary, pet));
    }

    [Fact]
    public void AnUnmanifestedPlayerGhostIsStillTurnedAway()
    {
        // The player half of the manifest rule is unchanged.
        var world = CreateWorld();
        var b = HealBench(world);
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));
        KillForCorpse(world, victim);

        Assert.False(b.Skills.UseActiveSkill(b.Sink, SkillType.Healing, victim));
        Assert.True(victim.IsStatFlag(StatFlag.Dead));
    }

    // --- SX-06K-01: the GHOST has to be at its own corpse -------------------

    private static (Bench Bench, Character Ghost, Item Corpse) CorpseBench(GameWorld world)
    {
        var b = HealBench(world);
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));
        var corpse = KillForCorpse(world, victim);
        victim.SetStatFlag(StatFlag.War);      // manifested, so 06J is out of the way
        return (b, victim, corpse);
    }

    [Fact]
    public void AGhostStandingAtItsCorpseIsRaised()
    {
        var world = CreateWorld();
        var (b, ghost, corpse) = CorpseBench(world);

        Assert.True(b.Skills.UseActiveSkill(b.Sink, SkillType.Healing, corpse));
        Assert.False(ghost.IsStatFlag(StatFlag.Dead));
    }

    [Fact]
    public void AGhostThatWanderedOffIsNotRaised()
    {
        var world = CreateWorld();
        var (b, ghost, corpse) = CorpseBench(world);
        world.MoveCharacter(ghost, new Point3D(500, 500, 0, 0));

        Assert.False(b.Skills.UseActiveSkill(b.Sink, SkillType.Healing, corpse));
        Assert.True(ghost.IsStatFlag(StatFlag.Dead));
        Assert.Equal(2, b.Bandages.Amount);     // and the bandage is not spent
    }

    [Fact]
    public void AGhostOnAnotherMapIsNotRaised()
    {
        var world = CreateWorld();
        var (b, ghost, corpse) = CorpseBench(world);
        world.MoveCharacter(ghost, new Point3D(101, 100, 0, 1));

        Assert.False(b.Skills.UseActiveSkill(b.Sink, SkillType.Healing, corpse));
        Assert.True(ghost.IsStatFlag(StatFlag.Dead));
    }

    // --- SX-06L-01: an unresolvable corpse is refused, not redirected -------

    [Fact]
    public void ACorpseWhoseOwnerAlreadyRoseIsRefused()
    {
        var world = CreateWorld();
        var (b, ghost, corpse) = CorpseBench(world);
        ghost.Resurrect();

        Assert.False(b.Skills.UseActiveSkill(b.Sink, SkillType.Healing, corpse));
        Assert.Equal(50, b.Healer.Hits);        // the healer was NOT bandaged instead
        Assert.Equal(2, b.Bandages.Amount);
    }

    [Fact]
    public void ACorpseWhoseOwnerIsGoneIsRefused()
    {
        var world = CreateWorld();
        var b = HealBench(world);
        var npc = world.CreateCharacter();
        npc.BodyId = 0xC8;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
        var corpse = KillForCorpse(world, npc);
        world.DeleteObject(npc);
        npc.Delete();

        Assert.False(b.Skills.UseActiveSkill(b.Sink, SkillType.Healing, corpse));
        Assert.Equal(50, b.Healer.Hits);
        Assert.Equal(2, b.Bandages.Amount);
    }

    [Fact]
    public void AskingForNoTargetStillBandagesTheHealer()
    {
        // The self-heal default is the point of "no target", and it stays.
        var world = CreateWorld();
        var b = HealBench(world);

        Assert.True(b.Skills.UseActiveSkill(b.Sink, SkillType.Healing, null));
        Assert.True(b.Healer.Hits > 50);
    }

    // --- SX-06M-01: the corpse's region has a say ---------------------------

    private static void RegionOver(GameWorld world, Point3D at, RegionFlag flag)
    {
        var region = new Region { Name = "test region", Flags = flag, MapIndex = at.Map };
        region.AddRect(90, 90, 120, 120);
        world.AddRegion(region);
    }

    [Theory]
    [InlineData(RegionFlag.NoMagic)]
    [InlineData(RegionFlag.Recall)]
    [InlineData(RegionFlag.NoTeleport)]
    public void AnAntimagicRegionRefusesTheResurrectionBeforeAnythingIsSpent(RegionFlag flag)
    {
        var world = CreateWorld();
        var (b, ghost, corpse) = CorpseBench(world);
        RegionOver(world, corpse.Position, flag);

        Assert.False(b.Skills.UseActiveSkill(b.Sink, SkillType.Healing, corpse));
        Assert.True(ghost.IsStatFlag(StatFlag.Dead));
        Assert.Equal(2, b.Bandages.Amount);
    }
}
