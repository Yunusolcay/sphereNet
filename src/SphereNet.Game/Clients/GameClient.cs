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
using ScriptSystemHooks = SphereNet.Scripting.Execution.ScriptSystemHooks;

namespace SphereNet.Game.Clients;

/// <summary>Parsed ON-block from a [MENU] section: item visuals + script lines to execute.</summary>
internal record MenuOptionEntry(ushort ModelId, ushort Hue, string Text, List<SphereNet.Scripting.Parsing.ScriptKey> Script);

/// <summary>
/// Game logic per-client handler. Maps to CClient in Source-X.
/// Bridges NetState (network) with Character (game object) and Account.
/// Integrates all game engines: movement, combat, speech, magic, trade, inventory.
/// Manages the client update loop (sending nearby objects, removing out-of-range).
/// </summary>
public sealed partial class GameClient : ITextConsole
{
    /// <summary>OR of all config FEATURE* flags (FEATURET2A|LBR|AOS|SE|ML|KR|SA|TOL|EXTRA).
    /// Set by Program.cs startup. If zero, HandleGameLogin falls back to a
    /// hardcoded mapping derived from client version.</summary>
    public static uint ServerFeatureFlags { get; set; }
    public static Func<string, Point3D?>? BotSpawnLocationProvider;

    private readonly NetState _netState;
    private readonly GameWorld _world;
    private readonly AccountManager _accountManager;
    private readonly ILogger _logger;

    private MovementEngine? _movement;
    private SpeechEngine? _speech;
    private CommandHandler? _commands;
    private SpellEngine? _spellEngine;
    private DeathEngine? _deathEngine;
    private PartyManager? _partyManager;
    private TradeManager? _tradeManager;
    private SkillHandlers? _skillHandlers;
    private CraftingEngine? _craftingEngine;
    private HousingEngine? _housingEngine;
    private GuildManager? _guildManager;
    private Mounts.MountEngine? _mountEngine;
    private TriggerDispatcher? _triggerDispatcher;
    private ScriptSystemHooks? _systemHooks;
    public static Action<Character>? OnWakeNpc;
    private ScriptDbAdapter? _scriptDb;
    private ScriptDbAdapter? _scriptLdb;
    private ScriptFileHandle? _scriptFile;
    private Func<string, string?>? _defMessageLookup;

    /// <summary>Callback to broadcast a packet to all clients whose character is near a point.</summary>
    public Action<Point3D, int, PacketWriter, uint>? BroadcastNearby { get; set; }
    /// <summary>Broadcast movement and update nearby clients' _lastKnownPos to prevent duplicate 0x77.</summary>
    public Action<Point3D, int, PacketWriter, uint, Character>? BroadcastMoveNearby { get; set; }
    /// <summary>
    /// Per-observer dispatch helper used by the death/resurrect pipeline
    /// where the packet sent depends on whether the observer is plain
    /// player vs Counsel+/AllShow staff. Action receives (observerChar,
    /// observerClient). Wired from Program.cs.ForEachClientInRange.
    /// </summary>
    public Action<Point3D, int, uint, Action<Character, GameClient>>? ForEachClientInRange { get; set; }
    /// <summary>Send a packet to a specific character (by UID). Wired from Program.cs.</summary>
    public Action<Serial, PacketWriter>? SendToChar { get; set; }
    /// <summary>Notify all nearby clients that a character appeared (login/teleport). Each client renders from its own perspective.</summary>
    public Action<Character>? BroadcastCharacterAppear { get; set; }

    /// <summary>When true, the next tick will run BuildViewDelta+ApplyViewDelta.
    /// Set by player movement or nearby object changes. Items-only char scan
    /// (character enter/leave/move handled by events).</summary>
    public bool ViewNeedsRefresh { get; set; }


    /// <summary>Fired when this client's character goes online (post-login
    /// complete, character placed). Program.cs uses it to populate the
    /// char-UID → client map that BroadcastNearby walks instead of a
    /// full _clients.Values scan. Cleared on OnDisconnect.</summary>
    public static Action<Character, GameClient>? OnCharacterOnline;
    public static Action<Character>? OnCharacterOffline;

    /// <summary>Wired by Program.cs. Used when *this* client kills another
    /// player and we need to invoke <see cref="OnCharacterDeath"/> on the
    /// victim's own client (so the dying player sees the death screen,
    /// ghost body and 0x2C death status). Not a static event because the
    /// callback needs access to the per-client clientsByCharUid map that
    /// only Program.cs owns.</summary>
    public Action<Character>? OnCharacterDeathOfOther { get; set; }

    /// <summary>Wired by Program.cs. Resolves a victim character's own
    /// GameClient and calls <see cref="OnResurrect"/> on it. Used by the
    /// .xresurrect target picker so a GM can right-click any dead body
    /// to revive its owner.</summary>
    public Action<Character>? OnResurrectOther { get; set; }

