using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SphereNet.Tests;

// Full vendor packet roundtrip. Raw 0x3B (buy) / 0x9F (sell) client bytes are
// decoded by the production PacketVendorBuy/PacketVendorSell handlers, routed
// through NetState to GameClient.HandleVendorBuy/HandleVendorSell, and settle in
// VendorEngine.ProcessBuy/ProcessSell. This locks the wire -> parse -> handler ->
// virtual-stock pipeline end to end. It complements the engine-level
// VendorTradeTests (which call ProcessBuy directly) and the parse-only routing
// cases in PacketManagerTests (bytes -> entries, no settlement). The Wave 19
// virtual-stock model is exercised untouched: stock lives in the vendor stock
// container (LAYER 26), is decremented on buy, and gold is settled in the pack.
[Collection("VendorStateSerial")]
public class VendorPacketRoundtripTests
{
    private static (GameWorld world, AccountManager accounts, ILoggerFactory lf) CreateEnv()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        VendorEngine.World = world;
        return (world, new AccountManager(lf), lf);
    }

    // Wire a GameClient to its NetState exactly as NetworkManager does in
    // production (state.VendorBuyHandler -> client.HandleVendorBuy), so a raw
    // packet fed to the parser reaches the real handler.
    private static NetState WireClient(GameWorld world, AccountManager accounts, ILoggerFactory lf, Character player)
    {
        var state = TestHarness.CreateActiveNetState(lf, 1);
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        TestHarness.AttachCharacter(client, player);
        state.VendorBuyHandler = (_, serial, flag, items) => client.HandleVendorBuy(serial, flag, items);
        state.VendorSellHandler = (_, serial, items) => client.HandleVendorSell(serial, items);
        return state;
    }

    private static (Character vendor, Item stockItem) MakeVendorWithStock(
        GameWorld world, ushort baseId, ushort amount, string? price)
    {
        // CreateCharacter registers the char in the world object table so the
        // handler's _world.FindChar(vendorSerial) can resolve it — PlaceCharacter
        // alone only adds to a sector, not the FindChar index.
        var vendor = world.CreateCharacter();
        vendor.Name = "vendor";
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));
        var stock = world.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var stockItem = world.CreateItem();
        stockItem.BaseId = baseId;
        stockItem.Amount = amount;
        if (price != null) stockItem.SetTag("PRICE", price);
        stock.AddItem(stockItem);
        return (vendor, stockItem);
    }

    private static Character MakePlayer(GameWorld world, int gold)
    {
        var player = world.CreateCharacter();
        player.Name = "buyer";
        player.IsPlayer = true;
        player.Str = short.MaxValue;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        player.Backpack = pack;
        if (gold > 0)
        {
            var coins = world.CreateItem();
            coins.BaseId = 0x0EED;
            coins.ItemType = ItemType.Gold;
            coins.Amount = (ushort)gold;
            pack.AddItem(coins);
        }
        return player;
    }

    private static void PutU32(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16));
        b.Add((byte)(v >> 8)); b.Add((byte)v);
    }

    private static void PutU16(List<byte> b, ushort v)
    {
        b.Add((byte)(v >> 8)); b.Add((byte)v);
    }

    // 0x3B body as positioned for OnReceive (opcode + length already stripped):
    // vendorSerial(4) flag(1) [layer(1) itemSerial(4) amount(2)]...
    private static byte[] BuildBuyPacket(uint vendorSerial, params (byte layer, uint serial, ushort amount)[] items)
    {
        var b = new List<byte>();
        PutU32(b, vendorSerial);
        b.Add(1); // flag != 0 -> item list present
        foreach (var (layer, serial, amount) in items)
        {
            b.Add(layer);
            PutU32(b, serial);
            PutU16(b, amount);
        }
        return [.. b];
    }

    // 0x9F body: vendorSerial(4) count(2) [itemSerial(4) amount(2)]...
    private static byte[] BuildSellPacket(uint vendorSerial, params (uint serial, ushort amount)[] items)
    {
        var b = new List<byte>();
        PutU32(b, vendorSerial);
        PutU16(b, (ushort)items.Length);
        foreach (var (serial, amount) in items)
        {
            PutU32(b, serial);
            PutU16(b, amount);
        }
        return [.. b];
    }

    [Fact]
    public void Buy_RawPacket_DecrementsStockAndChargesGold()
    {
        var (world, accounts, lf) = CreateEnv();
        var (vendor, stockItem) = MakeVendorWithStock(world, 0x0F0E, 10, "5");
        var player = MakePlayer(world, gold: 1000);
        var state = WireClient(world, accounts, lf, player);

        var bytes = BuildBuyPacket(vendor.Uid.Value, (0, stockItem.Uid.Value, 3));
        new PacketVendorBuy().OnReceive(new PacketBuffer(bytes), state);

        Assert.Equal(10 - 3, stockItem.Amount);          // virtual stock decremented
        Assert.Equal(1000 - 15, VendorEngine.CountGold(player)); // 3 * 5 charged
        // A non-stackable multi-buy materialises as separate Amount=1 objects
        // (Source-X Event_VendorBuy, CClientEvent.cpp:1328).
        Assert.Equal(3, player.Backpack!.Contents.Count(
            i => i.BaseId == 0x0F0E && i.Amount == 1));
    }

    [Fact]
    public void Buy_RawPacket_CraftedSerialNotInStockIsRejected()
    {
        var (world, accounts, lf) = CreateEnv();
        var (vendor, stockItem) = MakeVendorWithStock(world, 0x0F0E, 10, "5");
        var player = MakePlayer(world, gold: 1000);
        var state = WireClient(world, accounts, lf, player);

        // A real item that exists in the world but is NOT in the vendor stock.
        var rogue = world.CreateItem();
        rogue.BaseId = 0x0F0E;
        rogue.Amount = 1;
        world.PlaceItem(rogue, new Point3D(50, 50, 0, 0));

        var bytes = BuildBuyPacket(vendor.Uid.Value, (0, rogue.Uid.Value, 1));
        new PacketVendorBuy().OnReceive(new PacketBuffer(bytes), state);

        Assert.Equal(1000, VendorEngine.CountGold(player)); // no gold spent
        Assert.Equal(10, stockItem.Amount);                 // stock untouched
        Assert.DoesNotContain(player.Backpack!.Contents, i => i.Uid == rogue.Uid);
    }

    [Fact]
    public void Sell_RawPacket_RemovesItemAndPaysGold()
    {
        var (world, accounts, lf) = CreateEnv();
        var vendor = world.CreateCharacter();
        vendor.Name = "vendor";
        vendor.NpcBrain = NpcBrainType.Vendor;
        vendor.SetTag("VENDOR_GOLD", "1000"); // funded purse — W-F: the pool is always tracked
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));
        var player = MakePlayer(world, gold: 0);

        // Sellable item in the player's pack, PRICE 10 (the marked-up buy price).
        // W-F markup math (default 15%): payout each = 10*(100-15)/(100+15) = 7.
        var sellItem = world.CreateItem();
        sellItem.BaseId = 0x13B0;
        sellItem.Amount = 2;
        sellItem.SetTag("PRICE", "10");
        player.Backpack!.AddItem(sellItem);

        var state = WireClient(world, accounts, lf, player);

        var bytes = BuildSellPacket(vendor.Uid.Value, (sellItem.Uid.Value, 2));
        new PacketVendorSell().OnReceive(new PacketBuffer(bytes), state);

        Assert.True(sellItem.IsDeleted);                  // whole stack sold
        Assert.Equal(14, VendorEngine.CountGold(player)); // 2 * 7 paid out
    }
}
