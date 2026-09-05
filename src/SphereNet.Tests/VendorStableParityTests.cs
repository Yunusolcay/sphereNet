using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X vendor/stable parity (wiki/10.txt audit): claiming a stabled pet does
// not lose it when the follower cap is full, and the buy price comes from the
// selected stock entry (not the first same-BaseId entry).
public class VendorStableParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item AddPack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    // ---- #8: ClaimPet must not drop the pet when the follower cap is full ----

    [Fact]
    public void ClaimPet_FollowerCapFull_KeepsPetStabled()
    {
        var world = CreateWorld();
        var stable = new StableEngine();

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Fido";
        pet.NpcBrain = NpcBrainType.Animal;
        pet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        Assert.True(stable.StablePet(owner, pet, world));
        Assert.Equal(1, stable.GetStabledCount(owner));

        // Cap full → claim must fail WITHOUT removing the pet from the stable.
        owner.MaxFollower = 0;
        var claimedFail = stable.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0));
        Assert.Null(claimedFail);
        Assert.Equal(1, stable.GetStabledCount(owner)); // still stabled, not lost

        // Cap available → claim succeeds and the stable entry is consumed.
        owner.MaxFollower = 5;
        var claimedOk = stable.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0));
        Assert.NotNull(claimedOk);
        Assert.Equal(0, stable.GetStabledCount(owner));
    }

    // ---- Source-X CClientTarg: a pet carrying items in its pack can't be stabled ----

    [Fact]
    public void StablePet_RejectsPetWithNonEmptyPack()
    {
        var world = CreateWorld();
        var stable = new StableEngine();

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Packmule";
        pet.NpcBrain = NpcBrainType.Animal;
        pet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        var pack = AddPack(world, pet);
        var loot = world.CreateItem();
        loot.BaseId = 0x1F03;
        pack.AddItem(loot);

        // A loaded pack blocks stabling — the items would be lost with the snapshot.
        Assert.False(stable.StablePet(owner, pet, world));
        Assert.Equal(0, stable.GetStabledCount(owner));

        // Empty the pack and the same pet stables normally.
        pack.RemoveItem(loot);
        Assert.True(stable.StablePet(owner, pet, world));
        Assert.Equal(1, stable.GetStabledCount(owner));
    }

    // ---- #4: buy price uses the selected stock entry, not the first same-id one ----

    [Fact]
    public void ProcessBuy_SameBaseIdDifferentPrice_ChargesSelectedEntry()
    {
        var world = CreateWorld();
        VendorEngine.World = world;

        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

        var stock = world.CreateItem();
        stock.ItemType = ItemType.Container;
        stock.BaseId = 0x0E75;
        vendor.Equip(stock, Layer.VendorStock);

        // Two stock entries share a BaseId but have different prices; the cheap
        // one is added first (it is what GetServerBuyPrice-by-BaseId returns).
        var cheap = world.CreateItem();
        cheap.BaseId = 0x1F03; cheap.Amount = 5; cheap.SetTag("PRICE", "10");
        stock.AddItem(cheap);
        var dear = world.CreateItem();
        dear.BaseId = 0x1F03; dear.Amount = 5; dear.SetTag("PRICE", "999");
        stock.AddItem(dear);

        var buyer = world.CreateCharacter();
        buyer.IsPlayer = true;
        buyer.Str = short.MaxValue;
        world.PlaceCharacter(buyer, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, buyer);
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED; gold.ItemType = ItemType.Gold; gold.Amount = 5000;
        pack.AddItem(gold);

        // Buy the EXPENSIVE entry — the server must charge 999, not the cheap 10.
        int cost = VendorEngine.ProcessBuy(buyer, vendor,
            new[] { new TradeEntry { ItemUid = dear.Uid, ItemId = dear.BaseId, Amount = 1 } });

        Assert.Equal(999, cost);
    }

    // ---- #10: stable snapshots the FULL skill (no 1000 clip = data loss) ----

    [Fact]
    public void StablePet_SnapshotsFullSkill_NotClippedAt1000()
    {
        var world = CreateWorld();
        var stable = new StableEngine();

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Magus";
        pet.NpcBrain = NpcBrainType.Animal;
        pet.TryAssignOwnership(owner, owner);
        pet.SetSkill(SkillType.Magery, 1200); // 120.0 — above the old 1000 clip
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        Assert.True(stable.StablePet(owner, pet, world));
        var claimed = stable.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0));

        Assert.NotNull(claimed);
        Assert.Equal(1200, claimed!.GetSkill(SkillType.Magery)); // full value, not clipped
    }

    // ---- #10: stable capacity scales with handling skills / MAXPLAYERPETS ----

    [Fact]
    public void GetMaxStabledPets_ScalesWithSkillsAndTagOverride()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();

        Assert.Equal(5, StableEngine.GetMaxStabledPets(owner)); // unskilled = base

        owner.SetSkill(SkillType.Taming, 1200);
        owner.SetSkill(SkillType.AnimalLore, 1200);
        owner.SetSkill(SkillType.Veterinary, 1200); // sum 3600 -> +6 slots
        Assert.Equal(11, StableEngine.GetMaxStabledPets(owner));

        owner.SetTag("MAXPLAYERPETS", "3"); // explicit override wins
        Assert.Equal(3, StableEngine.GetMaxStabledPets(owner));
    }

    // ---- #5: bought item is a FULL clone (per-instance state travels with it) ----

    [Fact]
    public void ProcessBuy_FullClonesStockState()
    {
        var world = CreateWorld();
        VendorEngine.World = world;

        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

        var stock = world.CreateItem();
        stock.ItemType = ItemType.Container;
        stock.BaseId = 0x0E75;
        vendor.Equip(stock, Layer.VendorStock);

        var entry = world.CreateItem();
        entry.BaseId = 0x0F52; entry.Amount = 9; entry.SetTag("PRICE", "10");
        entry.SetTag("CUSTOM_STATE", "kept"); // per-instance state to verify the clone
        stock.AddItem(entry);

        var buyer = world.CreateCharacter();
        buyer.IsPlayer = true;
        buyer.Str = short.MaxValue;
        world.PlaceCharacter(buyer, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, buyer);
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED; gold.ItemType = ItemType.Gold; gold.Amount = 5000;
        pack.AddItem(gold);

        int cost = VendorEngine.ProcessBuy(buyer, vendor,
            new[] { new TradeEntry { ItemUid = entry.Uid, ItemId = entry.BaseId, Amount = 3 } });
        Assert.Equal(30, cost);

        // Each bought item is a full clone: it carries the per-instance tag, not
        // just id/hue/name (the old shallow copy silently dropped it). A
        // non-stackable multi-buy arrives as separate Amount=1 objects, matching
        // Source-X Event_VendorBuy (CClientEvent.cpp:1328).
        var bought = world.GetContainerContents(pack.Uid)
            .Where(i => i.BaseId == 0x0F52).ToList();
        Assert.Equal(3, bought.Count);
        Assert.All(bought, i =>
        {
            Assert.Equal(1, i.Amount);
            Assert.True(i.TryGetTag("CUSTOM_STATE", out string? v));
            Assert.Equal("kept", v);
        });
    }

    // ---- #1: a vendor with no BUY list buys anything (legacy behaviour kept) ----

    [Fact]
    public void GetVendorBuyFilter_NoBuyList_ReturnsNull()
    {
        var world = CreateWorld();
        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;

        // No VENDOR_BUY_LIST tag -> null filter -> the vendor buys anything.
        Assert.Null(VendorEngine.GetVendorBuyFilter(vendor));
    }

    // ---- #2: opt-in vendor money pool — sell debits it, blocks when broke ----

    [Fact]
    public void ProcessSell_VendorMoneyPool_DebitsAndBlocksWhenInsufficient()
    {
        var world = CreateWorld();
        VendorEngine.World = world;

        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, player);

        // PRICE 100 is the marked-up buy price; with the default 15% markup the
        // vendor pays 100*(100-15)/(100+15) = 73 each; 2 -> 146 total (W-F).
        var item = world.CreateItem();
        item.BaseId = 0x13B0; item.Amount = 2; item.SetTag("PRICE", "100");
        pack.AddItem(item);

        // Vendor can afford it: sale succeeds and the pool is debited.
        vendor.SetTag("VENDOR_GOLD", "1000");
        int paid = VendorEngine.ProcessSell(player, vendor,
            new[] { new TradeEntry { ItemUid = item.Uid, ItemId = item.BaseId, Amount = 2 } });
        Assert.Equal(146, paid);
        Assert.Equal(854, VendorEngine.GetVendorGold(vendor));

        // Now broke: a sale it cannot afford is skipped (shortfall), pool and
        // items untouched.
        var item2 = world.CreateItem();
        item2.BaseId = 0x13B0; item2.Amount = 2; item2.SetTag("PRICE", "100");
        pack.AddItem(item2);
        vendor.SetTag("VENDOR_GOLD", "30");
        int paid2 = VendorEngine.ProcessSell(player, vendor,
            new[] { new TradeEntry { ItemUid = item2.Uid, ItemId = item2.BaseId, Amount = 2 } }, out bool shortfall);
        Assert.Equal(0, paid2);
        Assert.True(shortfall);
        Assert.Equal(30, VendorEngine.GetVendorGold(vendor)); // unchanged
        Assert.Equal(2, item2.Amount);                        // items kept
    }

    [Fact]
    public void ProcessSell_UnfundedVendor_PaysNothing_UntilRestockFundsPurse()
    {
        // W-F: the vendor purse is ALWAYS tracked (Source-X m_Check_Amount) —
        // an unfunded vendor buys nothing; a restock tops the purse up.
        var world = CreateWorld();
        VendorEngine.World = world;

        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, player);
        var item = world.CreateItem();
        item.BaseId = 0x13B0; item.Amount = 1; item.SetTag("PRICE", "100");
        pack.AddItem(item);

        int paid = VendorEngine.ProcessSell(player, vendor,
            new[] { new TradeEntry { ItemUid = item.Uid, ItemId = item.BaseId, Amount = 1 } }, out bool shortfall);
        Assert.Equal(0, paid);
        Assert.True(shortfall);
        Assert.False(item.IsDeleted); // nothing was taken

        // Fund the purse the way the engine does — restock.
        VendorEngine.RestockVendor(vendor);
        Assert.True(VendorEngine.GetVendorGold(vendor) >= 100);

        int paid2 = VendorEngine.ProcessSell(player, vendor,
            new[] { new TradeEntry { ItemUid = item.Uid, ItemId = item.BaseId, Amount = 1 } });
        Assert.Equal(73, paid2); // 100*(100-15)/(100+15) with the default markup
        Assert.True(item.IsDeleted);
    }

    // ---- #9: stable rejects summoned / out-of-range pets ----

    [Fact]
    public void StablePet_RejectsSummonedAndDistantPets()
    {
        var world = CreateWorld();
        var stable = new StableEngine();

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        // Summoned pet (near) -> cannot be stabled.
        var summon = world.CreateCharacter();
        summon.NpcBrain = NpcBrainType.Animal;
        summon.TryAssignOwnership(owner, owner);
        summon.SetTag("SUMMON_MASTER", owner.Uid.Value.ToString());
        world.PlaceCharacter(summon, new Point3D(101, 100, 0, 0));
        Assert.True(summon.IsSummoned);
        Assert.False(stable.StablePet(owner, summon, world));

        // Distant pet -> out of range, cannot be stabled.
        var farPet = world.CreateCharacter();
        farPet.NpcBrain = NpcBrainType.Animal;
        farPet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(farPet, new Point3D(200, 200, 0, 0));
        Assert.False(stable.StablePet(owner, farPet, world));

        // A nearby, non-summoned owned pet stables fine.
        var pet = world.CreateCharacter();
        pet.NpcBrain = NpcBrainType.Animal;
        pet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(stable.StablePet(owner, pet, world));
    }

    // ---- #7: double-clicking a bank check redeems it to gold ----

    [Fact]
    public void BankCheck_DoubleClick_RedeemsToGoldAndConsumesCheck()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1850);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, player);
        TestHarness.AttachCharacter(client, player);

        var check = world.CreateItem();
        check.BaseId = 0x14F0;
        check.SetTag("BANKCHECK_AMOUNT", "1234");
        pack.AddItem(check);

        client.HandleDoubleClick(check.Uid.Value);

        Assert.True(check.IsDeleted); // check consumed
        var gold = world.GetContainerContents(pack.Uid)
            .FirstOrDefault(i => i.ItemType == ItemType.Gold);
        Assert.NotNull(gold);
        Assert.Equal(1234, gold!.Amount);
    }
}
