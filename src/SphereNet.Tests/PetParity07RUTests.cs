using System.Reflection;
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
/// What a pet wears, and what it puts down when told to.
///
/// Source-X routes the spoken equip through ItemEquip (CCharAct.cpp:3313): the
/// layer is scored by CanEquipLayer, which pairs the two hands (CCharStatus.cpp:410),
/// @EquipTest gets its veto before anything moves, a pile leaves all but one piece
/// behind (UnStackSplit, CItem.cpp:1251) and @Equip runs once the item is worn.
/// "Drop all" is DropAll (CCharNPCPet.cpp:255 -> CCharAct.cpp:564), which dumps the
/// pack and then moves the worn equipment into it (UnEquipAllItems, :592) - a
/// different command from plain "drop", which only empties the pack.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PetParity07RUTests
{
    private const ushort SwordTile = 0x0530;    // synthetic Wearable, one-handed slot
    private const ushort BowTile = 0x0531;      // synthetic Wearable, two-handed slot
    private const ushort ShieldTile = 0x0532;   // synthetic Wearable, two-handed slot

    private sealed record Bench(
        GameWorld World, GameClient Client, Character Owner, Character Pet, Item Pack);

    private static Bench Setup(TriggerDispatcher? triggers = null)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(SwordTile, new ItemTileData
        { Flags = TileFlag.Wearable, Quality = (byte)Layer.OneHanded });
        map.SetSyntheticItemTile(BowTile, new ItemTileData
        { Flags = TileFlag.Wearable, Quality = (byte)Layer.TwoHanded });
        map.SetSyntheticItemTile(ShieldTile, new ItemTileData
        { Flags = TileFlag.Wearable, Quality = (byte)Layer.TwoHanded });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7801);
        if (triggers != null)
            typeof(GameClient)
                .GetField("_triggerDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, triggers);

        var owner = Being(world, new Point3D(100, 100, 0, 0), player: true);
        owner.MaxFollower = 5;
        TestHarness.AttachCharacter(client, owner);

        var pet = Being(world, new Point3D(101, 100, 0, 0));
        pet.Name = "reviewpet";
        pet.TryAssignOwnership(owner, owner);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pet.Backpack = pack;
        pet.Equip(pack, Layer.Pack);

        return new Bench(world, client, owner, pet, pack);
    }

    private static Character Being(GameWorld world, Point3D at, bool player = false)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;
        ch.Dex = 100; ch.Stam = 100; ch.Int = 100;
        world.PlaceCharacter(ch, at);
        return ch;
    }

    private static Item Gear(GameWorld world, ushort tile, ItemType type)
    {
        var item = world.CreateItem();
        item.BaseId = tile;
        item.ItemType = type;
        return item;
    }

    private static Item Sword(GameWorld world) => Gear(world, SwordTile, ItemType.WeaponSword);
    private static Item Bow(GameWorld world) => Gear(world, BowTile, ItemType.WeaponBow);
    private static Item Shield(GameWorld world) => Gear(world, ShieldTile, ItemType.Shield);

    // --- SX-07R-01: the two hands are one decision -------------------------

    [Fact]
    public void ASwordInHandRulesOutABowAsWell()
    {
        // The command only checked the bow's OWN layer, so a pet held a sword and a
        // two-handed bow at the same time.
        var bench = Setup();
        var sword = Sword(bench.World);
        bench.Pet.Equip(sword, Layer.OneHanded);

        var bow = Bow(bench.World);
        Assert.True(bench.Pack.TryAddItem(bow));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet equip");

        Assert.Contains(bow, bench.Pack.Contents);
        Assert.Null(bench.Pet.GetEquippedItem(Layer.TwoHanded));
        Assert.Same(sword, bench.Pet.GetEquippedItem(Layer.OneHanded));
    }

    [Fact]
    public void ABowInHandRulesOutASwordAsWell()
    {
        var bench = Setup();
        var bow = Bow(bench.World);
        bench.Pet.Equip(bow, Layer.TwoHanded);

        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet equip");

        Assert.Contains(sword, bench.Pack.Contents);
        Assert.Null(bench.Pet.GetEquippedItem(Layer.OneHanded));
    }

    [Fact]
    public void OnlyOneOfTwoWeaponsInThePackIsTakenUp()
    {
        var bench = Setup();
        var sword = Sword(bench.World);
        var bow = Bow(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));
        Assert.True(bench.Pack.TryAddItem(bow));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet equip");

        bool swordWorn = ReferenceEquals(sword, bench.Pet.GetEquippedItem(Layer.OneHanded));
        bool bowWorn = ReferenceEquals(bow, bench.Pet.GetEquippedItem(Layer.TwoHanded));
        Assert.True(swordWorn ^ bowWorn, "exactly one weapon should end up in hand");
        Assert.Single(bench.Pack.Contents);
    }

    [Fact]
    public void AShieldStillPairsWithASword()
    {
        // A shield is not a weapon, so it does not answer CCPropsItemWeapon and the
        // reference leaves the sword hand alone.
        var bench = Setup();
        var shield = Shield(bench.World);
        bench.Pet.Equip(shield, Layer.TwoHanded);

        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet equip");

        Assert.Same(sword, bench.Pet.GetEquippedItem(Layer.OneHanded));
        Assert.Same(shield, bench.Pet.GetEquippedItem(Layer.TwoHanded));
    }

    // --- SX-07S-01: the script gets its say --------------------------------

    [Fact]
    public void AVetoedItemStaysInThePack()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "EquipTest", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);

        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet equip");

        Assert.Contains(sword, bench.Pack.Contents);
        Assert.Null(bench.Pet.GetEquippedItem(Layer.OneHanded));
        Assert.False(sword.IsEquipped);
    }

    [Fact]
    public void TheEquipTriggerRunsOnceTheItemIsWorn()
    {
        int equipCalls = 0;
        Character? wearer = null;
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Equip", (_, args) =>
        {
            equipCalls++;
            wearer = args.CharSrc;
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);

        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet equip");

        Assert.Same(sword, bench.Pet.GetEquippedItem(Layer.OneHanded));
        Assert.Equal(1, equipCalls);
        Assert.Same(bench.Pet, wearer);   // the wearer is the NPC, as ItemEquip passes it
    }

    // --- SX-07T-01: a pile wears one piece ---------------------------------

    [Fact]
    public void APetWearsOnePieceOfAPileAndLeavesTheRest()
    {
        var bench = Setup();
        var pile = Sword(bench.World);
        pile.Amount = 5;
        pile.SetTag("KEEPSAKE", "yes");
        Assert.True(bench.Pack.TryAddItem(pile));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet equip");

        var worn = bench.Pet.GetEquippedItem(Layer.OneHanded);
        Assert.NotNull(worn);
        Assert.Equal(1, worn!.Amount);

        var remainder = Assert.Single(bench.Pack.Contents);
        Assert.Equal(4, remainder.Amount);
        Assert.NotSame(worn, remainder);

        // Nothing minted, nothing lost - and the leftover is a full clone.
        Assert.Equal(5, worn.Amount + remainder.Amount);
        Assert.True(remainder.TryGetTag("KEEPSAKE", out string? kept));
        Assert.Equal("yes", kept);
    }

    [Fact]
    public void ASinglePieceIsWornWithoutSplittingAnything()
    {
        var bench = Setup();
        var sword = Sword(bench.World);
        Assert.True(bench.Pack.TryAddItem(sword));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet equip");

        Assert.Same(sword, bench.Pet.GetEquippedItem(Layer.OneHanded));
        Assert.Empty(bench.Pack.Contents);
    }

    // --- SX-07U-01: drop all is not drop -----------------------------------

    private static Item Loot(GameWorld world)
    {
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED;
        gold.ItemType = ItemType.Gold;
        gold.Amount = 10;
        return gold;
    }

    [Fact]
    public void DropAllTakesTheWeaponOffToo()
    {
        var bench = Setup();
        var sword = Sword(bench.World);
        bench.Pet.Equip(sword, Layer.OneHanded);
        var gold = Loot(bench.World);
        Assert.True(bench.Pack.TryAddItem(gold));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet drop all");

        Assert.False(sword.IsEquipped);
        Assert.Null(bench.Pet.GetEquippedItem(Layer.OneHanded));
        Assert.Contains(sword, bench.Pack.Contents);        // into the pack, not the dirt

        Assert.False(gold.ContainedIn.IsValid);             // the pack went to the ground
        Assert.Equal(bench.Pet.Position, gold.Position);
        Assert.DoesNotContain(gold, bench.Pack.Contents);
    }

    [Fact]
    public void AnEmptyPackDoesNotEndTheCommandEarly()
    {
        // Both branches bailed out on an empty pack, so a pet carrying nothing but a
        // drawn weapon kept it.
        var bench = Setup();
        var sword = Sword(bench.World);
        bench.Pet.Equip(sword, Layer.OneHanded);

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet drop all");

        Assert.False(sword.IsEquipped);
        Assert.Contains(sword, bench.Pack.Contents);
    }

    [Fact]
    public void PlainDropLeavesTheEquipmentAlone()
    {
        var bench = Setup();
        var sword = Sword(bench.World);
        bench.Pet.Equip(sword, Layer.OneHanded);
        var gold = Loot(bench.World);
        Assert.True(bench.Pack.TryAddItem(gold));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet drop");

        Assert.Same(sword, bench.Pet.GetEquippedItem(Layer.OneHanded));
        Assert.False(gold.ContainedIn.IsValid);
    }

    [Fact]
    public void ThePackAndTheMountStayWhereTheyAre()
    {
        var bench = Setup();
        var mount = bench.World.CreateItem();
        mount.ItemType = ItemType.EqHorse;
        bench.Pet.Equip(mount, Layer.Horse);

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet drop all");

        Assert.Same(bench.Pack, bench.Pet.GetEquippedItem(Layer.Pack));
        Assert.Same(mount, bench.Pet.GetEquippedItem(Layer.Horse));
    }

    [Fact]
    public void AConjuredPetDropsNothing()
    {
        // DropAll returns before touching anything for a conjured creature
        // (CCharAct.cpp:567) - its gear leaves with it.
        var bench = Setup();
        bench.Pet.SetStatFlag(StatFlag.Conjured);
        var sword = Sword(bench.World);
        bench.Pet.Equip(sword, Layer.OneHanded);
        var gold = Loot(bench.World);
        Assert.True(bench.Pack.TryAddItem(gold));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet drop all");

        Assert.Same(sword, bench.Pet.GetEquippedItem(Layer.OneHanded));
        Assert.Contains(gold, bench.Pack.Contents);
    }

    [Theory]
    [InlineData(ObjAttributes.Newbie)]
    [InlineData(ObjAttributes.Blessed2)]
    [InlineData(ObjAttributes.Move_Never)]
    [InlineData(ObjAttributes.Owned)]
    public void ProtectedGoodsAreNotThrownOnTheGround(ObjAttributes attr)
    {
        var bench = Setup();
        var kept = Loot(bench.World);
        kept.SetAttr(attr);
        Assert.True(bench.Pack.TryAddItem(kept));

        bench.Client.HandleSpeech(0, 0, 0, "reviewpet drop");

        Assert.Contains(kept, bench.Pack.Contents);
    }
}
