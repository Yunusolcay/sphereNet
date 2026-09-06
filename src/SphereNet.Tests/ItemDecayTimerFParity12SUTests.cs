using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Decay ownership, the drop contract and delayed script work.
///
/// Source-X keeps ONE clock: SetDecayTime clears with -1, arms with a positive value and
/// stands aside when a non-decay timer is already running (CItem.cpp:1478/1485). A pickup
/// ends the rot (CCharAct.cpp:3064). The drop event is handed the decay in tenths of a
/// second and the point as a string, reads both back, stops if the script deleted the
/// item, leaves a container the script chose, and treats RETURN 1 as success
/// (CItem.cpp:1629/1634/1654/1660); a region's protection shapes the NATURAL time before
/// the script speaks (:1620). The special item types answer their own expiry and only the
/// default path deletes (:6380/:6412). TIMERF separates CLEAR and STOP from scheduling
/// (CObjBase.cpp:2762), reads Sphere numbers (:2777), answers ISTIMERF with the remaining
/// time (:1499) and runs due work in time order (CWorldTicker.cpp:1051).
/// </summary>
public sealed class ItemDecayTimerFParity12SUTests
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

    private static (GameClient Client, Character Player) MakePlayer(GameWorld world, int port,
        TriggerDispatcher? dispatcher = null)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        if (dispatcher != null) client.SetEngines(triggerDispatcher: dispatcher);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return (client, player);
    }

    // ================================================================ 12S-1

    [Fact]
    public void ADecayOfLessThanASecondSurvivesASave()
    {
        var world = NewWorld();
        var item = Ground(world);
        world.PlaceItemWithDecay(item, item.Position, 900);
        Assert.True(item.DecayTime > Environment.TickCount64);

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_dec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, dir);

            var reloaded = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, dir);
            var back = reloaded.FindItem(item.Uid)!;

            Assert.True(back.DecayTime > 0, "a sub-second decay must not round away to nothing");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ALongDecayStillSurvivesASave()
    {
        var world = NewWorld();
        var item = Ground(world);
        world.PlaceItemWithDecay(item, item.Position, 60_000);

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_dec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, dir);

            var reloaded = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, dir);
            var back = reloaded.FindItem(item.Uid)!;

            Assert.InRange(back.DecayTime - Environment.TickCount64, 1, 61_000);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================ 12S-2

    [Fact]
    public void PickingSomethingUpEndsItsRot()
    {
        var world = NewWorld();
        var (client, player) = MakePlayer(world, 9401);
        var item = Ground(world);
        world.PlaceItemWithDecay(item, player.Position, 60_000);
        Assert.True(item.IsAttr(ObjAttributes.Decay));

        client.HandleItemPickup(item.Uid.Value, 0);

        Assert.Equal(0, item.DecayTime);
        Assert.False(item.IsAttr(ObjAttributes.Decay));
    }

    // ================================================================ 12S-3

    [Fact]
    public void PlacingSomethingDownDoesNotCutShortItsOwnScriptTimer()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        item.SetTimeout(Environment.TickCount64 + 60_000);

        world.PlaceItemWithDecay(item, new Point3D(100, 100, 0, 0), 1_000);

        Assert.Equal(0, item.DecayTime);
        Assert.True(item.Timeout > Environment.TickCount64 + 50_000);
    }

    [Fact]
    public void PlacingAnOrdinaryItemStillArmsItsDecay()
    {
        var world = NewWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;

        world.PlaceItemWithDecay(item, new Point3D(100, 100, 0, 0), 1_000);

        Assert.True(item.DecayTime > Environment.TickCount64);
        Assert.True(item.IsAttr(ObjAttributes.Decay));
    }

    // ================================================================ 12S-5

    [Fact]
    public void AProtectedRegionShapesTheNaturalTimeNotAnExplicitOne()
    {
        var world = NewWorld();
        var region = new Region { Name = "sanctuary", MapIndex = 0, Flags = RegionFlag.NoDecay };
        region.AddRect(0, 0, 200, 200);
        world.AddRegion(region);

        var dispatcher = new TriggerDispatcher();
        long seenTenths = -1;
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Ground", (_, args) =>
        {
            seenTenths = args.N1;
            args.N1 = 50;               // the script insists on five seconds
            return TriggerResult.Default;
        });
        var (client, player) = MakePlayer(world, 9402, dispatcher);
        var item = Ground(world);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, player.X, player.Y, 0, 0xFFFFFFFF);

        Assert.True(seenTenths < 0, "the region's protection belongs to the natural time");
        Assert.InRange(item.DecayTime - Environment.TickCount64, 1, 5_000);
    }

    [Fact]
    public void AProtectedRegionStillStopsTheNaturalRot()
    {
        var world = NewWorld();
        var region = new Region { Name = "sanctuary", MapIndex = 0, Flags = RegionFlag.NoDecay };
        region.AddRect(0, 0, 200, 200);
        world.AddRegion(region);

        var (client, player) = MakePlayer(world, 9403);
        var item = Ground(world);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, player.X, player.Y, 0, 0xFFFFFFFF);

        Assert.Equal(0, item.DecayTime);
    }

    // ================================================================ 12T-1

    [Fact]
    public void AnItemTheDropScriptRemovedIsNotPutBackDown()
    {
        var world = NewWorld();
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Ground", (obj, _) =>
        {
            if (obj is Item dropped) { world.DeleteObject(dropped); dropped.Delete(); }
            return TriggerResult.Default;
        });
        var (client, player) = MakePlayer(world, 9404, dispatcher);
        var item = Ground(world);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, player.X, player.Y, 0, 0xFFFFFFFF);

        Assert.True(item.IsDeleted);
        var sector = world.GetSector(new Point3D(player.X, player.Y, 0, 0));
        Assert.DoesNotContain(item, sector!.Items);
    }

    // ================================================================ 12T-2

    [Fact]
    public void AContainerTheDropScriptChoseIsRespected()
    {
        var world = NewWorld();
        var chest = Ground(world, ItemType.Container);
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Ground", (obj, _) =>
        {
            if (obj is Item dropped) chest.AddItem(dropped);
            return TriggerResult.Default;
        });
        var (client, player) = MakePlayer(world, 9405, dispatcher);
        var item = Ground(world);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, player.X, player.Y, 0, 0xFFFFFFFF);

        Assert.Equal(chest.Uid, item.ContainedIn);
    }

    // ================================================================ 12T-3

    [Fact]
    public void ADropScriptThatTakesOverLeavesTheItemOnTheGround()
    {
        var world = NewWorld();
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Ground", (_, _) => TriggerResult.True);
        var (client, player) = MakePlayer(world, 9406, dispatcher);
        // Out of the backpack, so a bounce would be visible: the item would land
        // back where it was picked up from instead of staying where it was dropped.
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
        player.Backpack = pack; player.Equip(pack, Layer.Pack);
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        pack.AddItem(item);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, player.X, player.Y, 0, 0xFFFFFFFF);

        Assert.False(item.ContainedIn.IsValid);   // NOT bounced back into the pack
        Assert.Equal(player.X, item.X);
    }

    // ================================================================ 12T-4

    [Fact]
    public void ARefusedTimerDoesNotStopATypeFromDoingItsOwnWork()
    {
        var world = NewWorld();
        var hive = Ground(world, ItemType.BeeHive);
        hive.More1 = 1;
        hive.SetTimeout(Environment.TickCount64 - 1);

        Item.OnTimerExpired = _ => TriggerResult.False;
        try
        {
            Assert.True(hive.OnTick());
        }
        finally { Item.OnTimerExpired = null; }

        Assert.False(hive.IsDeleted);
        Assert.Equal(2u, hive.More1);
    }

    [Fact]
    public void ARefusedTimerStillDestroysAnOrdinaryItem()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetTimeout(Environment.TickCount64 - 1);

        Item.OnTimerExpired = _ => TriggerResult.False;
        try
        {
            Assert.False(item.OnTick());
        }
        finally { Item.OnTimerExpired = null; }

        Assert.True(item.IsDeleted);
    }

    // ================================================================ 12T-5

    [Fact]
    public void ADecayableItemIsDestroyedWhenItsMillisecondTimerRunsOut()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.SetAttr(ObjAttributes.Decay);
        Assert.True(item.TrySetProperty("TIMERMS", "1"));
        item.SetTimeout(Environment.TickCount64 - 1);

        Assert.False(item.OnTick());
        Assert.True(item.IsDeleted);
    }

    [Fact]
    public void AnItemWithoutTheDecayAttributeSurvivesTheSameTimer()
    {
        var world = NewWorld();
        var item = Ground(world);
        Assert.True(item.TrySetProperty("TIMERMS", "1"));
        item.SetTimeout(Environment.TickCount64 - 1);

        Assert.True(item.OnTick());
        Assert.False(item.IsDeleted);
    }

    // ================================================================ 12U-1

    [Fact]
    public void ClearCancelsEveryPendingJob()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.TryExecuteCommand("TIMERF", "1, f_a", null!);
        item.TryExecuteCommand("TIMERF", "1, f_b", null!);
        Assert.Equal(2, item.TimerFEntries.Count);

        Assert.True(item.TryExecuteCommand("TIMERF", "CLEAR", null!));

        Assert.Empty(item.TimerFEntries);
    }

    [Fact]
    public void StopCancelsOnlyTheJobsItNames()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.TryExecuteCommand("TIMERF", "1, f_a", null!);
        item.TryExecuteCommand("TIMERF", "1, f_b", null!);

        Assert.True(item.TryExecuteCommand("TIMERF", "STOP f_a", null!));

        var remaining = Assert.Single(item.TimerFEntries);
        Assert.Equal("f_b", remaining.FunctionName);
    }

    [Fact]
    public void AWildcardStopClearsTheMatchingFamily()
    {
        var world = NewWorld();
        var item = Ground(world);
        item.TryExecuteCommand("TIMERF", "1, f_a", null!);
        item.TryExecuteCommand("TIMERF", "1, f_b", null!);

        Assert.True(item.TryExecuteCommand("TIMERF", "STOP f_*", null!));

        Assert.Empty(item.TimerFEntries);
    }

    // ================================================================ 12U-2

    [Fact]
    public void TheRemainingTimeOfAPendingJobCanBeAsked()
    {
        var world = NewWorld();
        var item = Ground(world);

        Assert.True(item.TryGetProperty("ISTIMERF.f_a", out string none));
        Assert.Equal("0", none);

        item.TryExecuteCommand("TIMERF", "60, f_a", null!);
        Assert.True(item.TryGetProperty("ISTIMERF.f_a", out string some));
        Assert.InRange(long.Parse(some), 55_000, 60_000);
    }

    // ================================================================ 12U-3

    [Theory]
    [InlineData("2, f_a", 2_000)]
    [InlineData("1+1, f_a", 2_000)]
    [InlineData("010, f_a", 16_000)]
    [InlineData("2 f_a", 2_000)]
    public void ADelayIsReadTheWaySphereWritesIt(string arg, long expectedMs)
    {
        var world = NewWorld();
        var item = Ground(world);

        Assert.True(item.TryExecuteCommand("TIMERF", arg, null!));

        var entry = Assert.Single(item.TimerFEntries);
        Assert.Equal("f_a", entry.FunctionName);
        Assert.InRange(entry.DueTickMs - Environment.TickCount64, expectedMs - 500, expectedMs + 100);
    }

    [Fact]
    public void ANegativeDelayIsRefusedRatherThanRunAtOnce()
    {
        var world = NewWorld();
        var item = Ground(world);

        item.TryExecuteCommand("TIMERF", "-1, f_a", null!);

        Assert.Empty(item.TimerFEntries);
    }

    // ================================================================ 12U-4

    [Fact]
    public void DueJobsRunInTheOrderTheyWereDue()
    {
        var world = NewWorld();
        var item = Ground(world);
        // The LATER one is scheduled first, so insertion order and due order disagree.
        item.TryExecuteCommand("TIMERFMS", "200, f_late", null!);
        item.TryExecuteCommand("TIMERFMS", "100, f_early", null!);

        var due = item.DequeueDueTimerF(Environment.TickCount64 + 5_000);

        Assert.Equal(2, due.Count);
        Assert.Equal("f_early", due[0].FunctionName);
        Assert.Equal("f_late", due[1].FunctionName);
    }

    // ================================================================ 12U-5

    [Fact]
    public void ASectorStopsListeningOnceItsLastCrystalDecays()
    {
        var world = NewWorld();
        var crystal = Ground(world, ItemType.CommCrystal);
        var sector = world.GetSector(crystal.Position)!;
        Assert.True(sector.HasListenItems);

        crystal.DecayTime = Environment.TickCount64 - 1;
        crystal.SetAttr(ObjAttributes.Decay);
        sector.OnMaintenanceTick();

        Assert.False(sector.HasListenItems);
    }
}
