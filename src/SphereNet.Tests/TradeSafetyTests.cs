using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Network.State;

namespace SphereNet.Tests;

public class TradeSafetyTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void Disconnect_AbortActiveTrade_ReturnsItemsToOwners()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new SphereNet.Game.Accounts.AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 901 };
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(netState, true);

        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        initiator.Str = 100;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));

        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        var tradeManager = new TradeManager();
        client.SetEngines(tradeManager: tradeManager);
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(client, initiator);

        // A trade partner must have an active client (Source-X Cmd_SecureTrade
        // refuses offline players); the test partner never logs in.
        partner.IsOnline = true;
        client.InitiateTrade(partner);
        var trade = tradeManager.FindTradeFor(initiator);
        Assert.NotNull(trade);

        var offered = world.CreateItem();
        offered.BaseId = 0x0EED;
        offered.ItemType = ItemType.Gold;
        offered.Amount = 500;
        trade!.InitiatorContainer.AddItem(offered);

        client.OnDisconnect();

        Assert.Null(tradeManager.FindTradeFor(initiator));
        Assert.Null(tradeManager.FindTradeFor(partner));
        Assert.False(offered.IsDeleted);
        Assert.True(initiator.Backpack != null);
        Assert.Contains(offered, initiator.Backpack!.Contents);
    }

    [Fact]
    public void SecureTrade_OverweightCompletion_ResetsAcceptanceAndKeepsItemsInTrade()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new SphereNet.Game.Accounts.AccountManager(loggerFactory);
        var client = TestHarness.CreateClient(loggerFactory, world, accountManager, 902);

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        initiator.Str = 100;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));

        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        partner.Str = 1;
        partner.ModMaxWeight = -43;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        var tradeManager = new TradeManager();
        client.SetEngines(tradeManager: tradeManager);
        TestHarness.AttachCharacter(client, initiator);

        // A trade partner must have an active client (Source-X Cmd_SecureTrade
        // refuses offline players); the test partner never logs in.
        partner.IsOnline = true;
        client.InitiateTrade(partner);
        var trade = tradeManager.FindTradeFor(initiator);
        Assert.NotNull(trade);

        var heavy = world.CreateItem();
        heavy.BaseId = 0x1F14;
        heavy.Amount = 100;
        trade!.InitiatorContainer.AddItem(heavy);

        Assert.False(trade.SetAccept(partner, true));
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 1);

        Assert.NotNull(tradeManager.FindTradeFor(initiator));
        Assert.False(trade.InitiatorAccepted);
        Assert.False(trade.PartnerAccepted);
        Assert.Contains(heavy, trade.InitiatorContainer.Contents);
        Assert.DoesNotContain(heavy, partner.Backpack?.Contents ?? []);
    }

    [Fact]
    public void TradeManager_CanAcceptTradeItems_AllowsEmptyOffer()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        var container = world.CreateItem();
        container.ItemType = ItemType.Container;

        Assert.True(TradeManager.CanAcceptTradeItems(ch, world, container, out _));
    }

    [Fact]
    public void TradeManager_CanAcceptTradeItems_RejectsWhenOverCapacity()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = 1;
        ch.ModMaxWeight = -43; // carry cap ~= 3 stones

        var container = world.CreateItem();
        container.ItemType = ItemType.Container;
        for (int i = 0; i < 20; i++)
        {
            var stack = world.CreateItem();
            stack.BaseId = 0x1F14; // ore — non-zero ITEMDEF weight
            stack.Amount = 100;
            container.AddItem(stack);
        }

        Assert.False(TradeManager.CanAcceptTradeItems(ch, world, container, out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void ReturnTradeItems_ReturnsItemsToBackpack()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;

        var container = world.CreateItem();
        container.ItemType = ItemType.Container;

        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        item.Amount = 100;
        container.AddItem(item);

        var trade = new SecureTrade(new Serial(0x80000001), owner, world.CreateCharacter(),
            container, world.CreateItem());

        TradeManager.ReturnTradeItems(world, trade);

        Assert.NotNull(owner.Backpack);
        Assert.Contains(item, owner.Backpack!.Contents);
    }
}
