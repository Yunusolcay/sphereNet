using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Picking up, dropping and equipping through the real client handlers.
///
/// Five rules Source-X enforces that the handlers did not:
/// - one item on the cursor at a time (CCharStatus.cpp:459 bounces the previous
///   occupant of LAYER_DRAGGING);
/// - an item only leaves a container the client has actually been shown
///   (CCharAct.cpp:2895, m_openedContainers);
/// - a plain item is not a container, so a drop onto one is redirected to where
///   that item lives (CClientEvent.cpp:489/504);
/// - the drop target's ROOT owner decides whether the transfer is allowed, not the
///   layer of the container the client named (CClientEvent.cpp:338);
/// - a busy equip layer is only freed if what is on it can be moved
///   (CCharStatus.cpp:470).
/// </summary>
public sealed class InventoryTransferParityTests
{
    private static (GameWorld World, GameClient Client, Character Player) Setup(int id = 7100)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.Player;
        player.Str = 100; player.MaxHits = player.Hits = 100;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AddPack(world, player);

        TestHarness.AttachCharacter(client, player);
        return (world, client, player);
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

    /// <summary>A banker beside the player: the self-bank drop path re-checks
    /// proximity, so without one every bank drop is refused for that reason and the
    /// capacity assertions below would prove nothing.</summary>
    private static void AddBanker(GameWorld world, Character near)
    {
        var banker = world.CreateCharacter();
        banker.NpcBrain = NpcBrainType.Banker;
        world.PlaceCharacter(banker, near.Position);
    }

    private static Item NewItem(GameWorld world, ushort baseId = 0x0F51)
    {
        var item = world.CreateItem();
        item.BaseId = baseId;
        return item;
    }

    // --- SX-01B-01: one item on the cursor ---------------------------------

