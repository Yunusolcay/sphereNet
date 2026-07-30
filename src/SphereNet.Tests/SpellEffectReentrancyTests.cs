using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 03 / C3 — spell-effect removal reentrancy. ProcessExpirations and the
/// removal helpers used to walk the live effect list by index while firing
/// removal callbacks (@EffectRemove / OnSpellEffectRemove). A callback that
/// removed other effects on the same target (the classic case: it kills the
/// target, which clears every remaining effect) shrank the list under the
/// index and the next [i] access threw ArgumentOutOfRangeException — on the
/// main tick, which rethrew and took the server down.
///
/// These drive real effects (scheduled through the private ScheduleEffectExpiry
/// so ExpireTick is controlled) and a reentrant OnSpellEffectRemove callback,
/// asserting the pass completes, every effect is retired exactly once, and stat
/// deltas are reverted exactly once (no double subtraction).
/// </summary>
[Collection("GlobalConfigSerial")]
public sealed class SpellEffectReentrancyTests
{
    private static readonly MethodInfo s_schedule =
        typeof(SpellEngine).GetMethod("ScheduleEffectExpiry", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("ScheduleEffectExpiry not found");

    private static readonly FieldInfo s_activeEffects =
        typeof(SpellEngine).GetField("_activeEffects", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_activeEffects not found");

    private static int EffectCount(SpellEngine engine) =>
        ((ICollection)s_activeEffects.GetValue(engine)!).Count;

    private static (GameWorld world, SpellEngine engine, Character caster, Character target) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var caster = world.CreateCharacter();
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Str = 50;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, new SpellRegistry());
        return (world, engine, caster, target);
    }

    // Schedule a real active effect on target with a controlled StrDelta and an
    // already-past ExpireTick so ProcessExpirations retires it deterministically.
    private static void AddEffect(SpellEngine engine, Character caster, Character target,
        SpellType spell, short strDelta)
    {
        var def = new SpellDef { Id = spell };
        var eff = s_schedule.Invoke(engine, [caster, target, spell, def, 0])!;
        eff.GetType().GetProperty("ExpireTick")!.SetValue(eff, 0L); // long past
        if (strDelta != 0)
        {
            eff.GetType().GetProperty("StrDelta")!.SetValue(eff, strDelta);
            target.Str += strDelta;
        }
    }

    [Fact]
    public void ProcessExpirations_RemovalCallbackKillsTargetAndClearsRest_CompletesSafely()
    {
        var (_, engine, caster, target) = Setup();
        AddEffect(engine, caster, target, SpellType.Clumsy, 5);
        AddEffect(engine, caster, target, SpellType.Weaken, 5);
        AddEffect(engine, caster, target, SpellType.Feeblemind, 5);
        Assert.Equal(3, EffectCount(engine));
        Assert.Equal(65, target.Str); // 50 base + 3x5

        int removed = 0;
        bool cascaded = false;
        Character.OnSpellEffectRemove = (ch, _) =>
        {
            removed++;
            // First removal "kills" the target: clear all its remaining effects
            // mid-pass — exactly the interleaving that overran the old index loop.
            if (!cascaded)
            {
                cascaded = true;
                engine.ClearAllEffectsOnDeath(ch);
            }
        };

        try
        {
            var ex = Record.Exception(() => engine.ProcessExpirations(Environment.TickCount64));
            Assert.Null(ex);                       // no ArgumentOutOfRangeException
            Assert.Equal(0, EffectCount(engine));  // every effect retired
            Assert.Equal(3, removed);              // each observed exactly once
            Assert.Equal(50, target.Str);          // reverted exactly once (no double subtraction)
        }
        finally
        {
            Character.OnSpellEffectRemove = null;
        }
    }

    [Fact]
    public void ClearAllEffectsOnDeath_ReentrantCallbackStripsRest_CompletesSafely()
    {
        var (_, engine, caster, target) = Setup();
        AddEffect(engine, caster, target, SpellType.Clumsy, 5);
        AddEffect(engine, caster, target, SpellType.Weaken, 5);
        AddEffect(engine, caster, target, SpellType.Feeblemind, 5);

        int removed = 0;
        bool cascaded = false;
        Character.OnSpellEffectRemove = (ch, _) =>
        {
            removed++;
            if (!cascaded)
            {
                cascaded = true;
                engine.StripDispellableEffects(ch); // reentrant removal of the rest
            }
        };

        try
        {
            var ex = Record.Exception(() => engine.ClearAllEffectsOnDeath(target));
            Assert.Null(ex);
            Assert.Equal(0, EffectCount(engine));
            Assert.Equal(3, removed);
            Assert.Equal(50, target.Str);
        }
        finally
        {
            Character.OnSpellEffectRemove = null;
        }
    }

    [Fact]
    public void BreakInvisibility_CallbackClearsOtherEffects_CompletesSafely()
    {
        var (_, engine, caster, target) = Setup();
        AddEffect(engine, caster, target, SpellType.Invisibility, 0);
        AddEffect(engine, caster, target, SpellType.Strength, 5);
        AddEffect(engine, caster, target, SpellType.Agility, 5);
        Assert.Equal(3, EffectCount(engine));

        bool cascaded = false;
        Character.OnSpellEffectRemove = (ch, _) =>
        {
            if (!cascaded)
            {
                cascaded = true;
                engine.ClearAllEffectsOnDeath(ch); // remove the other effects mid-pass
            }
        };

        try
        {
            var ex = Record.Exception(() => engine.BreakInvisibility(target));
            Assert.Null(ex);
            Assert.Equal(0, EffectCount(engine)); // invisibility + cascaded rest all gone
            Assert.Equal(50, target.Str);         // reverted exactly once
        }
        finally
        {
            Character.OnSpellEffectRemove = null;
        }
    }

    [Fact]
    public void ProcessExpirations_NoReentrancy_StillRetiresAllExpiredEffects()
    {
        // Baseline: the snapshot rewrite must not regress the ordinary path.
        var (_, engine, caster, target) = Setup();
        AddEffect(engine, caster, target, SpellType.Clumsy, 5);
        AddEffect(engine, caster, target, SpellType.Weaken, 5);

        int removed = 0;
        Character.OnSpellEffectRemove = (_, _) => removed++;
        try
        {
            engine.ProcessExpirations(Environment.TickCount64);
            Assert.Equal(0, EffectCount(engine));
            Assert.Equal(2, removed);
            Assert.Equal(50, target.Str);
        }
        finally
        {
            Character.OnSpellEffectRemove = null;
        }
    }
}
