using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Using things on other things: taking gear off, cutting cloth, washing bandages,
/// opening a house door and dyeing what may be dyed.
///
/// Source-X fires @Unequip from OnRemoveObj, which every unequip passes through
/// (CCharAct.cpp:398). Scissors CREATE the output and delete the input
/// (CClientTarg.cpp:2110), while bloody bandages are washed in water instead
/// (:2244). A key is matched by LOCK CODE, so a house key opens every door of its
/// multi (Use_Key -> IsKeyLockFit, CItem.cpp:4278). A dye vat hands out its own hue
/// (:2331) onto a target the actor owns and that is clothing or CAN_I_DYE
/// (:2302/2325), and SetHue takes the colour back from @Dye's ARGN1
/// (CObjBase.cpp:324).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemUseParity07YTests
{
    private const ushort ClothTile = 0x1766;
    private const ushort HideTile = 0x1078;
    private const ushort ShirtTile = 0x1517;
    private const ushort WaterTile = 0x00A9;    // synthetic Wet land

    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack);

    private static Bench Setup(TriggerDispatcher? triggers = null,
        PrivLevel priv = PrivLevel.Player)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticLandTile(WaterTile, new LandTileData
        { Flags = TileFlag.Wet, Name = "water" });
        map.SetSyntheticItemTile(ShirtTile, new ItemTileData { Weight = 4 });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7951);
        if (triggers != null)
            client.SetEngines(triggerDispatcher: triggers);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.PrivLevel = priv;
        me.Str = 100; me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);

        return new Bench(world, client, me, pack);
    }

    /// <summary>Put one definition in front of the loader. ResetEngineStatics clears
    /// the table between tests, and the collection serialises them.</summary>
    private static void DefineItem(int baseId, Action<ItemDef> shape)
    {
        var def = new ItemDef(new ResourceId(ResType.ItemDef, baseId));
        shape(def);
        var table = (Dictionary<int, ItemDef>)typeof(SphereNet.Game.Definitions.DefinitionLoader)
            .GetField("_itemDefs", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        table[baseId] = def;
    }

    private static void UseOn(Bench bench, Item tool, uint targetSerial,
        short x = 0, short y = 0)
    {
        bench.Client.HandleDoubleClick(tool.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            targetSerial, x, y, 0, 0);
    }

    // --- SX-07Y-01: taking gear off is still an unequip -------------------

    private static (Bench Bench, Item Worn, Func<int> Calls) UnequipBench(TriggerResult answer)
    {
        int calls = 0;
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Unequip", (_, _) => { calls++; return answer; });
        var bench = Setup(triggers);

        var worn = bench.World.CreateItem();
        worn.ItemType = ItemType.WeaponSword;
        bench.Me.Equip(worn, Layer.OneHanded);
        return (bench, worn, () => calls);
    }

    [Fact]
    public void TheUnequipMacroRunsTheUnequipTrigger()
    {
        var (bench, worn, calls) = UnequipBench(TriggerResult.Default);

        bench.Client.HandleUnequipMacro([(ushort)Layer.OneHanded]);

        Assert.Equal(1, calls());
        Assert.False(worn.IsEquipped);
        Assert.Contains(worn, bench.Pack.Contents);
    }

    [Fact]
    public void AVetoedUnequipKeepsTheItemWorn()
    {
        var (bench, worn, calls) = UnequipBench(TriggerResult.True);

        bench.Client.HandleUnequipMacro([(ushort)Layer.OneHanded]);

        Assert.Equal(1, calls());
        Assert.Same(worn, bench.Me.GetEquippedItem(Layer.OneHanded));
    }

    // --- SX-07Y-02: scissors cut something into something else ------------

    private static Item Scissors(Bench bench)
    {
        var scissors = bench.World.CreateItem();
        scissors.ItemType = ItemType.Scissors;
        Assert.True(bench.Pack.TryAddItem(scissors));
        return scissors;
    }

    [Fact]
    public void ClothIsCutIntoBandages()
    {
        var bench = Setup();
        var scissors = Scissors(bench);
        var cloth = bench.World.CreateItem();
        cloth.BaseId = ClothTile;
        cloth.ItemType = ItemType.Cloth;
        cloth.Amount = 5;
        cloth.Hue = new Color(123);
        Assert.True(bench.Pack.TryAddItem(cloth));

        UseOn(bench, scissors, cloth.Uid.Value);

        Assert.True(cloth.IsDeleted);
        var made = Assert.Single(bench.Pack.Contents, i => i.BaseId == 0x0E21);
        Assert.Equal(5, made.Amount);
        Assert.Equal((ushort)123, made.Hue.Value);
    }

    [Fact]
    public void AHideIsCutIntoLeather()
    {
        var bench = Setup();
        var scissors = Scissors(bench);
        var hide = bench.World.CreateItem();
        hide.BaseId = HideTile;
        hide.ItemType = ItemType.Hide;
        hide.Amount = 5;
        Assert.True(bench.Pack.TryAddItem(hide));

        UseOn(bench, scissors, hide.Uid.Value);

        Assert.True(hide.IsDeleted);
        var made = Assert.Single(bench.Pack.Contents, i => i.BaseId == 0x1067);
        Assert.Equal(5, made.Amount);
    }

    [Fact]
    public void AHideWithItsOwnLeatherProducesThat()
    {
        var bench = Setup();
        DefineItem(HideTile, d => { d.Type = ItemType.Hide; d.TData1 = 0x1079; });
        var scissors = Scissors(bench);
        var hide = bench.World.CreateItem();
        hide.BaseId = HideTile;
        hide.ItemType = ItemType.Hide;
        hide.Amount = 2;
        Assert.True(bench.Pack.TryAddItem(hide));

        UseOn(bench, scissors, hide.Uid.Value);

        Assert.Single(bench.Pack.Contents, i => i.BaseId == 0x1079);
    }

    [Fact]
    public void ClothingIsCutIntoItsWeightInBandages()
    {
        var bench = Setup();
        var scissors = Scissors(bench);
        var shirt = bench.World.CreateItem();
        shirt.BaseId = ShirtTile;              // synthetic tiledata weight 4 stones
        shirt.ItemType = ItemType.Clothing;
        Assert.True(bench.Pack.TryAddItem(shirt));

        UseOn(bench, scissors, shirt.Uid.Value);

        Assert.True(shirt.IsDeleted);
        var made = Assert.Single(bench.Pack.Contents, i => i.BaseId == 0x0E21);
        Assert.Equal(4, made.Amount);
    }

    // --- SX-07Y-03: a house key opens the house ---------------------------

    private static (Item Multi, Item Door) House(Bench bench)
    {
        var multi = bench.World.CreateItem();
        multi.BaseId = 0x4000;
        bench.World.PlaceItem(multi, bench.Me.Position);

        var door = bench.World.CreateItem();
        door.ItemType = ItemType.DoorLocked;
        door.Link = multi.Uid;                  // every component carries the code
        bench.World.PlaceItem(door, bench.Me.Position);
        return (multi, door);
    }

    private static Item RealHouseKey(Bench bench, Item multi)
    {
        var housing = new HousingEngine(bench.World, new MultiRegistry());
        typeof(HousingEngine)
            .GetMethod("CreateHouseKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(housing, [bench.Me, multi, false]);
        return Assert.Single(bench.Pack.Contents, i => i.ItemType == ItemType.Key);
    }

    [Fact]
    public void TheGamesOwnHouseKeyOpensTheHouseDoor()
    {
        // The key carries the multi's code, which the door shares - the active key
        // path demanded the key name the door itself.
        var bench = Setup();
        var (multi, door) = House(bench);
        var key = RealHouseKey(bench, multi);

        UseOn(bench, key, door.Uid.Value);

        Assert.Equal(ItemType.Door, door.ItemType);
    }

    [Fact]
    public void AKeyCutForSomewhereElseStillOpensNothing()
    {
        var bench = Setup();
        var (_, door) = House(bench);

        var stranger = bench.World.CreateItem();
        bench.World.PlaceItem(stranger, bench.Me.Position);
        var key = RealHouseKey(bench, stranger);

        UseOn(bench, key, door.Uid.Value);

        Assert.Equal(ItemType.DoorLocked, door.ItemType);
    }

    [Fact]
    public void AKeyCutForOneDoorStillOpensIt()
    {
        var bench = Setup();
        var door = bench.World.CreateItem();
        door.ItemType = ItemType.DoorLocked;
        bench.World.PlaceItem(door, bench.Me.Position);

        var key = bench.World.CreateItem();
        key.ItemType = ItemType.Key;
        key.SetTag("LINK", door.Uid.Value.ToString());
        Assert.True(bench.Pack.TryAddItem(key));

        UseOn(bench, key, door.Uid.Value);

        Assert.Equal(ItemType.Door, door.ItemType);
    }

    // --- SX-07Y-04/05/06: the dye vat -------------------------------------

    private static Item Vat(Bench bench, ushort hue, string? legacyTag = null)
    {
        var vat = bench.World.CreateItem();
        vat.ItemType = ItemType.DyeVat;
        vat.Hue = new Color(hue);
        if (legacyTag != null)
            vat.SetTag("DYE_HUE", legacyTag);
        Assert.True(bench.Pack.TryAddItem(vat));
        return vat;
    }

    private static Item Shirt(Bench bench, Item? container = null)
    {
        var shirt = bench.World.CreateItem();
        shirt.BaseId = ShirtTile;
        shirt.ItemType = ItemType.Clothing;
        shirt.Hue = new Color(1);
        Assert.True((container ?? bench.Pack).TryAddItem(shirt));
        return shirt;
    }

    [Fact]
    public void TheVatAppliesTheColourItIsWearing()
    {
        var bench = Setup();
        var vat = Vat(bench, 1110);
        var shirt = Shirt(bench);

        UseOn(bench, vat, shirt.Uid.Value);

        Assert.Equal((ushort)1110, shirt.Hue.Value);
    }

    [Fact]
    public void ALegacyTagDoesNotOutrankTheVatsOwnColour()
    {
        var bench = Setup();
        var vat = Vat(bench, 1110, legacyTag: "2220");
        var shirt = Shirt(bench);

        UseOn(bench, vat, shirt.Uid.Value);

        Assert.Equal((ushort)1110, shirt.Hue.Value);
    }

    [Fact]
    public void AColourlessLegacyVatStillWorksFromItsTag()
    {
        var bench = Setup();
        var vat = Vat(bench, 0, legacyTag: "2220");
        var shirt = Shirt(bench);

        UseOn(bench, vat, shirt.Uid.Value);

        Assert.Equal((ushort)2220, shirt.Hue.Value);
    }

    [Fact]
    public void DyeingTheVatColoursTheVatItself()
    {
        var bench = Setup();
        var vat = Vat(bench, 0, legacyTag: "2220");
        var dye = bench.World.CreateItem();
        dye.ItemType = ItemType.Dye;
        dye.Hue = new Color(1110);
        Assert.True(bench.Pack.TryAddItem(dye));

        UseOn(bench, dye, vat.Uid.Value);

        Assert.Equal((ushort)1110, vat.Hue.Value);
        Assert.False(vat.TryGetTag("DYE_HUE", out _));
    }

    [Fact]
    public void SomethingLyingOnTheGroundIsNotDyed()
    {
        var bench = Setup();
        var vat = Vat(bench, 1110);
        var loose = bench.World.CreateItem();
        loose.BaseId = ShirtTile;
        loose.ItemType = ItemType.Clothing;
        loose.Hue = new Color(1);
        bench.World.PlaceItem(loose, bench.Me.Position);

        UseOn(bench, vat, loose.Uid.Value);

        Assert.Equal((ushort)1, loose.Hue.Value);
    }

    [Fact]
    public void SomebodyElsesGoodsAreNotDyed()
    {
        var bench = Setup();
        var vat = Vat(bench, 1110);

        var other = bench.World.CreateCharacter();
        other.IsPlayer = true;
        bench.World.PlaceCharacter(other, new Point3D(101, 100, 0, 0));
        var theirPack = bench.World.CreateItem();
        theirPack.ItemType = ItemType.Container;
        other.Backpack = theirPack;
        other.Equip(theirPack, Layer.Pack);
        var theirs = Shirt(bench, theirPack);

        UseOn(bench, vat, theirs.Uid.Value);

        Assert.Equal((ushort)1, theirs.Hue.Value);
    }

    [Fact]
    public void SomethingThatCannotBeDyedIsNotDyed()
    {
        var bench = Setup();
        DefineItem(0x0EED, d => { d.Type = ItemType.Gold; d.Dye = false; d.Can = CanFlags.None; });
        var vat = Vat(bench, 1110);

        var gold = bench.World.CreateItem();
        gold.BaseId = 0x0EED;
        gold.ItemType = ItemType.Gold;
        gold.Hue = new Color(1);
        Assert.True(bench.Pack.TryAddItem(gold));

        UseOn(bench, vat, gold.Uid.Value);

        Assert.Equal((ushort)1, gold.Hue.Value);
    }

    [Fact]
    public void AnItemMarkedDyeableIsDyed()
    {
        var bench = Setup();
        DefineItem(0x1BC3, d => { d.Type = ItemType.Normal; d.Dye = true; });
        var vat = Vat(bench, 1110);

        var trinket = bench.World.CreateItem();
        trinket.BaseId = 0x1BC3;
        trinket.ItemType = ItemType.Normal;
        trinket.Hue = new Color(1);
        Assert.True(bench.Pack.TryAddItem(trinket));

        UseOn(bench, vat, trinket.Uid.Value);

        Assert.Equal((ushort)1110, trinket.Hue.Value);
    }

    [Fact]
    public void StaffMayStillDyeAnything()
    {
        var bench = Setup(priv: PrivLevel.GM);
        var vat = Vat(bench, 1110);
        var loose = bench.World.CreateItem();
        loose.ItemType = ItemType.Gold;
        loose.Hue = new Color(1);
        bench.World.PlaceItem(loose, bench.Me.Position);

        UseOn(bench, vat, loose.Uid.Value);

        Assert.Equal((ushort)1110, loose.Hue.Value);
    }

    [Fact]
    public void AScriptMayChooseTheColourItself()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Dye", (_, args) =>
        {
            Assert.Equal(1110, args.N1);
            args.N1 = 2220;
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);
        var vat = Vat(bench, 1110);
        var shirt = Shirt(bench);

        UseOn(bench, vat, shirt.Uid.Value);

        Assert.Equal((ushort)2220, shirt.Hue.Value);
    }

    [Fact]
    public void AScriptMayStillRefuseTheDye()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Dye", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);
        var vat = Vat(bench, 1110);
        var shirt = Shirt(bench);

        UseOn(bench, vat, shirt.Uid.Value);

        Assert.Equal((ushort)1, shirt.Hue.Value);
    }
}
