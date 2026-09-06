using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Security;
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
using System.IO;


namespace SphereNet.Server;

public static partial class Program
{
    // GM page queue lives on the world (GameWorld.GmPages) so it is persisted in
    // spheredata.scp and survives a restart, matching Source-X g_World.m_GMPages.

    private static string? ResolveServerProperty(string property)
    {
        string upper = property.ToUpperInvariant();
        return upper switch
        {
            // --- Read-only server stats ---
            "CLIENTS" => _network?.ActiveConnections.ToString() ?? "0",
            "ACCOUNTS" => _accounts?.Count.ToString() ?? "0",
            "CHARS" => _world?.TotalChars.ToString() ?? "0",
            "ITEMS" => _world?.TotalItems.ToString() ?? "0",
            "VERSION" => "SphereNet 1.0",
            "SERVNAME" or "NAME" => _config?.ServName ?? "SphereNet",

            // --- Time properties ---
            "TIME" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "TIMEUP" => ((int)(DateTime.UtcNow - _serverStartTime).TotalSeconds).ToString(),
            "RTIME" => DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy"),
            "RTICKS" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "TICKPERIOD" => _config.ServerTickMs.ToString(),

            // --- Save ---
            "SAVECOUNT" => _saveCount.ToString(),

            // --- Memory ---
            "MEM" => (GC.GetTotalMemory(false) / 1024).ToString(),

            // --- Regeneration rates (seconds to recover one point; Source-X stat
            // order REGEN0=STR/hits, REGEN1=INT/mana, REGEN2=DEX/stam, REGEN3=food) ---
            "REGEN0" => (_config?.RegenHits ?? 40).ToString(),
            "REGEN1" => (_config?.RegenMana ?? 20).ToString(),
            "REGEN2" => (_config?.RegenStam ?? 10).ToString(),
            "REGEN3" => (_config?.RegenFood ?? 3600).ToString(),

            // --- Misc ---
            "HEARALL" => IsHearAllEnabled() ? "1" : "0",
            "GMPAGES" => (_world?.GmPages.Count ?? 0).ToString(),
            // Source-X CServerConfig g_World.m_Stones.size() — the world's
            // stone objects (guild + town). Was stubbed "0", so the pack's
            // SERVER_MAX_GUILDS cap never triggered.
            "GUILDS" => (CountWorldStonesInt(ItemType.StoneGuild) +
                         CountWorldStonesInt(ItemType.StoneTown)).ToString(),
            "AGE" => ((int)(DateTime.UtcNow - _serverStartTime).TotalDays).ToString(),
            "BUILD" => ThisAssemblyVersion(),
            "URL" => "localhost",
            "MYSQL" => _scriptDb?.IsConnected == true ? "1" : "0",
            "SEASON" => ((int)(_weatherEngine?.CurrentSeason ?? SeasonType.Spring)).ToString(),
            "SEASONMODE" => (_weatherEngine?.CurrentSeasonMode ?? SeasonMode.Auto).ToString(),
            "FEATURETOL" => (_config?.FeatureTOL ?? 0).ToString(),
            "FEATURET2A" => (_config?.FeatureT2A ?? 0).ToString(),
            "CHATFLAGS" => (_config?.ChatFlags ?? 0).ToString(),
            "GENERICSOUNDS" => (_config?.GenericSounds == false ? "0" : "1"),
            "SERVIP" => _config?.ServIP ?? "0.0.0.0",
            "SCPFILES" => EnsureTrailingDirectorySeparator(
                _resources?.ScpBaseDir ?? _config?.ScpFilesDir ?? "scripts/"),
            "COMBATFLAGS" => (_config?.CombatFlags ?? 0).ToString(),
            "MAGICFLAGS" => (_config?.MagicFlags ?? 0).ToString(),
            "OPTIONFLAGS" => (_config?.OptionFlags ?? 0).ToString(),
            "EXPERIMENTAL" => (_config?.Experimental ?? 0).ToString(),
            "DECAYTIMER" => (_config?.DecayTimer ?? 30).ToString(),
            "ARCHERYMINDIST" => (_config?.ArcheryMinDist ?? 1).ToString(),
            "ARCHERYMAXDIST" => (_config?.ArcheryMaxDist ?? 12).ToString(),
            "MAXHOUSESPLAYER" => (_config?.MaxHousesPlayer ?? 1).ToString(),
            "MAXHOUSESACCOUNT" => (_config?.MaxHousesAccount ?? 1).ToString(),
            "NPCTRAINCOST" => (_config?.NpcTrainCost ?? 30).ToString(),
            "NPCTRAINMAX" => (_config?.NpcTrainMax ?? 420).ToString(),
            "GUARDSINSTANTKILL" => (_config?.GuardsInstantKill == true ? "1" : "0"),
            "GUILDSTONES" => CountWorldStones(ItemType.StoneGuild),
            "TOWNSTONES" => CountWorldStones(ItemType.StoneTown),
            // Iterable stone lists (Source-X CServerConfig GUILDSTONES./
            // TOWNSTONES. prefix): .COUNT and .<index>[.UID]. The pack walks
            // these for name/abbrev uniqueness and all-stone war search.
            _ when upper.StartsWith("GUILDSTONES.") =>
                ResolveStoneList(ItemType.StoneGuild, property[12..]),
            _ when upper.StartsWith("TOWNSTONES.") =>
                ResolveStoneList(ItemType.StoneTown, property[11..]),

            // --- Reference lookups via SERV.xxx ---
            "LASTNEWITEM" => _world?.LastNewItem.Value.ToString() ?? "0",
            "LASTNEWCHAR" => _world?.LastNewChar.Value.ToString() ?? "0",

            // --- SERV.MAP* ---
            _ when upper.StartsWith("MAPLIST.") => ResolveMapListProperty(upper[8..]),

            // --- SERV.SKILL.n.KEY / SERV.SKILL.n.NAME — skill table lookup.
            // Admin dialogs iterate all 58 skills using <Serv.Skill.<idx>.Key>
            // to discover defnames at runtime.
            _ when upper.StartsWith("SKILL.") => ResolveServSkill(upper[6..]),
            _ when upper.StartsWith("SPELL.") => ResolveServSpell(property[6..]),

            // --- SERV.CHARDEF.<defname>.<prop> / SERV.ITEMDEF.<defname>.<prop>
            // Used by script dialogs like d_spawn to render names/jobs from
            // definition data without instantiating the object.
            _ when upper.StartsWith("CHARDEF.") => ResolveServCharDef(property[8..]),
            _ when upper.StartsWith("ITEMDEF.") => ResolveServItemDef(property[8..]),
            // Source-X CServerConfig r_GetRef accepts both the paren accessor
            // AREA(<defname>).<prop> and the dot form AREA.<defname>.<prop>.
            _ when upper.StartsWith("AREA(") => ResolveServArea(property[4..]),
            _ when upper.StartsWith("AREA.") => ResolveServArea(property[5..]),
            _ when upper.StartsWith("MULTIDEF.") => ResolveServMultiDef(property[9..]),
            _ when upper.StartsWith("LIST.") => ResolveServList(property[5..]),
            _ when upper.StartsWith("DEFLIST.") => ResolveServDefList(property[8..]),
            _ when upper.StartsWith("GMPAGE.") => ResolveServGmPage(property[7..]),
            _ when upper.StartsWith("DB.") => ResolveScriptDbProperty(_scriptDb, property[3..]),
            _ when upper.StartsWith("LDB.") => ResolveScriptDbProperty(_scriptLdb, property[4..]),
            _ when upper.StartsWith("MDB.") => ResolveScriptDbProperty(_scriptMdb, property[4..]),
            // Full server-global FILE object surface (Source-X g_Serv._hFile):
            // scripts running without a client console (server hooks, NPC
            // triggers) reach the same shared ScriptFileHandle the clients use.
            _ when upper.StartsWith("FILE.") => ResolveServerFileObject(property[5..]),

            // --- ISEVENT.name — 1 if the named event script is loaded.
            // Used by admin dialogs to grey out delete buttons for missing events.
            _ when upper.StartsWith("ISEVENT.") => ResolveIsEvent(upper[8..]),

            // --- SERV.ACCOUNT.name or SERV.ACCOUNT.n ---
            _ when upper.StartsWith("ACCOUNT.") => ResolveServAccount(upper[8..]),

            // --- SERV.MAP.n.SECTOR.n.property ---
            _ when upper.StartsWith("MAP.") => ResolveServMapSector(upper[4..]),

            // --- SERV.MAP(x,y,z,m).property — Source-X function form
            // Used by d_admin houses/ships row to look up the
            // region name at a given world point:
            //   <Serv.Map(<REF1.P.X>,<REF1.P.Y>,0,<REF1.P.M>).Region.Name>
            _ when upper.StartsWith("MAP(") => ResolveServMapPoint(property[4..]),

            // --- RTIME.FORMAT / RTICKS.FORMAT / RTICKS.FROMTIME (standalone, forwarded here) ---
            // Pass the ORIGINAL-case property: strftime tokens are
            // case-sensitive (%d day vs %M minute), and `upper` would fold
            // %d/%m/%y into %D/%M/%Y — the very mismatch this path fixes.
            _ when upper.StartsWith("RTIME.FORMAT") => ResolveRtimeFormat(property),
            _ when upper.StartsWith("RTICKS.FORMAT") => ResolveRticksFormat(property),
            _ when upper.StartsWith("RTICKS.FROMTIME") => ResolveRticksFromTime(upper),

            // --- SERV.LOOKUPSKILL <name> — reverse lookup skill id by name.
            // Returns -1 on miss to match Source-X behaviour.
            _ when upper.StartsWith("LOOKUPSKILL ") => ResolveLookupSkill(property[12..]),
            _ when upper.StartsWith("LOOKUPSKILL(") => ResolveLookupSkill(
                property.EndsWith(")") ? property[12..^1] : property[12..]),

            // --- Global variables: VAR.name / VAR0.name ---
            _ when upper.StartsWith("VAR0.") => _world?.GetGlobalVar0(property[5..]) ?? "0",
            _ when upper.StartsWith("VAR.") => _world?.GetGlobalVar(property[4..]) ?? "",

            // --- OBJ / OBJ.property — global object reference ---
            "OBJ" => _world?.ObjReference.Value != 0 ? $"0{_world!.ObjReference.Value:X}" : "0",
            _ when upper.StartsWith("OBJ.") => ResolveObjProperty(property[4..]),

            // --- NEW / NEW.property — the object the last factory produced ---
            // One reference for both forms (Source-X g_World.m_uidNew,
            // CScriptObj.cpp:1381). Preferring the last ITEM here made a script that
            // had just created an NPC read back the earlier item, and disagreed with
            // NEW.<prop>, which fell through to the character. Serial.Invalid is
            // 0xFFFFFFFF, so the old "!= 0" test also passed for "nothing created yet".
            "NEW" => _world != null && _world.LastNewObject.IsValid
                        ? $"0{_world.LastNewObject.Value:X}" : "0",
            _ when upper.StartsWith("NEW.") => ResolveNewProperty(property[4..]),

            // --- UID.0xHEX.property — direct object access ---
            _ when upper.StartsWith("UID.") => ResolveUidProperty(property[4..]),

            // --- DEFMSG.name — default message lookup ---
            _ when upper.StartsWith("DEFMSG.") => ResolveDefMsg(property[7..]),

            // --- DEF.name / DEF0.name — generic [DEFNAME] lookup ---
            _ when upper.StartsWith("DEF0.") => ResolveDefValue(property[5..], decimalNumeric: true),
            _ when upper.StartsWith("DEF.") => ResolveDefValue(property[4..], decimalNumeric: false),

            // --- Commands (write operations, prefixed with _SET_/_CLEARVARS/_NEWDUPE) ---
            _ when upper.StartsWith("_SET_LIST.") => HandleSetGlobalList(property[10..]),
            _ when upper.StartsWith("_SET_VAR.") => HandleSetGlobalVar(property[9..]),
            _ when upper.StartsWith("_SET_OBJ=") => HandleSetObj(property[9..]),
            _ when upper.StartsWith("_SET_OBJ.") => HandleSetObjProperty(property[9..]),
            _ when upper.StartsWith("_SET_NEW=") => HandleSetNew(property[9..]),
            _ when upper.StartsWith("_SET_NEW.") => HandleSetNewProperty(property[9..]),
            _ when upper.StartsWith("_CLEARVARS=") => HandleClearVars(property[11..]),
            _ when upper.StartsWith("_NEWDUPE=") => HandleNewDupe(property[9..]),
            _ when upper.StartsWith("_NEWITEM_ACT=") => HandleNewItemForCaller(property[13..]),
            _ when upper.StartsWith("_NEWITEM=") => HandleServNewItem(property[9..]),
            _ when upper.StartsWith("_NEWNPC=") => HandleServNewNpc(property[8..]),
            _ when upper.StartsWith("_DB_VERB=") => HandleScriptDbVerb(property[9..]),
            _ when upper.StartsWith("_SET_DEFMSG=") => HandleSetDefMsg(property[12..]),
            _ when upper.StartsWith("_SET_SEASON=") => HandleSetSeason(property[12..]),

            // REF object property access
            _ when upper.StartsWith("_REF_GET=") => HandleRefGet(property[9..]),
            _ when upper.StartsWith("_REF_EXEC_AS=") => HandleRefExecAs(property[13..]),
            _ when upper.StartsWith("_REF_EXEC=") => HandleRefExec(property[10..]),

            // serv.allclients <function> — invoke <function> once per
            // online player, with the caller as src. Protocol format:
            // _ALLCLIENTS=<srcUid>|<funcName>.
            _ when upper.StartsWith("_ALLCLIENTS=") => HandleAllClients(property[12..]),
            _ when upper.StartsWith("_WRITEFILE=") => HandleServWriteFile(property[11..]),
            _ when upper.StartsWith("_DELETEFILE=") => HandleServDeleteFile(property[12..]),
            _ when upper.StartsWith("_CONSOLE=") => HandleServConsole(property[9..]),
            _ when upper.StartsWith("_LOG=") => HandleServLog(property[5..]),
            _ when upper.StartsWith("_GMPAGE=") => HandleServGmPage(property[8..]),
            _ when upper.StartsWith("_VARLIST=") => HandleServVarListToCaller(property[9..]),
            _ when upper.StartsWith("_PRINTLISTS=") => HandleServPrintListsToCaller(property[12..]),
            _ when upper.StartsWith("_BROADCAST=") => HandleServBroadcast(property[11..]),
            _ when upper.StartsWith("_GARBAGE") => HandleServGarbage(),
            _ when upper.StartsWith("_INFORMATION=") => HandleServInformationToCaller(property[13..]),
            _ when upper.StartsWith("_SHRINKMEM") => HandleServShrinkMem(),
            _ when upper.StartsWith("_SECUREMODE") => HandleServSecure(),
            _ when upper.StartsWith("_HEARALL=") => HandleServHearAll(property[9..]),
            _ when upper.StartsWith("_EXPORT=") => HandleServExport(property[8..]),
            _ when upper.StartsWith("_LOAD=") => HandleServLoad(property[6..]),
            _ when upper.StartsWith("_IMPORT=") => HandleServImport(property[8..]),
            _ when upper.StartsWith("_RESTORE=") => HandleServRestore(property[9..]),
            _ when upper.StartsWith("_SAVESTATICS=") => HandleServSaveStatics(property[13..]),
            _ when upper.StartsWith("_BLOCKIP=") => HandleServBlockIp(property[9..], block: true),
            _ when upper.StartsWith("_UNBLOCKIP=") => HandleServBlockIp(property[11..], block: false),
            _ when upper.StartsWith("_CALCCRYPT=") => HandleServCalcCryptToCaller(property[11..]),

            // serv.resync / serv.save / serv.shutdown — admin write
            // verbs reachable from dialog buttons (d_admin_function).
            // Sphere fires them as bare property reads on the server
            // resolver; we hijack and run the matching engine action.
            "RESYNC" => HandleServResync(),
            "SAVE" => HandleServSave(),
            "SHUTDOWN" => HandleServShutdown(""),
            _ when upper.StartsWith("SHUTDOWN ") => HandleServShutdown(property[9..]),

            // --- World-ops verbs reachable from scripts (Source-X SERV.*). These
            // were previously console-only or absent; they delegate to the same
            // engine actions the admin console fires. ---
            "RESPAWN" => HandleServRespawn(),
            "RESTOCK" => HandleServRestock(),
            "CLEARLISTS" => HandleServClearLists(""),
            _ when upper.StartsWith("CLEARLISTS ") => HandleServClearLists(property[11..]),
            "VARLIST" => HandleServVarList(""),
            _ when upper.StartsWith("VARLIST ") => HandleServVarList(property[8..]),
            "PRINTLISTS" => HandleServPrintLists(),
            _ when upper.StartsWith("HEARALL ") => HandleServHearAll(property[8..]),
            _ when upper.StartsWith("EXPORT ") => HandleServExport("0|" + property[7..]),
            _ when upper.StartsWith("LOAD ") => HandleServLoad(property[5..]),
            _ when upper.StartsWith("IMPORT ") => HandleServImport("0|" + property[7..]),
            _ when upper.StartsWith("RESTORE ") => HandleServRestore(property[8..]),
            "SAVESTATICS" => HandleServSaveStatics(""),
            _ when upper.StartsWith("SAVESTATICS ") => HandleServSaveStatics(property[12..]),
            // <SERV.CALCCRYPT ver[,cliType][,encType]> read form returns the
            // SphereCrypt.ini-style key line (Source-X SV_CALCCRYPT output).
            _ when upper.StartsWith("CALCCRYPT ") => CalcCryptLine(property[10..]),

            // Region property access
            _ when upper.StartsWith("_REGION_GET=") => HandleRegionGet(property[12..]),

            // Room property access
            _ when upper.StartsWith("_ROOM_GET=") => HandleRoomGet(property[10..]),

            // Bare defname constants (e.g. statf_insubstantial) used by
            // script expressions without DEF./DEF0. prefix.
            _ => ResolveDefConstant(upper)
        };
    }

