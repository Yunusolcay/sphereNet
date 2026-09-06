using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X item/inventory rule parity (wiki/item-inventory-remaining.txt):
//   #1 central ItemMoveRules.CanMove (Move_Never / frozen / dead gate on pickup),
//   #2 central Character.CanEquip (layer validity + REQSTR),
//   #3 per-container OVERRIDE.MAXITEMS cap on drop,
//   #5 @DropOn_Ground position (ARGN) + DECAY (LOCAL) mutation,
//   #7 decay @Timer veto (RETURN 1 keeps the item).
public class ItemInventoryRulesTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    // ---- #1: ItemMoveRules.CanMove ----

    [Fact]
    public void CanMove_MoveNeverItem_Denied()
    {
        var world = CreateWorld();
        var mover = world.CreateCharacter();
        mover.IsPlayer = true; mover.PrivLevel = PrivLevel.Player;
        var item = world.CreateItem();
        item.SetAttr(ObjAttributes.Move_Never);

        Assert.False(ItemMoveRules.CanMove(mover, item, out var denial));
        Assert.Equal(ItemMoveRules.MoveDenial.ItemImmovable, denial);
    }

    [Fact]
    public void CanMove_FrozenMover_Denied()
    {
        var world = CreateWorld();
        var mover = world.CreateCharacter();
        mover.IsPlayer = true; mover.PrivLevel = PrivLevel.Player;
        mover.SetStatFlag(StatFlag.Freeze);
        var item = world.CreateItem();

        Assert.False(ItemMoveRules.CanMove(mover, item, out var denial));
        Assert.Equal(ItemMoveRules.MoveDenial.MoverFrozen, denial);
    }

    [Fact]
    public void CanMove_DeadMover_Denied()
    {
        var world = CreateWorld();
        var mover = world.CreateCharacter();
        mover.IsPlayer = true; mover.PrivLevel = PrivLevel.Player;
        mover.Kill();
        var item = world.CreateItem();

        Assert.False(ItemMoveRules.CanMove(mover, item, out var denial));
        Assert.Equal(ItemMoveRules.MoveDenial.MoverDead, denial);
    }

    [Fact]
    public void CanMove_Gm_BypassesEveryGate()
    {
        var world = CreateWorld();
        var mover = world.CreateCharacter();
        mover.IsPlayer = true; mover.PrivLevel = PrivLevel.GM;
        mover.Kill();
        var item = world.CreateItem();
        item.SetAttr(ObjAttributes.Move_Never);

        Assert.True(ItemMoveRules.CanMove(mover, item, out _));
    }

    [Fact]
    public void CanMove_NormalItem_Allowed()
    {
        var world = CreateWorld();
        var mover = world.CreateCharacter();
        mover.IsPlayer = true; mover.PrivLevel = PrivLevel.Player;
        var item = world.CreateItem();

        Assert.True(ItemMoveRules.CanMove(mover, item, out var denial));
        Assert.Equal(ItemMoveRules.MoveDenial.None, denial);
    }

    // ---- #2: Character.CanEquip ----

    [Fact]
    public void CanEquip_TooWeak_Denied()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.PrivLevel = PrivLevel.Player; ch.Str = 10;
        var item = world.CreateItem();
        item.SetTag("OVERRIDE.REQSTR", "50");

        Assert.False(ch.CanEquip(item, Layer.OneHanded, out var denial));
        Assert.Equal(Character.EquipDenial.TooWeak, denial);
    }

    [Fact]
    public void CanEquip_StrongEnough_Ok()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.PrivLevel = PrivLevel.Player; ch.Str = 60;
        var item = world.CreateItem();
        item.SetTag("OVERRIDE.REQSTR", "50");

        Assert.True(ch.CanEquip(item, Layer.OneHanded, out _));
    }

    [Fact]
    public void CanEquip_InvalidLayer_Denied()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.PrivLevel = PrivLevel.Player;
        var item = world.CreateItem();

        Assert.False(ch.CanEquip(item, Layer.Dragging, out var denial));
        Assert.Equal(Character.EquipDenial.InvalidLayer, denial);
    }

    [Fact]
    public void CanEquip_Gm_BypassesReqStr()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.PrivLevel = PrivLevel.GM; ch.Str = 1;
        var item = world.CreateItem();
        item.SetTag("OVERRIDE.REQSTR", "9999");

        Assert.True(ch.CanEquip(item, Layer.OneHanded, out _));
    }

    // ---- #7: decay @Timer veto ----

    [Fact]
    public void Decay_TimerReturnsTrue_KeepsItemAndRearms()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        item.DecayTime = Environment.TickCount64 - 1000; // already elapsed

        Item.OnTimerExpired = _ => TriggerResult.True; // @Timer keeps it (reset by ResetEngineStatics)
        bool keepTicking = item.OnTick();

        Assert.True(keepTicking);
        Assert.False(item.IsDeleted);
        Assert.True(item.DecayTime > Environment.TickCount64); // re-armed
    }

    [Fact]
    public void Decay_NoTimerHandler_DeletesItem()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        item.DecayTime = Environment.TickCount64 - 1000;

        Item.OnTimerExpired = null;
        bool keepTicking = item.OnTick();

        Assert.False(keepTicking);
        Assert.True(item.IsDeleted);
    }

    [Fact]
    public void Decay_TimerReturnsDefault_DeletesItem()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        item.DecayTime = Environment.TickCount64 - 1000;

        Item.OnTimerExpired = _ => TriggerResult.Default; // no script handler
        bool keepTicking = item.OnTick();

        Assert.False(keepTicking);
        Assert.True(item.IsDeleted);
    }

    // ---- #1 + #3 + #5: packet handler integration ----

    private static (GameClient client, Character player, Item pack) MakePlayer(
        GameWorld world, int port, TriggerDispatcher? dispatcher = null)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var player = world.CreateCharacter();
        player.IsPlayer = true; player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
        player.Backpack = pack; player.Equip(pack, Layer.Pack);

        if (dispatcher != null)
            client.SetEngines(triggerDispatcher: dispatcher);
        return (client, player, pack);
    }

    [Fact]
    public void Pickup_MoveNeverItem_IsRejected()
    {
        var world = CreateWorld();
        var (client, player, _) = MakePlayer(world, 9301);

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        item.SetAttr(ObjAttributes.Move_Never);
        world.PlaceItem(item, player.Position);

        client.HandleItemPickup(item.Uid.Value, 0);

        Assert.NotEqual(player.Uid, item.ContainedIn); // never left the ground
        Assert.False(player.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void Drop_IntoContainerOverMaxItems_IsRejected()
    {
        var world = CreateWorld();
        var (client, player, pack) = MakePlayer(world, 9302);

        // A container whose per-instance cap is 0 rejects any drop.
        var box = world.CreateItem();
        box.BaseId = 0x0E75; box.ItemType = ItemType.Container;
        box.SetTag("OVERRIDE.MAXITEMS", "0");
        world.PlaceItem(box, player.Position);

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        pack.AddItem(item); // lifted from the pack → bounces back to the pack

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, box.Uid.Value);

        Assert.DoesNotContain(box.Contents, i => i.Uid == item.Uid); // capped out
        Assert.Equal(pack.Uid, item.ContainedIn);                    // bounced to origin (pack)
    }

    // ---- #4: drag bounce-to-origin on a failed drop ----

    [Fact]
    public void FailedDrop_BouncesItemBackToOriginContainer()
    {
        var world = CreateWorld();
        var (client, player, pack) = MakePlayer(world, 9304);

        // A pouch inside the pack; the item is lifted from the pouch.
        var pouch = world.CreateItem();
        pouch.BaseId = 0x0E79; pouch.ItemType = ItemType.Container;
        pack.AddItem(pouch);
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        pouch.AddItem(item);

        // A full target box rejects the drop.
        var box = world.CreateItem();
        box.BaseId = 0x0E75; box.ItemType = ItemType.Container;
        box.SetTag("OVERRIDE.MAXITEMS", "0");
        world.PlaceItem(box, player.Position);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, box.Uid.Value);

        Assert.Equal(pouch.Uid, item.ContainedIn); // back in the pouch, not the main pack
        Assert.Contains(pouch.Contents, i => i.Uid == item.Uid);
    }

    [Fact]
    public void FailedDrop_BouncesGroundItemBackToGround()
    {
        var world = CreateWorld();
        var (client, player, _) = MakePlayer(world, 9305);

        var origin = new Point3D(102, 100, 0, 0);
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, origin);

        var box = world.CreateItem();
        box.BaseId = 0x0E75; box.ItemType = ItemType.Container;
        box.SetTag("OVERRIDE.MAXITEMS", "0");
        world.PlaceItem(box, player.Position);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, box.Uid.Value);

        Assert.False(item.ContainedIn.IsValid);   // back on the ground
        Assert.Equal(origin.X, item.X);
        Assert.Equal(origin.Y, item.Y);
    }

    [Fact]
    public void DropOnGround_TriggerRelocatesAndSetsDecay()
    {
        var world = CreateWorld();
        var dispatcher = new TriggerDispatcher();
        // The Source-X contract: ARGN1 carries the decay in TENTHS OF A SECOND and the
        // string argument carries the drop point (MoveToCheck, CItem.cpp:1629). This
        // test used to write the X coordinate into ARGN1, which is what that finding
        // was about - passing then proved nothing about the reference.
        Point3D seenPoint = default;
        int seenTenths = -1;
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Ground", (_, args) =>
        {
            seenTenths = args.N1;
            Point3D.TryParse(args.S1 ?? "", out seenPoint);
            args.N1 = 50;                                     // 5 seconds
            args.S1 = $"{seenPoint.X + 1},{seenPoint.Y},{seenPoint.Z},{seenPoint.Map}";
            return TriggerResult.Default;
        });
        var (client, player, _) = MakePlayer(world, 9303, dispatcher);

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, player.Position);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, player.X, player.Y, 0, 0xFFFFFFFF);

        Assert.Equal(player.X, seenPoint.X);                 // the point it was given
        Assert.True(seenTenths > 0, "the natural decay should arrive in tenths");
        Assert.Equal(player.X + 1, item.X);                  // relocated by the script
        long remainingMs = item.DecayTime - Environment.TickCount64;
        Assert.InRange(remainingMs, 1, 5000);                // the 5 s it chose
    }

    [Fact]
    public void DropOnGround_LocalDecayStillWorksForOlderScripts()
    {
        var world = CreateWorld();
        var dispatcher = new TriggerDispatcher();
        // The LOCAL.DECAY seconds reading SphereNet has always accepted is kept, so a
        // pack written against it does not break when ARGN1 changes meaning.
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Ground", (_, args) =>
        {
            args.Locals?.SetInt("DECAY", 5);
            return TriggerResult.Default;
        });
        var (client, player, _) = MakePlayer(world, 9304, dispatcher);

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, player.Position);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, player.X, player.Y, 0, 0xFFFFFFFF);

        long remainingMs = item.DecayTime - Environment.TickCount64;
        Assert.InRange(remainingMs, 1, 5000);
    }

    // ---- #1b: bank cap counts the whole tree (nested bags can't bypass) ----

    [Fact]
    public void BankCap_CountsNestedBags_RejectsOverDeepLimit()
    {
        var world = CreateWorld();
        var (client, player, _) = MakePlayer(world, 9306);
        world.MaxBankItems = 2; // tiny cap for the test

        // A banker beside the player so the deep cap (not the bank-proximity gate)
        // is what rejects the drop.
        var banker = world.CreateCharacter();
        banker.NpcBrain = NpcBrainType.Banker;
        world.PlaceCharacter(banker, player.Position);

        var bank = world.CreateItem();
        bank.BaseId = 0x0E75; bank.ItemType = ItemType.Container;
        player.Equip(bank, Layer.BankBox);

        // A sub-bag holding one item: deep count = bag + item = 2 (the cap). The
        // bank's SHALLOW count is just 1 (the bag), which would wrongly allow more.
        var bag = world.CreateItem();
        bag.BaseId = 0x0E79; bag.ItemType = ItemType.Container;
        bank.AddItem(bag);
        var stored = world.CreateItem(); stored.BaseId = 0x0F7A;
        bag.AddItem(stored);

        var item = world.CreateItem(); item.BaseId = 0x0F7A;
        player.Backpack!.AddItem(item);

        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, bank.Uid.Value);

        Assert.DoesNotContain(bank.Contents, i => i.Uid == item.Uid); // deep cap rejects
    }

    // ---- #6: self bank box is only reachable while near a banker ----

    [Fact]
    public void SelfBankBox_RequiresNearbyBanker_ForDropAndPickup()
    {
        var world = CreateWorld();
        var (client, player, pack) = MakePlayer(world, 9307);

        var bank = world.CreateItem();
        bank.BaseId = 0x0E75; bank.ItemType = ItemType.Container;
        player.Equip(bank, Layer.BankBox);

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        pack.AddItem(item);

        // No banker nearby -> the drop into the bank is rejected (bounced to the pack).
        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, bank.Uid.Value);
        Assert.DoesNotContain(bank.Contents, i => i.Uid == item.Uid);
        Assert.Equal(pack.Uid, item.ContainedIn);

        // With a banker beside the player the drop succeeds.
        var banker = world.CreateCharacter();
        banker.NpcBrain = NpcBrainType.Banker;
        world.PlaceCharacter(banker, player.Position);
        client.HandleItemPickup(item.Uid.Value, 0);
        client.HandleItemDrop(item.Uid.Value, 0, 0, 0, bank.Uid.Value);
        Assert.Contains(bank.Contents, i => i.Uid == item.Uid);

        // Banker leaves -> picking the item back out of the bank is now rejected.
        world.MoveCharacter(banker, new Point3D(150, 150, 0, 0));
        client.HandleItemPickup(item.Uid.Value, 0);
        Assert.Contains(bank.Contents, i => i.Uid == item.Uid); // still in the bank
    }

    // ---- #6: a contained item must be reachable through its top parent ----

    [Fact]
    public void DClick_ContainedItemOutOfReach_IsBlocked()
    {
        var world = CreateWorld();
        var dispatcher = new TriggerDispatcher();
        bool fired = false;
        dispatcher.RegisterItemEvent("EVENTSITEM", "DClick", (_, _) => { fired = true; return TriggerResult.True; });
        var (client, _, _) = MakePlayer(world, 9307, dispatcher);

        var box = world.CreateItem();
        box.BaseId = 0x0E75; box.ItemType = ItemType.Container;
        world.PlaceItem(box, new Point3D(150, 150, 0, 0)); // far away
        var item = world.CreateItem(); item.BaseId = 0x0F7A;
        box.AddItem(item);

        client.HandleDoubleClick(item.Uid.Value);

        Assert.False(fired); // reach check blocked use before @DClick
    }

    [Fact]
    public void DClick_ContainedItemInReach_FiresTrigger()
    {
        var world = CreateWorld();
        var dispatcher = new TriggerDispatcher();
        bool fired = false;
        dispatcher.RegisterItemEvent("EVENTSITEM", "DClick", (_, _) => { fired = true; return TriggerResult.True; });
        var (client, player, _) = MakePlayer(world, 9308, dispatcher);

        var box = world.CreateItem();
        box.BaseId = 0x0E75; box.ItemType = ItemType.Container;
        world.PlaceItem(box, player.Position); // adjacent
        var item = world.CreateItem(); item.BaseId = 0x0F7A;
        box.AddItem(item);

        client.HandleDoubleClick(item.Uid.Value);

        Assert.True(fired);
    }
}
