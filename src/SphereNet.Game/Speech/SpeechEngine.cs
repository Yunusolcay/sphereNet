using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Definitions;
using SphereNet.Game.Scripting;
using SphereNet.Game.Party;
using SphereNet.Game.World;
using SphereNet.Game.Messages;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Speech;

/// <summary>
/// Speech/talk modes. Maps to TALKMODE_TYPE in Source-X sphereproto.h.
/// </summary>
public enum TalkMode : byte
{
    Say = 0,
    System = 1,
    Emote = 2,
    Item = 6,
    NoScroll = 7,
    Whisper = 8,
    Yell = 9,
    Spell = 10,
    Guild = 0xD,
    Alliance = 0xE,
    Command = 0xF,
    Broadcast = 0xFF,
}

/// <summary>
/// Speech engine. Maps to CClient::Event_Talk and CWorldComm::Speak in Source-X.
/// Routes speech to nearby characters, NPCs, and handles GM commands.
/// </summary>
public sealed class SpeechEngine
{
    private readonly GameWorld _world;

    /// <summary>Base hearing distances (in tiles) per mode.</summary>
    private const int DistanceSay = 18;
    private const int DistanceWhisper = 3;
    private const int DistanceYell = 48;

    /// <summary>GM command prefix (configurable).</summary>
    public char CommandPrefix { get; set; } = '.';

    /// <summary>Fired when an NPC hears speech (for keyword response).</summary>
    public event Action<Character, Character, string, TalkMode>? OnNpcHear;

    /// <summary>Fired when guild/party message should be routed.</summary>
    public event Action<Character, string, TalkMode>? OnChannelMessage;

    /// <summary>Party manager reference for party speech.</summary>
    public Party.PartyManager? PartyManager { get; set; }

    /// <summary>Guild manager reference for guild/alliance speech.</summary>
    public Guild.GuildManager? GuildManager { get; set; }

    public SpeechEngine(GameWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// Process speech from a character. Maps to Event_Talk flow.
    /// Returns true if the speech was handled (e.g., as a command).
    /// </summary>
    public bool ProcessSpeech(Character speaker, string text, TalkMode mode, ushort hue = 0x03B2, ushort font = 3)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        // Command dispatch is handled centrally in GameClient.HandleSpeech.
        // SpeechEngine is kept focused on non-command speech routing.

        // Guild/Alliance chat: not spatial, routed separately
        if (mode == TalkMode.Guild || mode == TalkMode.Alliance)
        {
            RouteChannelMessage(speaker, text, mode);
            return true;
        }

        // Get hearing distance based on mode
        int hearRange = mode switch
        {
            TalkMode.Whisper => DistanceWhisper,
            TalkMode.Yell => DistanceYell,
            _ => DistanceSay,
        };

        // Broadcast (GM yell)
        if (mode == TalkMode.Yell && speaker.PrivLevel >= PrivLevel.GM)
            hearRange = 0; // 0 = global broadcast handled below

        // Send speech to all characters in range
        var listeners = hearRange == 0
            ? _world.GetCharsInRange(speaker.Position, 9999)
            : _world.GetCharsInRange(speaker.Position, hearRange);

        foreach (var listener in listeners)
        {
            if (listener == speaker) continue;
            if (listener.IsDead && mode != TalkMode.Yell) continue;

            // Hidden/invisible check for whisper
            if (mode == TalkMode.Whisper && listener.IsInvisible) continue;

            // NPC keyword handling
            if (!listener.IsPlayer)
            {
                OnNpcHear?.Invoke(speaker, listener, text, mode);
            }
        }

        return false;
    }

    /// <summary>Route guild/alliance/party messages to correct recipients.</summary>
    private void RouteChannelMessage(Character speaker, string text, TalkMode mode)
    {
        if (mode == TalkMode.Guild)
        {
            var guild = GuildManager?.FindGuildFor(speaker.Uid);
            if (guild != null)
            {
                foreach (var member in guild.Members)
                {
                    if (member.CharUid == speaker.Uid) continue;
                    OnChannelMessage?.Invoke(speaker, text, mode);
                }
            }
        }
        else if (mode == TalkMode.Alliance)
        {
            var guild = GuildManager?.FindGuildFor(speaker.Uid);
            if (guild != null)
            {
                foreach (var allyStone in guild.Allies)
                {
                    var allyGuild = GuildManager?.GetGuild(allyStone);
                    if (allyGuild != null)
                    {
                        foreach (var member in allyGuild.Members)
                            OnChannelMessage?.Invoke(speaker, text, mode);
                    }
                }
                foreach (var member in guild.Members)
                    OnChannelMessage?.Invoke(speaker, text, mode);
            }
        }
    }
}

public enum CommandResult { NotFound, InsufficientPriv, Failed, Executed }

/// <summary>
/// GM command handler. Maps to CClient::Event_Command dispatch.
/// Routes commands by verb to registered handlers.
/// </summary>
public sealed class CommandHandler
{
    private const PrivLevel DefaultScriptCommandPrivLevel = PrivLevel.Owner; // Source-X fallback: PLEVEL 7
    private GameWorld? _registeredWorld;

    /// <summary>Null console for TryExecuteCommand calls that don't need output.</summary>
    private sealed class NullConsole : ITextConsole
    {
        public static readonly NullConsole Instance = new();
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "System";
    }

    public delegate void CommandFunc(Character gm, string args);
    public delegate bool CommandFuncEx(Character gm, string args);

