using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

/// <summary>
/// Champion spawn engine (Source-X CCChampion port): [CHAMPION] def loading,
/// Start/kill/candle/level progression, boss completion and tag persistence.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class ChampionSystemTests
{
    private const string ChampionScript = """
        [CHARDEF c_test_mongbat]
        DEFNAME=c_test_mongbat
        ID=0x27
        NAME=test mongbat

        [CHARDEF c_test_imp]
        DEFNAME=c_test_imp
        ID=0x4A
        NAME=test imp

        [CHARDEF c_test_boss]
        DEFNAME=c_test_boss
        ID=0x9B
        NAME=test boss

        [CHAMPION champ_test]
        DEFNAME=champ_test
        NAME=Test Champion
        LEVELMAX=5
        SPAWNSMAX=100
        NPCGROUP[1]=c_test_mongbat,c_test_imp
        NPCGROUP[2]=c_test_imp
        NPCGROUP[3]=c_test_imp
        NPCGROUP[4]=c_test_imp
        CHAMPIONID=c_test_boss

        [EOF]
        """;

    private static ResourceHolder LoadResources()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_champ_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, ChampionScript);
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    private static (GameWorld World, Item Altar, ChampionComponent Champ) CreateAltar(ResourceHolder resources)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var altar = world.CreateItem();
        altar.BaseId = 0x1F13;
        altar.ItemType = ItemType.SpawnChampion;
        world.PlaceItem(altar, new Point3D(1000, 1000, 0, 0));
        altar.SetTag("MORE1_DEFNAME", "champ_test");
        altar.InitializeSpawnComponent(world, resources);

        Assert.NotNull(altar.Champion);
        return (world, altar, altar.Champion!);
    }

    [Fact]
    public void ChampionSection_LoadsIntoDef()
    {
        var resources = LoadResources();
        var rid = resources.ResolveDefName("champ_test");
        Assert.True(rid.IsValid);
        Assert.Equal(ResType.Champion, rid.Type);
        Assert.NotNull(resources.GetResource(rid)?.StoredKeys);
    }

    [Fact]
    public void InitFromDef_ParsesChampionData()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);

        Assert.Equal("champ_test", champ.ChampionDefName);
        Assert.Equal("Test Champion", champ.ChampionName);
        Assert.Equal(5, champ.LevelMax);
        Assert.Equal(100, champ.SpawnsMax);
        Assert.NotEqual(0, champ.ChampionId);
    }

    [Fact]
    public void Start_SpawnsInitialWaveAndArmsDecayTimer()
    {
        var resources = LoadResources();
        var (world, altar, champ) = CreateAltar(resources);

        champ.Start();

        Assert.True(champ.Active);
        Assert.Equal(1, champ.Level);
        // Source-X InitializeLists defaults for 5 levels: level 1 = 58% of
        // SpawnsMax(100) = 58 monsters; CandlesNextLevel = 4 →
        // SpawnsNextRed = 58/4 = 14, initial whites quota = 14/5 = 2.
        Assert.Equal(14, champ.SpawnsNextRed);
        Assert.Equal(4, champ.CandlesNextLevel);
        // Source-X Start loop quirk (kept verbatim): the counter races the
        // quota SpawnNPC decrements, so a quota of 2 yields ONE initial spawn.
        Assert.Equal(1, champ.SpawnsCur);
        Assert.True(altar.Timeout > 0);   // 10-minute decay armed
    }

    [Fact]
    public void Kills_AdvanceWhiteAndRedCandles()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);
        champ.Start();

        // Kill until the first white candle appears (quota exhausted).
        int guard = 0;
        while (champ.WhiteCandles.Count == 0 && guard++ < 20)
            champ.OnKill(Serial.Invalid);
        Assert.Single(champ.WhiteCandles);

        // Keep killing until the 4 whites convert into a red candle.
        guard = 0;
        while (champ.RedCandles.Count == 0 && guard++ < 100)
            champ.OnKill(Serial.Invalid);
        Assert.Single(champ.RedCandles);
        Assert.Empty(champ.WhiteCandles); // consumed by the red
    }

    [Fact]
    public void MaxLevel_SpawnsBossAndBossDeathCompletes()
    {
        var resources = LoadResources();
        var (world, _, champ) = CreateAltar(resources);
        champ.Start();

        champ.SetLevel(champ.LevelMax);
        Assert.True(champ.ChampionSummoned.IsValid);
        var boss = world.FindChar(champ.ChampionSummoned);
        Assert.NotNull(boss);

        champ.OnKill(champ.ChampionSummoned);
        Assert.False(champ.Active); // @Complete → Stop
        Assert.Empty(champ.RedCandles);
        Assert.Empty(champ.WhiteCandles);
    }

    [Fact]
    public void DecayTick_RemovesRedCandleAndRefundsProgress()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);
        champ.Start();

        int guard = 0;
        while (champ.RedCandles.Count == 0 && guard++ < 100)
            champ.OnKill(Serial.Invalid);
        Assert.Single(champ.RedCandles);
        int deathsBefore = champ.DeathCount;

        champ.OnTick(Environment.TickCount64);
        Assert.Empty(champ.RedCandles);
        Assert.True(champ.DeathCount < deathsBefore);
        Assert.True(champ.Active); // still running until the last red decays

        // No red candles left → next decay tick stops the whole spawn.
        champ.OnTick(Environment.TickCount64);
        Assert.False(champ.Active);
    }

    [Fact]
    public void State_PersistsThroughTagsAcrossReinit()
    {
        var resources = LoadResources();
        var (world, altar, champ) = CreateAltar(resources);
        champ.Start();
        for (int i = 0; i < 5; i++)
            champ.OnKill(Serial.Invalid);

        int level = champ.Level;
        int deaths = champ.DeathCount;
        int whites = champ.WhiteCandles.Count;

        // Simulate a reload: fresh component, same item tags.
        altar.Champion = new ChampionComponent(altar, world);
        Assert.True(altar.Champion.InitFromDef(resources, "champ_test"));

        Assert.True(altar.Champion.Active);
        Assert.Equal(level, altar.Champion.Level);
        Assert.Equal(deaths, altar.Champion.DeathCount);
        Assert.Equal(whites, altar.Champion.WhiteCandles.Count);
    }

    [Fact]
    public void ItemSurface_RoutesChampionVerbsAndProperties()
    {
        var resources = LoadResources();
        var (_, altar, champ) = CreateAltar(resources);

        var console = new TestConsole();
        Assert.True(altar.TryExecuteCommand("START", "", console));
        Assert.True(champ.Active);

        Assert.True(altar.TryGetProperty("LEVEL", out string level));
        Assert.Equal("1", level);
        Assert.True(altar.TryGetProperty("CHAMPIONSPAWN", out string defName));
        Assert.Equal("champ_test", defName);

        Assert.True(altar.TryExecuteCommand("STOP", "", console));
        Assert.False(champ.Active);
    }

    [Fact]
    public void CandleList_PerLevel_MatchesSourceXInsertOrder()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);

        // Source-X InitializeLists builds the candle vector by inserting each
        // computed value at the FRONT. For LEVELMAX=7 that yields [3,3,3,3,2,2]
        // (appending would reverse the tail to [3,2,2,3,3,3] and mis-assign the
        // red-candle requirement per level). CandlesNextLevel accumulates the
        // per-level count, so its per-SetLevel delta exposes the ordering.
        champ.TrySetProperty("LEVELMAX", "7");
        var perLevel = new List<int>();
        int prev = champ.CandlesNextLevel;
        for (int lv = 1; lv <= 6; lv++)
        {
            champ.SetLevel(lv);
            perLevel.Add(champ.CandlesNextLevel - prev);
            prev = champ.CandlesNextLevel;
        }

        Assert.Equal(new[] { 3, 3, 3, 3, 2, 2 }, perLevel);
    }

    [Fact]
    public void NpcGroup_ReadResolvesMembers_WriteOverridesGroup()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);

        // Group 1 from the def is [mongbat, imp].
        Assert.True(champ.TryGetProperty("NPCGROUP1", out string first));
        Assert.Contains("mongbat", first, StringComparison.OrdinalIgnoreCase);
        Assert.True(champ.TryGetProperty("NPCGROUP1.1", out string second));
        Assert.Contains("imp", second, StringComparison.OrdinalIgnoreCase);
        // Out-of-range npc index → -1.
        Assert.True(champ.TryGetProperty("NPCGROUP1.9", out string oob));
        Assert.Equal("-1", oob);

        // Override group 2 to the boss def, then read it back.
        Assert.True(champ.TrySetProperty("NPCGROUP2", "c_test_boss"));
        Assert.True(champ.TryGetProperty("NPCGROUP2", out string grp2));
        Assert.Contains("boss", grp2, StringComparison.OrdinalIgnoreCase);

        // Empty list clears the OVERRIDE, and the definition's own group takes over
        // again (CCChampion.cpp:277/1014) — clearing an override is not the same as
        // deleting the wave.
        Assert.True(champ.TrySetProperty("NPCGROUP2", ""));
        Assert.True(champ.TryGetProperty("NPCGROUP2", out string cleared));
        Assert.Contains("imp", cleared, StringComparison.OrdinalIgnoreCase);

        // A level the definition never declared still reads back as -1.
        Assert.True(champ.TryGetProperty("NPCGROUP9", out string missing));
        Assert.Equal("-1", missing);
    }

    [Fact]
    public void MultiCreate_VerbIsAcceptedAsNoOp()
    {
        var resources = LoadResources();
        var (_, _, champ) = CreateAltar(resources);
        // Source-X MULTICREATE is a stub but must still be a recognised verb.
        Assert.True(champ.TryExecuteVerb("MULTICREATE", "", null));
    }

    private sealed class TestConsole : Core.Interfaces.ITextConsole
    {
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) { }
    }
}
