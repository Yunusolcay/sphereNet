using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;

namespace SphereNet.Persistence.Load;

/// <summary>
/// World loader. Maps to CWorld::Load in Source-X.
/// Auto-detects on-disk format via the manifest and file extensions so a
/// savedir can mix classic <c>.scp</c>, gzip, binary, or sharded layouts
/// without loader changes.
/// Handles both SphereNet saves (bare <c>[WORLDITEM]</c>) and classic
/// Sphere/Source-X saves (<c>[WORLDITEM i_backpack]</c> with defname suffix).
/// </summary>
public sealed class WorldLoader
{
    private readonly ILogger<WorldLoader> _logger;
    private int _migratedUuids;

    /// <summary>Resolves a Sphere defname to a base graphic/body ID.
    /// Returns 0 when the defname is unknown.</summary>
    public Func<string, ushort>? ResolveItemDef { get; set; }

    /// <summary>Resolves an ITEMDEF defname to its full 32-bit resource index.
    /// Non-numeric headers (<c>[ITEMDEF i_xxx]</c>) are keyed by a synthetic index
    /// above 0xFFFF that differs from the 16-bit graphic; the loader pins the
    /// defname so an instance can recover its Type/TData/MORE, which a lookup by
    /// the truncated graphic would miss. Returns 0 when unknown.</summary>
    public Func<string, int>? ResolveItemDefFullIndex { get; set; }

    /// <inheritdoc cref="ResolveItemDef"/>
    public Func<string, ushort>? ResolveCharDef { get; set; }

    /// <summary>Applies a WORLDCHAR section defname (c_man, c_banker, …).</summary>
    public Action<Character, string>? ApplyCharDefFromName { get; set; }

    /// <summary>Resolves a loaded CHARDEFINDEX to a client body graphic.</summary>
    public Func<int, ushort>? ResolveBodyFromCharDefIndex { get; set; }

    public WorldLoader(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<WorldLoader>();
    }

    public readonly record struct WorldImportScope(Point3D Center, int Distance, int Flags)
    {
        public bool IncludeItems => (Flags & 1) != 0;
        public bool IncludeChars => (Flags & 2) != 0;

        public bool Contains(Point3D point)
        {
            int distance = Math.Max(0, Distance);
            return point.Map == Center.Map && Center.GetDistanceTo(point) <= distance;
        }
    }

    /// <summary>Load one runtime <c>.scp</c>/<c>.sbin</c> file. This is used by
    /// Source-X-style SERV.LOAD / IMPORT / RESTORE world-ops where a script
    /// injects a bounded object file while the server is running.</summary>
    public (int Items, int Chars) LoadFile(GameWorld world, string path, AccountManager? accounts = null,
        WorldImportScope? scope = null)
    {
        if (!File.Exists(path))
            return (0, 0);

        string loadPath = path;
        string? filteredPath = null;
        if (scope.HasValue)
        {
            filteredPath = Path.Combine(Path.GetTempPath(), $"sphnet_import_{Guid.NewGuid():N}.scp");
            int filteredRecords = WriteScopedRuntimeLoadFile(path, filteredPath, scope.Value);
            _logger.LogInformation("Runtime scoped import filter: {Path} -> {Records} world record(s)",
                Path.GetFileName(path), filteredRecords);
            loadPath = filteredPath;
        }

        int itemCount = 0, charCount = 0;
        var charAccountLinks = new List<(Character Char, string AccountName)>();
        var charEquipLinks = new List<(Character Char, Serial ItemSerial, byte Layer)>();
        var itemContLinks = new List<(Item Item, Serial ContSerial, byte Layer)>();

        world.SuppressDirtyNotify = true;
        try
        {
            itemCount = LoadItemFile(world, loadPath, itemContLinks);
            charCount = LoadCharFile(world, loadPath, charAccountLinks, charEquipLinks);
            LoadDataFile(world, loadPath);
        }
        finally
        {
            world.SuppressDirtyNotify = false;
            world.ConsumeDirtyObjects();
            if (filteredPath != null)
            {
                try { File.Delete(filteredPath); } catch { }
            }
        }

        LinkLoadedAccounts(charAccountLinks, accounts);
        ResolveLoadedObjectLinks(world, itemContLinks, charEquipLinks);
        _logger.LogInformation("Runtime load: {Path} -> {Items} items, {Chars} chars",
            Path.GetFileName(path), itemCount, charCount);
        return (itemCount, charCount);
    }

    /// <summary>Restore one runtime object file, replacing any currently
    /// registered world object whose serial is present in the file. Unlike
    /// <see cref="LoadFile"/>, this matches Source-X's destructive restore
    /// intent for colliding serials while leaving unrelated world state alone.</summary>
    public (int Items, int Chars, int Replaced) RestoreFile(GameWorld world, string path, AccountManager? accounts = null,
        Func<IReadOnlyList<ObjBase>, string, int>? backupWriter = null)
    {
        if (!File.Exists(path))
            return (0, 0, 0);

        var restoreSerials = CollectWorldRecordSerials(path);
        var existing = CollectExistingWorldRecords(world, restoreSerials);
        string? backupPath = null;
        if (backupWriter != null && existing.Count > 0)
        {
            backupPath = Path.Combine(Path.GetTempPath(), $"sphnet_restore_rollback_{Guid.NewGuid():N}.scp");
            int backedUp = backupWriter(existing, backupPath);
            _logger.LogInformation("Runtime restore rollback snapshot: {Count} object(s) -> {Path}",
                backedUp, Path.GetFileName(backupPath));
        }

        int replaced = ReplaceExistingWorldRecords(world, restoreSerials);
        (int items, int chars) loaded;
        try
        {
            loaded = LoadFile(world, path, accounts);
        }
        catch (Exception ex) when (backupPath != null && File.Exists(backupPath))
        {
            TryRollbackRestore(world, backupPath, restoreSerials, accounts, ex);
            throw;
        }
        finally
        {
            if (backupPath != null)
            {
                try { File.Delete(backupPath); } catch { }
            }
        }

        _logger.LogInformation("Runtime restore: {Path} -> {Items} items, {Chars} chars, {Replaced} replaced",
            Path.GetFileName(path), loaded.items, loaded.chars, replaced);
        return (loaded.items, loaded.chars, replaced);
    }

