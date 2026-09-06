using System;
using System.Collections.Generic;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Which jobs belong to THIS tick is decided before any callback runs (review 12Z).
///
/// Source-X selects the ready timed objects into one shared buffer first
/// (CWorldTicker.cpp:1071) and only then walks that buffer (:1129). A timed function
/// created from inside a callback goes through CTimedFunctionHandler::Add
/// (:103) and is therefore not a member of the buffer already selected, so it cannot
/// run in the same pass. Selecting each object's ready jobs only when its turn came
/// made that depend on where the target sat in the active set: a zero-delay job added
/// to an object not yet visited ran in the same pass, one added to an object already
/// visited (or to an object holding no timers, which was not in the set at all)
/// waited for the next.
///
/// This is a different failure from the due-ORDER one in 12X-1: sorting the jobs
/// already in hand does not stop new ones from joining the pass. Both are answered by
/// the same shape - fix the pass membership up front, then run it in due order.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DelayedCallParity12ZTests
{
    private static Item GroundItem(GameWorld world, int x)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D((short)x, 100, 0, 0));
        return item;
    }

    /// <summary>The four target states from the 12Z matrix. In every one of them the
    /// job added from inside a callback must wait for the NEXT pass, and must then run
    /// exactly once.</summary>
    public enum TargetState
    {
        /// <summary>Target holds an unrelated long timer and joins the active set
        /// after the object whose callback adds the work.</summary>
        ActiveVisitedLater,
        /// <summary>...and joins it before that object.</summary>
        ActiveVisitedEarlier,
        /// <summary>Target holds no timers at all, so it is not in the active set.</summary>
        NoTimers,
        /// <summary>The callback adds the work to its own object.</summary>
        SameObject,
    }

    [Theory]
    [InlineData(TargetState.ActiveVisitedLater)]
    [InlineData(TargetState.ActiveVisitedEarlier)]
    [InlineData(TargetState.NoTimers)]
    [InlineData(TargetState.SameObject)]
    public void WorkAddedFromInsideACallbackWaitsForTheNextPass(TargetState state)
    {
        var world = TestHarness.CreateWorld();

        Item source;
        Item target;
        if (state == TargetState.ActiveVisitedEarlier)
        {
            // The target registers first, so it is walked before the adder.
            target = GroundItem(world, 102);
            target.AddTimerF(60_000, "f_future", "");   // never due in this test
            source = GroundItem(world, 100);
        }
        else
        {
            source = GroundItem(world, 100);
            target = state switch
            {
                TargetState.SameObject => source,
                _ => GroundItem(world, 102),
            };
            if (state == TargetState.ActiveVisitedLater)
                target.AddTimerF(60_000, "f_future", "");
        }

        source.AddTimerF(0, "f_first", "");

        var seen = new List<string>();
        world.TimerFExpired = (_, entry) =>
        {
            seen.Add(entry.FunctionName);
            if (entry.FunctionName == "f_first")
                target.AddTimerF(0, "f_new", "");   // zero delay: due immediately
        };

        long now = Environment.TickCount64;
        TestHarness.PumpTimerF(world, now);
        Assert.Equal(["f_first"], seen);          // f_new is not part of this pass

        TestHarness.PumpTimerF(world, now + 1000);
        Assert.Equal(["f_first", "f_new"], seen); // ...and runs exactly once, next pass

        TestHarness.PumpTimerF(world, now + 2000);
        Assert.Equal(["f_first", "f_new"], seen); // never twice
    }

    [Fact]
    public void ACallbackCanStillCancelAJobThatHasNotRunYet()
    {
        // Fixing pass membership must not undo the cancellation contract: the jobs are
        // selected up front but each is removed from its object immediately before it
        // runs, so an earlier callback in the same pass can still take one away.
        var world = TestHarness.CreateWorld();
        var a = GroundItem(world, 100);
        var b = GroundItem(world, 102);
        a.AddTimerF(0, "f_first", "");
        b.AddTimerF(0, "f_victim", "");

        var seen = new List<string>();
        world.TimerFExpired = (_, entry) =>
        {
            seen.Add(entry.FunctionName);
            if (entry.FunctionName == "f_first")
                b.TryExecuteCommand("TIMERF", "CLEAR", null!);
        };

        TestHarness.PumpTimerF(world, Environment.TickCount64);

        Assert.Equal(["f_first"], seen);
    }

    [Fact]
    public void ADeletedTargetsSelectedJobDoesNotRun()
    {
        // The target is chosen for the pass and then deleted by an earlier callback.
        var world = TestHarness.CreateWorld();
        var a = GroundItem(world, 100);
        var b = GroundItem(world, 102);
        a.AddTimerF(0, "f_first", "");
        b.AddTimerF(0, "f_on_deleted", "");

        var seen = new List<string>();
        world.TimerFExpired = (_, entry) =>
        {
            seen.Add(entry.FunctionName);
            if (entry.FunctionName == "f_first")
                world.DeleteObject(b);
        };

        TestHarness.PumpTimerF(world, Environment.TickCount64);

        Assert.Equal(["f_first"], seen);
    }
}