    /// <summary>Wired by Program.cs. GM .kill target cursor callback —
    /// args are (killer, victim).</summary>
    public Action<Character, Character>? OnKillTarget { get; set; }

    private Account? _account;
    private Character? _character;

    private readonly HashSet<uint> _knownChars = [];
    private readonly HashSet<uint> _knownItems = [];
    private readonly Dictionary<uint, Action<uint, uint[], (ushort, string)[]>> _gumpCallbacks = [];
    private readonly Dictionary<uint, (short X, short Y, sbyte Z, byte Dir, ushort Body, ushort Hue)> _lastKnownPos = [];
    private readonly Dictionary<uint, uint> _tooltipHashCache = []; // serial → last sent hash
    private string? _pendingTargetFunction;
    private string _pendingTargetArgs = "";
    private bool _pendingTargetAllowGround;
    private Serial _pendingTargetItemUid = Serial.Invalid;
    private bool _pendingTeleTarget;
    private bool _pendingRemoveTarget;
    private bool _pendingResurrectTarget;
    private bool _pendingInspectTarget;
    // Source-X dialog subject (CLIMODE_DIALOG pObj). When set, bare
    // property names inside the active script dialog resolve on this
    // object instead of the GM. Used by d_charprop1 / d_itemprop1 so
    // <BODY> / <STR> etc. reflect the inspected target. Cleared after
    // render; callbacks that act on the target stash its UID locally.
    private Serial _dialogSubjectUid = Serial.Invalid;
    /// <summary>Generic script-first → native fallback registry. When a
    /// named dialog (<c>d_xxx</c>) is requested via <c>SDIALOG</c> or a
    /// help/inspect entry point, the host first tries the script
    /// <c>[DIALOG d_xxx]</c> section through <see cref="TryShowScriptDialog"/>;
    /// only when no script section is found does the registered native
    /// fallback render. New native gumps should plug in here instead of
    /// hard-coding their own render path.</summary>
    private readonly Dictionary<string, Action<int>> _nativeDialogFallbacks =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingAddToken;
    private string? _pendingShowArgs;
    private string? _pendingEditArgs;
    /// <summary>Source-X X-prefix verb fallback (CClient.cpp:921). When
    /// the GM types e.g. <c>.xhits 100</c> the unknown-verb path opens a
    /// target cursor and stores <c>(verb="HITS", args="100")</c>; on
    /// pick, <see cref="SpeechEngine.ExecuteVerbForTarget"/> applies the
    /// verb to the picked object.</summary>
    private string? _pendingXVerb;
    private string _pendingXVerbArgs = "";
    // Phase C — Source-X parity targeted GM verbs.
    /// <summary>"NUKE" / "NUKECHAR" / "NUDGE" — armed via
    /// <see cref="BeginAreaTarget"/>. The picked tile is the area
    /// centre; <see cref="_pendingAreaRange"/> is the half-extent.</summary>
    private string? _pendingAreaVerb;
    private int _pendingAreaRange;
    private bool _pendingControlTarget;
    private bool _pendingDupeTarget;
    private bool _pendingHealTarget;
    private bool _pendingKillTarget;
    private bool _pendingBankTarget;
    private bool _pendingSummonToTarget;
    private bool _pendingMountTarget;
    private bool _pendingSummonCageTarget;
    private Point3D? _lastScriptTargetPoint;
    private uint _lastCombatNotifyTarget;
    private Action<uint, short, short, sbyte, ushort>? _pendingTargetCallback;
    private Item? _pendingScriptNewItem;
    private bool _targetCursorActive;
    private string? _pendingDialogCloseFunction;
    private string _pendingDialogArgs = "";
    /// <summary>
    /// Pending Source-X <c>INPDLG</c> prompt state. Keyed by the
    /// <c>(targetSerial, context)</c> pair we encoded into the outgoing
    /// 0xAB packet; the matching 0xAC reply restores the property name
    /// to write the user-typed value into.
    /// </summary>
    private readonly Dictionary<(uint Serial, ushort Context), string> _pendingInputDlg = new();
    /// <summary>Monotonic counter for fresh INPDLG <c>context</c> ids
    /// (Source-X uses CLIMODE constants, but we just need uniqueness per
    /// open prompt).</summary>
    private ushort _nextInputDlgContext = 0x1000;
    private List<MenuOptionEntry>? _pendingMenuOptions;
    private ushort _pendingMenuId;
    private string _pendingMenuDefname = "";
    private const ushort EditMenuId = 0xFFED;
    private uint[]? _pendingEditMenuUids;
    private Item?[]? _pendingEditMenuMemories;
    private short _lastHits, _lastMana, _lastStam;
    private long _lastVitalsPacketTick;
    private const int VitalsPacketIntervalMs = 250;
    private const int UpdateRange = 18;

