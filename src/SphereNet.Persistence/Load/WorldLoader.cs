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
/// </summary>
public sealed class WorldLoader
{
    private readonly ILogger<WorldLoader> _logger;

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

            foreach (string path in itemPaths)
            {
                int n = LoadItemFile(world, path, itemContLinks);
                itemCount += n;
                _logger.LogInformation("Items: {Path} -> {Count}", Path.GetFileName(path), n);
            }

            foreach (string path in charPaths)
            {
                int n = LoadCharFile(world, path, charAccountLinks);
                charCount += n;
                _logger.LogInformation("Chars: {Path} -> {Count}", Path.GetFileName(path), n);
            }
        }
        finally
        {
            world.SuppressDirtyNotify = false;
            world.DrainDirtyObjects();
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
            if (!section.Equals("WORLDITEM", StringComparison.OrdinalIgnoreCase))
            {
                while (reader.NextProperty(out _, out _)) { /* skip */ }
                continue;
            }

            var item = world.CreateItem();
            Serial contSerial = Serial.Invalid;
            byte layer = 0;

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
                ApplyItemProperty(item, key, val);
            }

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
            if (!section.Equals("WORLDCHAR", StringComparison.OrdinalIgnoreCase))
            {
                while (reader.NextProperty(out _, out _)) { /* skip */ }
                continue;
            }

            var ch = world.CreateCharacter();
            string? accountName = null;

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

                if (key.Equals("ACCOUNT", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("TAG.ACCOUNT", StringComparison.OrdinalIgnoreCase))
                {
                    accountName = val;
                    if (!key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase))
                        ch.SetTag("ACCOUNT", val);
                    continue;
                }

                ApplyCharProperty(ch, key, val);
            }

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
            case "OBODY":
                if (TryParseHexOrDec(val, out uint obody))
                    ch.OBody = (ushort)obody;
                break;
            case "ISPLAYER":
                ch.IsPlayer = val == "1";
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
                    // Deferred — equipment linking happens via CONT resolution pass.
                    break;
                }
                ch.TrySetProperty(key, val);
                break;
        }
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
}
