using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A cast that has already been paid for is the wrong place to discover that the
/// scroll is gone or the target is dead.
///
/// Source-X re-resolves the cast SOURCE at completion - m_Act_Prv_UID.ObjFind(),
/// CCharSpell.cpp:2882 - and hands that same resolved pointer to Spell_CanCast
/// (:3010), which refuses a null source (:2330) and one whose top-level owner is not
/// the caster (:2422). SphereNet decided "this is a scroll cast" from the TAG alone,
/// so a scroll destroyed or handed to another player during the cast time still
/// bought the half-mana, no-reagent discount, and the consumption then took the
/// scroll out of whoever now held it.
///
/// Source-X also opens Spell_CastDone with Spell_TargCheck (:2878) and only reaches
/// the reagent/mana/charge consumption 130 lines later (:3010). SphereNet re-resolved
/// the target AFTER the costs were taken and reported success anyway, so a target
/// that died or walked away during the cast time was charged for in full.
///
/// The failure is priced as an ABORT, not refunded: CCharSkill.cpp:3000 turns a false
/// return into SKTRIG_ABORT and Spell_CastFail(fAbort = true) applies MANALOSSABORT /
/// REAGENTLOSSABORT (CCharSpell.cpp:3316). An unconditional refund would be its own
/// deviation.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellParity04ATests
{
    private const SpellType Spell = SpellType.Strength;

    private static (GameWorld World, SpellEngine Engine, Character Caster) Setup(int manaCost = 40)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = Spell,
            Name = "Strength",
            Flags = SpellFlag.TargChar | SpellFlag.Good,
            ManaCost = (ushort)manaCost,
        });
        var engine = new SpellEngine(world, registry);

        var caster = MakeChar(world, 100);
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        // Saturates the success curve so the outcomes below are about rules, not luck.
        caster.SetSkill(SkillType.Magery, 1200);

        // Casting from memory needs the spell in an accessible book; the book bit
        // mask is More1/More2.
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.More1 = 1u << ((int)Spell - 1);
        Assert.True(caster.Backpack!.TryAddItem(book));

        return (world, engine, caster);
    }

    private static Character MakeChar(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.Int = 100; ch.MaxMana = 100; ch.Mana = 100;
        ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    private static Item ScrollIn(GameWorld world, Item container)
    {
        var scroll = world.CreateItem();
        scroll.ItemType = ItemType.Scroll;
        scroll.More1 = (uint)Spell;
        Assert.True(container.TryAddItem(scroll));
        return scroll;
    }

    // --- SX-04A-01: the source is re-resolved, not assumed --------------------

    [Fact]
    public void AScrollDestroyedDuringTheCastDoesNotStillPayForIt()
    {
        var (world, engine, caster) = Setup();
        var scroll = ScrollIn(world, caster.Backpack!);
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        world.RemoveItem(scroll);

        // Previously: the tag alone said "scroll", so this completed at half mana.
        Assert.False(engine.CastDone(caster));
        Assert.False(caster.TryGetTag("SCROLL_UID", out _));
    }

    [Fact]
    public void AScrollHandedToSomeoneElseIsNotConsumedFromTheirPack()
    {
        var (world, engine, caster) = Setup();
        var thief = MakeChar(world, 102);
        var scroll = ScrollIn(world, caster.Backpack!);
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);

        caster.Backpack!.RemoveItem(scroll);
        Assert.True(thief.Backpack!.TryAddItem(scroll));

        Assert.False(engine.CastDone(caster));
        Assert.False(scroll.IsDeleted);
        Assert.Equal(thief.Backpack!.Uid, scroll.ContainedIn);
    }

    [Fact]
    public void AScrollStillOnThePersonCastsAndIsConsumed()
    {
        // The other side of the gate: nesting it deeper in the pack is still "on
        // your person", because the reference tests the TOP-LEVEL owner.
        var (world, engine, caster) = Setup();
        var pouch = world.CreateItem();
        pouch.ItemType = ItemType.Container;
        Assert.True(caster.Backpack!.TryAddItem(pouch));
        var scroll = ScrollIn(world, pouch);
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.True(scroll.IsDeleted);
        Assert.Equal(80, caster.Mana);      // half of 40
        Assert.False(caster.TryGetTag("SCROLL_UID", out _));
    }

    [Fact]
    public void AScrollDroppedOnTheGroundIsNoLongerACastSource()
    {
        var (world, engine, caster) = Setup();
        var scroll = ScrollIn(world, caster.Backpack!);
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        caster.Backpack!.RemoveItem(scroll);
        world.PlaceItem(scroll, caster.Position);

        Assert.False(engine.CastDone(caster));
        Assert.False(scroll.IsDeleted);
    }

    [Fact]
    public void AWandMerelyEquippedMidCastDoesNotMakeAPlayerSpellFree()
    {
        // A player's source is what they ACTIVATED, never what they happen to hold -
        // reading the equipment at completion let a wand swapped in during the cast
        // time buy wand pricing for an ordinary spell.
        var (world, engine, caster) = Setup();
        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);

        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        caster.Equip(wand, Layer.OneHanded);

        Assert.True(engine.CastDone(caster));
        Assert.Equal(60, caster.Mana);      // full 40, not free
    }

    [Fact]
    public void AnNpcCastingWithAWieldedWandKeepsWandPricing()
    {
        // NPCs never tag a source - their AI hands them the wand - so the wielded
        // reading is preserved for them alone.
        var (world, engine, caster) = Setup();
        caster.IsPlayer = false;

        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        caster.Equip(wand, Layer.OneHanded);

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        Assert.Equal(100, caster.Mana);
    }

    // --- SX-04A-02: the target is checked before anything is paid -------------

    [Fact]
    public void ATargetThatDiedDuringTheCastCostsTheAbortPriceNotTheFullOne()
    {
        var (world, engine, caster) = Setup();
        var victim = MakeChar(world, 101);

        bool saved = Character.ManaLossAbort;
        try
        {
            Character.ManaLossAbort = false;
            Assert.True(engine.CastStart(caster, Spell, victim.Uid, victim.Position) >= 0);
            victim.SetStatFlag(StatFlag.Dead);

            Assert.False(engine.CastDone(caster));
            Assert.Equal(100, caster.Mana);
        }
        finally { Character.ManaLossAbort = saved; }
    }

    [Fact]
    public void TheAbortPriceIsStillChargedWhenTheConfigurationAsksForIt()
    {
        // Source-X is NOT zero-loss here: MANALOSSABORT governs it. Failing the cast
        // must not turn into a free retry either.
        var (world, engine, caster) = Setup();
        var victim = MakeChar(world, 101);

        bool saved = Character.ManaLossAbort;
        try
        {
            Character.ManaLossAbort = true;
            Assert.True(engine.CastStart(caster, Spell, victim.Uid, victim.Position) >= 0);
            victim.SetStatFlag(StatFlag.Dead);

            Assert.False(engine.CastDone(caster));
            Assert.True(caster.Mana < 100);
        }
        finally { Character.ManaLossAbort = saved; }
    }

    [Fact]
    public void ATargetThatWalkedOutOfRangeDoesNotConsumeTheScroll()
    {
        var (world, engine, caster) = Setup();
        var victim = MakeChar(world, 101);
        var scroll = ScrollIn(world, caster.Backpack!);
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());

        Assert.True(engine.CastStart(caster, Spell, victim.Uid, victim.Position) >= 0);
        world.MoveCharacter(victim, new Point3D(400, 400, 0, 0));

        Assert.False(engine.CastDone(caster));
        Assert.False(scroll.IsDeleted);
        Assert.False(caster.TryGetTag("SCROLL_UID", out _));
    }

    [Fact]
    public void ATargetThatVanishedDuringTheCastFailsTheSpell()
    {
        var (world, engine, caster) = Setup();
        var victim = MakeChar(world, 101);

        Assert.True(engine.CastStart(caster, Spell, victim.Uid, victim.Position) >= 0);
        world.DeleteObject(victim);

        Assert.False(engine.CastDone(caster));
    }

    [Fact]
    public void AValidTargetStillResolvesNormally()
    {
        var (world, engine, caster) = Setup();
        var victim = MakeChar(world, 101);

        Assert.True(engine.CastStart(caster, Spell, victim.Uid, victim.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        Assert.Equal(60, caster.Mana);
    }

    [Fact]
    public void AFailedCompletionReportsFailureToTheResolutionHook()
    {
        // @SpellSuccess vs @SpellFail hangs off this return value.
        var (world, engine, caster) = Setup();
        var victim = MakeChar(world, 101);

        bool? observed = null;
        engine.OnCastResolved = (_, _, ok) => observed = ok;

        Assert.True(engine.CastStart(caster, Spell, victim.Uid, victim.Position) >= 0);
        victim.SetStatFlag(StatFlag.Dead);
        engine.CastDone(caster);

        Assert.Equal(false, observed);
    }
}
