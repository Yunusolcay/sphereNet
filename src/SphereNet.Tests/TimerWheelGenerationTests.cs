using System.Linq;
using SphereNet.Game.Scheduling;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Cancellation and long timers.
///
/// Remove() cannot reach into a slot, so a cancelled entry stayed there. The UID
/// went back into the scheduled set on the next Schedule, and the leftover entry
/// then consumed that scheduling: the NPC fired at the OLD time and never at the
/// new one. Program.Tick.WakeNpc does exactly Remove+Schedule.
///
/// Separately, a slot index aliases every 25.6s, so anything scheduled past one
/// revolution fired on the aliased slot. NpcAI parks idle NPCs 30-60s out
/// (NpcAI.cs), which meant every idle NPC woke roughly 2.3x too often.
/// </summary>
public sealed class TimerWheelGenerationTests
{
    [Fact]
    public void RescheduleAfterRemove_FiresAtTheNewTimeOnly()
    {
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        wheel.Schedule(npc, 100);
        wheel.Remove(npc);
        wheel.Schedule(npc, 500);

        // The cancelled 100ms entry is still sitting in its slot; it must not fire.
        Assert.DoesNotContain(npc, wheel.Advance(100));

        // The new scheduling is intact and fires at its own deadline.
        Assert.Contains(npc, wheel.Advance(500));
        Assert.Equal(0, wheel.Count);
    }

    [Fact]
    public void RescheduleAfterRemove_ToAnEarlierTime_FiresThere()
    {
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        wheel.Schedule(npc, 2_000);
        wheel.Remove(npc);
        wheel.Schedule(npc, 300);

        Assert.Contains(npc, wheel.Advance(300));

        // ...and the abandoned far entry never fires afterwards.
        Assert.DoesNotContain(npc, wheel.Advance(2_000));
    }

    [Fact]
    public void RemoveWithoutReschedule_NeverFires()
    {
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        wheel.Schedule(npc, 100);
        wheel.Remove(npc);
        Assert.Equal(0, wheel.Count);

        Assert.DoesNotContain(npc, wheel.Advance(100));
        Assert.DoesNotContain(npc, wheel.Advance(30_000));
    }

    [Theory]
    [InlineData(30_000)]   // NpcAI's idle floor
    [InlineData(45_000)]
    [InlineData(60_000)]   // NpcAI's idle ceiling
    [InlineData(25_600)]   // exactly one revolution
    [InlineData(25_700)]   // one revolution plus one slot
    public void ATimerBeyondOneRevolution_DoesNotFireOnTheAliasedSlot(long fireAt)
    {
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        wheel.Schedule(npc, fireAt);

        // Walk the wheel in small steps up to just before the deadline. The
        // aliased slot is crossed at least once on the way.
        for (long t = 100; t < fireAt; t += 100)
            Assert.DoesNotContain(npc, wheel.Advance(t));

        Assert.Contains(npc, wheel.Advance(fireAt));
    }

    [Fact]
    public void ALongTimerSurvivesACoarseAdvanceJump()
    {
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        wheel.Schedule(npc, 30_000);

        // A single jump past the aliased slot but short of the deadline.
        Assert.DoesNotContain(npc, wheel.Advance(4_400));
        Assert.Equal(1, wheel.Count);

        // ...and one jump straight past the deadline still delivers it.
        Assert.Contains(npc, wheel.Advance(31_000));
    }

    [Fact]
    public void ManyNpcsWithMixedHorizons_EachFireExactlyOnceAtTheirOwnDeadline()
    {
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);

        var deadlines = new Dictionary<SphereNet.Game.Objects.Characters.Character, long>();
        for (int i = 0; i < 60; i++)
        {
            var npc = world.CreateCharacter();
            long at = 200 + (i * 997);      // spreads across several revolutions
            deadlines[npc] = at;
            wheel.Schedule(npc, at);
        }

        var firedAt = new Dictionary<SphereNet.Game.Objects.Characters.Character, long>();
        for (long t = 100; t <= 70_000; t += 100)
        {
            foreach (var npc in wheel.Advance(t))
            {
                Assert.False(firedAt.ContainsKey(npc), "an NPC fired twice");
                firedAt[npc] = t;
            }
        }

        var missing = deadlines.Where(d => !firedAt.ContainsKey(d.Key)).Select(d => d.Value).ToList();
        Assert.True(firedAt.Count == deadlines.Count,
            $"never fired: [{string.Join(", ", missing)}]");
        foreach (var (npc, expected) in deadlines)
        {
            // Fires on the first tick at or after its deadline, never before it.
            Assert.True(firedAt[npc] >= expected,
                $"fired at {firedAt[npc]} but was due at {expected}");
            Assert.True(firedAt[npc] - expected < 200,
                $"fired at {firedAt[npc]}, far past its {expected} deadline");
        }
        Assert.Equal(0, wheel.Count);
    }

    [Fact]
    public void RepeatedRemoveAndScheduleDoesNotLeakScheduledEntries()
    {
        var world = TestHarness.CreateWorld();
        var wheel = new TimerWheel(0);
        var npc = world.CreateCharacter();

        // Models an NPC being woken over and over by aggro/interaction.
        for (int i = 0; i < 50; i++)
        {
            wheel.Remove(npc);
            wheel.Schedule(npc, 100 + (i * 10));
            Assert.Equal(1, wheel.Count);
        }

        Assert.Contains(npc, wheel.Advance(1_000));
        Assert.Equal(0, wheel.Count);
        Assert.DoesNotContain(npc, wheel.Advance(60_000));
    }
}
