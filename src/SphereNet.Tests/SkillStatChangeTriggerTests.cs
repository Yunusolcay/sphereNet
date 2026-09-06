using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the newly-wired @SkillChange / @StatChange triggers. Both hang off the
// SkillEngine runtime gain hooks (OnSkillGain / OnStatGain), which fire only on a
// runtime change — NOT on load/spawn (those use the raw SetSkill / stat setters).
// The skill case drives the real engine seam (GainExperience -> OnSkillGain ->
// trigger); the stat case drives the hook contract directly because a stat gain
// cannot be forced deterministically (RNG via Random.Shared). The hooks are
// static, so they are saved/restored to avoid leaking into other tests.
public class SkillStatChangeTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void SkillGain_FiresSkillChangeTrigger_OnRuntimeGain()
    {
        TestHarness.SeedSkillAdvRates(); // gain follows ADV_RATE strictly (no curve = no gain)
        var oldHook = SkillEngine.OnSkillGain;
        try
        {
            var world = CreateWorld();
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
            player.SetSkill(SkillType.Hiding, 0);
            player.SetSkillLock(SkillType.Hiding, 0); // Up (gainable)

            var dispatcher = new TriggerDispatcher();
            long skillArg = -1, valueArg = -1;
            dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillChange",
                (_, a) => { skillArg = a.N1; valueArg = a.N2; return TriggerResult.Default; });

            // Mirror Program.EngineWiring's OnSkillGain -> @SkillChange wiring.
            SkillEngine.OnSkillGain = (ch, skill, newVal) =>
                dispatcher.FireCharTrigger(ch, CharTrigger.SkillChange,
                    new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = newVal, N3 = 1 });

            // Gain is RNG-gated; a gainable 0-skill will gain within this bound
            // with overwhelming probability (expected gains ≫ 1).
            bool gained = false;
            for (int i = 0; i < 20000 && !gained; i++)
            {
                SkillEngine.GainExperience(player, SkillType.Hiding, 50);
                gained = skillArg >= 0;
            }

            Assert.True(gained, "expected a runtime skill gain to fire @SkillChange");
            Assert.Equal((int)SkillType.Hiding, skillArg);
            Assert.True(valueArg > 0);
        }
        finally
        {
            SkillEngine.OnSkillGain = oldHook;
        }
    }

    [Fact]
    public void SetSkillRuntime_FiresCancelableSkillChange_AndModifies()
    {
        var saved = Character.OnSkillChange;
        try
        {
            var world = CreateWorld();
            var ch = world.CreateCharacter();
            ch.SetSkill(SkillType.Magery, 100);

            // No hook installed: the runtime set applies the value as-is.
            Assert.True(ch.SetSkillRuntime(SkillType.Magery, 200));
            Assert.Equal(200, ch.GetSkill(SkillType.Magery));

            // RETURN-1 veto: hook returns true → value unchanged, set reports cancelled.
            Character.OnSkillChange = (Character c, SkillType s, int oldV, ref int newV) => true;
            Assert.False(ch.SetSkillRuntime(SkillType.Magery, 500));
            Assert.Equal(200, ch.GetSkill(SkillType.Magery));

            // ARGN rewrite: hook changes the new value → the modified value lands.
            Character.OnSkillChange = (Character c, SkillType s, int oldV, ref int newV) => { newV = 333; return false; };
            Assert.True(ch.SetSkillRuntime(SkillType.Magery, 500));
            Assert.Equal(333, ch.GetSkill(SkillType.Magery));
        }
        finally
        {
            Character.OnSkillChange = saved;
        }
    }

    [Fact]
    public void StatGainHook_FiresStatChangeTrigger_WithStatIndexAndValue()
    {
        var oldHook = SkillEngine.OnStatGain;
        try
        {
            var world = CreateWorld();
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

            var dispatcher = new TriggerDispatcher();
            long statArg = -1, valueArg = -1;
            dispatcher.RegisterCharEvent("EVENTSPLAYER", "StatChange",
                (_, a) => { statArg = a.N1; valueArg = a.N2; return TriggerResult.Default; });

            // Mirror Program.EngineWiring's OnStatGain -> @StatChange wiring.
            SkillEngine.OnStatGain = (ch, statIdx, newVal) =>
                dispatcher.FireCharTrigger(ch, CharTrigger.StatChange,
                    new TriggerArgs { CharSrc = ch, N1 = statIdx, N2 = newVal, N3 = 1 });

            // The engine invokes this on a Dex gain (statIdx 1) — see
            // SkillEngine.TryStatGain. Drive the contract directly: stat gains are
            // RNG-gated and cannot be forced deterministically.
            SkillEngine.OnStatGain!.Invoke(player, 1, 55);

            Assert.Equal(1, statArg);
            Assert.Equal(55, valueArg);
        }
        finally
        {
            SkillEngine.OnStatGain = oldHook;
        }
    }
}
