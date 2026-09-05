using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

public class SaveFormatTests
{
    private static (WorldSaver saver, WorldLoader loader) MakeIO()
    {
        var lf = LoggerFactory.Create(b => { });
        return (new WorldSaver(lf), new WorldLoader(lf));
    }

    private static GameWorld MakeWorld()
    {
        var lf = LoggerFactory.Create(b => { });
        var w = new GameWorld(lf);
        w.InitMap(0, 6144, 4096);
        return w;
    }

    private static void WriteBinaryItemFile(string path, Serial serial, string name, ushort amount, Point3D position)
    {
        using var writer = SaveIO.OpenWriter(path, SaveFormat.Binary);
        writer.BeginRecord("WORLDITEM");
        writer.WriteProperty("SERIAL", $"0{serial.Value:X8}");
        writer.WriteProperty("ID", "0EED");
        writer.WriteProperty("NAME", name);
        writer.WriteProperty("P", position.ToString());
        writer.WriteProperty("AMOUNT", amount.ToString());
        writer.EndRecord();
    }

    private static (List<Character> chars, List<Item> items) Seed(GameWorld world, int charCount, int itemCount)
    {
        var chars = new List<Character>(charCount);
        var items = new List<Item>(itemCount);
        for (int i = 0; i < charCount; i++)
        {
            var c = world.CreateCharacter();
            c.Name = $"Ch{i}";
            c.BodyId = 0x0190;
            c.Str = (short)(50 + i);
            c.Dex = 50; c.Int = 50;
            c.MaxHits = 100; c.Hits = 100;
            c.IsPlayer = i == 0;
            world.PlaceCharacter(c, new Point3D((short)(1000 + i), 1000, 0, 0));
            chars.Add(c);
        }
        for (int i = 0; i < itemCount; i++)
        {
            var it = world.CreateItem();
            it.BaseId = (ushort)(0x0EED + (i % 3));
            it.Amount = (ushort)(1 + i);
            it.SetTag("TESTTAG", $"val{i}");
            world.PlaceItem(it, new Point3D((short)(2000 + i), 2000, 0, 0));
            items.Add(it);
        }
        return (chars, items);
    }

    [Theory]
    [InlineData(SaveFormat.Text, 0)]
    [InlineData(SaveFormat.TextGz, 0)]
    [InlineData(SaveFormat.Binary, 0)]
    [InlineData(SaveFormat.BinaryGz, 0)]
    [InlineData(SaveFormat.Text, 4)]
    [InlineData(SaveFormat.Binary, 4)]
    [InlineData(SaveFormat.BinaryGz, 8)]
    public void Roundtrip_PreservesEntityCountsAndKeyFields(SaveFormat fmt, int shards)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_save_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = fmt;
            saver.ShardCount = shards;

            var src = MakeWorld();
            var (srcChars, srcItems) = Seed(src, charCount: 7, itemCount: 11);
            Assert.True(saver.Save(src, tmp));

            // Load into a fresh world and compare.
            var dst = MakeWorld();
            var (itemCount, charCount) = loader.Load(dst, tmp);
            Assert.Equal(srcItems.Count, itemCount);
            Assert.Equal(srcChars.Count, charCount);

            // Roundtrip key fields on one sample entity.
            var sampleItem = srcItems[0];
            var reloadedItem = dst.FindItem(sampleItem.Uid);
            Assert.NotNull(reloadedItem);
            Assert.Equal(sampleItem.BaseId, reloadedItem!.BaseId);
            Assert.Equal(sampleItem.Amount, reloadedItem.Amount);
            Assert.True(reloadedItem.TryGetTag("TESTTAG", out var tagVal));
            Assert.Equal("val0", tagVal);

            var sampleChar = srcChars[^1];
            var reloadedChar = dst.FindChar(sampleChar.Uid);
            Assert.NotNull(reloadedChar);
            Assert.Equal(sampleChar.Name, reloadedChar!.Name);
            Assert.Equal(sampleChar.Str, reloadedChar.Str);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesGmPages()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_gmpage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();
            src.AddGmPage(new GameWorld.GmPageRecord("Alice", "stuck in a wall", "", "open", 1234567890));
            // A reason containing a comma must survive (section-per-page format).
            src.AddGmPage(new GameWorld.GmPageRecord("Bob", "lost item, need help", "GM_Joe", "assigned", 1234567999));
            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            Assert.Equal(2, dst.GmPages.Count);
            Assert.Equal("Alice", dst.GmPages[0].Account);
            Assert.Equal("stuck in a wall", dst.GmPages[0].Reason);
            Assert.Equal("open", dst.GmPages[0].Status);
            Assert.Equal(1234567890, dst.GmPages[0].Created);
            Assert.Equal("Bob", dst.GmPages[1].Account);
            Assert.Equal("lost item, need help", dst.GmPages[1].Reason);
            Assert.Equal("GM_Joe", dst.GmPages[1].Handler);
            Assert.Equal("assigned", dst.GmPages[1].Status);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesPendingTimerF()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_timerf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var item = src.CreateItem();
            item.BaseId = 0x0EED;
            src.PlaceItem(item, new Point3D(2000, 2000, 0, 0));
            // A delayed function ~1h out, args containing spaces (only the first two '|'
            // delimiters are structural).
            item.AddTimerF(3_600_000, "f_delayed", "arg one two");

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reloaded = dst.FindItem(item.Uid);
            Assert.NotNull(reloaded);
            var entry = Assert.Single(reloaded!.TimerFEntries);
            Assert.Equal("f_delayed", entry.FunctionName);
            Assert.Equal("arg one two", entry.Args); // args + spaces survive

