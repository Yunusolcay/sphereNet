using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Components;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Game.World.Sectors;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The environment a client is handed, the world clock, and the champion event.
///
/// Source-X sends the weather with the light on resync (addReSync,
/// CClientMsg.cpp:2107) and resolves it from the SECTOR, which exists everywhere on
/// the map (addWeather, :529). A sector environment change runs @EnvironChange for
/// every active character and only then asks who has a client (CSector.cpp:1310). A
/// linear sector index is resolved against that map's own grid (CWorldMap.cpp:229) and
/// MAPLIST answers from the loaded maps (CServerConfig.cpp:1710). The game clock is
/// saved and restored (CWorld.cpp:1510/1625) and advances by the time that really
/// passed (CWorldClock.cpp:31).
///
/// A champion answers a safe monster count with no list (CCChampion.cpp:640), measures
/// the level threshold against the candles already standing (:441), refuses new candles
/// once the boss is out (:346/:443), lifts each candle four above the altar and runs
/// its ITEMDEF Create (:409/:411), counts the boss spawn like any other (:337), clears
/// its candles when the altar dies (:92) and writes its live LEVELMAX / SPAWNSMAX
/// (:794).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WorldClockChampionParity12Tests
{
    // ================================================================ 12C-1 clock persistence

    [Fact]
    public void TheGameClockSurvivesASaveAndLoad()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 1024, 1024);
        world.SetWorldClockMinutes(473);

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_clock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            new WorldSaver(lf).Save(world, dir);

            var reloaded = new GameWorld(lf);
            reloaded.InitMap(0, 1024, 1024);
            new WorldLoader(lf).Load(reloaded, dir);

            Assert.Equal(473, reloaded.WorldClockMinutes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ASaveFromASaveWithNoClockStillLoads()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 1024, 1024);

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_clock_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            new WorldSaver(lf).Save(world, dir);
            // Strip the clock line the way a save written before this field looks.
            foreach (var file in Directory.GetFiles(dir, "*.scp"))
                File.WriteAllLines(file,
                    File.ReadAllLines(file).Where(l => !l.StartsWith("GAMETIME", StringComparison.OrdinalIgnoreCase)));

            var reloaded = new GameWorld(lf);
            reloaded.InitMap(0, 1024, 1024);
            new WorldLoader(lf).Load(reloaded, dir);

            Assert.Equal(0, reloaded.WorldClockMinutes);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================ 12C-2 clock advance

    [Fact]
    public void ALateTickCatchesUpOnEveryMinuteThatPassed()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        world.GameMinuteLengthMs = 20_000;
        world.SetWorldClockMinutes(100);
        // Pretend the last tick was 100 seconds ago: five game minutes.
        SetLastClockUpdate(world, Environment.TickCount64 - 100_000);

        world.OnTick();

        Assert.Equal(105, world.WorldClockMinutes);
    }

    [Fact]
    public void ATickThatIsNotYetDueDoesNotMoveTheClock()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        world.GameMinuteLengthMs = 20_000;
        world.SetWorldClockMinutes(100);
        SetLastClockUpdate(world, Environment.TickCount64 - 5_000);

        world.OnTick();

        Assert.Equal(100, world.WorldClockMinutes);
    }

    [Fact]
    public void TheLeftoverTimeIsKeptRatherThanDiscarded()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        world.GameMinuteLengthMs = 20_000;
        world.SetWorldClockMinutes(0);
        // 39 seconds: one whole minute plus 19 seconds that must not be thrown away.
        SetLastClockUpdate(world, Environment.TickCount64 - 39_000);

        world.OnTick();
        Assert.Equal(1, world.WorldClockMinutes);

        // Only one more second of real time is needed for the next minute.
        SetLastClockUpdate(world, GetLastClockUpdate(world) - 1_000);
        world.OnTick();
        Assert.Equal(2, world.WorldClockMinutes);
    }

    private static void SetLastClockUpdate(GameWorld world, long value) =>
        typeof(GameWorld).GetField("_lastClockUpdate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(world, value);

    private static long GetLastClockUpdate(GameWorld world) =>
        (long)typeof(GameWorld).GetField("_lastClockUpdate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(world)!;

    // ================================================================ 12B-3 sector index

    [Fact]
    public void ASectorIndexIsResolvedAgainstThatMapsOwnGrid()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256); // 4 columns of 64
        Assert.True(world.TryGetSectorGrid(0, out int cols, out int rows));
        Assert.Equal(4, cols);

        // The first sector of the SECOND row is index 4 here, not 96.
        var second = world.GetSectorByIndex(0, cols);
        Assert.NotNull(second);
        Assert.Equal(0, second!.SectorX);
        Assert.Equal(1, second.SectorY);

        Assert.Null(world.GetSectorByIndex(0, cols * rows));
        Assert.Null(world.GetSectorByIndex(0, -1));
    }

    // ================================================================ 12B-4 MAPLIST

    [Fact]
    public void TheMapRegistryAnswersForEveryLoadedMap()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        world.InitMap(1, 512, 128);

        Assert.Equal(new[] { 0, 1 }, world.LoadedMaps.ToArray());

        Assert.True(world.TryGetMapSize(1, out int w, out int h));
        Assert.Equal(512, w);
        Assert.Equal(128, h);
        Assert.False(world.TryGetMapSize(4, out _, out _));

        Assert.True(world.TryGetSectorGrid(1, out int cols, out int rows));
        Assert.Equal(8, cols);
        Assert.Equal(2, rows);
    }

    // ================================================================ 12B-1 / 12B-2 / 12B-5

    private sealed record Bench(GameWorld World, NetState State, GameClient Client, Character Me);

    private static Bench Setup()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 1024, 1024);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var state = TestHarness.CreateActiveNetState(lf, 1);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return new Bench(world, state, client, me);
    }

    [Fact]
    public void ResyncTellsTheClientWhatTheWeatherIs()
    {
        var b = Setup();
        b.World.ResolveWeather = _ => ((byte)WeatherType.Rain, (byte)30, (byte)15);
        b.Client.Resync();

        var pkt = Assert.Single(TestHarness.GetQueuedPackets(b.State).Where(p => p.Span[0] == 0x65).ToList());
        Assert.Equal((byte)WeatherType.Rain, pkt.Span[1]);
    }

    [Fact]
    public void WithNoWeatherEngineTheClientIsToldItIsDry()
    {
        var b = Setup();
        b.Client.Resync();

        var pkt = Assert.Single(TestHarness.GetQueuedPackets(b.State).Where(p => p.Span[0] == 0x65).ToList());
        Assert.Equal((byte)WeatherType.None, pkt.Span[1]);
    }

    [Fact]
    public void EveryCharacterInTheRegionLearnsOfTheWeatherNotJustTheOnesWithAClient()
    {
        var b = Setup();
        var region = new Region { Name = "the moor", MapIndex = 0 };
        region.AddRect(0, 0, 500, 500);
        b.World.AddRegion(region);

        var npc = b.World.CreateCharacter();
        npc.IsPlayer = false;
        b.World.PlaceCharacter(npc, new Point3D(102, 100, 0, 0));

        var reached = b.World.CharactersInRegion(region).ToList();

        Assert.Contains(b.Me, reached);
        Assert.Contains(npc, reached);
    }

    [Fact]
    public void ACharacterOutsideTheRegionIsNotReached()
    {
        var b = Setup();
        var region = new Region { Name = "small", MapIndex = 0 };
        region.AddRect(0, 0, 50, 50);
        b.World.AddRegion(region);

        Assert.DoesNotContain(b.Me, b.World.CharactersInRegion(region));
    }

    // ================================================================ champion fixture

    private const string ChampionScript = """
        [ITEMDEF 0f13]
        DEFNAME=i_champion_altar
        TYPE=t_spawn_champion

        [CHARDEF c_test_mob]
        DEFNAME=c_test_mob
        ID=0x27
        NAME=test mob

        [CHARDEF c_test_boss12]
        DEFNAME=c_test_boss12
        ID=0x9B
        NAME=test boss

        [CHAMPION champ_12]
        DEFNAME=champ_12
        NAME=Twelve
        LEVELMAX=5
        SPAWNSMAX=100
        NPCGROUP[1]=c_test_mob
        NPCGROUP[2]=c_test_mob
        NPCGROUP[3]=c_test_mob
        NPCGROUP[4]=c_test_mob
        CHAMPIONID=c_test_boss12

        [EOF]
        """;

    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_ch12_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, ChampionScript);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    private static (GameWorld World, Item Altar, ChampionComponent Champ) CreateAltar(
        ResourceHolder resources, string defName = "champ_12", sbyte altarZ = 0)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var altar = world.CreateItem();
        altar.BaseId = 0x1F13;
        altar.ItemType = ItemType.SpawnChampion;
        world.PlaceItem(altar, new Point3D(1000, 1000, altarZ, 0));
        altar.SetTag("MORE1_DEFNAME", defName);
        altar.InitializeSpawnComponent(world, resources);
        return (world, altar, altar.Champion!);
    }

    // ================================================================ 12C-3

    [Fact]
    public void AChampionWithNoDefinitionLinkedStartsWithoutThrowing()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources, defName: "champ_does_not_exist");
        Assert.NotNull(champ);

        var ex = Record.Exception(() => champ.Start());

        Assert.Null(ex);
    }

    // ================================================================ 12C-4

    [Fact]
    public void TheLevelRisesOnTheCandleAfterTheThresholdNotTheOneThatReachesIt()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);
        champ.Start();
        Assert.Equal(4, champ.CandlesNextLevel);

        for (int i = 0; i < 4; i++)
        {
            champ.AddRedCandle();
            // The fourth candle is the one that MEETS the threshold; upstream measures
            // against the candles already standing, so the level is still 1 here.
            Assert.Equal(1, champ.Level);
        }
        Assert.Equal(4, champ.RedCandles.Count);

        champ.AddRedCandle();
        Assert.Equal(2, champ.Level);
    }

    // ================================================================ 12C-5

    [Fact]
    public void ACandleStandsOnTheAltarNotInIt()
    {
        var resources = LoadResources();
        var (world, altar, champ) = CreateAltar(resources, altarZ: 10);
        champ.Start();

        champ.AddWhiteCandle();
        var white = world.FindItem(champ.WhiteCandles[0])!;
        Assert.Equal(14, white.Z);

        champ.AddRedCandle();
        var red = world.FindItem(champ.RedCandles[0])!;
        Assert.Equal(14, red.Z);
        Assert.Equal(altar.Uid, red.Link);
        Assert.True(red.IsAttr(ObjAttributes.Move_Never));
    }

    // ================================================================ 12D-4

    [Fact]
    public void ANewCandleRunsItsOwnItemdefCreate()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);
        champ.Start();
        var created = new List<Item>();
        Item.CreateTriggerHook = created.Add;
        try
        {
            champ.AddWhiteCandle();
            champ.AddRedCandle();
        }
        finally { Item.CreateTriggerHook = null; }

        Assert.Equal(2, created.Count);
    }

    // ================================================================ 12D-3

    [Fact]
    public void TheFinishedRingTakesNoMoreCandles()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);
        champ.Start();
        champ.SetLevel(champ.LevelMax);
        Assert.Empty(champ.RedCandles);
        Assert.Empty(champ.WhiteCandles);

        champ.AddWhiteCandle();
        champ.AddRedCandle();

        Assert.Empty(champ.WhiteCandles);
        Assert.Empty(champ.RedCandles);
    }

    [Fact]
    public void ACandleReadBackFromASaveIsStillRelinkedAtTheFinalLevel()
    {
        var resources = LoadResources();
        var (world, _, champ) = CreateAltar(resources);
        champ.Start();
        var stray = world.CreateItem();
        world.PlaceItem(stray, new Point3D(1001, 1001, 0, 0));
        champ.SetLevel(champ.LevelMax);

        champ.AddRedCandle(stray.Uid);

        Assert.Single(champ.RedCandles);
    }

    // ================================================================ 12D-5

    [Fact]
    public void TheBossCountsLikeAnyOtherSpawn()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);
        champ.Start();

        champ.SetLevel(champ.LevelMax);

        Assert.True(champ.ChampionSummoned.IsValid);
        Assert.Equal(champ.SpawnsMax, champ.SpawnsCur);
        Assert.Equal(0, champ.SpawnsNextWhite);
    }

    // ================================================================ 12D-1

    [Fact]
    public void DeletingTheAltarTakesItsCandlesWithIt()
    {
        var resources = LoadResources();
        var (world, _, champ) = CreateAltar(resources);
        champ.Start();
        champ.AddWhiteCandle();
        champ.AddRedCandle();
        var candles = champ.RedCandles.Concat(champ.WhiteCandles)
            .Select(world.FindItem).Where(i => i != null).ToList();
        Assert.NotEmpty(candles);

        champ.OnAltarDeleted();

        Assert.All(candles, c => Assert.True(c!.IsDeleted));
        Assert.Empty(champ.RedCandles);
        Assert.Empty(champ.WhiteCandles);
    }

    // ================================================================ 12D-2

    [Fact]
    public void ALiveBudgetChangeSurvivesTheNextLoad()
    {
        var resources = LoadResources();
        var (_, altar, champ) = CreateAltar(resources);
        champ.Start();

        Assert.True(champ.TrySetProperty("LEVELMAX", "7"));
        Assert.True(champ.TrySetProperty("SPAWNSMAX", "300"));
        Assert.True(champ.TrySetProperty("DEATHCOUNT", "42"));

        // Re-initialising is what a load does: the definition's values are read back in
        // and then the stored state is laid over them.
        altar.InitializeSpawnComponent(Item.ResolveWorld!()!, resources);
        var reloaded = altar.Champion!;

        Assert.Equal(7, reloaded.LevelMax);
        Assert.Equal(300, reloaded.SpawnsMax);
        Assert.Equal(42, reloaded.DeathCount);
    }

    [Fact]
    public void AStateLineWrittenBeforeTheBudgetFieldsStillLoads()
    {
        var resources = LoadResources();
        var (_, altar, champ) = CreateAltar(resources);
        champ.Start();
        // The nine-field shape an earlier save wrote.
        altar.SetTag("CHAMPION_STATE", "1|2|7|9|3|14|4|0|0");

        altar.InitializeSpawnComponent(Item.ResolveWorld!()!, resources);
        var reloaded = altar.Champion!;

        Assert.Equal(2, reloaded.Level);
        Assert.Equal(9, reloaded.DeathCount);
        Assert.Equal(5, reloaded.LevelMax);   // back to the definition's value
        Assert.Equal(100, reloaded.SpawnsMax);
    }
}
