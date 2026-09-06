using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Eating, practising, springing traps and praying at a shrine.
///
/// Source-X leaves Use_EatQty before it consumes anything when the eater has no room
/// (CCharUse.cpp:889), and grain and grass go down the same Use_Eat path as any other
/// food (:1844). A training dummy is a training action in its own right, refusing a
/// mounted or ranged-armed trainee and paying out in the weapon skill actually used
/// (Use_Train_Dummy, :337). A board sets its pieces out before it opens (Game_Create,
/// CItemContainer.cpp:1123). A trap damages through OnTakeDamage, which is what carries
/// @GetHit (:1753), and a shrine resurrects through the Resurrection spell effect, which
/// @SpellEffect can refuse (CClientUse.cpp:327).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ConsumeTrainDamageParity08DTests
{
    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack);

    private static Bench Setup(TriggerDispatcher? triggers = null)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8401);
        client.SetEngines(triggerDispatcher: triggers, skillHandlers: new SkillHandlers(world));

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

    // --- 08D-1: a full player loses nothing -----------------------------

    private static Item Meal(Bench bench, ushort amount = 3, ItemType kind = ItemType.Food)
    {
        var food = bench.World.CreateItem();
        food.BaseId = 0x09D0;
        food.ItemType = kind;
        food.Amount = amount;
        Assert.True(bench.Pack.TryAddItem(food));
        return food;
    }

    [Fact]
    public void AFullPlayerDoesNotWasteFood()
    {
        var bench = Setup();
        bench.Me.Food = 60;
        var food = Meal(bench);

        bench.Client.HandleDoubleClick(food.Uid.Value);

        Assert.Equal(3, food.Amount);
    }

    [Fact]
    public void AFullPlayerDoesNotWasteTheLastOne()
    {
        var bench = Setup();
        bench.Me.Food = 60;
        var food = Meal(bench, amount: 1);

        bench.Client.HandleDoubleClick(food.Uid.Value);

        Assert.False(food.IsDeleted);
    }

    [Fact]
    public void AHungryPlayerStillEats()
    {
        var bench = Setup();
        bench.Me.Food = 0;
        var food = Meal(bench);

        bench.Client.HandleDoubleClick(food.Uid.Value);

        Assert.Equal(2, food.Amount);
        Assert.True(bench.Me.Food > 0);
    }

    // --- 08D-2: grain is food, and runs out -----------------------------

    [Fact]
    public void GrainIsEatenDownToNothing()
    {
        var bench = Setup();
        bench.Me.Food = 0;
        var hay = Meal(bench, amount: 1, kind: ItemType.Grain);

        bench.Client.HandleDoubleClick(hay.Uid.Value);

        Assert.True(hay.IsDeleted);
    }

    [Fact]
    public void AFixedGrainSourceFeedsNobody()
    {
        var bench = Setup();
        bench.Me.Food = 0;
        var hay = bench.World.CreateItem();
        hay.ItemType = ItemType.Grain;
        hay.SetAttr(ObjAttributes.Move_Never);
        bench.World.PlaceItem(hay, bench.Me.Position);

        bench.Client.HandleDoubleClick(hay.Uid.Value);

        Assert.False(hay.IsDeleted);
        Assert.Equal(0, bench.Me.Food);
    }

    // --- 08D-3: a board comes with its pieces ---------------------------

    private static Item Board(Bench bench, uint game)
    {
        var board = bench.World.CreateItem();
        board.ItemType = ItemType.GameBoard;
        board.More1 = game;
        bench.World.PlaceItem(board, bench.Me.Position);
        return board;
    }

    [Theory]
    [InlineData(0u, 32)]    // chess
    [InlineData(1u, 24)]    // checkers
    [InlineData(2u, 30)]    // backgammon
    public void AnEmptyBoardSetsItselfUp(uint game, int pieces)
    {
        var bench = Setup();
        var board = Board(bench, game);

        bench.Client.HandleDoubleClick(board.Uid.Value);

        Assert.Equal(pieces, board.Contents.Count);
        Assert.All(board.Contents, p => Assert.Equal(ItemType.GamePiece, p.ItemType));
    }

    [Fact]
    public void ABoardWithNoGameStaysEmpty()
    {
        var bench = Setup();
        var board = Board(bench, 3);

        bench.Client.HandleDoubleClick(board.Uid.Value);

        Assert.Empty(board.Contents);
    }

    [Fact]
    public void AGameInProgressIsNotSweptAway()
    {
        var bench = Setup();
        var board = Board(bench, 0);
        var lonePiece = bench.World.CreateItem();
        lonePiece.BaseId = 0x3584;
        lonePiece.ItemType = ItemType.GamePiece;
        Assert.True(board.TryAddItem(lonePiece));

        bench.Client.HandleDoubleClick(board.Uid.Value);

        Assert.Single(board.Contents);
    }

    // --- 08D-4: the dummy trains ----------------------------------------

    private static Item Dummy(Bench bench, Point3D? at = null)
    {
        var dummy = bench.World.CreateItem();
        dummy.BaseId = 0x1070;
        dummy.ItemType = ItemType.TrainDummy;
        bench.World.PlaceItem(dummy, at ?? new Point3D(101, 100, 0, 0));
        return dummy;
    }

    // Whether the dummy was actually swung at is read from the dummy itself: a swing
    // parks it in its three-second animation state, and a refusal leaves it idle. The
    // experience the reference pays out is a chance-based gain, which is no way to tell
    // the two apart.
    private static bool Swung(Item dummy) => dummy.ItemType == ItemType.AnimActive;

    [Fact]
    public void SwingingAtTheDummyTrainsIt()
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.Wrestling, 200);
        var dummy = Dummy(bench);

        bench.Client.HandleDoubleClick(dummy.Uid.Value);

        Assert.True(Swung(dummy));
    }

    [Fact]
    public void TheDummyPicksTheSkillOfTheWeaponInHand()
    {
        // Past the cap for the sword's skill, but not for wrestling: only a
        // weapon-aware choice refuses this swing.
        var bench = Setup();
        var sword = bench.World.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        bench.Me.Equip(sword, Layer.OneHanded);
        bench.Me.SetSkill(SkillType.Swordsmanship, 900);
        bench.Me.SetSkill(SkillType.Wrestling, 0);
        var dummy = Dummy(bench);

        bench.Client.HandleDoubleClick(dummy.Uid.Value);

        Assert.False(Swung(dummy));
    }

    [Fact]
    public void BareHandsTrainWrestlingOnTheDummy()
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.Wrestling, 900);   // past the cap for wrestling
        bench.Me.SetSkill(SkillType.Swordsmanship, 0);
        var dummy = Dummy(bench);

        bench.Client.HandleDoubleClick(dummy.Uid.Value);

        Assert.False(Swung(dummy));
    }

    [Fact]
    public void ABowIsNoUseOnADummy()
    {
        var bench = Setup();
        var bow = bench.World.CreateItem();
        bow.ItemType = ItemType.WeaponBow;
        bench.Me.Equip(bow, Layer.TwoHanded);
        bench.Me.SetSkill(SkillType.Archery, 200);
        var dummy = Dummy(bench);

        bench.Client.HandleDoubleClick(dummy.Uid.Value);

        Assert.False(Swung(dummy));
    }

    [Fact]
    public void ADummyAcrossTheRoomTrainsNothing()
    {
        var bench = Setup();
        bench.Me.SetSkill(SkillType.Wrestling, 200);
        var dummy = Dummy(bench, new Point3D(110, 100, 0, 0));

        bench.Client.HandleDoubleClick(dummy.Uid.Value);

        Assert.False(Swung(dummy));
    }

    // --- 08D-5: a trap damages through the damage path ------------------

    [Fact]
    public void AScriptMayRefuseTrapDamage()
    {
        var bench = Setup();
        CombatEngine.OnDirectDamage = ctx => { ctx.Cancelled = true; return 0; };

        var trap = bench.World.CreateItem();
        trap.ItemType = ItemType.Trap;
        trap.More1 = 20;
        bench.World.PlaceItem(trap, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(trap.Uid.Value);

        Assert.Equal(100, bench.Me.Hits);
    }

    [Fact]
    public void ATrapStillHurtsWhenNobodyObjects()
    {
        var bench = Setup();
        var trap = bench.World.CreateItem();
        trap.ItemType = ItemType.Trap;
        trap.More1 = 20;
        bench.World.PlaceItem(trap, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(trap.Uid.Value);

        Assert.True(bench.Me.Hits < 100);
    }

    // --- 08D-6: the shrine goes through the spell -----------------------

    [Fact]
    public void AShrineResurrectionRunsTheSpellEffect()
    {
        int calls = 0;
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "SpellEffect", (_, args) =>
        {
            calls++;
            Assert.Equal((int)SpellType.Resurrection, args.N1);
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);
        bench.Me.Kill();

        var shrine = bench.World.CreateItem();
        shrine.ItemType = ItemType.Shrine;
        bench.World.PlaceItem(shrine, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(shrine.Uid.Value);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void AVetoedSpellEffectLeavesTheGhostAGhost()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "SpellEffect", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);
        bench.Me.Kill();

        var shrine = bench.World.CreateItem();
        shrine.ItemType = ItemType.Shrine;
        bench.World.PlaceItem(shrine, new Point3D(101, 100, 0, 0));

        bench.Client.HandleDoubleClick(shrine.Uid.Value);

        Assert.True(bench.Me.IsDead);
    }
}