            // The remaining time is preserved (not reset to 0 or the timer dropped):
            // not due now, still pending, fires only well after load.
            long now = Environment.TickCount64;
            Assert.Empty(reloaded.DequeueDueTimerF(now));
            Assert.Single(reloaded.DequeueDueTimerF(now + 10_000_000));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesMultiStructureType()
    {
        // B5: a Multi / MultiCustom / Ship item's BaseId is a raw multi index with no
        // ITEMDEF, so its TYPE cannot be re-derived from a def on load. Without persisting
        // TYPE the reloaded structure becomes t_normal and Housing/Ship DeserializeFromWorld
        // never re-registers it — the house/ship is lost on restart.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_multitype_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var house = src.CreateItem();
            house.BaseId = 0x64;
            house.ItemType = ItemType.Multi;
            house.Name = "a stone house";
            src.PlaceItem(house, new Point3D(2000, 2000, 0, 0));

            var ship = src.CreateItem();
            ship.BaseId = 0x08;
            ship.ItemType = ItemType.Ship;
            src.PlaceItem(ship, new Point3D(2005, 2005, 0, 0));

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reHouse = dst.FindItem(house.Uid);
            Assert.NotNull(reHouse);
            Assert.Equal(ItemType.Multi, reHouse!.ItemType); // structure type survives

            var reShip = dst.FindItem(ship.Uid);
            Assert.NotNull(reShip);
            Assert.Equal(ItemType.Ship, reShip!.ItemType);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesItemBaseWeightOverride()
    {
        // Wave 249: a per-item BASEWEIGHT override (Source-X m_weight) survives a save.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_baseweight_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var item = src.CreateItem();
            item.BaseId = 0x0F5E;
            src.PlaceItem(item, new Point3D(2000, 2000, 0, 0));
            item.TrySetProperty("BASEWEIGHT", "50");
            Assert.Equal(50, item.Weight);

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reloaded = dst.FindItem(item.Uid);
            Assert.NotNull(reloaded);
            Assert.Equal(50, reloaded!.WeightOverride);
            Assert.Equal(50, reloaded.Weight);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesSpawnerTimerAcrossComponentInit()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_spawn_timer_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var spawner = src.CreateItem();
            spawner.BaseId = 0x1F13;
            spawner.ItemType = ItemType.SpawnChar;
            spawner.More1 = 0x0190;
            src.PlaceItem(spawner, new Point3D(1000, 1000, 0, 0));
            spawner.SetTimeout(Environment.TickCount64 + 3_600_000);

            var itemSpawner = src.CreateItem();
            itemSpawner.BaseId = 0x1F14;
            itemSpawner.ItemType = ItemType.SpawnItem;
            itemSpawner.SpawnItem = new SphereNet.Game.Components.ItemSpawnComponent(itemSpawner, src);
            itemSpawner.SpawnItem.ResetTimer();
            Assert.True(itemSpawner.Timeout > Environment.TickCount64);
            src.PlaceItem(itemSpawner, new Point3D(1001, 1000, 0, 0));

            Assert.True(saver.Save(src, tmp));
            string itemSave = File.ReadAllText(Path.Combine(tmp, "sphereworld.scp"));
            Assert.Contains("TIMERMS=", itemSave);

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reloaded = dst.FindItem(spawner.Uid);
            Assert.NotNull(reloaded);
            long loadedTimeout = reloaded!.Timeout;
            Assert.True(loadedTimeout > Environment.TickCount64 + 3_000_000);

            reloaded.ItemType = ItemType.SpawnChar;
            var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
            reloaded.InitializeSpawnComponent(dst, resources, loadedTimeout);

            Assert.Equal(loadedTimeout, reloaded.Timeout);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldOps_ExportAndLoadFile_RoundtripsMixedScp()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_worldops_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var item = src.CreateItem();
            item.BaseId = 0x0EED;
            item.Amount = 42;
            item.Name = "coins";
            src.PlaceItem(item, new Point3D(2000, 2000, 0, 0));

            var ch = src.CreateCharacter();
            ch.Name = "Importer";
            ch.BodyId = 0x0190;
            ch.Str = 55; ch.Dex = 44; ch.Int = 33;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            var staticItem = src.CreateItem();
            staticItem.BaseId = 0x0B80;
            staticItem.Name = "static sign";
            staticItem.SetAttr(ObjAttributes.Static);
            src.PlaceItem(staticItem, new Point3D(2010, 2000, 0, 0));

            string worldPath = Path.Combine(tmp, "export_world.scp");
            Assert.Equal(2, saver.ExportWorld(src, worldPath)); // dynamic item + char; static skipped
            Assert.Contains("[WORLDITEM", File.ReadAllText(worldPath));
            Assert.Contains("[WORLDCHAR", File.ReadAllText(worldPath));

            var dst = MakeWorld();
            var (items, chars) = loader.LoadFile(dst, worldPath);
            Assert.Equal(1, items);
            Assert.Equal(1, chars);
            Assert.Equal(42, dst.FindItem(item.Uid)!.Amount);
            Assert.Equal("Importer", dst.FindChar(ch.Uid)!.Name);

            string staticPath = Path.Combine(tmp, "statics.scp");
            Assert.Equal(1, saver.ExportStatics(src, staticPath));
            var staticDst = MakeWorld();
            var (staticItems, staticChars) = loader.LoadFile(staticDst, staticPath);
            Assert.Equal(1, staticItems);
            Assert.Equal(0, staticChars);
            Assert.True(staticDst.FindItem(staticItem.Uid)!.IsAttr(ObjAttributes.Static));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldOps_RestoreFile_ReplacesExistingSerial()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var item = src.CreateItem();
            item.BaseId = 0x0EED;
            item.Amount = 42;
            item.Name = "coins";
            src.PlaceItem(item, new Point3D(2000, 2000, 0, 0));

            string path = Path.Combine(tmp, "one.scp");
            Assert.Equal(1, saver.ExportObject(item, path));

            var dst = MakeWorld();
            var (loadedItems, loadedChars) = loader.LoadFile(dst, path);
            Assert.Equal(1, loadedItems);
            Assert.Equal(0, loadedChars);

            var original = dst.FindItem(item.Uid)!;
            Assert.Equal(42, original.Amount);

            item.Amount = 77;
            item.Name = "replacement coins";
            Assert.Equal(1, saver.ExportObject(item, path));

            var (duplicateLoadItems, duplicateLoadChars) = loader.LoadFile(dst, path);
            Assert.Equal(0, duplicateLoadItems);
            Assert.Equal(0, duplicateLoadChars);
            Assert.Equal(42, dst.FindItem(item.Uid)!.Amount);

            var (restoredItems, restoredChars, replaced) = loader.RestoreFile(dst, path);
            Assert.Equal(1, replaced);
            Assert.Equal(1, restoredItems);
            Assert.Equal(0, restoredChars);
            Assert.True(original.IsDeleted);

            var restored = dst.FindItem(item.Uid)!;
            Assert.Equal(77, restored.Amount);
            Assert.Equal("replacement coins", restored.Name);
            Assert.NotSame(original, restored);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldOps_RestoreFile_RollsBackExistingSerialWhenLoadFails()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_restore_rollback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var serial = new Serial(0x4000BEEF);
            var position = new Point3D(2000, 2000, 0, 0);

            string originalPath = Path.Combine(tmp, "original.sbin");
            WriteBinaryItemFile(originalPath, serial, "old coins", 42, position);

            var dst = MakeWorld();
            var (loadedItems, loadedChars) = loader.LoadFile(dst, originalPath);
            Assert.Equal(1, loadedItems);
            Assert.Equal(0, loadedChars);

            var original = dst.FindItem(serial)!;
            Assert.Equal("old coins", original.Name);
            Assert.Equal(42, original.Amount);

            string restorePath = Path.Combine(tmp, "restore.sbin");
            WriteBinaryItemFile(restorePath, serial, "new coins", 77, position);

            int backedUp = 0;
            Assert.ThrowsAny<Exception>(() => loader.RestoreFile(dst, restorePath, backupWriter: (objects, backupPath) =>
            {
                backedUp = saver.ExportObjects(objects, backupPath);
                File.WriteAllBytes(restorePath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                return backedUp;
            }));

            Assert.Equal(1, backedUp);
            Assert.True(original.IsDeleted);

            var restored = dst.FindItem(serial)!;
            Assert.NotSame(original, restored);
            Assert.Equal("old coins", restored.Name);
            Assert.Equal(42, restored.Amount);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldOps_ScopedExport_FiltersByFlagsDistanceAndKeepsContainerGraph()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_scope_export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var center = src.CreateCharacter();
            center.Name = "scope center";
            center.BodyId = 0x0190;
            src.PlaceCharacter(center, new Point3D(1000, 1000, 0, 0));

            var nearChar = src.CreateCharacter();
            nearChar.Name = "near char";
            nearChar.BodyId = 0x0190;
            src.PlaceCharacter(nearChar, new Point3D(1005, 1000, 0, 0));

            var farChar = src.CreateCharacter();
            farChar.Name = "far char";
            farChar.BodyId = 0x0190;
            src.PlaceCharacter(farChar, new Point3D(1030, 1000, 0, 0));

            var nearItem = src.CreateItem();
            nearItem.BaseId = 0x0EED;
            nearItem.Name = "near coins";
            src.PlaceItem(nearItem, new Point3D(1002, 1000, 0, 0));

            var farItem = src.CreateItem();
            farItem.BaseId = 0x0EED;
            farItem.Name = "far coins";
            src.PlaceItem(farItem, new Point3D(1030, 1000, 0, 0));

            var chest = src.CreateItem();
            chest.BaseId = 0x0E75;
            chest.Name = "near chest";
            src.PlaceItem(chest, new Point3D(1001, 1001, 0, 0));

            var gem = src.CreateItem();
            gem.BaseId = 0x0F21;
            gem.Name = "chest gem";
            chest.AddItem(gem);

            var staticNear = src.CreateItem();
            staticNear.BaseId = 0x0B80;
            staticNear.Name = "near static";
            staticNear.SetAttr(ObjAttributes.Static);
            src.PlaceItem(staticNear, new Point3D(1001, 1000, 0, 0));

            var staticFar = src.CreateItem();
            staticFar.BaseId = 0x0B80;
            staticFar.Name = "far static";
            staticFar.SetAttr(ObjAttributes.Static);
            src.PlaceItem(staticFar, new Point3D(1030, 1000, 0, 0));

            var bothScope = new WorldSaver.WorldExportScope(center.Position, 10, 3);
            string bothPath = Path.Combine(tmp, "both.scp");
            Assert.Equal(5, saver.ExportWorld(src, bothPath, bothScope));

            string bothText = File.ReadAllText(bothPath);
            Assert.Contains("near char", bothText);
            Assert.Contains("near coins", bothText);
            Assert.Contains("near chest", bothText);
            Assert.Contains("chest gem", bothText);
            Assert.DoesNotContain("far char", bothText);
            Assert.DoesNotContain("far coins", bothText);
            Assert.DoesNotContain("near static", bothText);

            var dst = MakeWorld();
            var (items, chars) = loader.LoadFile(dst, bothPath);
            Assert.Equal(3, items);
            Assert.Equal(2, chars);
            Assert.Equal(chest.Uid, dst.FindItem(gem.Uid)!.ContainedIn);

            string itemsOnlyPath = Path.Combine(tmp, "items.scp");
            Assert.Equal(3, saver.ExportWorld(src, itemsOnlyPath,
                new WorldSaver.WorldExportScope(center.Position, 10, 1)));
            string itemsOnlyText = File.ReadAllText(itemsOnlyPath);
            Assert.Contains("near coins", itemsOnlyText);
            Assert.DoesNotContain("[WORLDCHAR", itemsOnlyText);

            string charsOnlyPath = Path.Combine(tmp, "chars.scp");
            Assert.Equal(2, saver.ExportWorld(src, charsOnlyPath,
                new WorldSaver.WorldExportScope(center.Position, 10, 2)));
            string charsOnlyText = File.ReadAllText(charsOnlyPath);
            Assert.Contains("near char", charsOnlyText);
            Assert.DoesNotContain("[WORLDITEM", charsOnlyText);

            string staticsPath = Path.Combine(tmp, "statics.scp");
            Assert.Equal(1, saver.ExportStatics(src, staticsPath, bothScope));
            string staticsText = File.ReadAllText(staticsPath);
            Assert.Contains("near static", staticsText);
            Assert.DoesNotContain("far static", staticsText);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldOps_ScopedImport_FiltersByFlagsDistanceAndKeepsContainerGraph()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_scope_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var center = src.CreateCharacter();
            center.Name = "scope center";
            center.BodyId = 0x0190;
            src.PlaceCharacter(center, new Point3D(1000, 1000, 0, 0));

            var nearChar = src.CreateCharacter();
            nearChar.Name = "near import char";
            nearChar.BodyId = 0x0190;
            src.PlaceCharacter(nearChar, new Point3D(1005, 1000, 0, 0));

            var farChar = src.CreateCharacter();
            farChar.Name = "far import char";
            farChar.BodyId = 0x0190;
            src.PlaceCharacter(farChar, new Point3D(1030, 1000, 0, 0));

            var nearItem = src.CreateItem();
            nearItem.BaseId = 0x0EED;
            nearItem.Name = "near import coins";
            src.PlaceItem(nearItem, new Point3D(1002, 1000, 0, 0));

            var farItem = src.CreateItem();
            farItem.BaseId = 0x0EED;
            farItem.Name = "far import coins";
            src.PlaceItem(farItem, new Point3D(1030, 1000, 0, 0));

            var chest = src.CreateItem();
            chest.BaseId = 0x0E75;
            chest.Name = "near import chest";
            src.PlaceItem(chest, new Point3D(1001, 1001, 0, 0));

            var gem = src.CreateItem();
            gem.BaseId = 0x0F21;
            gem.Name = "import chest gem";
            chest.AddItem(gem);

            string path = Path.Combine(tmp, "world.scp");
            Assert.Equal(7, saver.ExportWorld(src, path));

            var bothDst = MakeWorld();
            var bothScope = new WorldLoader.WorldImportScope(center.Position, 10, 3);
            var (bothItems, bothChars) = loader.LoadFile(bothDst, path, scope: bothScope);
            Assert.Equal(3, bothItems);
            Assert.Equal(2, bothChars);
            Assert.NotNull(bothDst.FindChar(center.Uid));
            Assert.NotNull(bothDst.FindChar(nearChar.Uid));
            Assert.Null(bothDst.FindChar(farChar.Uid));
            Assert.NotNull(bothDst.FindItem(nearItem.Uid));
            Assert.NotNull(bothDst.FindItem(chest.Uid));
            Assert.Equal(chest.Uid, bothDst.FindItem(gem.Uid)!.ContainedIn);
            Assert.Null(bothDst.FindItem(farItem.Uid));

            var itemsOnlyDst = MakeWorld();
            var (itemsOnlyItems, itemsOnlyChars) = loader.LoadFile(itemsOnlyDst, path,
                scope: new WorldLoader.WorldImportScope(center.Position, 10, 1));
            Assert.Equal(3, itemsOnlyItems);
            Assert.Equal(0, itemsOnlyChars);
            Assert.NotNull(itemsOnlyDst.FindItem(nearItem.Uid));
            Assert.Null(itemsOnlyDst.FindChar(nearChar.Uid));

            var charsOnlyDst = MakeWorld();
            var (charsOnlyItems, charsOnlyChars) = loader.LoadFile(charsOnlyDst, path,
                scope: new WorldLoader.WorldImportScope(center.Position, 10, 2));
            Assert.Equal(0, charsOnlyItems);
            Assert.Equal(2, charsOnlyChars);
            Assert.NotNull(charsOnlyDst.FindChar(nearChar.Uid));
            Assert.Null(charsOnlyDst.FindItem(nearItem.Uid));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesActivePoison()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_poison_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var ch = src.CreateCharacter();
            ch.Name = "Victim";
            ch.BodyId = 0x0190;
            ch.MaxHits = 100; ch.Hits = 100;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            var poisoner = new Serial(0x40001234);
            ch.ApplyPoison(3, poisoner); // greater poison → 6 ticks (Source-X OSI charges)
            // Advance one tick so the remaining count (5) is non-fresh — proving load
            // restores the exact remaining state rather than re-applying a fresh poison.
            ch.ProcessPoisonTick(Environment.TickCount64 + 10_000);
            Assert.Equal(5, ch.Poison.TicksRemaining);

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reloaded = dst.FindChar(ch.Uid);
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.IsPoisoned);
            Assert.Equal((byte)3, reloaded.PoisonLevel);
            Assert.Equal(5, reloaded.Poison.TicksRemaining); // remaining, not re-freshed to 6
            Assert.Equal(poisoner, reloaded.Poison.Source);    // poisoner kept for kill attribution
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_MaxPoolSuitBonus_PersistsBaseNotEffective()
    {
        // Wave 264: an equipped BONUSHITSMAX piece raises the effective MaxHits on
        // read, but the world save must serialize the BASE pool (100), never the
        // inflated effective total (120) — else the suit bonus would be baked into
        // the base and compound every save cycle.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_maxpool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var ch = src.CreateCharacter();
            ch.Name = "Suited";
            ch.BodyId = 0x0190;
            ch.MaxHits = 100; ch.Hits = 100;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            var helm = src.CreateItem();
            helm.BaseId = 0x1408; // plate helm
            helm.SetTag("BONUSHITSMAX", "20");
            ch.Equip(helm, Layer.Helm);
            ch.Hits = 120; // healed into the effective (suit) pool

            Assert.Equal(120, ch.MaxHits);      // effective read
            Assert.Equal(100, ch.BaseMaxHits);  // base is what saves

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reloaded = dst.FindChar(ch.Uid);
            Assert.NotNull(reloaded);
            // The persisted base is 100, not the inflated 120 — no compounding.
            Assert.Equal(100, reloaded!.BaseMaxHits);
            // Invariant holds after reload regardless of equip-restore timing.
            Assert.True(reloaded.Hits <= reloaded.MaxHits);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Load_ContItemWithoutLayer_EquipsViaTiledataLayer()
    {
        // Classic Sphere saves store worn clothing under CONT=<char> with NO LAYER
        // (and the itemdef often has no layer either — TYPE=t_clothing). The layer
        // then comes from tiledata (the graphic's Quality byte). Without deriving it,
        // the item equipped on Layer.None and the NPC rendered naked.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_naked_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            const ushort shirtGraphic = 0x1517;
            using (var w = SaveIO.OpenWriter(Path.Combine(tmp, "sphereworld.scp"), SaveFormat.Text))
            {
                w.BeginRecord("WORLDCHAR");
                w.WriteProperty("SERIAL", "01000101");
                w.WriteProperty("BODY", "0190");
                w.WriteProperty("P", "1000,1000,0");
                w.EndRecord();

                w.BeginRecord("WORLDITEM");
                w.WriteProperty("SERIAL", "040000101");
                w.WriteProperty("ID", "01517"); // leading 0 -> hex 0x1517
                w.WriteProperty("CONT", "01000101"); // equipped on the char, no LAYER
                w.EndRecord();
            }

            var (_, loader) = MakeIO();
            // Stand in for tiledata: the shirt graphic equips on the Shirt layer.
            loader.ResolveEquipLayerFromTile = baseId =>
                baseId == shirtGraphic ? (byte)Layer.Shirt : (byte)0;

            var world = MakeWorld();
            loader.Load(world, tmp);

            var ch = world.FindChar(new Serial(0x1000101));
            Assert.NotNull(ch);
            var shirt = ch!.GetEquippedItem(Layer.Shirt);
            Assert.NotNull(shirt);                    // dressed, not naked
            Assert.Equal(shirtGraphic, shirt!.BaseId);
            // The bogus layer-0 equip must not happen.
            Assert.Null(ch.GetEquippedItem(Layer.None));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Load_ClassicOStatsOnly_SeedBaseStats()
    {
        // Classic Sphere saves persist ONLY OSTR/ODEX/OINT for NPCs; Source-X
        // r_LoadVal maps them to Stat_SetBase, same as STR/DEX/INT. Mirroring
        // them into the O-fields alone loaded every legacy NPC with 0 stats
        // (the "spawned NPCs have STR 0" field report).
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_ostat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using (var w = SaveIO.OpenWriter(Path.Combine(tmp, "sphereworld.scp"), SaveFormat.Text))
            {
                w.BeginRecord("WORLDCHAR");
                w.WriteProperty("SERIAL", "01000201");
                w.WriteProperty("BODY", "011");
                w.WriteProperty("P", "1000,1000,0");
                w.WriteProperty("OSTR", "104");
                w.WriteProperty("ODEX", "89");
                w.WriteProperty("OINT", "42");
                w.WriteProperty("HITS", "104");
                w.EndRecord();
            }

            var (_, loader) = MakeIO();
            var world = MakeWorld();
            loader.Load(world, tmp);

            var ch = world.FindChar(new Serial(0x1000201));
            Assert.NotNull(ch);
            Assert.Equal(104, ch!.Str);
            Assert.Equal(89, ch.Dex);
            Assert.Equal(42, ch.Int);
            Assert.Equal(104, ch.OStr);
            Assert.Equal(104, ch.Hits);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesPerCharRegenOverrides()
    {
        // Wave 247: per-char regen rate overrides (Source-X CChar REGENHITS/…/FOOD).
        // Saved as tenths (D) so both a positive override and a "never" (-1) value
        // round-trip through the loader's REGEN*D set path exactly.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_regen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var ch = src.CreateCharacter();
            ch.Name = "Regenerator";
            ch.BodyId = 0x0190;
            ch.IsPlayer = true;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            ch.TrySetProperty("REGENSTAM", "7");    // 7000 ms per-char override
            ch.TrySetProperty("REGENMANA", "-1");   // never regen mana
            ch.TrySetProperty("REGENVALHITS", "4"); // recover 4 hits per event (Wave 248)
            ch.TrySetProperty("BLOODCOLOR", "0x26");  // Wave 249
            ch.TrySetProperty("FOLLOWERSLOTS", "3");   // Wave 249
            Assert.Equal(7000, ch.RegenStamRateMs);
            Assert.Equal(-1000, ch.RegenManaRateMs);
            Assert.Equal((ushort)4, ch.RegenValHits);

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reloaded = dst.FindChar(ch.Uid);
            Assert.NotNull(reloaded);
            Assert.Equal(7000, reloaded!.RegenStamRateMs);
            Assert.Equal(-1000, reloaded.RegenManaRateMs);
            Assert.Equal((ushort)4, reloaded.RegenValHits);
            Assert.Equal((ushort)0x26, reloaded.BloodHue);
            Assert.Equal(3, reloaded.FollowerSlotsOverride);
            // Untouched stats stay on the global default (field 0).
            Assert.Equal(0, reloaded.RegenHitsRateMs);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesPerElementResistMaxCaps()
    {
        // Wave 265: RESPHYSICALMAX persistence gap — the physical resist cap was the
        // one per-element max the WorldSaver never wrote (fire/cold/poison/energy were
        // saved). All five must now round-trip through the RES*MAX set path.
        // NOTE: the cap is display/storage-only, matching Source-X (send.cpp SA block);
        // it is deliberately NOT enforced on damage.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_resmax_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var ch = src.CreateCharacter();
            ch.Name = "Capped";
            ch.BodyId = 0x0190;
            ch.IsPlayer = true;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            ch.TrySetProperty("RESPHYSICALMAX", "75");
            ch.TrySetProperty("RESFIREMAX", "80");
            ch.TrySetProperty("RESCOLDMAX", "65");
            ch.TrySetProperty("RESPOISONMAX", "90");
            ch.TrySetProperty("RESENERGYMAX", "60");
            Assert.Equal((short)75, ch.ResPhysicalMax);

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reloaded = dst.FindChar(ch.Uid);
            Assert.NotNull(reloaded);
            Assert.Equal((short)75, reloaded!.ResPhysicalMax);
            Assert.Equal((short)80, reloaded.ResFireMax);
            Assert.Equal((short)65, reloaded.ResColdMax);
            Assert.Equal((short)90, reloaded.ResPoisonMax);
            Assert.Equal((short)60, reloaded.ResEnergyMax);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesCombatMemoriesAndAttackerLog()
    {
        // W-D (wiki/hedef.txt 12.2): Source-X saves EVERY memory item (they are
        // equipped items with timers) and the attacker log. SphereNet used to
        // persist only IPet/Guard/Guild/Town/Friend, so criminal-to-whom and
        // kill attribution reset on restart.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_combatmem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var victim = src.CreateCharacter();
            victim.Name = "Victim";
            victim.BodyId = 0x0190;
            victim.IsPlayer = true;
            src.PlaceCharacter(victim, new Point3D(1000, 1000, 0, 0));

            var aggressor = src.CreateCharacter();
            aggressor.Name = "Aggressor";
            aggressor.BodyId = 0x0190;
            aggressor.IsPlayer = true;
            src.PlaceCharacter(aggressor, new Point3D(1001, 1000, 0, 0));

            // Victim remembers the aggressor (personal-grey memory) and has him
            // in the attacker log with accumulated damage.
            victim.Memory_AddObjTypes(aggressor.Uid, MemoryType.HarmedBy);
            victim.CombatState.RecordAttack(aggressor.Uid, 37);

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reVictim = dst.FindChar(victim.Uid);
            Assert.NotNull(reVictim);
            // The transient HarmedBy memory survived the restart...
            Assert.NotNull(reVictim!.Memory_FindObjTypes(aggressor.Uid, MemoryType.HarmedBy));
            // ...and the attacker log kept the damage total.
            var rec = Assert.Single(reVictim.Attackers);
            Assert.Equal(aggressor.Uid, rec.Uid);
            Assert.Equal(37, rec.TotalDamage);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesSectorEnvironment()
    {
        // W-D (wiki/hedef.txt 12.1): Source-X CSector::r_Write persists per-
        // sector LIGHT/SEASON/RAINCHANCE/COLDCHANCE; SphereNet dropped them all.
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_sectorenv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            var src = MakeWorld();

            var sector = src.GetSector(0, 3, 4);
            Assert.NotNull(sector);
            sector!.Season = 3;      // winter
            sector.Light = 25;       // GM-set local darkness
            sector.RainChance = 80;
            sector.ColdChance = 60;
            sector.Weather = 2;      // snowing

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reSector = dst.GetSector(0, 3, 4);
            Assert.NotNull(reSector);
            Assert.Equal(3, reSector!.Season);
            Assert.Equal(25, reSector.Light);
            Assert.Equal(80, reSector.RainChance);
            Assert.Equal(60, reSector.ColdChance);
            Assert.Equal(2, reSector.Weather);

            // Untouched sectors stay at defaults (nothing was written for them).
            var untouched = dst.GetSector(0, 1, 1);
            Assert.NotNull(untouched);
            Assert.Equal(0, untouched!.Season);
            Assert.Equal(15, untouched.RainChance);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesActiveSpellEffectAndExpires()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_spelleffect_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 0;

            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Bless,
                Flags = SpellFlag.TargChar | SpellFlag.Bless | SpellFlag.Good,
                EffectBase = 50,
                EffectScale = 50,
                DurationBase = 600,
                DurationScale = 600,
            });

            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.Name = "Blessed";
            ch.BodyId = 0x0190;
            ch.Str = 50; ch.Dex = 50; ch.Int = 50;
            ch.MaxHits = 100; ch.Hits = 100;
            ch.PrivLevel = PrivLevel.GM;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            var engine = new SpellEngine(src, registry);
            saver.GetSpellEffectRecords = engine.GetPersistedEffectRecords;

            Assert.True(engine.CastStart(ch, SpellType.Bless, ch.Uid, ch.Position) >= 0);
            Assert.True(engine.CastDone(ch));
            Assert.Equal(60, ch.Str);

            engine.RevertAllForSave();
            try
            {
                Assert.True(saver.Save(src, tmp));
            }
            finally
            {
                engine.ReapplyAllAfterSave();
            }

            Assert.Equal(60, ch.Str);
            string charSave = File.ReadAllText(Path.Combine(tmp, "spherechars.scp"));
            Assert.Contains("STR=50", charSave);
            Assert.Contains("SPELLEFFECT=1|17|", charSave);

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var reloaded = dst.FindChar(ch.Uid);
            Assert.NotNull(reloaded);
            Assert.Equal(50, reloaded!.Str);
            Assert.Single(reloaded.PendingSpellEffectRecords);

            var restoredEngine = new SpellEngine(dst, registry);
            Assert.Equal(1, restoredEngine.RestorePersistedEffectsFromWorld());
            Assert.Empty(reloaded.PendingSpellEffectRecords);
            Assert.Equal(60, reloaded.Str);

            restoredEngine.ProcessExpirations(Environment.TickCount64 + 120_000);
            Assert.Equal(50, reloaded.Str);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_PreservesCurseWeaponLevel()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_curseweapon_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 0;

            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.CurseWeapon,
                Flags = SpellFlag.TargChar | SpellFlag.Good,
                EffectBase = 15,
                EffectScale = 15,
                DurationBase = 600,
                DurationScale = 600,
            });

            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.Name = "Cursed";
            ch.BodyId = 0x0190;
            ch.PrivLevel = PrivLevel.GM;
            ch.MaxMana = 100; ch.Mana = 100;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            var engine = new SpellEngine(src, registry);
            saver.GetSpellEffectRecords = engine.GetPersistedEffectRecords;

            Assert.True(engine.CastStart(ch, SpellType.CurseWeapon, ch.Uid, ch.Position) >= 0);
            Assert.True(engine.CastDone(ch));
            int level = ch.CurseWeaponLevel;
            Assert.True(level > 0);

            engine.RevertAllForSave();
            Assert.Equal(0, ch.CurseWeaponLevel); // reverted for clean save
            try { Assert.True(saver.Save(src, tmp)); }
            finally { engine.ReapplyAllAfterSave(); }
            Assert.Equal(level, ch.CurseWeaponLevel); // re-applied after save

            var dst = MakeWorld();
            loader.Load(dst, tmp);
            var reloaded = dst.FindChar(ch.Uid);
            Assert.NotNull(reloaded);

            var restoredEngine = new SpellEngine(dst, registry);
            Assert.Equal(1, restoredEngine.RestorePersistedEffectsFromWorld());
            Assert.Equal(level, reloaded!.CurseWeaponLevel); // level survived the round-trip

            restoredEngine.ProcessExpirations(Environment.TickCount64 + 120_000);
            Assert.Equal(0, reloaded.CurseWeaponLevel);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FormatDetection_RoundtripsViaExtension()
    {
        Assert.Equal(SaveFormat.Text, SaveIO.FormatFromPath("foo.scp"));
        Assert.Equal(SaveFormat.TextGz, SaveIO.FormatFromPath("foo.scp.gz"));
        Assert.Equal(SaveFormat.Binary, SaveIO.FormatFromPath("foo.sbin"));
        Assert.Equal(SaveFormat.BinaryGz, SaveIO.FormatFromPath("foo.sbin.gz"));
        Assert.Equal(SaveFormat.Text, SaveIO.FormatFromPath("foo.unknown"));

        Assert.Equal("foo.scp",     SaveIO.WithExtension("foo.scp.gz", SaveFormat.Text));
        Assert.Equal("foo.sbin",    SaveIO.WithExtension("foo.scp",    SaveFormat.Binary));
        Assert.Equal("foo.sbin.gz", SaveIO.WithExtension("foo.sbin",   SaveFormat.BinaryGz));
    }

    [Fact]
    public void WorldSaver_BackupLevels_RotatesCommittedFiles()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, _) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 0;
            saver.BackupLevels = 2;

            var world = MakeWorld();
            var item = world.CreateItem();
            item.BaseId = 0x0EED;
            world.PlaceItem(item, new Point3D(100, 100, 0, 0));

            Assert.True(saver.Save(world, tmp));
            item.Amount = 2;
            Assert.True(saver.Save(world, tmp));
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.scp.bak1")));
            item.Amount = 3;
            Assert.True(saver.Save(world, tmp));

            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.scp.bak1")));
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.scp.bak2")));
            Assert.False(File.Exists(Path.Combine(tmp, "sphereworld.scp.bak3")));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Roundtrip_NormalizesStatLockAndRuneRuntimeState()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_runtime_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.Name = "Runtime";
            ch.BodyId = 0x0190;
            ch.SetStatLock(0, 2);
            ch.SetTag("STATLOCK.1", "1");
            src.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

            var rune = src.CreateItem();
            rune.BaseId = 0x1F14;
            rune.SetTag("RUNE_X", "150");
            rune.SetTag("RUNE_Y", "160");
            rune.SetTag("RUNE_Z", "3");
            rune.SetTag("RUNE_MAP", "1");
            src.PlaceItem(rune, new Point3D(101, 100, 0, 0));

            Assert.True(saver.Save(src, tmp));

            string charSave = File.ReadAllText(Path.Combine(tmp, "spherechars.scp"));
            string itemSave = File.ReadAllText(Path.Combine(tmp, "sphereworld.scp"));
            Assert.Contains("StatLock[0]=2", charSave);
            Assert.Contains("StatLock[1]=1", charSave);
            Assert.DoesNotContain("TAG.STATLOCK.", charSave);
            Assert.Contains("MOREP=150,160,3,1", itemSave);
            Assert.DoesNotContain("RUNE_X", itemSave);

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var loadedChar = dst.FindChar(ch.Uid);
            Assert.NotNull(loadedChar);
            Assert.Equal(2, loadedChar!.GetStatLock(0));
            Assert.Equal(1, loadedChar.GetStatLock(1));

            var loadedRune = dst.FindItem(rune.Uid);
            Assert.NotNull(loadedRune);
            Assert.True(loadedRune!.TryGetRuneMark(out Point3D mark));
            Assert.Equal(new Point3D(150, 160, 3, 1), mark);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ShardHash_IsDeterministicAndDistributes()
    {
        var histogram = new int[8];
        for (uint uid = 1; uid < 10_000; uid++)
            histogram[ShardManifest.ShardIndexForUid(uid, 8)]++;

        // No shard should be completely empty or take more than 40% of entries.
        foreach (int b in histogram)
        {
            Assert.InRange(b, 500, 5_000);
        }
        // Deterministic: calling twice gives the same shard.
        Assert.Equal(
            ShardManifest.ShardIndexForUid(0xABCD1234, 8),
            ShardManifest.ShardIndexForUid(0xABCD1234, 8));
    }

    [Fact]
    public void Rolling_SplitsIntoMultipleFilesAndReloads()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_roll_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;   // uncompressed so size threshold is predictable
            saver.ShardCount = 1;             // rolling mode
            saver.ShardSizeBytes = 4 * 1024;  // 4 KB per shard — forces multiple files

            var src = MakeWorld();
            // 50 items is enough to cross a 4KB threshold for text format
            // (each WORLDITEM record is ~150-300 bytes).
            var (_, srcItems) = Seed(src, charCount: 0, itemCount: 50);
            Assert.True(saver.Save(src, tmp));

            // Expect multiple rolling segments + a manifest.
            string manifestPath = Path.Combine(tmp, "sphereworld.manifest");
            Assert.True(File.Exists(manifestPath), "rolling save should produce a manifest");
            var manifest = ShardManifest.TryLoad(manifestPath);
            Assert.NotNull(manifest);
            Assert.True(manifest!.Files.Count > 1,
                $"expected >1 rolling file, got {manifest.Files.Count}");

            // Round-trip: every item should come back.
            var dst = MakeWorld();
            var (itemCount, _) = loader.Load(dst, tmp);
            Assert.Equal(srcItems.Count, itemCount);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Rolling_SmallWorld_StaysSingleFile()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_rollsingle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, _) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 1;                      // rolling mode
            saver.ShardSizeBytes = 1 * 1024 * 1024;    // 1 MB — way above our data

            var src = MakeWorld();
            Seed(src, charCount: 2, itemCount: 3);
            Assert.True(saver.Save(src, tmp));

            // Single file should exist, no manifest.
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.scp")),
                "small world should land in classic single-file layout");
            Assert.False(File.Exists(Path.Combine(tmp, "sphereworld.manifest")),
                "single-file save should not write a manifest");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void AccountPersistence_Roundtrip(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_acc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var lf = LoggerFactory.Create(b => { });
            var src = new AccountManager(lf);
            src.CreateAccount("alice", "secret1");
            src.CreateAccount("bob", "secret2");
            src.CreateAccount("with space", "secret3"); // normalised to "withspace" on admission

            var alice = src.FindAccount("alice")!;
            alice.SetTag("RANK", "gold");
            alice.LastIp = "127.0.0.1";
            alice.SetCharSlot(0, new Serial(0x1234));
            alice.SetCharSlot(3, new Serial(0xABCD));

            int saved = AccountPersistence.Save(src, tmp, fmt);
            Assert.Equal(3, saved);

            // File extension matches the chosen format.
            string expectedPath = Path.Combine(tmp, "sphereaccu" + SaveIO.ExtensionFor(fmt));
            Assert.True(File.Exists(expectedPath), $"expected {expectedPath}");

            var dst = new AccountManager(lf);
            int loaded = AccountPersistence.Load(dst, tmp);
            Assert.Equal(3, loaded);

            var aliceBack = dst.FindAccount("alice");
            Assert.NotNull(aliceBack);
            Assert.Equal("127.0.0.1", aliceBack!.LastIp);
            Assert.True(aliceBack.TryGetTag("RANK", out var rank));
            Assert.Equal("gold", rank);
            Assert.Equal(new Serial(0x1234), aliceBack.GetCharSlot(0));
            Assert.Equal(new Serial(0xABCD), aliceBack.GetCharSlot(3));

            Assert.NotNull(dst.FindAccount("bob"));
            Assert.NotNull(dst.FindAccount("with space"));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AccountPersistence_PlainPassword_WhenMd5Disabled()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_accplain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var lf = LoggerFactory.Create(b => { });
            File.WriteAllText(Path.Combine(tmp, "sphereaccu.scp"),
                """
                [mortal]
                PLEVEL=7
                PASSWORD=1
                CHARUID0=0204d7
                [EOF]

                """);

            var mgr = new AccountManager(lf) { Md5Passwords = false };
            int loaded = AccountPersistence.Load(mgr, tmp);
            Assert.Equal(1, loaded);

            var acc = mgr.FindAccount("mortal");
            Assert.NotNull(acc);
            Assert.True(acc!.CheckPassword("1"));
            Assert.False(acc.CheckPassword("2"));
            // Md5Passwords=0 is Source-X plaintext storage, so the classic file's
            // value is kept as-is rather than converted on load.
            Assert.Equal("1", acc.PasswordHash);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AccountPersistence_FormatSwitch_RemovesOldFile()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_accsw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var lf = LoggerFactory.Create(b => { });
            var m = new AccountManager(lf);
            m.CreateAccount("alice", "s");

            AccountPersistence.Save(m, tmp, SaveFormat.Text);
            Assert.True(File.Exists(Path.Combine(tmp, "sphereaccu.scp")));

            AccountPersistence.Save(m, tmp, SaveFormat.BinaryGz);
            Assert.True(File.Exists(Path.Combine(tmp, "sphereaccu.sbin.gz")));
            // Stale text file should be cleaned up on format switch.
            Assert.False(File.Exists(Path.Combine(tmp, "sphereaccu.scp")),
                "old format file should be removed after format change");
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Migration_TextToBinary_PreservesData()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_mig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // Save as text.
            var (saverText, loader) = MakeIO();
            saverText.Format = SaveFormat.Text;
            saverText.ShardCount = 1;
            var src = MakeWorld();
            Seed(src, charCount: 4, itemCount: 6);
            Assert.True(saverText.Save(src, tmp));

            // Load back, save as binary + sharded — the migration flow.
            var (saverBinary, _) = MakeIO();
            saverBinary.Format = SaveFormat.BinaryGz;
            saverBinary.ShardCount = 4;
            var round1 = MakeWorld();
            loader.Load(round1, tmp);
            Assert.True(saverBinary.Save(round1, tmp));

            // Verify the manifest appeared and text files are gone.
            Assert.True(File.Exists(Path.Combine(tmp, "sphereworld.manifest")));
            Assert.False(File.Exists(Path.Combine(tmp, "sphereworld.scp")));

            // Third load via binary should still produce the same population.
            var round2 = MakeWorld();
            var (items2, chars2) = loader.Load(round2, tmp);
            Assert.Equal(6, items2);
            Assert.Equal(4, chars2);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void Roundtrip_RestoresEquipmentFromCharEquipLinks(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_equip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var lf = LoggerFactory.Create(b => { });
            var worldPath = Path.Combine(tmp, "sphereworld" + SaveIO.ExtensionFor(fmt));
            using (var writer = SaveIO.OpenWriter(worldPath, fmt))
            {
                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001000");
                writer.WriteProperty("ID", "0EED");
                writer.WriteProperty("NAME", "equipped test sword");
                writer.WriteProperty("P", "1000,1000,0,0");
                writer.EndRecord();

                writer.BeginRecord("WORLDCHAR");
                writer.WriteProperty("SERIAL", "01");
                writer.WriteProperty("NAME", "EquipTester");
                writer.WriteProperty("P", "1000,1001,0,0");
                writer.WriteProperty("BODY", "0190");
                writer.WriteProperty("EQUIP[1]", "040001000");
                writer.EndRecord();
            }

            var loader = new WorldLoader(lf);
            var dst = MakeWorld();
            var (items, chars) = loader.Load(dst, tmp);

            Assert.Equal(1, items);
            Assert.Equal(1, chars);

            var ch = dst.FindChar(new Serial(0x00000001));
            Assert.NotNull(ch);
            var equipped = ch!.GetEquippedItem(Layer.OneHanded);
            Assert.NotNull(equipped);
            Assert.Equal(new Serial(0x40001000), equipped!.Uid);
            Assert.True(equipped.IsEquipped);
            Assert.Equal(Layer.OneHanded, equipped.EquipLayer);
            Assert.Equal(ch.Uid, equipped.ContainedIn);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldLoader_DuplicateSerial_SkipsDuplicateAndContinues()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_dup_serial_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using (var writer = SaveIO.OpenWriter(Path.Combine(tmp, "sphereworld.scp"), SaveFormat.Text))
            {
                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001000");
                writer.WriteProperty("ID", "0EED");
                writer.EndRecord();

                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001000");
                writer.WriteProperty("ID", "0EED");
                writer.EndRecord();
            }

            var loader = new WorldLoader(LoggerFactory.Create(b => { }));
            var dst = MakeWorld();

            var (items, chars) = loader.Load(dst, tmp);

            Assert.Equal(1, items);
            Assert.Equal(0, chars);
            Assert.NotNull(dst.FindItem(new Serial(0x40001000)));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldLoader_DuplicateUuid_SkipsDuplicateAndContinues()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_dup_uuid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            string duplicateUuid = Guid.NewGuid().ToString("D");
            using (var writer = SaveIO.OpenWriter(Path.Combine(tmp, "sphereworld.scp"), SaveFormat.Text))
            {
                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001000");
                writer.WriteProperty("UUID", duplicateUuid);
                writer.WriteProperty("ID", "0EED");
                writer.EndRecord();

                writer.BeginRecord("WORLDITEM");
                writer.WriteProperty("SERIAL", "040001001");
                writer.WriteProperty("UUID", duplicateUuid);
                writer.WriteProperty("ID", "0EED");
                writer.EndRecord();
            }

            var loader = new WorldLoader(LoggerFactory.Create(b => { }));
            var dst = MakeWorld();

            var (items, chars) = loader.Load(dst, tmp);

            Assert.Equal(1, items);
            Assert.Equal(0, chars);
            Assert.NotNull(dst.FindItem(new Serial(0x40001000)));
            Assert.Null(dst.FindItem(new Serial(0x40001001)));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldLoader_SourceStyleFixture_PreservesCompatKeysAcrossRoundtrip()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_source_fixture_{Guid.NewGuid():N}");
        string outDir = Path.Combine(tmp, "out");
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "sphereworld.scp"),
                """
                [WORLDITEM i_gold]
                SERIAL=040001000
                ID=0EED
                P=1500,1600,5,0
                AMOUNT=42
                MORE1=01
                MORE2=02
                TIMER=30
                ATTR=010
                TAG.CUSTOM=kept
                SOURCEONLY=value

                [WORLDITEM i_sword_viking]
                SERIAL=040001001
                ID=013B9
                CONT=01
                LAYER=1
                MOREP=10,11,12,0

                [EOF]

                """);

            File.WriteAllText(Path.Combine(tmp, "spherechars.scp"),
                """
                [WORLDCHAR c_man]
                SERIAL=01
                NAME=FixtureChar
                ACCOUNT=mortal
                P=1501,1600,5,0
                BODY=0190
                STR=50
                DEX=60
                INT=70
                EQUIP[1]=040001001
                MEMORY=040001000,8
                REGION=britain
                EVENTS=e_player
                UNKNOWNCHARKEY=char-value

                [EOF]

                """);

            File.WriteAllText(Path.Combine(tmp, "spheredata.scp"),
                """
                [GLOBALS]
                VAR.TEST=1

                [LIST shardlist]
                ELEM=alpha

                [EOF]

                """);

            var lf = LoggerFactory.Create(b => { });
            var loader = new WorldLoader(lf);
            var world = MakeWorld();
            var accounts = new AccountManager(lf);
            accounts.CreateAccount("mortal", "pw");

            var (items, chars) = loader.Load(world, tmp, accounts);

            Assert.Equal(2, items);
            Assert.Equal(1, chars);

            var gold = world.FindItem(new Serial(0x40001000));
            Assert.NotNull(gold);
            Assert.Equal((ushort)42, gold!.Amount);
            Assert.True(gold.TryGetTag("CUSTOM", out var custom));
            Assert.Equal("kept", custom);
            Assert.True(gold.TryGetTag("SAVE.SOURCEONLY", out var sourceOnly));
            Assert.Equal("value", sourceOnly);

            var ch = world.FindChar(new Serial(0x00000001));
            Assert.NotNull(ch);
            Assert.True(ch!.IsPlayer);
            Assert.True(ch.TryGetTag("ACCOUNT", out var accountTag));
            Assert.Equal("mortal", accountTag);
            Assert.True(ch.TryGetTag("SAVE.REGION", out var region));
            Assert.Equal("britain", region);
            Assert.True(ch.TryGetTag("SAVE.UNKNOWNCHARKEY", out var unknownChar));
            Assert.Equal("char-value", unknownChar);
            Assert.Single(ch.Memories);
            Assert.Equal(new Serial(0x40001000), ch.Memories[0].Link);

            var equipped = ch.GetEquippedItem(Layer.OneHanded);
            Assert.NotNull(equipped);
            Assert.Equal(new Serial(0x40001001), equipped!.Uid);

            var acc = accounts.FindAccount("mortal");
            Assert.NotNull(acc);
            Assert.Equal(ch.Uid, acc!.GetCharSlot(0));

            var saver = new WorldSaver(lf) { Format = SaveFormat.Text, ShardCount = 0 };
            Assert.True(saver.Save(world, outDir));

            var reloaded = MakeWorld();
            var (items2, chars2) = loader.Load(reloaded, outDir);
            Assert.Equal(items, items2);
            Assert.Equal(chars, chars2);
            var gold2 = reloaded.FindItem(new Serial(0x40001000));
            Assert.NotNull(gold2);
            Assert.True(gold2!.TryGetTag("SAVE.SOURCEONLY", out var sourceOnly2));
            Assert.Equal("value", sourceOnly2);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WorldLoader_LoadsItemsEmbeddedInSphereCharsFile()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_spherechars_items_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "spherechars.scp"),
                """
                [WORLDCHAR c_man]
                SERIAL=01
                NAME=PlayerWithBank
                ACCOUNT=mortal
                P=1501,1600,5,0
                BODY=0190

                [WORLDITEM i_bankbox]
                SERIAL=040020190
                ID=0E75
                ATTR=014
                LAYER=29
                CONT=01

                [EOF]

                """);

