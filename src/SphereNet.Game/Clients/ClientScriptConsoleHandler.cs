using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Objects;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

/// <summary>
/// Script-verb handler extracted from the GameClient.ScriptConsole partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Hosts the script-command surface (TryExecuteScriptCommand verbs: MESSAGE,
/// SAYUA, INPDLG, TRYSRC, TARGETF, MENU, DIALOG, SERV.*, FILE.*, DB.*/LDB.*,
/// SENDPACKET), script-variable resolution and the FOR* object queries.
/// Method bodies moved verbatim; the private context shims below enumerate
/// exactly what this handler needs from GameClient. The ITextConsole
/// implementation (SysMessage/GetName/GetPrivLevel) stays on GameClient -
/// scripts receive the GameClient as their console identity.
/// </summary>
public sealed class ClientScriptConsoleHandler
{
    private readonly IClientContext _client;

    internal ClientScriptConsoleHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    // Source-X old-style menu (0x7C) hard cap: MAX_MENU_ITEMS is 64 and the
    // builders truncate at MAX_MENU_ITEMS-1, so at most 63 selectable entries.
    private const int MenuMaxItems = 63;

    private NetState _netState => _client.NetState;
    private Account? _account => _client.Account;
    private CommandHandler? _commands => _client.Cmds;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private ILogger _logger => _client.Log;
    private ScriptFileHandle? _scriptFile => _client.ScriptFile;
    private ScriptDbAdapter? _scriptDb => _client.ScriptDb;
    private ScriptDbAdapter? _scriptLdb => _client.ScriptLdb;
    private ScriptDbAdapter? _scriptMdb => _client.ScriptMdb;
    private string _scriptDatabaseRoot => _client.ScriptDatabaseRoot;
    private ClientTargetState Targets => _client.Targets;
    private ClientGumpRegistry Gumps => _client.Gumps;
    private ClientDialogHandler Dialogs => _client.Dialogs;
    private int _dialogDepth;
    private ushort _pendingMenuId { get => _client.PendingMenuId; set => _client.PendingMenuId = value; }
    private string _pendingMenuDefname { get => _client.PendingMenuDefname; set => _client.PendingMenuDefname = value; }
    private List<MenuOptionEntry>? _pendingMenuOptions { get => _client.PendingMenuOptions; set => _client.PendingMenuOptions = value; }
    private string? _pendingDialogCloseFunction
    {
        get => _client.PendingDialogCloseFunction;
        set => _client.PendingDialogCloseFunction = value;
    }
    private string _pendingDialogArgs
    {
        get => _client.PendingDialogArgs;
        set => _client.PendingDialogArgs = value;
    }
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null) => _client.SendGump(gump, callback);
    private void OpenVendorBuy(Character vendor) => _client.OpenVendorBuy(vendor);
    private void OpenBankBox() => _client.OpenBankBox();
    private bool CloseScriptDialog(string dialogId) => _client.CloseScriptDialog(dialogId);
    private bool IsScriptDialogOpen(string dialogId) => _client.IsScriptDialogOpen(dialogId);
    private bool OpenNamedDialog(string dialogId, int requestedPage = 0, ObjBase? subject = null) => _client.OpenNamedDialog(dialogId, requestedPage, subject);
    private void ClearPendingTargetState() => _client.ClearPendingTargetState();
    private void Resync() => _client.Resync();
    private void BroadcastDrawObject(Character ch) => _client.BroadcastDrawObject(ch);
    private void SendInputPromptGump(IScriptObj target, string propName, int maxLength) => _client.SendInputPromptGump(target, propName, maxLength);
    private void SendScriptPrompt(IScriptObj target, string functionName, string message) => _client.SendScriptPrompt(target, functionName, message);
    private void BeginXVerbTarget(string verb, string args) => _client.BeginXVerbTarget(verb, args);
    private bool TryFindMenuSection(string menuDefname, out SphereNet.Scripting.Parsing.ScriptSection menuSection) => _client.TryFindMenuSection(menuDefname, out menuSection);
    private static bool IsPlainDefToken(string token) => GameClient.IsPlainDefToken(token);

    /// <summary>Unimplemented SERV.* verbs seen this server run — used to
    /// warn-log only the first occurrence of each.</summary>
    private static readonly HashSet<string> s_unknownServVerbs = [];

    /// <summary>Vendor for the script BUY verb: the current dialog subject
    /// when it's a vendor NPC, otherwise the nearest vendor in range.</summary>
    private Character? ResolveVendorContext()
    {
        if (_character == null)
            return null;
        if (Dialogs.DialogSubjectUid.IsValid &&
            _world.FindChar(Dialogs.DialogSubjectUid) is { } subject &&
            Trade.VendorEngine.IsVendorLike(subject))
            return subject;

        Character? nearest = null;
        int bestDist = int.MaxValue;
        foreach (var ch in _world.GetCharsInRange(_character.Position, 8))
        {
            if (ch.IsDeleted || !Trade.VendorEngine.IsVendorLike(ch))
                continue;
            int dist = _character.Position.GetDistanceTo(ch.Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = ch;
            }
        }
        return nearest;
    }

    public bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs)
    {
        if (_character == null) return false;

        string cmd = key.Trim();
        string upper = cmd.ToUpperInvariant();

        if (upper == "ADDCLILOC")
        {
            string[] parts = args.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && uint.TryParse(parts[0], out uint cliloc))
                _client.ScriptTooltipProperties?.Add((cliloc, parts.Length > 1 ? parts[1] : ""));
            return true;
        }

        if (upper == "ADDCONTEXTENTRY")
        {
            string[] parts = args.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && ushort.TryParse(parts[0], out ushort tag) &&
                uint.TryParse(parts[1], out uint cliloc))
            {
                ushort flags = parts.Length >= 3 && ushort.TryParse(parts[2], out ushort parsedFlags)
                    ? parsedFlags : (ushort)0;
                _client.ScriptContextEntries?.Add((tag, cliloc, flags));
            }
            return true;
        }

        if (upper is "PROMPTCONSOLE" or "PROMPTCONSOLEU")
        {
            string raw = args.Trim();
            int split = raw.IndexOfAny([' ', '\t', ',']);
            string function = split < 0 ? raw : raw[..split].Trim();
            string message = split < 0 ? "Enter text:" : raw[(split + 1)..].Trim(' ', '\t', ',');
            SendScriptPrompt(target, function, message);
            return true;
        }

        if (upper == "CHANGEFACE")
        {
            // Source-X sends an empty gump with context 0x2B0. Enhanced clients
            // populate that context with their native face picker and return the
            // selected face item graphic as the button id.
            var facePicker = new GumpBuilder(_character.Uid.Value, 0x2B0, 0, 0)
            {
                ExplicitX = 50,
                ExplicitY = 50
            };
            SendGump(facePicker, (buttonId, _, _) =>
            {
                if (buttonId is < 0x3B44 or > 0x3B4D || _character.IsDeleted)
                    return;

                _character.GetEquippedItem(Layer.Face)?.Delete();
                var face = _world.CreateItem();
                face.BaseId = (ushort)buttonId;
                face.Hue = _character.Hue;
                face.Name = "face";
                if (!_character.Equip(face, Layer.Face))
                    face.Delete();
            });
            return true;
        }

        if (upper == "OBJ")
        {
            if (triggerArgs?.Object1 is Character objCh)
                _character.SetTag("OBJ", $"0{objCh.Uid.Value:X}");
            else if (triggerArgs?.Object1 is Item objItem)
                _character.SetTag("OBJ", $"0{objItem.Uid.Value:X}");
            return true;
        }

        // Source-X SET meta-verb: "Src.set <verb> [args]" pops a target
        // cursor and re-dispatches the verb against the picked object.
        // Sphere admin dialogs lean on this for "set dupe", "set
        // remove", "set xinfo" rows on the player tweak panel.
        if (upper == "SET" || upper == "SETUID")
        {
            string raw = args?.Trim() ?? "";
            if (raw.Length == 0) return true;
            int sp = raw.IndexOfAny(new[] { ' ', '\t' });
            string verb = sp > 0 ? raw[..sp] : raw;
            string verbArgs = sp > 0 ? raw[(sp + 1)..].TrimStart() : "";
            BeginXVerbTarget(verb, verbArgs);
            return true;
        }

        // Sphere MESSAGE command: overhead text on the target object.
        // Syntax: message @<hue>[,<type>,<font>] <text>
        //   e.g.  message @0481,1,1 [Nimloth]
        //   e.g.  message @080a [Invis]
        if (upper is "MESSAGE" or "MSG")
        {
            string raw = args.Trim();
            ushort hue = 0x03B2;
            byte speechType = 0; // normal overhead speech
            ushort font = 3;
            string text = raw;

            if (raw.StartsWith('@'))
            {
                int spaceIdx = raw.IndexOf(' ');
                string colorSpec = spaceIdx > 0 ? raw[1..spaceIdx] : raw[1..];
                text = spaceIdx > 0 ? raw[(spaceIdx + 1)..].Trim() : "";

                var colorParts = colorSpec.Split(',');
                if (colorParts.Length >= 1)
                {
                    string huePart = colorParts[0].Trim();
                    if (huePart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        ushort.TryParse(huePart.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hx))
                    {
                        hue = hx;
                    }
                    else if (ushort.TryParse(huePart, System.Globalization.NumberStyles.HexNumber, null, out ushort hHex))
                    {
                        hue = hHex;
                    }
                    else if (ushort.TryParse(huePart, out ushort hDec))
                    {
                        hue = hDec;
                    }
                }
                if (colorParts.Length >= 2 && byte.TryParse(colorParts[1], out byte t))
                    speechType = t;
                if (colorParts.Length >= 3 && ushort.TryParse(colorParts[2], out ushort f))
                    font = f;
            }

            // Sphere compatibility: MESSAGE should appear overhead on target.
            // Many script packs use type=1 or type=6 here, but UO clients can render
            // those as system/label text instead of overhead speech.
            if (speechType is 1 or 6)
                speechType = 0;

            if (text.Length > 0)
            {
                uint serial = _character.Uid.Value;
                ushort bodyId = _character.BodyId;
                Point3D origin = _character.Position;
                if (target is Character ch)
                {
                    serial = ch.Uid.Value;
                    bodyId = ch.BodyId;
                    origin = ch.Position;
                }
                else if (target is Item item)
                {
                    serial = item.Uid.Value;
                    bodyId = 0;
                    origin = item.Position;
                }
                var packet = new PacketSpeechUnicodeOut(serial, bodyId, speechType, hue, font,
                    "TRK", target.GetName(), text);
                _netState.Send(packet);
                BroadcastNearby?.Invoke(origin, 18, packet, _character.Uid.Value);
            }
            return true;
        }

        // Source-X MESSAGEUA hue,mode,font,lang,text: Unicode overhead bark
        // sent only to the invoking client (unlike SAYUA, which is audible to
        // nearby observers). MSG above is the legacy alias of MESSAGE.
        if (upper == "MESSAGEUA")
        {
            string[] parts = args.Split(',', 5, StringSplitOptions.TrimEntries);
            if (parts.Length < 5)
                return true;

            ushort hue = TryParseScriptNumber(parts[0], out long h)
                ? (ushort)Math.Clamp(h, ushort.MinValue, ushort.MaxValue)
                : (ushort)0x03B2;
            byte speechType = TryParseScriptNumber(parts[1], out long t)
                ? (byte)Math.Clamp(t, byte.MinValue, byte.MaxValue)
                : (byte)0;
            ushort font = TryParseScriptNumber(parts[2], out long f)
                ? (ushort)Math.Clamp(f, ushort.MinValue, ushort.MaxValue)
                : (ushort)3;
            string lang = parts[3].Length > 0 ? parts[3] : "ENU";
            string text = parts[4];
            if (text.Length == 0)
                return true;

            uint serial = _character.Uid.Value;
            ushort bodyId = _character.BodyId;
            if (target is Character messageChar)
            {
                serial = messageChar.Uid.Value;
                bodyId = messageChar.BodyId;
            }
            else if (target is Item messageItem)
            {
                serial = messageItem.Uid.Value;
                bodyId = 0;
            }

            _netState.Send(new PacketSpeechUnicodeOut(
                serial, bodyId, speechType, hue, font, lang, target.GetName(), text));
            return true;
        }

        // SAYUA — overhead speech with hue/type/font/lang
        // Format: sayua <hue>,<type>,<font>,<lang> <text>
        if (upper == "SAYUA")
        {
            string raw = args.Trim();
            int firstSpace = raw.IndexOf(' ');
            ushort hue = 0x03B2;
            byte speechType = 0;
            ushort font = 3;
            string text = raw;

            if (firstSpace > 0)
            {
                string paramsPart = raw[..firstSpace];
                text = raw[(firstSpace + 1)..].TrimStart();
                string[] parms = paramsPart.Split(',');
                if (parms.Length > 0 && ushort.TryParse(parms[0], out ushort h)) hue = h;
                if (parms.Length > 1 && byte.TryParse(parms[1], out byte t)) speechType = t;
                if (parms.Length > 2 && ushort.TryParse(parms[2], out ushort f)) font = f;
            }

            if (text.Length > 0)
            {
                uint serial = _character.Uid.Value;
                ushort bodyId = _character.BodyId;
                Point3D origin = _character.Position;
                if (target is Character ch)
                {
                    serial = ch.Uid.Value;
                    bodyId = ch.BodyId;
                    origin = ch.Position;
                }
                else if (target is Item item)
                {
                    serial = item.Uid.Value;
                    bodyId = 0;
                    origin = item.Position;
                }
                var packet = new PacketSpeechUnicodeOut(serial, bodyId, speechType, hue, font,
                    "TRK", target.GetName(), text);
                _netState.Send(packet);
                BroadcastNearby?.Invoke(origin, 18, packet, _character.Uid.Value);
            }
            return true;
        }

        // INPDLG <prop> <maxLength> — open a Source-X style text-entry
        // gump on this client. The reply (0xAC) writes the user-typed
        // value into <prop> on the script verb's target object.
        // Source-X: CObjBase.cpp:OV_INPDLG → CClient::addGumpInputVal.
        if (upper == "INPDLG")
        {
            string raw = args.Trim();
            if (raw.Length == 0)
                return true;

            string propName;
            int maxLen = 1;
            int sp = raw.IndexOf(' ');
            if (sp > 0)
            {
                propName = raw[..sp].Trim();
                if (!int.TryParse(raw[(sp + 1)..].Trim(), out maxLen) || maxLen <= 0)
                    maxLen = 1;
            }
            else
            {
                propName = raw;
            }

            SendInputPromptGump(target, propName, maxLen);
            return true;
        }

        if (upper == "TRYSRC")
        {
            // Source-X compatibility: execute the provided verb line against SRC,
            // but never fail the caller when the verb is missing.
            string payload = args.Trim();
            if (payload.Length == 0)
                return true;

            if (payload[0] is '.' or '/')
                payload = payload[1..].TrimStart();
            // Proper TRYSRC semantics:
            //   TRYSRC <srcRef> <verb...>
            // where <srcRef> can be UID/REF/etc. Examples from scripts:
            //   TRYSRC <UID> DIALOGCLOSE d_spawn
            //   TRYSRC <REF2> EFFECT 0,i_fx_fireball,10,16,0,044,4
            // If the first token resolves to an object reference, execute
            // the remaining command line against that object. Otherwise,
            // keep the legacy fallback and run the whole payload as a GM
            // command line.
            int firstSpace = payload.IndexOf(' ');
            if (firstSpace > 0)
            {
                string srcRefToken = payload[..firstSpace].Trim();
                string rest = payload[(firstSpace + 1)..].Trim();
                if (rest.Length > 0 && TryFindObjectByScriptRef(srcRefToken, out var srcRefObj))
                {
                    int cmdSpace = rest.IndexOf(' ');
                    string subCmd = cmdSpace > 0 ? rest[..cmdSpace] : rest;
                    string subArg = cmdSpace > 0 ? rest[(cmdSpace + 1)..].Trim() : "";
                    if (subCmd.Length > 0)
                    {
                        if (srcRefObj.TrySetProperty(subCmd, subArg))
                            return true;
                        if (srcRefObj.TryExecuteCommand(subCmd, subArg, _client))
                            return true;
                        _ = TryExecuteScriptCommand(srcRefObj, subCmd, subArg, triggerArgs);
                    }
                    return true;
                }
            }

            if (_commands != null)
            {
                _ = _commands.TryExecute(_character, payload);
                return true;
            }

            string fallbackCmd = payload;
            int fallbackSpace = fallbackCmd.IndexOf(' ');
            string cmd2 = fallbackSpace > 0 ? fallbackCmd[..fallbackSpace] : fallbackCmd;
            string arg2 = fallbackSpace > 0 ? fallbackCmd[(fallbackSpace + 1)..].Trim() : "";
            IScriptObj srcObj = triggerArgs?.Source ?? target;
            if (cmd2.Length > 0)
            {
                if (srcObj.TrySetProperty(cmd2, arg2))
                    return true;
                if (srcObj.TryExecuteCommand(cmd2, arg2, _client))
                    return true;
                _ = TryExecuteScriptCommand(srcObj, cmd2, arg2, triggerArgs);
            }
            return true;
        }

        if (upper is "TARGETF" or "TARGETFG")
        {
            string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            if (Targets.CursorActive)
                return true;
            ClearPendingTargetState();
            Targets.Function = parts[0];
            Targets.FunctionArgs = parts.Length > 1 ? parts[1].Trim() : "";
            Targets.AllowGround = upper == "TARGETFG";
            Targets.ItemUid = target is Item ti ? ti.Uid : Serial.Invalid;
            Targets.CursorActive = true;
            byte tType = (byte)(upper == "TARGETFG" ? 1 : 0);
            uint cursorId = (uint)Random.Shared.Next(1, int.MaxValue);
            Targets.CursorId = cursorId; // session guard: record the request id
            _netState.Send(new PacketTarget(tType, cursorId));
            return true;
        }

        if (upper is "TARGET" or "TARGETG")
        {
            if (Targets.CursorActive)
                return true;
            ClearPendingTargetState();
            Targets.AllowGround = upper == "TARGETG";
            Targets.CursorActive = true;
            byte tType = (byte)(upper == "TARGETG" ? 1 : 0);
            uint cursorId = (uint)Random.Shared.Next(1, int.MaxValue);
            Targets.CursorId = cursorId; // session guard: record the request id
            _netState.Send(new PacketTarget(tType, cursorId));
            return true;
        }

        if (upper == "SKILLMENU")
        {
            // Reference CV_SKILLMENU: open a [SKILLMENU name] selection menu.
            return OpenSkillMenu(args.Trim());
        }

        if (upper == "SUMMON")
        {
            // From the sm_summon CAST menu: stash the pick and run the real
            // Summon Creature cast — the completed cast conveys the selection
            // (Source-X stores m_atMagery.m_uiSummonID then Spell_CastStart).
            // Outside a pending cast menu, keep the direct CV_SUMMON verb.
            if (_character != null &&
                _character.TryGetTag("CAST_MENU_SPELL", out string? pendingSummon) &&
                pendingSummon == ((int)SpellType.SummonCreature).ToString())
            {
                _character.RemoveTag("CAST_MENU_SPELL");
                _character.SetTag("SUMMON_SELECT", args.Trim());
                (_client as GameClient)?.HandleCastSpell(SpellType.SummonCreature, _character.Uid.Value);
                return true;
            }
            // Reference CV_SUMMON: summon the given chardef as a temporary
            // summoned pet of the caster (used by sm_summon entries).
            SummonFromMenu(args.Trim());
            return true;
        }

        if (upper == "POLY")
        {
            // From a poly-family CAST menu (sm_polymorph / sm_beast_form /
            // sm_monster_form / chameleon): stash the picked form and run the
            // real cast (timed body revert included). The GM / script POLY
            // verb outside a pending cast menu falls through to the direct
            // character verb.
            var polyChar = _character;
            if (polyChar != null &&
                polyChar.TryGetTag("CAST_MENU_SPELL", out string? pendingPoly) &&
                int.TryParse(pendingPoly, out int pendingPolyId) &&
                (SpellType)pendingPolyId is SpellType.Polymorph or SpellType.Chameleon
                    or SpellType.BeastForm or SpellType.MonsterForm)
            {
                polyChar.RemoveTag("CAST_MENU_SPELL");
                polyChar.SetTag("POLY_SELECT", args.Trim());
                (_client as GameClient)?.HandleCastSpell((SpellType)pendingPolyId, polyChar.Uid.Value);
                return true;
            }
        }

        if (upper == "MAKEITEM")
        {
            // Reference SKILLMENU MAKEITEM: craft the itemdef through the
            // crafting engine recipe loaded from its SKILLMAKE/RESOURCES.
            MakeItemFromMenu(args.Trim());
            return true;
        }

        if (upper == "MENU")
        {
            string menuDefname = args.Trim();
            if (string.IsNullOrWhiteSpace(menuDefname))
            {
                _logger.LogWarning("[menu] MENU command with no argument");
                return true;
            }

            if (!TryFindMenuSection(menuDefname, out var menuSection))
            {
                _logger.LogWarning("[menu] Section [MENU {Defname}] not found", menuDefname);
                return true;
            }

            // Parse the MENU section:
            //   First key = title/question
            //   ON=0 text          → text-based item (modelId=0, hue=0)
            //   ON=baseid text     → item-based
            //   ON=baseid @hue, text → item-based with hue
            //   Lines after ON until next ON = script to execute

            var keys = menuSection.Keys;
            if (keys.Count == 0)
            {
                _logger.LogWarning("[menu] Empty MENU section {Defname}", menuDefname);
                return true;
            }

            string question = keys[0].RawLine;
            var options = new List<MenuOptionEntry>();
            MenuOptionEntry? current = null;

            for (int i = 1; i < keys.Count; i++)
            {
                var k = keys[i];
                if (k.Key.StartsWith("ON", StringComparison.OrdinalIgnoreCase) && k.Key.Length == 2)
                {
                    // Flush previous option
                    if (current != null) options.Add(current);

                    // Parse: ON=baseid text  or  ON=baseid @hue, text  or  ON=0 text
                    string onArg = k.Arg.Trim();
                    ushort modelId = 0;
                    ushort hue = 0;
                    string text = "";

                    int firstSpace = onArg.IndexOf(' ');
                    if (firstSpace < 0)
                    {
                        // ON=baseid with no text
                        _ = ushort.TryParse(onArg, System.Globalization.NumberStyles.HexNumber, null, out modelId);
                    }
                    else
                    {
                        string idPart = onArg[..firstSpace].Trim();
                        string rest = onArg[(firstSpace + 1)..].Trim();

                        if (idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || idPart.StartsWith("0", StringComparison.OrdinalIgnoreCase))
                            _ = ushort.TryParse(idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? idPart[2..] : idPart, System.Globalization.NumberStyles.HexNumber, null, out modelId);
                        else
                            _ = ushort.TryParse(idPart, out modelId);

                        // Check for @hue prefix: @hue, text  or  @hue text
                        if (rest.StartsWith('@'))
                        {
                            int comma = rest.IndexOf(',');
                            int space = rest.IndexOf(' ');
                            int sep = comma >= 0 ? comma : space;
                            if (sep > 1)
                            {
                                string huePart = rest[1..sep];
                                _ = ushort.TryParse(huePart, System.Globalization.NumberStyles.HexNumber, null, out hue);
                                text = rest[(sep + 1)..].TrimStart(' ', ',');
                            }
                            else
                            {
                                text = rest;
                            }
                        }
                        else
                        {
                            text = rest;
                        }
                    }

                    current = new MenuOptionEntry(modelId, hue, text, []);
                }
                else if (current != null)
                {
                    // Script line belonging to current ON block
                    current.Script.Add(k);
                }
            }
            if (current != null) options.Add(current);

            if (options.Count == 0)
            {
                _logger.LogWarning("[menu] MENU {Defname} has no ON entries", menuDefname);
                return true;
            }
            if (options.Count > MenuMaxItems)
                options.RemoveRange(MenuMaxItems, options.Count - MenuMaxItems);

            // Store pending state
            _pendingMenuId = (ushort)(Math.Abs(menuDefname.GetHashCode()) & 0xFFFF);
            _pendingMenuDefname = menuDefname;
            _pendingMenuOptions = options;

            // Build and send 0x7C packet
            var items = new List<MenuItemEntry>(options.Count);
            foreach (var opt in options)
                items.Add(new MenuItemEntry(opt.ModelId, opt.Hue, opt.Text));

            _netState.Send(new PacketMenuDisplay(_character.Uid.Value, _pendingMenuId, question, items));
            return true;
        }

        if (upper == "DIALOGCLOSE" || upper.StartsWith("DIALOGCLOSE ", StringComparison.Ordinal))
        {
            // Close the named open script dialog (0xBF 0x04) using the
            // server-side open-dialog registry. Bare DIALOGCLOSE (no name)
            // stays a no-op for legacy script compatibility.
            string dlgName = upper == "DIALOGCLOSE"
                ? args.Trim()
                : (cmd["DIALOGCLOSE ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            if (dlgName.Length > 0)
                CloseScriptDialog(dlgName);
            return true;
        }

        // SDIALOG = "send dialog", a Sphere alias for DIALOG used by some
        // shards' script packs. Accept both so imported scripts don't
        // need to be rewritten.
        if (upper == "DIALOG" || upper == "SDIALOG")
        {
            if (_dialogDepth >= 4) return true;
            _dialogDepth++;
            try { return HandleDialogCommand(args, target as ObjBase); } finally { _dialogDepth--; }
        }

        if (upper == "GO" && target is Character goChar)
        {
            if (TryParsePoint(args, goChar.Position, out var dst))
            {
                _world.MoveCharacter(goChar, dst);
                if (goChar == _character)
                {
                    Resync();
                    BroadcastDrawObject(_character);
                }
            }
            return true;
        }

        if (upper == "GONAME" && target is Character goNameChar)
        {
            string targetName = args.Trim();
            if (targetName.Length > 0)
            {
                var dst = _world.GetAllObjects()
                    .OfType<Character>()
                    .FirstOrDefault(c => c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                if (dst != null)
                {
                    _world.MoveCharacter(goNameChar, dst.Position);
                    if (goNameChar == _character)
                        Resync();
                }
            }
            return true;
        }

        if (upper == "SERV.NEWITEM")
        {
            string defName = args.Trim();
            if (_commands?.Resources == null || defName.Length == 0)
                return true;
            var rid = _commands.Resources.ResolveDefName(defName);
            if (!rid.IsValid) return true;

            var item = _world.CreateItem();
            // Apply the full ITEMDEF metadata (graphic from DispIndex, Type, TData,
            // ITEMDEF/SCRIPTDEF tags) instead of a raw (ushort)rid.Index: a
            // name-keyed itemdef hashes above 0xFFFF, so the cast produced a garbage
            // graphic with Type/MORE 0. Fall back to a guarded 16-bit id only when
            // the def can't be resolved.
            if (rid.Type != Core.Enums.ResType.ItemDef ||
                !Definitions.ItemDefHelper.ApplyInstanceMetadata(item, rid.Index, setName: false))
                item.BaseId = rid.Index is > 0 and <= ushort.MaxValue ? (ushort)rid.Index : (ushort)0;
            item.Name = defName;
            Targets.ScriptNewItem = item;
            return true;
        }

        if (upper.StartsWith("NEW.", StringComparison.Ordinal))
        {
            if (Targets.ScriptNewItem == null) return true;
            string sub = cmd[4..].ToUpperInvariant();
            switch (sub)
            {
                case "EQUIP":
                    _character.Backpack ??= _world.CreateItem();
                    _character.Backpack.BaseId = 0x0E75;
                    _character.Backpack.ItemType = ItemType.Container;
                    _character.Backpack.Name = "Backpack";
                    _character.Equip(_character.Backpack, Layer.Pack);
                    if (!_character.Backpack.TryAddItem(Targets.ScriptNewItem))
                        _world.PlaceItemWithDecay(Targets.ScriptNewItem, _character.Position);
                    Targets.ScriptNewItem = null;
                    return true;
                case "CONT":
                {
                    var trimmed = args.Trim();
                    if (trimmed.Length > 0 && trimmed != "-1")
                    {
                        uint cval = ObjBase.ParseHexOrDecUInt(trimmed);
                        var cont = _world.FindObject(new Serial(cval)) as Item;
                        if (cont != null)
                        {
                            if (!cont.TryAddItem(Targets.ScriptNewItem))
                                _world.PlaceItemWithDecay(Targets.ScriptNewItem, _character.Position);
                            Targets.ScriptNewItem = null;
                            return true;
                        }
                    }
                    if (_character.Backpack == null)
                    {
                        var scriptPack = _world.CreateItem();
                        scriptPack.BaseId = 0x0E75;
                        scriptPack.ItemType = ItemType.Container;
                        scriptPack.Name = "Backpack";
                        _character.Equip(scriptPack, Layer.Pack);
                    }
                    if (!_character.Backpack!.TryAddItem(Targets.ScriptNewItem))
                        _world.PlaceItemWithDecay(Targets.ScriptNewItem, _character.Position);
                    Targets.ScriptNewItem = null;
                    return true;
                }
                default:
                    Targets.ScriptNewItem.TrySetProperty(sub, args);
                    return true;
            }
        }

        if (upper == "SERV.ALLCLIENTS" || upper.StartsWith("SERV.ALLCLIENTS ", StringComparison.Ordinal))
        {
            string payload = args.Trim();
            if (upper.StartsWith("SERV.ALLCLIENTS ", StringComparison.Ordinal))
                payload = cmd["SERV.ALLCLIENTS ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}");

            if (payload.StartsWith("SOUND", StringComparison.OrdinalIgnoreCase))
            {
                string[] ps = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ps.Length >= 2 && ushort.TryParse(ps[1], out ushort snd))
                {
                    var pkt = new PacketSound(snd, _character.X, _character.Y, _character.Z);
                    BroadcastNearby?.Invoke(_character.Position, 9999, pkt, 0);
                }
            }
            else if (payload.Length > 0)
            {
                // Source-X parity: SERV.ALLCLIENTS <function> runs the function once
                // for each online player character as target, with SRC as current char.
                int firstSpace = payload.IndexOf(' ');
                string funcName = firstSpace > 0 ? payload[..firstSpace].Trim() : payload.Trim();
                string funcArgs = firstSpace > 0 ? payload[(firstSpace + 1)..].Trim() : "";

                var runner = _triggerDispatcher?.Runner;
                if (runner != null && funcName.Length > 0)
                {
                    foreach (var player in _world.GetAllObjects().OfType<Character>())
                    {
                        if (!player.IsPlayer || !player.IsOnline)
                            continue;

                        var callArgs = new ExecTriggerArgs(_character, 0, 0, funcArgs)
                        {
                            Object1 = player,
                            Object2 = _character
                        };

                        _ = runner.TryRunFunction(funcName, player, _client, callArgs, out _);
                    }
                }
                else
                {
                    string msg = payload.StartsWith("SYSMESSAGE", StringComparison.OrdinalIgnoreCase)
                        ? payload["SYSMESSAGE".Length..].Trim()
                        : payload;
                    SysMessage(msg);
                }
            }
            else
            {
                string msg = payload.StartsWith("SYSMESSAGE", StringComparison.OrdinalIgnoreCase)
                    ? payload["SYSMESSAGE".Length..].Trim()
                    : payload;
                SysMessage(msg);
            }
            return true;
        }

        if (upper == "SERV.LOG" || upper.StartsWith("SERV.LOG ", StringComparison.Ordinal))
        {
            string msg = upper == "SERV.LOG" ? args : cmd["SERV.LOG ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}");
            _logger.LogInformation("[SCRIPT] {Message}", msg.Trim());
            return true;
        }

        if (upper == "BANKSELF")
        {
            OpenBankBox();
            return true;
        }

        if (upper == "SMELT" || upper.StartsWith("SMELT ", StringComparison.Ordinal))
        {
            // Source-X CIV_SMELT: smelt this ore with SRC as the smith. Arg =
            // forge uid; without one the nearest forge in reach is used.
            if (target is not Item ore || _character == null) return true;
            string forgeArg = upper == "SMELT"
                ? args.Trim()
                : (cmd["SMELT ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            Serial forgeUid = Serial.Invalid;
            if (forgeArg.Length > 0)
            {
                string uidStr = forgeArg;
                if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) uidStr = uidStr[2..];
                else if (uidStr.StartsWith('0') && uidStr.Length > 1) uidStr = uidStr[1..];
                if (uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint fu))
                    forgeUid = new Serial(fu);
            }
            if (!forgeUid.IsValid)
            {
                foreach (var near in _world.GetItemsInRange(_character.Position, 3))
                {
                    if (near.ItemType == ItemType.Forge) { forgeUid = near.Uid; break; }
                }
            }
            _client.ItemUse.SmeltFromScript(ore, forgeUid);
            return true;
        }

        if (upper == "CARVECORPSE")
        {
            // Source-X CIV_CARVECORPSE: SRC carves this corpse (meat/hides/
            // feathers per the dead creature's body).
            if (target is Item corpse && corpse.ItemType == ItemType.Corpse && _character != null)
                _client.DeathEng?.CarveCorpse(_character, corpse);
            return true;
        }

        if (upper == "OPENTRADEWINDOW" || upper.StartsWith("OPENTRADEWINDOW ", StringComparison.Ordinal))
        {
            // Source-X CV_OPENTRADEWINDOW: open a secure trade with the char
            // named by the arg uid (or the current target object).
            string tradeArg = upper == "OPENTRADEWINDOW"
                ? args.Trim()
                : (cmd["OPENTRADEWINDOW ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            Character? partner = target as Character;
            if (tradeArg.Length > 0)
            {
                string uidStr = tradeArg;
                if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) uidStr = uidStr[2..];
                else if (uidStr.StartsWith('0') && uidStr.Length > 1) uidStr = uidStr[1..];
                if (uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint tradeUid))
                    partner = _world.FindChar(new Serial(tradeUid));
            }
            if (partner != null && partner != _character)
                _client.InitiateTrade(partner);
            return true;
        }

        if (upper == "OPENPAPERDOLL" || upper.StartsWith("OPENPAPERDOLL ", StringComparison.Ordinal))
        {
            // Source-X CV_OPENPAPERDOLL: open the paperdoll — the target
            // object's when it is a char, else the client's own.
            var pdTarget = target as Character ?? _character;
            if (pdTarget != null)
                _client.SendPaperdoll(pdTarget);
            return true;
        }

        if (upper == "CAST" || upper.StartsWith("CAST ", StringComparison.Ordinal))
        {
            // Source-X CV_CAST: begin casting a spell on this client — by
            // number or spell defname (s_ prefix optional).
            string spellArg = upper == "CAST"
                ? args.Trim()
                : (cmd["CAST ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            if (spellArg.Length == 0) return true;
            SphereNet.Core.Enums.SpellType castSpell;
            if (int.TryParse(spellArg, out int spellNum) && spellNum > 0)
                castSpell = (SphereNet.Core.Enums.SpellType)spellNum;
            else if (!Enum.TryParse(spellArg.Replace(" ", "").Replace("s_", "",
                         StringComparison.OrdinalIgnoreCase), ignoreCase: true, out castSpell))
                return true;
            _client.HandleCastSpell(castSpell, 0);
            return true;
        }

        if (upper == "WEBLINK" || upper.StartsWith("WEBLINK ", StringComparison.Ordinal))
        {
            // Source-X CV_WEBLINK — open a browser on the client (0xA5).
            string url = upper == "WEBLINK"
                ? args.Trim()
                : (cmd["WEBLINK ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}")).Trim();
            if (url.Length > 0)
                Send(new PacketWebLink(url));
            return true;
        }

        if (upper == "BUY")
        {
            // Source-X CV_BUY: open the vendor buy list. Vendor context is the
            // current dialog subject when it's an NPC vendor, otherwise the
            // nearest vendor NPC in interaction range.
            var vendor = ResolveVendorContext();
            if (vendor != null)
                OpenVendorBuy(vendor);
            return true;
        }

        if (upper == "BYE")
        {
            // Source-X CV_BYE: end the NPC interaction — close any open script
            // dialogs and drop the dialog subject so follow-up reads don't
            // resolve against the NPC anymore.
            foreach (var openDlg in Gumps.OpenScriptDialogs.Keys.ToArray())
                CloseScriptDialog(openDlg);
            Dialogs.DialogSubjectUid = Serial.Invalid;
            return true;
        }

        if (upper.StartsWith("SERV.", StringComparison.Ordinal))
        {
            // Unimplemented service verbs must not crash scripts (Sphere keeps
            // running), but the gap shouldn't be invisible either: warn once
            // per verb per server run, debug-log every hit, and tell GM-level
            // callers directly so script authors see it in-game.
            bool firstHit;
            lock (s_unknownServVerbs)
                firstHit = s_unknownServVerbs.Add(upper);
            if (firstHit)
                _logger.LogWarning("Script SERV verb not implemented: {Verb} {Args} (further uses logged at debug)", key, args);
            else
                _logger.LogDebug("Script SERV verb not implemented: {Verb} {Args}", key, args);
            if (_character != null && _character.PrivLevel >= PrivLevel.GM)
                SysMessage($"Script: unimplemented verb '{key}' ignored.");
            return true;
        }

        if (upper.StartsWith("FILE.", StringComparison.Ordinal))
        {
            if (_scriptFile == null)
            {
                _logger.LogWarning("FILE commands not enabled (OF_FileCommands not set in OptionFlags).");
                return true;
            }

            string fileVerb = upper.Length > 5 ? upper[5..] : "";
            switch (fileVerb)
            {
                case "OPEN":
                    _scriptFile.Open(args);
                    return true;
                case "CLOSE":
                    _scriptFile.Close();
                    return true;
                case "WRITE":
                    _scriptFile.Write(args);
                    return true;
                case "WRITELINE":
                    _scriptFile.WriteLine(args);
                    return true;
                case "WRITECHR":
                    if (int.TryParse(args, out int chrVal))
                        _scriptFile.WriteChr(chrVal);
                    return true;
                case "FLUSH":
                    _scriptFile.Flush();
                    return true;
                case "DELETEFILE":
                    // Resolves under the sandbox root (one consistent base);
                    // refuses to delete the currently-open file (Source-X).
                    _scriptFile.DeleteRelative(args);
                    return true;
                case "MODE.APPEND":
                    _scriptFile.ModeAppend = args != "0";
                    return true;
                case "MODE.CREATE":
                    _scriptFile.ModeCreate = args != "0";
                    return true;
                case "MODE.READFLAG":
                    _scriptFile.ModeRead = args != "0";
                    return true;
                case "MODE.WRITEFLAG":
                    _scriptFile.ModeWrite = args != "0";
                    return true;
                case "MODE.SETDEFAULT":
                    _scriptFile.SetModeDefault();
                    return true;
            }
            return true;
        }

        if (upper.StartsWith("DB.", StringComparison.Ordinal))
        {
            if (_scriptDb == null)
            {
                _logger.LogWarning("DB adapter is not configured for script runtime.");
                return true;
            }

            string dbVerb = upper.Length > 3 ? upper[3..] : "";
            switch (dbVerb)
            {
                case "CONNECT":
                {
                    bool ok;
                    string err;
                    string trimmed = args.Trim();
                    string[] dbArgs = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (dbArgs.Length == 2)
                        ok = _scriptDb.Connect(dbArgs[0], dbArgs[1], out err);
                    else if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains('='))
                        ok = _scriptDb.Connect(trimmed, out err);
                    else
                        ok = _scriptDb.ConnectDefault(out err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_connect_fail", err));
                    return true;
                }
                case "CLOSE":
                {
                    string name = args.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        _scriptDb.Close();
                    else if (name.Equals("*", StringComparison.Ordinal))
                        _scriptDb.CloseAll();
                    else
                        _scriptDb.Close(name);
                    return true;
                }
                case "SELECT":
                {
                    string name = args.Trim();
                    if (!_scriptDb.Select(name, out string err))
                        SysMessage(err);
                    return true;
                }
                case "QUERY":
                {
                    bool ok = _scriptDb.Query(args, out int rows, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_query_fail", err));
                    else
                        _logger.LogDebug("DB.QUERY returned {Rows} rows", rows);
                    return true;
                }
                case "EXECUTE":
                {
                    bool ok = _scriptDb.Execute(args, out int affected, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_execute_fail", err));
                    else
                        _logger.LogDebug("DB.EXECUTE affected {Rows} rows", affected);
                    return true;
                }
            }
            return true;
        }

        // Source-X MDB.* — the secondary MySQL reference object; same verb
        // surface as DB.* on its own connection.
        if (upper.StartsWith("MDB.", StringComparison.Ordinal))
        {
            if (_scriptMdb == null)
            {
                _logger.LogWarning("MDB adapter is not configured for script runtime.");
                return true;
            }

            string mdbVerb = upper.Length > 4 ? upper[4..] : "";
            switch (mdbVerb)
            {
                case "CONNECT":
                {
                    bool ok;
                    string err;
                    string trimmed = args.Trim();
                    string[] dbArgs = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (dbArgs.Length == 2)
                        ok = _scriptMdb.Connect(dbArgs[0], dbArgs[1], out err);
                    else if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains('='))
                        ok = _scriptMdb.Connect(trimmed, out err);
                    else
                        ok = _scriptMdb.ConnectDefault(out err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_connect_fail", err));
                    return true;
                }
                case "CLOSE":
                    _scriptMdb.Close();
                    return true;
                case "QUERY":
                {
                    if (!_scriptMdb.Query(args, out int rows, out string err))
                        SysMessage(ServerMessages.GetFormatted("db_query_fail", err));
                    else
                        _logger.LogDebug("MDB.QUERY returned {Rows} rows", rows);
                    return true;
                }
                case "EXECUTE":
                {
                    if (!_scriptMdb.Execute(args, out int affected, out string err))
                        SysMessage(ServerMessages.GetFormatted("db_execute_fail", err));
                    else
                        _logger.LogDebug("MDB.EXECUTE affected {Rows} rows", affected);
                    return true;
                }
            }
            return true;
        }

        if (upper.StartsWith("LDB.", StringComparison.Ordinal))
        {
            if (_scriptLdb == null)
            {
                _logger.LogWarning("SQLite (LDB) adapter is not configured for script runtime.");
                return true;
            }

            string ldbVerb = upper.Length > 4 ? upper[4..] : "";
            switch (ldbVerb)
            {
                case "CONNECT":
                {
                    string fileName = args.Trim();
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        SysMessage("LDB.CONNECT requires a filename.");
                        return true;
                    }
                    if (!_scriptLdb.ConnectFile(fileName, _scriptDatabaseRoot, out string err))
                        SysMessage(ServerMessages.GetFormatted("db_connect_fail", err));
                    return true;
                }
                case "CLOSE":
                {
                    _scriptLdb.Close();
                    return true;
                }
                case "QUERY":
                {
                    bool ok = _scriptLdb.Query(args, out int rows, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_query_fail", err));
                    else
                        _logger.LogDebug("LDB.QUERY returned {Rows} rows", rows);
                    return true;
                }
                case "EXECUTE":
                {
                    bool ok = _scriptLdb.Execute(args, out int affected, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_execute_fail", err));
                    else
                        _logger.LogDebug("LDB.EXECUTE affected {Rows} rows", affected);
                    return true;
                }
            }
            return true;
        }

        if (upper.Equals("SENDPACKET", StringComparison.Ordinal))
        {
            if (TryParseScriptPacket(args, out byte[] packet, out string err))
            {
                _netState.SendRaw(packet);
            }
            else
            {
                _logger.LogWarning("[script_packet] SENDPACKET rejected: {Error}; input='{Input}'", err, args);
            }
            return true;
        }

        if (TryExecuteSourceXClientVerb(upper, args, target))
            return true;

        return false;
    }

    /// <summary>Source-X CClient verb long tail (CClient_functions.tbl entries
    /// that had no SphereNet surface): GM tools, UI packets and targeting
    /// flows. Split out of the main dispatch chain for readability.</summary>
    private bool TryExecuteSourceXClientVerb(string upper, string args, IScriptObj target)
    {
        if (_character == null) return false;
        switch (upper)
        {
            case "ADD":
            case "ADDCHAR":
            case "ADDITEM":
            {
                string raw = args.Trim();
                if (raw.Length == 0)
                {
                    // Source-X bare ADD opens D_ADD (or MENU_ADDITEM fallback).
                    // Use the named dialog when the script pack provides it;
                    // otherwise keep the command acknowledged with a usage hint.
                    if (upper == "ADD" && OpenNamedDialog("d_add", subject: _character))
                        return true;
                    SysMessage($"Usage: {upper} <defname|id>");
                    return true;
                }

                // Source-X accepts an optional amount after comma and keeps it
                // in m_tmAdd until the target response.
                string[] addParts = raw.Split([',', ' ', '\t'], 2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string token = addParts[0];
                ushort amount = 1;
                if (addParts.Length > 1 && ushort.TryParse(addParts[1], out ushort parsedAmount))
                    amount = Math.Max((ushort)1, parsedAmount);
                (_client as GameClient)?.BeginAddTarget(token, amount);
                return true;
            }

            case "CHARLIST":
                // Source-X CV_CHARLIST — resend the character selection list.
                _client.ResendCharacterList();
                return true;

            case "CLOSEPAPERDOLL":
            case "CLOSEPROFILE":
            case "CLOSESTATUS":
            {
                // 0xBF sub 0x16 close-UI packet: Paperdoll=1, Status=2,
                // Profile=8. Optional arg = char uid; default = own char.
                uint windowType = upper switch
                {
                    "CLOSEPAPERDOLL" => 1u,
                    "CLOSEPROFILE" => 8u,
                    _ => 2u
                };
                uint uid = _character.Uid.Value;
                string uidArg = args.Trim();
                if (uidArg.Length > 0 &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(uidArg.AsSpan(), out long uv) && uv > 0)
                    uid = (uint)uv;
                Send(new SphereNet.Network.Packets.Outgoing.PacketCloseUIWindow(windowType, uid));
                return true;
            }

            case "CTAGLIST":
            {
                bool toLog = args.TrimStart().StartsWith("log", StringComparison.OrdinalIgnoreCase);
                foreach (var (tagKey, tagValue) in _character.CTags.GetAll())
                {
                    string line = $"CTAG.{tagKey}={tagValue}";
                    if (toLog)
                        _logger.LogInformation("{Line}", line);
                    else
                        SysMessage(line);
                }
                return true;
            }

            case "GMPAGE":
            {
                string reason = args.Trim();
                if (reason.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
                    reason = reason[4..].Trim();
                if (reason.Length > 0)
                {
                    _commands?.Execute(_character, $"PAGE {reason}");
                    return true;
                }

                _client.SendPrompt(0x474D5047, "Enter your help request:",
                    (_, _, _, text) =>
                    {
                        if (!string.IsNullOrWhiteSpace(text))
                            _commands?.Execute(_character, $"PAGE {text.Trim()}");
                    });
                return true;
            }

            case "INFORMATION":
                SysMessage($"SphereNet: {_world.TotalChars} chars, {_world.TotalItems} items.");
                SysMessage($"Client: {_netState.ClientVersionNumber}, map {_character.MapIndex}.");
                return true;

            case "RESEND":
                Resync();
                return true;

            case "SAVE":
                _commands?.Execute(_character, "SAVE");
                return true;

            case "SELF":
            {
                if (!Targets.CursorActive || _client is not GameClient selfClient)
                    return false;
                Point3D p = _character.Position;
                selfClient.Targeting.HandleTargetResponse(
                    0, Targets.CursorId, _character.Uid.Value, p.X, p.Y, p.Z, 0);
                return true;
            }

            case "SKILLSELECT":
            {
                string token = args.Trim();
                int skillId;
                if (Enum.TryParse(token, true, out SkillType skill))
                    skillId = (int)skill;
                else if (!int.TryParse(token, out skillId))
                    return true;
                (_client as GameClient)?.HandleUseSkill(skillId);
                return true;
            }

            case "VERSION":
            {
                string version = typeof(GameClient).Assembly.GetName().Version?.ToString() ?? "1.0.0";
                SysMessage($"SphereNet {version}");
                return true;
            }

            case "CODEXOFWISDOM":
            {
                var parts = args.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 1 || parts[0].Length == 0 ||
                    !SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[0].AsSpan(), out long topic))
                {
                    SysMessage("Usage: CODEXOFWISDOM TopicID [ForceOpen]");
                    return true;
                }
                bool force = parts.Length > 1 &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[1].AsSpan(), out long fv) && fv != 0;
                Send(new SphereNet.Network.Packets.Outgoing.PacketCodexOfWisdom((uint)topic, force));
                return true;
            }

            case "DYE":
            {
                // Source-X CV_DYE <uid> — open the hue picker on the object;
                // the 0x95 response lands in GameClient.HandleDyeResponse.
                string uidArg = args.Trim();
                ObjBase? dyeObj = null;
                if (uidArg.Length > 0 &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(uidArg.AsSpan(), out long duid))
                    dyeObj = _world.FindObject(new Serial((uint)duid));
                dyeObj ??= target as ObjBase;
                if (dyeObj == null) return true;
                ushort dispId = dyeObj is Item dyeItem ? dyeItem.DispIdFull
                    : dyeObj is Character dyeChar ? dyeChar.BodyId : (ushort)0;
                Send(new SphereNet.Network.Packets.Outgoing.PacketDyeWindow(
                    dyeObj.Uid.Value, dyeObj.Hue.Value, dispId));
                return true;
            }

            case "EVERBTARG":
            {
                // Source-X CV_EVERBTARG — prompt for text, then run
                // "<verb-prefix> <typed text>" against the last-picked target.
                string verbPrefix = args.Trim();
                uint pendingSerial = Targets.LastPickedSerial;
                _client.SendPrompt(0x0EE7B7A6, verbPrefix.Length > 0 ? "Enter the text" : "Enter the verb",
                    (_, _, _, text) =>
                    {
                        if (string.IsNullOrWhiteSpace(text)) return;
                        var obj = pendingSerial != 0 ? _world.FindObject(new Serial(pendingSerial)) : null;
                        if (obj == null) return;
                        string line = verbPrefix.Length > 0 ? $"{verbPrefix} {text}" : text;
                        int sp = line.IndexOfAny([' ', '\t', '=']);
                        string verb = sp < 0 ? line : line[..sp];
                        string verbArg = sp < 0 ? "" : line[(sp + 1)..].Trim();
                        if (verb.Length == 0) return;
                        if (!obj.TrySetProperty(verb, verbArg) &&
                            !obj.TryExecuteCommand(verb, verbArg, (ITextConsole)_client))
                            _ = TryExecuteScriptCommand(obj, verb, verbArg, null);
                    });
                return true;
            }

            case "GOTARG":
            {
                // Source-X CV_GOTARG — teleport 3 tiles west of the last target.
                var obj = Targets.LastPickedSerial != 0
                    ? _world.FindObject(new Serial(Targets.LastPickedSerial)) : null;
                if (obj == null) return true;
                var po = obj.GetTopLevelPosition();
                _character.MoveTo(new Point3D((short)(po.X - 3), po.Y, po.Z, po.Map));
                return true;
            }

            case "LAST":
            {
                // Source-X CV_LAST — feed the previous target into the cursor
                // that is currently up.
                if (!Targets.CursorActive || Targets.LastPickedSerial == 0)
                    return false;
                var obj = _world.FindObject(new Serial(Targets.LastPickedSerial));
                if (obj == null) return true;
                var p = obj.GetTopLevelPosition();
                (_client as GameClient)?.Targeting.HandleTargetResponse(0, Targets.CursorId, obj.Uid.Value, p.X, p.Y, p.Z, 0);
                return true;
            }

            case "LINK":
            {
                // Source-X CV_LINK — pick two items and cross-link them
                // (keys copy the lock uid instead).
                SysMessage("Select the item to link.");
                _client.SetPendingTarget((serial1, _, _, _, _) =>
                {
                    var first = _world.FindItem(new Serial(serial1));
                    if (first == null) { SysMessage("Must link to an item."); return; }
                    SysMessage("Select the item to link it to.");
                    _client.SetPendingTarget((serial2, _, _, _, _) =>
                    {
                        var second = _world.FindItem(new Serial(serial2));
                        if (second == null) { SysMessage("Must link to an item."); return; }
                        if (first == second) { SysMessage("That is the same item."); return; }
                        if (first.ItemType == ItemType.Key || second.ItemType == ItemType.Key)
                        {
                            var keyItem = first.ItemType == ItemType.Key ? first : second;
                            var other = keyItem == first ? second : first;
                            // Copy the lockable's lock id onto the key (MORE1).
                            keyItem.More1 = other.More1 != 0 ? other.More1 : other.Uid.Value;
                            if (other.More1 == 0 && other.ItemType != ItemType.Key)
                                other.More1 = keyItem.More1;
                        }
                        else
                        {
                            first.Link = second.Uid;
                            if (!second.Link.IsValid)
                                second.Link = first.Uid;
                        }
                        SysMessage("The items are linked.");
                    }, 0);
                }, 0);
                return true;
            }

            case "MAPWAYPOINT":
            {
                // Source-X CV_MAPWAYPOINT <uid>, <type> — type 0 removes.
                var parts = args.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 1 ||
                    !SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[0].AsSpan(), out long wuid))
                    return true;
                var wpObj = _world.FindObject(new Serial((uint)wuid));
                if (wpObj == null) return true;
                long wpType = parts.Length > 1 &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[1].AsSpan(), out long wt) ? wt : 0;
                var wp = wpObj.GetTopLevelPosition();
                if (wpType != 0)
                    Send(new SphereNet.Network.Packets.Outgoing.PacketWaypointAdd(
                        wpObj.Uid.Value, wp.X, wp.Y, wp.Z, wp.Map, (ushort)wpType, wpObj.GetName()));
                else
                    Send(new SphereNet.Network.Packets.Outgoing.PacketWaypointRemove(wpObj.Uid.Value));
                return true;
            }

            case "NUDGE":
                // Source-X CV_NUDGE dx dy dz over a marked area.
                _client.BeginAreaTarget("NUDGE", 8, args);
                return true;
            case "NUKE":
                // Optional arg = verb line applied instead of deleting.
                _client.BeginAreaTarget("NUKE", 8, args);
                return true;
            case "NUKECHAR":
                _client.BeginAreaTarget("NUKECHAR", 8, args);
                return true;

            case "REPAIR":
            {
                // Source-X CV_REPAIR — target an item, run the repair path.
                SysMessage("What item do you want to repair?");
                _client.SetPendingTarget((serial, _, _, _, _) =>
                {
                    var item = _world.FindItem(new Serial(serial));
                    if (item == null) { SysMessage("You can't repair that."); return; }
                    item.TryExecuteCommand("REPAIR", "", (ITextConsole)_client);
                }, 0);
                return true;
            }

            case "SCROLL":
            {
                // Source-X CV_SCROLL — open a [SCROLL name] section as the
                // updates scroll window (0xA6).
                var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;
                if (resources == null) return true;
                var rid = resources.ResolveDefName(args.Trim());
                if (!rid.IsValid || rid.Type != Core.Enums.ResType.Scroll) return true;
                var keys = resources.GetResource(rid)?.StoredKeys;
                if (keys == null || keys.Count == 0) return true;
                var sb = new System.Text.StringBuilder();
                foreach (var k in keys)
                    sb.Append(k.RawLine).Append('\r');
                Send(new SphereNet.Network.Packets.Outgoing.PacketOpenScroll(2, 0, sb.ToString()));
                return true;
            }

            case "SHOWSKILLS":
                // Source-X CV_SHOWSKILLS — resend the full skill list.
                _client.SendSkillList();
                return true;

            case "SKILLUPDATE":
            {
                // Source-X CV_SKILLUPDATE <skillname> — single-skill 0x3A.
                if (!Enum.TryParse<SkillType>(args.Trim(), true, out var skill))
                    return true;
                int sid = (int)skill;
                ushort raw = (ushort)Math.Clamp((int)_character.GetSkill(skill), 0, ushort.MaxValue);
                byte skLock = _character.GetSkillLock(skill);
                Send(new SphereNet.Network.Packets.Outgoing.PacketSkillSingle(
                    (ushort)sid, raw, raw, skLock, 1000));
                return true;
            }

            case "TILE":
            {
                // Source-X CV_TILE z item1 [item2 ...] — flood a marked area
                // with the given item ids at the given z, cycling the id list.
                if (args.Trim().Length == 0)
                {
                    SysMessage("Usage: TILE z-height item1 item2 ... itemX");
                    return true;
                }
                BeginTwoCornerTarget("Pick 1st corner:", "Pick 2nd corner:", (p1, p2) =>
                {
                    var toks = args.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                    if (toks.Length < 2) return;
                    if (!SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(toks[0].AsSpan(), out long tz))
                        return;
                    var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;
                    int created = 0, tokIdx = 1;
                    for (short mx = Math.Min(p1.X, p2.X); mx <= Math.Max(p1.X, p2.X); mx++)
                    for (short my = Math.Min(p1.Y, p2.Y); my <= Math.Max(p1.Y, p2.Y); my++)
                    {
                        string tok = toks[tokIdx];
                        if (++tokIdx >= toks.Length) tokIdx = 1;
                        var made = CreateTileItem(resources, tok,
                            new Point3D(mx, my, (sbyte)tz, _character.MapIndex));
                        if (made != null) created++;
                    }
                    SysMessage($"{created} tiled items.");
                });
                return true;
            }

            case "EXTRACT":
            {
                // Source-X CV_EXTRACT <file> <id> — write the dynamic items in
                // a marked area to a multi text file (version 6 format).
                var parts = args.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    SysMessage("Usage: EXTRACT <filename> <templateId>");
                    return true;
                }
                string fileName = parts[0];
                if (!SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[1].AsSpan(), out long templateId))
                    return true;
                BeginTwoCornerTarget("Select top-left corner of the extract area.",
                    "Select bottom-right corner.", (p1, p2) =>
                {
                    string? path = ResolveScriptFilePath(fileName);
                    if (path == null) { SysMessage("Invalid extract path."); return; }
                    short left = Math.Min(p1.X, p2.X), right = Math.Max(p1.X, p2.X);
                    short top = Math.Min(p1.Y, p2.Y), bottom = Math.Max(p1.Y, p2.Y);
                    var centre = new Point3D((short)((left + right) / 2), (short)((top + bottom) / 2), 0, _character.MapIndex);
                    int radius = Math.Max(1 + Math.Abs(right - left) / 2, 1 + Math.Abs(bottom - top) / 2);
                    var found = new List<Item>();
                    sbyte zLowest = sbyte.MaxValue;
                    foreach (var it in _world.GetItemsInRange(centre, radius))
                    {
                        if (it.ContainedIn.IsValid || it.IsEquipped) continue;
                        if (it.X < left || it.X > right || it.Y < top || it.Y > bottom) continue;
                        found.Add(it);
                        if (it.Z < zLowest) zLowest = it.Z;
                    }
                    if (found.Count == 0) { SysMessage("0 items extracted."); return; }
                    using var w = new StreamWriter(path, append: false);
                    w.WriteLine("6 version");
                    w.WriteLine($"{templateId} template id");
                    w.WriteLine("-1 item version");
                    w.WriteLine($"{found.Count} num components");
                    foreach (var it in found)
                        w.WriteLine($"{it.DispIdFull} {it.X - centre.X} {it.Y - centre.Y} {it.Z - zLowest} 0");
                    SysMessage($"{found.Count} items extracted to '{fileName}', id={templateId}.");
                });
                return true;
            }

            case "UNEXTRACT":
            {
                // Source-X CV_UNEXTRACT <file> <id> — rebuild an extracted
                // multi at the targeted point.
                var parts = args.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                {
                    SysMessage("Usage: UNEXTRACT <filename> <templateId>");
                    return true;
                }
                string fileName = parts[0];
                SysMessage("Select the position for the multi.");
                _client.SetPendingTarget((serial, x, y, z, _) =>
                {
                    string? path = ResolveScriptFilePath(fileName);
                    if (path == null || !File.Exists(path)) { SysMessage("Extract file not found."); return; }
                    var basePoint = serial != 0 && _world.FindObject(new Serial(serial)) is { } o
                        ? o.GetTopLevelPosition()
                        : new Point3D(x, y, z, _character.MapIndex);
                    int created = 0;
                    var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;
                    foreach (var line in File.ReadLines(path))
                    {
                        var toks = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (toks.Length < 4) continue;
                        if (!ushort.TryParse(toks[0], out ushort dispId) || dispId == 0) continue;
                        if (!int.TryParse(toks[1], out int ox) || !int.TryParse(toks[2], out int oy) ||
                            !int.TryParse(toks[3], out int oz)) continue;
                        var made = CreateTileItem(resources, "0x" + dispId.ToString("X"),
                            new Point3D((short)(basePoint.X + ox), (short)(basePoint.Y + oy),
                                (sbyte)(basePoint.Z + oz), basePoint.Map));
                        if (made != null) created++;
                    }
                    SysMessage($"{created} multi components created.");
                }, 1);
                return true;
            }

            case "EDIT":
            {
                // Source-X OV_EDIT — open the property editor for the target
                // (the GM .edit flow already implements Cmd_EditItem).
                if (_commands != null && target is ObjBase editObj)
                    _commands.ExecuteEditForTarget(_character, args.Trim(), editObj.Uid.Value);
                return true;
            }

            case "NEWBIESKILL":
            {
                // Source-X CHV_NEWBIESKILL — run a [NEWBIE name] section on
                // the target char (falls back to the invoking char).
                var newbieChar = target as Character ?? _character;
                if (args.Trim().Length > 0)
                    _client.ApplyNewbieSection(newbieChar, args.Trim());
                return true;
            }

            case "TARGETCLOSE":
            {
                // Source-X CHV_TARGETCLOSE — drop the client's target cursor
                // (0x6C with the cancel flag) and clear server-side state.
                ClearPendingTargetState();
                Send(new SphereNet.Network.Packets.Outgoing.PacketTarget(0, 0, 3));
                return true;
            }

            case "REMOVECLILOC":
            {
                // Source-X OV_REMOVECLILOC — drop every custom tooltip line
                // with the given cliloc id (valid inside @ClientTooltip).
                if (SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(
                        args.Split(',')[0].Trim().AsSpan(), out long rmId))
                    _client.ScriptTooltipProperties?.RemoveAll(t => t.ClilocId == (uint)rmId);
                return true;
            }

            case "REPLACECLILOC":
            {
                // Source-X OV_REPLACECLILOC — replace the FIRST matching line
                // in place; no match = no-op.
                var parts = args.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length > 0 && _client.ScriptTooltipProperties is { } list &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(parts[0].AsSpan(), out long repId))
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].ClilocId == (uint)repId)
                        {
                            list[i] = ((uint)repId, parts.Length > 1 ? parts[1] : "");
                            break;
                        }
                    }
                }
                return true;
            }

            case "CLILOCLIST":
            {
                // Source-X OV_CLILOCLIST — print the object's tooltip lines
                // ("id=args"); "log" routes to the server log.
                bool toLog = args.Trim().Equals("log", StringComparison.OrdinalIgnoreCase);
                Action<string> sink = toLog
                    ? line => _logger.LogInformation("{Line}", line)
                    : SysMessage;
                var clilocObj = target as ObjBase ?? _character;
                if (clilocObj.TooltipCache is { } cachedTip)
                {
                    foreach (var (id, tipArgs) in cachedTip.Properties)
                        sink($"{id}={tipArgs}");
                }
                else if (_client.ScriptTooltipProperties is { Count: > 0 } live)
                {
                    foreach (var (id, tipArgs) in live)
                        sink($"{id}={tipArgs}");
                }
                return true;
            }

            case "BADSPAWN":
            {
                // Source-X CV_BADSPAWN — teleport to a spawner whose resource
                // no longer resolves; sets it as ACT.
                int wanted = int.TryParse(args.Trim(), out int bi) && bi > 0 ? bi : 0;
                int seen = 0;
                foreach (var obj in _world.GetAllObjects())
                {
                    if (obj is not Item it || it.IsDeleted) continue;
                    // A spawner item whose component never initialized = the
                    // spawn resource does not resolve (Source-X bad spawn).
                    if (it.ItemType is not (ItemType.SpawnChar or ItemType.SpawnItem)) continue;
                    if (it.SpawnChar != null || it.SpawnItem != null) continue;
                    if (seen++ < wanted) continue;
                    _character.MoveTo(it.GetTopLevelPosition());
                    SysMessage($"Bad spawn (0{it.Uid.Value:X}). Set as ACT.");
                    if (Targets != null) Targets.LastPickedSerial = it.Uid.Value;
                    return true;
                }
                SysMessage("There are no bad spawns.");
                return true;
            }
        }
        return false;
    }

    /// <summary>Two-corner rectangle selection built on the generic pending
    /// target callback (Source-X CLIMODE_TARG_TILE flow).</summary>
    private void BeginTwoCornerTarget(string firstPrompt, string secondPrompt, Action<Point3D, Point3D> onDone)
    {
        if (_character == null) return;
        SysMessage(firstPrompt);
        _client.SetPendingTarget((serial1, x1, y1, z1, _) =>
        {
            var p1 = ResolveTargetPoint(serial1, x1, y1, z1);
            SysMessage(secondPrompt);
            _client.SetPendingTarget((serial2, x2, y2, z2, _) =>
            {
                var p2 = ResolveTargetPoint(serial2, x2, y2, z2);
                onDone(p1, p2);
            }, 1);
        }, 1);
    }

    private Point3D ResolveTargetPoint(uint serial, short x, short y, sbyte z)
    {
        if (serial != 0 && serial != 0xFFFFFFFF &&
            _world.FindObject(new Serial(serial)) is { } obj)
            return obj.GetTopLevelPosition();
        return new Point3D(x, y, z, _character?.MapIndex ?? 0);
    }

    /// <summary>Create a TILE/UNEXTRACT item from an itemdef token and drop it
    /// move-never at the given point.</summary>
    private Item? CreateTileItem(SphereNet.Scripting.Resources.ResourceHolder? resources, string token, Point3D at)
    {
        ushort dispId = 0;
        if (SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(token.AsSpan(), out long numeric) && numeric > 0)
        {
            dispId = (ushort)numeric;
        }
        else if (resources != null)
        {
            var rid = resources.ResolveDefName(token);
            if (rid.IsValid && rid.Type == Core.Enums.ResType.ItemDef)
            {
                var def = DefinitionLoader.GetItemDef(rid.Index);
                dispId = def != null && def.DispIndex > 0 ? def.DispIndex : (ushort)rid.Index;
            }
        }
        if (dispId == 0) return null;

        var item = _world.CreateItem();
        item.BaseId = dispId;
        item.Attributes |= Core.Enums.ObjAttributes.Move_Never;
        _world.PlaceItem(item, at);
        return item;
    }

    /// <summary>Sandbox EXTRACT/UNEXTRACT file paths under the script database
    /// root (the FILE.* sandbox) — a bare filename lands there; escapes are
    /// rejected.</summary>
    private string? ResolveScriptFilePath(string fileName)
    {
        try
        {
            string root = Path.GetFullPath(string.IsNullOrEmpty(_scriptDatabaseRoot) ? "." : _scriptDatabaseRoot);
            string full = Path.GetFullPath(Path.Combine(root, fileName));
            string guard = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return full.StartsWith(guard, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Open a [SKILLMENU name] section as a 0x7C selection menu.
    /// Entries: first non-ON line is the title; each ON=&lt;itemdef&gt; [text]
    /// shows the itemdef's art ("&lt;name&gt;" or empty text = itemdef name);
    /// TEST=SKILL value gates the entry on the character's skills; remaining
    /// lines run as script verbs on selection.</summary>
    internal bool OpenSkillMenu(string menuName)
    {
        if (_character == null || string.IsNullOrWhiteSpace(menuName))
            return false;
        var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;
        if (resources == null)
            return false;

        var rid = resources.ResolveDefName(menuName.Trim());
        if (!rid.IsValid || rid.Type != Core.Enums.ResType.SkillMenu)
            return false;
        var keys = resources.GetResource(rid)?.StoredKeys;
        if (keys == null || keys.Count == 0)
            return false;

        string title = menuName;
        bool titleSeen = false;
        var options = new List<MenuOptionEntry>();
        MenuOptionEntry? current = null;
        bool skipping = false;

        foreach (var k in keys)
        {
            bool isOn = k.Key.Equals("ON", StringComparison.OrdinalIgnoreCase);
            if (!isOn && current == null && !skipping && !titleSeen)
            {
                title = k.RawLine;
                titleSeen = true;
                continue;
            }

            if (isOn)
            {
                if (current != null && !skipping)
                    options.Add(current);
                skipping = false;

                string onArg = k.Arg.Trim();
                int sp = onArg.IndexOfAny([' ', '\t']);
                string itemRef = sp < 0 ? onArg : onArg[..sp];
                string text = sp < 0 ? "" : onArg[(sp + 1)..].Trim();

                ushort modelId = 0;
                string itemName = itemRef;
                var irid = resources.ResolveDefName(itemRef);
                if (irid.IsValid)
                {
                    var idef = DefinitionLoader.GetItemDef(irid.Index);
                    modelId = idef?.DispIndex ?? (ushort)irid.Index;
                    if (!string.IsNullOrEmpty(idef?.Name))
                        itemName = idef.Name;
                }
                if (string.IsNullOrEmpty(text) || text.Equals("<name>", StringComparison.OrdinalIgnoreCase))
                    text = itemName;

                current = new MenuOptionEntry(modelId, 0, text, []);
                continue;
            }

            if (current == null)
                continue;

            if (k.Key.Equals("TEST", StringComparison.OrdinalIgnoreCase))
            {
                if (!PassesSkillMenuTest(k.Arg))
                {
                    current = null;
                    skipping = true;
                }
                continue;
            }
            if (k.Key.Equals("TESTIF", StringComparison.OrdinalIgnoreCase))
            {
                // Source-X TESTIF: hide the entry when the expression is false.
                // (_character is non-null here — guarded at method entry.)
                var interp = _triggerDispatcher?.Runner?.Interpreter;
                if (interp != null &&
                    !interp.EvaluateConditionForTarget(k.Arg, _character, _client))
                {
                    current = null;
                    skipping = true;
                }
                continue;
            }

            current.Script.Add(k);
        }
        if (current != null && !skipping)
            options.Add(current);

        if (options.Count == 0)
        {
            SysMessage("You are not able to use any of those options.");
            return true;
        }
        if (options.Count > MenuMaxItems)
            options.RemoveRange(MenuMaxItems, options.Count - MenuMaxItems);

        _pendingMenuId = (ushort)(Math.Abs(menuName.GetHashCode()) & 0xFFFF);
        _pendingMenuDefname = menuName;
        _pendingMenuOptions = options;

        var items = new List<MenuItemEntry>(options.Count);
        foreach (var opt in options)
            items.Add(new MenuItemEntry(opt.ModelId, opt.Hue, opt.Text));
        _netState.Send(new PacketMenuDisplay(_character.Uid.Value, _pendingMenuId, title, items));
        return true;
    }

    /// <summary>TEST= line of a skill menu entry: comma-separated
    /// "SKILLNAME value" pairs (legacy fixed-point values, "75.0" = 750);
    /// non-skill tokens are ignored.</summary>
    private bool PassesSkillMenuTest(string arg)
    {
        if (_character == null || string.IsNullOrWhiteSpace(arg))
            return true;
        foreach (var part in arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int sp = part.LastIndexOf(' ');
            if (sp <= 0)
                continue;
            string name = part[..sp].Trim();
            if (!Enum.TryParse<SkillType>(name, true, out var skill))
                continue;
            int required = ValueCurve.ParseSphereNumber(part[(sp + 1)..].Trim());
            if (_character.GetSkill(skill) < required)
                return false;
        }
        return true;
    }

    private void SummonFromMenu(string defname)
    {
        if (_character == null || string.IsNullOrWhiteSpace(defname))
            return;
        var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;

        var creature = _world.CreateCharacter();
        creature.IsPlayer = false;
        if (!Definitions.CharDefHelper.TryApplyDefName(creature, defname.Trim(), resources, refresh: false))
        {
            creature.Delete();
            SysMessage("Nothing answers the summons.");
            return;
        }

        creature.TryAssignOwnership(_character, _character, summoned: true, enforceFollowerCap: true);

        // Duration follows the Summon spell's curve when defined (engine
        // summon parity); fall back to two minutes.
        int durationTenths = _client.Spells?.GetSpellDef(SpellType.SummonCreature)
            ?.GetDuration(_character.GetSkill(SkillType.Magery)) ?? 0;
        if (durationTenths <= 0)
            durationTenths = 1200;
        creature.SetTag("SUMMON_DURATION", durationTenths.ToString());
        creature.SetTag("SUMMON_EXPIRE_TICK",
            (Environment.TickCount64 + durationTenths * 100L).ToString());

        var pos = new Point3D((short)(_character.X + 1), _character.Y, _character.Z, _character.MapIndex);
        _world.PlaceCharacter(creature, pos);
        _client.BroadcastCharacterAppear?.Invoke(creature);
    }

    private void MakeItemFromMenu(string defname)
    {
        if (_character == null || string.IsNullOrWhiteSpace(defname))
            return;
        var resources = _commands?.Resources ?? DefinitionLoader.StaticResources;
        if (resources == null)
            return;

        var irid = resources.ResolveDefName(defname.Trim());
        if (!irid.IsValid)
            return;
        // The recipe's identity is the ITEMDEF resource id, the same id Source-X
        // carries through Skill_MakeItem (CCharSkill.cpp:870). Reducing it to the
        // display graphic first meant a defname could build a DIFFERENT item that
        // merely shared the art.
        var craftE = _client.CraftE;
        var recipe = craftE?.TryGetRecipe(irid.Index);
        if (craftE == null || recipe == null)
        {
            SysMessage("You don't know how to make that.");
            return;
        }
        if (!craftE.CanCraft(_character, recipe))
        {
            SysMessage("You lack the skill, materials or work site for that.");
            return;
        }

        _client.BeginPendingCraft(recipe, recipe.PrimarySkill, reopenGump: false);
    }

    private bool HandleDialogCommand(string args, ObjBase? subject = null)
    {
        if (_character == null) return false;

        string raw = args.Trim();
        string dialogId = "script_dialog";
        string closeSpec = "";
        int requestedPage = 1;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            int sep = raw.IndexOfAny([' ', ',']);
            if (sep < 0) { dialogId = raw; }
            else { dialogId = raw[..sep]; closeSpec = raw[(sep + 1)..].TrimStart(' ', ','); }
        }

        dialogId = dialogId.Trim().Trim(',', ';');
        if (string.IsNullOrWhiteSpace(dialogId)) dialogId = "script_dialog";

        if (!string.IsNullOrWhiteSpace(closeSpec))
        {
            string[] dialogTokens = closeSpec.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dialogTokens.Length > 0 && int.TryParse(dialogTokens[0], out int parsedPage))
                requestedPage = parsedPage;
        }

        if (OpenNamedDialog(dialogId, requestedPage, subject)) return true;

        string closeFn = "";
        if (!string.IsNullOrWhiteSpace(closeSpec))
        {
            string[] tokens = closeSpec.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 0)
            {
                if (tokens[0].Equals("DIALOGCLOSE", StringComparison.OrdinalIgnoreCase))
                    closeFn = tokens.Length > 1 ? tokens[1] : "";
                else
                    closeFn = tokens[0];
            }
        }

        _pendingDialogCloseFunction = string.IsNullOrWhiteSpace(closeFn)
            ? $"f_dialogclose_{dialogId}"
            : closeFn.Trim().Trim(',', ';');
        _pendingDialogArgs = dialogId;
        string title = $"Dialog {dialogId}";

        uint gumpId = (uint)Math.Abs(dialogId.GetHashCode());
        var gump = new GumpBuilder(_character.Uid.Value, gumpId, 360, 180);
        gump.AddResizePic(0, 0, 5054, 360, 180);
        gump.AddText(20, 20, 0, title);
        gump.AddText(20, 60, 0, $"[{dialogId}]");
        gump.AddButton(140, 130, 4005, 4007, 1);
        SendGump(gump);
        return true;
    }

    private static bool TryParseScriptPacket(string args, out byte[] packet, out string error)
    {
        packet = [];
        error = "";
        var tokens = args.Split([' ', '\t', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = "empty packet";
            return false;
        }

        var bytes = new List<byte>(tokens.Length + 8);
        foreach (string raw in tokens)
        {
            if (!TryParsePacketToken(raw, bytes, out error))
                return false;
            if (bytes.Count > 256)
            {
                error = "packet exceeds 256-byte script limit";
                return false;
            }
        }

        packet = bytes.ToArray();
        return packet.Length > 0;
    }

    private static bool TryParsePacketToken(string token, List<byte> bytes, out string error)
    {
        error = "";
        string t = token.Trim();
        if (t.Length == 0)
            return true;

        int colon = t.IndexOf(':');
        string kind = "";
        if (colon > 0)
        {
            kind = t[..colon].ToUpperInvariant();
            t = t[(colon + 1)..];
        }
        else if (t.Length > 1 && (t[0] == 'B' || t[0] == 'W' || t[0] == 'D') &&
                 (char.IsDigit(t[1]) || t[1] == 'x' || t[1] == 'X'))
        {
            kind = t[0] switch
            {
                'B' => "BYTE",
                'W' => "WORD",
                'D' => "DWORD",
                _ => ""
            };
            t = t[1..];
        }

        if (!TryParsePacketNumber(t, out uint value))
        {
            error = $"invalid token '{token}'";
            return false;
        }

        switch (kind)
        {
            case "":
            case "BYTE":
                if (value > byte.MaxValue)
                {
                    error = $"byte token out of range '{token}'";
                    return false;
                }
                bytes.Add((byte)value);
                return true;
            case "WORD":
                if (value > ushort.MaxValue)
                {
                    error = $"word token out of range '{token}'";
                    return false;
                }
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
                return true;
            case "DWORD":
                bytes.Add((byte)(value >> 24));
                bytes.Add((byte)(value >> 16));
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
                return true;
            default:
                error = $"unknown token type '{kind}'";
                return false;
        }
    }

    private static bool TryParsePacketNumber(string token, out uint value)
    {
        value = 0;
        string t = token.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        if (t.Length > 1 && t[0] == '0' && t.All(c => Uri.IsHexDigit(c)))
            return uint.TryParse(t.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out value);
        if (t.Any(c => char.IsLetter(c)))
            return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out value);
        return uint.TryParse(t, out value);
    }

    /// <summary>Map our internal <see cref="SphereNet.Core.Enums.ResType"/> to the
    /// Source-X RES_* numeric code (as defined in [DEFNAME] sphere_defs), so that
    /// <c>&lt;RESOURCETYPE x&gt;</c> compares equal to script constants like
    /// <c>&lt;def.res_chardef&gt;</c> (=6) and <c>&lt;def.res_itemdef&gt;</c> (=14).</summary>
    private static int SourceXResValue(SphereNet.Core.Enums.ResType type) => type switch
    {
        SphereNet.Core.Enums.ResType.Account => 1,
        SphereNet.Core.Enums.ResType.Area => 3,
        SphereNet.Core.Enums.ResType.Book => 5,
        SphereNet.Core.Enums.ResType.CharDef => 6,
        SphereNet.Core.Enums.ResType.Comment => 7,
        SphereNet.Core.Enums.ResType.DefName => 8,
        SphereNet.Core.Enums.ResType.Dialog => 9,
        SphereNet.Core.Enums.ResType.Events => 10,
        SphereNet.Core.Enums.ResType.Function => 12,
        SphereNet.Core.Enums.ResType.GamePage => 13,
        SphereNet.Core.Enums.ResType.ItemDef => 14,
        SphereNet.Core.Enums.ResType.Menu => 17,
        SphereNet.Core.Enums.ResType.Names => 19,
        SphereNet.Core.Enums.ResType.NewBie => 20,
        SphereNet.Core.Enums.ResType.Obscene => 22,
        SphereNet.Core.Enums.ResType.PlevelCfg => 23,
        SphereNet.Core.Enums.ResType.RegionResource => 24,
        SphereNet.Core.Enums.ResType.RegionType => 25,
        SphereNet.Core.Enums.ResType.ResourceList => 27,
        SphereNet.Core.Enums.ResType.RoomDef => 29,
        SphereNet.Core.Enums.ResType.Scroll => 31,
        SphereNet.Core.Enums.ResType.Sector => 32,
        SphereNet.Core.Enums.ResType.SkillDef => 34,
        SphereNet.Core.Enums.ResType.SkillClass => 35,
        SphereNet.Core.Enums.ResType.SkillMenu => 36,
        SphereNet.Core.Enums.ResType.Spawn => 37,
        SphereNet.Core.Enums.ResType.Speech => 38,
        SphereNet.Core.Enums.ResType.SpellDef => 39,
        SphereNet.Core.Enums.ResType.Sphere => 40,
        SphereNet.Core.Enums.ResType.ServerConfig => 40,
        SphereNet.Core.Enums.ResType.Template => 46,
        SphereNet.Core.Enums.ResType.Tip => 48,
        SphereNet.Core.Enums.ResType.TypeDef => 49,
        SphereNet.Core.Enums.ResType.WebPage => 52,
        SphereNet.Core.Enums.ResType.WorldChar => 54,
        SphereNet.Core.Enums.ResType.WorldItem => 55,
        SphereNet.Core.Enums.ResType.WorldScript => 57,
        _ => 0, // Unknown / MultiDef / Stone — no Source-X RES_ comparison value
    };

    public bool TryResolveScriptVariable(string varName, IScriptObj target, ITriggerArgs? triggerArgs, out string value)
    {
        value = "";
        if (_character == null) return false;

        // Common Sphere runtime constants used by admin/dialog scripts.
        // GETREFTYPE — match Source-X [DEFNAME ref_types] bit layout so
        // <GetRefType> == <Def.TRef_Char> works straight from script.
        if (varName.Equals("GETREFTYPE", StringComparison.OrdinalIgnoreCase))
        {
            if (target is SphereNet.Game.Objects.Items.Item)
                value = "0" + 0x080000.ToString("X");
            else if (target is SphereNet.Game.Objects.Characters.Character)
                value = "0" + 0x040000.ToString("X");
            else
                value = "0" + 0x010000.ToString("X");
            return true;
        }

        // Generic DEF.X / DEF0.X lookup — covers everything in a [DEFNAME ...]
        // section (admin_hidehighpriv, admin_flag_1, tcolor_orange, …). Admin
        // dialogs hit these for virtually every label; without this every
        // <Def.X> fell back to unresolved = empty string, leaving the gump
        // full of gaps.
        if (varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DEF0.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = varName.IndexOf('.');
            string defKey = varName[(dot + 1)..];
            bool asNumeric = varName[..dot].Equals("DEF0", StringComparison.OrdinalIgnoreCase);

            if (_commands?.Resources != null)
            {
                // String defs (admin_flag_X = "Invulnerability", etc.)
                if (_commands.Resources.TryGetDefValue(defKey, out string defVal))
                {
                    value = defVal;
                    return true;
                }
                // Numeric defs (Admin_Hidehighpriv 1) — stored as ResourceId index.
                var rid = _commands.Resources.ResolveDefName(defKey);
                if (rid.IsValid)
                {
                    value = asNumeric
                        ? rid.Index.ToString()
                        : $"0{rid.Index:X}"; // <Def.X> legacy-hex form
                    return true;
                }
            }
            value = "0";
            return true; // answered as "0" rather than unresolved — matches Sphere behaviour
        }
        // RESOURCETYPE / RESOURCEINDEX (Source-X): resolve a defname to its
        // resource TYPE code and resource INDEX. Worldgen spawners compare
        // <RESOURCETYPE <entry>> against <def.res_chardef> / <def.res_itemdef>,
        // so the type MUST use the Source-X RES_* numbering (CharDef=6,
        // ItemDef=14, Spawn=37, …) — NOT our internal ResType enum order.
        if (varName.StartsWith("RESOURCETYPE ", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("RESOURCEINDEX ", StringComparison.OrdinalIgnoreCase))
        {
            int sp = varName.IndexOf(' ');
            bool wantIndex = varName[..sp].Equals("RESOURCEINDEX", StringComparison.OrdinalIgnoreCase);
            string arg = varName[(sp + 1)..].Trim();
            var rid = _commands?.Resources?.ResolveDefName(arg)
                      ?? SphereNet.Core.Types.ResourceId.Invalid;
            if (!rid.IsValid)
            {
                value = "0"; // RES_UNKNOWN / no index
                return true;
            }
            value = wantIndex
                ? rid.Index.ToString()
                : SourceXResValue(rid.Type).ToString();
            return true;
        }
        if (varName.StartsWith("ISDIALOGOPEN.", StringComparison.OrdinalIgnoreCase))
        {
            value = IsScriptDialogOpen(varName["ISDIALOGOPEN.".Length..].Trim()) ? "1" : "0";
            return true;
        }

        if (varName.StartsWith("FILE.", StringComparison.OrdinalIgnoreCase))
        {
            if (_scriptFile == null)
            {
                value = "0";
                return true;
            }

            string fileProp = varName[5..].ToUpperInvariant();
            switch (fileProp)
            {
                case "OPEN":
                {
                    // FILE.OPEN as read property — returns "1" if file is open
                    value = _scriptFile.IsOpen ? "1" : "0";
                    return true;
                }
                case "INUSE":
                    value = _scriptFile.IsOpen ? "1" : "0";
                    return true;
                case "ISEOF":
                    value = _scriptFile.IsEof ? "1" : "0";
                    return true;
                case "FILEPATH":
                    value = _scriptFile.FilePath;
                    return true;
                case "POSITION":
                    value = _scriptFile.Position.ToString();
                    return true;
                case "LENGTH":
                    value = _scriptFile.Length.ToString();
                    return true;
                case "READCHAR":
                    // Numeric byte value at the current position (Source-X).
                    value = _scriptFile.ReadChar();
                    return true;
                case "READBYTE":
                    // Bare READBYTE = one byte's worth of text.
                    value = _scriptFile.ReadBytes(1);
                    return true;
                case "MODE.APPEND":
                    value = _scriptFile.ModeAppend ? "1" : "0";
                    return true;
                case "MODE.CREATE":
                    value = _scriptFile.ModeCreate ? "1" : "0";
                    return true;
                case "MODE.READFLAG":
                    value = _scriptFile.ModeRead ? "1" : "0";
                    return true;
                case "MODE.WRITEFLAG":
                    value = _scriptFile.ModeWrite ? "1" : "0";
                    return true;
                default:
                    // FILE.READBYTE n — Source-X reads n bytes as text.
                    if (fileProp.StartsWith("READBYTE", StringComparison.Ordinal))
                    {
                        string rbArg = fileProp.Length > 8 ? fileProp[8..].Trim() : "";
                        if (string.IsNullOrEmpty(rbArg) && varName.Length > 13)
                            rbArg = varName[13..].Trim();
                        int byteCount = 1;
                        if (!string.IsNullOrEmpty(rbArg))
                            int.TryParse(rbArg, out byteCount);
                        value = _scriptFile.ReadBytes(byteCount);
                        return true;
                    }
                    // FILE.READLINE n, FILE.SEEK pos, FILE.FILELINES path, FILE.FILEEXIST path
                    if (fileProp.StartsWith("READLINE", StringComparison.Ordinal))
                    {
                        string lineArg = fileProp.Length > 8 ? fileProp[8..].Trim() : "";
                        if (string.IsNullOrEmpty(lineArg) && varName.Length > 13)
                            lineArg = varName[13..].Trim();
                        int lineNum = 0;
                        if (!string.IsNullOrEmpty(lineArg))
                            int.TryParse(lineArg, out lineNum);
                        value = _scriptFile.ReadLine(lineNum);
                        return true;
                    }
                    if (fileProp.StartsWith("SEEK", StringComparison.Ordinal))
                    {
                        string seekArg = fileProp.Length > 4 ? fileProp[4..].Trim() : "";
                        if (string.IsNullOrEmpty(seekArg) && varName.Length > 9)
                            seekArg = varName[9..].Trim();
                        _scriptFile.Seek(seekArg);
                        value = _scriptFile.Position.ToString();
                        return true;
                    }
                    if (fileProp.StartsWith("FILELINES", StringComparison.Ordinal))
                    {
                        string flArg = fileProp.Length > 9 ? fileProp[9..].Trim() : "";
                        if (string.IsNullOrEmpty(flArg) && varName.Length > 14)
                            flArg = varName[14..].Trim();
                        value = _scriptFile.GetFileLinesRelative(flArg).ToString();
                        return true;
                    }
                    if (fileProp.StartsWith("FILEEXIST", StringComparison.Ordinal))
                    {
                        string feArg = fileProp.Length > 9 ? fileProp[9..].Trim() : "";
                        if (string.IsNullOrEmpty(feArg) && varName.Length > 14)
                            feArg = varName[14..].Trim();
                        value = _scriptFile.FileExistsRelative(feArg) ? "1" : "0";
                        return true;
                    }
                    break;
            }
            value = "0";
            return true;
        }

        if (varName.Equals("DB.CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptDb?.IsConnected == true ? "1" : "0";
            return true;
        }
        if (varName.StartsWith("DB.CONNECTED.", StringComparison.OrdinalIgnoreCase) && _scriptDb != null)
        {
            string connName = varName[13..];
            value = _scriptDb.IsConnected_Named(connName) ? "1" : "0";
            return true;
        }
        if (varName.Equals("DB.ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptDb?.ActiveSessionName ?? "";
            return true;
        }
        if (varName.StartsWith("DB.ESCAPEDATA.", StringComparison.OrdinalIgnoreCase) && _scriptDb != null)
        {
            string rawData = varName[14..];
            value = _scriptDb.EscapeData(rawData);
            return true;
        }
        if (_scriptDb != null && _scriptDb.TryResolveRowValue(varName, out string dbVal))
        {
            value = dbVal;
            return true;
        }
        if (varName.Equals("MDB.CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptMdb?.IsConnected == true ? "1" : "0";
            return true;
        }
        if (varName.StartsWith("MDB.ESCAPEDATA.", StringComparison.OrdinalIgnoreCase) && _scriptMdb != null)
        {
            value = _scriptMdb.EscapeData(varName[15..]);
            return true;
        }
        if (_scriptMdb != null && varName.StartsWith("MDB.ROW.", StringComparison.OrdinalIgnoreCase))
        {
            string mdbKey = "db.row." + varName[8..];
            if (_scriptMdb.TryResolveRowValue(mdbKey, out string mdbVal))
            {
                value = mdbVal;
                return true;
            }
        }
        if (varName.Equals("LDB.CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptLdb?.IsConnected == true ? "1" : "0";
            return true;
        }
        if (_scriptLdb != null && varName.StartsWith("LDB.ROW.", StringComparison.OrdinalIgnoreCase))
        {
            string ldbKey = "db.row." + varName[8..];
            if (_scriptLdb.TryResolveRowValue(ldbKey, out string ldbVal))
            {
                value = ldbVal;
                return true;
            }
        }
        if (varName.StartsWith("ACCOUNT.", StringComparison.OrdinalIgnoreCase))
        {
            if (_account != null && _account.TryGetProperty(varName["ACCOUNT.".Length..], out string acctVal))
            {
                value = acctVal;
                return true;
            }
            return false;
        }

        if (varName.Equals("TARGP", StringComparison.OrdinalIgnoreCase))
        {
            var p = Targets.LastScriptPoint ?? _character.Position;
            value = $"{p.X},{p.Y},{p.Z},{p.Map}";
            return true;
        }

        if (varName.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = varName.IndexOf('.');
            if (dot > 0)
            {
                string tagName = varName[(dot + 1)..].Trim().Trim(',', ';');
                string? tagVal = _character.CTags.Get(tagName);
                if (tagVal != null)
                {
                    value = tagVal;
                    return true;
                }
            }
            return false;
        }

        int objDot = varName.IndexOf('.');
        if (objDot > 0)
        {
            string root = varName[..objDot].Trim();
            string prop = varName[(objDot + 1)..].Trim();
            if (_character.TryGetProperty($"TAG.{root}", out string objRef) && TryFindObjectByScriptRef(objRef, out var scopedObj))
            {
                if (scopedObj.TryGetProperty(prop, out string scopedVal))
                {
                    value = scopedVal;
                    return true;
                }
            }
        }

        if (varName.StartsWith("ARGO.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("ACT.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("LINK.", StringComparison.OrdinalIgnoreCase))
        {
            IScriptObj? obj = null;
            int dot = varName.IndexOf('.');
            string root = dot > 0 ? varName[..dot].ToUpperInvariant() : varName.ToUpperInvariant();
            string sub = dot > 0 ? varName[(dot + 1)..] : "";
            if (root == "ARGO") obj = triggerArgs?.Object1;
            else if (root is "ACT" or "LINK") obj = triggerArgs?.Object2;

            if (obj == null) return false;

            if (sub.StartsWith("ACCOUNT.", StringComparison.OrdinalIgnoreCase) && obj is Character chAcct)
            {
                var acct = Character.ResolveAccountForChar?.Invoke(chAcct.Uid);
                if (acct != null && acct.TryGetProperty(sub["ACCOUNT.".Length..], out string acctVal))
                {
                    value = acctVal;
                    return true;
                }
                return false;
            }
            if (obj.TryGetProperty(sub, out string propVal))
            {
                value = propVal;
                return true;
            }
        }

        // Resolve object-scoped locals like OBJ.ISPLAYER where OBJ contains a UID string.
        int localDot = varName.IndexOf('.');
        if (localDot > 0)
        {
            string localName = varName[..localDot];
            if (triggerArgs != null && target.TryGetProperty($"TAG.{localName}", out string tagVal) && TryFindObjectByScriptRef(tagVal, out var refObj))
            {
                if (refObj.TryGetProperty(varName[(localDot + 1)..], out string scopedVal))
                {
                    value = scopedVal;
                    return true;
                }
            }
        }

        // Bare defname constants for general script execution paths
        // (outside dialog render), e.g. <statf_insubstantial>.
        if (_commands?.Resources != null && IsPlainDefToken(varName))
        {
            var rid = _commands.Resources.ResolveDefName(varName);
            if (rid.IsValid)
            {
                value = rid.Index.ToString();
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<IScriptObj> QueryScriptObjects(string query, IScriptObj target, string args, ITriggerArgs? triggerArgs)
    {
        if (_character == null) return Array.Empty<IScriptObj>();

        if (query.Equals("FORPLAYERS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => c.IsPlayer && c.MapIndex == _character.MapIndex &&
                            c.Position.GetDistanceTo(_character.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        if (query.Equals("FORINSTANCES", StringComparison.OrdinalIgnoreCase))
        {
            string def = args.Trim();
            if (def.Length == 0) return Array.Empty<IScriptObj>();

            int? itemBase = null;
            int? charBase = null;
            var rid = _commands?.Resources?.ResolveDefName(def) ?? ResourceId.Invalid;
            if (rid.IsValid)
            {
                if (rid.Type == Core.Enums.ResType.ItemDef) itemBase = rid.Index;
                else if (rid.Type == Core.Enums.ResType.CharDef) charBase = rid.Index;
            }
            else if (int.TryParse(def.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out int parsed))
            {
                itemBase = parsed;
                charBase = parsed;
            }

            return _world.GetAllObjects()
                .Where(o =>
                    (o is Item it && itemBase.HasValue && it.BaseId == (ushort)itemBase.Value) ||
                    (o is Character ch && charBase.HasValue && ch.BaseId == (ushort)charBase.Value))
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORCHARS — all characters (players + NPCs) within radius
        if (query.Equals("FORCHARS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => !c.IsDeleted && c.MapIndex == map &&
                            center.GetDistanceTo(c.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORCLIENTS — only online player characters within radius
        if (query.Equals("FORCLIENTS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => c.IsPlayer && c.IsOnline && !c.IsDeleted &&
                            c.MapIndex == map && center.GetDistanceTo(c.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORITEMS — all ground items within radius
        if (query.Equals("FORITEMS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Item>()
                .Where(it => !it.IsDeleted && it.IsOnGround &&
                             it.MapIndex == map && center.GetDistanceTo(it.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FOROBJS — all characters + items within radius
        if (query.Equals("FOROBJS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            var result = new List<IScriptObj>();
            foreach (var obj in _world.GetAllObjects())
            {
                if (obj.IsDeleted) continue;
                if (obj.MapIndex != map) continue;
                if (center.GetDistanceTo(obj.Position) > range) continue;
                if (obj is Item it && !it.IsOnGround) continue;
                result.Add(obj);
            }
            return result;
        }

        // FORCONT — all items inside a container (args: "uid [depth]")
        if (query.Equals("FORCONT", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            if (!TryFindObjectByScriptRef(parts[0], out var contObj) || contObj is not Item container)
                return Array.Empty<IScriptObj>();
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result);
            return result;
        }

        // FORCONTID — items in current target's backpack matching a BASEID (args: "baseid [depth]")
        if (query.Equals("FORCONTID", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            string defName = parts[0];
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            ushort? targetBaseId = ResolveBaseId(defName);
            if (!targetBaseId.HasValue) return Array.Empty<IScriptObj>();

            // Iterate the target character's backpack, or the target item as container
            Item? container = target is Character ch ? ch.Backpack : target as Item;
            if (container == null) return Array.Empty<IScriptObj>();
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result, baseIdFilter: targetBaseId.Value);
            return result;
        }

        // FORCONTTYPE — items in current target's backpack matching a TYPE (args: "type [depth]")
        if (query.Equals("FORCONTTYPE", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            string typeName = parts[0];
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            int? typeFilter = ResolveItemType(typeName);
            if (!typeFilter.HasValue) return Array.Empty<IScriptObj>();

            Item? container = target is Character ch ? ch.Backpack : target as Item;
            if (container == null) return Array.Empty<IScriptObj>();
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result, typeFilter: typeFilter.Value);
            return result;
        }

        // FORCHARLAYER — items on a specific equipment layer of the target character
        if (query.Equals("FORCHARLAYER", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(args.Trim(), out int layerNum)) return Array.Empty<IScriptObj>();
            Character? ch = target as Character ?? _character;
            var item = ch.GetEquippedItem((Layer)layerNum);
            if (item == null) return Array.Empty<IScriptObj>();
            // Layer 30 (Special) can contain multiple memory items; single item for other layers
            return new List<IScriptObj> { item };
        }

        if (query.Equals("FORCHARMEMORYTYPE", StringComparison.OrdinalIgnoreCase))
        {
            Character? ch = target as Character ?? _character;
            if (ch == null)
                return Array.Empty<IScriptObj>();
            return ch.GetMemoryEntriesByType(args, _world);
        }

        return Array.Empty<IScriptObj>();
    }

    private void CollectContainerItems(Item container, int depth, List<IScriptObj> result,
        ushort? baseIdFilter = null, int? typeFilter = null)
    {
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = true;
            if (baseIdFilter.HasValue && item.BaseId != baseIdFilter.Value) matches = false;
            if (typeFilter.HasValue && (int)item.ItemType != typeFilter.Value) matches = false;
            if (matches) result.Add(item);
            if (depth > 0 && item.ContentCount > 0)
                CollectContainerItems(item, depth - 1, result, baseIdFilter, typeFilter);
        }
    }

    private ushort? ResolveBaseId(string defName)
    {
        var rid = _commands?.Resources?.ResolveDefName(defName) ?? ResourceId.Invalid;
        if (rid.IsValid) return (ushort)rid.Index;
        if (ushort.TryParse(defName.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, null, out ushort v))
            return v;
        return null;
    }

    private int? ResolveItemType(string typeName)
    {
        // Try as enum name (e.g. "t_spellbook" → strip "t_" prefix, parse as ItemType)
        string name = typeName.TrimStart();
        if (name.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            name = name[2..];
        if (Enum.TryParse<Core.Enums.ItemType>(name, ignoreCase: true, out var itemType))
            return (int)itemType;
        // Try as numeric
        if (int.TryParse(typeName, out int num))
            return num;
        return null;
    }

    private bool TryFindObjectByScriptRef(string value, out IScriptObj obj)
    {
        obj = null!;
        string v = value.Trim();
        if (v.StartsWith("0", StringComparison.OrdinalIgnoreCase))
            v = v[1..];
        if (!uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return false;
        var found = _world.FindObject(new Serial(uid));
        if (found == null) return false;
        obj = found;
        return true;
    }

    private static bool TryParsePoint(string args, Point3D current, out Point3D point)
    {
        point = current;
        var parts = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!short.TryParse(parts[0], out short x) || !short.TryParse(parts[1], out short y))
            return false;
        sbyte z = parts.Length > 2 && sbyte.TryParse(parts[2], out sbyte tz) ? tz : current.Z;
        byte map = parts.Length > 3 && byte.TryParse(parts[3], out byte tm) ? tm : current.Map;
        point = new Point3D(x, y, z, map);
        return true;
    }

    private static bool TryParseScriptNumber(string text, out long value) =>
        SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(text.AsSpan(), out value);
}