    [Fact]
    public void ASecondPickupSettlesTheFirstItemInsteadOfOrphaningIt()
    {
        var (world, client, player) = Setup();
        var a = NewItem(world);
        var b = NewItem(world, 0x0F52);
        Assert.True(player.Backpack!.TryAddItem(a));
        Assert.True(player.Backpack.TryAddItem(b));

        client.Inventory.HandleItemPickup(a.Uid.Value, 0);
        client.Inventory.HandleItemPickup(b.Uid.Value, 0);

        // A went back to where it was lifted from; only B is on the cursor.
        Assert.Contains(a, player.Backpack.Contents);
        Assert.False(a.IsEquipped);
        Assert.True(player.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(b.Uid.Value.ToString(), held);
    }

    [Fact]
    public void PickingUpTheSameItemTwiceKeepsTheOriginalDrag()
    {
        var (world, client, player) = Setup(7101);
        var a = NewItem(world);
        Assert.True(player.Backpack!.TryAddItem(a));

        client.Inventory.HandleItemPickup(a.Uid.Value, 0);
        client.Inventory.HandleItemPickup(a.Uid.Value, 0);

        Assert.True(player.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(a.Uid.Value.ToString(), held);

        // The lift origin survived, so the item still bounces home on a failed drop.
        client.Inventory.HandleItemDrop(a.Uid.Value, 0, 0, 0, 0xFFFFFFFF);
        Assert.False(a.IsDeleted);
    }

    // --- SX-01B-05: only out of a container you were shown ------------------

    [Fact]
    public void AnItemCannotBeLiftedFromAContainerThatWasNeverOpened()
    {
        var (world, client, player) = Setup(7102);
        var chest = NewItem(world, 0x0E3C);
        chest.ItemType = ItemType.ContainerLocked;
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));

        var loot = NewItem(world);
        Assert.True(chest.TryAddItem(loot));

        client.Inventory.HandleItemPickup(loot.Uid.Value, 0);

        Assert.Contains(loot, chest.Contents);
        Assert.False(player.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void AnItemCanBeLiftedOnceTheContainerHasBeenOpened()
    {
        var (world, client, player) = Setup(7103);
        var chest = NewItem(world, 0x0E3C);
        chest.ItemType = ItemType.Container;
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));

        var loot = NewItem(world);
        Assert.True(chest.TryAddItem(loot));

        client.SendOpenContainer(chest);
        client.Inventory.HandleItemPickup(loot.Uid.Value, 0);

        Assert.True(player.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(loot.Uid.Value.ToString(), held);
    }

    [Fact]
    public void TheOwnBackpackNeverNeedsAnExplicitOpen()
    {
        var (world, client, player) = Setup(7104);
        var item = NewItem(world);
        Assert.True(player.Backpack!.TryAddItem(item));

        client.Inventory.HandleItemPickup(item.Uid.Value, 0);

        Assert.True(player.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void AnOpenedContainerThatMovesAwayStopsCounting()
    {
        var (world, client, player) = Setup(7105);
        var chest = NewItem(world, 0x0E3C);
        chest.ItemType = ItemType.Container;
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));
        var loot = NewItem(world);
        Assert.True(chest.TryAddItem(loot));

        client.SendOpenContainer(chest);

        // Someone carries the chest off; the view the client was given is stale.
        world.HideFromSector(chest);
        world.PlaceItem(chest, new Point3D(140, 140, 0, 0));

        client.Inventory.HandleItemPickup(loot.Uid.Value, 0);
        Assert.Contains(loot, chest.Contents);
    }

    // --- SX-01B-02: a plain item is not a container -------------------------

    [Fact]
    public void DroppingOntoAPlainItemDoesNotMakeItAContainer()
    {
        var (world, client, player) = Setup(7106);
        var sword = NewItem(world, 0x0F5E);
        sword.ItemType = ItemType.WeaponSword;
        Assert.True(player.Backpack!.TryAddItem(sword));

        var gem = NewItem(world, 0x0F16);
        Assert.True(player.Backpack.TryAddItem(gem));

        client.Inventory.HandleItemPickup(gem.Uid.Value, 0);
        client.Inventory.HandleItemDrop(gem.Uid.Value, 0, 0, 0, sword.Uid.Value);

        Assert.Empty(sword.Contents);
        Assert.Contains(gem, player.Backpack.Contents);   // redirected to the sword's own container
    }

    [Fact]
    public void DroppingOntoARealContainerStillInserts()
    {
        var (world, client, player) = Setup(7107);
        var bag = NewItem(world, 0x0E76);
        bag.ItemType = ItemType.Container;
        Assert.True(player.Backpack!.TryAddItem(bag));
        client.SendOpenContainer(bag);

        var gem = NewItem(world, 0x0F16);
        Assert.True(player.Backpack.TryAddItem(gem));

        client.Inventory.HandleItemPickup(gem.Uid.Value, 0);
        client.Inventory.HandleItemDrop(gem.Uid.Value, 0, 0, 0, bag.Uid.Value);

        Assert.Contains(gem, bag.Contents);
    }

    // --- SX-01B-04: the target's root owner decides -------------------------

    [Fact]
    public void AnItemCannotBePushedIntoAnotherPlayersNestedBag()
    {
        var (world, client, player) = Setup(7108);

        var other = world.CreateCharacter();
        other.IsPlayer = true;
        other.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(other, new Point3D(101, 100, 0, 0));
        var otherPack = AddPack(world, other);

        var theirBag = NewItem(world, 0x0E76);
        theirBag.ItemType = ItemType.Container;
        Assert.True(otherPack.TryAddItem(theirBag));

        var mine = NewItem(world);
        Assert.True(player.Backpack!.TryAddItem(mine));

        client.Inventory.HandleItemPickup(mine.Uid.Value, 0);
        client.Inventory.HandleItemDrop(mine.Uid.Value, 0, 0, 0, theirBag.Uid.Value);

        Assert.Empty(theirBag.Contents);
        Assert.Contains(mine, player.Backpack.Contents);
    }

    [Fact]
    public void AnItemCanStillBePutIntoYourOwnPetsPack()
    {
        var (world, client, player) = Setup(7109);

        var pet = world.CreateCharacter();
        pet.NpcBrain = NpcBrainType.Animal;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        pet.TryAssignOwnership(player, player);
        var petPack = AddPack(world, pet);

        var mine = NewItem(world);
        Assert.True(player.Backpack!.TryAddItem(mine));

        client.Inventory.HandleItemPickup(mine.Uid.Value, 0);
        client.Inventory.HandleItemDrop(mine.Uid.Value, 0, 0, 0, petPack.Uid.Value);

        Assert.Contains(mine, petPack.Contents);
    }

    // --- SX-01B-03: the bank counts what is coming in -----------------------

    [Fact]
    public void AFullBagCannotWalkPastTheBankItemLimit()
    {
        var (world, client, player) = Setup(7110);
        world.MaxBankItems = 3;

        AddBanker(world, player);

        var bank = NewItem(world, 0x09AB);
        bank.ItemType = ItemType.Container;
        player.Equip(bank, Layer.BankBox);
        client.SendOpenContainer(bank);

        var bag = NewItem(world, 0x0E76);
        bag.ItemType = ItemType.Container;
        Assert.True(player.Backpack!.TryAddItem(bag));
        for (int i = 0; i < 5; i++)
            Assert.True(bag.TryAddItem(NewItem(world, (ushort)(0x2000 + i))));

        client.Inventory.HandleItemPickup(bag.Uid.Value, 0);
        client.Inventory.HandleItemDrop(bag.Uid.Value, 0, 0, 0, bank.Uid.Value);

        Assert.Empty(bank.Contents);
        Assert.Contains(bag, player.Backpack.Contents);
    }

    [Fact]
    public void AnEmptyBagStillFitsInABankWithRoom()
    {
        var (world, client, player) = Setup(7111);
        world.MaxBankItems = 3;
        AddBanker(world, player);

        var bank = NewItem(world, 0x09AB);
        bank.ItemType = ItemType.Container;
        player.Equip(bank, Layer.BankBox);
        client.SendOpenContainer(bank);

        var bag = NewItem(world, 0x0E76);
        bag.ItemType = ItemType.Container;
        Assert.True(player.Backpack!.TryAddItem(bag));

        client.Inventory.HandleItemPickup(bag.Uid.Value, 0);
        client.Inventory.HandleItemDrop(bag.Uid.Value, 0, 0, 0, bank.Uid.Value);

        Assert.Contains(bag, bank.Contents);
    }

    // --- SX-01B-06: a cursed layer is not freed by equipping over it --------

    [Fact]
    public void EquippingOverACursedItemIsRefused()
    {
        var (world, client, player) = Setup(7112);

        var cursed = NewItem(world, 0x0F5E);
        cursed.ItemType = ItemType.WeaponSword;
        cursed.SetAttr(ObjAttributes.Cursed);
        player.Equip(cursed, Layer.OneHanded);
        Assert.False(ItemMoveRules.CanMove(player, cursed, out _));

        var replacement = NewItem(world, 0x0F5F);
        replacement.ItemType = ItemType.WeaponSword;
        Assert.True(player.Backpack!.TryAddItem(replacement));

        client.Inventory.HandleItemPickup(replacement.Uid.Value, 0);
        client.Inventory.HandleItemEquip(replacement.Uid.Value, (byte)Layer.OneHanded, player.Uid.Value);

        Assert.Equal(cursed, player.GetEquippedItem(Layer.OneHanded));
        Assert.False(replacement.IsEquipped);
    }

    [Fact]
    public void SwappingTwoOrdinaryWeaponsStillWorks()
    {
        var (world, client, player) = Setup(7113);

        var first = NewItem(world, 0x0F5E);
        first.ItemType = ItemType.WeaponSword;
        player.Equip(first, Layer.OneHanded);

        var second = NewItem(world, 0x0F5F);
        second.ItemType = ItemType.WeaponSword;
        Assert.True(player.Backpack!.TryAddItem(second));

        client.Inventory.HandleItemPickup(second.Uid.Value, 0);
        client.Inventory.HandleItemEquip(second.Uid.Value, (byte)Layer.OneHanded, player.Uid.Value);

        Assert.Equal(second, player.GetEquippedItem(Layer.OneHanded));
        Assert.Contains(first, player.Backpack.Contents);
    }
}
