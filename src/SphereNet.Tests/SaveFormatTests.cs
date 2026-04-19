using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
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
            src.CreateAccount("with space", "secret3"); // section name boundary test

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
}
