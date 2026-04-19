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

    /// <summary>Rolling threshold (bytes). Only consulted when <see cref="ShardCount"/>==1.</summary>
    public long ShardSizeBytes { get; set; }

    public Func<ResourceId, string?>? ResolveResourceName { get; set; }

    public WorldSaver(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<WorldSaver>();
    }

    public bool Save(GameWorld world, string savePath)
    {
        _saveIndex++;
        _logger.LogInformation("World save #{Index} starting (format={Format}, shards={Shards})...",
            _saveIndex, Format, ShardCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(savePath);

            var allObjects = world.GetAllObjects();

            int itemCount = SaveSharded(allObjects, savePath, "sphereworld", isItems: true);
            int charCount = SaveSharded(allObjects, savePath, "spherechars", isItems: false);
            SaveServerData(savePath);

            _logger.LogInformation("World save #{Index} complete: {Items} items, {Chars} chars in {Elapsed}s",
                _saveIndex, itemCount, charCount, sw.Elapsed.TotalSeconds.ToString("F1"));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "World save #{Index} FAILED", _saveIndex);
            return false;
        }
    }

    /// <summary>Write one logical save ("sphereworld" or "spherechars"). Three
    /// code paths, selected by <see cref="ShardCount"/>:
    /// <list type="bullet">
    /// <item><c>0</c> → classic single file, no rolling, no manifest.</item>
    /// <item><c>1</c> → Sphere-style rolling: one file until it fills, then
    /// spill to the next. Small worlds stay single-file + no manifest.</item>
    /// <item><c>2-16</c> → fixed UID-hash parallel shards, always manifest.</item>
    /// </list></summary>
    private int SaveSharded(IEnumerable<ObjBase> objects, string savePath, string baseName, bool isItems)
    {
        int shards = Math.Clamp(ShardCount, 0, 16);
        string ext = SaveIO.ExtensionFor(Format);
        long sizeLimit = Math.Max(0, ShardSizeBytes);

        int totalCount;
        List<string> outputFiles;

        if (shards == 0)
        {
            string fileName = baseName + ext;
            string tmp = Path.Combine(savePath, fileName + ".tmp");
            totalCount = WriteOneShard(objects, tmp, isItems, shardIndex: 0, shardCount: 1);
            outputFiles = new List<string> { fileName };
        }
        else if (shards == 1)
        {
            outputFiles = new List<string>();
            totalCount = WriteRollingShards(objects, savePath, baseName, ext, sizeLimit, isItems, outputFiles);

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

            var capturedObjects = objects.ToList();
            var tasks = new Task[shards];
            for (int i = 0; i < shards; i++)
            {
                int shardIdx = i;
                string tmp = Path.Combine(savePath, outputFiles[shardIdx] + ".tmp");
                tasks[i] = Task.Run(() =>
                    counts[shardIdx] = WriteOneShard(capturedObjects, tmp, isItems, shardIdx, shards));
            }
            Task.WaitAll(tasks);

            totalCount = 0;
            foreach (int c in counts) totalCount += c;
        }

        // Atomic commit for every real output file.
        foreach (string name in outputFiles)
            CommitFile(Path.Combine(savePath, name));

        // Manifest: written only when >1 file actually exists — keeps small
        // worlds at a single {base}{ext} file, matching classic Sphere layout.
        string manifestPath = ShardManifest.PathFor(savePath, baseName);
        if (outputFiles.Count > 1)
        {
            var manifest = new ShardManifest
            {
                Format = Format,
                ShardCount = outputFiles.Count,
                Files = outputFiles,
            };
            manifest.Save(manifestPath + ".tmp");
            CommitFile(manifestPath);
        }
        else if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        RemoveStaleSiblings(savePath, baseName, outputFiles);
        return totalCount;
    }

    /// <summary>Sphere-style rolling writer: open <c>{base}.0{ext}</c>, write
    /// records, and when the on-disk file crosses <paramref name="sizeLimit"/>
    /// close it at the next record boundary and open <c>{base}.1{ext}</c>.
    /// The actual file list is appended to <paramref name="outputFiles"/>.
    /// Size is polled on the raw FileStream (compressed bytes for gzip) which
    /// may lag by up to one gzip block — close enough for rolling.</summary>
    private int WriteRollingShards(IEnumerable<ObjBase> objects, string savePath, string baseName,
        string ext, long sizeLimit, bool isItems, List<string> outputFiles)
    {
        int count = 0;
        int errors = 0;
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
            writer.WriteHeaderComment($"Save #{_saveIndex} at {DateTime.UtcNow:u}");
            writer.WriteHeaderComment($"Rolling segment {fileIdx}");
        }

        OpenNext();

        foreach (var obj in objects)
        {
            try
            {
                if (isItems)
                {
                    if (obj is not Item item || item.IsDeleted) continue;
                    WriteItem(writer!, item);
                }
                else
                {
                    if (obj is not Character ch || ch.IsDeleted) continue;
                    WriteChar(writer!, ch);
                }
                count++;
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 5)
                    _logger.LogWarning(ex, "Error saving entity during rolling write");
            }

            if (sizeLimit > 0 && writer != null && writer.WrittenBytes >= sizeLimit)
            {
                writer.Dispose();
                writer = null; raw = null;
                fileIdx++;
                OpenNext();
            }
        }

        writer?.Dispose();

        if (errors > 0)
            _logger.LogWarning("Rolling save {Kind}: {Errors} entities failed",
                isItems ? "items" : "chars", errors);
        return count;
    }

    private int WriteOneShard(IEnumerable<ObjBase> objects, string tmpPath, bool isItems,
        int shardIndex, int shardCount)
    {
        using var writer = SaveIO.OpenWriter(tmpPath, Format);
        writer.WriteHeaderComment($"SphereNet {(isItems ? "World Items" : "World Characters")} Save");
        writer.WriteHeaderComment($"Save #{_saveIndex} at {DateTime.UtcNow:u}");
        if (shardCount > 1)
            writer.WriteHeaderComment($"Shard {shardIndex}/{shardCount}");

        int count = 0;
        int errors = 0;

        if (isItems)
        {
            foreach (var obj in objects)
            {
                if (obj is not Item item || item.IsDeleted) continue;
                if (shardCount > 1 && ShardManifest.ShardIndexForUid(item.Uid.Value, shardCount) != shardIndex)
                    continue;
                try
                {
                    WriteItem(writer, item);
                    count++;
                }
                catch (Exception ex)
                {
                    errors++;
                    if (errors <= 5)
                        _logger.LogWarning(ex, "Error saving item 0x{Serial:X8}", item.Uid.Value);
                }
            }
        }
        else
        {
            foreach (var obj in objects)
            {
                if (obj is not Character ch || ch.IsDeleted) continue;
                if (shardCount > 1 && ShardManifest.ShardIndexForUid(ch.Uid.Value, shardCount) != shardIndex)
                    continue;
                try
                {
                    WriteChar(writer, ch);
                    count++;
                }
                catch (Exception ex)
                {
                    errors++;
                    if (errors <= 5)
                        _logger.LogWarning(ex, "Error saving char 0x{Serial:X8} ({Name})",
                            ch.Uid.Value, ch.Name);
                }
            }
        }

        if (errors > 0)
            _logger.LogWarning("Save{Kind} shard {Idx}: {Errors} failed",
                isItems ? "Items" : "Chars", shardIndex, errors);
        return count;
    }

    private void WriteItem(ISaveWriter w, Item item)
    {
        w.BeginRecord("WORLDITEM");
        w.WriteProperty("SERIAL", $"0{item.Uid.Value:X8}");
        w.WriteProperty("UUID", item.Uuid.ToString("D"));
        w.WriteProperty("ID", $"0{item.BaseId:X}");
        w.WriteProperty("NAME", item.Name);
        w.WriteProperty("P", item.Position.ToString());
        w.WriteProperty("COLOR", item.Hue.Value.ToString());
        w.WriteProperty("AMOUNT", item.Amount.ToString());
        w.WriteProperty("TYPE", ((ushort)item.ItemType).ToString());
        w.WriteProperty("ATTR", ((uint)item.Attributes).ToString());

        if (item.More1 != 0) w.WriteProperty("MORE1", $"0{item.More1:X}");
        if (item.More2 != 0) w.WriteProperty("MORE2", $"0{item.More2:X}");
        if (item.MoreP != Point3D.Zero) w.WriteProperty("MOREP", item.MoreP.ToString());
        if (item.Link.IsValid) w.WriteProperty("LINK", $"0{item.Link.Value:X}");
        if (item.Price != 0) w.WriteProperty("PRICE", item.Price.ToString());
        if (item.Quality != 50) w.WriteProperty("QUALITY", item.Quality.ToString());

        if (item.TData1 != 0) w.WriteProperty("TDATA1", item.TData1.ToString());
        if (item.TData2 != 0) w.WriteProperty("TDATA2", item.TData2.ToString());
        if (item.TData3 != 0) w.WriteProperty("TDATA3", item.TData3.ToString());
        if (item.TData4 != 0) w.WriteProperty("TDATA4", item.TData4.ToString());

        if (item.ContainedIn.IsValid) w.WriteProperty("CONT", $"0{item.ContainedIn.Value:X8}");
        if (item.EquipLayer != 0) w.WriteProperty("LAYER", ((byte)item.EquipLayer).ToString());

        foreach (var (key, val) in item.Tags.GetAll())
            w.WriteProperty("TAG." + key, val);

        w.EndRecord();
    }

    private void WriteChar(ISaveWriter w, Character ch)
    {
        w.BeginRecord("WORLDCHAR");
        w.WriteProperty("SERIAL", $"0{ch.Uid.Value:X8}");
        w.WriteProperty("UUID", ch.Uuid.ToString("D"));
        w.WriteProperty("NAME", ch.Name);
        w.WriteProperty("P", ch.Position.ToString());
        w.WriteProperty("BODY", $"0{ch.BodyId:X}");
        w.WriteProperty("COLOR", ch.Hue.Value.ToString());
        w.WriteProperty("DIR", ((byte)ch.Direction).ToString());
        w.WriteProperty("STR", ch.Str.ToString());
        w.WriteProperty("DEX", ch.Dex.ToString());
        w.WriteProperty("INT", ch.Int.ToString());
        w.WriteProperty("HITS", ch.Hits.ToString());
        w.WriteProperty("MANA", ch.Mana.ToString());
        w.WriteProperty("STAM", ch.Stam.ToString());
        w.WriteProperty("MAXHITS", ch.MaxHits.ToString());
        w.WriteProperty("MAXMANA", ch.MaxMana.ToString());
        w.WriteProperty("MAXSTAM", ch.MaxStam.ToString());
        w.WriteProperty("FAME", ch.Fame.ToString());
        w.WriteProperty("KARMA", ch.Karma.ToString());
        if (ch.ResPhysical != 0) w.WriteProperty("RESPHYSICAL", ch.ResPhysical.ToString());
        if (ch.ResFire != 0) w.WriteProperty("RESFIRE", ch.ResFire.ToString());
        if (ch.ResCold != 0) w.WriteProperty("RESCOLD", ch.ResCold.ToString());
        if (ch.ResPoison != 0) w.WriteProperty("RESPOISON", ch.ResPoison.ToString());
        if (ch.ResEnergy != 0) w.WriteProperty("RESENERGY", ch.ResEnergy.ToString());
        if (ch.Kills != 0) w.WriteProperty("KILLS", ch.Kills.ToString());
        if (!string.IsNullOrEmpty(ch.Title)) w.WriteProperty("TITLE", ch.Title);
        w.WriteProperty("FLAGS", ((uint)ch.StatFlags).ToString());
        w.WriteProperty("NPCBRAIN", ((int)ch.NpcBrain).ToString());

        if (ch.OStr != 0) w.WriteProperty("OSTR", ch.OStr.ToString());
        if (ch.ODex != 0) w.WriteProperty("ODEX", ch.ODex.ToString());
        if (ch.OInt != 0) w.WriteProperty("OINT", ch.OInt.ToString());
        if (ch.OBody != 0) w.WriteProperty("OBODY", $"0{ch.OBody:X}");
        if (ch.OSkin != 0) w.WriteProperty("OSKIN", ch.OSkin.ToString());
        if (ch.Luck != 0) w.WriteProperty("LUCK", ch.Luck.ToString());
        if (ch.Exp != 0) w.WriteProperty("EXP", ch.Exp.ToString());
        if (ch.Level != 0) w.WriteProperty("LEVEL", ch.Level.ToString());
        if (ch.Deaths != 0) w.WriteProperty("DEATHS", ch.Deaths.ToString());
        if (ch.Home.X != 0 || ch.Home.Y != 0)
            w.WriteProperty("HOME", $"{ch.Home.X},{ch.Home.Y},{ch.Home.Z},{ch.Home.Map}");
        if (ch.HomeDist != 10) w.WriteProperty("HOMEDIST", ch.HomeDist.ToString());
        if (ch.ActPri != 0) w.WriteProperty("ACTPRI", ch.ActPri.ToString());
        if (ch.SpeechColor != 0x0035) w.WriteProperty("SPEECHCOLOR", ch.SpeechColor.ToString());
        if (ch.MaxFollower != 5) w.WriteProperty("MAXFOLLOWER", ch.MaxFollower.ToString());
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

        if (ch.IsPlayer) w.WriteProperty("ISPLAYER", "1");

        var accountTag = ch.Tags.Get("ACCOUNT");
        if (!string.IsNullOrEmpty(accountTag))
            w.WriteProperty("ACCOUNT", accountTag);

        for (int s = 0; s < (int)SphereNet.Core.Enums.SkillType.Qty; s++)
        {
            ushort val = ch.GetSkill((SphereNet.Core.Enums.SkillType)s);
            if (val > 0)
                w.WriteProperty($"SKILL[{s}]",
                    $"{val},{ch.GetSkillLock((SphereNet.Core.Enums.SkillType)s)}");
        }

        for (int layer = 0; layer <= (int)SphereNet.Core.Enums.Layer.Horse; layer++)
        {
            var equip = ch.GetEquippedItem((SphereNet.Core.Enums.Layer)layer);
            if (equip != null)
                w.WriteProperty($"EQUIP[{layer}]", $"0{equip.Uid.Value:X8}");
        }

        if (ch.Events.Count > 0)
        {
            string eventsCsv = string.Join(',',
                ch.Events
                    .Where(r => r.Type == SphereNet.Core.Enums.ResType.Events && r.Index != 0)
                    .Select(r =>
                    {
                        string? name = ResolveResourceName?.Invoke(r);
                        return string.IsNullOrWhiteSpace(name) ? $"Events:{r.Index}" : name!;
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(eventsCsv))
                w.WriteProperty("EVENTS", eventsCsv);
        }

        foreach (var (key, val) in ch.Tags.GetAll())
        {
            if (key.Equals("ACCOUNT", StringComparison.OrdinalIgnoreCase))
                continue;
            w.WriteProperty("TAG." + key, val);
        }

        w.EndRecord();
    }

    private void SaveServerData(string savePath)
    {
        // spheredata.scp stays classic text — tiny file, no benefit from binary/gzip.
        string finalPath = Path.Combine(savePath, "spheredata.scp");
        string tmpPath = finalPath + ".tmp";
        using (var sw = new StreamWriter(tmpPath))
        {
            sw.WriteLine("// SphereNet Server Data Save");
            sw.WriteLine($"// Save #{_saveIndex} at {DateTime.UtcNow:u}");
            sw.WriteLine();
            sw.WriteLine("[SPHERE]");
            sw.WriteLine($"SAVECOUNT={_saveIndex}");
            sw.WriteLine($"TIME={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            sw.WriteLine("[EOF]");
        }
        CommitFile(finalPath);
    }

    /// <summary>Atomic commit: .tmp → final (overwrite). Previous .bak rotation
    /// was dropped — it doubled the on-disk file count without a matching
    /// restore path. Catastrophic failure is still recoverable via the usual
    /// BACKUPLEVELS folder rotation (save/1, save/2, …).</summary>
    private static void CommitFile(string finalPath)
    {
        string tmpPath = finalPath + ".tmp";
        if (!File.Exists(tmpPath)) return;

        // Clean up any stale .bak from earlier runs that still wrote one.
        string stale = finalPath + ".bak";
        if (File.Exists(stale))
        {
            try { File.Delete(stale); } catch { /* best effort */ }
        }

        File.Move(tmpPath, finalPath, overwrite: true);
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

            // .bak leftovers from older runs always go.
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
}