    /// <summary>Resolve <c>ISEVENT.name</c>. Returns "1" when a script event
    /// with this defname exists in the loaded resource set, else "0".</summary>
    private static string? ResolveIsEvent(string eventName)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(eventName)) return "0";
        var rid = _resources.ResolveDefName(eventName);
        if (!rid.IsValid)
            rid = _resources.ResolveDefName("e_" + eventName);
        return rid.IsValid ? "1" : "0";
    }

    /// <summary>Resolve <c>SERV.SKILL.n[.KEY|NAME]</c>. Returns the enum name
    /// (e.g. "Alchemy") which matches Source-X defname for that skill slot.</summary>
    private static string? ResolveServSkill(string sub)
    {
        int dot = sub.IndexOf('.');
        string idxStr = dot < 0 ? sub : sub[..dot];
        string field = dot < 0 ? "" : sub[(dot + 1)..].ToUpperInvariant();
        if (!int.TryParse(idxStr, out int idx)) return null;
        if (!Enum.IsDefined(typeof(SphereNet.Core.Enums.SkillType), (short)idx))
            return "";
        string skillName = ((SphereNet.Core.Enums.SkillType)idx).ToString();
        if (string.IsNullOrEmpty(field) || field == "KEY" || field == "NAME" || field == "DEFNAME")
            return skillName;
        return "";
    }

    private static int CountWorldStonesInt(ItemType type) =>
        _world?.GetAllObjects().OfType<Item>().Count(i => i.ItemType == type) ?? 0;

    private static string CountWorldStones(ItemType type) =>
        CountWorldStonesInt(type).ToString();

    /// <summary>SERV.GUILDSTONES./TOWNSTONES. list access (Source-X
    /// CServerConfig): ".COUNT" / "" → count, ".&lt;index&gt;[.UID]" → the
    /// index-th stone's uid.</summary>
    private static string ResolveStoneList(ItemType type, string sub)
    {
        sub = sub.Trim();
        if (sub.Length == 0 || sub.Equals("COUNT", StringComparison.OrdinalIgnoreCase))
            return CountWorldStones(type);

        // "<index>" or "<index>.UID" (the only per-stone sub-key the pack reads).
        string idxTok = sub;
        int dot = sub.IndexOf('.');
        if (dot >= 0) idxTok = sub[..dot];
        if (!int.TryParse(idxTok.Trim(), out int index) || index < 0)
            return "";

        var stones = _world?.GetAllObjects().OfType<Item>()
            .Where(i => i.ItemType == type).ToList();
        if (stones == null || index >= stones.Count)
            return "";
        var stone = stones[index];
        if (dot < 0 || sub[(dot + 1)..].Equals("UID", StringComparison.OrdinalIgnoreCase))
            return $"0{stone.Uid.Value:X}";
        return stone.TryGetProperty(sub[(dot + 1)..], out string v) ? v : "";
    }

    private static string EnsureTrailingDirectorySeparator(string path) =>
        string.IsNullOrEmpty(path) || path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    /// <summary>Server-global FILE object dispatcher (Source-X CSFileObj on
    /// g_Serv._hFile): properties AND verbs over the shared ScriptFileHandle.
    /// Gated by OF_FileCommands (the handle only exists when enabled).</summary>
    private static string ResolveServerFileObject(string sub)
    {
        var file = _scriptFile;
        if (file == null)
            return "0"; // OF_FileCommands not set

        string upper = sub.Trim();
        int sp = upper.IndexOfAny([' ', '\t']);
        string member = (sp < 0 ? upper : upper[..sp]).ToUpperInvariant();
        string arg = sp < 0 ? "" : upper[(sp + 1)..].Trim();

        switch (member)
        {
            case "OPEN":
                return arg.Length > 0
                    ? (file.Open(arg) ? "1" : "0")
                    : (file.IsOpen ? "1" : "0");
            case "CLOSE":
                file.Close();
                return "1";
            case "FLUSH":
                file.Flush();
                return "1";
            case "INUSE":
                return file.IsOpen ? "1" : "0";
            case "ISEOF":
                return file.IsEof ? "1" : "0";
            case "FILEPATH":
                return file.FilePath;
            case "POSITION":
                return file.Position.ToString();
            case "LENGTH":
                return file.Length.ToString();
            case "READCHAR":
                return file.ReadChar();
            case "READBYTE":
            {
                int count = arg.Length > 0 && int.TryParse(arg, out int n) ? n : 1;
                return file.ReadBytes(count);
            }
            case "READLINE":
            {
                int line = arg.Length > 0 && int.TryParse(arg, out int n) ? n : 0;
                return file.ReadLine(line);
            }
            case "SEEK":
                file.Seek(arg);
                return file.Position.ToString();
            case "FILEEXIST":
                return file.FileExistsRelative(arg) ? "1" : "0";
            case "FILELINES":
                return file.GetFileLinesRelative(arg).ToString();
            case "DELETEFILE":
                return file.DeleteRelative(arg) ? "1" : "0";
            case "WRITE":
                return file.Write(arg) ? "1" : "0";
            case "WRITELINE":
                return file.WriteLine(arg) ? "1" : "0";
            case "WRITECHR":
                return arg.Length > 0 && int.TryParse(arg, out int chr) && file.WriteChr(chr) ? "1" : "0";
            case "MODE.APPEND":
                if (arg.Length > 0) { file.ModeAppend = arg != "0"; return "1"; }
                return file.ModeAppend ? "1" : "0";
            case "MODE.CREATE":
                if (arg.Length > 0) { file.ModeCreate = arg != "0"; return "1"; }
                return file.ModeCreate ? "1" : "0";
            case "MODE.READFLAG":
                if (arg.Length > 0) { file.ModeRead = arg != "0"; return "1"; }
                return file.ModeRead ? "1" : "0";
            case "MODE.WRITEFLAG":
                if (arg.Length > 0) { file.ModeWrite = arg != "0"; return "1"; }
                return file.ModeWrite ? "1" : "0";
            case "MODE.SETDEFAULT":
                file.SetModeDefault();
                return "1";
            default:
                return "0";
        }
    }

    private static string ResolveScriptDbProperty(ScriptDbAdapter? db, string property)
    {
        if (db == null) return "0";
        if (property.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase))
            return db.IsConnected ? "1" : "0";
        if (property.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
            return db.ActiveSessionName;
        if (property.Equals("NUMCOLS", StringComparison.OrdinalIgnoreCase))
            return db.NumCols.ToString();
        if (property.StartsWith("ESCAPEDATA.", StringComparison.OrdinalIgnoreCase))
            return db.EscapeData(property[11..]);
        string rowKey = property.StartsWith("ROW.", StringComparison.OrdinalIgnoreCase)
            ? "db." + property
            : property;
        return db.TryResolveRowValue(rowKey, out string value) ? value : "0";
    }

    private static string HandleScriptDbVerb(string raw)
    {
        string[] parts = raw.Split('|', 3);
        if (parts.Length < 2) return "0";
        ScriptDbAdapter? db = parts[0].ToUpperInvariant() switch
        {
            "DB" => _scriptDb,
            "LDB" => _scriptLdb,
            "MDB" => _scriptMdb,
            _ => null
        };
        if (db == null) return "0";
        string verb = parts[1].Trim().ToUpperInvariant();
        string arg = parts.ElementAtOrDefault(2)?.Trim() ?? "";
        bool ok;
        string error;
        switch (verb)
        {
            case "CONNECT":
                ok = parts[0].Equals("LDB", StringComparison.OrdinalIgnoreCase)
                    ? db.ConnectFile(arg, _resources?.ScpBaseDir ?? AppContext.BaseDirectory, out error)
                    : db.Connect(arg, out error);
                break;
            case "CLOSE":
                db.Close();
                return "1";
            case "QUERY":
                ok = db.Query(arg.Trim('"'), out _, out error);
                break;
            case "EXECUTE":
                ok = db.Execute(arg.Trim('"'), out _, out error);
                break;
            case "AQUERY":
                // Source-X DBO AQUERY: fire-and-forget query (no blocking wait).
                ok = db.QueryAsync(arg.Trim('"'));
                error = "";
                break;
            case "AEXECUTE":
                ok = db.ExecuteAsync(arg.Trim('"'));
                error = "";
                break;
            case "IMPORTDB" when parts[0].Equals("MDB", StringComparison.OrdinalIgnoreCase):
                ok = db.ConnectFile(arg.Trim('"'), _resources?.ScpBaseDir ?? AppContext.BaseDirectory, out error);
                break;
            default:
                return "0";
        }
        if (!ok && !string.IsNullOrWhiteSpace(error))
            _log?.LogWarning("Script {Db}.{Verb} failed: {Error}", parts[0], verb, error);
        return ok ? "1" : "0";
    }

    /// <summary>Resolve Source-X SERV.SPELL.&lt;id|defname&gt;.&lt;property&gt;.</summary>
    private static string? ResolveServSpell(string sub)
    {
        if (_spellRegistry == null || string.IsNullOrWhiteSpace(sub))
            return "";

        int dot = sub.IndexOf('.');
        if (dot <= 0)
            return "";

        string token = sub[..dot].Trim();
        string field = sub[(dot + 1)..].Trim();
        int spellId;
        ResourceLink? link = null;
        if (_resources?.ResolveDefName(token) is { IsValid: true, Type: ResType.SpellDef } rid)
        {
            spellId = rid.Index;
            link = _resources.GetResource(rid);
        }
        else
        {
            spellId = ValueCurve.ParseSphereNumber(token);
            link = _resources?.GetResource(ResType.SpellDef, spellId);
        }

        var def = _spellRegistry.Get((SpellType)(ushort)spellId);
        if (def == null)
            return "";
        if (field.Equals("DEFNAME", StringComparison.OrdinalIgnoreCase))
            return link?.DefName ?? token;
        if (field.Equals("SKILLREQ", StringComparison.OrdinalIgnoreCase))
            return string.Join(',', def.SkillReq.Select(kv => $"{kv.Key} {kv.Value}"));
        return def.TryGetProperty(field, out string value) ? value : "";
    }

    private static string HandleSetSeason(string raw)
    {
        if (_weatherEngine == null)
            return "0";

        string trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "0";

        SeasonType season;
        if (byte.TryParse(trimmed, out byte seasonNum) &&
            Enum.IsDefined(typeof(SeasonType), (int)seasonNum))
        {
            season = (SeasonType)seasonNum;
        }
        else if (!Enum.TryParse(trimmed, ignoreCase: true, out season))
        {
            return "0";
        }

        bool changed = _weatherEngine.SetSeason(season, resetCycleTimer: true);
        if (changed)
            BroadcastSeasonChange(playSound: true);
        return ((int)_weatherEngine.CurrentSeason).ToString();
    }

    private static string? ResolveServCharDef(string sub)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(sub))
            return "";

        int dot = sub.IndexOf('.');
        if (dot <= 0)
            return "";

        string defName = sub[..dot].Trim();
        string field = sub[(dot + 1)..].Trim().ToUpperInvariant();
        var rid = _resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != SphereNet.Core.Enums.ResType.CharDef)
            return "";

        var def = SphereNet.Game.Definitions.DefinitionLoader.GetCharDef(rid.Index);
        if (def == null)
            return "";

        return field switch
        {
            "NAME" => def.Name ?? "",
            "JOB" => def.Job ?? "",
            "DEFNAME" => def.DefName ?? "",
            "ID" or "DISPID" => $"0{def.DispIndex:X}",
            "ICON" => def.Icon ?? "",
            "NPC" or "NPCBRAIN" => def.NpcBrain.ToString(),
            "CAN" => $"0{(ulong)def.Can:X}",
            "COLOR" => $"0{def.BaseColor:X}",
            "FOODTYPE" => def.FoodTypeRaw,
            "MAXFOOD" => def.MaxFood.ToString(),
            "ERALIMITGEAR" => def.EraLimitGear.ToString(),
            "ERALIMITLOOT" => def.EraLimitLoot.ToString(),
            "ERALIMITPROPS" => def.EraLimitProps.ToString(),
            "RESPHYSICAL" => def.ResPhysical.ToString(),
            "RESFIRE" => def.ResFire.ToString(),
            "RESCOLD" => def.ResCold.ToString(),
            "RESPOISON" => def.ResPoison.ToString(),
            "RESENERGY" => def.ResEnergy.ToString(),
            "RESPHYSICALMAX" => def.ResPhysicalMax.ToString(),
            "RESFIREMAX" => def.ResFireMax.ToString(),
            "RESCOLDMAX" => def.ResColdMax.ToString(),
            "RESPOISONMAX" => def.ResPoisonMax.ToString(),
            "RESENERGYMAX" => def.ResEnergyMax.ToString(),
            "REFLECTPHYSICALDAM" => def.ReflectPhysicalDam.ToString(),
            "SOUND" or "SOUNDIDLE" => $"0{def.SoundIdle:X}",
            "SOUNDNOTICE" => $"0{def.SoundNotice:X}",
            "SOUNDHIT" => $"0{def.SoundHit:X}",
            "SOUNDGETHIT" => $"0{def.SoundGetHit:X}",
            "SOUNDDIE" => $"0{def.SoundDie:X}",
            _ => ""
        };
    }

    private static string? ResolveServItemDef(string sub)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(sub))
            return "";

        int dot = sub.IndexOf('.');
        if (dot <= 0)
            return "";

        string defName = sub[..dot].Trim();
        string field = sub[(dot + 1)..].Trim().ToUpperInvariant();
        var rid = _resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != SphereNet.Core.Enums.ResType.ItemDef)
            return "";

        var def = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(rid.Index);
        if (def == null)
            return "";

        return field switch
        {
            "NAME" => def.Name ?? "",
            "DEFNAME" => def.DefName ?? "",
            "ID" or "DISPID" => $"0{def.DispIndex:X}",
            "TYPE" => string.IsNullOrWhiteSpace(def.TypeRaw) ? def.Type.ToString() : def.TypeRaw,
            "TDATA1" => def.TData1Name ?? $"0{def.TData1:X}",
            "TDATA2" => def.TData2Name ?? $"0{def.TData2:X}",
            "TDATA3" => def.TData3Name ?? $"0{def.TData3:X}",
            "TDATA4" => def.TData4Name ?? $"0{def.TData4:X}",
            "CAN" => $"0{(ulong)def.Can:X}",
            "CANUSE" => $"0{(ulong)def.CanUse:X}",
            "HEIGHT" => def.Height.ToString(),
            "WEIGHT" => def.Weight.ToString(),
            "LAYER" => ((int)def.Layer).ToString(),
            "SPEED" => def.Speed.ToString(),
            "SKILL" => def.Skill.ToString(),
            "REQSTR" => def.ReqStr.ToString(),
            "VALUE" => def.ValueMin == def.ValueMax ? def.ValueMin.ToString() : $"{def.ValueMin},{def.ValueMax}",
            "DAM" => def.AttackMin == def.AttackMax ? def.AttackMin.ToString() : $"{def.AttackMin},{def.AttackMax}",
            "ARMOR" => def.DefenseMin == def.DefenseMax ? def.DefenseMin.ToString() : $"{def.DefenseMin},{def.DefenseMax}",
            "RESOURCES" => def.ResourcesRaw,
            "SKILLMAKE" => def.SkillMakeRaw,
            _ => def.TagDefs.Get(field) ?? ""
        };
    }

    private static string ThisAssemblyVersion() =>
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    private static string? ResolveServArea(string sub)
    {
        if (_world == null || string.IsNullOrWhiteSpace(sub))
            return "0";

        string selector, field;
        if (sub.StartsWith("(", StringComparison.Ordinal))
        {
            // Paren form: (<defname>).<field>
            int close = sub.IndexOf(')');
            if (close < 0)
                return "0";
            selector = sub[1..close].Trim();
            field = sub[(close + 1)..].TrimStart('.');
        }
        else
        {
            int dot = sub.IndexOf('.');
            selector = dot >= 0 ? sub[..dot] : sub;
            field = dot >= 0 ? sub[(dot + 1)..] : "";
        }

        Region? region = null;
        if (int.TryParse(selector, out int index))
        {
            if (index >= 0 && index < _world.Regions.Count)
                region = _world.Regions[index];
        }
        else
        {
            string normalized = selector.Trim();
            region = _world.Regions.FirstOrDefault(r =>
                string.Equals(r.DefName, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Name, normalized, StringComparison.OrdinalIgnoreCase));
        }

        if (region == null)
            return "0";
        if (string.IsNullOrWhiteSpace(field))
            return region.DefName ?? region.Name;
        return region.TryGetProperty(field, out string value) ? value : "0";
    }

    private static string? ResolveServList(string sub)
    {
        if (_world == null || string.IsNullOrWhiteSpace(sub))
            return "0";
        return _world.ResolveGlobalListRead(sub);
    }

    private static string? ResolveServDefList(string sub)
    {
        if (string.IsNullOrWhiteSpace(sub))
            return "0";

        int dot = sub.IndexOf('.');
        string kind = (dot >= 0 ? sub[..dot] : sub).ToUpperInvariant();
        string rest = dot >= 0 ? sub[(dot + 1)..] : "";
        var names = kind switch
        {
            "ITEMDEF" => DefinitionLoader.AllItemDefs
                .Select(kv => kv.Value.DefName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray()!,
            "CHARDEF" => DefinitionLoader.AllCharDefs
                .Select(kv => kv.Value.DefName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray()!,
            _ => Array.Empty<string>()
        };

        if (string.IsNullOrEmpty(rest) || rest.Equals("COUNT", StringComparison.OrdinalIgnoreCase))
            return names.Length.ToString();
        if (int.TryParse(rest, out int index) && index >= 0 && index < names.Length)
            return names[index];
        return "";
    }

    private static string? ResolveServGmPage(string sub)
    {
        var pages = _world?.GmPages;
        int dot = sub.IndexOf('.');
        string idxStr = dot >= 0 ? sub[..dot] : sub;
        string field = dot >= 0 ? sub[(dot + 1)..].ToUpperInvariant() : "";
        if (pages == null || !int.TryParse(idxStr, out int index) || index < 0 || index >= pages.Count)
            return "0";
        var page = pages[index];
        return field switch
        {
            "" => page.Reason,
            "ACCOUNT" => page.Account,
            "REASON" => page.Reason,
            "HANDLER" => page.Handler,
            "STATUS" => page.Status,
            "TIME" or "CREATED" => page.Created.ToString(),
            "DELETE" => RemoveGmPage(index),
            _ => ""
        };
    }

    private static string RemoveGmPage(int index)
    {
        _world?.RemoveGmPageAt(index);
        return "";
    }

    private static string? ResolveServMultiDef(string sub)
    {
        if (string.IsNullOrWhiteSpace(sub))
            return "0";

        int dot = sub.IndexOf('.');
        string idPart = dot >= 0 ? sub[..dot] : sub;
        string field = dot >= 0 ? sub[(dot + 1)..].ToUpperInvariant() : "";
        int multiId = ParseScriptInt(idPart);
        if (multiId == 0 && !idPart.Trim().Equals("0", StringComparison.Ordinal))
            return "0";

        var rid = new ResourceId(ResType.MultiDef, multiId);
        var link = _resources?.GetResource(rid);
        return field switch
        {
            "" or "ID" => $"0{multiId:X}",
            "TYPE" => link?.StoredKeys?.FirstOrDefault(k => k.Key.Equals("TYPE", StringComparison.OrdinalIgnoreCase))?.Arg ?? "T_MULTI",
            "COMPONENTS" or "COMPONENTCOUNT" => (link?.StoredKeys?.Count(k => k.Key.Equals("COMPONENT", StringComparison.OrdinalIgnoreCase)) ?? 0).ToString(),
            "NAME" or "DEFNAME" => link?.DefName ?? "",
            _ => ""
        };
    }

    private static int ParseScriptInt(string value)
    {
        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(v.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int hx) ? hx : 0;
        if (v.StartsWith('0') && v.Length > 1)
            return int.TryParse(v.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int sx) ? sx : 0;
        return int.TryParse(v, out int dec) ? dec : 0;
    }

    private static string? ResolveObjProperty(string subProp)
    {
        if (_world == null) return "0";
        var obj = _world.FindObject(_world.ObjReference);
        if (obj == null) return "0";
        return obj.TryGetProperty(subProp, out string val) ? val : "0";
    }

    private static string? ResolveNewProperty(string subProp)
    {
        if (_world == null) return "0";
        // The same reference bare NEW reads, so the two can never name different
        // objects (Source-X g_World.m_uidNew).
        var obj = _world.FindObject(_world.LastNewObject);
        if (obj == null) return "0";
        return obj.TryGetProperty(subProp, out string val) ? val : "0";
    }

    /// <summary>NEW=&lt;uid&gt; - repoint the global new-object reference, clearing it
    /// when the uid names nothing (Source-X SSV_NEW, CScriptObj.cpp:1305).</summary>
    private static string? HandleSetNew(string raw)
    {
        if (_world == null) return "";
        _world.LastNewObject = TryParseSerial(raw, out var uid) && _world.FindObject(uid) != null
            ? uid
            : SphereNet.Core.Types.Serial.Invalid;
        return "";
    }

    /// <summary>NEW.&lt;prop&gt;=&lt;value&gt; - write through the new-object reference,
    /// the way a resolved reference head reaches its target's own r_Verb
    /// (CScriptObj.cpp:1217).</summary>
    private static string? HandleSetNewProperty(string assignment)
    {
        if (_world == null) return "";
        int eq = assignment.IndexOf('=');
        if (eq <= 0) return "";
        string prop = assignment[..eq].Trim();
        string value = assignment[(eq + 1)..].Trim();
        var obj = _world.FindObject(_world.LastNewObject);
        if (obj == null || prop.Length == 0) return "";
        if (obj.TrySetProperty(prop, value))
            return "";
        obj.TryExecuteCommand(prop, value, new RefExecConsole());
        return "";
    }

    private static string? ResolveUidProperty(string uidAndProp)
    {
        if (_world == null) return null;
        // Format: 0xHEXVALUE.property or HEXVALUE.property
        int dot = uidAndProp.IndexOf('.');
        if (dot <= 0) return null;
        string uidStr = uidAndProp[..dot].Trim();
        string prop = uidAndProp[(dot + 1)..].Trim();
        // Strip leading 0 or 0x
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return null;
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "0";
        return obj.TryGetProperty(prop, out string val) ? val : "0";
    }

    private static string? ResolveDefMsg(string msgName)
    {
        string key = NormalizeDefMsgKey(msgName);
        if (ServerMessages.HasKey(key))
            return ServerMessages.Get(key);
        if (_resources != null && _resources.TryGetDefMessage(msgName, out string defMsg))
            return defMsg;
        if (_resources != null && _resources.TryGetDefMessage(key, out defMsg))
            return defMsg;
        return ServerMessages.Get(key);
    }

    private static string NormalizeDefMsgKey(string raw)
    {
        string key = (raw ?? "").Trim();
        if (key.StartsWith("DEFMSG.", StringComparison.OrdinalIgnoreCase))
            key = key[7..];
        if (key.StartsWith("DEFMSG_", StringComparison.OrdinalIgnoreCase))
            key = key[7..];
        return key.Trim().ToLowerInvariant();
    }

    private static string? ResolveDefValue(string defName, bool decimalNumeric)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(defName))
            return "0";

        string key = defName.Trim();
        if (_resources.TryGetDefValue(key, out string textValue))
            return StripSurroundingQuotes(textValue);

        var rid = _resources.ResolveDefName(key);
        if (rid.IsValid)
        {
            int idx = NumericDefIndex(rid);
            return decimalNumeric ? idx.ToString() : $"0{idx:X}";
        }

        string? builtIn = ResolveDefConstant(key.ToUpperInvariant());
        if (builtIn == null)
            return "0";

        if (decimalNumeric)
            return builtIn;

        if (long.TryParse(builtIn, out long numeric))
            return $"0{numeric:X}";

        return builtIn;
    }

    /// <summary>Numeric script value of a resolved defname. Source-X registers
    /// multis as itemdefs at ITEMID_MULTI (0x4000) + index, so a MULTIDEF
    /// defname evaluated in an expression (e.g. the deed @Create body's
    /// MORE=m_small_ship_n) must yield the 0x4000-based id — returning the raw
    /// index collapsed small-ship-north (index 0) to "0", which reads as
    /// "unset" everywhere downstream.</summary>
    private static int NumericDefIndex(SphereNet.Core.Types.ResourceId rid) =>
        rid.Type == ResType.MultiDef ? 0x4000 + rid.Index : rid.Index;

    private static string StripSurroundingQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static string? ResolveDefConstant(string upperToken)
    {
        if (_resources == null || string.IsNullOrWhiteSpace(upperToken))
            return null;
        // Only allow plain identifier-like tokens here so arithmetic and
        // command payloads don't accidentally route into defname lookups.
        for (int i = 0; i < upperToken.Length; i++)
        {
            char ch = upperToken[i];
            bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch is '_' or '.';
            if (!ok) return null;
        }
        var rid = _resources.ResolveDefName(upperToken);
        if (rid.IsValid) return NumericDefIndex(rid).ToString();

        // Built-in STATF_* fallback when scripts reference status-flag
        // constants directly and the defname pack doesn't provide them.
        // Example: (<Flags> & statf_insubstantial)
        if (upperToken.StartsWith("STATF_", StringComparison.Ordinal))
        {
            string suffix = upperToken[6..];
            foreach (SphereNet.Core.Enums.StatFlag flag in Enum.GetValues(typeof(SphereNet.Core.Enums.StatFlag)))
            {
                string enumName = flag.ToString().ToUpperInvariant();
                if (enumName == suffix || enumName.Replace("_", "", StringComparison.Ordinal) == suffix)
                    return ((uint)flag).ToString();
            }
        }
        return null;
    }

    private static string? HandleSetGlobalVar(string assignment)
    {
        // Format: name=value
        int eq = assignment.IndexOf('=');
        if (eq <= 0) return "";
        string name = assignment[..eq].Trim();
        string value = assignment[(eq + 1)..].Trim();
        _world?.SetGlobalVar(name, value);
        return "";
    }

    /// <summary>Bridge for LIST.&lt;name&gt;[.op]=value — delegates to the testable
    /// GameWorld.MutateGlobalList grammar (Source-X CListDefMap::r_LoadVal).</summary>
    private static string? HandleSetGlobalList(string assignment)
    {
        if (_world == null) return "";
        int eq = assignment.IndexOf('=');
        string keyPath = eq >= 0 ? assignment[..eq] : assignment;
        string value = eq >= 0 ? assignment[(eq + 1)..] : "";
        _world.MutateGlobalList(keyPath, value);
        return "";
    }

    private static string? HandleSetObj(string uidStr)
    {
        if (_world == null) return "";
        string v = uidStr.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        else if (v.StartsWith("0", StringComparison.Ordinal) && v.Length > 1)
            v = v[1..];
        if (uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            _world.ObjReference = new SphereNet.Core.Types.Serial(uid);
        else
            _world.ObjReference = SphereNet.Core.Types.Serial.Invalid;
        return "";
    }

    private static string? HandleSetObjProperty(string propAssignment)
    {
        if (_world == null) return "";
        // Format: property=value
        int eq = propAssignment.IndexOf('=');
        if (eq <= 0) return "";
        string prop = propAssignment[..eq].Trim();
        string val = propAssignment[(eq + 1)..].Trim();
        var obj = _world.FindObject(_world.ObjReference);
        obj?.TrySetProperty(prop, val);
        return "";
    }

    private static string? HandleClearVars(string prefix)
    {
        _world?.ClearGlobalVars(string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim());
        return "";
    }

    private static string? HandleNewDupe(string uidStr)
    {
        if (_world == null) return "";
        // The uid is a Sphere argument: upstream reads it with GetArgVal, so a bare
        // "10" is DECIMAL ten and only a leading zero makes it hex (CScriptObj.cpp:1311
        // -> CScript.cpp:154). Forcing hex made "10" address a completely different -
        // and perfectly valid - object, and made an item's decimal uid overflow and
        // copy nothing at all.
        if (!SphereNet.Core.Types.ScriptNumber.TryParseToken(uidStr, out long parsedUid) ||
            parsedUid is <= 0 or > uint.MaxValue)
        {
            _world.LastNewObject = SphereNet.Core.Types.Serial.Invalid;
            return "";
        }

        var original = _world.FindObject(new SphereNet.Core.Types.Serial((uint)parsedUid));
        if (original == null)
        {
            // Source-X clears m_uidNew and returns false when the source is gone
            // (CScriptObj.cpp:1311). Leaving the previous reference standing let a
            // script that checks NEW to see whether the copy worked read the OLD
            // object and treat it as the new copy.
            _world.LastNewObject = SphereNet.Core.Types.Serial.Invalid;
            return "";
        }

        if (original is SphereNet.Game.Objects.Items.Item origItem)
        {
            var clone = origItem.CreateDupe(_world);
            // Beside the source's top-level object, never inside its container
            // (MoveNearObj, CObjBase.cpp:498).
            if (!_world.PlaceItem(clone, origItem.GetDupeDropPosition()))
            {
                _world.RemoveItem(clone);
                _world.LastNewObject = SphereNet.Core.Types.Serial.Invalid;
                return "";
            }
            _world.LastNewItem = clone.Uid;
            _world.LastNewObject = clone.Uid;
        }
        else if (original is SphereNet.Game.Objects.Characters.Character origChar)
        {
            var clone = origChar.CreateDupe(_world);
            if (!_world.PlaceCharacter(clone, origChar.GetTopLevelPosition()))
            {
                _world.DeleteObject(clone);
                clone.Delete();
                _world.LastNewObject = SphereNet.Core.Types.Serial.Invalid;
                return "";
            }
            _world.LastNewChar = clone.Uid;
            _world.LastNewObject = clone.Uid;
        }
        return "";
    }

    private static string HandleServNewItem(string raw)
    {
        if (_world == null || _resources == null)
            return "0";

        // Four fields, not three: <id>,<amount>,<parent uid>,<equip flag>
        // (Source-X NEWITEM, CScriptObj.cpp:1340). Splitting into three left the
        // fourth glued onto the parent - "…,<uid>,1" made the uid unparseable, so the
        // item was created and then never placed anywhere.
        string[] parts = raw.Split(',', 4, StringSplitOptions.TrimEntries);
        string token = parts.ElementAtOrDefault(0)?.Trim() ?? "";
        if (token.Length == 0)
            return "0";

        ResourceId rid = _resources.ResolveDefName(token);
        if (!rid.IsValid)
        {
            int numeric = ValueCurve.ParseSphereNumber(token);
            rid = new ResourceId(ResType.ItemDef, numeric);
            if (_resources.GetResource(rid) == null)
                return "0";
        }
        // TEMPLATE is a valid header for NEWITEM: upstream goes through
        // CItem::CreateHeader, which accepts ITEMDEF and TEMPLATE alike and hands the
        // latter to CreateTemplate (CItem.cpp:461/554). Refusing anything but ITEMDEF
        // made a reward-bundle script's NEWITEM step return 0 and create nothing.
        if (rid.Type == ResType.Template)
            return HandleServNewFromTemplate(rid, parts);
        if (rid.Type != ResType.ItemDef)
            return "0";

        var def = DefinitionLoader.GetItemDef(rid.Index);
        ushort dispId = def?.DispIndex ?? 0;
        if (dispId == 0) dispId = def?.DupItemId ?? 0;
        if (dispId == 0 && rid.Index is > 0 and <= ushort.MaxValue)
            dispId = (ushort)rid.Index;
        if (dispId == 0)
            return "0";

        var item = _world.CreateItem();
        item.BaseId = dispId;
        item.Name = string.IsNullOrWhiteSpace(def?.Name) ? (def?.DefName ?? token) : def!.Name;
        ItemDefHelper.ApplyInstanceMetadata(item, rid.Index, setDisplayId: false,
            setName: !string.IsNullOrWhiteSpace(def?.Name));

        if (parts.Length > 1 && parts[1].Length > 0)
            item.Amount = (ushort)Math.Clamp(ValueCurve.ParseSphereNumber(parts[1]), 1, ushort.MaxValue);

        if (parts.Length > 2 && TryParseScriptUid(parts[2], out Serial parentUid))
            PlaceNewItemUnderParent(item, parentUid, def);

        // NEW names the object THIS call produced, written after everything else is
        // done (Source-X sets m_uidNew at the end of NEWITEM, CScriptObj.cpp:1381).
        // An @Create hook - or the auto-backpack below - creates its own objects and
        // moves the world's reference; without this the caller's NEW pointed at the
        // side item instead of the one it asked for.
        _world.LastNewItem = item.Uid;
        _world.LastNewObject = item.Uid;
        return $"0{item.Uid.Value:X}";
    }

    /// <summary>Bare <c>NEWITEM</c> written on an object, as opposed to the explicit
    /// <c>SERV.NEWITEM</c>. Source-X does the same creation and then, when the command
    /// was NOT addressed to the server, points the caller's ACT at what it made
    /// (CScriptObj.cpp:1383, guarded by <c>this != &amp;g_Serv</c>). Routing both forms
    /// through the server-only path left a character script working on its old ACT
    /// after creating something. Format: "callerUid|&lt;newitem args&gt;".</summary>
    private static string HandleNewItemForCaller(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe < 0)
            return HandleServNewItem(data);

        string callerUid = data[..pipe];
        string result = HandleServNewItem(data[(pipe + 1)..]);

        if (_world != null && result != "0" && TryParseSerial(callerUid, out var uid) &&
            _world.FindObject(uid) is { } caller && _world.LastNewObject.IsValid)
        {
            caller.TrySetProperty("ACT", $"0{_world.LastNewObject.Value:X}");
        }
        return result;
    }

    /// <summary>NEWITEM on a [TEMPLATE] header: build the recipe's first entry as the
    /// object the caller receives and, when that entry is a CONTAINER, create the rows
    /// after it inside it (Source-X CreateHeader -> CreateTemplate,
    /// CItem.cpp:461/554/628). The amount and parent fields apply to the object handed
    /// back, exactly as for an ITEMDEF header.</summary>
    private static string HandleServNewFromTemplate(ResourceId rid, string[] parts)
    {
        if (_world == null)
            return "0";

        int primary = TemplateEngine.ResolveTemplatePrimary(rid.Index);
        if (primary <= 0)
            return "0";

        var item = _world.CreateItem();
        if (!ItemDefHelper.ApplyInstanceMetadata(item, primary))
        {
            if (primary is <= 0 or > ushort.MaxValue)
            {
                _world.RemoveItem(item);
                return "0";
            }
            item.BaseId = (ushort)primary;
        }
        TemplateEngine.FillTemplateContents(_world, item, rid.Index);

        if (parts.Length > 1 && parts[1].Length > 0)
            item.Amount = (ushort)Math.Clamp(ValueCurve.ParseSphereNumber(parts[1]), 1, ushort.MaxValue);
        if (parts.Length > 2 && TryParseScriptUid(parts[2], out Serial templateParent))
            PlaceNewItemUnderParent(item, templateParent, DefinitionLoader.GetItemDef(primary));

        // Filling the contents created objects of its own; NEW must name the one this
        // call produced (CScriptObj.cpp:1381).
        _world.LastNewItem = item.Uid;
        _world.LastNewObject = item.Uid;
        return $"0{item.Uid.Value:X}";
    }

    /// <summary>Put a freshly made item where the NEWITEM parent field asked for.
    /// A CONTAINER parent takes it as content; a CHARACTER parent equips it at the
    /// layer its definition declares and only falls back to the pack when the
    /// definition names no layer (Source-X LoadSetContainer, CItem.cpp:2516). The
    /// character branch used to drop everything in the backpack, so a script creating
    /// a shirt straight onto someone left it unworn.</summary>
    private static void PlaceNewItemUnderParent(Item item, Serial parentUid,
        SphereNet.Scripting.Definitions.ItemDef? def)
    {
        if (_world == null)
            return;
        if (_world.FindItem(parentUid) is { } container)
        {
            container.TryAddItem(item);
            return;
        }
        if (_world.FindChar(parentUid) is not { } owner)
            return;

        Layer layer = def?.Layer is { } l and > Layer.None and < Layer.Qty ? l : Layer.None;
        if (layer != Layer.None && layer != Layer.Pack)
        {
            owner.Equip(item, layer);
            return;
        }

        if (owner.Backpack == null)
        {
            var pack = _world.CreateItem();
            pack.BaseId = 0x0E75;
            pack.ItemType = ItemType.Container;
            pack.Name = "Backpack";
            owner.Equip(pack, Layer.Pack);
        }
        owner.Backpack?.TryAddItem(item);
    }

    private static string HandleServNewNpc(string raw)
    {
        if (_world == null || _resources == null)
            return "0";
        string token = raw.Split(',', 2, StringSplitOptions.TrimEntries)[0];
        if (string.IsNullOrWhiteSpace(token))
            return "0";

        var npc = _world.CreateCharacter();
        npc.IsPlayer = false;
        bool applied = CharDefHelper.TryApplyDefName(npc, token, _resources, stats: true, refresh: false);
        if (!applied)
        {
            int numeric = ValueCurve.ParseSphereNumber(token);
            var link = _resources.GetResource(ResType.CharDef, numeric);
            if (link != null)
                applied = CharDefHelper.TryApplyDefName(npc, link.DefName ?? link.HeaderArgument, _resources,
                    stats: true, refresh: false);
        }
        if (!applied)
        {
            _world.DeleteObject(npc);
            return "0";
        }

        // A new NPC starts with FULL pools. Source-X reaches CreateNewCharCheck
        // through NPC_LoadScript (CCharNPC.cpp:30/265) and fills hits, stamina and
        // mana to their maxima there (CChar.cpp:1042); a script lowering them
        // afterwards is a separate, later act. Applying the definition only sets the
        // MAXIMA, so an NPC made this way stood at 0/100 hits and 0/80 mana and could
        // not cast. The GM add-NPC path already fills the same three pools.
        npc.Hits = npc.MaxHits;
        npc.Stam = npc.MaxStam;
        npc.Mana = npc.MaxMana;

        _world.LastNewChar = npc.Uid;
        _world.LastNewObject = npc.Uid;
        return $"0{npc.Uid.Value:X}";
    }

    private static bool TryParseScriptUid(string raw, out Serial uid)
    {
        uint value = ObjBase.ParseHexOrDecUInt(raw.Trim());
        uid = new Serial(value);
        return value != 0;
    }

    private static string? HandleSetDefMsg(string assignment)
    {
        int eq = assignment.IndexOf('=');
        if (eq <= 0)
            return "";

        string key = NormalizeDefMsgKey(assignment[..eq]);
        string value = assignment[(eq + 1)..].Trim();
        if (key.Length == 0)
            return "";

        ServerMessages.SetOverride(key, value);
        _log?.LogInformation("[script] DEFMSG.{Key} overridden at runtime", key);
        return "";
    }

    private static bool IsHearAllEnabled()
        => _config != null && (_config.LogMask & SphereConfig.LogMaskPlayerSpeak) != 0;

    private static string HandleServHearAll(string arg)
    {
        if (_config == null)
            return "0";

        string trimmed = (arg ?? "").Trim();
        bool enabled = IsHearAllEnabled();
        if (trimmed.Length == 0)
        {
            enabled = !enabled;
        }
        else
        {
            enabled = !(trimmed == "0" ||
                        trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.Equals("no", StringComparison.OrdinalIgnoreCase));
        }

        if (enabled)
            _config.LogMask |= SphereConfig.LogMaskPlayerSpeak;
        else
            _config.LogMask &= ~SphereConfig.LogMaskPlayerSpeak;

        _log?.LogInformation("[script] SERV.HEARALL - {State}", enabled ? "ON" : "OFF");
        return enabled ? "1" : "0";
    }

    /// <summary>Get property from object referenced by UID. Format: "uidHex|property"</summary>
    private static string? HandleRefGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return "0";
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "0";
        return obj.TryGetProperty(prop, out string val) ? val : "0";
    }

    /// <summary>Execute command on object referenced by UID. Format: "uidHex|command|args"</summary>
    private static string? HandleRefExec(string data)
    {
        var parts = data.Split('|', 3);
        if (parts.Length < 2 || _world == null) return "";
        string uidStr = parts[0].Trim();
        string cmd = parts[1].Trim();
        string cmdArgs = parts.Length > 2 ? parts[2].Trim() : "";
        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return "";
        var obj = _world.FindObject(new SphereNet.Core.Types.Serial(uid));
        if (obj == null) return "";
        // Try set property first, then execute command with a minimal console
        if (cmdArgs.Length > 0 && obj.TrySetProperty(cmd, cmdArgs))
            return "";
        if (obj.TryExecuteCommand(cmd, cmdArgs, new RefExecConsole()))
            return "";

        // Client-scoped verbs (DIALOG, SDIALOG, MENU, INPDLG, GUMPDISPLAY, ...)
        // are not implemented on the object itself; they live on the
        // owning player's GameClient. Imported sphere admin scripts use
        //   UID.<player>.Dialog d_X
        // to push a dialog to a specific player. Route the verb to that
        // player's client so the gump actually goes out the wire.
        if (obj is Character ch)
        {
            var gc = FindGameClient(ch);
            if (gc != null)
                gc.TryExecuteScriptCommand(obj, cmd, cmdArgs, null);
        }
        return "";
    }

    /// <summary>Execute command on a target object using a specific source
    /// client. Format: "srcUid|targetUid|command|args". This preserves
    /// Source-X dialog semantics for admin scripts: UID.target.DIALOG opens
    /// on SRC's client, with target bound as the dialog subject.</summary>
    private static string? HandleRefExecAs(string data)
    {
        var parts = data.Split('|', 4);
        if (parts.Length < 3 || _world == null) return "";
        if (!TryParseSerial(parts[0], out var srcUid) ||
            !TryParseSerial(parts[1], out var targetUid))
            return "";

        var srcChar = _world.FindChar(srcUid);
        var target = _world.FindObject(targetUid);
        if (srcChar == null || target == null)
            return "";

        string cmd = parts[2].Trim();
        string cmdArgs = parts.Length > 3 ? parts[3].Trim() : "";
        if (cmd.Length == 0)
            return "";

        var asConsole = new RefExecConsole(srcChar);
        if (target.TrySetProperty(cmd, cmdArgs))
            return "";
        if (target.TryExecuteCommand(cmd, cmdArgs, asConsole))
            return "";

        var sourceClient = FindGameClient(srcChar);
        if (sourceClient != null)
        {
            sourceClient.TryExecuteScriptCommand(target, cmd, cmdArgs,
                new SphereNet.Scripting.Execution.TriggerArgs(srcChar)
                {
                    Object1 = target,
                    Object2 = srcChar,
                });
        }

        return "";
    }

    /// <summary>Iterate all online players and invoke a script function
    /// on each. Format: "srcUid|funcName". Caller stays as src; each
    /// iterated player becomes the function's target. Sphere admin
    /// scripts use this pattern to tally online clients or push a
    /// system message to everyone.</summary>
    private static string? HandleAllClients(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "";
        string uidStr = data[..pipe].Trim();
        string payload = data[(pipe + 1)..].Trim();
        if (string.IsNullOrEmpty(payload)) return "";

        if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uidStr = uidStr[2..];
        else if (uidStr.StartsWith("0", StringComparison.Ordinal) && uidStr.Length > 1)
            uidStr = uidStr[1..];
        if (!uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint srcUid))
            return "";

        var srcObj = _world.FindObject(new SphereNet.Core.Types.Serial(srcUid));
        if (srcObj is not SphereNet.Game.Objects.Characters.Character srcChar)
            return "";

        // Source-X allows two payload shapes:
        //   serv.allclients f_count_players          → call function f_count_players
        //   serv.allclients sysmessage @0481 Booting → execute verb on every client
        // Detect verb form by splitting first token and asking the
        // target object whether it can execute it. Works for SYSMESSAGE,
        // MESSAGE, EVENTS, SHRINK, KICK, ... — the standard CChar verbs.
        int sp = payload.IndexOfAny(new[] { ' ', '\t' });
        string head = sp > 0 ? payload[..sp] : payload;
        string tail = sp > 0 ? payload[(sp + 1)..].TrimStart() : "";

        var snapshot = BuildAllClientsSnapshot();
        // Diagnostic for the admin online-player list ("admin" command tallies
        // clients into CTAG0 via this path). [allclients] shows whether SRC
        // resolved and how many clients the callback runs for — an empty list
        // there means the function never ran / SRC was wrong.
        _log.LogDebug("[allclients] src=0x{Src:X} '{SrcName}' payload='{Payload}' clients={Count}",
            srcChar.Uid.Value, srcChar.Name ?? "?", payload, snapshot.Count);
        var sourceClient = FindGameClient(srcChar);
        foreach (var (target, targetClient) in snapshot)
        {
            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(srcChar)
            {
                Object1 = target,
                Object2 = srcChar,
            };
            ITextConsole? callbackConsole = targetClient ?? sourceClient;

            // Function form (e.g. "f_Admin_GetPlayers") takes priority: a
            // [FUNCTION] with this name runs as the per-client callback, with
            // SRC = the admin who issued ALLCLIENTS. Only when no such function
            // exists is the payload treated as a verb (sysmessage, message,
            // kick, ...). TryRunFunction is its own existence check — it returns
            // false for a non-function. The previous "verb first" order let
            // TryExecuteScriptCommand swallow the function NAME (return true), so
            // the callback never ran and admin tallies like the online-player
            // list came back empty.
            bool dispatched = false;
            if (_triggerRunner != null &&
                _triggerRunner.TryRunFunction(payload, target, callbackConsole, trigArgs, out _))
                dispatched = true;
            if (!dispatched && target.TryExecuteCommand(head, tail, callbackConsole ?? new RefExecConsole()))
                dispatched = true;
            if (!dispatched && callbackConsole != null)
                callbackConsole.TryExecuteScriptCommand(target, head, tail, trigArgs);
        }
        return "";
    }

    private static List<(Character Target, GameClient? Client)> BuildAllClientsSnapshot()
    {
        return BuildAllClientsSnapshot(_world, _clients.Values.ToList(), _clientsByCharUid);
    }

    private static List<(Character Target, GameClient? Client)> BuildAllClientsSnapshot(
        GameWorld? world,
        IReadOnlyCollection<GameClient> clients,
        IReadOnlyDictionary<Serial, GameClient> indexedClients)
    {
        var snapshot = new List<(Character Target, GameClient? Client)>();
        var seen = new HashSet<Serial>();

        GameClient? ResolveClient(Character ch)
        {
            if (indexedClients.TryGetValue(ch.Uid, out var indexed) && indexed.Character == ch)
                return indexed;
            foreach (var client in clients)
            {
                if (client.Character == ch)
                    return client;
            }
            return null;
        }

        void Add(Character? ch, GameClient? client)
        {
            if (ch == null || ch.IsDeleted || !ch.IsPlayer || !ch.IsOnline)
                return;
            if (!seen.Add(ch.Uid))
                return;
            snapshot.Add((ch, client ?? ResolveClient(ch)));
        }

        if (world != null)
        {
            foreach (var ch in world.OnlinePlayers)
                Add(ch, ResolveClient(ch));
        }

        foreach (var client in clients)
        {
            if (client.IsPlaying)
                Add(client.Character, client);
        }

        return snapshot;
    }

    private static string? HandleServWriteFile(string raw)
    {
        try
        {
            string payload = raw.Trim();
            if (payload.Length == 0)
                return "0";

            string path;
            string text;
            int sep = payload.IndexOf('|');
            if (sep >= 0)
            {
                path = payload[..sep].Trim();
                text = payload[(sep + 1)..];
            }
            else
            {
                int sp = payload.IndexOfAny(new[] { ' ', '\t' });
                if (sp <= 0)
                    return "0";
                path = payload[..sp].Trim();
                text = payload[(sp + 1)..].TrimStart();
            }

            string basePath = GetScriptFilesBasePath();
            string? resolved = ResolveScriptSafePath(basePath, path);
            if (resolved == null)
                return "0";
            Directory.CreateDirectory(Path.GetDirectoryName(resolved) ?? basePath);
            File.AppendAllText(resolved, text + Environment.NewLine, Encoding.UTF8);
            return "1";
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SERV.WRITEFILE failed");
            return "0";
        }
    }

    private static string? HandleServDeleteFile(string raw)
    {
        try
        {
            return ScriptFileHandle.DeleteFile(GetScriptFilesBasePath(), raw.Trim()) ? "1" : "0";
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SERV.DELETEFILE failed");
            return "0";
        }
    }

    private static string? HandleServConsole(string raw)
    {
        string command = raw.Trim();
        if (command.Length == 0)
            return "0";

        HandleConsoleCommand(command);
        return "1";
    }

    private static string? HandleServLog(string message)
    {
        _log?.LogInformation("[script] {Message}", message);
        return "";
    }

    private static string? HandleServGmPage(string data)
    {
        string src = "";
        string reason = data;
        int pipe = data.IndexOf('|');
        if (pipe >= 0)
        {
            src = data[..pipe].Trim();
            reason = data[(pipe + 1)..].Trim();
        }

        if (_world == null)
            return "0";
        string account = src;
        if (TryParseSerial(src, out var uid) && _world.FindObject(uid) is Character ch)
            account = Character.ResolveAccountForChar?.Invoke(ch.Uid)?.Name ?? ch.Name ?? src;
        _world.AddGmPage(new GameWorld.GmPageRecord(
            account, reason, "", "open", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return _world.GmPages.Count.ToString();
    }

    private static string GetScriptFilesBasePath()
    {
        string root = _scriptDirs.Count > 0
            ? _scriptDirs[0]
            : Path.GetDirectoryName(_config?.ScpFilesDir ?? "") ?? AppContext.BaseDirectory;
        return Path.Combine(root, "files");
    }

    private static string? ResolveScriptSafePath(string basePath, string path)
    {
        string normalized = path.Trim().Trim('"');
        return SafePath.TryResolveUnderRoot(basePath, normalized, out string full, out _)
            ? full
            : null;
    }

    private static bool TryParseSerial(string raw, out Serial serial)
    {
        serial = Serial.Invalid;
        string v = raw.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            v = v[2..];
        else if (v.StartsWith('0') && v.Length > 1)
            v = v[1..];
        if (!uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return false;
        serial = new Serial(uid);
        return true;
    }

    /// <summary>Source-X <c>serv.resync</c> from a script: trigger the
    /// same hot-reload pipeline used by the telnet/console RESYNC verb.</summary>
    private static string? HandleServResync()
    {
        try { PerformScriptResync(); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Script-driven serv.resync failed");
        }
        return "";
    }

    /// <summary>Source-X <c>serv.save</c>: route to the standard world
    /// save path (<see cref="PerformSave"/>) so dialog-driven backups
    /// produce identical .scp output.</summary>
    private static string? HandleServSave()
    {
        try { PerformSave(); }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Script-driven serv.save failed");
        }
        return "";
    }

    /// <summary>Source-X <c>serv.respawn</c>: re-run every spawner (the same action
    /// the admin console RESPAWN fires). Returns the spawner count touched.</summary>
    private static string? HandleServRespawn()
    {
        try { RequestRespawnOnMainLoop(); }
        catch (Exception ex) { _log?.LogWarning(ex, "Script-driven serv.respawn failed"); }
        return "";
    }

    /// <summary>Source-X <c>serv.restock</c>: restock every vendor (admin console
    /// RESTOCK equivalent).</summary>
    private static string? HandleServRestock()
    {
        try { RequestRestockOnMainLoop(); }
        catch (Exception ex) { _log?.LogWarning(ex, "Script-driven serv.restock failed"); }
        return "";
    }

    /// <summary>Source-X <c>serv.clearlists [prefix]</c>: drop all global script
    /// lists (or those matching a prefix). Returns the number cleared — the list
    /// counterpart of <c>serv.clearvars</c>, which already existed.</summary>
    private static string? HandleServClearLists(string args)
    {
        if (_world == null) return "0";
        string? prefix = string.IsNullOrWhiteSpace(args) ? null : args.Trim();
        return _world.ClearGlobalLists(prefix).ToString();
    }

    /// <summary>Source-X <c>serv.varlist [prefix]</c>: dump global VAR entries to the
    /// server log (admin-visible). Returns the number listed. Caller-console routing
    /// is a separate step; the log dump makes it usable for diagnostics now.</summary>
    private static string? HandleServVarList(string args)
    {
        string? prefix = string.IsNullOrWhiteSpace(args) ? null : args.Trim();
        return DumpGlobalVars(prefix, line => _log?.LogInformation("{Line}", line)).ToString();
    }

    /// <summary>Resolve the caller from a serial into a text console (an online GM's
    /// client), or null when unresolved.</summary>
    private static ITextConsole? ResolveCallerConsole(string src)
    {
        if (_world != null && TryParseSerial(src, out var uid) && _world.FindObject(uid) is Character ch)
            return FindGameClient(ch);
        return null;
    }

    /// <summary>Source-X <c>serv.blockip &lt;ip&gt;[,seconds]</c> / <c>serv.unblockip &lt;ip&gt;</c>
    /// (SV_BLOCKIP/SV_UNBLOCKIP). Gated at PLEVEL_Admin like Source-X; the srcUid
    /// prefix carries the invoking character so the gate can be enforced from
    /// scripts (srcUid 0 = server/hook context, allowed). The optional decay
    /// seconds are accepted but the block is permanent until UNBLOCKIP — the
    /// block list has no timed-decay model; the request is logged.</summary>
    private static string HandleServBlockIp(string data, bool block)
    {
        int pipe = data.IndexOf('|');
        string src = pipe >= 0 ? data[..pipe].Trim() : "0";
        string argStr = (pipe >= 0 ? data[(pipe + 1)..] : data).Trim();

        var console = ResolveCallerConsole(src);
        if (src is not ("0" or ""))
        {
            if (_world == null || !TryParseSerial(src, out var uid) ||
                _world.FindObject(uid) is not Character caller ||
                caller.PrivLevel < SphereNet.Core.Enums.PrivLevel.Admin)
            {
                console?.SysMessage("You lack the privilege to manage IP blocks.");
                _log?.LogWarning("[script] SERV.{Verb} refused — caller below Admin",
                    block ? "BLOCKIP" : "UNBLOCKIP");
                return "0";
            }
        }

        var parts = argStr.Split(',', 2, StringSplitOptions.TrimEntries);
        string ip = parts.Length > 0 ? parts[0] : "";
        if (ip.Length == 0)
        {
            console?.SysMessage(block ? "Usage: BLOCKIP <address>[,seconds]" : "Usage: UNBLOCKIP <address>");
            return "0";
        }

        if (_ipBlockList == null)
            return "0";

        string msg;
        string result;
        if (block)
        {
            int decaySeconds = 0;
            if (parts.Length > 1 && int.TryParse(parts[1], out int parsed) && parsed > 0)
                decaySeconds = parsed;
            _ipBlockList.Add(ip, decaySeconds);
            msg = decaySeconds > 0
                ? $"IP blocked: {ip} (expires in {decaySeconds}s)"
                : $"IP blocked: {ip}";
            result = "1";
        }
        else if (_ipBlockList.Remove(ip))
        {
            msg = $"IP unblocked: {ip}";
            result = "1";
        }
        else
        {
            msg = $"IP not in block list: {ip}";
            result = "0";
        }

        console?.SysMessage(msg);
        _log?.LogInformation("[script] SERV.{Verb} {Msg}", block ? "BLOCKIP" : "UNBLOCKIP", msg);
        return result;
    }

    /// <summary>Source-X <c>serv.calccrypt &lt;version&gt;[,clientType][,encType]</c>
    /// (SV_CALCCRYPT → CCryptoKeyCalc::CalculateLoginKeys): derive the login
    /// crypt key pair for a client version and print the SphereCrypt.ini-style
    /// line to the invoking console (log fallback).</summary>
    private static string HandleServCalcCryptToCaller(string data)
    {
        int pipe = data.IndexOf('|');
        string src = pipe >= 0 ? data[..pipe].Trim() : "0";
        string argStr = (pipe >= 0 ? data[(pipe + 1)..] : data).Trim();

        string? line = CalcCryptLine(argStr);
        if (string.IsNullOrEmpty(line) || line == "0")
            return "0";

        var console = ResolveCallerConsole(src);
        if (console != null) console.SysMessage(line);
        else _log?.LogInformation("{Line}", line);
        return "1";
    }

    /// <summary>Port of CCryptoKeyCalc::CalculateLoginKeys + FormattedLoginKey.
    /// Args: "major.minor.revision[,clientType][,encType]" — clientType 3 (EC)
    /// offsets the major version by 63; encType 0 auto-detects from version.</summary>
    private static string? CalcCryptLine(string argStr)
    {
        var parts = argStr.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].Length == 0)
            return "0";

        var v = parts[0].Split('.');
        if (v.Length < 3 ||
            !uint.TryParse(v[0], out uint major) ||
            !uint.TryParse(v[1], out uint minor))
            return "0";

        // The revision token may carry a trailing build letter ("2.0.0x").
        string revTok = v[2];
        char buildSub = '\0';
        if (revTok.Length > 0 && char.IsLetter(revTok[^1]))
        {
            buildSub = char.ToLowerInvariant(revTok[^1]);
            revTok = revTok[..^1];
        }
        if (!uint.TryParse(revTok, out uint revision))
            return "0";
        uint build = v.Length >= 4 && uint.TryParse(v[3], out uint b) ? b : 0;

        int cliType = parts.Length >= 2 && int.TryParse(parts[1], out int ct) ? ct : 0;
        if (cliType is < 0 or > 3) cliType = 0; // CLIENTTYPE_2D..CLIENTTYPE_EC
        int encForce = parts.Length >= 3 && int.TryParse(parts[2], out int ef) ? ef : 0;

        uint keyMajor = major;
        if (cliType == 3) // CLIENTTYPE_EC: kuiECMajorVerOffset
            keyMajor += 63;

        // CCryptoKeyCalc::CalculateLoginKeysReportedVer bit mix
        uint key1, key2;
        unchecked
        {
            key1 = (keyMajor << 23) | (minor << 14) | (revision << 4);
            key1 ^= (revision * revision) << 9;
            key1 ^= minor * minor;
            key1 ^= (minor * 11) << 24;
            key1 ^= (revision * 7) << 19;
            key1 ^= 0x2C13A5FD;

            key2 = (keyMajor << 22) | (revision << 13) | (minor << 3);
            key2 ^= (revision * revision * 3) << 10;
            key2 ^= minor * minor;
            key2 ^= (minor * 13) << 23;
            key2 ^= (revision * 7) << 18;
            key2 ^= 0xA31D527F;
        }

        // GetEncryptionTypeForClient port (auto-detect when encForce is NONE)
        string encName;
        if (encForce is >= 1 and <= 4)
        {
            encName = encForce switch
            {
                1 => "ENC_BFISH",
                2 => "ENC_BTFISH",
                3 => "ENC_TFISH",
                _ => "ENC_LOGIN"
            };
        }
        else
        {
            encName = major switch
            {
                1 when minor is >= 23 and <= 25 => "ENC_LOGIN",
                1 when minor == 26 => "ENC_BFISH",
                2 when minor == 0 && revision == 0 && build == 0 =>
                    buildSub == 'x' ? "ENC_BTFISH" : "ENC_BFISH",
                2 when minor == 0 && revision <= 3 => "ENC_BTFISH",
                _ => "ENC_BFISH"
            };
        }

        // FormattedLoginKey: std::left + setfill('0') pads the fields on the
        // RIGHT with zeros — 7.0.20 renders "7002000".
        static string PadField(uint value, int width) =>
            value.ToString().PadRight(width, '0');

        string verKey = cliType == 3
            ? PadField(keyMajor, 2) + PadField(minor, 2) + PadField(revision, 2) + PadField(0, 2)
            : PadField(major, 1) + PadField(minor, 2) + PadField(revision, 2) + PadField(0, 2);

        string verString = build > 0
            ? $"{major}.{minor}.{revision}.{build}"
            : $"{major}.{minor}.{revision}";

        return $"{verKey} 0{key1:X8} 0{key2:X8} {encName} // {verString}";
    }

    /// <summary>Source-X <c>serv.b &lt;text&gt;</c> (SV_B → CWorldComm::Broadcast):
    /// send a system message to every logged-in player. Previously console-only,
    /// so announce scripts calling serv.b were silent no-ops.</summary>
    private static string HandleServBroadcast(string text)
    {
        string msg = text.Trim();
        if (msg.Length == 0) return "0";
        BroadcastToAllPlayers(msg, 0x03B2);
        _log?.LogInformation("[script] SERV.B {Text}", msg);
        return "1";
    }

    /// <summary>Source-X <c>serv.garbage</c> — the FixWeirdness world-integrity
    /// sweep (repair/delete malformed items with reason logging) followed by a
    /// managed GC pass, mirroring CWorld::GarbageCollection.</summary>
    private static string HandleServGarbage()
    {
        if (_world != null)
        {
            var (checkedCount, fixedCount, deleted) = _world.GarbageCollection(
                line => _log?.LogInformation("{Line}", line));
            _log?.LogInformation(
                "[script] SERV.GARBAGE — {Checked} items checked, {Fixed} fixed, {Deleted} deleted",
                checkedCount, fixedCount, deleted);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _log?.LogInformation("[script] SERV.GARBAGE — GC complete, memory {Mem} KB",
            GC.GetTotalMemory(true) / 1024);
        return "1";
    }

    /// <summary>Source-X SECURE toggle state: while on, script/console SHUTDOWN
    /// is refused (guards against a stray script killing the shard).</summary>
    private static bool _secureMode;
    public static bool SecureMode => _secureMode;

    /// <summary>Source-X <c>serv.shrinkmem</c> (SetProcessWorkingSetSize):
    /// mapped to a compacting managed GC pass.</summary>
    private static string HandleServShrinkMem()
    {
        long before = GC.GetTotalMemory(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(true);
        _log?.LogInformation("[script] SERV.SHRINKMEM — {Before} KB -> {After} KB",
            before / 1024, after / 1024);
        return "1";
    }

    /// <summary>Source-X <c>serv.secure</c> — toggle secure mode. While enabled,
    /// SHUTDOWN from scripts is refused.</summary>
    private static string HandleServSecure()
    {
        _secureMode = !_secureMode;
        _log?.LogInformation("[script] SERV.SECURE — secure mode {State}",
            _secureMode ? "ON" : "OFF");
        return _secureMode ? "1" : "0";
    }

    /// <summary>Source-X <c>serv.information</c> as a script command: dump the
    /// server status lines to the invoking client's console (log fallback),
    /// mirroring the admin-console INFORMATION output.</summary>
    private static string HandleServInformationToCaller(string src)
    {
        Action<string> sink;
        var console = ResolveCallerConsole(src.Trim());
        if (console != null) sink = console.SysMessage;
        else sink = line => _log?.LogInformation("{Line}", line);

        sink("SphereNet Server v1.0");
        if (_config != null) sink($"Server: {_config.ServName}");
        if (_world != null)
        {
            var (c, i, s) = _world.GetStats();
            sink($"Characters: {c}, Items: {i}, Sectors: {s}");
        }
        sink($"Accounts: {_accounts?.Count ?? 0}");
        sink($"Memory: {GC.GetTotalMemory(false) / 1024} KB");
        return "1";
    }

    /// <summary>Enumerate global VARs (optionally prefix-filtered) into a sink. Returns
    /// the count; a trailing summary line is emitted too.</summary>
    private static int DumpGlobalVars(string? prefix, Action<string> sink)
    {
        if (_world == null) return 0;
        int n = 0;
        foreach (var kv in _world.GetAllGlobalVars())
        {
            if (prefix != null && !kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            sink($"VAR.{kv.Key}={kv.Value}");
            n++;
        }
        sink($"VARLIST: {n} variable(s)");
        return n;
    }

    /// <summary>Source-X <c>serv.varlist [prefix]</c> as a command: dump the global VAR
    /// list to the invoking client's console (falling back to the server log when there
    /// is no online caller). Format: <c>srcUid|prefix</c>.</summary>
    private static string? HandleServVarListToCaller(string data)
    {
        int pipe = data.IndexOf('|');
        string src = pipe >= 0 ? data[..pipe].Trim() : "0";
        string prefixArg = pipe >= 0 ? data[(pipe + 1)..].Trim() : "";
        string? prefix = string.IsNullOrWhiteSpace(prefixArg) ? null : prefixArg;

        var console = ResolveCallerConsole(src);
        int n = console != null
            ? DumpGlobalVars(prefix, console.SysMessage)
            : DumpGlobalVars(prefix, line => _log?.LogInformation("{Line}", line));
        return n.ToString();
    }

    /// <summary>Source-X <c>serv.printlists</c>: dump global list names and sizes to
    /// the server log. Returns the number of lists.</summary>
    private static string? HandleServPrintLists()
        => DumpGlobalLists(line => _log?.LogInformation("{Line}", line)).ToString();

    private static int DumpGlobalLists(Action<string> sink)
    {
        if (_world == null) return 0;
        int n = 0;
        foreach (var kv in _world.GetAllGlobalLists())
        {
            sink($"LIST.{kv.Key} ({kv.Value.Count} entries)");
            n++;
        }
        sink($"PRINTLISTS: {n} list(s)");
        return n;
    }

    /// <summary>Source-X <c>serv.printlists</c> as a command: dump global list names and
    /// sizes to the invoking client's console (log fallback). Format: <c>srcUid</c>.</summary>
    private static string? HandleServPrintListsToCaller(string data)
    {
        var console = ResolveCallerConsole(data.Trim());
        int n = console != null
            ? DumpGlobalLists(console.SysMessage)
            : DumpGlobalLists(line => _log?.LogInformation("{Line}", line));
        return n.ToString();
    }

    private static string? HandleServExport(string data)
    {
        try
        {
            if (_world == null || _saver == null)
                return "0";

            int pipe = data.IndexOf('|');
            string src = pipe >= 0 ? data[..pipe].Trim() : "0";
            string payload = pipe >= 0 ? data[(pipe + 1)..].Trim() : data.Trim();
            if (string.IsNullOrWhiteSpace(payload))
                return "0";

            ObjBase? target = null;
            string pathArg = payload;
            if (TryParseScopedWorldOpsArgs(payload, out var scopedExport))
            {
                if (!TryParseSerial(src, out var centerUid) || _world.FindObject(centerUid) is not { } center)
                    return "0";

                string? scopedPath = ResolveWorldOpsPath(scopedExport.Path, forWrite: true);
                if (scopedPath == null)
                    return "0";

                var scope = new WorldSaver.WorldExportScope(center.Position, scopedExport.Distance, scopedExport.Flags);
                int scopedCount = _saver.ExportWorld(_world, scopedPath, scope);
                _log?.LogInformation(
                    "SERV.EXPORT wrote {Count} scoped record(s) to {Path} around 0x{Uid:X8} flags={Flags} distance={Distance}",
                    scopedCount, scopedPath, center.Uid.Value, scopedExport.Flags, scopedExport.Distance);
                return scopedCount.ToString();
            }

            string first = FirstToken(payload, out string rest);

            if (first.Equals("WORLD", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("ALL", StringComparison.OrdinalIgnoreCase))
            {
                pathArg = rest;
            }
            else if (TryParseSerial(first, out var uid) && _world.FindObject(uid) is { } explicitObj)
            {
                target = explicitObj;
                pathArg = rest;
            }
            else if (TryParseSerial(src, out var srcUid))
            {
                target = _world.FindObject(srcUid);
            }

            string? path = ResolveWorldOpsPath(pathArg, forWrite: true);
            if (path == null)
                return "0";

            int count = target != null
                ? _saver.ExportObject(target, path)
                : _saver.ExportWorld(_world, path);
            _log?.LogInformation("SERV.EXPORT wrote {Count} record(s) to {Path}", count, path);
            return count.ToString();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SERV.EXPORT failed");
            return "0";
        }
    }

    private static string? HandleServLoad(string args)
    {
        try
        {
            if (_world == null || _loader == null)
                return "0";

            string? path = ResolveWorldOpsPath(args, forWrite: false);
            if (path == null || !File.Exists(path))
                return "0";

            var (items, chars) = _loader.LoadFile(_world, path, _accounts);
            InitializeSpawnItems();
            _spellEngine?.RestorePersistedEffectsFromWorld();
            int total = items + chars;
            _log?.LogInformation("SERV.LOAD imported {Items} item(s), {Chars} char(s) from {Path}",
                items, chars, path);
            return total.ToString();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SERV.LOAD/IMPORT failed");
            return "0";
        }
    }

    private static string? HandleServImport(string data)
    {
        try
        {
            if (_world == null || _loader == null)
                return "0";

            int pipe = data.IndexOf('|');
            string centerUidText = pipe >= 0 ? data[..pipe].Trim() : "0";
            string payload = pipe >= 0 ? data[(pipe + 1)..].Trim() : data.Trim();
            if (string.IsNullOrWhiteSpace(payload))
                return "0";

            WorldLoader.WorldImportScope? scope = null;
            string pathArg = payload;
            if (TryParseScopedWorldOpsArgs(payload, out var scopedImport))
            {
                if (!TryParseSerial(centerUidText, out var centerUid) ||
                    _world.FindObject(centerUid) is not { } center)
                    return "0";

                scope = new WorldLoader.WorldImportScope(center.Position, scopedImport.Distance, scopedImport.Flags);
                pathArg = scopedImport.Path;
            }

            string? path = ResolveWorldOpsPath(pathArg, forWrite: false);
            if (path == null || !File.Exists(path))
                return "0";

            var (items, chars) = _loader.LoadFile(_world, path, _accounts, scope);
            InitializeSpawnItems();
            _spellEngine?.RestorePersistedEffectsFromWorld();
            int total = items + chars;
            _log?.LogInformation(
                "SERV.IMPORT imported {Items} item(s), {Chars} char(s) from {Path}{Scope}",
                items, chars, path, scope.HasValue ? " with scope" : "");
            return total.ToString();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SERV.IMPORT failed");
            return "0";
        }
    }

    private static string? HandleServRestore(string args)
    {
        try
        {
            if (_world == null || _loader == null)
                return "0";

            string? path = ResolveWorldOpsPath(args, forWrite: false);
            if (path == null || !File.Exists(path))
                return "0";

            Func<IReadOnlyList<ObjBase>, string, int>? backupWriter =
                _saver != null ? _saver.ExportObjects : null;
            var (items, chars, replaced) = _loader.RestoreFile(_world, path, _accounts, backupWriter);
            InitializeSpawnItems();
            _spellEngine?.RestorePersistedEffectsFromWorld();
            int total = items + chars;
            _log?.LogInformation(
                "SERV.RESTORE restored {Items} item(s), {Chars} char(s), replaced {Replaced} object(s) from {Path}",
                items, chars, replaced, path);
            return total.ToString();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SERV.RESTORE failed");
            return "0";
        }
    }

    private static string? HandleServSaveStatics(string args)
    {
        try
        {
            if (_world == null || _saver == null)
                return "0";

            string pathArg = string.IsNullOrWhiteSpace(args) ? "spherestatics.scp" : args;
            string? path = ResolveWorldOpsPath(pathArg, forWrite: true);
            if (path == null)
                return "0";

            int count = _saver.ExportStatics(_world, path);
            _log?.LogInformation("SERV.SAVESTATICS wrote {Count} static item(s) to {Path}", count, path);
            return count.ToString();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SERV.SAVESTATICS failed");
            return "0";
        }
    }

    private static string FirstToken(string text, out string rest)
    {
        string trimmed = text.Trim();
        int sp = trimmed.IndexOfAny(new[] { ' ', '\t' });
        if (sp < 0)
        {
            rest = "";
            return trimmed;
        }
        rest = trimmed[(sp + 1)..].TrimStart();
        return trimmed[..sp];
    }

    private readonly record struct ScopedWorldOpsArgs(string Path, int Flags, int Distance);

    private static bool TryParseScopedWorldOpsArgs(string payload, out ScopedWorldOpsArgs args)
    {
        args = default;
        string[] parts;

        if (payload.Contains(',', StringComparison.Ordinal))
        {
            parts = payload.Split(',', StringSplitOptions.TrimEntries);
        }
        else
        {
            parts = payload.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) ||
            !int.TryParse(parts[1], out int flags) ||
            !int.TryParse(parts[2], out int distance) ||
            flags <= 0 || distance < 0)
            return false;

        flags &= 3;
        if (flags == 0)
            return false;

        args = new ScopedWorldOpsArgs(parts[0], flags, distance);
        return true;
    }

    private static string? ResolveWorldOpsPath(string raw, bool forWrite)
    {
        string path = raw.Trim().Trim('"');
        if (path.Length == 0)
            return null;

        if (!path.EndsWith(".scp", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".scp.gz", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".sbin", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".sbin.gz", StringComparison.OrdinalIgnoreCase))
            path += ".scp";

        string basePath = ResolvePath(AppDomain.CurrentDomain.BaseDirectory,
            _config?.WorldSaveDir ?? "save/");
        if (!SafePath.TryResolveUnderRoot(basePath, path, out string full, out _))
            return null;
        if (forWrite)
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? Path.GetFullPath(basePath));
        return full;
    }

    /// <summary>Source-X <c>serv.shutdown [seconds]</c>. Without an arg,
    /// shut down immediately; with a delay, schedule a single-shot
    /// timer. The scheduling is best-effort — the server tick loop is
    /// what actually exits when <c>_running</c> flips to false.</summary>
    private static string? HandleServShutdown(string args)
    {
        // Secure mode (SERV.SECURE) refuses a script-initiated shutdown.
        if (_secureMode)
        {
            _log?.LogWarning("[script] SERV.SHUTDOWN refused — secure mode is ON");
            return "0";
        }

        int delaySec = 0;
        if (!string.IsNullOrWhiteSpace(args))
            int.TryParse(args.Trim(), out delaySec);

        if (delaySec <= 0)
        {
            _running = false;
            return "";
        }

        // Detached delay; we don't await it because the resolver runs on
        // the script tick path and must return synchronously.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delaySec * 1000);
                _running = false;
            }
            catch { /* ignore */ }
        });
        return "";
    }

    /// <summary>Get property from region referenced by UID. Format: "uid|property"</summary>
    private static string? HandleRegionGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (!uint.TryParse(uidStr, out uint regionUid))
            return "0";
        var region = _world.FindRegionByUid(regionUid);
        if (region == null) return "0";
        return region.TryGetProperty(prop, out string val) ? val : "0";
    }

    /// <summary>Get property from room referenced by UID. Format: "uid|property"</summary>
    private static string? HandleRoomGet(string data)
    {
        int pipe = data.IndexOf('|');
        if (pipe <= 0 || _world == null) return "0";
        string uidStr = data[..pipe].Trim();
        string prop = data[(pipe + 1)..].Trim();
        if (!uint.TryParse(uidStr, out uint roomUid))
            return "0";
        var room = _world.FindRoomByUid(roomUid);
        if (room == null) return "0";
        return room.TryGetProperty(prop, out string val) ? val : "0";
    }

    private static string? ResolveRtimeFormat(string property)
    {
        // RTIME.FORMAT <format> — format current time
        // Property arrives as "RTIME.FORMAT <format>" or just "RTIME.FORMAT"
        int spaceIdx = property.IndexOf(' ');
        if (spaceIdx < 0)
            return DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy");
        string fmt = property[(spaceIdx + 1)..].Trim();
        return FormatStrftime(DateTime.Now, fmt);
    }

    /// <summary>Format a DateTime with a C strftime-style pattern, the way
    /// Source-X CSTime::Format does (CSTime.cpp:289). The scripts pass POSIX
    /// tokens (%Y %m %d %H %M %w ...) — feeding those straight to
    /// DateTime.ToString mis-read %m as minutes, %H:%M as hour:month, and
    /// threw on %Y/%w, so every pack date/time function produced garbage.</summary>
    private static string FormatStrftime(DateTime dt, string strftimeFmt)
    {
        if (string.IsNullOrEmpty(strftimeFmt) || !strftimeFmt.Contains('%'))
        {
            // No POSIX tokens — treat as a .NET pattern (back-compat).
            try { return dt.ToString(strftimeFmt); }
            catch { return dt.ToString("ddd MMM dd HH:mm:ss yyyy"); }
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(strftimeFmt.Length + 16);
        for (int i = 0; i < strftimeFmt.Length; i++)
        {
            if (strftimeFmt[i] != '%' || i + 1 >= strftimeFmt.Length)
            {
                sb.Append(strftimeFmt[i]);
                continue;
            }
            char c = strftimeFmt[++i];
            sb.Append(c switch
            {
                'Y' => dt.ToString("yyyy", inv),
                'y' => dt.ToString("yy", inv),
                'm' => dt.ToString("MM", inv),
                'd' => dt.ToString("dd", inv),
                'e' => dt.Day.ToString(inv).PadLeft(2),
                'H' => dt.ToString("HH", inv),
                'I' => dt.ToString("hh", inv),
                'M' => dt.ToString("mm", inv),
                'S' => dt.ToString("ss", inv),
                'p' => dt.ToString("tt", inv),
                'A' => dt.ToString("dddd", inv),
                'a' => dt.ToString("ddd", inv),
                'B' => dt.ToString("MMMM", inv),
                'b' or 'h' => dt.ToString("MMM", inv),
                'j' => dt.DayOfYear.ToString("D3", inv),
                'w' => ((int)dt.DayOfWeek).ToString(inv),          // 0=Sunday
                'u' => (((int)dt.DayOfWeek + 6) % 7 + 1).ToString(inv), // 1=Mon..7=Sun
                'n' => "\n",
                't' => "\t",
                '%' => "%",
                _ => "%" + c, // unknown token: leave verbatim (strftime is impl-defined)
            });
        }
        return sb.ToString();
    }

    private static string? ResolveRticksFormat(string property)
    {
        // RTICKS.FORMAT <timestamp>,<format>
        var parts = property.Split(' ', 2);
        if (parts.Length < 2) return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var args = parts[1].Split(',', 2);
        if (args.Length < 2 || !long.TryParse(args[0].Trim(), out long ts))
            return "0";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
            return FormatStrftime(dt, args[1].Trim());
        }
        catch { return "0"; }
    }

    private static string? ResolveRticksFromTime(string property)
    {
        // RTICKS.FROMTIME <year>,<month>,<day>,<hour>,<min>,<sec>
        var parts = property.Split(' ', 2);
        if (parts.Length < 2) return "0";
        var args = parts[1].Split(',');
        if (args.Length < 3) return "0";
        try
        {
            int year = int.Parse(args[0].Trim());
            int month = int.Parse(args[1].Trim());
            int day = int.Parse(args[2].Trim());
            int hour = args.Length > 3 ? int.Parse(args[3].Trim()) : 0;
            int min = args.Length > 4 ? int.Parse(args[4].Trim()) : 0;
            int sec = args.Length > 5 ? int.Parse(args[5].Trim()) : 0;
            var dt = new DateTimeOffset(year, month, day, hour, min, sec, TimeSpan.Zero);
            return dt.ToUnixTimeSeconds().ToString();
        }
        catch { return "0"; }
    }

    /// <summary>Resolve <c>SERV.MAP(x,y,z,m).Region.Name</c> and similar
    /// Source-X function-form lookups. Splits the args inside parens on
    /// commas, looks up the region containing the point, then forwards
    /// the trailing <c>.Region.X</c> sub-property chain to the region
    /// object via <see cref="HandleRegionGet"/>.</summary>
    private static string? ResolveServMapPoint(string rest)
    {
        // rest is e.g. "1455,1612,0,0).Region.Name" — strip up to ')'.
        int close = rest.IndexOf(')');
        if (close < 0) return "0";
        string argList = rest[..close];
        string trailing = rest[(close + 1)..];
        if (trailing.StartsWith('.')) trailing = trailing[1..];

        var parts = argList.Split(',');
        if (parts.Length < 2) return "0";

        if (!int.TryParse(parts[0].Trim(), out int x)) return "0";
        if (!int.TryParse(parts[1].Trim(), out int y)) return "0";
        int z = parts.Length > 2 && int.TryParse(parts[2].Trim(), out int parsedZ) ? parsedZ : 0;
        int m = parts.Length > 3 && int.TryParse(parts[3].Trim(), out int parsedM) ? parsedM : 0;

        var pt = new SphereNet.Core.Types.Point3D((short)x, (short)y, (sbyte)z, (byte)m);
        if (_world == null) return "0";

        // Bare "MAP(x,y,z,m)" or trailing "P": just echo the point.
        if (string.IsNullOrEmpty(trailing) || trailing.Equals("P", StringComparison.OrdinalIgnoreCase))
            return pt.ToString();

        // REGION[.prop] — delegate to the existing HandleRegionGet path.
        if (trailing.StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
        {
            var region = _world.FindRegion(pt);
            if (region == null) return "";
            string regionRest = trailing.Length > 6 && trailing[6] == '.' ? trailing[7..] : "";
            if (string.IsNullOrEmpty(regionRest)) return region.Name;
            return region.TryGetProperty(regionRest, out string rv) ? rv : "0";
        }

        // ROOM[.prop] — same idea but no per-point room lookup engine yet.
        return null;
    }

    private static string? ResolveServMapSector(string rest)
    {
        // MAP.0.SECTOR.n or MAP.0.SECTOR.n.property or MAP.0.ALLSECTORS
        int firstDot = rest.IndexOf('.');
        if (firstDot < 0) return null;

        string mapStr = rest[..firstDot];
        if (!int.TryParse(mapStr, out int mapNum)) return null;

        string sub = rest[(firstDot + 1)..];

        if (sub.StartsWith("SECTOR.", StringComparison.OrdinalIgnoreCase))
        {
            string sectorPart = sub[7..]; // after "SECTOR."
            int propDot = sectorPart.IndexOf('.');
            string sectorIdxStr = propDot >= 0 ? sectorPart[..propDot] : sectorPart;
            if (!int.TryParse(sectorIdxStr, out int sectorIdx)) return null;

            // The linear index is resolved against THAT map's own sector grid
            // (CWorldMap::GetSectorByIndex, CWorldMap.cpp:229). A hardcoded 96 columns
            // is only right for a 6144-wide map: on any other size the index landed in
            // the wrong row, and past the first row it addressed a sector that does not
            // exist.
            var sector = _world?.GetSectorByIndex(mapNum, sectorIdx);
            if (sector == null) return "0";

            if (propDot < 0) return sector.GetName(); // just "MAP.0.SECTOR.n" — return name

            string prop = sectorPart[(propDot + 1)..];
            if (sector.TryGetProperty(prop, out string val))
                return val;
            return "0";
        }

        return null;
    }

    private static string? ResolveMapListProperty(string rest)
    {
        // MAPLIST.0 → 1 (valid), MAPLIST.0.BOUND.X → max X, etc.
        int dotIdx = rest.IndexOf('.');
        string mapStr = dotIdx >= 0 ? rest[..dotIdx] : rest;
        if (!int.TryParse(mapStr, out int mapNum))
            return null;

        // Answered from the maps this world actually loaded and their real sizes
        // (IsMapSupported / GetMapSize / MapSectorsData, CServerConfig.cpp:1710). The
        // old answers were map-0-only and hardcoded to 6144x4096, so a shard on a
        // second facet or a custom-sized map read a world that is not there.
        if (_world == null || !_world.TryGetMapSize(mapNum, out int width, out int height))
            return "0";

        if (dotIdx < 0)
            return "1"; // map exists

        _world.TryGetSectorGrid(mapNum, out int cols, out int rows);
        string sub = rest[(dotIdx + 1)..].ToUpperInvariant();
        return sub switch
        {
            "BOUND.X" => width.ToString(),
            "BOUND.Y" => height.ToString(),
            "CENTER.X" => (width / 2).ToString(),
            "CENTER.Y" => (height / 2).ToString(),
            "SECTOR.SIZE" => SphereNet.Game.World.Sectors.Sector.SectorSize.ToString(),
            "SECTOR.COLS" => cols.ToString(),
            "SECTOR.ROWS" => rows.ToString(),
            "SECTOR.QTY" => (cols * rows).ToString(),
            _ => null
        };
    }

    /// <summary>Resolve <c>SERV.LOOKUPSKILL &lt;name&gt;</c>. Accepts either the
    /// enum name ("Alchemy") or the defname stored in a loaded SKILL block.
    /// Returns the numeric skill id, or "-1" if no match.</summary>
    private static string? ResolveLookupSkill(string name)
    {
        string trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return "-1";

        // Enum name match first (case-insensitive)
        if (Enum.TryParse<SphereNet.Core.Enums.SkillType>(trimmed, true, out var sk)
            && sk != (SphereNet.Core.Enums.SkillType)(-1))
        {
            return ((int)sk).ToString();
        }

        // Defname lookup via the resource holder: SKILLs register under
        // their defname ("Skill_Alchemy" or "Alchemy") so the resolver
        // can map script names back to the enum slot.
        if (_resources != null)
        {
            var rid = _resources.ResolveDefName(trimmed);
            if (rid.IsValid && rid.Type == SphereNet.Core.Enums.ResType.SkillDef)
                return rid.Index.ToString();
        }

        return "-1";
    }

    private static string? ResolveServAccount(string rest)
    {
        if (_accounts == null) return null;

        // Split "<key>.<subprop>" — the key is an account NAME or a zero-based INDEX,
        // and the optional sub-property is read off the resolved account.
        int dot = rest.IndexOf('.');
        string key = dot >= 0 ? rest[..dot] : rest;
        string subProp = dot >= 0 ? rest[(dot + 1)..] : "";

        // Name takes priority (a name that happens to be numeric still wins).
        var acct = _accounts.FindAccount(key);
        bool numericKey = int.TryParse(key, out int idx) && idx >= 0;
        if (acct == null && numericKey)
            acct = _accounts.GetByIndex(idx); // SERV.ACCOUNT.n — indexed access

        if (acct == null)
            // An out-of-range index resolves to "0" (admin dialogs iterate past the
            // end); an unknown name falls through so other resolvers can try.
            return numericKey ? "0" : null;

        if (subProp.Length == 0)
            return acct.Name;
        return acct.TryGetProperty(subProp, out string value) ? value : "0";
    }


    /// <summary>Minimal ITextConsole for REF command execution.</summary>
    /// <summary>The console a bridged verb runs under. With no character it is the
    /// server (Source-X g_Serv); given one it SPEAKS FOR that character, so a verb
    /// that needs its caller - the pSrc-&gt;GetChar() step, CItem.cpp:3574 - finds it.
    /// TRYSRC relies on this: it changes who is asking without changing the target,
    /// and a source-less console made every such verb refuse.</summary>
    private sealed class RefExecConsole(Character? sourceChar = null) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => sourceChar?.PrivLevel ?? PrivLevel.Admin;
        public string GetName() => sourceChar?.Name ?? "SERVER";
        public void SysMessage(string text) { }
        public IScriptObj? GetSourceChar() => sourceChar;
    }

    private sealed class ServerHookContext : IScriptObj
    {
        public string GetName() => "SERVER";

        public bool TryGetProperty(string key, out string value)
        {
            value = key.Equals("NAME", StringComparison.OrdinalIgnoreCase) ? "SERVER" : "";
            return key.Equals("NAME", StringComparison.OrdinalIgnoreCase);
        }

        public bool TryExecuteCommand(string key, string args, ITextConsole source) => false;

        public bool TrySetProperty(string key, string value) => false;

        public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) => TriggerResult.Default;
    }
}
