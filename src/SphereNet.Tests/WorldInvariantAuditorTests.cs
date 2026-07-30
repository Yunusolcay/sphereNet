using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Proves the world-load invariant auditor detects each class of "fine in .edit,
/// broken in the client" bug this project has actually hit — so a future
/// regression is caught by loading data and auditing, not by playing the game.
/// The final test runs the auditor against the real live pack+save when present.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WorldInvariantAuditorTests
{
    private readonly ITestOutputHelper _out;
    public WorldInvariantAuditorTests(ITestOutputHelper o) => _out = o;

    private static GameWorld MakeWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void CleanWorld_ProducesNoAnomalies()
    {
        var world = MakeWorld();
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        world.PlaceItem(pack, new Point3D(100, 100, 0, 0));
        var child = world.CreateItem();
        pack.AddItem(child);

        Assert.Empty(WorldInvariantAuditor.Audit(world));
    }

    [Fact]
    public void DetectsContainerIndexMissing_TheEmptyBagBug()
    {
        var world = MakeWorld();
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        world.PlaceItem(pack, new Point3D(100, 100, 0, 0));
        var child = world.CreateItem();
        pack.AddItem(child); // properly indexed

        // Simulate the load-order bug: the item stays in Contents but drops out of
        // the client-facing container index (what happened when ResolveWorld was
        // unwired during load).
        world.ContainerIndexRemove(pack.Uid.Value, child);

        var anomalies = WorldInvariantAuditor.Audit(world);
        Assert.Contains(anomalies, a =>
            a.Kind == WorldInvariantAuditor.Kind.ContainerIndexMissing &&
            a.Uid == child.Uid.Value);

        // The backstop rebuild clears it — exactly the shipped fix.
        world.RebuildContainerIndex();
        Assert.DoesNotContain(WorldInvariantAuditor.Audit(world),
            a => a.Kind == WorldInvariantAuditor.Kind.ContainerIndexMissing);
    }

    /// <summary>The orphan direction must be checked even when the container is
    /// server-side EMPTY: index entries under an empty bag are items the client
    /// draws but nobody can pick up. The check used to bail out on
    /// ContentCount == 0 and never saw them.</summary>
    [Fact]
    public void DetectsContainerIndexOrphan_InAServerSideEmptyBag()
    {
        var world = MakeWorld();
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        world.PlaceItem(pack, new Point3D(100, 100, 0, 0));
        var ghost = world.CreateItem();
        pack.AddItem(ghost);

        // Drop it from the authoritative Contents only — the index keeps serving
        // it to the client.
        pack.RemoveItem(ghost);
        world.ContainerIndexAdd(pack.Uid.Value, ghost);

        Assert.Equal(0, pack.ContentCount);
        Assert.Contains(WorldInvariantAuditor.Audit(world), a =>
            a.Kind == WorldInvariantAuditor.Kind.ContainerIndexOrphan &&
            a.Uid == ghost.Uid.Value);
    }

    /// <summary>An item parked on a character that is neither worn, remembered
    /// nor dragged is unreachable — the server-side shape of "my item vanished".
    /// </summary>
    [Fact]
    public void DetectsCharacterLimboItem_TheVanishedItem()
    {
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Tester";
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var lost = world.CreateItem();
        lost.ContainedIn = ch.Uid; // never equipped, never a memory

        Assert.Contains(WorldInvariantAuditor.Audit(world), a =>
            a.Kind == WorldInvariantAuditor.Kind.CharacterLimboItem &&
            a.Uid == lost.Uid.Value);
    }

    /// <summary>Worn gear, memories and the item currently in hand are all
    /// legitimately parked on the character and must not be flagged.</summary>
    [Fact]
    public void CharacterContainment_AcceptsWornMemoryAndDraggedItems()
    {
        var world = MakeWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var worn = world.CreateItem();
        worn.ItemType = ItemType.Container;
        ch.Equip(worn, Layer.Pack);

        var memory = ch.Memory_CreateSpellEffect(1, 0x2053, 5, Serial.Invalid, "bless");

        var dragged = world.CreateItem();
        dragged.ContainedIn = ch.Uid;
        ch.SetTag("DRAGGING", dragged.Uid.Value.ToString());

        Assert.DoesNotContain(WorldInvariantAuditor.Audit(world),
            a => a.Kind == WorldInvariantAuditor.Kind.CharacterLimboItem);
        Assert.NotNull(memory);
    }

    [Fact]
    public void DetectsContainerParentMissing()
    {
        var world = MakeWorld();
        var orphan = world.CreateItem();
        orphan.ContainedIn = new Serial(0xDEADBEEF); // no such parent

        Assert.Contains(WorldInvariantAuditor.Audit(world),
            a => a.Kind == WorldInvariantAuditor.Kind.ContainerParentMissing &&
                 a.Uid == orphan.Uid.Value);
    }

    [Fact]
    public void DetectsSpawnerOverCount_TheRunawayWorldgem()
    {
        var world = MakeWorld();
        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnChar;
        world.PlaceItem(spawner, new Point3D(100, 100, 0, 0));
        spawner.SpawnChar = new SpawnComponent(spawner, world) { CharDefId = 0x0190, MaxCount = 1 };

        // Far more live children than the cap — the accumulation the ADDOBJ re-link
        // fix prevents. RegisterExisting de-dupes, so use distinct serials.
        for (uint i = 1; i <= 12; i++)
            spawner.SpawnChar.RegisterExisting(new Serial(0x50000000 + i));

        Assert.Contains(WorldInvariantAuditor.Audit(world),
            a => a.Kind == WorldInvariantAuditor.Kind.SpawnerOverCount &&
                 a.Uid == spawner.Uid.Value);
    }

    [Fact]
    public void DetectsItemTypeDivergence_TheUnmaterialisedRawType()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_audit_type_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, "[ITEMDEF 064]\r\nDEFNAME=i_audit_door\r\nTYPE=t_door\r\n");
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
            res.LoadResourceFile(tmp);
            new DefinitionLoader(res, new SpellRegistry()).LoadAll();

            var world = MakeWorld();
            var item = world.CreateItem();
            item.BaseId = 0x64; // legacy graphic only — raw _type left Normal (the bug)

            Assert.Contains(WorldInvariantAuditor.Audit(world),
                a => a.Kind == WorldInvariantAuditor.Kind.ItemTypeDivergence &&
                     a.Uid == item.Uid.Value);

            // Materialisation (the shipped load-time fix) resolves it.
            item.MaterializeDefinitionType();
            Assert.DoesNotContain(WorldInvariantAuditor.Audit(world),
                a => a.Kind == WorldInvariantAuditor.Kind.ItemTypeDivergence);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Runs the auditor over the REAL live pack+save when present on the
    /// box. Skips cleanly in CI. This is the "catch it without playing" net: it
    /// loads exactly what the server loads and shouts if anything is inconsistent.</summary>
    [Fact]
    public void LiveWorld_HasNoCriticalAnomalies()
    {
        (string scripts, string save)[] candidates =
        {
            (@"C:\sphereNetServer\scripts", @"C:\sphereNetServer\save"),
            (@"C:\56T\scripts", @"C:\56T\save"),
        };
        var picked = candidates.FirstOrDefault(c =>
            Directory.Exists(c.scripts) && File.Exists(Path.Combine(c.save, "sphereworld.scp")));
        if (picked.scripts == null)
        {
            _out.WriteLine("SKIP: no live pack/save present");
            return;
        }

        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        foreach (var file in Directory.EnumerateFiles(picked.scripts, "*.scp", SearchOption.AllDirectories))
            resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;               // wired BEFORE load (index fix)
        Item.ResolveDefName = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            return rid.IsValid && rid.Type == ResType.ItemDef ? (ushort)rid.Index : (ushort)0;
        };
        try
        {
            var loader = new SphereNet.Persistence.Load.WorldLoader(lf);
            loader.ApplyCharDefFromName = (ch, defname) =>
                CharDefHelper.TryApplyDefName(ch, defname, resources);
            loader.ResolveBodyFromCharDefIndex = idx => CharDefHelper.ResolveBodyId(idx, resources);
            loader.Load(world, picked.save);

            // Mirror the bootstrap spawner init so spawner invariants are real.
            foreach (var obj in world.GetAllObjects().ToArray())
                if (obj is Item it && it.ItemType is ItemType.SpawnChar or ItemType.SpawnItem)
                    it.InitializeSpawnComponent(world, resources);

            var anomalies = WorldInvariantAuditor.Audit(world);
            var byKind = anomalies.GroupBy(a => a.Kind)
                .ToDictionary(g => g.Key, g => g.Count());
            _out.WriteLine($"audited world; anomalies={anomalies.Count}");
            foreach (var kv in byKind)
                _out.WriteLine($"  {kv.Key}: {kv.Value}");
            foreach (var a in anomalies.Take(20))
                _out.WriteLine($"    {a}");

            int missing = byKind.GetValueOrDefault(WorldInvariantAuditor.Kind.ContainerIndexMissing);
            int divergence = byKind.GetValueOrDefault(WorldInvariantAuditor.Kind.ItemTypeDivergence);
            int overcount = byKind.GetValueOrDefault(WorldInvariantAuditor.Kind.SpawnerOverCount);
            Assert.True(missing == 0, $"container index missing: {missing} (bags would render empty)");
            Assert.True(divergence == 0, $"item type unmaterialised: {divergence}");
            Assert.True(overcount == 0, $"spawner runaway over-count: {overcount}");
        }
        finally
        {
            Item.ResolveDefName = null;
            ObjBase.ResolveWorld = null;
            Item.ResolveWorld = null;
        }
    }
}
