using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Spawners and champion events: what a script can retarget, what a save carries back,
/// and what a spawner is allowed to destroy.
///
/// Source-X keeps ONE spawn component for creatures and items alike. DELOBJ checks
/// membership and only unlinks (CCSpawn.cpp:509); ADDOBJ and SPAWNID change the live
/// component (:933/:943/:585); the member list, MOREP and the verb table make no
/// distinction between the two kinds (:1064/:1094/:1233); neither generator runs while
/// the spawner is inside a container (:299/:383); an item placed by @Spawn keeps that
/// place and its name (:323/:353); and @AddObj is handed the timer in seconds and
/// re-arms the spawner with whatever it leaves there (:648).
///
/// A champion falls back to its definition's wave when an override is cleared (:277),
/// reloads a definition as a whole new configuration (:146/:1218), links a candle uid
/// instead of inventing one (:360), maps CHAMPIONSUMMONED onto the boss uid (:1046),
/// assigns LEVEL without re-running the transition (:1010), fires @Stop only for a
/// requested stop (:185/:216), and skips the candle veto for a candle that is already
/// gone (:657).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnChampionParity12EITests
{
    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_12
        TYPE=t_spawn_char

        [ITEMDEF 01f14]
        DEFNAME=i_spawn_item_12
        TYPE=t_spawn_item

        [ITEMDEF 01000]
        DEFNAME=i_prize_a
        NAME=Definition A

        [ITEMDEF 01001]
        DEFNAME=i_prize_b
        NAME=Definition B

        [CHARDEF c_wave_a]
        DEFNAME=c_wave_a
        ID=0x27
        NAME=wave a

        [CHARDEF c_wave_b]
        DEFNAME=c_wave_b
        ID=0x0d0
        NAME=wave b

        [CHARDEF c_boss_12]
        DEFNAME=c_boss_12
        ID=0x9B
        NAME=boss twelve

        [SPAWN spawn_group_12]
        ID=c_wave_a,1

        [CHAMPION champ_full]
        DEFNAME=champ_full
        NAME=Full
        LEVELMAX=5
        SPAWNSMAX=100
        NPCGROUP[1]=c_wave_a
        NPCGROUP[2]=c_wave_a
        NPCGROUP[3]=c_wave_a
        NPCGROUP[4]=c_wave_a
        CHAMPIONID=c_boss_12

        [CHAMPION champ_bossless]
        DEFNAME=champ_bossless
        NAME=Bossless
        LEVELMAX=5
        SPAWNSMAX=100
        NPCGROUP[1]=c_wave_a

        [EOF]
        """;

    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_sp12_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        return resources;
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Spawner(GameWorld world, ResourceHolder res, ItemType type,
        string target, int maxCount = 1)
    {
        var stone = world.CreateItem();
        stone.BaseId = type == ItemType.SpawnItem ? (ushort)0x1F14 : (ushort)0x1F13;
        stone.ItemType = type;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", target);
        stone.Amount = (ushort)maxCount;
        stone.InitializeSpawnComponent(world, res);
        return stone;
    }

    // ================================================================ 12G-1

    [Fact]
    public void ReleasingASpawnedCreatureDoesNotDestroyIt()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_a");
        stone.SpawnChar!.RespawnNow();
        var member = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;

        stone.SpawnChar.DelObj(member.Uid);

        Assert.False(member.IsDeleted);
        Assert.NotNull(world.FindChar(member.Uid));
        Assert.Equal(0, stone.SpawnChar.CurrentCount);
    }

    [Fact]
    public void ReleasingSomethingThatIsNotAMemberTouchesNothing()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_a");
        var bystander = world.CreateCharacter();
        world.PlaceCharacter(bystander, new Point3D(120, 120, 0, 0));

        stone.SpawnChar!.DelObj(bystander.Uid);

        Assert.False(bystander.IsDeleted);
        Assert.NotNull(world.FindChar(bystander.Uid));
    }

    [Fact]
    public void ReleasingAPlayerCharacterIsRefused()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_a");
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(120, 120, 0, 0));

        stone.SpawnChar!.DelObj(player.Uid);

        Assert.False(player.IsDeleted);
    }

    // ================================================================ 12G-2

    [Fact]
    public void RetargetingASpawnerChangesWhatItProduces()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_a");

        Assert.True(stone.TrySetProperty("SPAWNID", "c_wave_b"));
        stone.SpawnChar!.RespawnNow();

        var member = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;
        Assert.Equal(res.ResolveDefName("c_wave_b").Index, member.CharDefIndex);
    }

    [Fact]
    public void RetargetingAnItemSpawnerChangesWhatItProduces()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");

        Assert.True(stone.TrySetProperty("SPAWNID", "i_prize_b"));
        stone.SpawnItem!.RespawnNow();

        Assert.Equal(res.ResolveDefName("i_prize_b").Index, stone.SpawnItem.ItemDefId);
    }

    // ================================================================ 12I-4

    [Fact]
    public void NamingASingleCreatureDropsTheGroupThatWasSetBefore()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "spawn_group_12");

        stone.SpawnChar!.SetFromDefName("c_wave_b", res);
        stone.SpawnChar.RespawnNow();

        var member = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;
        Assert.Equal(res.ResolveDefName("c_wave_b").Index, member.CharDefIndex);
    }

    // ================================================================ 12G-3

    [Fact]
    public void HandingASpawnerAnExistingCreatureCountsItAtOnce()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_a");
        var loose = world.CreateCharacter();
        world.PlaceCharacter(loose, new Point3D(101, 100, 0, 0));

        Assert.True(stone.TrySetProperty("ADDOBJ", $"0{loose.Uid.Value:X}"));

        Assert.Equal(1, stone.SpawnChar!.CurrentCount);
        // and the quota is full, so a respawn adds nothing on top of it.
        stone.SpawnChar.RespawnNow();
        Assert.Equal(1, stone.SpawnChar.CurrentCount);
    }

    // ================================================================ 12I-2

    [Fact]
    public void ASpawnerInsideABagProducesNothing()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_wave_a");
        var bag = world.CreateItem();
        world.PlaceItem(bag, new Point3D(100, 100, 0, 0));
        bag.AddItem(stone);

        stone.SpawnChar!.ForceSpawn();
        stone.OnTick();

        Assert.Equal(0, stone.SpawnChar.CurrentCount);
    }

    [Fact]
    public void AnItemSpawnerInsideABagProducesNothingEither()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");
        var bag = world.CreateItem();
        world.PlaceItem(bag, new Point3D(100, 100, 0, 0));
        bag.AddItem(stone);

        stone.SpawnItem!.ForceSpawn();
        stone.OnTick();

        Assert.Equal(0, stone.SpawnItem.CurrentCount);
    }

    // ================================================================ 12G-4 / 12G-5

    [Fact]
    public void AnItemPlacedBySpawnStaysWhereTheScriptPutIt()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");
        SpawnComponent.OnSpawnTrigger = (_, trigger, args) =>
        {
            if (trigger == ItemTrigger.Spawn && args.SpawnedItem != null)
                world.PlaceItem(args.SpawnedItem, new Point3D(150, 151, 7, 0));
            return TriggerResult.Default;
        };
        try { stone.SpawnItem!.RespawnNow(); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        var spawned = world.FindItem(stone.SpawnItem!.SpawnedUids[0])!;
        Assert.Equal(150, spawned.X);
        Assert.Equal(151, spawned.Y);
        Assert.Equal(7, spawned.Z);
    }

    [Fact]
    public void AnItemNobodyPlacedStillLandsByTheSpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");
        stone.MoreP = new Point3D(0, 0, 0, 0);
        stone.SpawnItem!.SpawnRange = 0;

        stone.SpawnItem.RespawnNow();

        var spawned = world.FindItem(stone.SpawnItem.SpawnedUids[0])!;
        Assert.Equal(100, spawned.X);
        Assert.Equal(100, spawned.Y);
    }

    [Fact]
    public void ANameChosenByCreateSurvives()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");
        Item.CreateTriggerHook = i => i.Name = "Created Name";
        try { stone.SpawnItem!.RespawnNow(); }
        finally { Item.CreateTriggerHook = null; }

        var spawned = world.FindItem(stone.SpawnItem!.SpawnedUids[0])!;
        Assert.Equal("Created Name", spawned.Name);
    }

    [Fact]
    public void AnItemWithNoNameOfItsOwnGetsTheDefinitionsName()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");

        stone.SpawnItem!.RespawnNow();

        var spawned = world.FindItem(stone.SpawnItem.SpawnedUids[0])!;
        Assert.Equal("Definition A", spawned.Name);
    }

    // ================================================================ 12H-2

    [Fact]
    public void AnItemSpawnerTakesItsIntervalAndRangeFromMoreP()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = world.CreateItem();
        stone.BaseId = 0x1F14;
        stone.ItemType = ItemType.SpawnItem;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "i_prize_a");
        stone.MoreP = new Point3D(9, 9, 7, 0);

        stone.InitializeSpawnComponent(world, res);

        Assert.Equal(7, stone.SpawnItem!.SpawnRange);
    }

    // ================================================================ 12I-3

    [Fact]
    public void ACharSpawnersRangeSurvivesBeingInitialisedTwice()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_wave_a");
        stone.MoreP = new Point3D(9, 9, 7, 0);

        stone.InitializeSpawnComponent(world, res);
        Assert.Equal(7, stone.SpawnChar!.SpawnRange);

        stone.InitializeSpawnComponent(world, res);
        Assert.Equal(7, stone.SpawnChar.SpawnRange);
    }

    // ================================================================ 12H-3 / 12H-4

    [Fact]
    public void BringingAnItemSpawnersTimerForwardMakesItSpawn()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");
        stone.SpawnItem!.ResetTimer(Environment.TickCount64 + 3_600_000);

        Assert.True(stone.TrySetProperty("TIMER", "0"));
        stone.OnTick();

        Assert.Equal(1, stone.SpawnItem.CurrentCount);
    }

    [Fact]
    public void AnItemSpawnerCanBeStopped()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");
        stone.SpawnItem!.RespawnNow();
        Assert.Equal(1, stone.SpawnItem.CurrentCount);

        Assert.True(stone.TryExecuteCommand("STOP", "", null!));

        Assert.True(stone.SpawnItem.IsStopped);
        Assert.Equal(0, stone.SpawnItem.CurrentCount);
        stone.SpawnItem.RespawnNow();
        Assert.Equal(0, stone.SpawnItem.CurrentCount);

        // START opens it again.
        Assert.True(stone.TryExecuteCommand("START", "", null!));
        stone.SpawnItem.RespawnNow();
        Assert.Equal(1, stone.SpawnItem.CurrentCount);
    }

    // ================================================================ 12I-1

    [Fact]
    public void DeletingAnItemSpawnerTakesItsItemsWithIt()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");
        stone.SpawnItem!.RespawnNow();
        var child = world.FindItem(stone.SpawnItem.SpawnedUids[0])!;

        world.DeleteObject(stone);
        stone.Delete();

        Assert.True(child.IsDeleted);
    }

    // ================================================================ 12I-5

    [Fact]
    public void AddObjIsHandedTheTimerAndCanChangeIt()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a", maxCount: 2);
        stone.SpawnItem!.SetDelay(9, 9);
        int seen = -999;
        SpawnComponent.OnSpawnTrigger = (_, trigger, args) =>
        {
            if (trigger == ItemTrigger.AddObj)
            {
                seen = args.N1;
                args.N1 = 111;
            }
            return TriggerResult.Default;
        };
        try { stone.SpawnItem.RespawnNow(); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.True(seen >= 0, $"the trigger should be handed the remaining seconds, saw {seen}");
        long remainingMs = stone.Timeout - Environment.TickCount64;
        Assert.InRange(remainingMs, 100_000, 112_000); // ~111 s, not the 9-minute default
    }

    // ================================================================ 12H-1

    [Fact]
    public void AnItemSpawnersMembersComeBackAfterASaveAndLoad()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_a");
        stone.SpawnItem!.RespawnNow();
        Assert.Equal(1, stone.SpawnItem.CurrentCount);

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_sp12s_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, dir);

            var reloaded = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, dir);
            var back = reloaded.FindItem(stone.Uid)!;
            back.InitializeSpawnComponent(reloaded, res);

            // The member is counted again, so the spawner does not top itself up.
            Assert.Equal(1, back.SpawnItem!.CurrentCount);
            back.SpawnItem.RespawnNow();
            Assert.Equal(1, back.SpawnItem.CurrentCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================ 12E-1

    [Fact]
    public void ClearingAWaveOverrideGoesBackToTheDefinition()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");

        Assert.True(altar.Champion!.TrySetProperty("NPCGROUP1", "c_wave_b"));
        Assert.True(altar.Champion.TrySetProperty("NPCGROUP1", ""));

        Assert.True(altar.Champion.TryGetProperty("NPCGROUP1", out string back));
        Assert.Contains("c_wave_a", back, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ 12E-3

    [Fact]
    public void SwitchingToADefinitionWithNoBossLeavesNoBoss()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        Assert.NotEqual(0, altar.Champion!.ChampionId);

        Assert.True(altar.Champion.InitFromDef(res, "champ_bossless"));

        Assert.Equal(0, altar.Champion.ChampionId);
    }

    // ================================================================ 12E-4

    [Fact]
    public void AskingToLinkAMissingCandleDoesNotInventOne()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        altar.Champion!.Start();
        int before = world.GetAllObjects().Count();

        altar.Champion.AddWhiteCandle(new Serial(0x4000ABCD));

        Assert.Empty(altar.Champion.WhiteCandles);
        Assert.Equal(before, world.GetAllObjects().Count());
    }

    // ================================================================ 12E-5

    [Fact]
    public void AScriptCanNameTheEventsBoss()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        var boss = world.CreateCharacter();
        world.PlaceCharacter(boss, new Point3D(102, 100, 0, 0));

        Assert.True(altar.TrySetProperty("CHAMPIONSUMMONED", $"0{boss.Uid.Value:X}"));

        Assert.Equal(boss.Uid, altar.Champion!.ChampionSummoned);
    }

    // ================================================================ 12E-2

    [Fact]
    public void ABossThatIsDeletedNoLongerBlocksTheEvent()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        altar.Champion!.Start();
        altar.Champion.SetLevel(altar.Champion.LevelMax);
        var boss = world.FindChar(altar.Champion.ChampionSummoned)!;

        world.DeleteObject(boss);
        boss.Delete();
        altar.Champion.SpawnNpc();

        // The uid can legitimately be REUSED for the replacement, so the object is what
        // is checked: the event has a live boss again rather than a dead reference.
        Assert.True(altar.Champion.ChampionSummoned.IsValid);
        var replacement = world.FindChar(altar.Champion.ChampionSummoned);
        Assert.NotNull(replacement);
        Assert.False(replacement!.IsDeleted);
        Assert.NotSame(boss, replacement);
    }

    // ================================================================ 12F-1

    [Fact]
    public void AStopScriptCannotHoldTheEventOpenAfterTheBossDies()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        altar.Champion!.Start();
        SpawnComponent.OnSpawnTrigger = (_, trigger, _) =>
            trigger == ItemTrigger.Stop ? TriggerResult.True : TriggerResult.Default;
        try { altar.Champion.Complete(); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.False(altar.Champion.Active);
    }

    [Fact]
    public void AStopScriptStillVetoesAStaffStop()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        altar.Champion!.Start();
        var staff = world.CreateCharacter();
        world.PlaceCharacter(staff, new Point3D(101, 100, 0, 0));
        SpawnComponent.OnSpawnTrigger = (_, trigger, _) =>
            trigger == ItemTrigger.Stop ? TriggerResult.True : TriggerResult.Default;
        try { altar.Champion.Stop(staff); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.True(altar.Champion.Active);
    }

    // ================================================================ 12F-2

    [Fact]
    public void AssigningTheLevelDoesNotReRunTheTransition()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        altar.Champion!.Start();
        int thresholdBefore = altar.Champion.CandlesNextLevel;

        Assert.True(altar.TrySetProperty("LEVEL", "1"));

        Assert.Equal(1, altar.Champion.Level);
        Assert.Equal(thresholdBefore, altar.Champion.CandlesNextLevel);
        Assert.False(altar.Champion.ChampionSummoned.IsValid);
    }

    [Fact]
    public void AssigningTheFinalLevelDoesNotSummonTheBoss()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        altar.Champion!.Start();

        Assert.True(altar.TrySetProperty("LEVEL", altar.Champion.LevelMax.ToString()));

        Assert.Equal(altar.Champion.LevelMax, altar.Champion.Level);
        Assert.False(altar.Champion.ChampionSummoned.IsValid);
    }

    // ================================================================ 12F-4

    [Fact]
    public void ACandleThatIsAlreadyGoneLeavesTheListWhateverTheScriptSays()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        altar.Champion!.Start();
        altar.Champion.AddWhiteCandle();
        var candle = world.FindItem(altar.Champion.WhiteCandles[0])!;
        world.DeleteObject(candle);
        candle.Delete();

        SpawnComponent.OnSpawnTrigger = (_, trigger, _) =>
            trigger == ItemTrigger.DelWhiteCandle ? TriggerResult.True : TriggerResult.Default;
        try { altar.Champion.DelWhiteCandle(0); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Empty(altar.Champion.WhiteCandles);
    }

    [Fact]
    public void AVetoStillKeepsACandleThatIsReallyThere()
    {
        var res = LoadResources();
        var world = NewWorld();
        var altar = ChampionAltar(world, res, "champ_full");
        altar.Champion!.Start();
        altar.Champion.AddWhiteCandle();

        SpawnComponent.OnSpawnTrigger = (_, trigger, _) =>
            trigger == ItemTrigger.DelWhiteCandle ? TriggerResult.True : TriggerResult.Default;
        try { altar.Champion.DelWhiteCandle(0); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Single(altar.Champion.WhiteCandles);
    }

    // ================================================================ 12F-5

    [Fact]
    public void TheActivationStampIsGameTime()
    {
        var res = LoadResources();
        var world = NewWorld();
        world.SetWorldClockMinutes(100);
        ChampionComponent.ResolveGameClockMs = () => world.GameClockMs;
        var altar = ChampionAltar(world, res, "champ_full");

        altar.Champion!.Start();

        Assert.Equal(world.GameClockMs, altar.Champion.LastActivationTime);
        // and that is nowhere near a UTC second count.
        Assert.True(altar.Champion.LastActivationTime < 1_000_000_000L);
    }

    // ================================================================ 12F-3

    [Fact]
    public void ClassicChampionStateFieldsSurviveALoad()
    {
        var res = LoadResources();
        var world = NewWorld();
        // What the loader does: the plain fields arrive before the component exists.
        var altar = world.CreateItem();
        altar.BaseId = 0x1F13;
        altar.ItemType = ItemType.SpawnChampion;
        world.PlaceItem(altar, new Point3D(100, 100, 0, 0));
        altar.SetTag("MORE1_DEFNAME", "champ_full");
        Assert.True(altar.TrySetProperty("ACTIVE", "1"));
        Assert.True(altar.TrySetProperty("LEVEL", "2"));
        Assert.True(altar.TrySetProperty("SPAWNSCUR", "10"));
        Assert.True(altar.TrySetProperty("DEATHCOUNT", "7"));
        Assert.True(altar.TrySetProperty("LASTACTIVATIONTIME", "12345"));

        altar.InitializeSpawnComponent(world, res);

        Assert.True(altar.Champion!.Active);
        Assert.Equal(2, altar.Champion.Level);
        Assert.Equal(10, altar.Champion.SpawnsCur);
        Assert.Equal(7, altar.Champion.DeathCount);
        Assert.Equal(12345, altar.Champion.LastActivationTime);
    }

    private static Item ChampionAltar(GameWorld world, ResourceHolder res, string def)
    {
        var altar = world.CreateItem();
        altar.BaseId = 0x1F13;
        altar.ItemType = ItemType.SpawnChampion;
        world.PlaceItem(altar, new Point3D(100, 100, 0, 0));
        altar.SetTag("MORE1_DEFNAME", def);
        altar.InitializeSpawnComponent(world, res);
        Assert.NotNull(altar.Champion);
        return altar;
    }
}
