using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Network.State;

namespace SphereNet.Tests;

public class ItemUseParityTests
{
    [Fact]
    public void WeaponDoubleClick_TargetWeapon_AppliesBackpackPoisonPotion()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out var state, out var player);
        player.PrivLevel = PrivLevel.GM;
        player.SetSkill(SkillType.Poisoning, 1000);

        var pack = EquipBackpack(world, player);
        var blade = world.CreateItem();
        blade.ItemType = ItemType.WeaponSword;
        blade.Name = "poison blade";
        pack.AddItem(blade);
        var poison = world.CreateItem();
        poison.ItemType = ItemType.Potion;
        poison.Quality = 80;
        poison.SetTag("POTION_SPELL", "Poison");
        pack.AddItem(poison);

        client.SetEngines(skillHandlers: new SkillHandlers(world));
        client.HandleDoubleClick(blade.Uid.Value);

        Assert.True(client.HasPendingTarget);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, blade.Uid.Value, 0, 0, 0, 0);

        Assert.True(blade.TryGetTag("POISON_SKILL", out var poisonSkill));
        Assert.Equal("80", poisonSkill);
        Assert.True(poison.IsDeleted);
        Assert.False(client.HasPendingTarget);
    }

    [Fact]
    public void WeaponDoubleClick_TargetDamagedItem_RepairsThroughTinkering()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.PrivLevel = PrivLevel.GM;
        player.SetSkill(SkillType.Tinkering, 1000);

        var pack = EquipBackpack(world, player);
        var tool = world.CreateItem();
        tool.ItemType = ItemType.WeaponMaceSmith;
        pack.AddItem(tool);

        var damaged = world.CreateItem();
        damaged.ItemType = ItemType.Armor;
        damaged.HitsMax = 50;
        damaged.HitsCur = 10;
        damaged.Name = "damaged armor";
        pack.AddItem(damaged);

        // Source-X Use_Repair recognises the piece with Arms Lore first and needs an
        // anvil within two tiles (CCharUse.cpp:764/781); neither was modelled when
        // this test was written.
        player.SetSkill(SkillType.ArmsLore, 1000);
        var anvil = world.CreateItem();
        anvil.ItemType = ItemType.Anvil;
        world.PlaceItem(anvil, player.Position);

        client.SetEngines(skillHandlers: new SkillHandlers(world));
        client.HandleDoubleClick(tool.Uid.Value);

        Assert.True(client.HasPendingTarget);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, damaged.Uid.Value, 0, 0, 0, 0);

        Assert.True(damaged.HitsCur > 10);
        Assert.False(client.HasPendingTarget);
    }

    [Fact]
    public void TrapDoubleClick_SpringsTheTrapAndDamagesUser()
    {
        // Source-X CCharUse Do_Use_Item IT_TRAP: using a trap SPRINGS it (Use_Trap
        // + OnTakeDamage on the user in touch range). It does NOT route to the
        // RemoveTrap skill — the pre-W-A SphereNet behaviour was inverted.
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);

        var trap = world.CreateItem();
        trap.BaseId = 0x1100;
        trap.ItemType = ItemType.Trap;
        trap.More2 = 9; // base damage
        world.PlaceItem(trap, player.Position);

        player.MaxHits = 50;
        player.Hits = 50;
        short hitsBefore = player.Hits;
        client.SetEngines(skillHandlers: new SkillHandlers(world));
        client.HandleDoubleClick(trap.Uid.Value);

        Assert.Equal(ItemType.TrapActive, trap.ItemType); // idle trap armed itself
        Assert.Equal(hitsBefore - 9, player.Hits);        // user took MORE2 damage
        Assert.False(client.HasPendingTarget);            // no RemoveTrap cursor
    }

    [Fact]
    public void OreDoubleClick_TargetForge_SmeltsOreIntoIngots()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.SetSkill(SkillType.Mining, 1000);
        // The reference success curve tops out below certainty (~99.6%);
        // GM auto-success keeps the smelt roll deterministic for the test.
        player.PrivLevel = PrivLevel.GM;

        var pack = EquipBackpack(world, player);
        var ore = world.CreateItem();
        ore.ItemType = ItemType.Ore;
        ore.BaseId = 0x19B9;
        ore.Name = "iron ore";
        ore.Amount = 4;
        pack.AddItem(ore);

        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        client.HandleDoubleClick(ore.Uid.Value);
        Assert.True(client.HasPendingTarget);

        client.HandleTargetResponse(0, client.ActiveTargetCursorId, forge.Uid.Value, forge.X, forge.Y, forge.Z, 0);

        Assert.True(ore.IsDeleted);
        var ingots = Assert.Single(pack.Contents, i => i.ItemType == ItemType.Ingot);
        Assert.Equal(0x1BF2, ingots.BaseId);
        Assert.Equal(4, ingots.Amount);
        Assert.False(client.HasPendingTarget);
    }

    [Fact]
    public void OreDoubleClick_SmeltTriggerReturnTrue_CancelsNativeSmelt()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.SetSkill(SkillType.Mining, 1000);

        var pack = EquipBackpack(world, player);
        var ore = world.CreateItem();
        ore.ItemType = ItemType.Ore;
        ore.BaseId = 0x19B9;
        ore.Amount = 2;
        pack.AddItem(ore);

        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int smeltCount = 0;
        player.SetSkill(SkillType.Mining, 700);
        dispatcher.RegisterItemEvent("EVENTSITEM", "Smelt", (_, args) =>
        {
            smeltCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(ore, args.ItemSrc);
            Assert.Same(forge, args.O1);
            // Source-X seeds ARGN1 with the smelter's MINING SKILL, ARGN2 with the
            // number of resource kinds the ore yields, and the produce itself in
            // LOCAL.resource.0 (Skill_Mining_Smelt, CCharSkill.cpp:1138). The ore
            // count used to be passed as ARGN1, which is not the reference's
            // contract.
            Assert.Equal(700, args.N1);
            Assert.Equal(1, args.N2);
            Assert.NotNull(args.Locals);
            Assert.Equal("1", args.Locals!.Get("resource.0.amount"));
            return TriggerResult.True;
        });
        client.SetEngines(triggerDispatcher: dispatcher);

        client.HandleDoubleClick(ore.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, forge.Uid.Value, forge.X, forge.Y, forge.Z, 0);

        Assert.Equal(1, smeltCount);
        Assert.False(ore.IsDeleted);
        Assert.Equal(2, ore.Amount);
        Assert.DoesNotContain(pack.Contents, i => i.ItemType == ItemType.Ingot);
    }

    [Fact]
    public void FishPoleDoubleClick_OpensFishingTarget()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);

        var pack = EquipBackpack(world, player);
        var pole = world.CreateItem();
        pole.ItemType = ItemType.FishPole; // t_fish_pole
        pole.Name = "fishing pole";
        pack.AddItem(pole);

        client.SetEngines(skillHandlers: new SkillHandlers(world));
        client.HandleDoubleClick(pole.Uid.Value);

        // Double-clicking a fishing pole must arm the fishing target cursor —
        // the "elime gelmiyor / target açılmıyor" report is this not firing.
        Assert.True(client.HasPendingTarget);
    }

    [Fact]
    public void WaterWashDoubleClick_RestoresFood_WithoutDeletingFixture()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.Food = 10;

        // A placed water trough (Source-X Use_Drink) — must give the benefit but
        // survive, not vanish like an eaten ration.
        var trough = world.CreateItem();
        trough.ItemType = ItemType.WaterWash;
        world.PlaceItem(trough, new Point3D(101, 100, 0, 0));

        client.HandleDoubleClick(trough.Uid.Value);

        Assert.True(player.Food > 10);       // drank from it
        Assert.False(trough.IsDeleted);      // the fixture stays
    }

    // Grain is food: Source-X routes it through Use_Eat (CCharUse.cpp:1844), which
    // consumes what was eaten - the last unit included. The old expectation kept a
    // single ear of grain alive forever, feeding its owner on every click; what
    // protects a placed trough is the move rule, not an exemption from being eaten.
    [Fact]
    public void GrainStackDoubleClick_IsEatenDownToNothing()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.Food = 0;

        var pack = EquipBackpack(world, player);
        var hay = world.CreateItem();
        hay.ItemType = ItemType.Grain;
        hay.Amount = 2;
        pack.AddItem(hay);

        client.HandleDoubleClick(hay.Uid.Value);
        Assert.Equal(1, hay.Amount);         // a movable stack loses a unit
        Assert.False(hay.IsDeleted);

        client.HandleDoubleClick(hay.Uid.Value);
        Assert.True(hay.IsDeleted);          // and the last unit is eaten too
    }

    [Fact]
    public void AFixedGrainFixtureIsNotEatenAway()
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = CreatePlayingClient(loggerFactory, world, accounts, out _, out var player);
        player.Food = 0;

        var hay = world.CreateItem();
        hay.ItemType = ItemType.Grain;
        hay.SetAttr(ObjAttributes.Move_Never);
        world.PlaceItem(hay, player.Position);

        client.HandleDoubleClick(hay.Uid.Value);

        Assert.False(hay.IsDeleted);
        Assert.Equal(0, player.Food);        // and it feeds nobody either
    }

    private static GameClient CreatePlayingClient(
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
        GameWorld world,
        AccountManager accounts,
        out NetState state,
        out Character player)
    {
        state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(10_000, 20_000));
        var client = new GameClient(state, world, accounts, loggerFactory.CreateLogger<GameClient>());
        player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Tester";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return client;
    }

    private static Item EquipBackpack(GameWorld world, Character player)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        return pack;
    }
}
