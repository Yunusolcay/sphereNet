using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static void PerformScriptResync()
    {
        _log.LogInformation("ReSync: scanning for modified script files...");
        _systemHooks.DispatchServer("resync_start", _serverHookContext);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int reloaded = _resources.Resync();

        if (reloaded == 0)
        {
            _log.LogInformation("ReSync: no modified files found.");
            BroadcastSysMessage("ReSync: no changes detected.");
            return;
        }

        // Re-process definitions from reloaded resources
        var defLoader = new DefinitionLoader(_resources, _spellRegistry);
        defLoader.LoadAll();
        int recipeCount = _craftingEngine.LoadRecipesFromDefs(_resources);
        if (recipeCount > 0)
            _log.LogInformation("ReSync: reloaded {Count} craft recipes from SKILLMAKE definitions.", recipeCount);
        PlaceTeleporters();
        _commands?.InvalidateAreaCache();
        if (_commands != null)
        {
            int scriptCmdCount = _commands.LoadScriptCommandPrivileges(_resources);
            _log.LogInformation("ReSync: reloaded {Count} script command privilege entries.", scriptCmdCount);
        }

        // Reloaded files may add or remove [ON=@X] hooks and f_onchar_*/
        // f_onitem_* fallback functions; refresh the used-trigger gates so
        // hot paths see the new state without a restart.
        _triggerDispatcher?.BuildUsedTriggerCache();

        sw.Stop();
        _log.LogInformation(
            "ReSync complete: {Files} files reloaded, {Spells} spells, {Items} itemdefs, {Chars} chardefs, {Skills} skilldefs ({Ms}ms)",
            reloaded, defLoader.SpellsLoaded, defLoader.ItemDefsLoaded, defLoader.CharDefsLoaded,
            defLoader.SkillDefsLoaded, sw.ElapsedMilliseconds);

        BroadcastSysMessage($"ReSync: {reloaded} script files reloaded in {sw.ElapsedMilliseconds}ms.");
        _systemHooks.DispatchServer("resync_success", _serverHookContext,
            reloaded.ToString(), reloaded);
        SphereNet.Scripting.Parsing.ScriptFile.ClearFileCache();
    }

    private static void BroadcastSysMessage(string message)
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsPlaying)
                client.SysMessage(message);
        }
    }

    private static void SyncOnlineAccountPrivLevel(string accountName, PrivLevel level)
    {
        foreach (var client in _clients.Values)
        {
            if (!client.IsPlaying || client.Account == null || client.Character == null) continue;
            if (!client.Account.Name.Equals(accountName, StringComparison.OrdinalIgnoreCase)) continue;

            client.Account.PrivLevel = level;
            client.SysMessage($"Your privilege level is now {level} ({(int)level}).");
            _log.LogInformation("Online privilege sync: account={Account} char=0x{Char:X8} -> {Level}",
                accountName, client.Character.Uid.Value, level);
        }
    }

    private static void InitializeSpawnItems()
    {
        int spawns = 0;
        int fromTag = 0;
        int typeInherited = 0;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not SphereNet.Game.Objects.Items.Item item)
                continue;

            if (item.BaseId != 0 && item.ItemType == ItemType.Normal)
            {
                // Sphere saves only write TYPE when it DIFFERS from the base
                // itemdef — a saved TYPE must therefore win. Unconditional
                // inheritance clobbered e.g. the 56T i_worldgem_bit item
                // spawners (saved TYPE=t_spawn_item, itemdef t_spawn_char).
                var idef = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(item.BaseId);
                if (idef != null && idef.Type != ItemType.Normal)
                {
                    typeInherited++;
                    item.ItemType = idef.Type;
                }
            }

            // Sphere saves don't write TYPE — detect spawn items by SPAWNID tag.
            // But SPAWNID does not mean "char spawner": upstream writes it for an ITEM
            // spawner too (r_Write, CCSpawn.cpp:1105). Overwriting a type that is
            // already a known spawner kind turned a t_spawn_item record from a Source-X
            // save into a char spawner and built the wrong component for it.
            string? spawnId = item.Tags.Get("SPAWNID");
            if (!string.IsNullOrEmpty(spawnId) &&
                item.ItemType is not (ItemType.SpawnChar or ItemType.SpawnItem or ItemType.SpawnChampion))
            {
                item.ItemType = ItemType.SpawnChar;
                fromTag++;
            }

            // Source-X parity: spawn items are always invisible (ATTR_INVIS).
            // Old Sphere saves may omit ATTR for spawn items detected via SPAWNID tag.
            if (item.ItemType == ItemType.SpawnChar && !item.IsAttr(SphereNet.Core.Enums.ObjAttributes.Invis))
                item.SetAttr(SphereNet.Core.Enums.ObjAttributes.Invis);

            // Item spawners (TYPE=t_spawn_item) were skipped here entirely, so
            // imported resource-node spawners (56T mining veins etc.) never
            // got their component and stayed dead.
            if (item.ItemType is not (ItemType.SpawnChar or ItemType.SpawnChampion or ItemType.SpawnItem))
                continue;

            long loadedTimeout = item.Timeout;
            item.InitializeSpawnComponent(_world, _resources, loadedTimeout);

            // Apply Sphere SPAWNID/TIMELO/TIMEHI/MAXDIST tags
            if (!string.IsNullOrEmpty(spawnId) && item.SpawnChar != null)
            {
                item.SpawnChar.SetFromDefName(spawnId, _resources);

                string? timeLo = item.Tags.Get("TIMELO");
                string? timeHi = item.Tags.Get("TIMEHI");
                if (timeLo != null || timeHi != null)
                {
                    int.TryParse(timeLo ?? "15", out int lo);
                    int.TryParse(timeHi ?? "30", out int hi);
                    item.SpawnChar.SetDelay(lo, hi);
                }

                string? maxDist = item.Tags.Get("MAXDIST");
                if (maxDist != null && int.TryParse(maxDist, out int dist))
                    item.SpawnChar.SpawnRange = dist;

                // Classic Sphere SPAWNID convention: each ADDOBJ line is a spawn
                // slot, so the ADDOBJ count raises MaxCount. Source-X MORE1/AMOUNT
                // spawners take their cap from AMOUNT instead and must NOT do this
                // (the general re-link below never touches MaxCount), or a save that
                // over-accumulated would lock the inflated count in permanently.
                string? spawnSlots = item.Tags.Get("ADDOBJ");
                if (!string.IsNullOrEmpty(spawnSlots))
                {
                    int slots = spawnSlots.Split(',', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries).Length;
                    if (slots > item.SpawnChar.MaxCount)
                        item.SpawnChar.MaxCount = slots;
                }

                // Tags override MOREP — reset timer with final values
                item.SpawnChar.ResetTimer(loadedTimeout);
            }

            spawns++;
        }
        if (spawns > 0)
            _log.LogInformation("Initialized {Count} spawn items ({FromTag} from SPAWNID tag, {TypeInh} type inherited from ITEMDEF)",
                spawns, fromTag, typeInherited);

        int brainFixed = 0;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not SphereNet.Game.Objects.Characters.Character ch) continue;
            if (ch.IsPlayer || ch.NpcBrain != SphereNet.Core.Enums.NpcBrainType.None) continue;

            var cdef = SphereNet.Game.Definitions.DefinitionLoader.GetCharDef(ch.CharDefIndex);
            if (cdef != null && cdef.NpcBrain != SphereNet.Core.Enums.NpcBrainType.None)
                ch.NpcBrain = cdef.NpcBrain;
            else
                ch.NpcBrain = SphereNet.Core.Enums.NpcBrainType.Monster;
            brainFixed++;
        }
        if (brainFixed > 0)
            _log.LogInformation("Inherited NpcBrain from CHARDEF for {Count} NPCs", brainFixed);
    }

    /// <summary>One-pass boot repair for NPCs whose BASE stats are 0. Older
    /// builds loaded classic OSTR-only saves into the O-fields alone and then
    /// PERSISTED the zeroed STR in native format, so the damage survives the
    /// loader fix. Restore from the O-mirror when present, otherwise re-run
    /// the chardef @Create (classic packs assign stats there), then top the
    /// vitals off. Requires the trigger dispatcher — call after engine wiring.</summary>
    private static void RepairZeroStatNpcs()
    {
        int repaired = 0;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not SphereNet.Game.Objects.Characters.Character ch) continue;
            if (ch.IsPlayer || ch.IsDeleted || ch.Str > 0) continue;

            if (ch.OStr > 0)
            {
                ch.Str = ch.OStr;
                if (ch.ODex > 0) ch.Dex = ch.ODex;
                if (ch.OInt > 0) ch.Int = ch.OInt;
            }
            else
            {
                _triggerDispatcher?.FireCharTrigger(ch,
                    SphereNet.Core.Enums.CharTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch });
            }

            // Whatever the source, a live NPC never stands on zero stats.
            if (ch.Str <= 0) ch.Str = 50;
            if (ch.Dex <= 0) ch.Dex = 50;
            if (ch.Int <= 0) ch.Int = 20;
            if (ch.Hits <= 1 || ch.Hits > ch.MaxHits) ch.Hits = ch.MaxHits;
            if (ch.Stam <= 0) ch.Stam = ch.MaxStam;
            if (ch.Mana <= 0) ch.Mana = ch.MaxMana;
            repaired++;
        }
        if (repaired > 0)
            _log.LogInformation(
                "[boot_repair] restored base stats for {Count} NPCs saved with STR=0 by an older build",
                repaired);

        // Resource-marker worldgems saved by older builds carry no decay
        // (TIMER=-1 forever). Arm each with one regen period — fully refilled
        // by then, so expiring is lossless (Source-X MoveToDecay lifecycle).
        int armed = 0;
        long decayAt = Environment.TickCount64 + 3_600_000L;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is not SphereNet.Game.Objects.Items.Item it || it.IsDeleted) continue;
            if (it.DecayTime > 0) continue;
            if (!it.TryGetTag("RESOURCE_MARKER", out string? mk) || mk != "1") continue;
            it.DecayTime = decayAt;
            armed++;
        }
        if (armed > 0)
            _log.LogInformation(
                "[boot_repair] armed decay on {Count} immortal resource-marker worldgems", armed);
    }

    private static bool TryParseHexOrDecUInt(string val, out uint result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (val.StartsWith('0') && val.Length > 1 && !val.Contains('.'))
            return uint.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);
        return uint.TryParse(val, out result);
    }

    /// <summary>Run the world invariant auditor over the freshly loaded world and
    /// surface any inconsistencies in the log. Cheap (one pass, hash-set compares)
    /// and read-only, so it runs on every boot as a canary — a non-zero count means
    /// something loaded inconsistently (empty-looking bag, unmaterialised type,
    /// runaway spawner) that a player would otherwise have to discover in-game.</summary>
    /// <summary>
    /// Return the contents of any secure-trade window found in a freshly loaded
    /// world, then delete the window.
    ///
    /// A trade session lives only in TradeManager's memory, so a save taken mid-trade
    /// comes back with the windows but no session to drive them. Source-X reaches the
    /// same end through CItem::IsWeird (CItem.cpp:1005), which runs over every object
    /// on load: a trade window whose owner has no active client has its contents
    /// bounced to that owner and is then removed.
    ///
    /// This runs before the world is otherwise used. Until the windows were parented
    /// to their owners they saved with no CONT= at all, and the loader places a
    /// parentless item at its stored position - which for these was tile (0,0),
    /// leaving real player goods in an openable container on the map.
    /// </summary>
    private static void RecoverInterruptedTrades()
    {
        var windows = _world.GetAllObjects()
            .OfType<SphereNet.Game.Objects.Items.Item>()
            .Where(i => !i.IsDeleted && i.ItemType == SphereNet.Core.Enums.ItemType.EqTradeWindow)
            .ToList();
        if (windows.Count == 0) return;

        int returned = 0;
        foreach (var window in windows)
        {
            var owner = _world.FindChar(window.ContainedIn);
            foreach (var item in new List<SphereNet.Game.Objects.Items.Item>(window.Contents))
            {
                window.RemoveItem(item);
                if (owner != null && !owner.IsDeleted)
                    SphereNet.Game.Trade.TradeManager.ReturnItemToCharacter(_world, owner, item);
                else
                    // No owner to give it back to (a save written before the windows
                    // carried one). Drop it where the window stood rather than
                    // deleting player property.
                    _world.PlaceItemWithDecay(item, window.GetTopLevelPosition());
                returned++;
            }

            owner?.Unequip(SphereNet.Core.Enums.Layer.Special);
            _world.RemoveItem(window);
        }

        _log.LogInformation(
            "Recovered {Count} item(s) from {Windows} interrupted trade window(s)",
            returned, windows.Count);
    }

    private static void AuditLoadedWorld()
    {
        var anomalies = SphereNet.Game.Diagnostics.WorldInvariantAuditor.Audit(_world);
        if (anomalies.Count == 0)
        {
            _log.LogInformation("World audit: clean (no consistency anomalies)");
            return;
        }

        var byKind = anomalies.GroupBy(a => a.Kind)
            .Select(g => $"{g.Key}={g.Count()}");
        _log.LogWarning("World audit: {Count} anomalies [{Kinds}]",
            anomalies.Count, string.Join(", ", byKind));
        foreach (var a in anomalies.Take(20))
            _log.LogWarning("  {Anomaly}", a);
    }

    private static void PlaceTeleporters()
    {
        var teleporters = _resources.Teleporters;
        if (teleporters.Count == 0) return;

        // Remove previously placed script teleporters (Static + Telepad)
        var toRemove = new List<SphereNet.Game.Objects.Items.Item>();
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj is SphereNet.Game.Objects.Items.Item item &&
                item.ItemType == ItemType.Telepad &&
                item.IsAttr(ObjAttributes.Static))
            {
                toRemove.Add(item);
            }
        }
        foreach (var item in toRemove)
            _world.RemoveItem(item);

        int placed = 0;
        foreach (var (src, dest, name) in teleporters)
        {
            var item = _world.CreateItem();
            item.BaseId = 0x1BC3;
            item.ItemType = ItemType.Telepad;
            item.MoreP = dest;
            item.Name = string.IsNullOrEmpty(name) ? "teleporter" : name;
            item.SetAttr(ObjAttributes.Invis | ObjAttributes.Static | ObjAttributes.Move_Never);
            _world.PlaceItem(item, src);
            placed++;
        }

        _log.LogInformation("Placed {Count} teleporters from scripts ({Removed} old removed)",
            placed, toRemove.Count);
    }


    /// <summary>Load ROOMDEF sections from script resources into GameWorld.</summary>
    private static void LoadRegionDefs()
    {
        int count = 0;
        foreach (var link in _resources.GetAllResources())
        {
            if (link.Id.Type != ResType.Area) continue;

            var region = new SphereNet.Game.World.Regions.Region
            {
                ResourceId = link.Id,
                Name = link.DefName ?? link.Id.ToString(),
                DefName = link.DefName
            };

            var keys = link.StoredKeys;
            if (keys == null)
            {
                // CRITICAL: ReadAllSections() walks from the section's start
                // line to EOF and returns *every* section in between. We must
                // consume only the first one — the resource link points at
                // exactly one [AREADEF/ROOMDEF a_xxx] block. The previous code
                // merged every following section's keys into this definition,
                // so an early AREADEF inherited the RECTs of every later one
                // in the file (observed: a single "Minax Stronghold" reported
                // with 36/37/40/41/42 rects depending on file position,
                // swallowing Britain because the cumulative rect set covered
                // the city coordinates).
                using var sf = link.OpenAtStoredPosition();
                if (sf == null) continue;
                var sections = sf.ReadAllSections();
                if (sections.Count == 0) continue;
                keys = sections[0].Keys;
            }

            foreach (var key in keys)
            {
                var upper = key.Key.ToUpperInvariant();
                switch (upper)
                {
                    case "NAME":
                        region.Name = key.Arg;
                        break;
                    case "P":
                        var pp = key.Arg.Split(',');
                        if (pp.Length >= 3 &&
                            short.TryParse(pp[0].Trim(), out short px) &&
                            short.TryParse(pp[1].Trim(), out short py) &&
                            sbyte.TryParse(pp[2].Trim(), out sbyte pz))
                        {
                            byte pm = pp.Length > 3 && byte.TryParse(pp[3].Trim(), out byte pmap) ? pmap : (byte)0;
                            region.P = new SphereNet.Core.Types.Point3D(px, py, pz, pm);
                            // Source-X CRegionWorld treats P's 4th component as the
                            // map index; AREADEFs almost never carry an explicit MAP
                            // line, so without this fallback every region defaulted
                            // to map 0 and AREADEFs from Malas/Tokuno/Ilshenar leaked
                            // into Felucca/Trammel lookups (e.g. Hanse's Hostel
                            // shadowing Britain because both ended up on map 0).
                            if (pm != 0)
                                region.MapIndex = pm;
                        }
                        break;
                    case "MAP":
                        if (byte.TryParse(key.Arg, out byte mapIdx))
                            region.MapIndex = mapIdx;
                        break;
                    case "RECT":
                        var parts = key.Arg.Split(',');
                        if (parts.Length >= 4 &&
                            short.TryParse(parts[0].Trim(), out short x1) &&
                            short.TryParse(parts[1].Trim(), out short y1) &&
                            short.TryParse(parts[2].Trim(), out short x2) &&
                            short.TryParse(parts[3].Trim(), out short y2))
                        {
                            region.AddRect(x1, y1, x2, y2);
                            // Source-X RECT syntax: x1,y1,x2,y2[,m]. The optional
                            // 5th value is the rect's map and also pins the region's
                            // map when no MAP/P key was provided.
                            if (parts.Length >= 5 &&
                                byte.TryParse(parts[4].Trim(), out byte rectMap) &&
                                rectMap != 0 && region.MapIndex == 0)
                            {
                                region.MapIndex = rectMap;
                            }
                        }
                        break;
                    case "FLAGS":
                        region.Flags = ParseRegionFlags(key.Arg);
                        break;
                    case "GROUP":
                        region.Group = key.Arg;
                        break;
                    case "EVENTS":
                        if (!string.IsNullOrEmpty(key.Arg))
                        {
                            foreach (var ev in key.Arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                // Source-X keeps REGIONTYPE links inside the region's
                                // EVENTS list and FindNaturalResource scans that list
                                // by res-type (CRegionWorld::FindNaturalResource). The
                                // standard area defs attach their resource tables this
                                // way ("EVENTS=r_default,r_default_rock,…"), so route
                                // REGIONTYPE names into the region-type list too —
                                // otherwise mining/fishing/lumberjacking never sees
                                // the area's ore/fish/tree tables and falls back to an
                                // arbitrary (often all-nothing) table.
                                string evName = ev.TrimStart('+');
                                var evResolved = _resources.ResolveDefName(evName);
                                if (evResolved.IsValid && evResolved.Type == ResType.RegionType)
                                    region.AddRegionType(evResolved);
                                region.AddEvent(ResourceId.FromString(evName, ResType.Events));
                            }
                        }
                        break;
                    case "RESOURCES":
                        if (!string.IsNullOrEmpty(key.Arg))
                        {
                            foreach (var res in key.Arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                region.AddRegionType(ResourceId.FromString(res, ResType.RegionType));
                        }
                        break;
                    default:
                        if (upper.StartsWith("TAG.", StringComparison.Ordinal))
                            region.SetTag(upper[4..], key.Arg);
                        break;
                }
            }

            _world.AddRegion(region);
            count++;
        }

        if (count > 0)
            _log.LogInformation("Loaded {Count} AREADEF definitions as regions", count);
    }

    /// <summary>
    /// Parse a Source-X FLAGS expression. Accepts numeric (decimal/hex) values
    /// and pipe-separated symbol lists like
    /// "REGION_FLAG_NOBUILDING|REGION_FLAG_GUARDED" used by sphere scripts.
    /// </summary>
    private static SphereNet.Core.Enums.RegionFlag ParseRegionFlags(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return SphereNet.Core.Enums.RegionFlag.None;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(trimmed.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                          System.Globalization.CultureInfo.InvariantCulture, out uint hexVal))
            return (SphereNet.Core.Enums.RegionFlag)hexVal;
        if (uint.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out uint decVal))
            return (SphereNet.Core.Enums.RegionFlag)decVal;

        SphereNet.Core.Enums.RegionFlag result = SphereNet.Core.Enums.RegionFlag.None;
        foreach (var token in trimmed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Source-X DEF macro names are REGION_FLAG_<NAME>; the C# enum uses
            // PascalCase. Strip the prefix and try a case-insensitive Enum.Parse
            // with a couple of well known aliases.
            var name = token;
            if (name.StartsWith("REGION_FLAG_", StringComparison.OrdinalIgnoreCase))
                name = name.Substring("REGION_FLAG_".Length);
            name = name.Replace("_", string.Empty);

            // Common aliases between Source-X define names and our enum members.
            name = name switch
            {
                "NOBUILDING" => "NoBuild",
                "GUARDEDOFF" => "GuardedOff",
                "NOMAGIC" => "NoMagic",
                "NOPVP" => "NoPvP",
                "NOPERACRIME" => "NoPeraCrime",
                "SAFEZONE" => "SafeZone",
                _ => name
            };

            if (Enum.TryParse<SphereNet.Core.Enums.RegionFlag>(name, true, out var flag))
                result |= flag;
        }
        return result;
    }

    private static void LoadRoomDefs()
    {
        int count = 0;
        foreach (var link in _resources.GetAllResources())
        {
            if (link.Id.Type != ResType.RoomDef) continue;

            var room = new SphereNet.Game.World.Regions.Room
            {
                ResourceId = link.Id,
                Name = link.DefName ?? link.Id.ToString()
            };

            // Read stored keys or re-open the script file
            var keys = link.StoredKeys;
            if (keys == null)
            {
                // CRITICAL: ReadAllSections() walks from the section's start
                // line to EOF and returns *every* section in between. We must
                // consume only the first one — the resource link points at
                // exactly one [AREADEF/ROOMDEF a_xxx] block. The previous code
                // merged every following section's keys into this definition,
                // so an early AREADEF inherited the RECTs of every later one
                // in the file (observed: a single "Minax Stronghold" reported
                // with 36/37/40/41/42 rects depending on file position,
                // swallowing Britain because the cumulative rect set covered
                // the city coordinates).
                using var sf = link.OpenAtStoredPosition();
                if (sf == null) continue;
                var sections = sf.ReadAllSections();
                if (sections.Count == 0) continue;
                keys = sections[0].Keys;
            }

            foreach (var key in keys)
            {
                var upper = key.Key.ToUpperInvariant();
                switch (upper)
                {
                    case "NAME":
                        room.Name = key.Arg;
                        break;
                    case "MAP":
                        if (byte.TryParse(key.Arg, out byte mapIdx))
                            room.MapIndex = mapIdx;
                        break;
                    case "RECT":
                        // Format: x1,y1,x2,y2
                        var parts = key.Arg.Split(',');
                        if (parts.Length >= 4 &&
                            short.TryParse(parts[0].Trim(), out short x1) &&
                            short.TryParse(parts[1].Trim(), out short y1) &&
                            short.TryParse(parts[2].Trim(), out short x2) &&
                            short.TryParse(parts[3].Trim(), out short y2))
                        {
                            room.AddRect(x1, y1, x2, y2);
                        }
                        break;
                    case "EVENTS":
                        if (!string.IsNullOrEmpty(key.Arg))
                        {
                            foreach (var ev in key.Arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                room.AddEvent(ResourceId.FromString(ev, ResType.Events));
                        }
                        break;
                    default:
                        if (upper.StartsWith("TAG.", StringComparison.Ordinal))
                            room.SetTag(upper[4..], key.Arg);
                        break;
                }
            }

            _world.AddRoom(room);
            count++;
        }

        if (count > 0)
            _log.LogInformation("Loaded {Count} ROOMDEF definitions", count);
    }

    // --- Script Loading ---

    private static int LoadAllScripts(string dir)
    {
        int count = 0;
        var files = ScriptResourceManifest.Resolve(dir, warning => _log.LogWarning("{Warning}", warning));
        // Register the manifest up-front so nested [RESOURCES] references to
        // files already scheduled are not queued a second time.
        foreach (var file in files)
            _resources.RegisterKnownResourceFile(file);
        foreach (var file in files)
        {
            _resources.LoadResourceFile(file);
            count++;
        }
        // Nested [RESOURCES] sections queue extra files — Source-X
        // AddResourceFile appends them to the end of the load list. Loading a
        // drained file may queue more, so keep draining until dry.
        var pending = _resources.DrainPendingResourceFiles();
        while (pending.Count > 0)
        {
            foreach (var file in pending)
            {
                _log.LogInformation("Loading nested [RESOURCES] file: {File}", file);
                _resources.LoadResourceFile(file);
                count++;
            }
            pending = _resources.DrainPendingResourceFiles();
        }
        return count;
    }

    private static void RegisterBuiltinDefNames()
    {
        // Engine baseline DEFNAMEs — script packs (defs.scp) typically
        // override these on load. We register them up-front so admin
        // dialogs and core scripts that depend on them still resolve
        // even when a barebones scripts/ directory is shipped.
        if (_resources == null) return;

        // [DEFNAME ref_types] from Source-X core defs.scp.
        // Used by <GetRefType> == <Def.TRef_*> guards.
        var refTypes = new (string Name, int Value)[]
        {
            ("tref_serv",          0x000001),
            ("tref_file",          0x000002),
            ("tref_newfile",       0x000004),
            ("tref_db",            0x000008),
            ("tref_resdef",        0x000010),
            ("tref_resbase",       0x000020),
            ("tref_functionargs",  0x000040),
            ("tref_fileobj",       0x000080),
            ("tref_fileobjcont",   0x000100),
            ("tref_account",       0x000200),
            ("tref_stonemember",   0x000800),
            ("tref_serverdef",     0x001000),
            ("tref_sector",        0x002000),
            ("tref_world",         0x004000),
            ("tref_gmpage",        0x008000),
            ("tref_client",        0x010000),
            ("tref_object",        0x020000),
            ("tref_char",          0x040000),
            ("tref_item",          0x080000),
        };

        foreach (var (name, val) in refTypes)
        {
            _resources.RegisterDefName(name,
                new SphereNet.Core.Types.ResourceId(
                    SphereNet.Core.Enums.ResType.DefName, val));
        }
    }

    private static List<string> ResolveScriptDirectories(string basePath, string scpConfig)
    {
        var dirs = new List<string>();
        if (string.IsNullOrWhiteSpace(scpConfig))
            return dirs;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = scpConfig.Split([';', '|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            string dir = ResolvePath(basePath, part.Trim());
            if (!Directory.Exists(dir))
                continue;
            if (seen.Add(dir))
                dirs.Add(dir);
        }

        return dirs;
    }

    // --- Helpers ---

    private static string FindConfigFile(string basePath, string fileName)
    {
        string[] searchPaths =
        [
            Path.Combine(Directory.GetCurrentDirectory(), fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "config", fileName),
            Path.Combine(basePath, fileName),
            Path.Combine(basePath, "config", fileName),
        ];

        foreach (string path in searchPaths)
        {
            if (File.Exists(path))
                return path;
        }
        return "";
    }

    private static string FindDir(string basePath, string dirName)
    {
        string[] searchPaths =
        [
            Path.Combine(Directory.GetCurrentDirectory(), dirName),
            Path.Combine(basePath, dirName),
        ];

        foreach (string path in searchPaths)
        {
            if (Directory.Exists(path))
                return path;
        }
        return "";
    }

    /// <summary>
    /// Resolve a config path: absolute is used as-is; a relative path prefers
    /// the working directory when it exists there (same CWD-first rule
    /// FindConfigFile/FindDir use — running the server from a data directory
    /// keeps saves/accounts THERE), falling back to the exe directory.
    /// </summary>
    private static string ResolvePath(string basePath, string configPath)
    {
        if (Path.IsPathRooted(configPath))
            return configPath;
        string cwdPath = Path.Combine(Directory.GetCurrentDirectory(), configPath);
        if (Directory.Exists(cwdPath) || File.Exists(cwdPath))
            return cwdPath;
        return Path.Combine(basePath, configPath);
    }
}
