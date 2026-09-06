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
/// What happens to a spawned object after it leaves the spawner, and what the spawner's
/// own events are handed.
///
/// Taming a creature ends its spawn membership (NPC_PetSetOwner, CCharNPCPet.cpp:614),
/// and so does deleting it, before the uid can be handed to anything else
/// (CObjBase.cpp:147). A spawned item is stripped of ATTR_OWNED and ATTR_MOVE_ALWAYS
/// (CCSpawn.cpp:340). A zero weight takes a group member out of the draw
/// (CRandGroupDef.cpp:229) and the first half of an ID row is the resource even when it
/// is numeric (:79). A template's CONTAINER row holds the ITEM rows that follow it
/// (CItem.cpp:628/642). Live membership fires @AddObj, sets the creature's home from the
/// new spawner and parks the timer on the last slot before the trigger runs
/// (CCSpawn.cpp:631/643/648); @DelObj is handed the SPAWN POINT and the remaining
/// seconds, and clears the link (:542/:568).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnParity12NPTests
{
    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_np
        TYPE=t_spawn_char

        [ITEMDEF 01f14]
        DEFNAME=i_spawn_item_np
        TYPE=t_spawn_item

        [ITEMDEF 01000]
        DEFNAME=i_prize_np
        NAME=Prize

        [ITEMDEF 0e75]
        DEFNAME=i_box_np
        NAME=Box
        TYPE=t_container

        [CHARDEF 0200]
        DEFNAME=c_numeric_np
        ID=0x27
        NAME=numeric member

        [CHARDEF c_other_np]
        DEFNAME=c_other_np
        ID=0x0d0
        NAME=other member

        [SPAWN spawn_zero_np]
        ID=c_other_np,0
        ID=c_numeric_np,1

        [SPAWN spawn_numeric_np]
        ID=0200,9

        [TEMPLATE t_box_np]
        DEFNAME=t_box_np
        CONTAINER=i_box_np
        ITEM=i_prize_np

        [EOF]
        """;

    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_np_{Guid.NewGuid():N}.scp");
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

    /// <summary>The world-level wiring the server installs, so the component can reach
    /// the spawner that owns an object.</summary>
    private static IDisposable WireOwnership(GameWorld world)
    {
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
        Character.ReleaseFromSpawner = ch =>
        {
            if (!ch.TryGetTag("SPAWN_POINT_UUID", out string? raw) ||
                !Guid.TryParse(raw, out Guid spawnUuid) ||
                world.FindByUuid(spawnUuid) is not Item spawner)
                return;
            spawner.SpawnChar?.DelObj(ch.Uid);
        };
        return new Unwire();
    }

    private sealed class Unwire : IDisposable
    {
        public void Dispose()
        {
            SpawnComponent.ReleaseFromPreviousSpawner = null;
            Character.ReleaseFromSpawner = null;
        }
    }

    // ================================================================ 12N-1

    [Fact]
    public void TamingACreatureEndsItsSpawnMembership()
    {
        var res = LoadResources();
        var world = NewWorld();
        using var _ = WireOwnership(world);
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");
        stone.SpawnChar!.RespawnNow();
        var beast = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;
        var tamer = world.CreateCharacter();
        tamer.IsPlayer = true;
        world.PlaceCharacter(tamer, new Point3D(101, 100, 0, 0));

        Assert.True(beast.TryAssignOwnership(tamer, tamer));

        Assert.Equal(0, stone.SpawnChar.CurrentCount);
        // and the old spawn point can no longer destroy it.
        stone.SpawnChar.KillAll();
        Assert.False(beast.IsDeleted);
    }

    [Fact]
    public void AWildCreatureIsStillClearedByItsSpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        using var _ = WireOwnership(world);
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");
        stone.SpawnChar!.RespawnNow();
        var beast = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;

        stone.SpawnChar.KillAll();

        Assert.True(beast.IsDeleted);
    }

    // ================================================================ 12N-2

    [Fact]
    public void ASpawnerEmptiedByTheSaveSweepIsScheduledAgain()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");
        stone.SpawnChar!.RespawnNow();
        Assert.Equal(1, stone.SpawnChar.CurrentCount);
        Assert.True(stone.Timeout < 0, "a full spawner parks its timer");

        var member = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;
        member.Kill();
        // What a world save does before writing the member list.
        stone.SpawnChar.CleanupDead();

        Assert.Equal(0, stone.SpawnChar.CurrentCount);
        Assert.True(stone.Timeout > 0, "losing the member has to re-open the schedule");
    }

    // ================================================================ 12N-4

    [Fact]
    public void ASpawnedItemBelongsToNobody()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_np");
        Item.CreateTriggerHook = i =>
        {
            i.SetAttr(ObjAttributes.Owned);
            i.SetAttr(ObjAttributes.Move_Always);
            i.SetAttr(ObjAttributes.Magic);
        };
        try { stone.SpawnItem!.RespawnNow(); }
        finally { Item.CreateTriggerHook = null; }

        var spawned = world.FindItem(stone.SpawnItem!.SpawnedUids[0])!;
        Assert.False(spawned.IsAttr(ObjAttributes.Owned));
        Assert.False(spawned.IsAttr(ObjAttributes.Move_Always));
        // Everything else the definition or its create script set is left alone.
        Assert.True(spawned.IsAttr(ObjAttributes.Magic));
    }

    // ================================================================ 12O-1

    [Fact]
    public void AZeroWeightMemberIsOutOfTheDraw()
    {
        var res = LoadResources();
        var group = (SpawnGroupDef)res.GetResource(res.ResolveDefName("spawn_zero_np"))!;

        Assert.Equal(0, group.Members[0].Weight);
        Assert.Equal(1, group.TotalWeight);
        // Even the lowest roll skips it.
        Assert.Equal("c_numeric_np", group.SelectRandomMember(new LowestRoll()));
    }

    private sealed class LowestRoll : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    // ================================================================ 12O-2

    [Fact]
    public void ANumericGroupMemberIsAResourceNotAWeight()
    {
        var res = LoadResources();
        var group = (SpawnGroupDef)res.GetResource(res.ResolveDefName("spawn_numeric_np"))!;

        var member = Assert.Single(group.Members);
        Assert.Equal("0200", member.CharDefName);
        Assert.Equal(9, member.Weight);

        // and it actually produces the creature.
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "spawn_numeric_np");
        stone.SpawnChar!.RespawnNow();
        Assert.Equal(1, stone.SpawnChar.CurrentCount);
    }

    // ================================================================ 12O-3

    [Fact]
    public void ATemplateContainerCarriesItsContents()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_np");
        stone.SpawnItem!.SetFromDefName("t_box_np", res);

        stone.SpawnItem.RespawnNow();

        var box = world.FindItem(stone.SpawnItem.SpawnedUids[0])!;
        Assert.Equal(res.ResolveDefName("i_box_np").Index, box.BaseId);
        var content = Assert.Single(box.Contents);
        Assert.Equal(res.ResolveDefName("i_prize_np").Index, content.BaseId);
    }

    // ================================================================ 12O-4

    [Fact]
    public void ATemplateTargetIsStillATemplateAfterALoad()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnItem, "i_prize_np");
        Assert.True(stone.TrySetProperty("SPAWNID", "t_box_np"));
        Assert.True(stone.SpawnItem!.IsTemplateTarget);

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_np_s_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            new SphereNet.Persistence.Save.WorldSaver(lf).Save(world, dir);

            var reloaded = NewWorld();
            new SphereNet.Persistence.Load.WorldLoader(lf).Load(reloaded, dir);
            var back = reloaded.FindItem(stone.Uid)!;
            back.InitializeSpawnComponent(reloaded, res);

            Assert.True(back.SpawnItem!.IsTemplateTarget);
            back.SpawnItem.RespawnNow();
            Assert.Equal(1, back.SpawnItem.CurrentCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================ 12O-5

    [Fact]
    public void EnrollingAnExistingCreatureFiresTheMembershipEvent()
    {
        var res = LoadResources();
        var world = NewWorld();
        using var _ = WireOwnership(world);
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");
        var loose = world.CreateCharacter();
        world.PlaceCharacter(loose, new Point3D(101, 100, 0, 0));

        int fired = 0;
        SpawnComponent.OnSpawnTrigger = (_, trigger, _) =>
        {
            if (trigger == ItemTrigger.AddObj) fired++;
            return TriggerResult.Default;
        };
        try { Assert.True(stone.TrySetProperty("ADDOBJ", $"0{loose.Uid.Value:X}")); }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Equal(1, fired);
        Assert.Equal(1, stone.SpawnChar!.CurrentCount);
    }

    // ================================================================ 12P-1

    [Fact]
    public void AnEnrolledCreatureTakesItsNewSpawnersHome()
    {
        var res = LoadResources();
        var world = NewWorld();
        using var _ = WireOwnership(world);
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");
        stone.SpawnChar!.SpawnRange = 7;
        var loose = world.CreateCharacter();
        world.PlaceCharacter(loose, new Point3D(150, 150, 0, 0));
        loose.Home = new Point3D(20, 30, 0, 0);
        loose.HomeDist = 42;

        Assert.True(stone.TrySetProperty("ADDOBJ", $"0{loose.Uid.Value:X}"));

        Assert.Equal(100, loose.Home.X);
        Assert.Equal(100, loose.Home.Y);
        Assert.Equal(7, loose.HomeDist);
    }

    [Fact]
    public void ARefusedEnrolmentLeavesTheCreatureAlone()
    {
        var res = LoadResources();
        var world = NewWorld();
        using var _ = WireOwnership(world);
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");
        stone.SpawnChar!.RespawnNow();   // quota of one, now full
        var loose = world.CreateCharacter();
        world.PlaceCharacter(loose, new Point3D(150, 150, 0, 0));
        loose.Home = new Point3D(20, 30, 0, 0);
        loose.HomeDist = 42;

        Assert.True(stone.TrySetProperty("ADDOBJ", $"0{loose.Uid.Value:X}"));

        Assert.Equal(20, loose.Home.X);
        Assert.Equal(42, loose.HomeDist);
    }

    // ================================================================ 12P-2

    [Fact]
    public void AReleasedCreatureNoLongerNamesItsOldSpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");
        stone.SpawnChar!.RespawnNow();
        var member = world.FindChar(stone.SpawnChar.SpawnedUids[0])!;
        Assert.True(member.TryGetProperty("SPAWNITEM", out string? before));
        Assert.NotEqual("0", before);

        stone.SpawnChar.DelObj(member.Uid);

        Assert.True(member.TryGetProperty("SPAWNITEM", out string? after));
        Assert.Equal("0", after);
        Assert.Null(member.Tags.Get("SPAWN_POINT_UUID"));
    }

    // ================================================================ 12P-3

    [Theory]
    [InlineData(ItemType.SpawnChar, "c_other_np")]
    [InlineData(ItemType.SpawnItem, "i_prize_np")]
    public void ReleasingAMemberTellsTheScriptWhichSpawnerAndLetsItSetTheInterval(
        ItemType type, string target)
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, type, target);
        if (type == ItemType.SpawnItem) stone.SpawnItem!.SetDelay(9, 9);
        else stone.SpawnChar!.SetDelay(9, 9);

        Serial memberUid;
        if (type == ItemType.SpawnItem)
        {
            stone.SpawnItem!.RespawnNow();
            memberUid = stone.SpawnItem.SpawnedUids[0];
        }
        else
        {
            stone.SpawnChar!.RespawnNow();
            memberUid = stone.SpawnChar.SpawnedUids[0];
        }

        Item? seenPoint = null;
        long seenSeconds = -999;
        SpawnComponent.OnSpawnTrigger = (_, trigger, args) =>
        {
            if (trigger == ItemTrigger.DelObj)
            {
                seenPoint = args.SpawnPoint;
                seenSeconds = args.N1;
                args.N1 = 111;
            }
            return TriggerResult.Default;
        };
        try
        {
            if (type == ItemType.SpawnItem) stone.SpawnItem!.DelObj(memberUid);
            else stone.SpawnChar!.DelObj(memberUid);
        }
        finally { SpawnComponent.OnSpawnTrigger = null; }

        Assert.Same(stone, seenPoint);
        Assert.True(seenSeconds >= 0, $"the remaining seconds should be shown, saw {seenSeconds}");
        long remainingMs = stone.Timeout - Environment.TickCount64;
        Assert.InRange(remainingMs, 100_000, 112_000);   // ~111 s, not the 9-minute default
    }

    // ================================================================ 12P-4

    [Fact]
    public void TheGeneralCreateChainSeesAPlacedAndAttachedCreature()
    {
        var res = LoadResources();
        var world = NewWorld();
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");

        Point3D seenPos = default;
        bool seenSpawned = false;
        int seenCount = -1;
        SpawnComponent.OnNpcScriptInit = ch =>
        {
            seenPos = ch.Position;
            seenSpawned = ch.IsStatFlag(StatFlag.Spawned);
            seenCount = stone.SpawnChar!.CurrentCount;
        };
        try { stone.SpawnChar!.RespawnNow(); }
        finally { SpawnComponent.OnNpcScriptInit = null; }

        Assert.Equal(100, seenPos.X);
        Assert.True(seenSpawned);
        Assert.Equal(1, seenCount);
    }

    // ================================================================ 12P-5

    [Theory]
    [InlineData(ItemType.SpawnChar, "c_other_np")]
    [InlineData(ItemType.SpawnItem, "i_prize_np")]
    public void FillingTheLastSlotParksTheTimer(ItemType type, string target)
    {
        var res = LoadResources();
        var world = NewWorld();
        using var _ = WireOwnership(world);
        var stone = Spawner(world, res, type, target);

        if (type == ItemType.SpawnItem) stone.SpawnItem!.RespawnNow();
        else stone.SpawnChar!.RespawnNow();

        Assert.True(stone.Timeout < 0, "a full spawner should not stay scheduled");
    }

    [Fact]
    public void FillingTheLastSlotByHandParksTheTimerToo()
    {
        var res = LoadResources();
        var world = NewWorld();
        using var _ = WireOwnership(world);
        var stone = Spawner(world, res, ItemType.SpawnChar, "c_other_np");
        var loose = world.CreateCharacter();
        world.PlaceCharacter(loose, new Point3D(101, 100, 0, 0));

        Assert.True(stone.TrySetProperty("ADDOBJ", $"0{loose.Uid.Value:X}"));

        Assert.Equal(1, stone.SpawnChar!.CurrentCount);
        Assert.True(stone.Timeout < 0);
    }
}
