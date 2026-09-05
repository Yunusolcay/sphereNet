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
/// What a cast costs must be decided once and paid once.
///
/// Three ways it was not:
/// - A scroll was exempt from the reagent check at cast start but not from the
///   consumption at completion, so carrying reagents made the same scroll cost more
///   than casting it empty-handed. Source-X gates Calc_SpellReagentsConsume on the
///   cast source being the caster (CResourceCalc.cpp:565).
/// - Completion consumed without re-checking, so removing the reagents during the
///   cast produced the spell for free. Source-X re-runs Spell_CanCast with
///   fTest=false at CastDone and fails the cast if it cannot pay (CCharSpell.cpp:3009).
/// - The search looked only at the top of the backpack, so reagents tidied into a
///   pouch read as missing. Source-X CContainer::ContentConsume recurses through
///   every searchable container (CContainer.cpp:441).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellReagentCostTests
{
    private const ushort ReagentId = 0x0F7A;   // nightshade
    private const SpellType Spell = SpellType.Strength;

    private static (GameWorld World, SpellEngine Engine, Character Caster) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var def = new SpellDef
        {
            Id = Spell,
            Name = "Strength",
            Flags = SpellFlag.TargChar | SpellFlag.Good,
            ManaCost = 4,
        };
        def.Reagents[ReagentId] = 1;

        var registry = new SpellRegistry();
        registry.Register(def);
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.Player;
        caster.Int = 100;
        caster.MaxMana = 100;
        caster.Mana = 100;
        // 120.0 Magery against a difficulty-0 spell saturates the success curve at
        // 1000/1000 while the roll is 0..999, so completion never fizzles here - the
        // outcomes below are about cost, not luck.
        caster.SetSkill(SkillType.Magery, 1200);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        caster.Backpack = pack;
        caster.Equip(pack, Layer.Pack);

        // Casting from memory needs the spell in an accessible book; the book bit
        // mask is More1/More2 (Strength is spell id 16).
        var book = world.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.More1 = 1u << ((int)Spell - 1);
        Assert.True(pack.TryAddItem(book));

        return (world, engine, caster);
    }

    private static Item AddReagents(GameWorld world, Item container, ushort amount)
    {
        var reg = world.CreateItem();
        reg.BaseId = ReagentId;
        reg.Amount = amount;
        Assert.True(container.TryAddItem(reg));
        return reg;
    }

    private static Item MakeBagIn(GameWorld world, Item parent, ItemType type = ItemType.Container)
    {
        var bag = world.CreateItem();
        bag.ItemType = type;
        Assert.True(parent.TryAddItem(bag));
        return bag;
    }

    private static int TotalReagents(GameWorld world, Character caster) =>
        world.GetContainerContentsRecursive(caster.Backpack!.Uid)
             .Where(i => !i.IsDeleted && i.BaseId == ReagentId)
             .Sum(i => Math.Max(1, (int)i.Amount));

    // --- G07: reagents in a sub-bag are reachable --------------------------

    [Fact]
    public void ReagentsInsideAPouchAreFound()
    {
        var (world, engine, caster) = Setup();
        var pouch = MakeBagIn(world, caster.Backpack!);
        AddReagents(world, pouch, 10);

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0,
            "reagents in a sub-bag must count");
    }

    [Fact]
    public void ReagentsSplitAcrossTwoPouchesAddUp()
    {
        var (world, engine, caster) = Setup();
        var a = MakeBagIn(world, caster.Backpack!);
        var b = MakeBagIn(world, caster.Backpack!);
        AddReagents(world, a, 1);
        AddReagents(world, b, 1);

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        Assert.Equal(1, TotalReagents(world, caster));
    }

    [Theory]
    [InlineData(ItemType.ContainerLocked)]
    [InlineData(ItemType.EqBankBox)]
    [InlineData(ItemType.EqVendorBox)]
    [InlineData(ItemType.EqTradeWindow)]
    public void ReagentsBehindAnUnsearchableContainerDoNotCount(ItemType type)
    {
        // Source-X CItemContainer::IsSearchable keeps the bank, a vendor box, an
        // open trade and locked chests out of a resource search.
        var (world, engine, caster) = Setup();
        var locked = MakeBagIn(world, caster.Backpack!, type);
        AddReagents(world, locked, 10);

        Assert.Equal(-1, engine.CastStart(caster, Spell, caster.Uid, caster.Position));
    }

    [Fact]
    public void NoReagentsAnywhereStillRefusesTheCast()
    {
        var (_, engine, caster) = Setup();
        Assert.Equal(-1, engine.CastStart(caster, Spell, caster.Uid, caster.Position));
    }

    // --- G06: the bill is re-checked at completion -------------------------

    [Fact]
    public void RemovingTheReagentsDuringTheCastFailsTheSpell()
    {
        var (world, engine, caster) = Setup();
        var stack = AddReagents(world, caster.Backpack!, 10);

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        short manaAfterStart = caster.Mana;

        // The player moves the reagents out mid-cast.
        caster.Backpack!.RemoveItem(stack);
        world.PlaceItemWithDecay(stack, new Point3D(120, 120, 0, 0));

        Assert.False(engine.CastDone(caster), "an unpayable cast must not succeed");
        Assert.Equal(manaAfterStart, caster.Mana);
        Assert.Equal(10, Math.Max(1, (int)stack.Amount));
    }

    [Fact]
    public void APayableCastConsumesExactlyItsBill()
    {
        var (world, engine, caster) = Setup();
        AddReagents(world, caster.Backpack!, 10);

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        Assert.Equal(9, TotalReagents(world, caster));
    }

    // --- G05: a scroll cast owes no reagents -------------------------------

    [Fact]
    public void AScrollCastDoesNotSpendReagentsTheCasterHappensToCarry()
    {
        var (world, engine, caster) = Setup();
        AddReagents(world, caster.Backpack!, 10);

        var scroll = world.CreateItem();
        scroll.ItemType = ItemType.Scroll;
        Assert.True(caster.Backpack!.TryAddItem(scroll));
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(10, TotalReagents(world, caster));
    }

    [Fact]
    public void AScrollCastWorksWithNoReagentsAtAll()
    {
        // The two paths must agree: the start check already exempted the scroll.
        var (world, engine, caster) = Setup();

        var scroll = world.CreateItem();
        scroll.ItemType = ItemType.Scroll;
        Assert.True(caster.Backpack!.TryAddItem(scroll));
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());

        Assert.True(engine.CastStart(caster, Spell, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
    }
}