            var lf = LoggerFactory.Create(b => { });
            var loader = new WorldLoader(lf);
            var world = MakeWorld();
            var accounts = new AccountManager(lf);
            accounts.CreateAccount("mortal", "pw");

            var (items, chars) = loader.Load(world, tmp, accounts);

            Assert.Equal(1, items);
            Assert.Equal(1, chars);

            var ch = world.FindChar(new Serial(0x00000001));
            Assert.NotNull(ch);

            var bank = world.FindItem(new Serial(0x40020190));
            Assert.NotNull(bank);
            Assert.Equal(ch!.Uid, bank!.ContainedIn);
            Assert.Same(bank, ch.GetEquippedItem(Layer.BankBox));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void Roundtrip_PreservesNestedContainersAndBankBox(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_nested_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = fmt;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.Name = "Banker";
            ch.IsPlayer = true;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            var bank = src.CreateItem();
            bank.ItemType = ItemType.Container;
            bank.Name = "bank box";
            ch.Equip(bank, Layer.BankBox);

            var pouch = src.CreateItem();
            pouch.ItemType = ItemType.Container;
            pouch.Name = "nested pouch";
            bank.AddItem(pouch);

            var gold = src.CreateItem();
            gold.BaseId = 0x0EED;
            gold.ItemType = ItemType.Gold;
            gold.Amount = 42;
            pouch.AddItem(gold);

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            var (items, chars) = loader.Load(dst, tmp);

            Assert.Equal(3, items);
            Assert.Equal(1, chars);

            var loadedChar = dst.FindChar(ch.Uid);
            Assert.NotNull(loadedChar);
            var loadedBank = loadedChar!.GetEquippedItem(Layer.BankBox);
            Assert.NotNull(loadedBank);
            Assert.Equal(loadedChar.Uid, loadedBank!.ContainedIn);

            var loadedPouch = dst.FindItem(pouch.Uid);
            Assert.NotNull(loadedPouch);
            Assert.Equal(loadedBank.Uid, loadedPouch!.ContainedIn);
            Assert.Contains(loadedPouch, loadedBank.Contents);

            var loadedGold = dst.FindItem(gold.Uid);
            Assert.NotNull(loadedGold);
            Assert.Equal(loadedPouch.Uid, loadedGold!.ContainedIn);
            Assert.Equal(42, loadedGold.Amount);
            Assert.Contains(loadedGold, loadedPouch.Contents);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Save_UsesCapturedSnapshotForShardedDiskWrite()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_snapshot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = SaveFormat.Text;
            saver.ShardCount = 4;

            var src = MakeWorld();
            var backpack = src.CreateItem();
            backpack.BaseId = 0x0E75;
            src.PlaceItem(backpack, new Point3D(1000, 1000, 0, 0));

            var houseChest = src.CreateItem();
            houseChest.BaseId = 0x0E43;
            src.PlaceItem(houseChest, new Point3D(2000, 2000, 0, 0));

            var gold = src.CreateItem();
            gold.BaseId = 0x0EED;
            gold.Amount = 100;
            backpack.AddItem(gold);

            Assert.True(saver.Save(src, tmp));

            // Mutate the live world after the snapshot was captured. The files
            // already committed from the snapshot must not observe this move.
            houseChest.AddItem(gold);

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var loadedGold = dst.FindItem(gold.Uid);
            Assert.NotNull(loadedGold);
            Assert.Equal(backpack.Uid, loadedGold!.ContainedIn);
            Assert.NotEqual(houseChest.Uid, loadedGold.ContainedIn);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.Binary)]
    public void Roundtrip_PreservesOSkinDistinctFromOBody(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_oskin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = fmt;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var ch = src.CreateCharacter();
            ch.Name = "SkinTest";
            ch.IsPlayer = true;
            ch.OBody = 0x0190;
            ch.OSkin = 0x03EA;
            src.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var loaded = dst.FindChar(ch.Uid);
            Assert.NotNull(loaded);
            Assert.Equal((ushort)0x0190, loaded!.OBody);
            Assert.Equal((ushort)0x03EA, loaded.OSkin);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.Binary)]
    public void Roundtrip_PreservesNpcActionState(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_npc_action_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (saver, loader) = MakeIO();
            saver.Format = fmt;
            saver.ShardCount = 0;

            var src = MakeWorld();
            var target = src.CreateCharacter();
            target.Name = "Target";
            src.PlaceCharacter(target, new Point3D(1001, 1000, 0, 0));

            var npc = src.CreateCharacter();
            npc.Name = "Hunter";
            npc.NpcBrain = NpcBrainType.Monster;
            npc.Action = SkillType.Mining;
            npc.Act = target.Uid;
            npc.ActArg1 = 11;
            npc.ActArg2 = 22;
            npc.ActArg3 = 33;
            npc.ActP = new Point3D(1200, 1300, 5, 0);
            npc.ActPrv = target.Uid;
            npc.ActDiff = 44;
            npc.FightTarget = target.Uid;
            npc.PetAIMode = PetAIMode.Attack;
            npc.FleeStepsCurrent = 3;
            npc.FleeStepsMax = 20;
            src.PlaceCharacter(npc, new Point3D(1000, 1000, 0, 0));

            Assert.True(saver.Save(src, tmp));

            var dst = MakeWorld();
            loader.Load(dst, tmp);

            var loaded = dst.FindChar(npc.Uid);
            Assert.NotNull(loaded);
            Assert.Equal(SkillType.Mining, loaded!.Action);
            Assert.Equal(target.Uid, loaded.Act);
            Assert.Equal(11, loaded.ActArg1);
            Assert.Equal(22, loaded.ActArg2);
            Assert.Equal(33, loaded.ActArg3);
            Assert.Equal(new Point3D(1200, 1300, 5, 0), loaded.ActP);
            Assert.Equal(target.Uid, loaded.ActPrv);
            Assert.Equal(44, loaded.ActDiff);
            Assert.Equal(target.Uid, loaded.FightTarget);
            Assert.Equal(PetAIMode.Attack, loaded.PetAIMode);
            Assert.Equal(3, loaded.FleeStepsCurrent);
            Assert.Equal(20, loaded.FleeStepsMax);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}
