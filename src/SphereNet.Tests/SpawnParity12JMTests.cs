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
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Spawn groups, membership rules and the configuration a spawner carries across a
/// restart.
///
/// Source-X declares a group member as <c>ID=&lt;resource&gt;,&lt;weight&gt;</c> and
/// lets a following WEIGHT key restate the last one (CRandGroupDef.cpp:79/95). A
/// resource index is 20 bits (CResourceID.h:112). PILE only applies to a stackable type
/// (CCSpawn.cpp:329), and a failed scatter falls back to the spawner's own square
/// (:353). AddObj takes only the right kind of object, only while there is room, and
/// releases it from whatever spawner held it before (:585/:621); DelObj stands aside
/// during a teardown (:512). AMOUNT is the capacity (:938) while MORE2 on a char
/// spawner is the current member count and refuses to be written (:1042). TIMELO,
/// TIMEHI, MAXDIST and PILE are written to the save in their own right (:1119).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnParity12JMTests
{
    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_jm
        TYPE=t_spawn_char

        [ITEMDEF 01f14]
        DEFNAME=i_spawn_item_jm
        TYPE=t_spawn_item

        [ITEMDEF 01000]
        DEFNAME=i_single_jm
        NAME=Single Thing

        [ITEMDEF 01002]
        DEFNAME=i_stack_jm
        NAME=Stackable Thing
        TYPE=t_normal

        [CHARDEF c_common_jm]
        DEFNAME=c_common_jm
        ID=0x27
        NAME=common

        [CHARDEF c_rare_jm]
        DEFNAME=c_rare_jm
        ID=0x0d0
        NAME=rare

        [CHARDEF 012345]
        DEFNAME=c_high_index_jm
        ID=0x9B
        NAME=high index

        [SPAWN spawn_weighted_jm]
        ID=c_common_jm,9
        ID=c_rare_jm,1

        [SPAWN spawn_weightkey_jm]
        ID=c_common_jm
        WEIGHT=9
        ID=c_rare_jm

        [SPAWN spawn_flat_jm]
        ID=c_common_jm
        ID=c_rare_jm

        [TEMPLATE t_prize_jm]
        DEFNAME=t_prize_jm
        ITEM=i_single_jm

        [EOF]
        """;

    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_jm_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        return resources;
    }

    private static GameWorld NewWorld(int size = 256)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, size, size);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Spawner(GameWorld world, ResourceHolder res, ItemType type,
        string target, int maxCount = 1, short x = 100, short y = 100)
    {
        var stone = world.CreateItem();
        stone.BaseId = type == ItemType.SpawnItem ? (ushort)0x1F14 : (ushort)0x1F13;
        stone.ItemType = type;
        world.PlaceItem(stone, new Point3D(x, y, 0, 0));
        stone.SetTag("MORE1_DEFNAME", target);
        stone.Amount = (ushort)maxCount;
        stone.InitializeSpawnComponent(world, res);
        return stone;
    }

    // ================================================================ 12J-1

    [Fact]
    public void AGroupMemberKeepsTheWeightWrittenAfterIt()
    {
        var res = LoadResources();
        var group = (SpawnGroupDef)res.GetResource(res.ResolveDefName("spawn_weighted_jm"))!;

        Assert.Equal(2, group.Members.Count);
        Assert.Equal(9, group.Members[0].Weight);
        Assert.Equal(1, group.Members[1].Weight);
        Assert.Equal(10, group.TotalWeight);
    }

    [Fact]
    public void AFollowingWeightKeyRestatesTheMemberAboveIt()
    {
        var res = LoadResources();
        var group = (SpawnGroupDef)res.GetResource(res.ResolveDefName("spawn_weightkey_jm"))!;

        Assert.Equal(2, group.Members.Count);
        Assert.Equal(9, group.Members[0].Weight);
        Assert.Equal(1, group.Members[1].Weight);
        Assert.Equal(10, group.TotalWeight);
    }

    [Fact]
    public void AGroupWithNoWeightsIsStillEven()
    {
        var res = LoadResources();
        var group = (SpawnGroupDef)res.GetResource(res.ResolveDefName("spawn_flat_jm"))!;

        Assert.Equal(2, group.TotalWeight);
    }

    [Fact]
    public void TheHeavierMemberWinsTheLowRoll()
    {
        var res = LoadResources();
        var group = (SpawnGroupDef)res.GetResource(res.ResolveDefName("spawn_weighted_jm"))!;

        // With 9:1, a roll at the bottom of the range has to land on the common one.
        Assert.Equal("c_common_jm", group.SelectRandomMember(new LowestRoll()));
    }

    private sealed class LowestRoll : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    // ================================================================ 12J-4

    [Fact]
    public void ANumericSpawnIdKeepsItsFullResourceIndex()
    {
        var res = LoadResources();
        var world = NewWorld();
        var byName = Spawner(world, res, ItemType.SpawnChar, "c_high_index_jm");
        int expected = res.ResolveDefName("c_high_index_jm").Index;
        Assert.Equal(0x12345, expected);
        Assert.Equal(expected, byName.SpawnChar!.CharDefId);

        var byNumber = Spawner(world, res, ItemType.SpawnChar, "c_common_jm", x: 120, y: 120);
        byNumber.SpawnChar!.SetFromDefName("012345", res);

        Assert.Equal(expected, byNumber.SpawnChar.CharDefId);
    }

    // ================================================================ 12J-3

    [Fact]
    public void PileLeavesASingleObjectAlone()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_single_jm");
        stone.SpawnItem!.Pile = 5;

        stone.SpawnItem.RespawnNow();

        var spawned = world.FindItem(stone.SpawnItem.SpawnedUids[0])!;
        Assert.False(spawned.IsStackable);
        Assert.Equal(1, spawned.Amount);
    }

    // ================================================================ 12L-5

    [Fact]
    public void ASpawnerAtTheMapEdgeStillProduces()
    {
        var res = LoadResources();
        var world = NewWorld();
        // At x = 0 a scatter to the WEST lands off the map. The roll is pinned to the
        // bottom of its range so the miss is certain rather than one chance in three;
        // the spawner's own square is then the fallback.
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_single_jm", x: 0, y: 100);
        stone.SpawnItem!.SpawnRange = 1;
        typeof(ItemSpawnComponent)
            .GetField("_rand", System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.NonPublic)!
            .SetValue(stone.SpawnItem, new LowestRoll());

        stone.SpawnItem.RespawnNow();

        Assert.Equal(1, stone.SpawnItem.CurrentCount);
        var spawned = world.FindItem(stone.SpawnItem.SpawnedUids[0])!;
        Assert.Equal(0, spawned.X);   // landed on the spawner itself
    }

    // ================================================================ 12L-1

    [Fact]
    public void AStoppedItemSpawnerProducesNothingOnAnOrdinaryTick()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_single_jm");
        stone.SpawnItem!.RespawnNow();
        stone.SpawnItem.Stop();
        Assert.Equal(0, stone.SpawnItem.CurrentCount);

        stone.SpawnItem.ForceSpawn();
        stone.OnTick();

        Assert.Equal(0, stone.SpawnItem.CurrentCount);
    }

    // ================================================================ 12M-1

    [Fact]
    public void APlayerCannotBeEnrolledAsASpawnChild()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_common_jm");
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));

        Assert.True(stone.TrySetProperty("ADDOBJ", $"0{player.Uid.Value:X}"));

        Assert.Equal(0, stone.SpawnChar!.CurrentCount);
        // and the spawner's own teardown leaves them alone.
        stone.SpawnChar.KillAll();
        Assert.False(player.IsDeleted);
    }

    [Fact]
    public void AFullSpawnerTakesNoMoreMembers()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_common_jm");
        stone.SpawnChar!.RespawnNow();
        Assert.Equal(1, stone.SpawnChar.CurrentCount);

        var extra = world.CreateCharacter();
        world.PlaceCharacter(extra, new Point3D(101, 100, 0, 0));
        Assert.True(stone.TrySetProperty("ADDOBJ", $"0{extra.Uid.Value:X}"));

        Assert.Equal(1, stone.SpawnChar.CurrentCount);
    }

    // ================================================================ 12L-2

    [Fact]
    public void HandingACreatureToASecondSpawnerReleasesItFromTheFirst()
    {
        var res = LoadResources();
        var world = NewWorld();
        SpawnComponent.ReleaseFromPreviousSpawner = (obj, newSpawner) =>
        {
            if (!obj.TryGetTag("SPAWN_POINT_UUID", out string? raw) ||
                !Guid.TryParse(raw, out Guid prevUuid) ||
                world.FindByUuid(prevUuid) is not Item prev ||
                prev.Uuid == newSpawner.Uuid)
                return;
            prev.SpawnChar?.DelObj(obj.Uid);
            prev.SpawnItem?.DelObj(obj.Uid);
        };
        try
        {
            var a = Spawner(world, res, ItemType.SpawnChar, "c_common_jm");
            var b = Spawner(world, res, ItemType.SpawnChar, "c_common_jm", x: 150, y: 150);
            a.SpawnChar!.RespawnNow();
            var member = world.FindChar(a.SpawnChar.SpawnedUids[0])!;

            Assert.True(b.TrySetProperty("ADDOBJ", $"0{member.Uid.Value:X}"));

            Assert.Equal(0, a.SpawnChar.CurrentCount);
            Assert.Equal(1, b.SpawnChar!.CurrentCount);

            // The old owner's teardown no longer reaches it.
            a.SpawnChar.KillAll();
            Assert.False(member.IsDeleted);
        }
        finally { SpawnComponent.ReleaseFromPreviousSpawner = null; }
    }

    // ================================================================ 12L-4

    [Fact]
    public void ATeardownSurvivesAScriptThatRemovesMembersWhileItRuns()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_common_jm", maxCount: 3);
        stone.SpawnChar!.RespawnNow();
        Assert.True(stone.SpawnChar.CurrentCount > 0);

        SpawnComponent.OnSpawnTrigger = (item, trigger, args) =>
        {
            if (trigger == ItemTrigger.DelObj && args.SpawnedChar != null)
                item.SpawnChar?.DelObj(args.SpawnedChar.Uid);   // re-entrant
            return TriggerResult.Default;
        };
        try
        {
            var ex = Record.Exception(() => stone.SpawnChar.KillAll());
            Assert.Null(ex);
        }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Equal(0, stone.SpawnChar.CurrentCount);
        // The guard was released, so ordinary notification works again.
        int fired = 0;
        SpawnComponent.OnSpawnTrigger = (_, trigger, _) =>
        {
            if (trigger == ItemTrigger.DelObj) fired++;
            return TriggerResult.Default;
        };
        try
        {
            stone.SpawnChar.RespawnNow();
            stone.SpawnChar.DelObj(stone.SpawnChar.SpawnedUids[0]);
        }
        finally { SpawnComponent.OnSpawnTrigger = null; }
        Assert.Equal(1, fired);
    }

    // ================================================================ 12L-3

    [Fact]
    public void ATemplateTargetProducesTheItemItNames()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_single_jm");
        stone.SpawnItem!.SetFromDefName("t_prize_jm", res);
        Assert.True(stone.SpawnItem.IsTemplateTarget);

        stone.SpawnItem.RespawnNow();

        Assert.Equal(1, stone.SpawnItem.CurrentCount);
        var spawned = world.FindItem(stone.SpawnItem.SpawnedUids[0])!;
        Assert.Equal(res.ResolveDefName("i_single_jm").Index, spawned.BaseId);
    }

    // ================================================================ 12K-2

    [Fact]
    public void RaisingASpawnersAmountRaisesItsCapacity()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_common_jm");

        Assert.True(stone.TrySetProperty("AMOUNT", "3"));

        Assert.Equal(3, stone.SpawnChar!.MaxCount);
        stone.SpawnChar.RespawnNow();
        Assert.Equal(3, stone.SpawnChar.CurrentCount);
    }

    // ================================================================ 12K-3

    [Fact]
    public void ResetOnASpawnerActuallyClearsIt()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_common_jm");
        stone.SpawnChar!.RespawnNow();
        var member = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;

        Assert.True(stone.TryExecuteCommand("RESET", "", null!));

        Assert.True(member.IsDeleted);
    }

    [Fact]
    public void ResetOnACustomHouseStillClearsItsDesign()
    {
        var world = NewWorld();
        var house = world.CreateItem();
        house.ItemType = ItemType.MultiCustom;
        world.PlaceItem(house, new Point3D(100, 100, 0, 0));
        house.SetTag("DESIGN_1", "something");

        Assert.True(house.TryExecuteCommand("RESET", "", null!));

        Assert.Null(house.Tags.Get("DESIGN_1"));
    }

    // ================================================================ 12M-3

    [Fact]
    public void AnOldMemberCountDoesNotWidenACharSpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_common_jm");
        stone.Amount = 1;
        stone.More2 = 5;   // the old "currently spawned" counter

        stone.InitializeSpawnComponent(world, res);

        Assert.Equal(1, stone.SpawnChar!.MaxCount);
        stone.SpawnChar.RespawnNow();
        Assert.Equal(1, stone.SpawnChar.CurrentCount);
    }

    // ================================================================ 12M-4

    [Theory]
    [InlineData(ItemType.SpawnChar, "c_common_jm")]
    [InlineData(ItemType.SpawnItem, "i_single_jm")]
    public void TheSeparateTimingFieldsAreAppliedOnLoad(ItemType type, string target)
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = world.CreateItem();
        stone.BaseId = type == ItemType.SpawnItem ? (ushort)0x1F14 : (ushort)0x1F13;
        stone.ItemType = type;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", target);
        // The shape a save writes alongside (or instead of) MOREP.
        Assert.True(stone.TrySetProperty("TIMELO", "9"));
        Assert.True(stone.TrySetProperty("TIMEHI", "9"));
        Assert.True(stone.TrySetProperty("MAXDIST", "7"));

        stone.InitializeSpawnComponent(world, res);

        int range = type == ItemType.SpawnItem
            ? stone.SpawnItem!.SpawnRange
            : stone.SpawnChar!.SpawnRange;
        Assert.Equal(7, range);
    }

    // ================================================================ 12J-5 / 12K-1

    [Fact]
    public void APileSizeAndAStoppedStateSurviveASaveAndLoad()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_single_jm");
        stone.SpawnItem!.Pile = 7;
        stone.SpawnItem.Stop();

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_jm_s_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, dir);

            var reloaded = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, dir);
            var back = reloaded.FindItem(stone.Uid)!;
            back.InitializeSpawnComponent(reloaded, res);

            Assert.Equal(7, back.SpawnItem!.Pile);
            Assert.True(back.SpawnItem.IsStopped);
            back.SpawnItem.ForceSpawn();
            back.OnTick();
            Assert.Equal(0, back.SpawnItem.CurrentCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================ 12M-2

    /// <summary>The membership relink never touches the capacity. The startup pass that
    /// USED to count ADDOBJ lines into MaxCount lives in Program.WorldBootstrap and is
    /// out of this project's reach; this pins the component contract it relied on.</summary>
    [Fact]
    public void RelinkingMembershipNeverWidensASpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_common_jm");
        stone.Amount = 1;
        // Three ADDOBJ entries naming nothing that exists, two of them the same.
        stone.SetTag("ADDOBJ", "01abc,01abc,01abd");

        stone.InitializeSpawnComponent(world, res);

        Assert.Equal(1, stone.SpawnChar!.MaxCount);
        Assert.Equal(0, stone.SpawnChar.CurrentCount);
        stone.SpawnChar.RespawnNow();
        Assert.Equal(1, stone.SpawnChar.CurrentCount);
    }
}
