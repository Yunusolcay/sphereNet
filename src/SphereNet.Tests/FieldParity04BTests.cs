using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a spell field may reach, and how many of them one step may set off.
///
/// Source-X CheckLocation (CCharAct.cpp:4928) walks the items on the tile under
/// two rules SphereNet applied to neither the walking nor the standing path:
///
///  * an item is skipped outright - before @STEP - unless its Z overlaps the
///    character's, zdiff = itemZ - charZ measured against the item's height
///    counted as at least 3, rejected when zdiff > height or zdiff &lt; -3 (:4934);
///  * at most ONE spell field may land per check (:4996), a cap the reference
///    documents as the guard against stacked Fire Fields multiplying the cost of
///    a single step and against a Paralyze+Fire stack re-freezing the victim at
///    every damage tick.
///
/// The cap follows the RESULT: fSpellHit takes the return of OnSpellEffect
/// (:5008), which is false when the effect was refused - so an inert field never
/// swallows the one behind it. That refusal includes an invulnerable target,
/// turned away at the top of the harmful branch (CCharSpell.cpp:3762) before any
/// poison, damage or paralyze is applied.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class FieldParity04BTests
{
    private const int FireDamage = 10;

    private static (GameWorld World, SpellEngine Engine) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.FireField, Name = "Fire Field",
            Flags = SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Damage | SpellFlag.Field,
        });
        registry.Register(new SpellDef
        {
            Id = SpellType.PoisonField, Name = "Poison Field",
            Flags = SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Field,
        });
        registry.Register(new SpellDef
        {
            Id = SpellType.ParalyzeField, Name = "Paralyze Field",
            Flags = SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Field,
        });
        registry.Register(new SpellDef
        {
            Id = SpellType.Paralyze, Name = "Paralyze",
            Flags = SpellFlag.Harm, DurationBase = 300,
        });
        registry.Register(new SpellDef
        {
            Id = SpellType.WallOfStone, Name = "Wall of Stone",
            Flags = SpellFlag.TargXYZ | SpellFlag.Field,
        });

        var engine = new SpellEngine(world, registry);
        Character.FieldTouchHook = engine.ApplyFieldTouch;
        return (world, engine);
    }

    private static Character Victim(GameWorld world, Point3D at)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM;        // bypasses collision/map checks only
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, at);
        return ch;
    }

    private static Item Field(GameWorld world, SpellType spell, Point3D at)
    {
        var item = world.CreateItem();
        item.SetTag("FIELD_SPELL", ((int)spell).ToString());
        if (spell == SpellType.FireField)
            item.SetTag("FIELD_DAMAGE", FireDamage.ToString());
        if (spell == SpellType.PoisonField)
            item.SetTag("FIELD_POISON", "2");
        world.PlaceItem(item, at);
        return item;
    }

    /// <summary>One step east, onto the tile the fields are on.</summary>
    private static void StepOnto(GameWorld world, Character ch) =>
        Assert.True(new MovementEngine(world).TryMove(ch, Direction.East, running: false, sequence: 1));

    private static void StandOn(Character ch) =>
        typeof(Character).GetMethod("ApplyStandingFieldDamage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(ch, null);

    // --- SX-04B-01: one spell field per location check -----------------------

    [Fact]
    public void StackedFireFieldsCostOneFieldPerStep()
    {
        var (world, _) = Setup();
        var tile = new Point3D(101, 100, 0, 0);
        for (int i = 0; i < 3; i++) Field(world, SpellType.FireField, tile);

        var victim = Victim(world, new Point3D(100, 100, 0, 0));
        StepOnto(world, victim);

        Assert.Equal(100 - FireDamage, victim.Hits);
    }

    [Fact]
    public void StackedFireFieldsCostOneFieldPerStandingTick()
    {
        var (world, _) = Setup();
        var tile = new Point3D(101, 100, 0, 0);
        for (int i = 0; i < 3; i++) Field(world, SpellType.FireField, tile);

        var victim = Victim(world, tile);
        StandOn(victim);

        Assert.Equal(100 - FireDamage, victim.Hits);
    }

    [Fact]
    public void WalkingAndStandingChargeTheSameForAStack()
    {
        // The defect was the two paths disagreeing: standing stopped after the
        // first field, walking added up every one of them.
        var (world, _) = Setup();
        var tile = new Point3D(101, 100, 0, 0);
        for (int i = 0; i < 3; i++) Field(world, SpellType.FireField, tile);

        var walker = Victim(world, new Point3D(100, 100, 0, 0));
        StepOnto(world, walker);

        var stander = Victim(world, tile);
        StandOn(stander);

        Assert.Equal(stander.Hits, walker.Hits);
    }

    [Fact]
    public void ABarrierDoesNotSwallowTheFieldBehindIt()
    {
        // A wall of stone lands no effect, so it must leave the fire field on the
        // same tile its chance - the cap counts effects, not touches.
        var (world, _) = Setup();
        var tile = new Point3D(101, 100, 0, 0);
        Field(world, SpellType.WallOfStone, tile);
        Field(world, SpellType.FireField, tile);

        var victim = Victim(world, new Point3D(100, 100, 0, 0));
        StepOnto(world, victim);

        Assert.Equal(100 - FireDamage, victim.Hits);
    }

    [Fact]
    public void ARefusedFieldDoesNotSwallowTheFieldBehindIt()
    {
        // Same rule through the immunity door: the poison field turns an
        // invulnerable target away, and the fire field behind it is then also
        // refused - but by its OWN immunity check, not by the cap. Proven by
        // giving only the second field a victim it can hurt.
        var (world, engine) = Setup();
        var tile = new Point3D(101, 100, 0, 0);
        var poison = Field(world, SpellType.PoisonField, tile);
        var fire = Field(world, SpellType.FireField, tile);

        var victim = Victim(world, tile);
        victim.SetStatFlag(StatFlag.Invul);
        Assert.Equal(FieldTouchResult.Handled, engine.ApplyFieldTouch(victim, poison));

        victim.ClearStatFlag(StatFlag.Invul);
        Assert.Equal(FieldTouchResult.SpellHit, engine.ApplyFieldTouch(victim, fire));
    }

    [Fact]
    public void ATrapOnTheSameTileStillSpringsAfterAField()
    {
        // The cap is scoped to spell fields. Source-X keeps walking the rest of
        // the tile's items, so a trap sharing it still goes off.
        var (world, _) = Setup();
        var tile = new Point3D(101, 100, 0, 0);
        Field(world, SpellType.FireField, tile);

        var trap = world.CreateItem();
        trap.ItemType = ItemType.Trap;
        trap.More2 = 7;
        world.PlaceItem(trap, tile);

        var victim = Victim(world, new Point3D(100, 100, 0, 0));
        StepOnto(world, victim);

        Assert.True(victim.Hits < 100 - FireDamage, "the trap did not spring");
    }

    // --- SX-04B-02: the touch has to reach in Z ------------------------------

    [Theory]
    [InlineData(0, true)]       // same floor
    [InlineData(-3, true)]      // the item three below still touches
    [InlineData(-4, false)]     // one lower does not
    [InlineData(3, true)]       // height counts as at least 3
    [InlineData(4, false)]      // above that, out of reach
    [InlineData(-50, false)]    // the storey below
    public void OnlyAnItemWhoseHeightOverlapsIsTouched(int itemZOffset, bool reaches)
    {
        var (world, _) = Setup();
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(101, 100, (sbyte)itemZOffset, 0));

        Assert.Equal(reaches, item.IsWithinStepHeight(0));
    }

    [Fact]
    public void AFieldOnTheFloorBelowDoesNotBurnTheStoreyAbove()
    {
        var (world, _) = Setup();
        Field(world, SpellType.FireField, new Point3D(101, 100, 0, 0));

        var victim = Victim(world, new Point3D(100, 100, 50, 0));
        StepOnto(world, victim);

        Assert.Equal(100, victim.Hits);
    }

    [Fact]
    public void AFieldOnTheFloorBelowDoesNotBurnAStandingCharacter()
    {
        var (world, _) = Setup();
        Field(world, SpellType.FireField, new Point3D(101, 100, 0, 0));

        var victim = Victim(world, new Point3D(101, 100, 50, 0));
        StandOn(victim);

        Assert.Equal(100, victim.Hits);
    }

    [Fact]
    public void AFieldOnTheSameFloorStillBurns()
    {
        // The other side of the height gate: it must not silence ordinary fields.
        var (world, _) = Setup();
        Field(world, SpellType.FireField, new Point3D(101, 100, 0, 0));

        var victim = Victim(world, new Point3D(100, 100, 0, 0));
        StepOnto(world, victim);

        Assert.Equal(100 - FireDamage, victim.Hits);
    }

    // --- SX-04B-03: an invulnerable target refuses a harmful field -----------

    [Fact]
    public void AnInvulnerableCharacterIsNotPoisonedByAField()
    {
        // The tick damage was already zeroed for Invul, but the poison itself was
        // accepted - flag, timer, source UID and the client status notification.
        var (world, engine) = Setup();
        var field = Field(world, SpellType.PoisonField, new Point3D(101, 100, 0, 0));

        var victim = Victim(world, field.Position);
        victim.SetStatFlag(StatFlag.Invul);

        Assert.Equal(FieldTouchResult.Handled, engine.ApplyFieldTouch(victim, field));
        Assert.False(victim.IsPoisoned);
    }

    [Fact]
    public void AnInvulnerableCharacterIsNotFrozenByAParalyzeField()
    {
        var (world, engine) = Setup();
        var field = Field(world, SpellType.ParalyzeField, new Point3D(101, 100, 0, 0));

        var victim = Victim(world, field.Position);
        victim.SetStatFlag(StatFlag.Invul);

        Assert.Equal(FieldTouchResult.Handled, engine.ApplyFieldTouch(victim, field));
        Assert.False(victim.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void AnInvulnerableCharacterTakesNoFireFieldDamage()
    {
        var (world, engine) = Setup();
        var field = Field(world, SpellType.FireField, new Point3D(101, 100, 0, 0));

        var victim = Victim(world, field.Position);
        victim.SetStatFlag(StatFlag.Invul);

        Assert.Equal(FieldTouchResult.Handled, engine.ApplyFieldTouch(victim, field));
        Assert.Equal(100, victim.Hits);
    }

    [Fact]
    public void AnOrdinaryCharacterIsStillPoisonedAndFrozen()
    {
        var (world, engine) = Setup();
        var poison = Field(world, SpellType.PoisonField, new Point3D(101, 100, 0, 0));
        var paralyze = Field(world, SpellType.ParalyzeField, new Point3D(102, 100, 0, 0));

        var victim = Victim(world, poison.Position);
        Assert.Equal(FieldTouchResult.SpellHit, engine.ApplyFieldTouch(victim, poison));
        Assert.True(victim.IsPoisoned);

        Assert.Equal(FieldTouchResult.SpellHit, engine.ApplyFieldTouch(victim, paralyze));
        Assert.True(victim.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void PoisonAlreadyRunningWhenInvulnerabilityArrivesStillDealsNoDamage()
    {
        // The existing tick-side protection is not what this round changed, and
        // must stay: an already-poisoned character who becomes invulnerable keeps
        // the poison but takes nothing from it.
        var (world, engine) = Setup();
        var field = Field(world, SpellType.PoisonField, new Point3D(101, 100, 0, 0));

        var victim = Victim(world, field.Position);
        Assert.Equal(FieldTouchResult.SpellHit, engine.ApplyFieldTouch(victim, field));
        Assert.True(victim.IsPoisoned);

        victim.SetStatFlag(StatFlag.Invul);
        for (int i = 0; i < 10; i++)
            victim.ProcessPoisonTick(Environment.TickCount64 + (i * 5000));

        Assert.Equal(100, victim.Hits);
    }
}
