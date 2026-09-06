using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Smelting ore and mending gear.
///
/// Source-X reads the ingot an ore yields from the ore definition's TDATA1
/// (Skill_Mining_Smelt, CCharSkill.cpp:1150), hands @Smelt the smelter's Mining
/// skill, the number of resource kinds and the produce in LOCAL.resource.0 and reads
/// them back (:1138/:1196), loses only rand(amount/2)+1 of the pile on a failed
/// smelt (:1247), and creates the ingot before it is handed over, not after it has
/// merged into a pile already in the pack (:1260/:1284). A repair identifies the
/// piece with Arms Lore first, needs an anvil within two tiles and spends half the
/// damage percentage in raw materials (Use_Repair, CCharUse.cpp:764/781/794).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SmeltRepairParity08ATests
{
    private const ushort OreTile = 0x19B9;
    private const ushort IronIngot = 0x1BF2;
    private const ushort SpecialIngot = 0x6001;

    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack);

    private static Bench Setup(TriggerDispatcher? triggers = null)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8101);
        if (triggers != null)
            client.SetEngines(triggerDispatcher: triggers);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = 100; me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 100; me.Int = 100;
        me.SetSkill(SkillType.Mining, 1000);
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);

        return new Bench(world, client, me, pack);
    }

    /// <summary>Decide skill rolls instead of drawing them. Source-X's own
    /// @SkillUseQuick hook, reset between tests by ResetEngineStatics - without it a
    /// bell curve gives a 0-skill smith the occasional success and the outcome of
    /// these tests is a coin toss.</summary>
    private static void SkillRolls(params (SkillType Skill, bool Succeeds)[] outcomes)
    {
        Character.OnSkillUseQuick = (_, skillId, _, result) =>
        {
            foreach (var (skill, succeeds) in outcomes)
            {
                if ((int)skill == skillId)
                    return succeeds ? 1 : 0;
            }
            return result;
        };
    }

    private static void DefineItem(int baseId, Action<ItemDef> shape)
    {
        var def = new ItemDef(new ResourceId(ResType.ItemDef, baseId));
        shape(def);
        var table = (Dictionary<int, ItemDef>)typeof(SphereNet.Game.Definitions.DefinitionLoader)
            .GetField("_itemDefs", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        table[baseId] = def;
    }

    private static (Item Ore, Item Forge) Smeltable(Bench bench, ushort amount = 4,
        ushort ingot = 0)
    {
        if (ingot != 0)
            DefineItem(OreTile, d => { d.Type = ItemType.Ore; d.TData1 = ingot; });

        var ore = bench.World.CreateItem();
        ore.BaseId = OreTile;
        ore.ItemType = ItemType.Ore;
        ore.Amount = amount;
        Assert.True(bench.Pack.TryAddItem(ore));

        var forge = bench.World.CreateItem();
        forge.ItemType = ItemType.Forge;
        bench.World.PlaceItem(forge, new Point3D(101, 100, 0, 0));
        return (ore, forge);
    }

    private static void Smelt(Bench bench, Item ore, Item forge)
    {
        bench.Client.HandleDoubleClick(ore.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            forge.Uid.Value, forge.X, forge.Y, forge.Z, 0);
    }

    private static Item? Ingots(Bench bench, ushort id) =>
        bench.Pack.Contents.FirstOrDefault(i => i.BaseId == id);

    // --- SX-08A-01: the ore's own ingot ----------------------------------

    [Fact]
    public void AnOreSmeltsIntoTheIngotItsDefinitionNames()
    {
        // Every coloured ore turned into iron, carrying only its hue.
        var bench = Setup();
        var (ore, forge) = Smeltable(bench, ingot: SpecialIngot);

        Smelt(bench, ore, forge);

        Assert.NotNull(Ingots(bench, SpecialIngot));
        Assert.Null(Ingots(bench, IronIngot));
    }

    [Fact]
    public void AnOreWithNoDefinitionStillSmeltsIntoIron()
    {
        var bench = Setup();
        var (ore, forge) = Smeltable(bench);

        Smelt(bench, ore, forge);

        Assert.NotNull(Ingots(bench, IronIngot));
    }

    [Fact]
    public void AnExplicitSmeltToTagStillWins()
    {
        var bench = Setup();
        var (ore, forge) = Smeltable(bench, ingot: SpecialIngot);
        ore.SetTag("SMELT_TO", IronIngot.ToString());

        Smelt(bench, ore, forge);

        Assert.NotNull(Ingots(bench, IronIngot));
    }

    // --- SX-08A-02: a failed smelt costs part of the pile ----------------

    [Fact]
    public void AFailedSmeltDoesNotBurnTheWholePile()
    {
        var bench = Setup();
        SkillRolls((SkillType.Mining, false));
        var (ore, forge) = Smeltable(bench, amount: 10);

        Smelt(bench, ore, forge);

        Assert.False(ore.IsDeleted);
        Assert.InRange(ore.Amount, 5, 9);         // rand(10/2)+1 lost, at most half
        Assert.Null(Ingots(bench, IronIngot));
    }

    [Fact]
    public void AFailedSmeltOfATinyPileStillLeavesSomething()
    {
        var bench = Setup();
        SkillRolls((SkillType.Mining, false));
        var (ore, forge) = Smeltable(bench, amount: 2);

        Smelt(bench, ore, forge);

        Assert.False(ore.IsDeleted);
        Assert.Equal(1, ore.Amount);              // rand(1)+1 == 1
    }

    // --- SX-08A-03: the @Smelt contract ----------------------------------

    [Fact]
    public void AScriptMayChooseTheProduceAndTheYield()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Smelt", (_, args) =>
        {
            Assert.Equal(1000, args.N1);          // the smelter's Mining skill
            Assert.Equal(1, args.N2);             // one kind of resource
            args.Locals!.SetInt("resource.0.ID", SpecialIngot);
            args.Locals.SetInt("resource.0.amount", 2);
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);
        var (ore, forge) = Smeltable(bench, amount: 4);

        Smelt(bench, ore, forge);

        var made = Ingots(bench, SpecialIngot);
        Assert.NotNull(made);
        Assert.Equal(8, made!.Amount);            // 4 ore x 2 each
    }

    [Fact]
    public void AScriptMayWaiveTheSkillRequirement()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Smelt", (_, args) =>
        {
            args.N3 = 1;                          // skip the minimum-skill roll
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);
        SkillRolls((SkillType.Mining, false));
        var (ore, forge) = Smeltable(bench, amount: 4);

        Smelt(bench, ore, forge);

        Assert.NotNull(Ingots(bench, IronIngot));
    }

    [Fact]
    public void AScriptMayStillRefuseTheSmelt()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Smelt", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);
        var (ore, forge) = Smeltable(bench, amount: 4);

        Smelt(bench, ore, forge);

        Assert.False(ore.IsDeleted);
        Assert.Equal(4, ore.Amount);
        Assert.Null(Ingots(bench, IronIngot));
    }

    // --- SX-08A-04: @Create belongs to the new ingots --------------------

    [Fact]
    public void TheCreateTriggerSeesOnlyTheNewIngots()
    {
        // Merging first and firing afterwards ran the creation script over the pile
        // the player already had.
        int seenAmount = -1;
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Create", (obj, _) =>
        {
            if (obj is Item made && made.BaseId == IronIngot)
                seenAmount = made.Amount;
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);

        // The pile has to be able to take the new ingots, or the merge this finding
        // is about never happens: CAN_I_PILE is what makes an ingot stackable.
        DefineItem(IronIngot, d => { d.Type = ItemType.Ingot; d.Can = CanFlags.I_Pile; });
        var already = bench.World.CreateItem();
        already.BaseId = IronIngot;
        already.ItemType = ItemType.Ingot;
        already.Name = "iron ingot";
        already.Amount = 10;
        Assert.True(bench.Pack.TryAddItem(already));

        var (ore, forge) = Smeltable(bench, amount: 4);
        Smelt(bench, ore, forge);

        Assert.Equal(4, seenAmount);              // the four just made, not fourteen
        Assert.Equal(14, already.Amount);         // and they still merged afterwards
    }

    // --- SX-08A-05 / 08A-06: repairing -----------------------------------

    private sealed class RepairSink(Character self, GameWorld world, CraftingEngine? crafting)
        : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public Random Random { get; } = new(7);
        public GameWorld World { get; } = world;
        public CraftingEngine? Crafting { get; } = crafting;
        public List<string> Messages { get; } = [];
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private sealed record Forge(Bench Bench, Item Broken, RepairSink Sink);

    private static Forge RepairBench(bool anvil = true, bool materials = true,
        bool identifies = true, CraftingEngine? crafting = null)
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.Tinkering, 2000);
        bench.Me.SetSkill(SkillType.ArmsLore, 1000);
        SkillRolls((SkillType.ArmsLore, identifies), (SkillType.Tinkering, true));

        if (anvil)
        {
            var block = bench.World.CreateItem();
            block.ItemType = ItemType.Anvil;
            bench.World.PlaceItem(block, bench.Me.Position);
        }

        var broken = bench.World.CreateItem();
        broken.BaseId = 0x13BB;
        broken.ItemType = ItemType.Armor;
        broken.HitsMax = 100;
        broken.HitsCur = 20;
        Assert.True(bench.Pack.TryAddItem(broken));

        if (materials)
        {
            var iron = bench.World.CreateItem();
            iron.BaseId = IronIngot;
            iron.ItemType = ItemType.Ingot;
            iron.Amount = 100;
            Assert.True(bench.Pack.TryAddItem(iron));
        }

        return new Forge(bench, broken, new RepairSink(bench.Me, bench.World, crafting));
    }

    [Fact]
    public void RepairingNeedsAnAnvil()
    {
        var forge = RepairBench(anvil: false);

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Broken));
        Assert.Equal(20, forge.Broken.GetHitsCur());
    }

    [Fact]
    public void RepairingWithAnAnvilStillWorks()
    {
        var forge = RepairBench();

        Assert.True(ActiveSkillEngine.RepairItem(forge.Sink, forge.Broken));
        Assert.Equal(100, forge.Broken.GetHitsCur());
    }

    [Fact]
    public void ASmithWhoCannotIdentifyThePieceDoesNotRepairIt()
    {
        // Arms Lore is its own stage before the craft skill is ever rolled.
        var forge = RepairBench(identifies: false);

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Broken));
        Assert.Equal(20, forge.Broken.GetHitsCur());
    }

    [Fact]
    public void RepairingSpendsTheMaterialsThePieceIsMadeOf()
    {
        var world = TestHarness.CreateWorld();
        var crafting = new CraftingEngine(world);
        var recipe = new CraftRecipe { ResultDefId = 0x13BB, ResultItemId = 0x13BB };
        recipe.Resources.Add(new CraftResource { ItemId = IronIngot, Amount = 100 });
        crafting.RegisterRecipe(recipe);

        var forge = RepairBench(crafting: crafting);
        var iron = forge.Bench.Pack.Contents.First(i => i.BaseId == IronIngot);

        Assert.True(ActiveSkillEngine.RepairItem(forge.Sink, forge.Broken));

        // 80% damaged, half of that against 100 iron = 40 spent.
        Assert.Equal(60, iron.Amount);
    }

    [Fact]
    public void ASmithWithoutTheMaterialsRepairsNothing()
    {
        var world = TestHarness.CreateWorld();
        var crafting = new CraftingEngine(world);
        var recipe = new CraftRecipe { ResultDefId = 0x13BB, ResultItemId = 0x13BB };
        recipe.Resources.Add(new CraftResource { ItemId = IronIngot, Amount = 100 });
        crafting.RegisterRecipe(recipe);

        var forge = RepairBench(materials: false, crafting: crafting);

        Assert.False(ActiveSkillEngine.RepairItem(forge.Sink, forge.Broken));
        Assert.Equal(20, forge.Broken.GetHitsCur());
    }
}
