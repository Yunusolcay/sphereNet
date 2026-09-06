using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The life of one equip: what the other hand keeps, when the drag ends, and where a
/// refused item lands.
///
/// Source-X pairs the two hands through CCPropsItemWeapon::CanSubscribe - is this a
/// weapon? - so a shield on HAND2 never displaces a HAND1 weapon (CanEquipLayer,
/// CCharStatus.cpp:410). The 0x13 equip request ends the drag mode as soon as it is
/// validated, whatever the outcome (receive.cpp:542), and hands a refused item to
/// Event_Item_Drop_Fail, which puts it back where it was lifted from
/// (CClientEvent.cpp:248). CanEquipLayer refuses a too-weak wearer before it touches
/// any layer conflict (CCharStatus.cpp:333), so a failed equip cannot disarm anyone.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class EquipLifecycleParity07VXTests
{
    private const ushort SwordTile = 0x0540;    // synthetic Wearable, one-handed slot
    private const ushort ShieldTile = 0x0541;   // synthetic Wearable, two-handed slot
    private const ushort BowTile = 0x0542;      // synthetic Wearable, two-handed slot

    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack);

    private static Bench Setup(TriggerDispatcher? triggers = null, int str = 100)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(SwordTile, new ItemTileData
        { Flags = TileFlag.Wearable, Quality = (byte)Layer.OneHanded });
        map.SetSyntheticItemTile(ShieldTile, new ItemTileData
        { Flags = TileFlag.Wearable, Quality = (byte)Layer.TwoHanded });
        map.SetSyntheticItemTile(BowTile, new ItemTileData
        { Flags = TileFlag.Wearable, Quality = (byte)Layer.TwoHanded });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7901);
        if (triggers != null)
            client.SetEngines(triggerDispatcher: triggers);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = (short)str; me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);

        return new Bench(world, client, me, pack);
    }

    private static Item Gear(GameWorld world, ushort tile, ItemType type)
    {
        var item = world.CreateItem();
        item.BaseId = tile;
        item.ItemType = type;
        return item;
    }

    private static Item Sword(GameWorld world) => Gear(world, SwordTile, ItemType.WeaponSword);
    private static Item Shield(GameWorld world) => Gear(world, ShieldTile, ItemType.Shield);
    private static Item Bow(GameWorld world) => Gear(world, BowTile, ItemType.WeaponBow);

    private static Item Gold(GameWorld world)
    {
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED;
        gold.ItemType = ItemType.Gold;
        gold.Amount = 10;
        return gold;
    }

    private static void PickUpAndEquip(Bench bench, Item item, Layer layer)
    {
        bench.Client.HandleItemPickup(item.Uid.Value, item.Amount);
        bench.Client.HandleItemEquip(item.Uid.Value, (byte)layer, bench.Me.Uid.Value);
    }

    // --- SX-07V-01: a shield is not a two-hander --------------------------

    [Fact]
    public void AShieldOnTheOffHandLayerIsNotCalledTwoHanded()
    {
        var bench = Setup();
        var shield = Shield(bench.World);
        bench.Me.Equip(shield, Layer.TwoHanded);

        Assert.False(shield.IsTwoHanded);
    }

    [Fact]
    public void AWeaponOnTheOffHandLayerStillIs()
    {
        var bench = Setup();
        var bow = Bow(bench.World);
        bench.Me.Equip(bow, Layer.TwoHanded);

        Assert.True(bow.IsTwoHanded);
    }

    [Fact]
    public void AShieldStaysOnWhenTheOtherHandTakesASword()
    {
        // Being on the two-handed layer was taken for being two-handed, so equipping
        // a sword sent the shield to the pack.
        var bench = Setup();
        var shield = Shield(bench.World);
        bench.Me.Equip(shield, Layer.TwoHanded);

        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));
        PickUpAndEquip(bench, sword, Layer.OneHanded);

        Assert.Same(sword, bench.Me.GetEquippedItem(Layer.OneHanded));
        Assert.Same(shield, bench.Me.GetEquippedItem(Layer.TwoHanded));
        Assert.DoesNotContain(shield, bench.Pack.Contents);
    }

    [Fact]
    public void ARealTwoHanderStillGivesUpTheOtherHand()
    {
        var bench = Setup();
        var bow = Bow(bench.World);
        bench.Me.Equip(bow, Layer.TwoHanded);

        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));
        PickUpAndEquip(bench, sword, Layer.OneHanded);

        Assert.Same(sword, bench.Me.GetEquippedItem(Layer.OneHanded));
        Assert.Null(bench.Me.GetEquippedItem(Layer.TwoHanded));
        Assert.Contains(bow, bench.Pack.Contents);
    }

    // --- SX-07W-01: a finished equip is a finished drag -------------------

    [Fact]
    public void APickupAfterEquippingDoesNotTakeTheWornItemBack()
    {
        var bench = Setup();
        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));
        PickUpAndEquip(bench, sword, Layer.OneHanded);
        Assert.Same(sword, bench.Me.GetEquippedItem(Layer.OneHanded));

        var gold = Gold(bench.World);
        Assert.True(bench.Pack.TryAddItem(gold));
        bench.Client.HandleItemPickup(gold.Uid.Value, gold.Amount);

        // The stale drag made the next pickup restore the sword out of its layer.
        Assert.Same(sword, bench.Me.GetEquippedItem(Layer.OneHanded));
        Assert.DoesNotContain(sword, bench.Pack.Contents);
        Assert.True(bench.Me.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(gold.Uid.Value.ToString(), held);
    }

    [Fact]
    public void AnItemEquippedOffTheGroundIsNotSentBackToIt()
    {
        var bench = Setup();
        var sword = Sword(bench.World);
        var where = new Point3D(101, 100, 0, 0);
        bench.World.PlaceItem(sword, where);
        PickUpAndEquip(bench, sword, Layer.OneHanded);
        Assert.Same(sword, bench.Me.GetEquippedItem(Layer.OneHanded));

        var gold = Gold(bench.World);
        Assert.True(bench.Pack.TryAddItem(gold));
        bench.Client.HandleItemPickup(gold.Uid.Value, gold.Amount);

        Assert.Same(sword, bench.Me.GetEquippedItem(Layer.OneHanded));
        // Position is not cleared by equipping, so the ground origin is judged by
        // parentage: back on the tile it would belong to no one.
        Assert.Equal(bench.Me.Uid, sword.ContainedIn);
        Assert.DoesNotContain(sword, bench.World.GetItemsInRange(where, 0));
    }

    [Fact]
    public void TheWornItemCanBeLiftedAgain()
    {
        // The stale drag named the sword, so a pickup of that same sword was refused
        // as a repeat of a drag that had already finished.
        var bench = Setup();
        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));
        PickUpAndEquip(bench, sword, Layer.OneHanded);

        bench.Client.HandleItemPickup(sword.Uid.Value, 1);

        Assert.False(sword.IsEquipped);
        Assert.True(bench.Me.TryGetTag("DRAGGING", out string? held));
        Assert.Equal(sword.Uid.Value.ToString(), held);
    }

    // --- SX-07X-01: a refused equip puts the item back --------------------

    private static Item HeavySword(GameWorld world)
    {
        var sword = Sword(world);
        sword.SetTag("OVERRIDE.REQSTR", "80");
        return sword;
    }

    [Fact]
    public void AnItemTooHeavyToWearGoesBackInThePack()
    {
        var bench = Setup(str: 10);
        var sword = HeavySword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));

        bench.Client.HandleEquipMacro([sword.Uid.Value]);

        Assert.False(sword.IsEquipped);
        Assert.Contains(sword, bench.Pack.Contents);
        Assert.False(bench.Me.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void AnEquipThatNeverHappensDoesNotDisarmTheWearer()
    {
        var bench = Setup(str: 10);
        var worn = Sword(bench.World);
        bench.Me.Equip(worn, Layer.OneHanded);

        var heavy = HeavySword(bench.World);
        Assert.True(bench.Pack.TryAddItem(heavy));

        bench.Client.HandleEquipMacro([heavy.Uid.Value]);

        Assert.Same(worn, bench.Me.GetEquippedItem(Layer.OneHanded));
        Assert.Contains(heavy, bench.Pack.Contents);
    }

    [Fact]
    public void AVetoedItemGoesBackInThePack()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "EquipTest", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);

        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));

        bench.Client.HandleEquipMacro([sword.Uid.Value]);

        Assert.False(sword.IsEquipped);
        Assert.Contains(sword, bench.Pack.Contents);
        Assert.False(bench.Me.TryGetTag("DRAGGING", out _));
    }

    [Fact]
    public void AVetoedDragGoesBackToTheContainerItCameFrom()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "EquipTest", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);

        var chest = bench.World.CreateItem();
        chest.ItemType = ItemType.Container;
        Assert.True(bench.Pack.TryAddItem(chest));

        var sword = Sword(bench.World);
        Assert.True(chest.TryAddItem(sword));

        PickUpAndEquip(bench, sword, Layer.OneHanded);

        Assert.False(sword.IsEquipped);
        Assert.Contains(sword, chest.Contents);   // its own slot, not just "the pack"
    }

    [Fact]
    public void APermittedMacroEquipStillSwapsTheLayer()
    {
        var bench = Setup();
        var worn = Sword(bench.World);
        bench.Me.Equip(worn, Layer.OneHanded);

        var better = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(better));

        bench.Client.HandleEquipMacro([better.Uid.Value]);

        Assert.Same(better, bench.Me.GetEquippedItem(Layer.OneHanded));
        Assert.Contains(worn, bench.Pack.Contents);
        Assert.False(bench.Me.TryGetTag("DRAGGING", out _));
    }
}
