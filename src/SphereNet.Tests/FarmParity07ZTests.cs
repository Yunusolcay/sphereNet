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
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Farming, the hive and the water's edge.
///
/// Source-X plants a seed only in soil, refuses ground a tree already holds and
/// REPLACES an existing crop rather than stacking a second one on it (Use_Seed,
/// CCharUse.cpp:1467). Reaping runs @ResourceTest first with the growth stage and
/// the fruit, refuses a stage that still has somewhere to grow, then runs
/// @ResourceGather with the amount and the produce, which keeps its own definition's
/// type (Plant_Use, CItemPlant.cpp:21). A hive spends the stock in MORE1 and rests
/// 15 minutes, refilling to five on its own tick (CCharUse.cpp:1692; CItem.cpp:6380).
/// A pitcher is filled from whatever the player pointed at, resolved through
/// CanTouchStatic (CClientTarg.cpp:2340), not from the land tile under the cursor.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class FarmParity07ZTests
{
    private const ushort SeedTile = 0x0DCF;
    private const ushort RipeCrop = 0x3186;
    private const ushort YoungCrop = 0x0C85;
    private const ushort CottonId = 0x0DF9;
    private const ushort DirtStatic = 0x3573;
    private const ushort WetLand = 0x00A9;

    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack,
        MapDataManager Map);

    private static Bench Setup(TriggerDispatcher? triggers = null,
        PrivLevel priv = PrivLevel.Player, bool soil = false, bool wet = false)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: wet ? WetLand : (ushort)3);
        map.SetSyntheticLandTile(WetLand, new LandTileData { Flags = TileFlag.Wet, Name = "water" });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8001);
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

        var bench = new Bench(world, client, me, pack, map);
        if (soil)
            Soil(bench, PlantSpot);
        return bench;
    }

    private static readonly Point3D PlantSpot = new(101, 100, 0, 0);

    private static void Soil(Bench bench, Point3D at)
    {
        var dirt = bench.World.CreateItem();
        dirt.BaseId = DirtStatic;
        dirt.ItemType = ItemType.Dirt;
        bench.World.PlaceItem(dirt, at);
    }

    private static void DefineItem(int baseId, Action<ItemDef> shape)
    {
        var def = new ItemDef(new ResourceId(ResType.ItemDef, baseId));
        shape(def);
        var table = (Dictionary<int, ItemDef>)typeof(SphereNet.Game.Definitions.DefinitionLoader)
            .GetField("_itemDefs", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        table[baseId] = def;
    }

    private static Item Seed(Bench bench)
    {
        DefineItem(SeedTile, d => { d.Type = ItemType.Seed; d.TData1 = YoungCrop; });
        var seed = bench.World.CreateItem();
        seed.BaseId = SeedTile;
        seed.ItemType = ItemType.Seed;
        seed.Amount = 2;
        Assert.True(bench.Pack.TryAddItem(seed));
        return seed;
    }

    private static void Plant(Bench bench, Item seed, Point3D at)
    {
        bench.Client.HandleDoubleClick(seed.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId, 0,
            at.X, at.Y, at.Z, 0);
    }

    private static int CropsAt(Bench bench, Point3D at) =>
        bench.World.GetItemsInRange(at, 0).Count(i => i.ItemType == ItemType.Crops);

    // --- SX-07Z-01: where a seed may go -----------------------------------

    [Fact]
    public void ASeedNeedsSoil()
    {
        var bench = Setup();
        var seed = Seed(bench);

        Plant(bench, seed, PlantSpot);

        Assert.Equal(0, CropsAt(bench, PlantSpot));
        Assert.Equal(2, seed.Amount);       // nothing spent either
    }

    [Fact]
    public void SoilTakesTheSeed()
    {
        var bench = Setup(soil: true);
        var seed = Seed(bench);

        Plant(bench, seed, PlantSpot);

        Assert.Equal(1, CropsAt(bench, PlantSpot));
        Assert.Equal(1, seed.Amount);
    }

    [Fact]
    public void StaffMayPlantAnywhere()
    {
        var bench = Setup(priv: PrivLevel.GM);
        var seed = Seed(bench);

        Plant(bench, seed, PlantSpot);

        Assert.Equal(1, CropsAt(bench, PlantSpot));
    }

    [Fact]
    public void ATreeRefusesTheGround()
    {
        var bench = Setup(soil: true);
        var tree = bench.World.CreateItem();
        tree.ItemType = ItemType.Tree;
        bench.World.PlaceItem(tree, PlantSpot);
        var seed = Seed(bench);

        Plant(bench, seed, PlantSpot);

        Assert.Equal(0, CropsAt(bench, PlantSpot));
        Assert.Equal(2, seed.Amount);
        Assert.False(tree.IsDeleted);
    }

    [Fact]
    public void AnExistingCropIsReplacedRatherThanStackedOn()
    {
        var bench = Setup(soil: true);
        var old = bench.World.CreateItem();
        old.BaseId = YoungCrop;
        old.ItemType = ItemType.Crops;
        bench.World.PlaceItem(old, PlantSpot);
        var seed = Seed(bench);

        Plant(bench, seed, PlantSpot);

        Assert.Equal(1, CropsAt(bench, PlantSpot));   // one plot, not two
        Assert.True(old.IsDeleted);
    }

    // --- SX-07Z-02/03/04: reaping -----------------------------------------

    private static Item Crop(Bench bench, ushort id, ushort grow, ushort fruit)
    {
        DefineItem(id, d =>
        {
            d.Type = ItemType.Crops;
            d.TData1 = YoungCrop;
            d.TData2 = grow;
            d.TData3 = fruit;
        });
        DefineItem(fruit, d => d.Type = ItemType.Cotton);

        var crop = bench.World.CreateItem();
        crop.BaseId = id;
        crop.ItemType = ItemType.Crops;
        bench.World.PlaceItem(crop, PlantSpot);
        return crop;
    }

    private static Item? Picked(Bench bench) =>
        bench.Pack.Contents.FirstOrDefault(i => i.BaseId == CottonId);

    [Fact]
    public void ARipePlantGivesItsFruit()
    {
        var bench = Setup();
        var crop = Crop(bench, RipeCrop, grow: 0, fruit: CottonId);

        bench.Client.HandleDoubleClick(crop.Uid.Value);

        Assert.NotNull(Picked(bench));
    }

    [Fact]
    public void APlantStillGrowingGivesNothing()
    {
        // TDATA2 says there is another stage to come; the old check only asked
        // whether a fruit was defined at all.
        var bench = Setup();
        var crop = Crop(bench, RipeCrop, grow: 0x3187, fruit: CottonId);

        bench.Client.HandleDoubleClick(crop.Uid.Value);

        Assert.Null(Picked(bench));
        Assert.False(crop.IsAttr(ObjAttributes.Invis));   // not reset either
    }

    [Fact]
    public void ThePickedProduceKeepsItsOwnType()
    {
        var bench = Setup();
        var crop = Crop(bench, RipeCrop, grow: 0, fruit: CottonId);

        bench.Client.HandleDoubleClick(crop.Uid.Value);

        var picked = Picked(bench);
        Assert.NotNull(picked);
        Assert.Equal(ItemType.Cotton, picked!.ItemType);   // cotton is not food
    }

    [Fact]
    public void AResourceTestVetoStopsTheHarvest()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "ResourceTest", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);
        var crop = Crop(bench, RipeCrop, grow: 0, fruit: CottonId);

        bench.Client.HandleDoubleClick(crop.Uid.Value);

        Assert.Null(Picked(bench));
        Assert.False(crop.IsAttr(ObjAttributes.Invis));
    }

    [Fact]
    public void AResourceTestMayRipenThePlantItself()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "ResourceTest", (_, args) =>
        {
            Assert.Equal(0x3187, args.N1);      // the stage still to come
            Assert.Equal(CottonId, args.N2);
            args.N1 = 0;                        // ripe after all
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);
        var crop = Crop(bench, RipeCrop, grow: 0x3187, fruit: CottonId);

        bench.Client.HandleDoubleClick(crop.Uid.Value);

        Assert.NotNull(Picked(bench));
    }

    [Fact]
    public void AResourceGatherVetoDestroysTheProduceAndLeavesThePlant()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "ResourceGather", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);
        var crop = Crop(bench, RipeCrop, grow: 0, fruit: CottonId);

        bench.Client.HandleDoubleClick(crop.Uid.Value);

        Assert.Null(Picked(bench));
        Assert.False(crop.IsAttr(ObjAttributes.Invis));   // no crop reset on a veto
    }

    [Fact]
    public void AResourceGatherMayChooseTheAmount()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "ResourceGather", (_, args) =>
        {
            Assert.Equal(1, args.N1);
            args.N1 = 3;
            return TriggerResult.Default;
        });
        var bench = Setup(triggers);
        var crop = Crop(bench, RipeCrop, grow: 0, fruit: CottonId);

        bench.Client.HandleDoubleClick(crop.Uid.Value);

        Assert.Equal((ushort)3, Picked(bench)?.Amount);
    }

    // --- SX-07Z-05: the hive --------------------------------------------

    private static Item Hive(Bench bench, uint stock)
    {
        var hive = bench.World.CreateItem();
        hive.ItemType = ItemType.BeeHive;
        hive.More1 = stock;
        bench.World.PlaceItem(hive, PlantSpot);
        return hive;
    }

    private static int Harvested(Bench bench) =>
        bench.Pack.Contents.Count(i => i.BaseId is 0x09EC or 0x1423);

    [Fact]
    public void AnEmptyHiveGivesNothingHoweverOftenItIsTried()
    {
        var bench = Setup();
        var hive = Hive(bench, 0);

        for (int i = 0; i < 40; i++)
            bench.Client.HandleDoubleClick(hive.Uid.Value);

        Assert.Equal(0, Harvested(bench));
        Assert.Equal(0u, hive.More1);
    }

    [Fact]
    public void AHiveGivesAtMostItsStock()
    {
        var bench = Setup();
        var hive = Hive(bench, 1);

        for (int i = 0; i < 40; i++)
            bench.Client.HandleDoubleClick(hive.Uid.Value);

        Assert.True(Harvested(bench) <= 1, "one unit of stock, at most one product");
        Assert.Equal(0u, hive.More1 == 0 ? 0u : 1u);
    }

    [Fact]
    public void AUsedHiveGoesQuiet()
    {
        var bench = Setup();
        var hive = Hive(bench, 3);

        bench.Client.HandleDoubleClick(hive.Uid.Value);

        Assert.True(hive.Timeout > 0);
    }

    [Fact]
    public void AHiveRefillsOnItsOwnTickUpToFive()
    {
        var bench = Setup();
        var hive = Hive(bench, 4);

        hive.SetTimeout(1);                       // due now
        hive.OnTick();
        Assert.Equal(5u, hive.More1);

        hive.SetTimeout(1);
        hive.OnTick();
        Assert.Equal(5u, hive.More1);             // and no further
    }

    // --- SX-07Z-06: filling a pitcher ------------------------------------

    private static Item Pitcher(Bench bench)
    {
        var pitcher = bench.World.CreateItem();
        pitcher.BaseId = 0x0FF6;
        pitcher.ItemType = ItemType.PitcherEmpty;
        Assert.True(bench.Pack.TryAddItem(pitcher));
        return pitcher;
    }

    [Theory]
    [InlineData(ItemType.Water)]
    [InlineData(ItemType.WaterWash)]
    public void AWaterSourceStandingOnDryGroundStillFillsIt(ItemType kind)
    {
        var bench = Setup();
        var trough = bench.World.CreateItem();
        trough.ItemType = kind;
        bench.World.PlaceItem(trough, PlantSpot);
        var pitcher = Pitcher(bench);

        bench.Client.HandleDoubleClick(pitcher.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            trough.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(ItemType.Pitcher, pitcher.ItemType);
    }

    [Fact]
    public void DryGroundStillFillsNothing()
    {
        var bench = Setup();
        var pitcher = Pitcher(bench);

        bench.Client.HandleDoubleClick(pitcher.Uid.Value);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId, 0,
            PlantSpot.X, PlantSpot.Y, 0, 0);

        Assert.Equal(ItemType.PitcherEmpty, pitcher.ItemType);
    }

    [Fact]
    public void OpenWaterStillFillsIt()
    {
        var bench = Setup(wet: true);
        var pitcher = Pitcher(bench);

        bench.Client.HandleDoubleClick(pitcher.Uid.Value);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId, 0,
            PlantSpot.X, PlantSpot.Y, 0, 0);

        Assert.Equal(ItemType.Pitcher, pitcher.ItemType);
    }
}