    public NetState NetState => _netState;
    public Account? Account => _account;
    public Character? Character => _character;
    public bool IsPlaying => _character != null && !_character.IsDeleted;
    public bool HasPendingTarget => _targetCursorActive;

    /// <summary>Called when the network connection is closed. Marks character as offline.</summary>
    public void OnDisconnect()
    {
        if (_character != null)
        {
            _logger.LogInformation("[LOGOUT] '{Name}' pos: {X},{Y},{Z} map={Map}",
                _character.Name, _character.X, _character.Y, _character.Z, _character.Position.Map);

            // Yakındaki oyunculara karakterin çıktığını bildir
            BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                new PacketDeleteObject(_character.Uid.Value), _character.Uid.Value);

            _systemHooks?.DispatchClient("disconnect", _character, _account);
            _character.IsOnline = false;
            _character.CTags.RemoveByPrefix("");
            OnCharacterOffline?.Invoke(_character);
            _world.RemoveOnlinePlayer(_character);
            _tooltipHashCache.Clear();
            _knownItems.Clear();
            _knownChars.Clear();
            _lastKnownPos.Clear();
            _paperdollThrottle.Clear();
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.LogOut,
                new TriggerArgs { CharSrc = _character });
            _logger.LogInformation("Client '{Name}' disconnected", _character.Name);
            _character = null;
        }
        else if (_account != null)
        {
            _systemHooks?.DispatchClient("disconnect", _account, null);
        }
    }

    public void Send(PacketWriter packet) => _netState.Send(packet);

    public GameClient(NetState netState, GameWorld world, AccountManager accountManager, ILogger logger)
    {
        _netState = netState;
        _world = world;
        _accountManager = accountManager;
        _logger = logger;

        RegisterNativeDialogFallbacks();
    }

    /// <summary>Wire built-in <c>d_xxx</c> native gump fallbacks. Each entry
    /// is only used when the script-side <c>[DIALOG d_xxx]</c> section is
    /// missing — see <see cref="OpenNamedDialog"/>.</summary>
    private void RegisterNativeDialogFallbacks()
    {
        _nativeDialogFallbacks["d_helppage"] = page => ShowHelpPageDialog(page <= 0 ? 1 : page);
    }

    /// <summary>Generic script-first dialog dispatcher. Tries the script
    /// <c>[DIALOG dialogId]</c> section (Source-X parity), falling back to
    /// any registered native gump. Returns true when something was
    /// rendered. <paramref name="subject"/> binds the gump's CLIMODE_DIALOG
    /// pObj for property reads (used by edit / inspect).</summary>
    public bool OpenNamedDialog(string dialogId, int requestedPage = 0, ObjBase? subject = null)
    {
        if (string.IsNullOrWhiteSpace(dialogId))
            return false;

        if (TryShowScriptDialog(dialogId, requestedPage, subject))
            return true;

        if (_nativeDialogFallbacks.TryGetValue(dialogId, out var nativeOpen))
        {
            nativeOpen(requestedPage);
            return true;
        }

        return false;
    }

    public void SetEngines(
        MovementEngine? movement = null,
        SpeechEngine? speech = null,
        CommandHandler? commands = null,
        SpellEngine? spellEngine = null,
        DeathEngine? deathEngine = null,
        PartyManager? partyManager = null,
        TradeManager? tradeManager = null,
        SkillHandlers? skillHandlers = null,
        CraftingEngine? craftingEngine = null,
        HousingEngine? housingEngine = null,
        TriggerDispatcher? triggerDispatcher = null,
        GuildManager? guildManager = null,
        Mounts.MountEngine? mountEngine = null)
    {
        _movement = movement;
        _speech = speech;
        _commands = commands;
        _spellEngine = spellEngine;
        _deathEngine = deathEngine;
        _partyManager = partyManager;
        _tradeManager = tradeManager;
        _skillHandlers = skillHandlers;
        _craftingEngine = craftingEngine;
        _housingEngine = housingEngine;
        _triggerDispatcher = triggerDispatcher;
        _guildManager = guildManager;
        _mountEngine = mountEngine;
    }

    public void SetScriptServices(
        ScriptSystemHooks? systemHooks = null,
        ScriptDbAdapter? scriptDb = null,
        Func<string, string?>? defMessageLookup = null,
        ScriptFileHandle? scriptFile = null,
        ScriptDbAdapter? scriptLdb = null)
    {
        _systemHooks = systemHooks;
        _scriptDb = scriptDb;
        _scriptLdb = scriptLdb;
        _defMessageLookup = defMessageLookup;
        _scriptFile = scriptFile;
    }
}