    /// <summary>The resolved file set for one save generation (level 0 = current,
    /// level N = the aligned <c>.bakN</c> rotation of every logical file).</summary>
    private readonly record struct GenerationPaths(
        List<string> ItemPaths, List<string> CharPaths, List<string> StaticPaths,
        List<string> MultiPaths, List<string> DataPaths, bool Incomplete)
    {
        public bool IsEmpty => ItemPaths.Count == 0 && CharPaths.Count == 0 &&
            StaticPaths.Count == 0 && MultiPaths.Count == 0 && DataPaths.Count == 0;

        public IEnumerable<string> AllFiles => ItemPaths
            .Concat(CharPaths).Concat(StaticPaths).Concat(MultiPaths).Concat(DataPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Load world data, tolerating a corrupt/truncated current save. Each save
    /// rotates every logical file's <c>.bakN</c> together, so <c>.bak1</c> is the
    /// previous full generation, <c>.bak2</c> the one before, etc. Try the current
    /// generation first; if any of its files fail an integrity read, fall back to
    /// the newest intact backup generation. A corrupt save therefore no longer
    /// aborts boot with a raw exception — and because a whole generation is loaded
    /// or skipped as a unit, a fallback never mixes new and old files. If every
    /// generation is unreadable, throw a clear, actionable error instead of
    /// silently starting a blank world.
    /// </summary>
    public (int Items, int Chars) Load(GameWorld world, string savePath, AccountManager? accounts = null)
    {
        if (!Directory.Exists(savePath))
        {
            _logger.LogInformation("No save directory at {Path} — starting a fresh world", savePath);
            return (0, 0);
        }

        // .bakN levels are bounded by BackupLevels (max 32); probe a bit past the
        // configured max in case backups pre-date a lowered setting.
        const int MaxBackupProbe = 32;
        bool anyGenerationExisted = false;
        for (int level = 0; level <= MaxBackupProbe; level++)
        {
            var gen = ResolveGeneration(savePath, level);
            if (gen.IsEmpty)
            {
                // A missing generation. Keep probing past a missing CURRENT save so
                // a surviving .bakN is still recovered instead of silently booting a
                // blank world — the previous behaviour returned a fresh world here
                // even when good backups sat on disk. Backups rotate contiguously
                // from .bak1, so once a backup level is missing nothing older remains.
                if (level == 0)
                    continue;
                break;
            }
            anyGenerationExisted = true;

            // A generation whose manifest lists a shard that is not on disk is
            // incomplete: loading its surviving shards would silently lose every
            // object in the missing one. Treat it like a corrupt generation and fall
            // back to the previous (whole) one rather than booting with holes.
            if (gen.Incomplete)
            {
                _logger.LogError(
                    "Save generation {Which} is incomplete (a manifest shard is missing); falling back to the previous generation",
                    level == 0 ? "current" : $".bak{level}");
                continue;
            }

            var (ok, badFile) = ValidateGeneration(gen);
            if (!ok)
            {
                _logger.LogError(
                    "Save generation {Which} is unreadable (bad file: {File}); falling back to the previous generation",
                    level == 0 ? "current" : $".bak{level}", badFile);
                continue;
            }

            // Cross-file consistency: each file individually validated, but a torn
            // multi-file commit can leave them from different saves (new sphereworld
            // beside old spherechars). Their SAVEID stamps disagree, so reject the
            // mixed generation and fall back to the last internally-consistent one.
            if (!IsGenerationConsistent(gen, out string stampDetail))
            {
                _logger.LogError(
                    "Save generation {Which} is internally inconsistent — its files come from different saves ({Detail}), " +
                    "indicating a torn multi-file commit; falling back to the previous generation",
                    level == 0 ? "current" : $".bak{level}", stampDetail);
                continue;
            }

            if (level > 0)
                _logger.LogWarning(
                    "Loading from BACKUP generation .bak{Level} — the current save (and any newer backups) were missing or unreadable. " +
                    "Investigate the current save files; the next save will overwrite this backup.", level);

            return Materialize(world, gen, accounts);
        }

        // Reached only when no generation could be loaded. If nothing existed at any
        // level it is a genuine fresh start; if generations existed but every one
        // failed validation, refuse to silently blank the world.
        if (!anyGenerationExisted)
        {
            _logger.LogInformation("No world save found in {Path} — starting a fresh world", savePath);
            return (0, 0);
        }

        throw new InvalidDataException(
            $"All world save generations under '{savePath}' are unreadable. Restore a known-good backup " +
            "(e.g. copy a matching set of sphere*.bakN files over the current ones) or move the corrupt " +
            "files aside to start a fresh world.");
    }

    private GenerationPaths ResolveGeneration(string savePath, int bakLevel)
    {
        var items = ResolveSaveFiles(savePath, "sphereworld", bakLevel, out bool incItems);
        var chars = ResolveSaveFiles(savePath, "spherechars", bakLevel, out bool incChars);
        var statics = ResolveSaveFiles(savePath, "spherestatics", bakLevel, out bool incStatics);
        var multis = ResolveSaveFiles(savePath, "spheremultis", bakLevel, out bool incMultis);
        var data = ResolveSaveFiles(savePath, "spheredata", bakLevel, out bool incData);
        return new GenerationPaths(items, chars, statics, multis, data,
            incItems || incChars || incStatics || incMultis || incData);
    }

    /// <summary>Read every file of a generation end-to-end, without materializing
    /// anything, so corruption (bad gzip, truncation, misaligned binary records)
    /// is detected before a single object is created. Returns the first bad file.</summary>
    private (bool Ok, string? BadFile) ValidateGeneration(GenerationPaths gen)
    {
        foreach (string path in gen.AllFiles)
        {
            try
            {
                using var reader = SaveIO.OpenReader(path);
                while (reader.NextRecord(out _))
                    while (reader.NextProperty(out _, out _)) { }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Save file failed integrity validation: {Path}", Path.GetFileName(path));
                return (false, Path.GetFileName(path));
            }
        }
        return (true, null);
    }

    /// <summary>Read a single stamp property from the first record of a save file
    /// that opens with the given section. Returns null when the file has no such
    /// stamp (a legacy save) or it can't be parsed.</summary>
    private static long? ReadStamp(string path, string section, string prop)
    {
        try
        {
            using var reader = SaveIO.OpenReader(path);
            while (reader.NextRecord(out string sec))
            {
                bool match = sec.Equals(section, StringComparison.OrdinalIgnoreCase);
                while (reader.NextProperty(out string key, out string val))
                {
                    if (match && key.Equals(prop, StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(val, out long id))
                        return id;
                }
                if (match) return null; // section present but no parsable stamp
            }
        }
        catch { /* validation already vetted readability; treat as unstamped */ }
        return null;
    }

    /// <summary>Verify that every logical file of a generation was written by the
    /// SAME save. Item/char shards carry a [SAVEID] record and spheredata records
    /// SAVECOUNT; a torn multi-file commit (a crash between the back-to-back
    /// renames) leaves files from different saves whose stamps disagree. Enforced
    /// only when every present base is stamped — a legacy or partially-stamped
    /// generation can't be verified and is accepted rather than wrongly rejected.</summary>
    private bool IsGenerationConsistent(GenerationPaths gen, out string detail)
    {
        long? worldId = gen.ItemPaths.Count > 0
            ? ReadStamp(gen.ItemPaths[0], SaveIO.SaveIdSection, SaveIO.SaveIdProperty) : null;
        long? charId = gen.CharPaths.Count > 0
            ? ReadStamp(gen.CharPaths[0], SaveIO.SaveIdSection, SaveIO.SaveIdProperty) : null;
        long? dataId = gen.DataPaths.Count > 0
            ? ReadStamp(gen.DataPaths[0], SaveIO.ServerDataSection, SaveIO.SaveCountProperty) : null;

        detail = $"world={worldId?.ToString() ?? "-"} chars={charId?.ToString() ?? "-"} data={dataId?.ToString() ?? "-"}";

        long? reference = null;
        foreach (var (count, id) in new[]
                 { (gen.ItemPaths.Count, worldId), (gen.CharPaths.Count, charId), (gen.DataPaths.Count, dataId) })
        {
            if (count == 0) continue;      // this base has no file — nothing to compare
            if (id == null) return true;   // present but unstamped — can't verify, accept
            if (reference == null) reference = id;
            else if (id != reference) return false; // stamps disagree — torn/mixed generation
        }
        return true;
    }

    private (int Items, int Chars) Materialize(GameWorld world, GenerationPaths gen, AccountManager? accounts)
    {
        int itemCount = 0, charCount = 0;

        var charAccountLinks = new List<(Character Char, string AccountName)>();
        var charEquipLinks = new List<(Character Char, Serial ItemSerial, byte Layer)>();
        var itemContLinks = new List<(Item Item, Serial ContSerial, byte Layer)>();

        // Suppress dirty notifications for the whole bulk materialization.
        // Otherwise the dirty set balloons to millions of entries and stalls
        // the fast-path drain during login. Clients get a view resync at
        // login anyway.
        world.SuppressDirtyNotify = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var itemPaths = gen.ItemPaths;
            var charPaths = gen.CharPaths;
            var staticPaths = gen.StaticPaths;
            var multiPaths = gen.MultiPaths;

            var itemSectionPaths = itemPaths
                .Concat(charPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string path in itemSectionPaths)
            {
                int n = LoadItemFile(world, path, itemContLinks);
                itemCount += n;
                _logger.LogInformation("Items: {Path} -> {Count}", Path.GetFileName(path), n);
            }

            foreach (string path in staticPaths)
            {
                int n = LoadItemFile(world, path, itemContLinks);
                itemCount += n;
                _logger.LogInformation("Statics: {Path} -> {Count}", Path.GetFileName(path), n);
            }

            foreach (string path in multiPaths)
            {
                int n = LoadItemFile(world, path, itemContLinks);
                itemCount += n;
                _logger.LogInformation("Multis: {Path} -> {Count}", Path.GetFileName(path), n);
            }

            // Sphere/Source-X saves can mix [WORLDITEM] and [WORLDCHAR]
            // sections in either sphereworld.scp or spherechars.scp. Make a
            // second pass over both logical files to pick up mobiles after the
            // item pass has captured all container/equipment references.
            var charSectionPaths = itemPaths
                .Concat(charPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string path in charSectionPaths)
            {
                int n = LoadCharFile(world, path, charAccountLinks, charEquipLinks);
                if (n > 0)
                {
                    charCount += n;
                    _logger.LogInformation("WorldChars: {Path} -> {Count}", Path.GetFileName(path), n);
                }
            }

            // spheredata.scp — globals, lists, world scripts
            var dataPaths = gen.DataPaths;
            foreach (string path in dataPaths)
                LoadDataFile(world, path);
        }
        finally
        {
            world.SuppressDirtyNotify = false;
            world.ConsumeDirtyObjects();
        }

        // Post-load: link accounts to characters (only if not already linked via CHARUID)
        if (accounts != null)
        {
            int linked = 0;
            foreach (var (ch, accName) in charAccountLinks)
            {
                var acc = accounts.FindAccount(accName);
                if (acc != null)
                {
                    // Any char with a resolved account slot is a player
                    // character. Legacy Sphere saves don't write ISPLAYER
                    // so without this line those chars default to
                    // IsPlayer=false and GetNotoriety treats them as
                    // NPCs (grey overhead name, ignored by notoriety
                    // rules). Account linkage is authoritative proof
                    // that this is a player mobile.
                    ch.IsPlayer = true;

                    bool alreadyLinked = false;
                    for (int i = 0; i < 7; i++)
                    {
                        if (acc.GetCharSlot(i) == ch.Uid)
                        {
                            alreadyLinked = true;
                            break;
                        }
                    }

                    if (!alreadyLinked)
                    {
                        int slot = acc.FindFreeSlot();
                        if (slot >= 0)
                        {
                            acc.SetCharSlot(slot, ch.Uid);
                            linked++;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Orphan player character 0x{Uid:X8} ({Name}) — account '{Account}' not found",
                        ch.Uid.Value, ch.Name, accName);
                }
            }
            _logger.LogInformation("Linked {Count}/{Total} characters to accounts", linked, charAccountLinks.Count);
        }

        // Post-load: resolve container/equipment references
        int containedCount = 0;
        foreach (var (item, contSerial, layer) in itemContLinks)
        {
            var parent = world.FindObject(contSerial);
            if (parent is Character parentChar)
            {
                EquipLoadedItem(world, parentChar, item, layer);
                containedCount++;
            }
            else if (parent is Item parentItem)
            {
                if (parentItem.Uid == item.Uid)
                {
                    _logger.LogWarning("Item {Uid:X8} references itself as container, placing on ground",
                        item.Uid.Value);
                    world.PlaceItem(item, item.Position);
                }
                else if (parentItem.AddItem(item))
                {
                    containedCount++;
                }
                else
                {
                    // A corrupt/hand-edited CONT chain (e.g. A->B->A) would leave
                    // this item's ContainedIn pointing into a cycle; TryAddItem
                    // refused the add. Quarantine it on the ground so its
                    // ContainedIn no longer forms the cycle (which would otherwise
                    // hang the parent-chain walk in packet sends).
                    _logger.LogWarning(
                        "Item 0x{Uid:X8} CONT 0x{Cont:X8} forms a containment cycle; placing on ground",
                        item.Uid.Value, contSerial.Value);
                    world.PlaceItem(item, item.Position);
                }
            }
            else
            {
                world.PlaceItem(item, item.Position);
            }
        }

        int equipCount = 0;
        foreach (var (ch, itemSerial, layer) in charEquipLinks)
        {
            var item = world.FindItem(itemSerial);
            if (item == null)
            {
                _logger.LogWarning("Character 0x{CharUid:X8} references missing EQUIP item 0x{ItemUid:X8} on layer {Layer}",
                    ch.Uid.Value, itemSerial.Value, layer);
                continue;
            }

            if (item.ContainedIn.IsValid && world.FindItem(item.ContainedIn) is { } parentItem)
                parentItem.RemoveItem(item);

            if (ch.Equip(item, (Layer)layer))
                equipCount++;
        }

        // A legacy save stores neither TYPE nor TDATA — an item's type comes from
        // its ITEMDEF (resolved via defname/BaseId). The lazy ItemType getter hid
        // this, but the raw _type field stayed Normal, so code reading it directly
        // (and the <TYPE> script read) saw t_normal. Materialize TYPE/TDATA from the
        // resolved def now that all defs are loaded, so a legacy instance matches a
        // script-created one. Definitions must already be built (they are — the
        // caller loads them before the save). Does not change the save format.
        foreach (var obj in world.GetAllObjects())
            if (obj is Item it && !it.IsDeleted)
                it.MaterializeDefinitionType();

        // The container reverse index is maintained incrementally by the
        // Item.ContainedIn setter, but only when Item.ResolveWorld is wired — and
        // the initial save load runs BEFORE that wiring. Without this rebuild every
        // saved container's contents live in Item.Contents (.edit sees them) yet
        // are absent from the index the client's open-container 0x3C batch reads,
        // so bags render empty on the client after a restart. Rebuild once from the
        // authoritative ContainedIn now that every item and parent link exists.
        world.RebuildContainerIndex();

        _logger.LogInformation("World loaded: {Items} items, {Chars} chars, {Contained} contained/equipped in {Elapsed}s",
            itemCount, charCount, containedCount + equipCount, sw.Elapsed.TotalSeconds.ToString("F1"));

        if (_migratedUuids > 0)
            _logger.LogInformation("UUID migration: {Count} objects had no UUID — auto-generated (will persist on next save)",
                _migratedUuids);

        return (itemCount, charCount);
    }

    /// <summary>Resolves an equipped item's layer from tiledata (the UO item
    /// graphic's Quality byte IS its equip layer). Wired by the host from the loaded
    /// tiledata; classic Sphere saves omit LAYER for worn clothing whose itemdef also
    /// has no explicit layer (TYPE=t_clothing), so the layer must come from tiledata.</summary>
    public Func<ushort, byte>? ResolveEquipLayerFromTile { get; set; }

    /// <summary>Equip an item that a save stored under <c>CONT=&lt;char&gt;</c>.
    /// When the save gave no explicit <c>LAYER=</c> (layer 0), the layer is derived
    /// from the itemdef and then tiledata — classic Sphere saves omit the layer for
    /// worn clothing, so without this the item equipped on <see cref="Layer.None"/>
    /// and the wearer rendered naked. A non-wearable child (or an already-occupied
    /// layer) falls back to the backpack, then the ground, instead of a bogus
    /// layer-0 equip.</summary>
    private void EquipLoadedItem(GameWorld world, Character parentChar, Item item, byte layer)
    {
        Layer targetLayer = layer != 0 ? (Layer)layer : ResolveContItemLayer(item);
        // Never evict an already-worn item. A tiledata-derived layer can collide
        // with a piece already seated on that slot (two graphics resolving to the
        // same hand/torso layer); Character.Equip would bounce the incumbent onto
        // the ground ("weapon in hand AND a weapon dropped underneath the NPC").
        // The newcomer goes to the pack instead, leaving the worn item in place.
        if (targetLayer != Layer.None && parentChar.GetEquippedItem(targetLayer) == null &&
            parentChar.Equip(item, targetLayer))
            return;
        // Could not resolve a wearable slot — log enough to see which graphics the
        // running tiledata leaves unlayered (the "worn item drops to the ground /
        // NPC stays naked" symptom) before falling back to pack / ground.
        byte tileQ = ResolveEquipLayerFromTile?.Invoke(item.BaseId) ?? 0;
        if (parentChar.Backpack is { } pack)
        {
            _logger.LogDebug("Unlayered worn item 0x{Base:X} (saveLayer={SL} tiledataQ={Q}) on '{Name}' -> backpack",
                item.BaseId, layer, tileQ, parentChar.Name);
            pack.AddItem(item);
        }
        else
        {
            _logger.LogWarning("Unlayered worn item 0x{Base:X} (saveLayer={SL} tiledataQ={Q}) on '{Name}' -> ground (no backpack)",
                item.BaseId, layer, tileQ, parentChar.Name);
            world.PlaceItem(item, item.Position);
        }
    }

    /// <summary>Layer for a char-contained item with no explicit LAYER: the item's
    /// own EquipLayer/itemdef layer, else the tiledata Quality (UO's equip layer).</summary>
    private Layer ResolveContItemLayer(Item item)
    {
        Layer byDef = item.ResolveEquipLayer();
        if (byDef != Layer.None)
            return byDef;
        // Primary UO source (matches Source-X CItemBase: m_layer = tiledata.m_layer).
        byte q = ResolveEquipLayerFromTile?.Invoke(item.BaseId) ?? 0;
        if (q > 0 && q <= (byte)Layer.Horse)
            return (Layer)q;
        // Last resort: a fixed-slot equip type whose tiledata layer byte is 0
        // (hair/beard) or the char's sole unlabelled container (its backpack).
        // Without this a naked NPC's backpack fails to seat, which cascades every
        // subsequent worn item that fails to equip onto the ground.
        return item.ResolveEquipLayerByType();
    }

    private void LinkLoadedAccounts(List<(Character Char, string AccountName)> charAccountLinks,
        AccountManager? accounts)
    {
        if (accounts == null)
            return;

        int linked = 0;
        foreach (var (ch, accName) in charAccountLinks)
        {
            var acc = accounts.FindAccount(accName);
            if (acc != null)
            {
                ch.IsPlayer = true;

                bool alreadyLinked = false;
                for (int i = 0; i < 7; i++)
                {
                    if (acc.GetCharSlot(i) == ch.Uid)
                    {
                        alreadyLinked = true;
                        break;
                    }
                }

                if (!alreadyLinked)
                {
                    int slot = acc.FindFreeSlot();
                    if (slot >= 0)
                    {
                        acc.SetCharSlot(slot, ch.Uid);
                        linked++;
                    }
                }
            }
            else
            {
                _logger.LogWarning("Orphan player character 0x{Uid:X8} ({Name}) — account '{Account}' not found",
                    ch.Uid.Value, ch.Name, accName);
            }
        }

        if (charAccountLinks.Count > 0)
            _logger.LogInformation("Linked {Count}/{Total} characters to accounts", linked, charAccountLinks.Count);
    }

    private void ResolveLoadedObjectLinks(GameWorld world,
        List<(Item Item, Serial ContSerial, byte Layer)> itemContLinks,
        List<(Character Char, Serial ItemSerial, byte Layer)> charEquipLinks)
    {
        foreach (var (item, contSerial, layer) in itemContLinks)
        {
            var parent = world.FindObject(contSerial);
            if (parent is Character parentChar)
            {
                EquipLoadedItem(world, parentChar, item, layer);
            }
            else if (parent is Item parentItem)
            {
                if (parentItem.Uid == item.Uid)
                {
                    _logger.LogWarning("Item {Uid:X8} references itself as container, placing on ground",
                        item.Uid.Value);
                    world.PlaceItem(item, item.Position);
                }
                else if (!parentItem.AddItem(item))
                {
                    // Corrupt CONT cycle — quarantine on the ground (see Materialize).
                    _logger.LogWarning(
                        "Item 0x{Uid:X8} CONT 0x{Cont:X8} forms a containment cycle; placing on ground",
                        item.Uid.Value, contSerial.Value);
                    world.PlaceItem(item, item.Position);
                }
            }
            else
            {
                world.PlaceItem(item, item.Position);
            }
        }

        foreach (var (ch, itemSerial, layer) in charEquipLinks)
        {
            var item = world.FindItem(itemSerial);
            if (item == null)
            {
                _logger.LogWarning("Character 0x{CharUid:X8} references missing EQUIP item 0x{ItemUid:X8} on layer {Layer}",
                    ch.Uid.Value, itemSerial.Value, layer);
                continue;
            }

            if (item.ContainedIn.IsValid && world.FindItem(item.ContainedIn) is { } parentItem)
                parentItem.RemoveItem(item);

            ch.Equip(item, (Layer)layer);
        }
    }

    /// <summary>Resolve actual on-disk paths for a logical save name
    /// (e.g. "sphereworld") at a backup level (0 = current, N = the aligned
    /// <c>.bakN</c> rotation). Priority: manifest → format probes. Returns an
    /// empty list if nothing exists at that level.</summary>
    private List<string> ResolveSaveFiles(string savePath, string baseName, int bakLevel, out bool incomplete)
    {
        incomplete = false;
        string suffix = bakLevel == 0 ? "" : $".bak{bakLevel}";

        string manifestPath = ShardManifest.PathFor(savePath, baseName) + suffix;
        var manifest = ShardManifest.TryLoad(manifestPath);
        if (manifest != null && manifest.Files.Count > 0)
        {
            var list = new List<string>(manifest.Files.Count);
            foreach (var name in manifest.Files)
            {
                // Shard names in the manifest are the logical (current) names; at a
                // backup level the on-disk file carries the same .bakN suffix as
                // the manifest, since every file rotates together.
                string full = Path.Combine(savePath, name + suffix);
                if (File.Exists(full)) list.Add(full);
                else
                {
                    // A manifest shard that is not on disk means this generation is
                    // incomplete — loading only the surviving shards would silently
                    // drop every object in the missing one. Flag it so the caller
                    // falls back to a whole intact generation instead.
                    incomplete = true;
                    _logger.LogWarning("Manifest {Base} ({Which}) references missing shard {File}",
                        baseName, bakLevel == 0 ? "current" : $".bak{bakLevel}", name);
                }
            }
            if (bakLevel == 0)
                _logger.LogInformation("Loaded manifest {Base}: format={Format}, shards={Count}",
                    baseName, manifest.Format, list.Count);
            return list;
        }

        // No manifest — probe for single-file variants in priority order
        // (most-specific format first so a gzip survives alongside a stale .scp).
        foreach (string ext in new[] { ".sbin.gz", ".sbin", ".scp.gz", ".scp" })
        {
            string candidate = Path.Combine(savePath, baseName + ext + suffix);
            if (File.Exists(candidate))
                return new List<string> { candidate };
        }
        return new List<string>();
    }

    private int LoadItemFile(GameWorld world, string path, List<(Item, Serial, byte)> contLinks)
    {
        int count = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            if (!ParseSectionType(section, "WORLDITEM", out string? defname))
            {
                while (reader.NextProperty(out _, out _)) { /* skip */ }
                continue;
            }

            var item = world.CreateItem();
            Serial contSerial = Serial.Invalid;
            byte layer = 0;

            if (defname != null && ResolveItemDef != null)
            {
                ushort baseId = ResolveItemDef(defname);
                if (baseId != 0)
                    item.BaseId = baseId;
                else
                    _logger.LogDebug("Unknown item defname '{DefName}' — BaseId stays default", defname);

                // Pin the source defname when the ITEMDEF is keyed by a synthetic
                // index (non-numeric [ITEMDEF i_xxx] header) that the 16-bit BaseId
                // graphic can't reach. Without this, ResolveInstanceDefIndex falls
                // back to GetItemDef(BaseId) — a different def or none — and the
                // instance reads Type/TData/MORE as 0 (same 0xFFFF-truncation class
                // as the Spawn_FFFF body bug). Numeric headers (BaseId == index)
                // resolve directly and need no tag.
                int fullIndex = ResolveItemDefFullIndex?.Invoke(defname) ?? 0;
                if (fullIndex != 0 && fullIndex != baseId)
                    item.SetTag("ITEMDEF", defname);
            }

            bool hasUuid = false;
            bool hasId = false;
            bool skipItem = false;
            while (reader.NextProperty(out string key, out string val))
            {
                string upper = key.ToUpperInvariant();
                // Once a duplicate SERIAL/UUID has retired (DeleteObject'd) this
                // instance, drain the rest of the record with NO side effects —
                // in particular the UUID branch, which otherwise re-indexed the
                // already-deleted object and left a stale UUID -> dead-object entry
                // (M8: FindByUuid would return a deleted object).
                if (skipItem)
                    continue;
                if (upper == "SERIAL")
                {
                    if (TryParseHexOrDec(val, out uint serial))
                    {
                        var oldUid = item.Uid;
                        var newUid = new Serial(serial);
                        if (!world.TryReRegisterObject(item, oldUid, newUid, out var existing))
                        {
                            _logger.LogWarning(
                                "Skipping duplicate item serial 0x{Serial:X8} in {File}: existing={ExistingType}",
                                serial, Path.GetFileName(path), existing?.GetType().Name ?? "unknown");
                            world.DeleteObject(item);
                            skipItem = true;
                        }
                    }
                    continue;
                }
                if (upper == "UUID")
                {
                    if (Guid.TryParse(val, out Guid uuid))
                    {
                        var oldUuid = item.Uuid;
                        item.Uuid = uuid;
                        if (!world.TryReIndexUuid(item, oldUuid, out var existing))
                        {
                            _logger.LogWarning(
                                "Skipping duplicate item UUID {Uuid} for serial 0x{Serial:X8} in {File}: existing=0x{Existing:X8}",
                                uuid, item.Uid.Value, Path.GetFileName(path), existing?.Uid.Value ?? 0);
                            world.DeleteObject(item);
                            skipItem = true;
                        }
                        hasUuid = true;
                    }
                    continue;
                }
                if (upper == "CONT")
                {
                    if (TryParseHexOrDec(val, out uint c))
                        contSerial = new Serial(c);
                    continue;
                }
                if (upper == "LAYER")
                {
                    byte.TryParse(val, out layer);
                    continue;
                }
                if (upper == "CREATE")
                    continue;
                if (upper == "ID")
                    hasId = true;
                ApplyItemProperty(item, key, val);
            }
            if (skipItem)
                continue;

            if (!hasUuid)
                _migratedUuids++;

            if (!hasId && defname != null && item.BaseId == 0)
                _logger.LogWarning("Item 0x{Uid:X} defname='{Def}' has no BaseId after load — will be invisible",
                    item.Uid.Value, defname);

            if (contSerial.IsValid)
                contLinks.Add((item, contSerial, layer));
            else
                world.PlaceItem(item, item.Position);
            count++;

            if (count % 100_000 == 0)
                _logger.LogInformation("  Loading items... {Count} ({Elapsed}s)",
                    count, sw.Elapsed.TotalSeconds.ToString("F1"));
        }
        return count;
    }

    private enum RuntimeWorldRecordKind
    {
        Other,
        Item,
        Character
    }

    private sealed class RuntimeWorldRecord
    {
        public RuntimeWorldRecord(string section, List<(string Key, string Value)> properties,
            RuntimeWorldRecordKind kind)
        {
            Section = section;
            Properties = properties;
            Kind = kind;
        }

        public string Section { get; }
        public List<(string Key, string Value)> Properties { get; }
        public RuntimeWorldRecordKind Kind { get; }
        public Serial Serial { get; set; } = Serial.Invalid;
        public Serial Container { get; set; } = Serial.Invalid;
        public Point3D? Position { get; set; }
    }

    private int WriteScopedRuntimeLoadFile(string sourcePath, string filteredPath, WorldImportScope scope)
    {
        var records = ReadRuntimeWorldRecords(sourcePath);
        var keep = SelectScopedRuntimeRecords(records, scope);

        Directory.CreateDirectory(Path.GetDirectoryName(filteredPath) ?? ".");
        using var fs = new FileStream(filteredPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new TextSaveWriter(fs);
        writer.WriteHeaderComment($"SphereNet scoped import filter from {Path.GetFileName(sourcePath)} at {DateTime.UtcNow:u}");

        int keptWorldRecords = 0;
        foreach (var record in records)
        {
            bool isWorldRecord = record.Kind != RuntimeWorldRecordKind.Other;
            if (isWorldRecord && !keep.Contains(record))
                continue;

            writer.BeginRecord(record.Section);
            foreach (var (key, value) in record.Properties)
                writer.WriteProperty(key, value);
            writer.EndRecord();

            if (isWorldRecord)
                keptWorldRecords++;
        }

        return keptWorldRecords;
    }

    private static List<RuntimeWorldRecord> ReadRuntimeWorldRecords(string path)
    {
        var records = new List<RuntimeWorldRecord>();
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            RuntimeWorldRecordKind kind = RuntimeWorldRecordKind.Other;
            if (ParseSectionType(section, "WORLDITEM", out _))
                kind = RuntimeWorldRecordKind.Item;
            else if (ParseSectionType(section, "WORLDCHAR", out _))
                kind = RuntimeWorldRecordKind.Character;

            var props = new List<(string Key, string Value)>();
            while (reader.NextProperty(out string key, out string val))
                props.Add((key, val));

            var record = new RuntimeWorldRecord(section, props, kind);
            if (kind != RuntimeWorldRecordKind.Other)
                PopulateRuntimeWorldRecordMetadata(record);
            records.Add(record);
        }

        return records;
    }

    private static void PopulateRuntimeWorldRecordMetadata(RuntimeWorldRecord record)
    {
        foreach (var (key, value) in record.Properties)
        {
            if (key.Equals("SERIAL", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHexOrDec(value, out uint serial))
                    record.Serial = new Serial(serial);
            }
            else if (record.Kind == RuntimeWorldRecordKind.Item &&
                     key.Equals("CONT", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHexOrDec(value, out uint cont))
                    record.Container = new Serial(cont);
            }
            else if (key.Equals("P", StringComparison.OrdinalIgnoreCase))
            {
                if (Point3D.TryParse(value.AsSpan(), out var point))
                    record.Position = point;
            }
        }
    }

    private static HashSet<RuntimeWorldRecord> SelectScopedRuntimeRecords(
        IReadOnlyList<RuntimeWorldRecord> records, WorldImportScope scope)
    {
        var keep = new HashSet<RuntimeWorldRecord>();
        var bySerial = new Dictionary<uint, RuntimeWorldRecord>();

        foreach (var record in records)
        {
            if (record.Serial.IsValid && !bySerial.ContainsKey(record.Serial.Value))
                bySerial.Add(record.Serial.Value, record);
        }

        foreach (var record in records)
        {
            if (record.Kind == RuntimeWorldRecordKind.Character)
            {
                if (scope.IncludeChars && record.Position is { } point && scope.Contains(point))
                    keep.Add(record);
            }
            else if (record.Kind == RuntimeWorldRecordKind.Item)
            {
                if (scope.IncludeItems && !record.Container.IsValid &&
                    record.Position is { } point && scope.Contains(point))
                    keep.Add(record);
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var record in records)
            {
                if (record.Kind != RuntimeWorldRecordKind.Item || keep.Contains(record) ||
                    !scope.IncludeItems || !record.Container.IsValid)
                    continue;

                if (bySerial.TryGetValue(record.Container.Value, out var parent) && keep.Contains(parent))
                    changed |= keep.Add(record);
            }
        } while (changed);

        return keep;
    }

    private static List<Serial> CollectWorldRecordSerials(string path)
    {
        var serials = new List<Serial>();
        var seen = new HashSet<uint>();
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            bool isWorldRecord =
                ParseSectionType(section, "WORLDITEM", out _) ||
                ParseSectionType(section, "WORLDCHAR", out _);

            while (reader.NextProperty(out string key, out string val))
            {
                if (!isWorldRecord || !key.Equals("SERIAL", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryParseHexOrDec(val, out uint serial) && serial != 0 && seen.Add(serial))
                    serials.Add(new Serial(serial));
            }
        }

        return serials;
    }

    private int ReplaceExistingWorldRecords(GameWorld world, IReadOnlyList<Serial> restoreSerials)
    {
        int replaced = 0;
        foreach (var serial in restoreSerials)
        {
            var existing = world.FindObject(serial);
            if (existing == null)
                continue;

            world.DeleteObject(existing);
            if (existing is Item item)
                item.Delete();
            else if (existing is Character ch)
                ch.Delete();
            replaced++;
        }

        if (replaced > 0)
            _logger.LogInformation("Runtime restore pre-replaced {Count} existing object(s)", replaced);
        return replaced;
    }

    private static List<ObjBase> CollectExistingWorldRecords(GameWorld world, IReadOnlyList<Serial> restoreSerials)
    {
        var existing = new List<ObjBase>();
        foreach (var serial in restoreSerials)
        {
            var obj = world.FindObject(serial);
            if (obj != null)
                existing.Add(obj);
        }
        return existing;
    }

    private void TryRollbackRestore(GameWorld world, string backupPath, IReadOnlyList<Serial> restoreSerials,
        AccountManager? accounts, Exception originalException)
    {
        _logger.LogWarning(originalException,
            "Runtime restore failed after replace; rolling back {Count} serial(s) from {Backup}",
            restoreSerials.Count, Path.GetFileName(backupPath));

        try
        {
            ReplaceExistingWorldRecords(world, restoreSerials);
            var (items, chars) = LoadFile(world, backupPath, accounts);
            _logger.LogInformation("Runtime restore rollback complete: {Items} item(s), {Chars} char(s)",
                items, chars);
        }
        catch (Exception rollbackEx)
        {
            _logger.LogError(rollbackEx, "Runtime restore rollback failed");
        }
    }

    private int LoadCharFile(GameWorld world, string path, List<(Character, string)> accountLinks,
        List<(Character, Serial, byte)> equipLinks)
    {
        int count = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var reader = SaveIO.OpenReader(path);

        while (reader.NextRecord(out string section))
        {
            if (!ParseSectionType(section, "WORLDCHAR", out string? defname))
            {
                while (reader.NextProperty(out _, out _)) { /* skip */ }
                continue;
            }

            var ch = world.CreateCharacter();
            string? accountName = null;
            bool charHasUuid = false;
            bool skipChar = false;

            if (defname != null && ApplyCharDefFromName != null)
            {
                ApplyCharDefFromName(ch, defname);
            }
            else if (defname != null && ResolveCharDef != null)
            {
                ushort bodyId = ResolveCharDef(defname);
                if (bodyId != 0)
                {
                    ch.BodyId = bodyId;
                    ch.OBody = bodyId;
                    ch.BaseId = bodyId;
                }
                else
                    _logger.LogDebug("Unknown char defname '{DefName}' — BodyId stays default", defname);
            }

            while (reader.NextProperty(out string key, out string val))
            {
                // Drain with no side effects once a duplicate SERIAL/UUID retired
                // this instance — the UUID branch must not re-index the deleted
                // char (M8; see the item loop).
                if (skipChar)
                    continue;
                if (key.Equals("SERIAL", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseHexOrDec(val, out uint serial))
                    {
                        var oldUid = ch.Uid;
                        var newUid = new Serial(serial);
                        if (!world.TryReRegisterObject(ch, oldUid, newUid, out var existing))
                        {
                            _logger.LogWarning(
                                "Skipping duplicate character serial 0x{Serial:X8} in {File}: existing={ExistingType}",
                                serial, Path.GetFileName(path), existing?.GetType().Name ?? "unknown");
                            world.DeleteObject(ch);
                            skipChar = true;
                        }
                    }
                    continue;
                }

                if (key.Equals("UUID", StringComparison.OrdinalIgnoreCase))
                {
                    if (Guid.TryParse(val, out Guid uuid))
                    {
                        var oldUuid = ch.Uuid;
                        ch.Uuid = uuid;
                        if (!world.TryReIndexUuid(ch, oldUuid, out var existing))
                        {
                            _logger.LogWarning(
                                "Skipping duplicate character UUID {Uuid} for serial 0x{Serial:X8} in {File}: existing=0x{Existing:X8}",
                                uuid, ch.Uid.Value, Path.GetFileName(path), existing?.Uid.Value ?? 0);
                            world.DeleteObject(ch);
                            skipChar = true;
                        }
                        charHasUuid = true;
                    }
                    continue;
                }

                if (key.Equals("ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("TAG.ACCOUNT", StringComparison.OrdinalIgnoreCase))
                {
                    accountName = val;
                    if (!key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase))
                        ch.SetTag("ACCOUNT", val);
                    continue;
                }

                if (key.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryParseEquipProperty(key, val, out var itemSerial, out byte layer))
                {
                    equipLinks.Add((ch, itemSerial, layer));
                    continue;
                }

                ApplyCharProperty(ch, key, val);
            }
            if (skipChar)
                continue;

            CharDefHelper.EnsureDisplayBody(ch, DefinitionLoader.StaticResources);
            ch.RestoreSummonExpiry();
            ch.ClearTransientVisualState();

            if (!charHasUuid)
                _migratedUuids++;

            world.PlaceCharacter(ch, ch.Position);

            // A Ridden creature is out of play: a mount under its rider, or a pet
            // parked by the stable master or a figurine (Source-X STATF_RIDDEN +
            // disconnected). Placement puts every character into a sector, so
            // without this it would come back from the save visible, tickable and
            // attackable while its owner still believes it is stabled.
            if (ch.IsStatFlag(SphereNet.Core.Enums.StatFlag.Ridden))
                world.HideFromSector(ch);

            if (!string.IsNullOrEmpty(accountName))
                accountLinks.Add((ch, accountName));
            count++;

            if (count % 50_000 == 0)
                _logger.LogInformation("  Loading chars... {Count} ({Elapsed}s)",
                    count, sw.Elapsed.TotalSeconds.ToString("F1"));
        }
        return count;
    }

    private void ApplyItemProperty(Item item, string key, string val)
    {
        switch (key.ToUpperInvariant())
        {
            case "ID":
                if (TryParseHexOrDec(val, out uint id))
                    item.BaseId = (ushort)id;
                break;
            case "TYPE":
                item.TrySetProperty(key, val);
                break;
            case "AMOUNT":
                if (ushort.TryParse(val, out ushort a))
                    item.Amount = a;
                break;
            case "CONT":
                if (TryParseHexOrDec(val, out uint cont))
                    item.ContainedIn = new Serial(cont);
                break;
            default:
                if (!item.TrySetProperty(key, val))
                    item.SetTag("SAVE." + key, val);
                break;
        }
    }

    private void ApplyCharProperty(Character ch, string key, string val)
    {
        string upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "BODY":
                if (TryParseHexOrDec(val, out uint body))
                {
                    ushort bid = (ushort)body;
                    // Saves sometimes store CHARDEF hash (e.g. 0x03DB) in BODY.
                    if (ch.CharDefIndex != 0 && bid == (ushort)ch.CharDefIndex)
                        break;
                    ch.BodyId = bid;
                }
                break;
            case "CHARDEFINDEX":
                if (TryParseHexOrDec(val, out uint cdi))
                    ch.CharDefIndex = (int)cdi;
                break;
            case "OBODY":
                if (TryParseHexOrDec(val, out uint obody))
                    ch.OBody = (ushort)obody;
                break;
            case "OSKIN":
                if (TryParseHexOrDec(val, out uint oskin))
                    ch.OSkin = (ushort)oskin;
                break;
            case "ISPLAYER":
                ch.IsPlayer = val == "1";
                break;
            case "CREATE":
                // Source-X CChar::r_LoadVal CHC_CREATE: the stored value is the
                // character's AGE at save time in tenths of a second — creation
                // time = now - age (feeds the MinCharDeleteTime delete gate).
                if (long.TryParse(val, out long ageTenths) && ageTenths >= 0)
                    ch.CreatedUtcSeconds =
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ageTenths / 10;
                break;
            // Source-X r_LoadVal treats OSTR/ODEX/OINT and STR/DEX/INT as the
            // same key — both call Stat_SetBase. Classic Sphere saves persist
            // ONLY the O-variants, so mirroring them into the O-fields alone
            // loaded every legacy NPC with base stats of 0.
            case "OSTR":
                if (short.TryParse(val, out short ostr))
                {
                    ch.OStr = ostr;
                    ch.Str = ostr;
                }
                break;
            case "ODEX":
                if (short.TryParse(val, out short odex))
                {
                    ch.ODex = odex;
                    ch.Dex = odex;
                }
                break;
            case "OINT":
                if (short.TryParse(val, out short oint))
                {
                    ch.OInt = oint;
                    ch.Int = oint;
                }
                break;
            case "SPELLEFFECT":
                ch.AddPendingSpellEffectRecord(val);
                break;
            default:
                if (upper.StartsWith("SKILL[", StringComparison.Ordinal) && upper.Contains(']'))
                {
                    var idx = upper.IndexOf('[');
                    var end = upper.IndexOf(']');
                    if (int.TryParse(upper.AsSpan(idx + 1, end - idx - 1), out int skillIdx)
                        && skillIdx >= 0 && skillIdx < (int)SphereNet.Core.Enums.SkillType.Qty)
                    {
                        var parts = val.Split(',');
                        if (parts.Length >= 1 && ushort.TryParse(parts[0], out ushort sv))
                            ch.SetSkill((SphereNet.Core.Enums.SkillType)skillIdx, sv);
                        if (parts.Length >= 2 && byte.TryParse(parts[1], out byte lockVal))
                            ch.SetSkillLock((SphereNet.Core.Enums.SkillType)skillIdx, lockVal);
                    }
                    break;
                }
                if (upper.StartsWith("EQUIP[", StringComparison.Ordinal))
                {
                    break;
                }
                if (upper == "MEMORY")
                {
                    var parts = val.Split(',');
                    if (parts.Length >= 2 && TryParseHexOrDec(parts[0], out uint mUid) &&
                        ushort.TryParse(parts[1], out ushort mFlags))
                    {
                        var mem = ch.Memory_CreateObj(new Serial(mUid), (SphereNet.Core.Enums.MemoryType)mFlags);
                        // Extended format (W-D): remaining timeout ms (-1 = none)
                        // + original unix creation stamp. Older 2-field saves keep
                        // the fresh timeout CreateObj armed.
                        if (parts.Length >= 3 && long.TryParse(parts[2], out long memRemainMs))
                        {
                            if (memRemainMs < 0)
                                mem.SetTimeout(-1);
                            else
                                mem.SetTimeout(Environment.TickCount64 + Math.Max(1, memRemainMs));
                        }
                        if (parts.Length >= 4 && uint.TryParse(parts[3], out uint memCreated) && memCreated != 0)
                            mem.More1 = memCreated;
                    }
                    break;
                }
                if (upper == "ATTACKER")
                {
                    var parts = val.Split(',');
                    if (parts.Length >= 2 && TryParseHexOrDec(parts[0], out uint aUid) &&
                        int.TryParse(parts[1], out int aDamage))
                    {
                        bool ignored = parts.Length >= 3 && parts[2] == "1";
                        ch.CombatState.RestoreAttacker(new Serial(aUid), aDamage, ignored);
                    }
                    break;
                }
                if (TryResolveSkillName(upper, out SkillType skill))
                {
                    if (ushort.TryParse(val, out ushort sv))
                        ch.SetSkill(skill, sv);
                    break;
                }
                if (!ch.TrySetProperty(key, val))
                    ch.SetTag("SAVE." + key, val);
                break;
        }
    }

    private static bool TryParseEquipProperty(string key, string value, out Serial itemSerial, out byte layer)
    {
        itemSerial = Serial.Invalid;
        layer = 0;

        string upper = key.ToUpperInvariant();
        if (!upper.StartsWith("EQUIP[", StringComparison.Ordinal) || !upper.EndsWith(']'))
            return false;

        if (!byte.TryParse(upper.AsSpan("EQUIP[".Length, upper.Length - "EQUIP[".Length - 1), out layer))
            return false;

        if (!TryParseHexOrDec(value, out uint serial) || serial == 0)
            return false;

        itemSerial = new Serial(serial);
        return true;
    }

    private void LoadDataFile(GameWorld world, string path)
    {
        if (!File.Exists(path)) return;
        using var reader = Formats.SaveIO.OpenReader(path);
        int globals = 0, lists = 0;

        while (reader.NextRecord(out string section))
        {
            string upper = section.ToUpperInvariant();

            if (upper == "GLOBALS")
            {
                while (reader.NextProperty(out string key, out string val))
                {
                    world.SetGlobalVar(key, val);
                    globals++;
                }
            }
            else if (upper.StartsWith("LIST ", StringComparison.OrdinalIgnoreCase))
            {
                string listName = section[5..].Trim();
                var list = world.GetOrCreateList(listName);
                while (reader.NextProperty(out string key, out string val))
                {
                    if (key.Equals("ELEM", StringComparison.OrdinalIgnoreCase))
                        list.Add(val);
                }
                lists++;
            }
            else if (upper.StartsWith("GMPAGE", StringComparison.OrdinalIgnoreCase))
            {
                string account = "", reason = "", handler = "", status = "open";
                long created = 0;
                while (reader.NextProperty(out string key, out string val))
                {
                    switch (key.ToUpperInvariant())
                    {
                        case "ACCOUNT": account = val; break;
                        case "REASON": reason = val; break;
                        case "HANDLER": handler = val; break;
                        case "STATUS": status = val; break;
                        case "TIME": case "CREATED": long.TryParse(val, out created); break;
                    }
                }
                world.AddGmPage(new GameWorld.GmPageRecord(account, reason, handler, status, created));
            }
            else if (upper == "DOORS")
            {
                while (reader.NextProperty(out string key, out string val))
                {
                    if (key.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = val.Split(',');
                        if (parts.Length == 4 &&
                            byte.TryParse(parts[0], out byte dMap) &&
                            short.TryParse(parts[1], out short dX) &&
                            short.TryParse(parts[2], out short dY) &&
                            sbyte.TryParse(parts[3], out sbyte dZ))
                        {
                            world.SetMapStaticDoorOpen(dMap, dX, dY, dZ, true);
                        }
                    }
                }
            }
            else if (upper == "SECTORS")
            {
                while (reader.NextProperty(out string key, out string val))
                {
                    // ENV is the original line, whose weather byte meant 0=dry, 1=rain,
                    // 2=snow. ENV2 carries Source-X WEATHER_TYPE codes (255=dry, 0=rain)
                    // plus an explicit light-override flag. Reading a legacy ENV line as
                    // if it were the new scale would turn every stored dry sector into
                    // rain, so the two keys keep their own meanings.
                    bool legacy = key.Equals("ENV", StringComparison.OrdinalIgnoreCase);
                    if (!legacy && !key.Equals("ENV2", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parts = val.Split(',');
                    int minFields = legacy ? 8 : 9;
                    if (parts.Length < minFields ||
                        !int.TryParse(parts[0], out int sMap) ||
                        !int.TryParse(parts[1], out int sX) ||
                        !int.TryParse(parts[2], out int sY) ||
                        !byte.TryParse(parts[3], out byte sWeather) ||
                        !byte.TryParse(parts[4], out byte sSeason) ||
                        !byte.TryParse(parts[5], out byte sLight))
                        continue;

                    bool lightPinned;
                    short sRain, sCold;
                    if (legacy)
                    {
                        sWeather = sWeather switch
                        {
                            0 => SphereNet.Game.World.Sectors.Sector.WeatherDry,
                            1 => (byte)WeatherType.Rain,
                            2 => (byte)WeatherType.Snow,
                            _ => SphereNet.Game.World.Sectors.Sector.WeatherDry,
                        };
                        // The old line had nowhere to say the light was pinned; a
                        // non-zero stored light was the only way it could be.
                        lightPinned = sLight != 0;
                        if (!short.TryParse(parts[6], out sRain) ||
                            !short.TryParse(parts[7], out sCold))
                            continue;
                    }
                    else
                    {
                        lightPinned = parts[6].Trim() == "1";
                        if (!short.TryParse(parts[7], out sRain) ||
                            !short.TryParse(parts[8], out sCold))
                            continue;
                    }

                    var sector = world.GetSector(sMap, sX, sY);
                    if (sector == null) continue;
                    sector.Weather = sWeather;
                    sector.Season = sSeason;
                    if (lightPinned) sector.Light = sLight;
                    else sector.ClearLightOverride();
                    sector.RainChance = sRain;
                    sector.ColdChance = sCold;
                }
            }
            else if (upper.StartsWith("WORLDSCRIPT ", StringComparison.OrdinalIgnoreCase))
            {
                string scriptName = section[12..].Trim();
                while (reader.NextProperty(out string key, out string val))
                {
                    world.SetGlobalVar($"SCRIPT.{scriptName}.{key}", val);
                }
            }
            else if (upper == "SPHERE")
            {
                while (reader.NextProperty(out string key, out string val))
                {
                    if (key.Equals("VERSION", StringComparison.OrdinalIgnoreCase))
                        _logger.LogInformation("Save file version: {Version}", val);
                }
            }
            else
            {
                // TIMERF, EOF — skip properties
                while (reader.NextProperty(out _, out _)) { }
            }
        }

        if (globals > 0 || lists > 0)
            _logger.LogInformation("Data: {Path} -> {Globals} globals, {Lists} lists",
                Path.GetFileName(path), globals, lists);
    }

    /// <summary>Check whether <paramref name="section"/> matches
    /// <paramref name="expectedType"/> (case-insensitive), handling both
    /// SphereNet bare headers (<c>WORLDITEM</c>) and classic Sphere headers
    /// with a defname suffix (<c>WORLDITEM i_backpack</c>).</summary>
    private static bool ParseSectionType(string section, string expectedType, out string? defname)
    {
        defname = null;
        if (section.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
            return true;
        if (section.StartsWith(expectedType, StringComparison.OrdinalIgnoreCase)
            && section.Length > expectedType.Length
            && section[expectedType.Length] == ' ')
        {
            defname = section[(expectedType.Length + 1)..].Trim();
            if (defname.Length == 0) defname = null;
            return true;
        }
        return false;
    }

    private static bool TryParseHexOrDec(string val, out uint result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;

        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);

        if (val.StartsWith('0') && val.Length > 1 && !val.Contains('.'))
            return uint.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);

        return uint.TryParse(val, out result);
    }

    private static bool TryResolveSkillName(string upper, out SkillType skill)
    {
        if (_sphereSkillNames.TryGetValue(upper, out skill))
            return true;
        if (Enum.TryParse(upper, true, out skill) && Enum.IsDefined(skill))
            return true;
        skill = SkillType.None;
        return false;
    }

    private static readonly Dictionary<string, SkillType> _sphereSkillNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EVALUATINGINTEL"] = SkillType.EvalInt,
        ["EVALUATINGINTELLECT"] = SkillType.EvalInt,
        ["ITEMID"] = SkillType.ItemId,
        ["ITEMIDENTIFICATION"] = SkillType.ItemId,
        ["MACEFIGHTING"] = SkillType.MaceFighting,
        ["ANIMALLORE"] = SkillType.AnimalLore,
        ["ARMSLOREBOWCRAFT"] = SkillType.Bowcraft,
        ["DETECTINGHIDDEN"] = SkillType.DetectingHidden,
        ["MAGICRESISTANCE"] = SkillType.MagicResistance,
        ["SPIRITSPEAK"] = SkillType.SpiritSpeak,
        ["TASTEID"] = SkillType.TasteId,
        ["REMOVETRAP"] = SkillType.RemoveTrap,
    };
}
