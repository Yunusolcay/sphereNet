using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Secure trade: whose window is whose, what an accept means, and what happens when
/// the offer changes underneath one.
///
/// The root defect was that both trade windows were loose world items with no owner
/// and no position. Source-X Cmd_SecureTrade (CClientUse.cpp:1414/1420) equips each
/// window on its character as IT_EQ_TRADE_WINDOW at LAYER_SPECIAL, and almost
/// everything else here follows from having that owner:
/// - a player can take back what they offered, because the window's top-level object
///   is themselves (the 01B reach gate found neither owner nor open record before);
/// - the accept packet can be checked against the sender's OWN container
///   (receive.cpp:1132), so nobody can set the partner's flag;
/// - a save taken mid-trade has something to reconcile against on load.
/// </summary>
public sealed class SecureTradeParityTests
{
    private static (GameWorld World, GameClient Client, Character Me, Character Partner) Setup(int id = 8200)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);

        var me = MakePlayer(world, 100);
        var partner = MakePlayer(world, 101);
        partner.IsOnline = true;

        TestHarness.AttachCharacter(client, me);
        TestHarness.SetPrivateField(client, "_tradeManager", new TradeManager());
        return (world, client, me, partner);
    }

    private static Character MakePlayer(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.Player;
        ch.Str = 100; ch.MaxHits = ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container; pack.BaseId = 0x0E75;
        ch.Backpack = pack; ch.Equip(pack, Layer.Pack);
        return ch;
    }

    private static Item NewItem(GameWorld world, ushort baseId = 0x0F51)
    {
        var item = world.CreateItem();
        item.BaseId = baseId;
        return item;
    }

    private static TradeManager Manager(GameClient client) =>
        (TradeManager)typeof(GameClient)
            .GetField("_tradeManager", System.Reflection.BindingFlags.Instance |
                                       System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;

    // --- SX-02-02 / SX-02B-01: the window belongs to someone ----------------

    [Fact]
    public void EachTradeWindowIsOwnedByItsSide()
    {
        var (_, client, me, partner) = Setup();
        Assert.True(client.InitiateTrade(partner));

        var trade = Manager(client).FindTradeFor(me)!;
        Assert.Equal(ItemType.EqTradeWindow, trade.InitiatorContainer.ItemType);
        Assert.Equal(Layer.Special, trade.InitiatorContainer.EquipLayer);
        Assert.Equal(me.Uid, trade.InitiatorContainer.ContainedIn);
        Assert.Equal(partner.Uid, trade.PartnerContainer.ContainedIn);
    }

    [Fact]
    public void APlayerCanTakeBackWhatTheyOffered()
    {
        var (world, client, me, partner) = Setup(8201);
        var offered = NewItem(world);
        Assert.True(me.Backpack!.TryAddItem(offered));

        client.Inventory.HandleItemPickup(offered.Uid.Value, 0);
        Assert.True(client.InitiateTrade(partner, offered));

        var trade = Manager(client).FindTradeFor(me)!;
        Assert.Contains(offered, trade.InitiatorContainer.Contents);

        // Withdrawing was impossible once the 01B reach gate landed: the window had
        // no owner and no opened-container record, so it failed both branches.
        client.Inventory.HandleItemPickup(offered.Uid.Value, 0);
        Assert.True(me.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(offered.Uid.Value.ToString(), held);
    }

    [Fact]
    public void APlayerCannotReachIntoThePartnersWindow()
    {
        var (world, client, me, partner) = Setup(8202);
        Assert.True(client.InitiateTrade(partner));

        var trade = Manager(client).FindTradeFor(me)!;
        var theirs = NewItem(world);
        Assert.True(trade.PartnerContainer.TryAddItem(theirs));

        client.Inventory.HandleItemPickup(theirs.Uid.Value, 0);

        Assert.Contains(theirs, trade.PartnerContainer.Contents);
        Assert.False(me.TryGetTag("DRAGGING", out _));
    }

    // --- SX-02-01: the accept packet carries the state ----------------------

    [Fact]
    public void AnUncheckedAcceptDoesNotCompleteTheTrade()
    {
        var (world, client, me, partner) = Setup(8203);
        Assert.True(client.InitiateTrade(partner));
        var trade = Manager(client).FindTradeFor(me)!;

        var mine = NewItem(world);
        Assert.True(trade.InitiatorContainer.TryAddItem(mine));
        trade.SetAccept(partner, true);

        // param 0 means "I do not accept". It used to be ignored, and the stored
        // bool was flipped instead - so this completed the trade.
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 0);

        Assert.NotNull(Manager(client).FindTradeFor(me));
        Assert.Contains(mine, trade.InitiatorContainer.Contents);
        Assert.False(trade.InitiatorAccepted);
    }

    [Fact]
    public void WithdrawingAcceptanceClearsBothSides()
    {
        var (_, client, me, partner) = Setup(8204);
        Assert.True(client.InitiateTrade(partner));
        var trade = Manager(client).FindTradeFor(me)!;

        trade.SetAccept(partner, true);
        Assert.True(trade.PartnerAccepted);

        // Source-X Trade_Status(false) clears the partner's mark as well.
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 0);

        Assert.False(trade.InitiatorAccepted);
        Assert.False(trade.PartnerAccepted);
    }

    [Fact]
    public void RepeatingAnAcceptDoesNotUndoIt()
    {
        var (_, client, me, partner) = Setup(8205);
        Assert.True(client.InitiateTrade(partner));
        var trade = Manager(client).FindTradeFor(me)!;

        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 1);
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 1);

        Assert.True(trade.InitiatorAccepted);
    }

    [Fact]
    public void AnAcceptNamingThePartnersContainerIsIgnored()
    {
        // Reading the flag from the packet without this check would let a client set
        // the OTHER side's acceptance (Source-X receive.cpp:1132).
        var (_, client, me, partner) = Setup(8206);
        Assert.True(client.InitiateTrade(partner));
        var trade = Manager(client).FindTradeFor(me)!;

        client.HandleSecureTrade(2, trade.PartnerContainer.Uid.Value, 1);

        Assert.False(trade.InitiatorAccepted);
        Assert.False(trade.PartnerAccepted);
    }

    // --- SX-02-04: changing the offer invalidates acceptance ----------------

    [Fact]
    public void RemovingAnOfferedItemClearsAcceptance()
    {
        var (world, client, me, partner) = Setup(8207);
        Item.OnTradeWindowChanged = w => Manager(client).FindByContainer(w.Uid.Value)?.ResetAcceptance();
        try
        {
            Assert.True(client.InitiateTrade(partner));
            var trade = Manager(client).FindTradeFor(me)!;

            var mine = NewItem(world);
            Assert.True(trade.InitiatorContainer.TryAddItem(mine));
            trade.SetAccept(partner, true);
            Assert.True(trade.PartnerAccepted);

            // A server-side change - a script move, an engine deletion - must not
            // leave the partner agreeing to an offer that no longer exists.
            trade.InitiatorContainer.RemoveItem(mine);

            Assert.False(trade.PartnerAccepted);
            Assert.False(trade.InitiatorAccepted);
        }
        finally { Item.OnTradeWindowChanged = null; }
    }

    [Fact]
    public void AddingToAnOfferClearsAcceptance()
    {
        var (world, client, me, partner) = Setup(8208);
        Item.OnTradeWindowChanged = w => Manager(client).FindByContainer(w.Uid.Value)?.ResetAcceptance();
        try
        {
            Assert.True(client.InitiateTrade(partner));
            var trade = Manager(client).FindTradeFor(me)!;
            trade.SetAccept(partner, true);

            Assert.True(trade.InitiatorContainer.TryAddItem(NewItem(world)));

            Assert.False(trade.PartnerAccepted);
        }
        finally { Item.OnTradeWindowChanged = null; }
    }

    // --- SX-02-03 / SX-02B-02: a refused start gives the item back ----------

    [Fact]
    public void AnItemDroppedOnAPlayerRefusingTradesComesBack()
    {
        var (world, client, me, partner) = Setup(8209);
        partner.SetTag("REFUSETRADES", "1");

        var item = NewItem(world);
        Assert.True(me.Backpack!.TryAddItem(item));
        client.Inventory.HandleItemPickup(item.Uid.Value, 0);

        client.Inventory.HandleItemDrop(item.Uid.Value, 0, 0, 0, partner.Uid.Value);

        Assert.Null(Manager(client).FindTradeFor(me));
        Assert.Contains(item, me.Backpack.Contents);
        Assert.False(me.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void ATradeCannotBeStartedWithAnOfflinePlayer()
    {
        var (world, client, me, partner) = Setup(8210);
        partner.IsOnline = false;   // a body in the world is not a player who can answer

        var item = NewItem(world);
        Assert.True(me.Backpack!.TryAddItem(item));
        client.Inventory.HandleItemPickup(item.Uid.Value, 0);

        client.Inventory.HandleItemDrop(item.Uid.Value, 0, 0, 0, partner.Uid.Value);

        Assert.Null(Manager(client).FindTradeFor(me));
        Assert.Contains(item, me.Backpack.Contents);
    }

    [Fact]
    public void ARefusedStartLeavesNothingParentedToTheCharacter()
    {
        var (world, client, me, partner) = Setup(8211);
        partner.SetStatFlag(StatFlag.Dead);

        var item = NewItem(world);
        Assert.True(me.Backpack!.TryAddItem(item));
        client.Inventory.HandleItemPickup(item.Uid.Value, 0);
        client.Inventory.HandleItemDrop(item.Uid.Value, 0, 0, 0, partner.Uid.Value);

        // The failure mode being locked out: present in the world, but reachable
        // through no container, no equipment layer and no cursor.
        Assert.NotEqual(me.Uid, item.ContainedIn);
        Assert.Contains(item, me.Backpack.Contents);
    }
}
