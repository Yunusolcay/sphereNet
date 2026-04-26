using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
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

    /// <inheritdoc cref="ResolveItemDef"/>
    public Func<string, ushort>? ResolveCharDef { get; set; }

    public WorldLoader(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<WorldLoader>();
    }

    /// <summary>Load world data from save files.</summary>
    public (int Items, int Chars) Load(GameWorld world, string savePath, AccountManager? accounts = null)
    {
        int itemCount = 0, charCount = 0;

        var charAccountLinks = new List<(Character Char, string AccountName)>();
        var itemContLinks = new List<(Item Item, Serial ContSerial, byte Layer)>();

        // Suppress dirty notifications for the whole bulk materialization.
        // Otherwise the dirty set balloons to millions of entries and stalls
        // the fast-path drain during login. Clients get a view resync at
        // login anyway.
        world.SuppressDirtyNotify = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var itemPaths = ResolveSaveFiles(savePath, "sphereworld");
            var charPaths = ResolveSaveFiles(savePath, "spherechars");
            var staticPaths = ResolveSaveFiles(savePath, "spherestatics");
            var multiPaths = ResolveSaveFiles(savePath, "spheremultis");

            foreach (string path in itemPaths)
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

            // Sphere/Source-X sphereworld.scp contains both [WORLDITEM] and
            // [WORLDCHAR] sections. LoadItemFile above only processed items;
            // now make a second pass over the same files to pick up NPCs.
            foreach (string path in itemPaths)
            {
                int n = LoadCharFile(world, path, charAccountLinks);
                if (n > 0)
                {
                    charCount += n;
                    _logger.LogInformation("WorldChars: {Path} -> {Count}", Path.GetFileName(path), n);
                }
            }

            foreach (string path in charPaths)
            {
                int n = LoadCharFile(world, path, charAccountLinks);
                charCount += n;
                _logger.LogInformation("Chars: {Path} -> {Count}", Path.GetFileName(path), n);
            }

            // spheredata.scp — globals, lists, world scripts
            var dataPaths = ResolveSaveFiles(savePath, "spheredata");
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
                parentChar.Equip(item, (Layer)layer);
                containedCount++;
            }
            else if (parent is Item parentItem)
            {
                parentItem.AddItem(item);
                containedCount++;
            }
            else
            {
                world.PlaceItem(item, item.Position);
            }
        }

        _logger.LogInformation("World loaded: {Items} items, {Chars} chars, {Contained} contained/equipped in {Elapsed}s",
            itemCount, charCount, containedCount, sw.Elapsed.TotalSeconds.ToString("F1"));

        if (_migratedUuids > 0)
            _logger.LogInformation("UUID migration: {Count} objects had no UUID — auto-generated (will persist on next save)",
                _migratedUuids);

        return (itemCount, charCount);
    }

    /// <summary>Resolve actual on-disk paths for a logical save name
    /// (e.g. "sphereworld"). Priority: manifest → format probes. Returns an
    /// empty list if nothing exists.</summary>
    private List<string> ResolveSaveFiles(string savePath, string baseName)
    {
        string manifestPath = ShardManifest.PathFor(savePath, baseName);
        var manifest = ShardManifest.TryLoad(manifestPath);
        if (manifest != null && manifest.Files.Count > 0)
        {
            var list = new List<string>(manifest.Files.Count);
            foreach (var name in manifest.Files)
            {
                string full = Path.Combine(savePath, name);
                if (File.Exists(full)) list.Add(full);
                else _logger.LogWarning("Manifest references missing shard {File}", name);
            }
            _logger.LogInformation("Loaded manifest {Base}: format={Format}, shards={Count}",
                baseName, manifest.Format, list.Count);
            return list;
        }

        // No manifest — probe for single-file variants in priority order
        // (most-specific format first so a gzip survives alongside a stale .scp).
        foreach (string ext in new[] { ".sbin.gz", ".sbin", ".scp.gz", ".scp" })
        {
            string candidate = Path.Combine(savePath, baseName + ext);
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
            }

            bool hasUuid = false;
            bool hasId = false;
            while (reader.NextProperty(out string key, out string val))
            {
                string upper = key.ToUpperInvariant();
                if (upper == "SERIAL")
                {
                    if (TryParseHexOrDec(val, out uint serial))
                    {
                        var oldUid = item.Uid;
                        world.ReRegisterObject(item, oldUid, new Serial(serial));
                    }
                    continue;
                }
                if (upper == "UUID")
                {
                    if (Guid.TryParse(val, out Guid uuid))
                    {
                        var oldUuid = item.Uuid;
                        item.Uuid = uuid;
                        world.ReIndexUuid(item, oldUuid);
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

    private int LoadCharFile(GameWorld world, string path, List<(Character, string)> accountLinks)
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

            if (defname != null && ResolveCharDef != null)
            {
                ushort bodyId = ResolveCharDef(defname);
                if (bodyId != 0)
                {
                    ch.BodyId = bodyId;
                    ch.OBody = bodyId;
                }
                else
                    _logger.LogDebug("Unknown char defname '{DefName}' — BodyId stays default", defname);
            }

            while (reader.NextProperty(out string key, out string val))
            {
                if (key.Equals("SERIAL", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseHexOrDec(val, out uint serial))
                    {
                        var oldUid = ch.Uid;
                        world.ReRegisterObject(ch, oldUid, new Serial(serial));
                    }
                    continue;
                }

                if (key.Equals("UUID", StringComparison.OrdinalIgnoreCase))
                {
                    if (Guid.TryParse(val, out Guid uuid))
                    {
                        var oldUuid = ch.Uuid;
                        ch.Uuid = uuid;
                        world.ReIndexUuid(ch, oldUuid);
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

                ApplyCharProperty(ch, key, val);
            }
            if (!charHasUuid)
                _migratedUuids++;

            world.PlaceCharacter(ch, ch.Position);
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
                if (ushort.TryParse(val, out ushort t))
                    item.ItemType = (ItemType)t;
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
                item.TrySetProperty(key, val);
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
                    ch.BodyId = (ushort)body;
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
                    ch.OBody = (ushort)oskin;
                break;
            case "ISPLAYER":
                ch.IsPlayer = val == "1";
                break;
            case "OSTR":
                if (short.TryParse(val, out short ostr))
                    ch.Str = ostr;
                break;
            case "ODEX":
                if (short.TryParse(val, out short odex))
                    ch.Dex = odex;
                break;
            case "OINT":
                if (short.TryParse(val, out short oint))
                    ch.Int = oint;
                break;
            default:
                if (upper.StartsWith("SKILL[") && upper.Contains(']'))
                {
                    var idx = upper.IndexOf('[');
                    var end = upper.IndexOf(']');
                    if (int.TryParse(upper.AsSpan(idx + 1, end - idx - 1), out int skillIdx))
                    {
                        var parts = val.Split(',');
                        if (parts.Length >= 1 && ushort.TryParse(parts[0], out ushort sv))
                            ch.SetSkill((SphereNet.Core.Enums.SkillType)skillIdx, sv);
                        if (parts.Length >= 2 && byte.TryParse(parts[1], out byte lockVal))
                            ch.SetSkillLock((SphereNet.Core.Enums.SkillType)skillIdx, lockVal);
                    }
                    break;
                }
                if (upper.StartsWith("EQUIP["))
                {
                    break;
                }
                if (upper == "MEMORY")
                {
                    var parts = val.Split(',');
                    if (parts.Length >= 2 && TryParseHexOrDec(parts[0], out uint mUid) &&
                        ushort.TryParse(parts[1], out ushort mFlags))
                    {
                        ch.Memory_CreateObj(new Serial(mUid), (SphereNet.Core.Enums.MemoryType)mFlags);
                    }
                    break;
                }
                if (TryResolveSkillName(upper, out SkillType skill))
                {
                    if (ushort.TryParse(val, out ushort sv))
                        ch.SetSkill(skill, sv);
                    break;
                }
                ch.TrySetProperty(key, val);
                break;
        }
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
            else if (upper.StartsWith("WORLDSCRIPT ", StringComparison.OrdinalIgnoreCase))
            {
                string scriptName = section[12..].Trim();
                while (reader.NextProperty(out string key, out string val))
                {
                    world.SetGlobalVar($"SCRIPT.{scriptName}.{key}", val);
                }
            }
            else
            {
                // SPHERE, TIMERF, EOF — skip properties
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
