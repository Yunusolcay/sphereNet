using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The item timer: one clock, one gate, and what survives a save or a split.
///
/// Source-X runs @Timer FIRST and a RETURN 1 ends the tick before components, the type
/// switch and the corpse branch alike (CItem.cpp:6217); a RETURN 0 deletes, and without
/// ATTR_DECAY or that explicit refusal the item is kept (:6412). Decay is not a second
/// clock - TIMER sets the one timeout (CObjBase.cpp:1978) - and a script that vetoes is
/// given no replacement interval (:6222). TIMERMS takes a negative to clear and zero to
/// mean now, TIMERD counts tenths of a second (:2033/:2040 -> CTimedObject.cpp:57), a set
/// timer is written to the save even once elapsed (:2081 -> :123), and DupeCopy carries
/// the remaining timeout onto a split piece (CItem.cpp:4099).
/// </summary>
public sealed class ItemTimerParity12QRTests
{
    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Ground(GameWorld world, ItemType type = ItemType.Normal)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        item.ItemType = type;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        return item;
    }

    private static IDisposable Handler(Func<Item, TriggerResult?> handler)
    {
        Item.OnTimerExpired = handler;
        return new Unhook();
    }

    private sealed class Unhook : IDisposable
    {
        public void Dispose() { Item.OnTimerExpired = null; Item.OnCorpseDecay = null; }
    }

    // ================================================================ 12Q-1

    [Fact]
    public void AScriptCanStopTheTickBeforeTheTypeBehaviourRuns()
    {
        var world = NewWorld();
        var hive = Ground(world, ItemType.BeeHive);
        hive.More1 = 1;                       // one unit of honey so far
        hive.SetTimeout(Environment.TickCount64 - 1);

        using var _ = Handler(_ => TriggerResult.True);
        Assert.True(hive.OnTick());

        Assert.Equal(1u, hive.More1);         // the hive did NOT refill
    }

    [Fact]
    public void WithNoObjectionTheTypeBehaviourStillRuns()
    {
        var world = NewWorld();
        var hive = Ground(world, ItemType.BeeHive);
        hive.More1 = 1;
        hive.SetTimeout(Environment.TickCount64 - 1);

        using var _ = Handler(_ => TriggerResult.Default);
        Assert.True(hive.OnTick());

        Assert.Equal(2u, hive.More1);
    }

    [Fact]
    public void AScriptCanAskForAnOrdinaryItemToBeDestroyed()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetTimeout(Environment.TickCount64 - 1);

        using var _ = Handler(_ => TriggerResult.False);

        Assert.False(item.OnTick());
        Assert.True(item.IsDeleted);
    }

    // ================================================================ 12Q-5

    [Fact]
    public void AnItemThatDoesNotDecayIsNotDestroyedByItsOwnTimer()
    {
        var world = NewWorld();
        var item = Ground(world);
        Assert.False(item.IsAttr(ObjAttributes.Decay));

        Assert.True(item.TrySetProperty("TIMER", "1"));
        // Setting a script timer grants no decay clock at all, so there is nothing to
        // delete the item when the timer comes due.
        Assert.Equal(0, item.DecayTime);
        item.SetTimeout(Environment.TickCount64 - 1);

        Assert.True(item.OnTick());
        Assert.False(item.IsDeleted);
    }

    [Fact]
    public void GivingAScriptTimerDoesNotMakeAnItemPerishable()
    {
        var world = NewWorld();
        var item = Ground(world);

        Assert.True(item.TrySetProperty("TIMER", "60"));

        Assert.Equal(0, item.DecayTime);
    }

    [Fact]
    public void AnItemThatAlreadyDecaysKeepsTheMirroredClock()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetAttr(ObjAttributes.Decay);

        Assert.True(item.TrySetProperty("TIMER", "60"));

        Assert.True(item.DecayTime > Environment.TickCount64);
    }

    // ================================================================ 12Q-2

    [Fact]
    public void TurningTheTimerOffStopsTheDecayToo()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetAttr(ObjAttributes.Decay);
        item.DecayTime = Environment.TickCount64 - 1;

        Assert.True(item.TrySetProperty("TIMER", "-1"));

        Assert.True(item.OnTick());
        Assert.False(item.IsDeleted);
    }

    [Fact]
    public void AnItemLeftAloneStillDecays()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetAttr(ObjAttributes.Decay);
        item.DecayTime = Environment.TickCount64 - 1;

        Assert.False(item.OnTick());
        Assert.True(item.IsDeleted);
    }

    // ================================================================ 12Q-3

    [Fact]
    public void AVetoKeepsTheIntervalTheScriptChose()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetAttr(ObjAttributes.Decay);
        item.DecayTime = Environment.TickCount64 - 1;

        using var _ = Handler(i =>
        {
            i.TrySetProperty("TIMER", "111");
            return TriggerResult.True;
        });

        Assert.True(item.OnTick());
        Assert.False(item.IsDeleted);
        long remaining = item.Timeout - Environment.TickCount64;
        Assert.InRange(remaining, 100_000, 112_000);
        // The two clocks agree instead of the decay running off on its own default.
        Assert.Equal(item.Timeout, item.DecayTime);
    }

    // ================================================================ 12Q-4

    [Fact]
    public void ACorpseCanBeKeptByItsOwnTimerScript()
    {
        var world = NewWorld();
        var corpse = Ground(world, ItemType.Corpse);
        corpse.DecayTime = Environment.TickCount64 - 1;
        int scattered = 0;
        Item.OnCorpseDecay = _ => { scattered++; return true; };

        using var _ = Handler(_ => TriggerResult.True);
        Assert.True(corpse.OnTick());

        Assert.Equal(0, scattered);
        Assert.False(corpse.IsDeleted);
    }

    [Fact]
    public void AnUnclaimedCorpseStillRots()
    {
        var world = NewWorld();
        var corpse = Ground(world, ItemType.Corpse);
        corpse.DecayTime = Environment.TickCount64 - 1;
        int scattered = 0;
        Item.OnCorpseDecay = _ => { scattered++; return true; };

        using var _ = Handler(_ => TriggerResult.Default);
        Assert.False(corpse.OnTick());

        Assert.Equal(1, scattered);
        Assert.True(corpse.IsDeleted);
    }

    // ================================================================ 12R-2

    [Fact]
    public void AMillisecondTimerCanBeClearedAndFiredAtOnce()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetTimeout(Environment.TickCount64 + 60_000);

        Assert.True(item.TrySetProperty("TIMERMS", "-1"));
        Assert.Equal(0, item.Timeout);

        item.SetTimeout(Environment.TickCount64 + 60_000);
        Assert.True(item.TrySetProperty("TIMERMS", "0"));
        Assert.InRange(item.Timeout - Environment.TickCount64, -50, 50);
    }

    // ================================================================ 12R-3

    [Fact]
    public void TenthsOfASecondAreUnderstood()
    {
        var world = NewWorld();
        var item = Ground(world);

        Assert.True(item.TrySetProperty("TIMERD", "10"));

        Assert.InRange(item.Timeout - Environment.TickCount64, 900, 1_100);
    }

    [Fact]
    public void ANegativeTenthsValueClearsTheTimer()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetTimeout(Environment.TickCount64 + 60_000);

        Assert.True(item.TrySetProperty("TIMERD", "-1"));

        Assert.Equal(0, item.Timeout);
    }

    // ================================================================ 12R-1

    [Fact]
    public void ATimerThatCameDueBeforeTheSaveStillFiresAfterTheLoad()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetTimeout(Environment.TickCount64 - 1_000);   // due, not yet ticked

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_tmr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, dir);

            var reloaded = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, dir);
            var back = reloaded.FindItem(item.Uid)!;

            int fired = 0;
            using var _ = Handler(_ => { fired++; return TriggerResult.Default; });
            back.OnTick();

            Assert.Equal(1, fired);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnItemWithNoTimerDoesNotGainOne()
    {
        var world = NewWorld();
        var item = Ground(world);
        Assert.Equal(0, item.Timeout);

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_tmr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, dir);

            var reloaded = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, dir);
            var back = reloaded.FindItem(item.Uid)!;

            int fired = 0;
            using var _ = Handler(_ => { fired++; return TriggerResult.Default; });
            back.OnTick();

            Assert.Equal(0, fired);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================ 12R-4

    [Fact]
    public void BothHalvesOfASplitKeepTheTimer()
    {
        var world = NewWorld();
        var stack = Ground(world);
        long due = Environment.TickCount64 + 60_000;
        stack.SetTimeout(due);

        var half = world.CreateItem();
        half.CopyStackInstanceStateFrom(stack);

        Assert.Equal(due, half.Timeout);
    }

    // ================================================================ 12R-5

    [Fact]
    public void ATimerArmedOnTheGroundStillRunsAfterTheItemIsBagged()
    {
        var world = NewWorld();
        var bag = Ground(world, ItemType.Container);
        var item = Ground(world);
        // Armed FIRST, moved SECOND - the order that used to lose the timer.
        item.SetTimeout(Environment.TickCount64 - 1);
        bag.AddItem(item);

        int fired = 0;
        using var _ = Handler(_ => { fired++; return TriggerResult.Default; });
        typeof(GameWorld).GetMethod("TickOffGroundTimers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(world, [Environment.TickCount64]);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void ATimerArmedInsideTheBagStillRuns()
    {
        var world = NewWorld();
        var bag = Ground(world, ItemType.Container);
        var item = Ground(world);
        bag.AddItem(item);
        item.SetTimeout(Environment.TickCount64 - 1);

        int fired = 0;
        using var _ = Handler(_ => { fired++; return TriggerResult.Default; });
        typeof(GameWorld).GetMethod("TickOffGroundTimers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(world, [Environment.TickCount64]);

        Assert.Equal(1, fired);
    }
}
