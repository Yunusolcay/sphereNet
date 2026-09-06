using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;

namespace SphereNet.Persistence.Save;

/// <summary>
/// World saver. Maps to CWorld::Save in Source-X.
/// Writes items to <c>sphereworld</c>, characters to <c>spherechars</c>,
/// server state to <c>spheredata</c>. File format (extension) and optional
/// sharding are driven by <see cref="SaveFormat"/> and <c>SaveShards</c>.
/// Classic single-file <c>.scp</c> output is the default and binary-identical
/// to the pre-refactor behaviour.
/// </summary>
public sealed class WorldSaver
{
    private readonly ILogger<WorldSaver> _logger;
    private int _saveIndex;

    /// <summary>Format new saves will be written in. Runtime-changeable via
    /// the migration command; loader auto-detects so mixing formats in a
    /// snapshot dir is safe.</summary>
    public SaveFormat Format { get; set; } = SaveFormat.Text;

    /// <summary>Sharding mode. 0=always single file, 1=rolling (size-based),
    /// 2-16=fixed parallel hash shards. Clamped to [0,16] on config load.</summary>
    public int ShardCount { get; set; } = 1;

    /// <summary>Write hash shards sequentially on the calling thread instead of
    /// fanning out pool tasks. Set for background saves so the whole write
    /// stays on one low-priority thread (parallel gzip starved small boxes).</summary>
    public bool SequentialShardWrites { get; set; }

    /// <summary>Rolling threshold (bytes). Only consulted when <see cref="ShardCount"/>==1.</summary>
    public long ShardSizeBytes { get; set; }

    /// <summary>Number of per-file backup generations to keep beside the live save.</summary>
    public int BackupLevels { get; set; }

    public Func<ResourceId, string?>? ResolveResourceName { get; set; }

    /// <summary>Resolves an item BaseId (graphic) to its script defname
    /// (e.g. 0x0E75 → "i_backpack"). Used for Source-X compatible section headers.</summary>
    public Func<ushort, string?>? ResolveItemDefName { get; set; }

    /// <summary>Resolves a character CHARDEFINDEX to its script defname
    /// (e.g. hash → "c_man"). Used for Source-X compatible section headers.</summary>
    public Func<int, string?>? ResolveCharDefName { get; set; }

    /// <summary>Returns active spell-effect records for a character as
    /// remaining-time save strings. Wired by the host because SpellEngine owns
    /// the runtime effect list.</summary>
    public Func<Character, long, IEnumerable<string>>? GetSpellEffectRecords { get; set; }

    public WorldSaver(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<WorldSaver>();
    }

    public readonly record struct WorldExportScope(Point3D Center, int Distance, int Flags)
    {
        public bool IncludeItems => (Flags & 1) != 0;
        public bool IncludeChars => (Flags & 2) != 0;

        public bool Contains(Point3D point)
        {
            int distance = Math.Max(0, Distance);
            return point.Map == Center.Map &&
                   Math.Abs((int)point.X - Center.X) <= distance &&
                   Math.Abs((int)point.Y - Center.Y) <= distance;
        }
    }

    public bool Save(GameWorld world, string savePath)
        => WritePrepared(Prepare(world), savePath);

    /// <summary>Fully captured save state: immutable per-object records plus the
    /// pre-rendered server-data file. Holds NO references into the live world,
    /// so <see cref="WritePrepared"/> may run on a background thread while the
    /// main loop keeps mutating the world (E2 / SAVEBACKGROUND).</summary>
    public sealed class PreparedWorldSave
    {
        internal PreparedWorldSave(WorldSaveSnapshot snapshot, string serverData, int saveIndex)
        {
            Snapshot = snapshot;
            ServerData = serverData;
            SaveIndex = saveIndex;
        }

        internal WorldSaveSnapshot Snapshot { get; }
        internal string ServerData { get; }
        public int SaveIndex { get; }
    }

    /// <summary>MAIN-THREAD phase: walk the live world once and capture every
    /// record. This is the only part of a save that reads world state.</summary>
    public PreparedWorldSave Prepare(GameWorld world)
    {
        _saveIndex++;
        var snapshot = CaptureSnapshot(world);
        string serverData = BuildServerData(world);
        return new PreparedWorldSave(snapshot, serverData, _saveIndex);
    }