    private readonly Dictionary<string, CommandFunc> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CommandFuncEx> _commandsEx = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PrivLevel> _privLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PrivLevel> _scriptCommandPrivLevels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _preferBuiltinVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "TELE", "EDIT", "XEDIT"
    };

    /// <summary>Set this to enable AREADEF-based location lookup for GO command.</summary>
    public ResourceHolder? Resources { get; set; }
    public char CommandPrefix { get; set; } = '.';

    public event Action? OnSaveCommand;
    public event Action? OnShutdownCommand;
    public event Action<string>? OnBroadcastCommand;
    public event Action? OnResyncCommand;
    public event Action<Character>? OnCharacterResyncRequested;
    public event Action<Character>? OnTeleportTargetRequested;
    /// <summary>Fired after a teleport that crossed map boundaries. Handler should
    /// send PacketMapChange and full resync to the character's owner client.</summary>
    public event Action<Character>? OnCharacterMapChanged;
    /// <summary>Fired when a character's own appearance flags (e.g. invisible, war mode)
    /// changed and the owner client must re-render the player via DrawPlayer.</summary>
    public event Action<Character>? OnCharacterSelfRedraw;
    /// <summary>Fired by .STRESS to queue large-scale test population generation.</summary>
    public event Action<int, int>? OnStressGenerateRequested;
    /// <summary>Fired by .STRESSREPORT — dumps runtime metrics to server log.</summary>
    public event Action? OnStressReportRequested;
    /// <summary>Fired by .STRESSCLEAN — deletes all stress-tagged objects.</summary>
    public event Action? OnStressCleanupRequested;
    /// <summary>Fired by .SAVEFORMAT — switches save format (and optional shard
    /// count) then forces a full save in the new format. Argument string is
    /// already parsed: (format, shards). shards=-1 means "keep current".</summary>
    public event Action<string, int>? OnSaveFormatChangeRequested;
    /// <summary>Fired by .SCRIPTDEBUG — enables/disables expression-parser
    /// diagnostic logging. Host wires this to ExpressionParser.DebugUnresolved.</summary>
    public event Action<bool>? OnScriptDebugToggleRequested;
    public event Action<Character, string>? OnAddTargetRequested;
    public event Action<Character>? OnRemoveTargetRequested;
    /// <summary>Fired by .RESURRECT (no args = self, with UID = direct,
    /// no UID + alive caller = target cursor). Wired in Program.cs to
    /// resolve to the victim's GameClient.OnResurrect so the proper
    /// 0x77/0x20 broadcast happens. Source-X equivalent: DV_RESURRECT
    /// verb on a character.</summary>
    public event Action<Character, Core.Types.Serial?>? OnResurrectRequested;
    /// <summary>Fired by .XRESURRECT — request a target cursor on the GM
    /// client; the picked character is then resurrected.</summary>
    public event Action<Character>? OnResurrectTargetRequested;
    public event Action<Character, string, IReadOnlyList<string>>? OnShowDialogRequested;
    public event Action<Character, string>? OnShowTargetRequested;
    public event Action<Character, string>? OnEditTargetRequested;
    public event Action<Character, uint>? OnInspectRequested;
    /// <summary>Fired by <c>.info</c> with no argument. Program.cs wires
    /// this to a target-cursor flow on the calling client; the picked
    /// UID is then routed through OnInspectRequested.</summary>
    public event Action<Character>? OnInspectTargetRequested;
    public event Action<Character, int>? OnCastRequested;

    /// <summary>Raised when ".dialog &lt;name&gt; [page]" is typed. The host
    /// opens the named script dialog on the character's client.</summary>
    public event Action<Character, string, int>? OnScriptDialogRequested;

    /// <summary>Fired when a command wants to send a system message to a character.</summary>
    public event Action<Character, string>? OnSysMessage;
    public Func<Character, string, bool>? ScriptFallbackExecutor { get; set; }
    public event Action<Character, string, string>? OnScriptParityWarning;
    public TriggerDispatcher? TriggerDispatcher { get; set; }

    public bool ExecuteShowForTarget(Character gm, string args, uint targetSerial) =>
        ExecuteShowCommand(gm, args, forcedTargetSerial: targetSerial);
    public bool ExecuteEditForTarget(Character gm, string args, uint targetSerial) =>
        ExecuteEditCommand(gm, args, forcedTargetSerial: targetSerial);

    private ushort ResolveCharBodyId(CharDef? charDef, ushort fallbackBaseId)
    {
        if (charDef == null)
            return fallbackBaseId;
        if (charDef.DispIndex > 0)
            return charDef.DispIndex;

        string refName = charDef.DisplayIdRef?.Trim() ?? "";
        if (refName.Length > 0 && Resources != null)
        {
            var refRid = Resources.ResolveDefName(refName);
            if (refRid.IsValid && refRid.Type == ResType.CharDef)
            {
                var refDef = DefinitionLoader.GetCharDef(refRid.Index);
                if (refDef?.DispIndex > 0)
                    return refDef.DispIndex;
                return (ushort)refRid.Index;
            }
        }

        return fallbackBaseId;
    }

    public void Register(string verb, PrivLevel minLevel, CommandFunc handler)
    {
        _commands[verb] = handler;
        _commandsEx.Remove(verb);
        _privLevels[verb] = minLevel;
    }

    public void RegisterEx(string verb, PrivLevel minLevel, CommandFuncEx handler)
    {
        _commandsEx[verb] = handler;
        _commands.Remove(verb);
        _privLevels[verb] = minLevel;
    }

    /// <summary>
    /// Execute a command. Returns true if handled.
    /// Maps to CChar::r_Verb dispatch chain.
    /// </summary>
    public bool Execute(Character gm, string commandLine) =>
        TryExecute(gm, commandLine) == CommandResult.Executed;

    public CommandResult TryExecute(Character gm, string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return CommandResult.NotFound;

        // Source-X parity: handle "verb=value" syntax (e.g. ".events=e_human_player")
        // Convert to property assignment on the character.
        int eqIdx = commandLine.IndexOf('=');
        if (eqIdx > 0)
        {
            string propKey = commandLine[..eqIdx].Trim();
            string propVal = commandLine[(eqIdx + 1)..].Trim();
            if (propKey.Length > 0 && gm.TrySetProperty(propKey, propVal))
                return CommandResult.Executed;
        }

        int spaceIdx = commandLine.IndexOf(' ');
        string verb = spaceIdx > 0 ? commandLine[..spaceIdx] : commandLine;
        string args = spaceIdx > 0 ? commandLine[(spaceIdx + 1)..].Trim() : "";
        _commandsEx.TryGetValue(verb, out var handlerEx);

        if (!_commands.TryGetValue(verb, out var handler) && handlerEx == null)
        {
            // Source-X parity: load command privileges from [PLEVEL X] script sections.
            if (_scriptCommandPrivLevels.TryGetValue(verb, out var scriptMinLevel))
            {
                if (gm.PrivLevel < scriptMinLevel)
                    return CommandResult.InsufficientPriv;
                if (ScriptFallbackExecutor?.Invoke(gm, commandLine) == true)
                    return CommandResult.Executed;
                return CommandResult.NotFound;
            }

            // Compatibility fallback: function exists but isn't listed under [PLEVEL].
            // Treat as default PLEVEL 7 (Owner) unless explicitly defined.
            if (gm.PrivLevel < DefaultScriptCommandPrivLevel)
                return CommandResult.InsufficientPriv;
            if (ScriptFallbackExecutor?.Invoke(gm, commandLine) == true)
                return CommandResult.Executed;

            // Source-X parity: try as object command/property on the character.
            // This allows ".events +e_human_player", ".name NewName", etc.
            if (gm.TryExecuteCommand(verb, args, NullConsole.Instance))
                return CommandResult.Executed;
            if (args.Length > 0 && gm.TrySetProperty(verb, args))
                return CommandResult.Executed;

            OnScriptParityWarning?.Invoke(gm, verb, "Command not found in built-in map or [PLEVEL] script matrix.");
            return CommandResult.NotFound;
        }

        if (_privLevels.TryGetValue(verb, out var minLevel) && gm.PrivLevel < minLevel)
            return CommandResult.InsufficientPriv;

        // Script-first parity: if a function with this verb exists, execute it first.
        // For a few core utility verbs we prefer built-ins to avoid recursive target
        // cursor loops from custom scripts.
        if (!_preferBuiltinVerbs.Contains(verb))
        {
            if (ScriptFallbackExecutor?.Invoke(gm, commandLine) == true)
                return CommandResult.Executed;
        }

        if (handlerEx != null)
            return handlerEx(gm, args) ? CommandResult.Executed : CommandResult.Failed;

        handler!(gm, args);
        return CommandResult.Executed;
    }

    /// <summary>Return minimum privilege required for the given command line verb.</summary>
    public PrivLevel? GetRequiredPrivLevel(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;
        int spaceIdx = commandLine.IndexOf(' ');
        string verb = spaceIdx > 0 ? commandLine[..spaceIdx] : commandLine;
        if (_privLevels.TryGetValue(verb, out var level))
            return level;
        if (_scriptCommandPrivLevels.TryGetValue(verb, out var scriptLevel))
            return scriptLevel;
        // Script function exists but no explicit [PLEVEL] mapping -> default to 7.
        if (ScriptFallbackExecutor != null)
            return DefaultScriptCommandPrivLevel;
        return null;
    }

    /// <summary>Register standard Source-X GM commands.</summary>
    public void RegisterDefaults(GameWorld world)
    {
        _registeredWorld = world;
        Register("ADD", PrivLevel.Counsel, (gm, args) =>
        {
            string token = args
                .Replace("\0", string.Empty, StringComparison.Ordinal)
                .Trim();

            if (string.IsNullOrWhiteSpace(token))
            {
                OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_add_usage"));
                return;
            }
            OnAddTargetRequested?.Invoke(gm, token);
            OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_add_select", token));
        });

        RegisterEx("GO", PrivLevel.Counsel, (gm, args) =>
        {
            // Try coordinates first: .GO 1495 1629 10 [map]  or  .GO 1495,1629,10[,map]
            string safeArgs = args.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();

            var parts = safeArgs.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                short.TryParse(parts[0], out short x) &&
                short.TryParse(parts[1], out short y))
            {
                byte targetMap = parts.Length > 3 && byte.TryParse(parts[3], out byte tm)
                    ? tm : gm.MapIndex;
                sbyte z;
                if (parts.Length > 2 && sbyte.TryParse(parts[2], out sbyte tz))
                {
                    z = tz;
                }
                else
                {
                    // Auto-resolve terrain Z when not specified
                    z = world.MapData?.GetEffectiveZ(targetMap, x, y) ?? 0;
                }
                var pos = new Point3D(x, y, z, targetMap);
                byte oldMap = gm.MapIndex;
                world.MoveCharacter(gm, pos);
                if (oldMap != pos.Map)
                    OnCharacterMapChanged?.Invoke(gm);
                OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_teleported", pos));
                return true;
            }

            // Named location from AREADEF scripts — prefer caller's current map
            // when multiple definitions share a name (e.g. Britain on map0 and map1).
            var namedPos = ResolveAreaDef(safeArgs, gm.MapIndex);
            if (namedPos != null)
            {
                byte oldMap = gm.MapIndex;
                world.MoveCharacter(gm, namedPos.Value);
                if (oldMap != namedPos.Value.Map)
                    OnCharacterMapChanged?.Invoke(gm);
                OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_teleported_named", safeArgs, namedPos.Value));
                return true;
            }
            OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_go_usage", safeArgs));
            return false;
        });

        Register("KILL", PrivLevel.GM, (gm, args) =>
        {
            if (uint.TryParse(args.Replace("0", ""), System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            {
                var target = world.FindChar(new Core.Types.Serial(uid));
                target?.Kill();
            }
        });

        // .RESURRECT [uid]
        //   * no arg: resurrect self (works whether the caller is dead or
        //     not — Source-X DV_RESURRECT is callable on living chars too,
        //     it just no-ops on the IsDead check inside Resurrect())
        //   * hex uid arg: resurrect that specific character
        // .XRESURRECT
        //   * pops a target cursor on the GM client; whoever is targeted
        //     gets resurrected
        Register("RESURRECT", PrivLevel.GM, (gm, args) =>
        {
            string raw = args.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                OnResurrectRequested?.Invoke(gm, null);
                return;
            }
            string uidText = raw.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (uint.TryParse(uidText, System.Globalization.NumberStyles.HexNumber, null, out uint uid)
                || uint.TryParse(raw, out uid))
            {
                OnResurrectRequested?.Invoke(gm, new Core.Types.Serial(uid));
            }
            else
            {
                OnSysMessage?.Invoke(gm, "Usage: .resurrect [hex_uid]");
            }
        });

        Register("XRESURRECT", PrivLevel.GM, (gm, _) =>
        {
            OnResurrectTargetRequested?.Invoke(gm);
            OnSysMessage?.Invoke(gm, "Select a character to resurrect.");
        });

        Register("REMOVE", PrivLevel.GM, (gm, args) =>
        {
            string raw = args.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                OnRemoveTargetRequested?.Invoke(gm);
                OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_remove_select"));
                return;
            }

            string uidText = raw.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim();
            bool parsed = uint.TryParse(uidText, System.Globalization.NumberStyles.HexNumber, null, out uint uid)
                          || uint.TryParse(raw, out uid);
            if (!parsed)
            {
                OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_remove_usage"));
                return;
            }

            var item = world.FindItem(new Core.Types.Serial(uid));
            if (item != null)
            {
                world.DeleteObject(item);
                item.Delete();
                return;
            }

            var ch = world.FindChar(new Core.Types.Serial(uid));
            if (ch != null)
            {
                if (ch == gm)
                {
                    OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_cant_remove_self"));
                    return;
                }
                world.DeleteObject(ch);
                ch.Delete();
                return;
            }

            OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_object_not_found", $"{uid:X8}"));
        });

        Register("INVIS", PrivLevel.Counsel, (gm, args) =>
        {
            // Source-X semantics: .INVIS 1 → set invisible, .INVIS 0 → clear,
            // no argument → toggle. Sphere treats any non-"0" argument as "on".
            string a = args.Trim();
            bool makeInvis = string.IsNullOrEmpty(a)
                ? !gm.IsInvisible                // toggle
                : a != "0";                      // explicit on/off
            if (makeInvis)
            {
                gm.SetStatFlag(StatFlag.Invisible);
                OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_now_invisible"));
            }
            else
            {
                gm.ClearStatFlag(StatFlag.Invisible);
                OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_now_visible"));
            }
            OnCharacterSelfRedraw?.Invoke(gm);
        });

        Register("ALLMOVE", PrivLevel.Counsel, (gm, args) =>
        {
            string a = args.Trim();
            bool enable = string.IsNullOrEmpty(a) ? !gm.AllMove : a != "0";
            gm.AllMove = enable;
            OnSysMessage?.Invoke(gm,
                ServerMessages.Get(enable ? "gm_allmove_on" : "gm_allmove_off"));
        });

        Register("STRESS", PrivLevel.Owner, (gm, args) =>
        {
            // .STRESS               → default 500000 items + 400000 NPCs
            // .STRESS 100000 25000  → custom counts
            int items = 500_000, npcs = 400_000;
            var toks = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length >= 1 && !int.TryParse(toks[0], out items))
            {
                OnSysMessage?.Invoke(gm, "Usage: .STRESS [items] [npcs]");
                return;
            }
            if (toks.Length >= 2 && !int.TryParse(toks[1], out npcs))
            {
                OnSysMessage?.Invoke(gm, "Usage: .STRESS [items] [npcs]");
                return;
            }
            OnSysMessage?.Invoke(gm,
                $"Queuing {items:N0} items and {npcs:N0} NPCs across town centers. Watch server log for progress.");
            OnStressGenerateRequested?.Invoke(items, npcs);
        });

        Register("STRESSREPORT", PrivLevel.Counsel, (gm, _) =>
        {
            OnSysMessage?.Invoke(gm, "Stress report dumped to server log.");
            OnStressReportRequested?.Invoke();
        });

        Register("STRESSCLEAN", PrivLevel.Owner, (gm, _) =>
        {
            OnSysMessage?.Invoke(gm, "Stress cleanup queued. Watch server log for progress.");
            OnStressCleanupRequested?.Invoke();
        });

        Register("SCRIPTDEBUG", PrivLevel.Owner, (gm, args) =>
        {
            // Toggle script diagnostic logging on/off. When on, unresolved
            // <X> expressions get reported to the server console — use it
            // while hunting missing properties in imported Sphere scripts.
            bool turnOn = args.Equals("on", StringComparison.OrdinalIgnoreCase)
                || args == "1"
                || string.IsNullOrEmpty(args); // bare .SCRIPTDEBUG toggles on
            if (args.Equals("off", StringComparison.OrdinalIgnoreCase) || args == "0")
                turnOn = false;

            OnScriptDebugToggleRequested?.Invoke(turnOn);
            OnSysMessage?.Invoke(gm, turnOn
                ? "Script diagnostic logging: ON. Unresolved <X> expressions will be reported."
                : "Script diagnostic logging: OFF.");
        });

        Register("SAVEFORMAT", PrivLevel.Owner, (gm, args) =>
        {
            // .SAVEFORMAT              → print current format + available values
            // .SAVEFORMAT Text          → switch format, keep shard count
            // .SAVEFORMAT BinaryGz 8    → switch format + shard count, then save
            if (string.IsNullOrWhiteSpace(args))
            {
                OnSysMessage?.Invoke(gm,
                    "Usage: .SAVEFORMAT <Text|TextGz|Binary|BinaryGz> [shards]. Forces a save in the new format.");
                return;
            }
            var toks = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string fmt = toks[0];
            int shards = -1;
            if (toks.Length >= 2 && int.TryParse(toks[1], out int s))
                shards = Math.Clamp(s, 1, 16);
            OnSysMessage?.Invoke(gm, $"Switching save format to {fmt}" +
                (shards > 0 ? $" (shards={shards})" : "") + " and forcing a save...");
            OnSaveFormatChangeRequested?.Invoke(fmt, shards);
        });

        Register("INVUL", PrivLevel.GM, (gm, _) =>
        {
            if (gm.IsStatFlag(StatFlag.Invul))
                gm.ClearStatFlag(StatFlag.Invul);
            else
                gm.SetStatFlag(StatFlag.Invul);
        });

        Register("SET", PrivLevel.GM, (gm, args) =>
        {
            int eq = args.IndexOf(' ');
            if (eq > 0)
            {
                string key = args[..eq].Trim();
                string val = args[(eq + 1)..].Trim();
                gm.TrySetProperty(key, val);
            }
        });

        Register("INFO", PrivLevel.Counsel, (gm, args) =>
        {
            // .info <uid>   → open the inspect dialog directly.
            // .info         → drop a target cursor; the inspect dialog
            //                 opens on whatever character/item the GM
            //                 clicks. Matches Source-X .info UX.
            if (!string.IsNullOrWhiteSpace(args) &&
                uint.TryParse(args.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out uint targetUid))
            {
                OnInspectRequested?.Invoke(gm, targetUid);
                return;
            }
            OnInspectTargetRequested?.Invoke(gm);
        });

        Register("SHOW", PrivLevel.Counsel, (gm, args) =>
        {
            ExecuteShowCommand(gm, args, forcedTargetSerial: null);
        });

        Register("XSHOW", PrivLevel.Counsel, (gm, args) =>
        {
            string text = string.IsNullOrWhiteSpace(args) ? "EVENTS" : args.Trim();
            OnShowTargetRequested?.Invoke(gm, text);
            OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_show_select", text));
        });

        Register("SAVE", PrivLevel.Admin, (_, _) =>
        {
            OnSaveCommand?.Invoke();
        });

        Register("SHUTDOWN", PrivLevel.Admin, (_, _) =>
        {
            OnShutdownCommand?.Invoke();
        });

        Register("ACCOUNT", PrivLevel.Admin, (gm, args) =>
        {
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_account_mgmt"));
        });

        Register("BROADCAST", PrivLevel.GM, (gm, args) =>
        {
            OnBroadcastCommand?.Invoke(args);
        });

        Register("PAGE", PrivLevel.Player, (gm, args) =>
        {
            OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_page_submitted", args));
            // TODO: Route to online GMs via OnPageCommand event
        });

        Register("RESYNC", PrivLevel.Admin, (gm, _) =>
        {
            OnResyncCommand?.Invoke();
        });

        Register("RY", PrivLevel.Admin, (gm, _) =>
        {
            OnResyncCommand?.Invoke();
        });

        // Additional GM commands
        Register("ADDSKILL", PrivLevel.GM, (gm, args) =>
        {
            // .ADDSKILL <skillId> <value>
            var parts = args.Split(' ', 2);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int skillId) && int.TryParse(parts[1], out int value))
            {
                gm.SetSkill((SkillType)skillId, (ushort)Math.Clamp(value, 0, 1200));
                OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_skill_set", (SkillType)skillId, $"{value / 10.0:F1}"));
            }
        });

        Register("FREEZE", PrivLevel.GM, (gm, args) =>
        {
            if (uint.TryParse(args.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            {
                var target = world.FindChar(new Core.Types.Serial(uid));
                if (target != null)
                {
                    target.SetStatFlag(StatFlag.Freeze);
                    OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_frozen", target.Name));
                }
            }
            else
            {
                OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_freeze_usage"));
            }
        });

        Register("UNFREEZE", PrivLevel.GM, (gm, args) =>
        {
            if (uint.TryParse(args.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            {
                var target = world.FindChar(new Core.Types.Serial(uid));
                if (target != null)
                {
                    target.ClearStatFlag(StatFlag.Freeze);
                    OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_unfrozen", target.Name));
                }
            }
        });

        // JAIL <serial> [minutes] — jail a character, optionally with a duration in minutes
        Register("JAIL", PrivLevel.GM, (gm, args) =>
        {
            var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            if (uint.TryParse(parts[0].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            {
                var target = world.FindChar(new Core.Types.Serial(uid));
                if (target != null)
                {
                    var jailPos = new Point3D(1476, 1604, 20, 0);
                    world.MoveCharacter(target, jailPos);
                    target.SetStatFlag(StatFlag.Freeze);

                    // Jail duration (minutes)
                    if (parts.Length > 1 && int.TryParse(parts[1], out int minutes) && minutes > 0)
                    {
                        long releaseTime = Environment.TickCount64 + minutes * 60_000L;
                        target.SetTag("JAIL_RELEASE", releaseTime.ToString());
                        OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_jailed_timed", target.Name, minutes));
                    }
                    else
                    {
                        target.SetTag("JAIL_RELEASE", "0"); // indefinite
                        OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_jailed_indef", target.Name));
                    }

                    OnCharacterResyncRequested?.Invoke(target);
                }
            }
        });

        Register("UNJAIL", PrivLevel.GM, (gm, args) =>
        {
            if (uint.TryParse(args.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            {
                var target = world.FindChar(new Core.Types.Serial(uid));
                if (target != null)
                {
                    target.ClearStatFlag(StatFlag.Freeze);
                    target.RemoveTag("JAIL_RELEASE");
                    var spawnPos = new Point3D(1495, 1629, 10, 0);
                    world.MoveCharacter(target, spawnPos);
                    OnCharacterResyncRequested?.Invoke(target);
                    OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_released", target.Name));
                }
            }
        });

        Register("UNSTICK", PrivLevel.Counsel, (gm, _) =>
        {
            // Teleport GM to default Britain bank location
            var safePos = new Point3D(1495, 1629, 10, 0);
            world.MoveCharacter(gm, safePos);
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_safe_teleport"));
        });

        Register("ADDNPC", PrivLevel.GM, (gm, args) =>
        {
            if (string.IsNullOrEmpty(args)) { OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_addnpc_usage")); return; }
            if (ushort.TryParse(args.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out ushort bodyId))
            {
                var npc = world.CreateCharacter();
                npc.BodyId = bodyId;
                npc.Name = $"NPC_{bodyId:X}";
                npc.IsPlayer = false;
                npc.Str = 50; npc.Dex = 50; npc.Int = 50;
                npc.MaxHits = 50; npc.Hits = 50;
                npc.MaxMana = 50; npc.Mana = 50;
                npc.MaxStam = 50; npc.Stam = 50;
                world.PlaceCharacter(npc, gm.Position);
                OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_npc_created", npc.Name, gm.Position));
            }
        });

        Register("SETPRIV", PrivLevel.Admin, (gm, args) =>
        {
            var parts = args.Split(' ', 2);
            if (parts.Length >= 2 &&
                uint.TryParse(parts[0].Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out uint uid) &&
                Enum.TryParse<PrivLevel>(parts[1], true, out var level))
            {
                var target = world.FindChar(new Core.Types.Serial(uid));
                if (target != null)
                {
                    target.PrivLevel = level;
                    OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_priv_set", target.Name, level));
                }
            }
        });

        Register("ALLSHOW", PrivLevel.Counsel, (gm, args) =>
        {
            if (args == "0" || args.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                gm.AllShow = false;
                OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_allshow_off"));
            }
            else
            {
                // Toggle if no args, or set on with "1"/"on"
                if (string.IsNullOrEmpty(args))
                    gm.AllShow = !gm.AllShow;
                else
                    gm.AllShow = true;

                OnSysMessage?.Invoke(gm, gm.AllShow
                    ? ServerMessages.Get("gm_allshow_on")
                    : ServerMessages.Get("gm_allshow_off"));
            }
        });

        Register("TELE", PrivLevel.Counsel, (gm, _) =>
        {
            // Source-X behavior: request a target cursor and teleport to selected
            // object or ground location once target response arrives.
            OnTeleportTargetRequested?.Invoke(gm);
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_tele_select"));
        });

        Register("EDIT", PrivLevel.Counsel, (gm, args) =>
        {
            ExecuteEditCommand(gm, args, forcedTargetSerial: null);
        });

        Register("XEDIT", PrivLevel.Counsel, (gm, args) =>
        {
            string text = args.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                ExecuteEditCommand(gm, text, forcedTargetSerial: null);
                return;
            }
            OnEditTargetRequested?.Invoke(gm, "EVENTS");
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_inspect_select"));
        });

        Register("UPDATE", PrivLevel.Counsel, (gm, _) =>
        {
            OnResyncCommand?.Invoke();
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_resync"));
        });

        Register("FIX", PrivLevel.Counsel, (gm, _) =>
        {
            // Re-seat to current tile and force visual refresh on client side.
            world.MoveCharacter(gm, gm.Position);
            OnResyncCommand?.Invoke();
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_pos_fixed"));
        });

        Register("GM", PrivLevel.Counsel, (gm, _) =>
        {
            if (gm.PrivLevel >= PrivLevel.GM)
            {
                if (gm.IsInvisible) gm.ClearStatFlag(StatFlag.Invisible);
                else gm.SetStatFlag(StatFlag.Invisible);
                OnSysMessage?.Invoke(gm, gm.IsInvisible ? ServerMessages.Get("gm_mode_on") : ServerMessages.Get("gm_mode_off"));
            }
            else
            {
                OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_privlevel", gm.PrivLevel, (int)gm.PrivLevel));
            }
        });

        Register("WHERE", PrivLevel.Player, (ch, _) =>
        {
            var md = world.MapData;
            sbyte terrainZ = md?.GetTerrainTile(ch.MapIndex, ch.X, ch.Y).Z ?? 0;
            sbyte effectiveZ = md?.GetEffectiveZ(ch.MapIndex, ch.X, ch.Y, ch.Z) ?? 0;
            OnSysMessage?.Invoke(ch, ServerMessages.GetFormatted("gm_position", ch.X, ch.Y, ch.Z, ch.Position.Map, terrainZ, effectiveZ));
        });

        Register("STATICS", PrivLevel.Counsel, (ch, _) =>
        {
            // Diagnostic: list static tiles at the caller's position and whether
            // each one blocks walking. If this prints "0 statics" on a tile that
            // is obviously a wall/building, statics*.mul is not loaded or
            // StaticReader is buggy.
            var md = world.MapData;
            if (md == null)
            {
                OnSysMessage?.Invoke(ch, "MapData not loaded.");
                return;
            }
            var statics = md.GetStatics(ch.MapIndex, ch.X, ch.Y);
            bool passable = md.IsPassable(ch.MapIndex, ch.X, ch.Y, ch.Z);
            OnSysMessage?.Invoke(ch, $"Tile {ch.X},{ch.Y},{ch.Z} map={ch.MapIndex}: {statics.Length} statics, passable={passable}");
            foreach (var s in statics)
            {
                var td = md.GetItemTileData(s.TileId);
                OnSysMessage?.Invoke(ch,
                    $"  tile=0x{s.TileId:X4} z={s.Z} h={td.CalcHeight} impassable={td.IsImpassable} surface={td.IsSurface} wall={td.IsWall}");
            }
        });

        Register("DIALOG", PrivLevel.GM, (gm, args) =>
        {
            string raw = args.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                OnSysMessage?.Invoke(gm, "Usage: .dialog <name> [page]");
                return;
            }
            var parts = raw.Split([' ', ','], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string dialogName = parts[0];
            int page = 1;
            if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int p) && p > 0)
                page = p;
            OnScriptDialogRequested?.Invoke(gm, dialogName, page);
        });

        Register("CAST", PrivLevel.GM, (ch, args) =>
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                OnSysMessage?.Invoke(ch, ServerMessages.Get("gm_cast_usage"));
                return;
            }

            int spellId = -1;
            string arg = args.Trim();

            // Try numeric first
            if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("0X"))
            {
                int.TryParse(arg.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out spellId);
            }
            else if (int.TryParse(arg, out int n))
            {
                spellId = n;
            }
            else if (Enum.TryParse<SpellType>(arg, true, out var st))
            {
                spellId = (int)st;
            }

            if (spellId < 0)
            {
                OnSysMessage?.Invoke(ch, ServerMessages.GetFormatted("gm_unknown_spell", arg));
                return;
            }

            OnCastRequested?.Invoke(ch, spellId);
        });
    }

    private bool ExecuteShowCommand(Character gm, string args, uint? forcedTargetSerial)
    {
        string text = args.Trim();
        if (string.IsNullOrEmpty(text))
        {
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_show_events_usage"));
            return false;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!parts[0].Equals("EVENTS", StringComparison.OrdinalIgnoreCase))
        {
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_show_events_usage"));
            return false;
        }

        IScriptObj target = gm;
        uint targetUid = gm.Uid.Value;

        if (forcedTargetSerial.HasValue)
        {
            if (_registeredWorld == null)
                return false;
            targetUid = forcedTargetSerial.Value;
            target = _registeredWorld.FindObject(new Serial(targetUid)) ?? target;
            if (target == gm && targetUid != gm.Uid.Value)
            {
                OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_object_not_found", $"{targetUid:X8}"));
                return false;
            }
        }
        else if (parts.Length >= 2)
        {
            if (_registeredWorld == null)
                return false;
            string uidText = parts[1].Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            bool parsed = uint.TryParse(uidText, System.Globalization.NumberStyles.HexNumber, null, out targetUid)
                            || uint.TryParse(parts[1], out targetUid);
            if (!parsed)
            {
                OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_invalid_serial", parts[1]));
                return false;
            }

            target = _registeredWorld.FindObject(new Serial(targetUid)) ?? target;
            if (target == gm && targetUid != gm.Uid.Value)
            {
                OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_object_not_found", $"{targetUid:X8}"));
                return false;
            }
        }

        List<ResourceId>? evList = target switch
        {
            Character ch => ch.Events,
            SphereNet.Game.Objects.Items.Item it => it.Events,
            _ => null
        };

        List<ResourceId>? tevList = null;
        if (target is Character tch)
        {
            var charDef = DefinitionLoader.GetCharDef(tch.BaseId);
            if (charDef?.Events.Count > 0) tevList = charDef.Events;
        }
        else if (target is SphereNet.Game.Objects.Items.Item tit)
        {
            var itemDef = DefinitionLoader.GetItemDef(tit.BaseId);
            if (itemDef?.Events.Count > 0) tevList = itemDef.Events;
        }

        if (evList == null)
        {
            OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_no_events", $"{targetUid:X8}"));
            return false;
        }

        string ResolveName(ResourceId rid)
        {
            string? byLink = Resources?.GetResource(rid)?.DefName;
            if (!string.IsNullOrWhiteSpace(byLink))
                return byLink!;

            if (Resources != null)
            {
                foreach (var link in Resources.GetAllResources())
                {
                    if (link.Id == rid && !string.IsNullOrWhiteSpace(link.DefName))
                        return link.DefName!;
                }
            }

            return rid.ToString();
        }

        string targetName = target.GetName();
        string eventsLine = evList.Count > 0
            ? string.Join(", ", evList.Select(ResolveName))
            : "(empty)";
        string teventsLine = tevList != null && tevList.Count > 0
            ? string.Join(", ", tevList.Select(ResolveName))
            : "(empty)";

        var dialogLines = new List<string>
        {
            $"Target: 0x{targetUid:X8}  Name: {targetName}",
            $"EVENTS: {eventsLine}",
            $"TEVENTS: {teventsLine}"
        };

        if (OnShowDialogRequested != null)
        {
            OnShowDialogRequested(gm, $"SHOW EVENTS 0x{targetUid:X8}", dialogLines);
            return true;
        }

        OnSysMessage?.Invoke(gm, dialogLines[0]);
        OnSysMessage?.Invoke(gm, dialogLines[1]);
        OnSysMessage?.Invoke(gm, dialogLines[2]);
        return true;
    }

    private bool ExecuteEditCommand(Character gm, string args, uint? forcedTargetSerial)
    {
        if (forcedTargetSerial.HasValue)
        {
            OnInspectRequested?.Invoke(gm, forcedTargetSerial.Value);
            return true;
        }

        string text = args.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            OnEditTargetRequested?.Invoke(gm, "EVENTS");
            OnSysMessage?.Invoke(gm, ServerMessages.Get("gm_inspect_select"));
            return true;
        }

        string token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        string uidText = token.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        bool parsed = uint.TryParse(uidText, System.Globalization.NumberStyles.HexNumber, null, out uint targetUid)
                      || uint.TryParse(token, out targetUid);
        if (!parsed)
        {
            OnSysMessage?.Invoke(gm, ServerMessages.GetFormatted("gm_invalid_serial", token));
            return false;
        }

        OnInspectRequested?.Invoke(gm, targetUid);
        return true;
    }

    /// <summary>
    /// Resolve a named location from AREADEF resources. When multiple AREADEFs share
    /// the same NAME= (common when map0/map1/... each define their own "Britain"),
    /// the caller's current map is preferred. Falls back to the first-seen definition
    /// if no same-map match exists, so names unique to a single map still resolve.
    /// </summary>
    private Point3D? ResolveAreaDef(string name, byte preferredMap)
    {
        if (string.IsNullOrWhiteSpace(name) || Resources == null)
            return null;

        // Build cache on first use
        if (_areaCache == null)
            BuildAreaCache();

        string key = name.Trim();
        if (_areaCacheByMap!.TryGetValue((key, preferredMap), out var matchPos))
            return matchPos;
        return _areaCache!.TryGetValue(key, out var pos) ? pos : null;
    }

    private Dictionary<string, Point3D>? _areaCache;
    private Dictionary<(string Name, byte Map), Point3D>? _areaCacheByMap;

    private void BuildAreaCache()
    {
        _areaCache = new Dictionary<string, Point3D>(StringComparer.OrdinalIgnoreCase);
        _areaCacheByMap = new Dictionary<(string, byte), Point3D>(
            new AreaMapKeyComparer());
        if (Resources == null) return;

        foreach (var link in Resources.GetAllResources())
        {
            if (link.Id.Type != ResType.Area) continue;

            using var sf = link.OpenAtStoredPosition();
            if (sf == null) continue;

            var sections = sf.ReadAllSections();
            if (sections.Count == 0)
                continue;

            // ResourceLink is positioned at a specific section line. Read only that section.
            var section = sections[0];
            if (!section.Name.Equals("AREADEF", StringComparison.OrdinalIgnoreCase) &&
                !section.Name.Equals("AREA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? areaName = null;
            string? pValue = null;

            foreach (var key in section.Keys)
            {
                if (key.Key.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                    areaName = key.Arg?.Trim().Trim('"');
                else if (key.Key.Equals("P", StringComparison.OrdinalIgnoreCase) && pValue == null)
                    pValue = key.Arg;
            }

            if (areaName != null && pValue != null)
            {
                var parts = pValue.Split(',');
                if (parts.Length >= 3 &&
                    short.TryParse(parts[0], out short x) &&
                    short.TryParse(parts[1], out short y) &&
                    sbyte.TryParse(parts[2], out sbyte z))
                {
                    byte map = parts.Length > 3 && byte.TryParse(parts[3], out byte m) ? m : (byte)0;
                    var pos = new Point3D(x, y, z, map);
                    AddAreaAlias(areaName, pos);
                }
            }
        }
    }

    private void AddAreaAlias(string alias, Point3D pos)
    {
        if (string.IsNullOrWhiteSpace(alias) || _areaCache == null || _areaCacheByMap == null)
            return;

        string key = alias.Trim();
        _areaCache.TryAdd(key, pos);
        _areaCacheByMap.TryAdd((key, pos.Map), pos);
    }

    /// <summary>Invalidate the area cache (called after RESYNC).</summary>
    public void InvalidateAreaCache()
    {
        _areaCache = null;
        _areaCacheByMap = null;
    }

    private sealed class AreaMapKeyComparer : IEqualityComparer<(string Name, byte Map)>
    {
        public bool Equals((string Name, byte Map) x, (string Name, byte Map) y)
            => x.Map == y.Map &&
               string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, byte Map) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name),
                obj.Map);
    }

    /// <summary>
    /// Load script command privilege mapping from [PLEVEL X] sections.
    /// Each non-empty key line in a PLEVEL section is treated as a command verb.
    /// </summary>
    public int LoadScriptCommandPrivileges(ResourceHolder resources)
    {
        _scriptCommandPrivLevels.Clear();
        int loaded = 0;

        foreach (var link in resources.GetAllResources())
        {
            if (link.Id.Type != ResType.PlevelCfg)
                continue;

            using var sf = link.OpenAtStoredPosition();
            if (sf == null)
                continue;

            var sections = sf.ReadAllSections();
            if (sections.Count == 0)
                continue;

            var section = sections[0];
            if (!TryParsePrivLevel(section.Argument, out var level))
                continue;

            foreach (var key in section.Keys)
            {
                string cmd = key.Key.Trim();
                if (string.IsNullOrEmpty(cmd))
                    continue;
                if (cmd.StartsWith("//", StringComparison.Ordinal))
                    continue;

                _scriptCommandPrivLevels[cmd] = level;
                loaded++;
            }
        }

        return loaded;
    }

    private static bool TryParsePrivLevel(string raw, out PrivLevel level)
    {
        level = PrivLevel.Counsel;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string token = raw.Trim().Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!int.TryParse(token, out int n))
            return false;

        n = Math.Clamp(n, (int)PrivLevel.Guest, (int)PrivLevel.Owner);
        level = (PrivLevel)n;
        return true;
    }
}
