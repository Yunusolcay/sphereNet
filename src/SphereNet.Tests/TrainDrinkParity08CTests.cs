using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Practising, drinking and linking.
///
/// A pickpocket dip is a training aid, not loot: Source-X wants it on the ground within
/// a tile, refuses a mounted trainee and anyone past the practice cap, and leaves the
/// dip where it stands (Use_Train_PickPocketDip, CCharUse.cpp:397). Standing against an
/// archery butte collects what is stuck in it before anything else (:453). A torn web is
/// simply deleted (CItem.cpp:5886). Drinking runs @Drink with the delay, the amount and
/// the empty bottle, and makes the drinker drunk (Use_Drink, CCharUse.cpp:1003/1031).
/// A crystal is targeted through the item-bound cursor, which re-checks the source
/// (CClientTarg.cpp:1683).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TrainDrinkParity08CTests
{
    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack);

    private static Bench Setup(TriggerDispatcher? triggers = null, SpellEngine? spells = null)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8301);
        // The skill pipeline has to be live, or a training item routed into the ordinary
        // skill would quietly do nothing and hide what these tests are about.
        client.SetEngines(triggerDispatcher: triggers, spellEngine: spells,
            skillHandlers: new SphereNet.Game.Skills.SkillHandlers(world));

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = 100; me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);

        return new Bench(world, client, me, pack);
    }

    // --- 08C-1: the dip is practised on, not stolen ----------------------

    private static Item Dip(Bench bench, Point3D? at = null)
    {
        var dip = bench.World.CreateItem();
        dip.BaseId = 0x1EBB;
        dip.ItemType = ItemType.TrainPickpocket;
        bench.World.PlaceItem(dip, at ?? new Point3D(101, 100, 0, 0));
        return dip;
    }

    [Fact]
    public void ThePickpocketDipStaysWhereItStands()
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.Stealing, 200);
        var dip = Dip(bench);
        Character.OnSkillUseQuick = (_, _, _, _) => 1;   // a successful practice roll

        bench.Client.HandleDoubleClick(dip.Uid.Value);

        Assert.False(dip.ContainedIn.IsValid);
        Assert.DoesNotContain(dip, bench.Pack.Contents);
        Assert.False(dip.IsDeleted);
    }

    [Fact]
    public void AFixedDipIsNotCarriedOffEither()
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.Stealing, 200);
        var dip = Dip(bench);
        dip.SetAttr(ObjAttributes.Move_Never);
        Character.OnSkillUseQuick = (_, _, _, _) => 1;

        bench.Client.HandleDoubleClick(dip.Uid.Value);

        Assert.False(dip.ContainedIn.IsValid);
    }

    [Fact]
    public void ADipOutOfArmsReachTeachesNothing()
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.Stealing, 200);
        var dip = Dip(bench, new Point3D(110, 100, 0, 0));
        int rolls = 0;
        Character.OnSkillUseQuick = (_, _, _, result) => { rolls++; return result; };

        bench.Client.HandleDoubleClick(dip.Uid.Value);

        Assert.Equal(0, rolls);
    }

    [Fact]
    public void APractisedThiefIsPastTheDip()
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.Stealing, 900);     // above the practice cap
        var dip = Dip(bench);
        int rolls = 0;
        Character.OnSkillUseQuick = (_, _, _, result) => { rolls++; return result; };

        bench.Client.HandleDoubleClick(dip.Uid.Value);

        Assert.Equal(0, rolls);
    }

    // --- 08C-2: the butte gives its ammunition back ---------------------

    [Fact]
    public void AButteHandsBackWhatIsStuckInIt()
    {
        var bench = Setup();
        var butte = bench.World.CreateItem();
        butte.ItemType = ItemType.ArcheryButte;
        butte.More1 = 0x0F3F;       // arrows
        butte.More2 = 7;
        bench.World.PlaceItem(butte, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(butte.Uid.Value);

        var recovered = Assert.Single(bench.Pack.Contents, i => i.BaseId == 0x0F3F);
        Assert.Equal(7, recovered.Amount);
        Assert.Equal(0u, butte.More1);
        Assert.Equal(0u, butte.More2);
    }

    [Fact]
    public void AnEmptyButteHandsBackNothing()
    {
        var bench = Setup();
        var butte = bench.World.CreateItem();
        butte.ItemType = ItemType.ArcheryButte;
        bench.World.PlaceItem(butte, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(butte.Uid.Value);

        Assert.Empty(bench.Pack.Contents);
    }

    [Fact]
    public void AButteAcrossTheYardKeepsItsAmmunition()
    {
        var bench = Setup();
        var butte = bench.World.CreateItem();
        butte.ItemType = ItemType.ArcheryButte;
        butte.More1 = 0x0F3F;
        butte.More2 = 7;
        bench.World.PlaceItem(butte, new Point3D(110, 100, 0, 0));

        bench.Client.HandleDoubleClick(butte.Uid.Value);

        Assert.Equal(7u, butte.More2);
        Assert.Empty(bench.Pack.Contents);
    }

    // --- 08C-4 / 08C-5: drinking ----------------------------------------

    private static Item Booze(Bench bench)
    {
        var ale = bench.World.CreateItem();
        ale.BaseId = 0x099F;
        ale.ItemType = ItemType.Booze;
        ale.Amount = 3;
        Assert.True(bench.Pack.TryAddItem(ale));
        return ale;
    }

    [Fact]
    public void DrinkingRunsTheDrinkTrigger()
    {
        int calls = 0;
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "Drink", (_, args) =>
        {
            calls++;
            Assert.True(args.N1 > 0);               // the effect delay
            Assert.Equal(1, args.N2);               // one bottle
            Assert.NotNull(args.Locals);
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);
        var ale = Booze(bench);

        bench.Client.HandleDoubleClick(ale.Uid.Value);

        Assert.Equal(1, calls);
        Assert.Equal(2, ale.Amount);
    }

    [Fact]
    public void AVetoedDrinkIsNotSwallowed()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "Drink", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);
        var ale = Booze(bench);

        bench.Client.HandleDoubleClick(ale.Uid.Value);

        Assert.Equal(3, ale.Amount);
    }

    [Fact]
    public void AScriptMayMakeTheDrinkFree()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "Drink", (_, args) =>
        {
            args.N2 = 0;                            // costs nothing
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);
        var ale = Booze(bench);

        bench.Client.HandleDoubleClick(ale.Uid.Value);

        Assert.Equal(3, ale.Amount);
    }

    [Fact]
    public void DrinkingMakesTheDrinkerDrunk()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        // The drunk effect is the Liquor spell; an engine with no such definition can
        // apply nothing, so the pack's own entry is stood in for here.
        registry.Register(new SpellDef
        {
            Id = SpellType.Liquor,
            Name = "Liquor",
            DurationBase = 100,
            DurationScale = 100,
        });
        var spells = new SpellEngine(world, registry);
        var bench = SetupWith(world, spells);
        var ale = Booze(bench);

        bench.Client.HandleDoubleClick(ale.Uid.Value);

        Assert.Contains(SpellType.Liquor, RunningEffects(spells));
    }

    /// <summary>The engine keeps its running effects private; the reentrancy tests read
    /// the same field this way.</summary>
    private static List<SpellType> RunningEffects(SpellEngine engine)
    {
        var field = typeof(SpellEngine).GetField("_activeEffects",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var running = new List<SpellType>();
        foreach (var effect in (System.Collections.IEnumerable)field.GetValue(engine)!)
        {
            var spell = effect.GetType()
                .GetField("Spell", System.Reflection.BindingFlags.Public |
                                   System.Reflection.BindingFlags.Instance)
                ?.GetValue(effect)
                ?? effect.GetType().GetProperty("Spell")?.GetValue(effect);
            if (spell is SpellType type)
                running.Add(type);
        }
        return running;
    }

    private static Bench SetupWith(GameWorld world, SpellEngine spells)
    {
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8302);
        client.SetEngines(spellEngine: spells,
            skillHandlers: new SphereNet.Game.Skills.SkillHandlers(world));

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = 100; me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        return new Bench(world, client, me, pack);
    }

    // --- 08C-6: the crystal is targeted as an item ----------------------

    [Fact]
    public void ACrystalThatChangedHandsIsNotLinkedByItsOldHolder()
    {
        var bench = Setup();
        var crystal = bench.World.CreateItem();
        crystal.ItemType = ItemType.CommCrystal;
        Assert.True(bench.Pack.TryAddItem(crystal));

        var partner = bench.World.CreateItem();
        partner.ItemType = ItemType.CommCrystal;
        bench.World.PlaceItem(partner, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(crystal.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);

        // Somebody else takes the crystal while the cursor is still open.
        var other = bench.World.CreateItem();
        other.ItemType = ItemType.Container;
        bench.World.PlaceItem(other, new Point3D(102, 100, 0, 0));
        bench.Pack.RemoveItem(crystal);
        Assert.True(other.TryAddItem(crystal));

        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            partner.Uid.Value, 0, 0, 0, 0);

        Assert.False(crystal.Link.IsValid);
    }

    [Fact]
    public void ACrystalStillInHandStillLinks()
    {
        var bench = Setup();
        var crystal = bench.World.CreateItem();
        crystal.ItemType = ItemType.CommCrystal;
        Assert.True(bench.Pack.TryAddItem(crystal));

        var partner = bench.World.CreateItem();
        partner.ItemType = ItemType.CommCrystal;
        bench.World.PlaceItem(partner, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(crystal.Uid.Value);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            partner.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(partner.Uid, crystal.Link);
    }
}