    /// <summary>WRITE phase: shard, encode and commit the captured records.
    /// Touches only the prepared data — safe on any thread.</summary>
    public bool WritePrepared(PreparedWorldSave prepared, string savePath)
    {
        _logger.LogInformation("World save #{Index} starting (format={Format}, shards={Shards})...",
            prepared.SaveIndex, Format, ShardCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(savePath);

            // Phase 1 — write EVERY logical file (sphereworld, spherechars,
            // spheredata) to its .tmp sibling; touch no live file yet. A crash here
            // leaves the previous generation fully intact — only stray .tmp files,
            // which the catch below and the next save clean up.
            var itemPending = WriteShardsToTmp(prepared.Snapshot.Items, savePath, "sphereworld", isItems: true, prepared.SaveIndex, out int itemCount);
            var charPending = WriteShardsToTmp(prepared.Snapshot.Characters, savePath, "spherechars", isItems: false, prepared.SaveIndex, out int charCount);
            WriteServerDataToTmp(savePath, prepared.ServerData);

            // Phase 2 — commit all three back-to-back. Previously each logical file
            // was committed before the next was even written, so a crash mid-save
            // could publish a new-generation sphereworld next to an old-generation
            // spherechars (a mixed, internally inconsistent world that the loader
            // silently degrades into dangling CONT/EQUIP data loss). Deferring every
            // rename until all writes finish shrinks that window from the whole
            // multi-second write of the later files to just these adjacent renames.
            CommitShards(itemPending, savePath);
            CommitShards(charPending, savePath);
            CommitServerData(savePath);

            _logger.LogInformation("World save #{Index} complete: {Items} items, {Chars} chars in {Elapsed}s",
                prepared.SaveIndex, itemCount, charCount, sw.Elapsed.TotalSeconds.ToString("F1"));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "World save #{Index} FAILED", prepared.SaveIndex);
            CleanupTmpFiles(savePath);
            return false;
        }
    }

    /// <summary>Write a classic text export atomically: build the whole file in a
    /// sibling <c>.tmp</c>, verify it re-reads with the expected record count, then
    /// move it over the final path. A crash mid-write leaves the previous final
    /// intact (only a stray <c>.tmp</c>), and a truncated/garbled write is rejected
    /// before it can replace the final. This matters because destructive restore
    /// uses these files as its rollback snapshot — a corrupt snapshot must never
    /// silently overwrite a good one, and a failed export must throw so the caller
    /// aborts before deleting live objects.</summary>
    private static int WriteTextExportAtomic(string path, Func<TextSaveWriter, int> writeBody)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string tmp = path + ".tmp";
        try
        {
            int expected;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new TextSaveWriter(fs))
            {
                expected = writeBody(writer);
            } // writer/fs disposed → fully flushed and closed

            int actual = CountTextRecords(tmp);
            if (actual != expected)
                throw new InvalidDataException(
                    $"Export validation failed for '{Path.GetFileName(path)}': wrote {expected} records but re-read {actual}");

            File.Move(tmp, path, overwrite: true);
            return expected;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    private static int CountTextRecords(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new TextSaveReader(fs);
        int n = 0;
        while (reader.NextRecord(out _))
        {
            while (reader.NextProperty(out _, out _)) { }
            n++;
        }
        return n;
    }

    /// <summary>Export a single object as a classic text <c>.scp</c> record.
    /// Source-X world-ops use the script save format, so this intentionally
    /// writes text regardless of the server's normal binary/gzip save mode.</summary>
    public int ExportObject(ObjBase obj, string path)
    {
        long now = Environment.TickCount64;
        return WriteTextExportAtomic(path, writer =>
        {
            writer.WriteHeaderComment($"SphereNet object export at {DateTime.UtcNow:u}");
            if (obj is Item item && !item.IsDeleted) { WriteItem(writer, item, now); return 1; }
            if (obj is Character ch && !ch.IsDeleted) { WriteChar(writer, ch, now); return 1; }
            return 0;
        });
    }

    /// <summary>Export a bounded object set to one classic text <c>.scp</c>
    /// file. Used by destructive world-ops as a rollback snapshot.</summary>
    public int ExportObjects(IEnumerable<ObjBase> objects, string path)
    {
        long now = Environment.TickCount64;
        return WriteTextExportAtomic(path, writer =>
        {
            writer.WriteHeaderComment($"SphereNet object set export at {DateTime.UtcNow:u}");
            int count = 0;
            foreach (var obj in objects)
            {
                if (obj is Item item && !item.IsDeleted) { WriteItem(writer, item, now); count++; }
                else if (obj is Character ch && !ch.IsDeleted) { WriteChar(writer, ch, now); count++; }
            }
            return count;
        });
    }

    /// <summary>Export the current dynamic world into one mixed text
    /// <c>.scp</c> file. Uses the same snapshot/filtering as normal world save
    /// (for example, virtual vendor stock is skipped).</summary>
    public int ExportWorld(GameWorld world, string path, WorldExportScope? scope = null)
    {
        var snapshot = CaptureSnapshot(world, scope);
        return WriteTextExportAtomic(path, writer =>
        {
            writer.WriteHeaderComment($"SphereNet world export at {DateTime.UtcNow:u}");
            foreach (var record in snapshot.Items)
                WriteRecord(writer, record);
            foreach (var record in snapshot.Characters)
                WriteRecord(writer, record);
            return snapshot.Items.Count + snapshot.Characters.Count;
        });
    }

    /// <summary>Export static world items into one text <c>.scp</c> file.
    /// This is the bounded first slice of Source-X <c>SAVESTATICS</c>: dynamic
    /// items/chars stay in the normal world export while ATTR_STATIC items are
    /// written here for inspection or later import.</summary>
    public int ExportStatics(GameWorld world, string path, WorldExportScope? scope = null)
    {
        long now = Environment.TickCount64;
        return WriteTextExportAtomic(path, writer =>
        {
            writer.WriteHeaderComment($"SphereNet statics export at {DateTime.UtcNow:u}");
            int count = 0;
            foreach (var obj in world.GetAllObjects())
            {
                if (obj is not Item item || item.IsDeleted || !item.IsAttr(Core.Enums.ObjAttributes.Static))
                    continue;
                if (scope is { } s && (!s.IncludeItems || !s.Contains(item.Position)))
                    continue;
                WriteItem(writer, item, now);
                count++;
            }
            return count;
        });
    }

    /// <summary>Write one logical save ("sphereworld" or "spherechars"). Three
    /// code paths, selected by <see cref="ShardCount"/>:
    /// <list type="bullet">
    /// <item><c>0</c> → classic single file, no rolling, no manifest.</item>
    /// <item><c>1</c> → Sphere-style rolling: one file until it fills, then
    /// spill to the next. Small worlds stay single-file + no manifest.</item>
    /// <item><c>2-16</c> → fixed UID-hash parallel shards, always manifest.</item>
    /// </list></summary>
    private WorldSaveSnapshot CaptureSnapshot(GameWorld world, WorldExportScope? scope = null)
    {
        var allObjects = world.GetAllObjects().ToArray();
        var byUid = scope.HasValue
            ? allObjects.ToDictionary(obj => obj.Uid.Value)
            : null;
        var items = new List<SaveRecord>();
        var chars = new List<SaveRecord>();
        long now = Environment.TickCount64;

        // Vendor SELL stock is virtual: the container equipped at LAYER 26 and
        // every item inside it are rebuilt on demand from the SELL template when a
        // player opens the vendor (PopulateVendorStock). Persisting them bloats
        // every save with transient stock items (~20 per vendor), so collect that
        // container's UID and skip it and its contents below.
        //
        // LAYER 27 (VendorExtra) is NOT virtual and must persist: it is where a
        // player vendor keeps the goods it bought from players (Source-X
        // pContExtra in Event_VendorSell). Excluding it too meant anything sold to
        // a player vendor would vanish on the next save.
        var vendorStock = new HashSet<uint>();
        foreach (var obj in allObjects)
        {
            if (obj is Item it && !it.IsDeleted &&
                it.EquipLayer == Core.Enums.Layer.VendorStock)
                vendorStock.Add(it.Uid.Value);
        }

        // Parent-serial map so the vendor-stock filter can walk an item's FULL
        // ancestor chain. Skipping only a DIRECT child of a stock container left
        // items in a sub-bag inside vendor stock persisted with a CONT pointing at
        // a never-persisted parent — on load they dropped to the ground at a stale
        // position (world clutter). Read-only during the parallel capture below.
        var parentOf = new Dictionary<uint, uint>(allObjects.Length);
        foreach (var obj in allObjects)
        {
            if (obj is Item pit && !pit.IsDeleted && pit.ContainedIn.IsValid)
                parentOf[pit.Uid.Value] = pit.ContainedIn.Value;
        }

        // Capture in parallel: this is a pure read of live objects (the main
        // thread is inside the save, nothing mutates), and per-object record
        // building (property formatting + string alloc) dominated the sync-save
        // stall (~0.45s of a 0.75s save on a 32K world). Slot arrays keep the
        // original object order so the output stays deterministic.
        var itemSlots = new SaveRecord?[allObjects.Length];
        var charSlots = new SaveRecord?[allObjects.Length];
        System.Threading.Tasks.Parallel.For(0, allObjects.Length, i =>
        {
            var obj = allObjects[i];
            if (obj is Item item)
            {
                if (item.IsDeleted || item.IsAttr(Core.Enums.ObjAttributes.Static))
                    return;
                if (IsInsideVendorStock(item.Uid.Value, vendorStock, parentOf))
                    return; // virtual vendor stock (or nested inside it) — never persisted
                if (!ShouldExportItem(item, scope, byUid))
                    return;
                itemSlots[i] = CaptureItem(item, now);
            }
            else if (obj is Character ch)
            {
                if (ch.IsDeleted)
                    return;
                if (!ShouldExportChar(ch, scope))
                    return;
                charSlots[i] = CaptureChar(ch, now);
            }
        });
        foreach (var rec in itemSlots)
            if (rec != null) items.Add(rec);
        foreach (var rec in charSlots)
            if (rec != null) chars.Add(rec);

        return new WorldSaveSnapshot(items, chars);
    }

    private static bool ShouldExportChar(Character ch, WorldExportScope? scope)
    {
        if (!scope.HasValue)
            return true;

        var s = scope.Value;
        return s.IncludeChars && s.Contains(ch.Position);
    }

    /// <summary>True if the item is a vendor-stock container or nested at ANY
    /// depth inside one — such objects are virtual (rebuilt from the SELL template
    /// on vendor open) and must not be persisted. Walks the ancestor chain via
    /// <paramref name="parentOf"/> with a depth cap so a corrupt/cyclic CONT chain
    /// can't loop.</summary>
    private static bool IsInsideVendorStock(uint uid, HashSet<uint> vendorStock, Dictionary<uint, uint> parentOf)
    {
        if (vendorStock.Contains(uid)) return true;
        uint cur = uid;
        for (int guard = 0; guard < 64; guard++)
        {
            if (!parentOf.TryGetValue(cur, out uint parent))
                return false; // reached a grounded/equipped root — not vendor stock
            if (vendorStock.Contains(parent))
                return true;
            cur = parent;
        }
        return false; // depth cap (corrupt/cyclic chain) — persist rather than lose it
    }

    private static bool ShouldExportItem(Item item, WorldExportScope? scope,
        IReadOnlyDictionary<uint, ObjBase>? byUid)
    {
        if (!scope.HasValue)
            return true;

        var s = scope.Value;
        if (!s.IncludeItems)
            return false;

        if (!item.ContainedIn.IsValid)
            return s.Contains(item.Position);

        if (byUid == null)
            return false;

        Serial parentSerial = item.ContainedIn;
        for (int guard = 0; guard < 64 && parentSerial.IsValid; guard++)
        {
            if (!byUid.TryGetValue(parentSerial.Value, out var parent))
                return false;

            if (parent is Character ch)
                return s.IncludeChars && s.Contains(ch.Position);

            if (parent is not Item parentItem || parentItem.IsDeleted ||
                parentItem.IsAttr(Core.Enums.ObjAttributes.Static))
                return false;

            if (!parentItem.ContainedIn.IsValid)
                return s.Contains(parentItem.Position);

            parentSerial = parentItem.ContainedIn;
        }

        return false;
    }

    /// <summary>What <see cref="CommitShards"/> needs to atomically publish a
    /// logical save whose shards (and manifest) have already been written to their
    /// <c>.tmp</c> siblings. Produced by <see cref="WriteShardsToTmp"/>.</summary>
    private readonly record struct PendingShardCommit(
        string BaseName,
        List<string> OutputFiles,
        bool WriteManifest,
        string ManifestPath);

    /// <summary>Phase 1 of a logical save: write every shard (and, when there is
    /// more than one, the manifest) to its <c>.tmp</c> sibling WITHOUT touching any
    /// live file. Returns the record count via <paramref name="totalCount"/> and the
    /// handle <see cref="CommitShards"/> uses to publish the result. Splitting write
    /// from commit lets <see cref="WritePrepared"/> stage sphereworld, spherechars
    /// and spheredata to .tmp before committing any of them, so a crash between
    /// writes can never leave a new-generation file beside an old-generation one.</summary>
    private PendingShardCommit WriteShardsToTmp(IReadOnlyList<SaveRecord> records, string savePath, string baseName, bool isItems, int saveIndex, out int totalCount)
    {
        int shards = Math.Clamp(ShardCount, 0, 16);
        string ext = SaveIO.ExtensionFor(Format);
        long sizeLimit = Math.Max(0, ShardSizeBytes);

        List<string> outputFiles;

        if (shards == 0)
        {
            string fileName = baseName + ext;
            string tmp = Path.Combine(savePath, fileName + ".tmp");
            totalCount = WriteOneShard(records, tmp, isItems, shardIndex: 0, shardCount: 1, saveIndex);
            outputFiles = new List<string> { fileName };
        }
        else if (shards == 1)
        {
            outputFiles = new List<string>();
            totalCount = WriteRollingShards(records, savePath, baseName, ext, sizeLimit, isItems, outputFiles, saveIndex);

            // Small worlds that never hit the rolling threshold get promoted
            // to the classic {base}{ext} name so there's no lone .0 suffix
            // and no manifest — identical on-disk layout to SaveShards=0.
            if (outputFiles.Count == 1)
            {
                string oldName = outputFiles[0];
                string newName = baseName + ext;
                if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                {
                    string oldTmp = Path.Combine(savePath, oldName + ".tmp");
                    string newTmp = Path.Combine(savePath, newName + ".tmp");
                    if (File.Exists(newTmp)) File.Delete(newTmp);
                    File.Move(oldTmp, newTmp);
                    outputFiles[0] = newName;
                }
            }
        }
        else
        {
            outputFiles = new List<string>(shards);
            var counts = new int[shards];
            for (int i = 0; i < shards; i++)
                outputFiles.Add($"{baseName}.{i}{ext}");

            var shardBuckets = PartitionRecordsByShard(records, shards);
            if (SequentialShardWrites)
            {
                // Background-save mode: stay on the single low-priority writer
                // thread instead of fanning out normal-priority pool tasks —
                // parallel gzip across shards saturated small VDS boxes and
                // starved the main loop (yield/net_in stalls) for the whole
                // write window.
                for (int i = 0; i < shards; i++)
                {
                    string tmp = Path.Combine(savePath, outputFiles[i] + ".tmp");
                    counts[i] = WriteOneShard(shardBuckets[i], tmp, isItems, i, shards, saveIndex);
                }
            }
            else
            {
                var tasks = new Task[shards];
                for (int i = 0; i < shards; i++)
                {
                    int shardIdx = i;
                    string tmp = Path.Combine(savePath, outputFiles[shardIdx] + ".tmp");
                    tasks[i] = Task.Run(() =>
                        counts[shardIdx] = WriteOneShard(shardBuckets[shardIdx], tmp, isItems, shardIdx, shards, saveIndex));
                }
                Task.WaitAll(tasks);
            }

            totalCount = 0;
            foreach (int c in counts) totalCount += c;
        }

        // Manifest (written only when >1 file exists — small worlds stay a single
        // {base}{ext} file, matching classic Sphere layout) goes to .tmp here in the
        // write phase; CommitShards publishes it alongside the shards.
        string manifestPath = ShardManifest.PathFor(savePath, baseName);
        bool writeManifest = outputFiles.Count > 1;
        if (writeManifest)
        {
            var manifest = new ShardManifest
            {
                Format = Format,
                ShardCount = outputFiles.Count,
                Files = outputFiles,
            };
            manifest.Save(manifestPath + ".tmp");
        }

        return new PendingShardCommit(baseName, outputFiles, writeManifest, manifestPath);
    }

    /// <summary>Phase 2 of a logical save: publish shards already staged to .tmp by
    /// rotating each live file to .bak and renaming its .tmp into place, committing
    /// (or clearing) the manifest, then pruning stale siblings. Runs only after
    /// EVERY logical file of the save has been written, so the window in which the
    /// on-disk set is internally inconsistent is just these adjacent renames.</summary>
    private void CommitShards(PendingShardCommit pending, string savePath)
    {
        foreach (string name in pending.OutputFiles)
            CommitFile(Path.Combine(savePath, name));

        if (pending.WriteManifest)
            CommitFile(pending.ManifestPath);
        else if (File.Exists(pending.ManifestPath))
            File.Delete(pending.ManifestPath);

        RemoveStaleSiblings(savePath, pending.BaseName, pending.OutputFiles);
    }

    private static List<SaveRecord>[] PartitionRecordsByShard(IEnumerable<SaveRecord> records, int shards)
    {
        var buckets = new List<SaveRecord>[shards];
        for (int i = 0; i < shards; i++)
            buckets[i] = [];

        foreach (var record in records)
            buckets[ShardManifest.ShardIndexForUid(record.Uid, shards)].Add(record);

        return buckets;
    }

    /// <summary>Sphere-style rolling writer: open <c>{base}.0{ext}</c>, write
    /// records, and when the on-disk file crosses <paramref name="sizeLimit"/>
    /// close it at the next record boundary and open <c>{base}.1{ext}</c>.
    /// The actual file list is appended to <paramref name="outputFiles"/>.
    /// Size is polled on the raw FileStream (compressed bytes for gzip) which
    /// may lag by up to one gzip block — close enough for rolling.</summary>
    private int WriteRollingShards(IEnumerable<SaveRecord> records, string savePath, string baseName,
        string ext, long sizeLimit, bool isItems, List<string> outputFiles, int saveIndex)
    {
        int count = 0;
        int fileIdx = 0;
        ISaveWriter? writer = null;
        FileStream? raw = null;

        void OpenNext()
        {
            string name = $"{baseName}.{fileIdx}{ext}";
            outputFiles.Add(name);
            string tmp = Path.Combine(savePath, name + ".tmp");
            writer = SaveIO.OpenWriter(tmp, Format, out raw);
            writer.WriteHeaderComment($"SphereNet {(isItems ? "World Items" : "World Characters")} Save");
            writer.WriteHeaderComment($"Save #{saveIndex} at {DateTime.UtcNow:u}");
            writer.WriteHeaderComment($"Rolling segment {fileIdx}");
            WriteSaveIdRecord(writer!, saveIndex);
        }

        OpenNext();

        try
        {
            foreach (var record in records)
            {
                // A record failure fails the whole rolling write (see WriteOneShard):
                // it propagates to WritePrepared, which deletes the .tmp files and
                // returns false without committing.
                WriteRecord(writer!, record);
                count++;

                if (sizeLimit > 0 && writer != null && writer.WrittenBytes >= sizeLimit)
                {
                    writer.Dispose();
                    writer = null; raw = null;
                    fileIdx++;
                    OpenNext();
                }
            }
        }
        finally
        {
            // Release the file handle on the current segment even on failure, so
            // WritePrepared's .tmp cleanup can delete it.
            writer?.Dispose();
        }

        return count;
    }

    private int WriteOneShard(IEnumerable<SaveRecord> records, string tmpPath, bool isItems,
        int shardIndex, int shardCount, int saveIndex)
    {
        using var writer = SaveIO.OpenWriter(tmpPath, Format);
        writer.WriteHeaderComment($"SphereNet {(isItems ? "World Items" : "World Characters")} Save");
        writer.WriteHeaderComment($"Save #{saveIndex} at {DateTime.UtcNow:u}");
        if (shardCount > 1)
            writer.WriteHeaderComment($"Shard {shardIndex}/{shardCount}");
        WriteSaveIdRecord(writer, saveIndex);

        int count = 0;

        foreach (var record in records)
        {
            if (shardCount > 1 && ShardManifest.ShardIndexForUid(record.Uid, shardCount) != shardIndex)
                continue;
            // Do NOT swallow per-record errors. A record that fails to encode/write
            // (oversized field, or a stream/IO error) must fail the whole shard so
            // the caller aborts the commit and keeps the previous good save —
            // silently dropping the record loses that entity from the world, and
            // continuing after a stream error risks a misaligned shard. The
            // exception propagates to WritePrepared, which cleans up the .tmp files
            // and returns false without committing.
            WriteRecord(writer, record);
            count++;
        }

        return count;
    }

    private SaveRecord CaptureItem(Item item, long now)
    {
        using var writer = new SnapshotSaveWriter();
        WriteItem(writer, item, now);
        return writer.ToRecord(item.Uid.Value);
    }

    private SaveRecord CaptureChar(Character ch, long now)
    {
        using var writer = new SnapshotSaveWriter();
        WriteChar(writer, ch, now);
        return writer.ToRecord(ch.Uid.Value);
    }

    private static void WriteRecord(ISaveWriter writer, SaveRecord record)
    {
        writer.BeginRecord(record.Section);
        foreach (var (key, value) in record.Properties)
            writer.WriteProperty(key, value);
        writer.EndRecord();
    }

    // Cross-file generation stamp. Every item/char shard opens with a [SAVEID]
    // record carrying this save's index; spheredata already records the same value
    // as SAVECOUNT. The loader compares the three, so a torn multi-file commit — a
    // crash between the back-to-back renames leaving a new sphereworld beside an old
    // spherechars — is detected (their SAVEIDs disagree) and the mixed generation is
    // rejected in favour of the last consistent one. Legacy saves have no stamp; the
    // loader skips the check when any file lacks one.
    private static void WriteSaveIdRecord(ISaveWriter writer, int saveIndex)
    {
        writer.BeginRecord(SaveIO.SaveIdSection);
        writer.WriteProperty(SaveIO.SaveIdProperty, saveIndex.ToString());
        writer.EndRecord();
    }

    /// <summary>Persist an object's pending TIMERF/TIMERFMS entries as remaining-time
    /// (not the absolute due tick, which resets on restart) so a delayed function/verb
    /// survives save-load — Source-X keeps object timers across a world save.</summary>
    private static void WriteTimerF(ISaveWriter w, SphereNet.Game.Objects.ObjBase obj, long now)
    {
        foreach (var t in obj.TimerFEntries)
        {
            long remainingMs = t.DueTickMs - now;
            if (remainingMs < 0) remainingMs = 0;
            // remainingMs | functionName | args — functionName holds no '|'; args may,
            // so the loader splits on the first two delimiters only.
            w.WriteProperty("TIMERF", $"{remainingMs}|{t.FunctionName}|{t.Args}");
        }
    }

    private void WriteItem(ISaveWriter w, Item item, long now)
    {
        EngineTags.StripEphemeral(item);

        string? defname = ResolveItemDefName?.Invoke(item.BaseId);
        w.BeginRecord(defname != null ? $"WORLDITEM {defname}" : "WORLDITEM");
        w.WriteProperty("SERIAL", $"0{item.Uid.Value:X8}");
        w.WriteProperty("UUID", item.Uuid.ToString("D"));
        if (defname == null)
            w.WriteProperty("ID", $"0{item.BaseId:X}");
        w.WriteProperty("NAME", item.Name);
        w.WriteProperty("P", item.Position.ToString());
        if (item.Hue.Value != 0) w.WriteProperty("COLOR", $"0{item.Hue.Value:x}");
        if (item.Amount > 1) w.WriteProperty("AMOUNT", item.Amount.ToString());
        if (item.WeightOverride is int baseWeight) w.WriteProperty("BASEWEIGHT", baseWeight.ToString());
        if (item.MaxAmountOverride is int maxAmt) w.WriteProperty("MAXAMOUNT", maxAmt.ToString());
        if (item.Direction != 0) w.WriteProperty("DIR", item.Direction.ToString());
        if ((uint)item.Attributes != 0) w.WriteProperty("ATTR", $"0{(uint)item.Attributes:x}");
        if (item.DispIdOverride != 0) w.WriteProperty("DISPID", $"0{item.DispIdOverride:x}");

        // Persist the instance TYPE for structure items (Multi / MultiCustom / Ship).
        // Their BaseId is a raw multi index with no ITEMDEF, so the loader's
        // def-materialization cannot recover the type — without this a reloaded
        // house/ship becomes t_normal and HousingEngine/ShipEngine.DeserializeFromWorld
        // never re-registers it (the structure is lost on restart). ParseItemType reads
        // the numeric ItemType back on load.
        if (item.ItemType is SphereNet.Core.Enums.ItemType.Multi
            or SphereNet.Core.Enums.ItemType.MultiCustom
            or SphereNet.Core.Enums.ItemType.Ship)
            w.WriteProperty("TYPE", ((ushort)item.ItemType).ToString());

        if (item.More1 != 0) w.WriteProperty("MORE1", $"0{item.More1:X}");
        if (item.More2 != 0) w.WriteProperty("MORE2", $"0{item.More2:X}");
        if (item.MoreB != 0) w.WriteProperty("MOREB", $"0{item.MoreB:X}");
        if (item.MoreP != Point3D.Zero) w.WriteProperty("MOREP", item.MoreP.ToString());
        if (item.Crafter.IsValid) w.WriteProperty("CRAFTER", $"0{item.Crafter.Value:X}");
        if (item.UsesRemaining != 0) w.WriteProperty("USESREMAINING", item.UsesRemaining.ToString());
        if (item.Link.IsValid) w.WriteProperty("LINK", $"0{item.Link.Value:X}");
        if (item.Price != 0) w.WriteProperty("PRICE", item.Price.ToString());
        if (item.Quality != 0) w.WriteProperty("QUALITY", item.Quality.ToString()); // Source-X persists only non-zero quality

        item.MigrateHitsFromTags();
        if (item.HitsCur > 0) w.WriteProperty("HITS", item.HitsCur.ToString());
        if (item.HitsMax > 0) w.WriteProperty("MAXHITS", item.HitsMax.ToString());

        if (item.TData1 != 0) w.WriteProperty("TDATA1", item.TData1.ToString());
        if (item.TData2 != 0) w.WriteProperty("TDATA2", item.TData2.ToString());
        if (item.TData3 != 0) w.WriteProperty("TDATA3", item.TData3.ToString());
        if (item.TData4 != 0) w.WriteProperty("TDATA4", item.TData4.ToString());

        if (item.ContainedIn.IsValid) w.WriteProperty("CONT", $"0{item.ContainedIn.Value:X8}");
        if (item.EquipLayer != 0) w.WriteProperty("LAYER", ((byte)item.EquipLayer).ToString());
        if (item.ContainerGridIndex != 0) w.WriteProperty("CONTGRID", item.ContainerGridIndex.ToString());

        long timeout = item.Timeout;
        if (timeout > 0)
        {
            long remainingMs = timeout - now;
            if (remainingMs > 0)
                w.WriteProperty("TIMERMS", remainingMs.ToString());
        }

        if (item.DecayTime > 0)
        {
            long remainingSec = (item.DecayTime - now) / 1000;
            if (remainingSec > 0)
                w.WriteProperty("DECAY", remainingSec.ToString());
        }

        WriteTimerF(w, item, now);

        foreach (var r in item.Events)
        {
            if (r.Type != SphereNet.Core.Enums.ResType.Events || r.Index == 0) continue;
            string? name = ResolveResourceName?.Invoke(r);
            if (!string.IsNullOrWhiteSpace(name))
                w.WriteProperty("EVENTS", name!);
        }

        // Spawn items: write ADDOBJ from live SpawnComponent state, not stale tag
        if (item.SpawnChar != null)
        {
            item.SpawnChar.CleanupDead();
            foreach (var uid in item.SpawnChar.SpawnedUids)
                w.WriteProperty("ADDOBJ", $"0{uid.Value:x}");
        }

        item.MigrateRuneFromTags();

        foreach (var (key, val) in item.Tags.GetAll())
        {
            string upper = key.ToUpperInvariant();
            if (upper == "ADDOBJ")
                continue; // already written from SpawnComponent above
            if (EngineTags.IsEphemeral(key))
                continue;
            if (upper is "HITS" or "HITSMAX" or "MAXHITS")
                continue;
            if (upper.StartsWith("RUNE_", StringComparison.Ordinal))
                continue;
            if (upper is "SPAWNID" or "TIMELO" or "TIMEHI" or "MAXDIST"
                or "REGION.FLAGS" or "REGION.EVENTS" or "OWNER" or "HOUSETYPE"
                or "LOCKDOWNSPERCENT" or "BASEVENDORS" or "BASESTORAGE")
            {
                w.WriteProperty(upper, val);
            }
            else if (upper is "ADDCOMP" or "SECURE" or "LOCKITEM")
            {
                foreach (var entry in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    w.WriteProperty(upper, entry);
            }
            else
            {
                w.WriteProperty("TAG." + key, val);
            }
        }

        w.EndRecord();
    }

    private void WriteChar(ISaveWriter w, Character ch, long now)
    {
        EngineTags.StripEphemeral(ch);

        string? defname = ch.CharDefIndex != 0
            ? ResolveCharDefName?.Invoke(ch.CharDefIndex)
            : null;
        if (string.IsNullOrEmpty(defname) && ch.TryGetTag("CHARDEF", out string? tagDef) && !string.IsNullOrEmpty(tagDef))
            defname = tagDef;
        w.BeginRecord(defname != null ? $"WORLDCHAR {defname}" : "WORLDCHAR");
        w.WriteProperty("SERIAL", $"0{ch.Uid.Value:X8}");
        w.WriteProperty("UUID", ch.Uuid.ToString("D"));
        w.WriteProperty("NAME", ch.Name);
        w.WriteProperty("P", ch.Position.ToString());
        w.WriteProperty("BODY", $"0{ch.BodyId:X}");
        // Full-width chardef hash (24-bit). Without this, NPCs reload with
        // CharDefIndex=0 → trigger / brain lookups fall back to BaseId
        // (the truncated body id) and re-introduce the c_alchemist→c_man
        // brain hijack on every restart.
        if (ch.CharDefIndex != 0 && ch.CharDefIndex != ch.BaseId)
            w.WriteProperty("CHARDEFINDEX", $"0{ch.CharDefIndex:X}");
        if (ch.Hue.Value != 0) w.WriteProperty("COLOR", $"0{ch.Hue.Value:x}");
        w.WriteProperty("DIR", ((byte)ch.Direction).ToString());
        w.WriteProperty("STR", ch.Str.ToString());
        w.WriteProperty("DEX", ch.Dex.ToString());
        w.WriteProperty("INT", ch.Int.ToString());
        w.WriteProperty("HITS", ch.Hits.ToString());
        w.WriteProperty("MANA", ch.Mana.ToString());
        w.WriteProperty("STAM", ch.Stam.ToString());
        // Persist the BASE max pools, not the effective getters — the equipped-suit
        // BONUSHITSMAX/MANAMAX/STAMMAX contribution is derived on read and must never
        // be written back as base (it would inflate permanently across save cycles).
        w.WriteProperty("MAXHITS", ch.BaseMaxHits.ToString());
        w.WriteProperty("MAXMANA", ch.BaseMaxMana.ToString());
        w.WriteProperty("MAXSTAM", ch.BaseMaxStam.ToString());
        // Source-X CREATE key: character age at save time, in tenths of a
        // second (CChar::r_Write). Players only — feeds the MinCharDeleteTime
        // delete gate; NPCs don't need it and it would bloat 50K+ records.
        if (ch.IsPlayer && ch.CreatedUtcSeconds > 0)
            w.WriteProperty("CREATE",
                (Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ch.CreatedUtcSeconds) * 10).ToString());
        w.WriteProperty("OFAME", ch.Fame.ToString());
        w.WriteProperty("OKARMA", ch.Karma.ToString());
        if (ch.Food != 0) w.WriteProperty("OFOOD", ch.Food.ToString());
        var maxFoodTag = ch.Tags.Get("MAXFOOD");
        if (!string.IsNullOrEmpty(maxFoodTag)) w.WriteProperty("MAXFOOD", maxFoodTag);
        if (ch.ResPhysical != 0) w.WriteProperty("RESPHYSICAL", ch.ResPhysical.ToString());
        if (ch.ResFire != 0) w.WriteProperty("RESFIRE", ch.ResFire.ToString());
        if (ch.ResCold != 0) w.WriteProperty("RESCOLD", ch.ResCold.ToString());
        if (ch.ResPoison != 0) w.WriteProperty("RESPOISON", ch.ResPoison.ToString());
        if (ch.ResEnergy != 0) w.WriteProperty("RESENERGY", ch.ResEnergy.ToString());
        if (ch.Kills != 0) w.WriteProperty("KILLS", ch.Kills.ToString());
        if (ch.CriminalTimerRemainingSeconds > 0)
            w.WriteProperty("CRIMINALTIMER", ch.CriminalTimerRemainingSeconds.ToString());
        if (ch.MurderDecayRemainingSeconds > 0)
            w.WriteProperty("MURDERDECAY", ch.MurderDecayRemainingSeconds.ToString());
        if (!string.IsNullOrEmpty(ch.Title)) w.WriteProperty("TITLE", ch.Title);
        w.WriteProperty("FLAGS", $"0{(uint)ch.StatFlags:x}");
        w.WriteProperty("NPC", ((int)ch.NpcBrain).ToString());
        if (ch.NpcSpells.Count > 0)
        {
            foreach (var spell in ch.NpcSpells)
                w.WriteProperty("NPCSPELL", ((int)spell).ToString());
        }

        if (ch.OStr != 0) w.WriteProperty("OSTR", ch.OStr.ToString());
        if (ch.ODex != 0) w.WriteProperty("ODEX", ch.ODex.ToString());
        if (ch.OInt != 0) w.WriteProperty("OINT", ch.OInt.ToString());
        if (ch.OBody != 0) w.WriteProperty("OBODY", $"0{ch.OBody:X}");
        if (ch.OSkin != 0) w.WriteProperty("OSKIN", $"0{ch.OSkin:x}");
        if (ch.Luck != 0) w.WriteProperty("LUCK", ch.Luck.ToString());
        if (ch.Exp != 0) w.WriteProperty("EXP", ch.Exp.ToString());
        if (ch.Level != 0) w.WriteProperty("LEVEL", ch.Level.ToString());
        if (ch.Deaths != 0) w.WriteProperty("DEATHS", ch.Deaths.ToString());
        if (ch.Home.X != 0 || ch.Home.Y != 0)
            w.WriteProperty("HOME", $"{ch.Home.X},{ch.Home.Y},{ch.Home.Z},{ch.Home.Map}");
        if (ch.HomeDist != Character.UnlimitedHomeDistance)
            w.WriteProperty("HOMEDIST", ch.HomeDist.ToString());
        if (ch.ActPri != 0) w.WriteProperty("ACTPRI", ch.ActPri.ToString());
        if (ch.Action != 0) w.WriteProperty("ACTION", ((int)ch.Action).ToString());
        if (ch.Act.IsValid) w.WriteProperty("ACT", $"0{ch.Act.Value:X8}");
        if (ch.ActArg1 != 0) w.WriteProperty("ACTARG1", ch.ActArg1.ToString());
        if (ch.ActArg2 != 0) w.WriteProperty("ACTARG2", ch.ActArg2.ToString());
        if (ch.ActArg3 != 0) w.WriteProperty("ACTARG3", ch.ActArg3.ToString());
        if (ch.ActP.X != 0 || ch.ActP.Y != 0 || ch.ActP.Z != 0 || ch.ActP.Map != 0)
            w.WriteProperty("ACTP", $"{ch.ActP.X},{ch.ActP.Y},{ch.ActP.Z},{ch.ActP.Map}");
        if (ch.ActPrv.IsValid) w.WriteProperty("ACTPRV", $"0{ch.ActPrv.Value:X8}");
        if (ch.ActDiff != 0) w.WriteProperty("ACTDIFF", ch.ActDiff.ToString());
        if (ch.FightTarget.IsValid) w.WriteProperty("FIGHTTARGET", $"0{ch.FightTarget.Value:X8}");
        if (!ch.IsPlayer && ch.PetAIMode != SphereNet.Core.Enums.PetAIMode.Follow)
            w.WriteProperty("PETAI", ((int)ch.PetAIMode).ToString());
        if (ch.FleeStepsCurrent != 0) w.WriteProperty("FLEESTEPS", ch.FleeStepsCurrent.ToString());
        if (ch.FleeStepsMax != 0) w.WriteProperty("FLEESTEPSMAX", ch.FleeStepsMax.ToString());
        if (ch.SpeechColor != 0x0035) w.WriteProperty("SPEECHCOLOR", ch.SpeechColor.ToString());
        if (ch.MaxFollower != 5) w.WriteProperty("MAXFOLLOWER", ch.MaxFollower.ToString());
        // Per-char regen rate overrides (ms). Saved as tenths (D) so the loader
        // round-trips them exactly through the REGEN*D set path; 0 = global default.
        if (ch.RegenHitsRateMs != 0) w.WriteProperty("REGENHITSD", (ch.RegenHitsRateMs / 100).ToString());
        if (ch.RegenManaRateMs != 0) w.WriteProperty("REGENMANAD", (ch.RegenManaRateMs / 100).ToString());
        if (ch.RegenStamRateMs != 0) w.WriteProperty("REGENSTAMD", (ch.RegenStamRateMs / 100).ToString());
        if (ch.RegenFoodRateMs != 0) w.WriteProperty("REGENFOODD", (ch.RegenFoodRateMs / 100).ToString());
        if (ch.RegenValHits != 0) w.WriteProperty("REGENVALHITS", ch.RegenValHits.ToString());
        if (ch.RegenValMana != 0) w.WriteProperty("REGENVALMANA", ch.RegenValMana.ToString());
        if (ch.RegenValStam != 0) w.WriteProperty("REGENVALSTAM", ch.RegenValStam.ToString());
        if (ch.RegenValFood != 0) w.WriteProperty("REGENVALFOOD", ch.RegenValFood.ToString());
        if (ch.BloodHue != 0) w.WriteProperty("BLOODCOLOR", $"0{ch.BloodHue:X}");
        if (ch.FollowerSlotsOverride is int fsOverride) w.WriteProperty("FOLLOWERSLOTS", fsOverride.ToString());
        if (ch.ResPhysicalMax != 70) w.WriteProperty("RESPHYSICALMAX", ch.ResPhysicalMax.ToString());
        if (ch.ResFireMax != 70) w.WriteProperty("RESFIREMAX", ch.ResFireMax.ToString());
        if (ch.ResColdMax != 70) w.WriteProperty("RESCOLDMAX", ch.ResColdMax.ToString());
        if (ch.ResPoisonMax != 70) w.WriteProperty("RESPOISONMAX", ch.ResPoisonMax.ToString());
        if (ch.ResEnergyMax != 70) w.WriteProperty("RESENERGYMAX", ch.ResEnergyMax.ToString());
        if (ch.NightSight) w.WriteProperty("NIGHTSIGHT", "1");
        if (ch.StepStealth != 0) w.WriteProperty("STEPSTEALTH", ch.StepStealth.ToString());
        if (ch.SpeedMode != 0) w.WriteProperty("SPEEDMODE", ch.SpeedMode.ToString());
        if (!string.IsNullOrEmpty(ch.Profile)) w.WriteProperty("PROFILE", ch.Profile);
        if (ch.PFlag != 0) w.WriteProperty("PFLAG", ch.PFlag.ToString());
        if (ch.Tithing != 0) w.WriteProperty("TITHING", ch.Tithing.ToString());
        if (ch.SkillClass != 0) w.WriteProperty("SKILLCLASS", ch.SkillClass.ToString());

        if (ch.IsPlayer) w.WriteProperty("ISPLAYER", "1");

        long chTimeout = ch.Timeout;
        if (chTimeout > 0)
        {
            long chRemainingMs = chTimeout - now;
            if (chRemainingMs > 0)
                w.WriteProperty("TIMERMS", chRemainingMs.ToString());
        }

        var accountTag = ch.Tags.Get("ACCOUNT");
        if (!string.IsNullOrEmpty(accountTag))
            w.WriteProperty("ACCOUNT", accountTag);

        for (int s = 0; s < (int)SphereNet.Core.Enums.SkillType.Qty; s++)
        {
            var skillType = (SphereNet.Core.Enums.SkillType)s;
            ushort val = ch.GetSkill(skillType);
            if (val > 0)
            {
                string skillName = Enum.IsDefined(skillType) ? skillType.ToString() : $"SKILL[{s}]";
                w.WriteProperty(skillName, val.ToString());
            }
        }

        for (int s = 0; s < (int)SphereNet.Core.Enums.SkillType.Qty; s++)
        {
            byte lockVal = ch.GetSkillLock((SphereNet.Core.Enums.SkillType)s);
            if (lockVal != 0)
                w.WriteProperty($"SkillLock[{s}]", lockVal.ToString());
        }

        for (int i = 0; i < 3; i++)
        {
            byte lockVal = ch.GetStatLock(i);
            if (lockVal != 0)
                w.WriteProperty($"StatLock[{i}]", lockVal.ToString());
        }

        for (int layer = 0; layer <= (int)SphereNet.Core.Enums.Layer.Horse; layer++)
        {
            // Skip the virtual vendor SELL stock container (LAYER 26); it and its
            // contents are excluded from CaptureSnapshot and rebuilt on demand, so
            // persisting the EQUIP reference would dangle on load. LAYER 27
            // (VendorExtra) holds real goods bought from players and is persisted.
            if (layer == (int)SphereNet.Core.Enums.Layer.VendorStock)
                continue;
            var equip = ch.GetEquippedItem((SphereNet.Core.Enums.Layer)layer);
            if (equip != null)
                w.WriteProperty($"EQUIP[{layer}]", $"0{equip.Uid.Value:X8}");
        }

        foreach (var r in ch.Events)
        {
            if (r.Type != SphereNet.Core.Enums.ResType.Events || r.Index == 0) continue;
            string? name = ResolveResourceName?.Invoke(r);
            if (!string.IsNullOrWhiteSpace(name))
                w.WriteProperty("EVENTS", name!);
        }

        foreach (var r in ch.DSpeech)
        {
            if (r.Type != SphereNet.Core.Enums.ResType.Speech || r.Index == 0) continue;
            string? name = ResolveResourceName?.Invoke(r);
            if (!string.IsNullOrWhiteSpace(name))
                w.WriteProperty("DSPEECH", name!);
        }

        foreach (var mem in ch.Memories)
        {
            var flags = mem.GetMemoryTypes();
            if (flags == 0) continue;
            // Source-X saves EVERY memory item (they are ordinary equipped items
            // with timers), so fight/aggressor/sawcrime state survives a restart
            // — criminal-to-whom and murder attribution no longer reset. Format:
            // link,flags,remainingTimeoutMs(-1 = none),unixCreated.
            long memRemaining = mem.Timeout > 0 ? Math.Max(1, mem.Timeout - now) : -1;
            w.WriteProperty("MEMORY",
                $"0{mem.Link.Value:X8},{(ushort)flags},{memRemaining},{mem.More1}");
        }

        // Attacker log (Source-X m_lastAttackers): damage totals + ignore flags
        // per aggressor, so loot rights / kill credit survive a save-load.
        foreach (var rec in ch.Attackers)
            w.WriteProperty("ATTACKER",
                $"0{rec.Uid.Value:X8},{rec.TotalDamage},{(rec.Ignored ? 1 : 0)}");

        WriteTimerF(w, ch, now);

        if (GetSpellEffectRecords != null)
        {
            foreach (string record in GetSpellEffectRecords(ch, now))
            {
                if (!string.IsNullOrWhiteSpace(record))
                    w.WriteProperty("SPELLEFFECT", record);
            }
        }

        // Active poison (level, remaining ticks + time, poisoner). Saved as remaining
        // time so it resumes after load instead of silently ending on restart.
        if (ch.Poison.IsPoisoned && ch.Poison.TicksRemaining > 0)
        {
            string src = ch.Poison.Source.IsValid ? $"0{ch.Poison.Source.Value:X}" : "0";
            w.WriteProperty("POISON",
                $"{ch.Poison.Level}|{ch.Poison.TicksRemaining}|{ch.Poison.RemainingTickMs}|{src}");
        }

        foreach (var (key, val) in ch.Tags.GetAll())
        {
            string upper = key.ToUpperInvariant();
            if (upper is "ACCOUNT" or "MAXFOOD")
                continue;
            if (EngineTags.IsEphemeral(key))
                continue;
            if (upper.StartsWith("STATLOCK.", StringComparison.Ordinal))
                continue;
            if (upper == "SUMMON_EXPIRE_TICK")
            {
                // An absolute TickCount64 is UPTIME, not wall time: written to the
                // save it means nothing once the machine has rebooted, and the
                // summon then waits out a threshold that may be days away. Source-X
                // stores a timer as the REMAINING milliseconds
                // (_GetTimerAdjusted -> TIMERMS, CObjBase.cpp:2081) and rebuilds the
                // deadline against the load time (:2037), which is also how the
                // POISON record above already survives a restart.
                long remaining = long.TryParse(val, out long expireTick)
                    ? Math.Max(0, expireTick - Environment.TickCount64)
                    : 0;
                w.WriteProperty("TAG.SUMMON_EXPIRE_REMAINING", remaining.ToString());
                continue;
            }
            if (upper is "DSPEECH" or "EMOTECOLOR" or "VIRTUALGOLD"
                or "LASTUSED" or "LASTDISCONNECTED" or "NEED" or "SPAWNITEM")
            {
                w.WriteProperty(upper, val);
                continue;
            }
            w.WriteProperty("TAG." + key, val);
        }

        w.EndRecord();
    }

    private void SaveServerData(string savePath, GameWorld world)
        => WriteServerData(savePath, BuildServerData(world));

    /// <summary>Write the pre-rendered spheredata content with the usual
    /// tmp + rotate + commit dance. Prepared-data only — any thread.</summary>
    private void WriteServerData(string savePath, string content)
    {
        WriteServerDataToTmp(savePath, content);
        CommitServerData(savePath);
    }

    /// <summary>Phase 1 for spheredata: stage the content to its .tmp sibling only.</summary>
    private static void WriteServerDataToTmp(string savePath, string content)
    {
        string tmpPath = Path.Combine(savePath, "spheredata.scp") + ".tmp";
        File.WriteAllText(tmpPath, content);
    }

    /// <summary>Phase 2 for spheredata: rotate the live file to .bak and rename its
    /// staged .tmp into place.</summary>
    private void CommitServerData(string savePath)
        => CommitFile(Path.Combine(savePath, "spheredata.scp"));

    /// <summary>Render the spheredata.scp payload from live world state
    /// (globals, lists, GM pages, doors, sector env). MAIN-THREAD only.
    ///
    /// Goes through <see cref="TextSaveWriter"/> like every other save file. Hand
    /// rolling the KEY=VALUE lines here meant globals, list elements and GM page
    /// reasons skipped the shared value encoding, so anything holding a line break
    /// was silently truncated at the first one on reload.</summary>
    private string BuildServerData(GameWorld world)
    {
        using var buffer = new MemoryStream();
        using (var w = new TextSaveWriter(buffer, ownsStream: false))
        {
            w.WriteHeaderComment("SphereNet Server Data Save");
            w.WriteHeaderComment($"Save #{_saveIndex} at {DateTime.UtcNow:u}");

            w.BeginRecord("SPHERE");
            w.WriteProperty("VERSION", "1");
            w.WriteProperty("SAVECOUNT", _saveIndex.ToString());
            w.WriteProperty("TIME", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            // The GAME clock, in game minutes. TIME above is the real-world date the
            // save was taken and always has been; reading it back as game time would
            // invent a new bug. Source-X keeps the game clock in its own header field
            // (TIMEHIRES, CWorld.cpp:1510) and restores it on load (:1625) - without an
            // equivalent, every restart put the world back at midnight and took the
            // moon phases with it.
            w.WriteProperty("GAMETIME", world.WorldClockMinutes.ToString());

            // GLOBALS
            var globals = world.GetAllGlobalVars().ToList();
            if (globals.Count > 0)
            {
                w.BeginRecord("GLOBALS");
                foreach (var (key, val) in globals)
                    w.WriteProperty(key, val);
            }

            // LISTs
            foreach (var (name, list) in world.GetAllGlobalLists())
            {
                if (list.Count == 0) continue;
                w.BeginRecord($"LIST {name}");
                foreach (var elem in list)
                    w.WriteProperty("ELEM", elem);
            }

            // GM page queue (help requests). Source-X CGMPage::r_Write persists
            // these in the world save; one section per page keeps the free-text
            // reason on its own value line.
            var gmPages = world.GmPages;
            for (int gp = 0; gp < gmPages.Count; gp++)
            {
                var page = gmPages[gp];
                w.BeginRecord($"GMPAGE {gp}");
                w.WriteProperty("ACCOUNT", page.Account);
                w.WriteProperty("REASON", page.Reason);
                if (!string.IsNullOrEmpty(page.Handler))
                    w.WriteProperty("HANDLER", page.Handler);
                w.WriteProperty("STATUS", page.Status.ToString());
                w.WriteProperty("TIME", page.Created.ToString());
            }

            // Open static doors
            var openDoors = world.OpenMapStaticDoors;
            if (openDoors.Count > 0)
            {
                w.BeginRecord("DOORS");
                foreach (var (map, x, y, z) in openDoors)
                    w.WriteProperty("OPEN", $"{map},{x},{y},{z}");
            }

            // Per-sector environment overrides (Source-X CSector::r_Write saves
            // LIGHT / SEASON / RAINCHANCE / COLDCHANCE per sector). Only sectors
            // that differ from defaults are written; a GM-set local night or
            // winter no longer resets on restart.
            bool wroteSectorHeader = false;
            foreach (var (mapId, sector) in world.EnumerateSectors())
            {
                if (sector.Weather == SphereNet.Game.World.Sectors.Sector.WeatherDry && sector.Season == 0 &&
                    !sector.IsLightOverridden &&
                    sector.RainChance == 15 && sector.ColdChance == 5)
                    continue;
                if (!wroteSectorHeader)
                {
                    w.BeginRecord("SECTORS");
                    wroteSectorHeader = true;
                }
                // ENV2, not ENV: the weather byte now carries Source-X WEATHER_TYPE codes
                // (255=dry, 0=rain) where the old ENV line meant 0=dry, 1=rain. A reader
                // that took the new numbers for the old ones would turn every dry sector
                // into rain, so the new meaning gets its own key and the old key keeps
                // its old meaning forever.
                w.WriteProperty("ENV2",
                    $"{mapId},{sector.SectorX},{sector.SectorY}," +
                    $"{sector.Weather},{sector.Season},{sector.Light}," +
                    $"{(sector.IsLightOverridden ? 1 : 0)}," +
                    $"{sector.RainChance},{sector.ColdChance}");
            }
            // TextSaveWriter closes the file with its own [EOF] marker.
        }

        // spheredata.scp has always been written without a byte-order mark and a
        // classic server may still read it, so drop the preamble StreamWriter emits.
        byte[] bytes = buffer.ToArray();
        byte[] preamble = System.Text.Encoding.UTF8.GetPreamble();
        int offset = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return System.Text.Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    /// <summary>Atomic commit: rotate existing final to .bak1..N, then .tmp → final.</summary>
    private void CommitFile(string finalPath)
    {
        string tmpPath = finalPath + ".tmp";
        if (!File.Exists(tmpPath)) return;

        RotateBackups(finalPath);

        File.Move(tmpPath, finalPath, overwrite: true);
    }

    private void RotateBackups(string finalPath)
    {
        int levels = Math.Clamp(BackupLevels, 0, 32);
        if (levels <= 0)
        {
            for (int i = 1; i <= 32; i++)
            {
                string staleGeneration = $"{finalPath}.bak{i}";
                if (File.Exists(staleGeneration))
                {
                    try { File.Delete(staleGeneration); } catch { /* best effort */ }
                }
            }
            return;
        }

        string oldest = $"{finalPath}.bak{levels}";
        if (File.Exists(oldest))
        {
            try { File.Delete(oldest); } catch { /* best effort */ }
        }

        for (int i = levels - 1; i >= 1; i--)
        {
            string src = $"{finalPath}.bak{i}";
            if (!File.Exists(src)) continue;
            string dst = $"{finalPath}.bak{i + 1}";
            try { File.Move(src, dst, overwrite: true); } catch { /* best effort */ }
        }

        string stale = finalPath + ".bak";
        if (File.Exists(stale))
        {
            try { File.Delete(stale); } catch { /* best effort */ }
        }

        if (File.Exists(finalPath))
        {
            try { File.Copy(finalPath, $"{finalPath}.bak1", overwrite: true); } catch { /* best effort */ }
        }
    }

    private void CleanupTmpFiles(string savePath)
    {
        try
        {
            foreach (var tmp in Directory.EnumerateFiles(savePath, "*.tmp"))
            {
                try { File.Delete(tmp); }
                catch { /* best effort */ }
            }
        }
        catch { /* directory may not exist */ }
    }

    /// <summary>Delete files that match the save base but are NOT in the
    /// current output list — prevents orphaned old-format/old-shard files
    /// from shadowing the live snapshot.</summary>
    private void RemoveStaleSiblings(string savePath, string baseName, IEnumerable<string> keepNames)
    {
        var keep = new HashSet<string>(keepNames, StringComparer.OrdinalIgnoreCase);
        // Known save extensions including .bak from prior commits.
        string[] patterns =
        {
            $"{baseName}.scp", $"{baseName}.scp.gz", $"{baseName}.sbin", $"{baseName}.sbin.gz",
        };
        foreach (string p in patterns)
        {
            string full = Path.Combine(savePath, p);
            if (File.Exists(full) && !keep.Contains(p))
            {
                try { File.Delete(full); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove stale save {File}", full); }
            }
        }
        // Shard fragments {base}.{N}.{ext} + any leftover .bak sidecars.
        foreach (string file in Directory.GetFiles(savePath, baseName + ".*"))
        {
            string name = Path.GetFileName(file);
            if (keep.Contains(name)) continue;
            if (name.Equals(baseName + ".manifest", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

            // Plain .bak leftovers from older runs always go. Numbered
            // .bakN files belong to BackupLevels rotation.
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(file); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove stale backup {File}", file); }
                continue;
            }

            // Only touch things that look like shard fragments.
            if (IsShardFragment(name, baseName))
            {
                try { File.Delete(file); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove stale shard {File}", file); }
            }
        }
    }

    private static bool IsShardFragment(string fileName, string baseName)
    {
        // e.g. "sphereworld.3.sbin.gz" → prefix "sphereworld." + digits + known extension
        if (!fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) return false;
        int digitStart = baseName.Length + 1;
        int i = digitStart;
        while (i < fileName.Length && char.IsDigit(fileName[i])) i++;
        if (i == digitStart) return false; // no digits
        string tail = fileName[i..].ToLowerInvariant();
        return tail == ".scp" || tail == ".scp.gz" || tail == ".sbin" || tail == ".sbin.gz";
    }

    internal sealed record WorldSaveSnapshot(IReadOnlyList<SaveRecord> Items, IReadOnlyList<SaveRecord> Characters);

    internal sealed record SaveRecord(uint Uid, string Section, IReadOnlyList<(string Key, string Value)> Properties);

    private sealed class SnapshotSaveWriter : ISaveWriter
    {
        private readonly List<(string Key, string Value)> _properties = [];
        private string? _section;
        private bool _recordOpen;

        public long WrittenBytes { get; private set; }

        public void BeginRecord(string section)
        {
            if (_recordOpen)
                EndRecord();
            _section = section;
            _properties.Clear();
            _recordOpen = true;
            WrittenBytes += section.Length;
        }

        public void WriteProperty(string key, string value)
        {
            if (!_recordOpen)
                throw new InvalidOperationException("WriteProperty called before BeginRecord");
            _properties.Add((key, value));
            WrittenBytes += key.Length + value.Length + 1;
        }

        public void EndRecord()
        {
            _recordOpen = false;
        }

        public void WriteHeaderComment(string line)
        {
            _ = line;
        }

        public void Flush()
        {
        }

        public SaveRecord ToRecord(uint uid)
        {
            if (string.IsNullOrEmpty(_section))
                throw new InvalidOperationException("Snapshot record was not opened");
            return new SaveRecord(uid, _section, _properties.ToArray());
        }

        public void Dispose()
        {
            EndRecord();
        }
    }
}
