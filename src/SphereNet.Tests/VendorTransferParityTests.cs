using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a vendor actually hands over, and what it keeps.
///
/// A player vendor sells REAL objects out of its own store; an NPC vendor sells
/// from a virtual template list. Source-X Event_VendorBuy splits on exactly that
/// (CClientEvent.cpp:1352) and the halves are not interchangeable - cloning a
/// player vendor's item gave the buyer a copy with a new uid and an empty inside
/// while the original was destroyed. The same split exists on the sell side
/// (:1521): a player vendor stores what it buys in its extra container so it can
/// resell it, rather than destroying it.
///
/// A non-stackable multi-buy is also delivered as separate objects with Amount=1
/// (:1328); one object carrying Amount=3 is not three swords to anything
/// downstream that counts objects.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VendorTransferParityTests
{
    // A pile itemdef so 0x0F7A resolves as stackable; IsStackable needs the I_Pile
    // CAN flag (0x0100) or tiledata, and neither exists in a bare test world.
    private const string PileScript = """
        [ITEMDEF 0f7a]
        DEFNAME=i_test_reagent
        NAME=nightshade
        CAN=0x0100
        """;

    private static void LoadPileDef()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_vendorpile_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, PileScript);
        var resources = new ResourceHolder(NullLoggerFactory.Instance.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        try { File.Delete(tempFile); } catch { }
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int gold, int x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.Player;
        ch.Str = 100; ch.MaxHits = ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));

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

    private static (Character Vendor, Item Stock, Item Extra) MakeVendor(
        GameWorld world, Point3D at, Character? owner = null)
    {
        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, at);
        if (owner != null)
            vendor.TryAssignOwnership(owner, owner);

        var stock = world.CreateItem();
        stock.ItemType = ItemType.Container;
        vendor.Equip(stock, Layer.VendorStock);

        var extra = world.CreateItem();
        extra.ItemType = ItemType.Container;
        vendor.Equip(extra, Layer.VendorExtra);

        return (vendor, stock, extra);
    }

    private static Item AddStockRow(GameWorld world, Item stock, ushort baseId, ushort amount, int price)
    {
        var row = world.CreateItem();
        row.BaseId = baseId;
        row.Amount = amount;
        row.SetTag("PRICE", price.ToString());
        stock.AddItem(row);
        return row;
    }

    // --- SX-01A-01: non-stackable multi-buy ---------------------------------

    [Fact]
    public void BuyingThreeNonStackableItemsDeliversThreeObjects()
    {
        var world = CreateWorld();
        var buyer = MakePlayer(world, 100);
        var (vendor, stock, _) = MakeVendor(world, buyer.Position);
        var row = AddStockRow(world, stock, 0x0F52, 5, 10);   // dagger, not stackable
        Assert.False(row.IsStackable);

        Assert.Equal(30, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 3 }]));

        var delivered = buyer.Backpack!.Contents.Where(i => i.BaseId == 0x0F52).ToList();
        Assert.Equal(3, delivered.Count);
        Assert.All(delivered, i => Assert.Equal(1, i.Amount));
        Assert.Equal(3, delivered.Select(i => i.Uid).Distinct().Count());
        Assert.Equal(2, row.Amount);
    }

    [Fact]
    public void BuyingThreeStackableItemsStillDeliversOnePile()
    {
        var world = CreateWorld();
        var buyer = MakePlayer(world, 100);
        LoadPileDef();
        var (vendor, stock, _) = MakeVendor(world, buyer.Position);
        var row = AddStockRow(world, stock, 0x0F7A, 5, 10);   // reagent
        Assert.True(row.IsStackable);

        Assert.Equal(30, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 3 }]));

        var delivered = buyer.Backpack!.Contents.Where(i => i.BaseId == 0x0F7A).ToList();
        Assert.Single(delivered);
        Assert.Equal(3, delivered[0].Amount);
    }

    // --- SX-01A-02: player vendor hands over the real object ----------------

    [Fact]
    public void BuyingAFullBagFromAPlayerVendorKeepsItsContentsAndUid()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 0, 90);
        var buyer = MakePlayer(world, 100);
        var (vendor, stock, _) = MakeVendor(world, buyer.Position, owner);

        var bag = world.CreateItem();
        bag.ItemType = ItemType.Container;
        bag.SetTag("PRICE", "10");
        stock.AddItem(bag);

        var cargo = world.CreateItem();
        cargo.BaseId = 0x0F52;
        cargo.SetTag("HEIRLOOM", "yes");
        Assert.True(bag.TryAddItem(cargo));
        var bagUid = bag.Uid;
        var cargoUid = cargo.Uid;

        Assert.Equal(10, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = bag.Uid, Amount = 1 }]));

        Assert.False(bag.IsDeleted, "the original bag was destroyed");
        Assert.Equal(bagUid, bag.Uid);
        Assert.Contains(bag, buyer.Backpack!.Contents);

        Assert.False(cargo.IsDeleted, "the bag's contents were destroyed");
        Assert.Equal(cargoUid, cargo.Uid);
        Assert.Contains(cargo, bag.Contents);
        Assert.True(cargo.TryGetTag("HEIRLOOM", out _));
    }

    [Fact]
    public void APartialBuyFromAPlayerVendorStillLeavesTheRemainder()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 0, 90);
        var buyer = MakePlayer(world, 100);
        LoadPileDef();
        var (vendor, stock, _) = MakeVendor(world, buyer.Position, owner);
        var row = AddStockRow(world, stock, 0x0F7A, 5, 10);

        Assert.Equal(20, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 2 }]));

        Assert.False(row.IsDeleted);
        Assert.Equal(3, row.Amount);
        Assert.Contains(stock.Contents, i => i.Uid == row.Uid);
        Assert.Equal(2, buyer.Backpack!.Contents.First(i => i.BaseId == 0x0F7A).Amount);
    }

    [Fact]
    public void AnNpcVendorStillSellsFromItsVirtualTemplate()
    {
        // No owner: the stock row is a template entry, so a clone is correct and
        // the row is consumed.
        var world = CreateWorld();
        var buyer = MakePlayer(world, 100);
        var (vendor, stock, _) = MakeVendor(world, buyer.Position);
        var row = AddStockRow(world, stock, 0x0F52, 1, 10);

        Assert.Equal(10, VendorEngine.ProcessBuy(buyer, vendor,
            [new TradeEntry { ItemUid = row.Uid, Amount = 1 }]));

        Assert.True(row.IsDeleted);
        Assert.Contains(buyer.Backpack!.Contents, i => i.BaseId == 0x0F52);
    }

    // --- SX-01A-03: a player vendor keeps what it buys ----------------------

    [Fact]
    public void SellingToAPlayerVendorMovesTheObjectIntoItsExtraStore()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 0, 90);
        var seller = MakePlayer(world, 0);
        var (vendor, stock, extra) = MakeVendor(world, seller.Position, owner);
        vendor.SetTag("VENDOR_GOLD", "1000");
        AddStockRow(world, stock, 0x0F52, 1, 10);   // vendor deals in this item

        var goods = world.CreateItem();
        goods.BaseId = 0x0F52;
        Assert.True(seller.Backpack!.TryAddItem(goods));
        var goodsUid = goods.Uid;

        int paid = VendorEngine.ProcessSell(seller, vendor,
            [new TradeEntry { ItemUid = goods.Uid, Amount = 1 }]);

        Assert.True(paid > 0);
        Assert.False(goods.IsDeleted, "the vendor destroyed what it paid for");
        Assert.Equal(goodsUid, goods.Uid);
        Assert.Contains(goods, extra.Contents);
    }

    [Fact]
    public void SellingToAnOwnerlessNpcVendorStillDestroysTheGoods()
    {
        var world = CreateWorld();
        var seller = MakePlayer(world, 0);
        var (vendor, stock, _) = MakeVendor(world, seller.Position);
        vendor.SetTag("VENDOR_GOLD", "1000");
        AddStockRow(world, stock, 0x0F52, 1, 10);

        var goods = world.CreateItem();
        goods.BaseId = 0x0F52;
        Assert.True(seller.Backpack!.TryAddItem(goods));

        Assert.True(VendorEngine.ProcessSell(seller, vendor,
            [new TradeEntry { ItemUid = goods.Uid, Amount = 1 }]) > 0);
        Assert.True(goods.IsDeleted);
    }

    [Fact]
    public void APlayerVendorsBoughtGoodsSurviveASaveAndLoad()
    {
        // The extra container used to be excluded from the world save alongside the
        // virtual SELL stock, so everything a player vendor bought vanished on the
        // next restart.
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_vextra_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var world = CreateWorld();
            var owner = MakePlayer(world, 0, 90);
            var (vendor, _, extra) = MakeVendor(world, new Point3D(100, 100, 0, 0), owner);

            var goods = world.CreateItem();
            goods.BaseId = 0x0F52;
            goods.SetTag("BOUGHT_FROM", "alice");
            Assert.True(extra.TryAddItem(goods));
            var goodsUid = goods.Uid;

            var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
            {
                Format = SaveFormat.Text,
                ShardCount = 0,
            };
            Assert.True(saver.Save(world, dir));

            var reloaded = new GameWorld(LoggerFactory.Create(_ => { }));
            reloaded.InitMap(0, 6144, 4096);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => reloaded;
            Item.ResolveWorld = () => reloaded;
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, dir);

            var back = reloaded.FindItem(goodsUid);
            Assert.NotNull(back);
            Assert.True(back!.TryGetTag("BOUGHT_FROM", out string? who) && who == "alice");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
