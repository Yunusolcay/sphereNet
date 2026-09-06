using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X spell parity:
//  #6a wand-charge / scroll consumption moved off the double-click / client
//      success path into SpellEngine.CastDone, so a charge/scroll is spent only
//      when the cast commits to success (never lost to fizzle/interrupt/cancel)
//      and NPC/precast casts consume correctly too.
//  #3  a single OnCastResolved engine hook fires @SpellSuccess/@SpellEffect/
//      @SpellFail for ANY caster.
//  #4  [SPELL] ON=@Trigger blocks dispatch via TriggerDispatcher.FireSpellTrigger.
public class SpellCastSourceTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (SpellEngine engine, Character caster) Setup(GameWorld world)
    {
        var registry = new SpellRegistry();
        // A self-buff spell (no target flags) so CastDone resolves to a benign
        // self-effect; GM caster makes fizzle/LOS deterministic.
        registry.Register(new SpellDef
        {
            Id = SpellType.Strength,
            Name = "Strength",
            Flags = SpellFlag.Good,
            ManaCost = 0,
            CastTimeBase = 1,
        });
        var engine = new SpellEngine(world, registry);
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        // A wand or scroll must be ON the caster to pay for a cast (Source-X
        // Spell_CanCast, CCharSpell.cpp:2422), so these fixtures need somewhere for
        // the source to live rather than leaving it floating unowned.
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        caster.Backpack = pack;
        caster.Equip(pack, Layer.Pack);

        return (engine, caster);
    }

    [Fact]
    public void Wand_ChargeConsumed_OnlyOnSuccessfulCast()
    {
        var world = CreateWorld();
        var (engine, caster) = Setup(world);

        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        wand.More1 = (uint)SpellType.Strength;
        wand.SetTag("CHARGES", "3");

        Assert.True(caster.Backpack!.TryAddItem(wand));
        caster.SetTag("WAND_UID", wand.Uid.Value.ToString());
        caster.BeginCast(SpellType.Strength, caster.Uid, caster.Position);

        Assert.True(engine.CastDone(caster));

        Assert.True(wand.TryGetTag("CHARGES", out string? ch));
        Assert.Equal("2", ch);                              // exactly one charge spent
        Assert.False(caster.TryGetTag("WAND_UID", out _));  // source tag cleared
    }

    [Fact]
    public void Wand_ChargeNotConsumed_OnInterruptedCast()
    {
        var world = CreateWorld();
        var (engine, caster) = Setup(world);
        caster.PrivLevel = PrivLevel.Player; // GM is never interrupted by movement

        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        wand.More1 = (uint)SpellType.Strength;
        wand.SetTag("CHARGES", "3");

        Assert.True(caster.Backpack!.TryAddItem(wand));
        caster.SetTag("WAND_UID", wand.Uid.Value.ToString());
        caster.BeginCast(SpellType.Strength, caster.Uid, caster.Position);

        Assert.True(engine.TryInterruptFromMovement(caster));

        Assert.True(wand.TryGetTag("CHARGES", out string? ch));
        Assert.Equal("3", ch);                              // untouched — bug fix
        Assert.False(caster.TryGetTag("WAND_UID", out _));  // tag cleared, no leak
    }

    [Fact]
    public void Scroll_Consumed_OnSuccessfulCast()
    {
        var world = CreateWorld();
        var (engine, caster) = Setup(world);

        var scroll = world.CreateItem();
        scroll.ItemType = ItemType.Scroll;
        scroll.Amount = 1;

        Assert.True(caster.Backpack!.TryAddItem(scroll));
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());
        caster.BeginCast(SpellType.Strength, caster.Uid, caster.Position);

        Assert.True(engine.CastDone(caster));

        Assert.True(scroll.IsDeleted);                       // last scroll consumed
        Assert.False(caster.TryGetTag("SCROLL_UID", out _));
    }

    [Fact]
    public void OnCastResolved_Fires_WithResolvedSpellAndSuccess()
    {
        var world = CreateWorld();
        var (engine, caster) = Setup(world);

        SpellType? firedSpell = null;
        bool? firedSuccess = null;
        engine.OnCastResolved = (_, s, ok) => { firedSpell = s; firedSuccess = ok; };

        caster.BeginCast(SpellType.Strength, caster.Uid, caster.Position);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(SpellType.Strength, firedSpell);
        Assert.True(firedSuccess);
    }

    [Fact]
    public void FireSpellTrigger_RunsSpellDefOnBlock()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet-spelltrig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "spell.scp");
        File.WriteAllText(path, "[SPELL 1]\nNAME=Clumsy\nON=@Fail\nRETURN 1\n");

        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(path);

        var world = CreateWorld();
        var ch = world.CreateCharacter();

        // The [SPELL 1] ON=@Fail body runs and RETURN 1 surfaces as TriggerResult.True.
        var result = stack.Dispatcher.FireSpellTrigger((SpellType)1, "Fail", ch,
            new TriggerArgs { CharSrc = ch });
        Assert.Equal(TriggerResult.True, result);

        // A trigger with no ON= block is a no-op (not True), never a crash.
        var none = stack.Dispatcher.FireSpellTrigger((SpellType)1, "Success", ch,
            new TriggerArgs { CharSrc = ch });
        Assert.NotEqual(TriggerResult.True, none);
    }
}
