using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Who is allowed to trade with a vendor.
///
/// Two ways the answer was wrong:
/// - A vendor window outlives the player. Dying with it open left the transaction
///   reachable: the handler checked the vendor, the map and the distance but never
///   the buyer's own death, so a ghost could still take stock. Source-X CanTouch
///   refuses a dead character every item that is not death-immune
///   (CCharStatus.cpp:1360).
/// - The load-test bot's payment exemption keyed on the CHARACTER NAME, which the
///   player chooses. An ordinary character called "SphereBotanist" matched the
///   prefix and bought for free, with bot mode switched off.
/// </summary>
public sealed class VendorAuthorizationTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;
        return world;
    }

    private static Character MakeBuyer(GameWorld world, string name, int gold)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        ch.PrivLevel = PrivLevel.Player;
        ch.Str = 50; ch.MaxHits = ch.Hits = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container; pack.BaseId = 0x0E75;
        ch.Backpack = pack; ch.Equip(pack, Layer.Pack);

        if (gold > 0)
        {
            var coins = world.CreateItem();
            coins.BaseId = 0x0EED; coins.ItemType = ItemType.Gold; coins.Amount = (ushort)gold;
            pack.AddItem(coins);
        }
        return ch;
    }

    private static (Character Vendor, Item Row) MakeVendor(GameWorld world, Point3D at)
    {
        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, at);

        var stock = world.CreateItem();
        stock.ItemType = ItemType.Container;
        vendor.Equip(stock, Layer.VendorStock);

        var row = world.CreateItem();
        row.BaseId = 0x0F52; row.Amount = 5; row.SetTag("PRICE", "10");
        stock.AddItem(row);
        return (vendor, row);
    }

    // --- G08: a ghost cannot trade -----------------------------------------

    [Fact]
    public void ADeadBuyerCannotBuy()
    {
        var world = CreateWorld();
        var buyer = MakeBuyer(world, "Alice", 100);
        var (vendor, row) = MakeVendor(world, buyer.Position);

        buyer.SetStatFlag(StatFlag.Dead);

        Assert.Equal(-1, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));

        Assert.Equal(100, VendorEngine.CountGold(buyer));
        Assert.Equal(5, row.Amount);
        Assert.DoesNotContain(buyer.Backpack!.Contents, i => i.BaseId == 0x0F52);
    }

    [Fact]
    public void ADeadSellerCannotSell()
    {
        var world = CreateWorld();
        var seller = MakeBuyer(world, "Alice", 0);
        var (vendor, _) = MakeVendor(world, seller.Position);

        var goods = world.CreateItem();
        goods.BaseId = 0x0F52; goods.Amount = 1;
        Assert.True(seller.Backpack!.TryAddItem(goods));

        seller.SetStatFlag(StatFlag.Dead);

        Assert.Equal(-1, VendorEngine.ProcessSell(seller, vendor,
            [new TradeEntry { ItemUid = goods.Uid, Amount = 1 }]));
        Assert.Contains(goods, seller.Backpack.Contents);
    }

    [Fact]
    public void ALivingBuyerIsUnaffected()
    {
        var world = CreateWorld();
        var buyer = MakeBuyer(world, "Alice", 100);
        var (vendor, row) = MakeVendor(world, buyer.Position);

        Assert.Equal(10, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));
        Assert.Equal(90, VendorEngine.CountGold(buyer));
        Assert.Equal(4, row.Amount);
    }

    // --- G09: a display name is not a payment exemption --------------------

    [Fact]
    public void APlayerNamedLikeABotStillHasToPay()
    {
        var world = CreateWorld();
        var buyer = MakeBuyer(world, "SphereBotanist", 0);   // no money at all
        var (vendor, row) = MakeVendor(world, buyer.Position);

        Assert.Equal(-1, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));

        Assert.Equal(5, row.Amount);
        Assert.DoesNotContain(buyer.Backpack!.Contents, i => i.BaseId == 0x0F52);
    }

    [Theory]
    [InlineData("SphereBot1")]
    [InlineData("spherebot99")]
    [InlineData("SPHEREBOTTLER")]
    public void NoNameShapeGrantsFreeGoodsWhileBotModeIsOff(string name)
    {
        Assert.False(SphereNet.Game.Diagnostics.BotEngine.IsLiveBotCharacter(name),
            "bot privilege must require a bot session the server is actually running");

        var world = CreateWorld();
        var buyer = MakeBuyer(world, name, 0);
        var (vendor, row) = MakeVendor(world, buyer.Position);

        Assert.Equal(-1, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));
        Assert.Equal(5, row.Amount);
    }

    [Fact]
    public void APayingPlayerNamedLikeABotIsChargedNormally()
    {
        var world = CreateWorld();
        var buyer = MakeBuyer(world, "SphereBotanist", 100);
        var (vendor, row) = MakeVendor(world, buyer.Position);

        Assert.Equal(10, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));
        Assert.Equal(90, VendorEngine.CountGold(buyer));
    }
}
