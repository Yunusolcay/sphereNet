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
public sealed class GameClient : ITextConsole
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

    // ==================== Login Flow ====================

    public void HandleLoginRequest(string account, string password)
    {
        _account = _accountManager.Authenticate(account, password);
        if (_account == null)
        {
            _netState.Send(new PacketLoginDenied(3));
            _netState.MarkClosing();
            return;
        }

        _account.LastIp = _netState.RemoteEndPoint?.Address.ToString() ?? "";
        // Keep login-server list deterministic for local development.
        // 0.0.0.0 (or unstable interface picks) can make some clients hang.
        _netState.Send(new PacketServerList("SphereNet", 0x7F000001));
    }

    public void HandleGameLogin(string account, string password, uint authId)
    {
        _logger.LogDebug("HandleGameLogin: account='{Account}' authId=0x{AuthId:X8}", account, authId);
        _account = _accountManager.Authenticate(account, password);
        if (_account == null)
        {
            _logger.LogDebug("HandleGameLogin: AUTH FAILED for '{Account}'", account);
            _netState.Send(new PacketLoginDenied(3));
            _netState.MarkClosing();
            return;
        }

        // Feature enable (0xB9) — must come before char list.
        // Prefer config-driven FEATURE* OR from sphere.ini (set via
        // ServerFeatureFlags during startup). Fall back to a client-version
        // mapping if the config is empty (e.g. test harness without ini).
        uint featureFlags;
        if (ServerFeatureFlags != 0)
        {
            featureFlags = ServerFeatureFlags;
        }
        else if (_netState.IsClientPost7090)
            featureFlags = 0x0244; // SA+ML
        else if (_netState.IsClientPost6017)
            featureFlags = 0x0044; // ML
        else if (_netState.ClientVersionNumber >= 50_000_000)
            featureFlags = 0x0004; // context menus (SE)
        else if (_netState.ClientVersionNumber >= 40_000_000)
            featureFlags = 0x0001; // T2A (AOS)
        else
            featureFlags = 0x0000; // minimal
        _netState.Send(new PacketFeatureEnable(featureFlags, _netState.IsClientPost60142));

        var charNames = _account.GetCharNames(uid => _world.FindChar(uid)?.GetName());
        var charListPacket = new PacketCharList(charNames);
        var built = charListPacket.Build();
        _netState.Send(built);
    }

    public void HandleCharSelect(int slot, string name)
    {
        if (_account == null) return;

        // Dedup: if this client already has a live character, a retransmitted
        // 0x5D/0xF8 must not create a second one. Without this guard a bugged
        // client sending the create packet N times produced N characters and
        // N paperdoll-open packets — observed as "20 paperdolls opened".
        if (_character != null && _character.IsOnline)
        {
            _logger.LogDebug("[LOGIN] Ignoring duplicate CharSelect for account '{Acct}'",
                _account.Name);
            return;
        }

        var charUid = _account.GetCharSlot(slot);
        if (charUid.IsValid)
            _character = _world.FindChar(charUid);

        if (_character == null)
        {
            _character = _world.CreateCharacter();
            _character.Name = string.IsNullOrWhiteSpace(name) ? _account.Name : name;
            _character.IsPlayer = true;
            _character.BodyId = 0x0190;
            _character.Str = 50; _character.Dex = 50; _character.Int = 50;
            _character.MaxHits = 50; _character.MaxMana = 50; _character.MaxStam = 50;
            _character.Hits = 50; _character.Mana = 50; _character.Stam = 50;

            var startPos = BotSpawnLocationProvider?.Invoke(_account.Name)
                ?? new Point3D(1495, 1629, 10, 0);
            _world.PlaceCharacter(_character, startPos);
            int assignSlot = slot >= 0 ? slot : _account.FindFreeSlot();
            if (assignSlot >= 0)
                _account.SetCharSlot(assignSlot, _character.Uid);
            _logger.LogInformation("Created char '{Name}' for account '{Acct}'", _character.Name, _account.Name);
        }
        else
        {
            var botPos = BotSpawnLocationProvider?.Invoke(_account.Name);
            if (botPos.HasValue)
                _world.MoveCharacter(_character, botPos.Value);
        }

        EnterWorld();
    }

    private void EnterWorld()
    {
        if (_character == null) return;
        // Dedup re-entry: 7.0.x clients sometimes retransmit 0x5D/0xF8 during
        // handshake. Every repeat used to drive a fresh login packet burst
        // (including 0x88 OpenPaperdoll), producing N paperdoll windows on the
        // client. Once IsOnline is set, subsequent EnterWorld calls are no-ops.
        if (_character.IsOnline)
        {
            _logger.LogDebug("[LOGIN] Ignoring duplicate EnterWorld for '{Name}'", _character.Name);
            return;
        }

        if (_account != null)
        {
            _character.SetTag("ACCOUNT", _account.Name);
            bool slotFound = false;
            for (int i = 0; i < 7; i++)
            {
                if (_account.GetCharSlot(i) == _character.Uid)
                { slotFound = true; break; }
            }
            if (!slotFound)
            {
                int free = _account.FindFreeSlot();
                if (free >= 0)
                    _account.SetCharSlot(free, _character.Uid);
            }
        }

        _logger.LogInformation("[LOGIN] '{Name}' pos: {X},{Y},{Z} map={Map}",
            _character.Name, _character.X, _character.Y, _character.Z, _character.Position.Map);
        _character.IsOnline = true;
        _world.AddOnlinePlayer(_character); // activates tick for this player's sectors
        OnCharacterOnline?.Invoke(_character, this);
        // Ensure character is in correct sector (may have been removed or stale after save/load)
        _world.PlaceCharacter(_character, _character.Position);
        EnsurePlayerBackpack(_character);
        _mountEngine?.EnsureMountedState(_character);

        if (_account != null)
        {
            var accLvl = _account.PrivLevel;
            var chLvl = _character.PrivLevel;
            var max = chLvl >= accLvl ? chLvl : accLvl;
            if (chLvl != max || accLvl != max)
            {
                _logger.LogInformation(
                    "[LOGIN] PrivLevel sync: account='{Acct}' PLEVEL={AccLvl} char=0x{Char:X8} PRIVLEVEL={ChLvl} -> {Max}",
                    _account.Name, accLvl, _character.Uid.Value, chLvl, max);
            }
            if (_account.PrivLevel != max) _account.PrivLevel = max;
            if (_character.PrivLevel != max) _character.PrivLevel = max;
        }
        _character.NormalizePlayerSkillClass();

        // Ensure Max stats are derived from attributes if missing (old saves)
        if (_character.MaxHits <= 0 && _character.Str > 0)
            _character.MaxHits = _character.Str;
        if (_character.MaxMana <= 0 && _character.Int > 0)
            _character.MaxMana = _character.Int;
        if (_character.MaxStam <= 0 && _character.Dex > 0)
            _character.MaxStam = _character.Dex;
        // Ensure current stats are at least 1 for a living character
        if (_character.Hits <= 0 && !_character.IsDead && _character.MaxHits > 0)
            _character.Hits = _character.MaxHits;
        if (_character.Mana <= 0 && _character.MaxMana > 0)
            _character.Mana = _character.MaxMana;
        if (_character.Stam <= 0 && _character.MaxStam > 0)
            _character.Stam = _character.MaxStam;

        // Sync _last* tracking fields so TickStatUpdate sends initial packets correctly
        _lastHits = _character.Hits;
        _lastMana = _character.Mana;
        _lastStam = _character.Stam;

        // Snap Z to the nearest walkable surface unless the character is
        // clearly on an upper-level structure (roof / bridge / second floor).
        // Rule:
        //   diff < 0                        → snap up  (character is below ground)
        //   0 < diff <= RoofSnapTolerance   → snap down (saved Z is stale / hovers)
        //   diff > RoofSnapTolerance        → keep (assume legitimate upper floor)
        // Without the downward snap, saves written with an out-of-band Z (e.g.
        // old dismount code that zeroed Z) keep that Z after login and every
        // subsequent CanWalkTo projects collision onto wall foundations.
        const int RoofSnapTolerance = 5;
        var mapData = _world.MapData;
        if (mapData != null)
        {
            sbyte terrainZ = mapData.GetEffectiveZ(_character.MapIndex, _character.X, _character.Y, _character.Z);
            int diff = terrainZ - _character.Z;
            if (diff != 0 && diff >= -RoofSnapTolerance)
            {
                _logger.LogInformation("Login Z correction: {OldZ} -> {NewZ} for '{Name}' at {X},{Y}",
                    _character.Z, terrainZ, _character.Name, _character.X, _character.Y);
                _character.Position = new Point3D(_character.X, _character.Y, terrainZ, _character.MapIndex);
            }
        }

        // Map dimensions per map index (ML-expanded Felucca = 7168x4096)
        ushort mapW = _character.MapIndex switch
        {
            0 => 7168,  // Felucca (ML expanded)
            1 => 7168,  // Trammel (ML expanded)
            2 => 2304,  // Ilshenar
            3 => 2560,  // Malas
            4 => 1448,  // Tokuno
            5 => 1280,  // Ter Mur
            _ => 7168
        };
        ushort mapH = _character.MapIndex switch
        {
            0 => 4096,
            1 => 4096,
            2 => 1600,
            3 => 2048,
            4 => 1448,
            5 => 4096,
            _ => 4096
        };

        _netState.Send(new PacketLoginConfirm(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z,
            (byte)_character.Direction, mapW, mapH
        ));

        _netState.Send(new PacketMapChange((byte)_character.MapIndex));
        _netState.Send(new PacketMapPatches()); // no map diffs — all zeros

        SendCharacterStatus(_character);
        SendSkillList();

        // Send paperdoll info on login so the client has name/title immediately.
        SendPaperdoll(_character);
        _netState.Send(new PacketGlobalLight(_world.GlobalLight));
        _netState.Send(new PacketPersonalLight(_character.Uid.Value, _character.LightLevel));
        _netState.Send(new PacketSeason((byte)_world.CurrentSeason));

        // Send player's own character with equipment — client needs this to render worn items
        SendDrawObject(_character);

        // Send equipped items individually (0x2E) so client tracks them in inventory
        for (int i = 1; i <= (int)Layer.Horse; i++)
        {
            var equip = _character.GetEquippedItem((Layer)i);
            if (equip != null)
            {
                _netState.Send(new PacketWornItem(
                    equip.Uid.Value, equip.DispIdFull, (byte)i,
                    _character.Uid.Value, equip.Hue));
            }
        }

        _netState.Send(new PacketLoginComplete());

        _knownChars.Clear();
        _knownItems.Clear();
        _lastKnownPos.Clear();

        // Fire @LogIn trigger
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.LogIn, new TriggerArgs { CharSrc = _character });
        _systemHooks?.DispatchClient("add", _character, _account);

        // Source-X CClient::Login: post LOGIN_PLAYER / LOGIN_PLAYERS so the new
        // arrival sees how many fellow players are already in the shard.
        int otherPlayers = 0;
        foreach (var c in _world.OnlinePlayers)
            if (c != _character && c.IsPlayer && c.IsOnline) otherPlayers++;
        if (otherPlayers == 1)
            SysMessage(ServerMessages.Get(Msg.LoginPlayer));
        else if (otherPlayers > 1)
            SysMessage(ServerMessages.GetFormatted(Msg.LoginPlayers, otherPlayers));

        // Source-X also stamps the previous login timestamp via LOGIN_LASTLOGGED.
        if (_account != null && _account.LastLogin > DateTime.MinValue)
        {
            SysMessage(ServerMessages.GetFormatted(Msg.LoginLastlogged,
                _account.LastLogin.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        // Ensure first world snapshot is fully consistent.
        // Some clients can show partially black map/chunk artifacts if they only
        // receive the minimal login packet set without a full nearby object refresh.
        Resync();
        _mountEngine?.EnsureMountedState(_character);

        // Immediate view update so this client sees all nearby characters/items
        // right away, without waiting for the next game-loop tick.
        UpdateClientView();

        _logger.LogInformation("Client '{Name}' entered world at {Pos}", _character.Name, _character.Position);
    }

    private void EnsurePlayerBackpack(Character ch)
    {
        if (!ch.IsPlayer)
            return;

        Item? pack = ch.GetEquippedItem(Layer.Pack) ?? ch.Backpack;
        if (pack == null)
        {
            // Recover backpack by containment link first to avoid creating duplicates.
            pack = _world.GetContainerContents(ch.Uid)
                .FirstOrDefault(i =>
                    !i.IsDeleted &&
                    (i.EquipLayer == Layer.Pack ||
                     i.BaseId == 0x0E75 ||
                     i.ItemType == ItemType.Container));
        }
        if (pack == null || pack.IsDeleted || _world.FindItem(pack.Uid) == null)
        {
            pack = _world.CreateItem();
            pack.BaseId = 0x0E75; // backpack
            pack.ItemType = ItemType.Container;
            pack.Name = "Backpack";
        }

        // Keep canonical backpack metadata consistent, then ensure it is equipped.
        pack.ItemType = ItemType.Container;
        if (pack.BaseId == 0)
            pack.BaseId = 0x0E75;
        if (string.IsNullOrWhiteSpace(pack.Name))
            pack.Name = "Backpack";

        ch.Backpack = pack;
        if (ch.GetEquippedItem(Layer.Pack) != pack)
            ch.Equip(pack, Layer.Pack);
    }

    /// <summary>
    /// Full client resync. Clears all known objects and re-sends the entire world state.
    /// Maps to CClient::addReSync in Source-X. Called when:
    ///   - Client requests resync (packet 0x22 / .RESYNC command)
    ///   - Movement desync detected
    ///   - Teleport/map change
    ///   - GM manually triggers it
    /// </summary>
    public void Resync()
    {
        if (_character == null || !IsPlaying) return;
        _mountEngine?.EnsureMountedState(_character);

        // 1. Delete all known objects on client side
        foreach (uint uid in _knownChars)
            _netState.Send(new PacketDeleteObject(uid));
        foreach (uint uid in _knownItems)
            _netState.Send(new PacketDeleteObject(uid));

        _knownChars.Clear();
        _knownItems.Clear();
        _lastKnownPos.Clear();

        // 2. Reposition player first, then send full appearance.
        // DrawPlayer (0x20) must come BEFORE DrawObject (0x78) because the
        // UO client redraws the local character on 0x20 without equipment —
        // sending 0x78 afterwards restores the full equipment visual including
        // the mount at Layer.Horse.
        _netState.Send(new PacketDrawPlayer(
            _character.Uid.Value, _character.BodyId, _character.Hue,
            BuildMobileFlags(_character),
            _character.X, _character.Y, _character.Z, (byte)_character.Direction));
        SendDrawObject(_character);

        // Send equipped items individually so client tracks them
        for (int i = 1; i <= (int)Layer.Horse; i++)
        {
            var equip = _character.GetEquippedItem((Layer)i);
            if (equip != null)
            {
                _netState.Send(new PacketWornItem(
                    equip.Uid.Value, equip.DispIdFull, (byte)i,
                    _character.Uid.Value, equip.Hue));
            }
        }

        // 3. Re-send full status
        SendCharacterStatus(_character);

        // 4. Re-send light & season
        _netState.Send(new PacketGlobalLight(_world.GlobalLight));
        _netState.Send(new PacketPersonalLight(_character.Uid.Value, _character.LightLevel));
        _netState.Send(new PacketSeason((byte)_world.CurrentSeason, playSound: false));

        // 5. Reset walk sequence (0 = resync sentinel, client must send seq 0 next)
        _netState.WalkSequence = 0;
        _nextMoveTime = 0;

        // 6. Final authoritative DrawObject — ensures mount at Layer.Horse renders.
        // Some clients skip mount rendering from the first 0x78 if it arrives
        // interleaved with status/light packets. This final 0x78 is sent after
        // all other visual updates, guaranteeing the equipment list (including
        // mount) is processed last.
        SendDrawObject(_character);

        // 7. Force full scan on next tick to re-populate all nearby objects
        ViewNeedsRefresh = true;

        BroadcastCharacterAppear?.Invoke(_character);

        _logger.LogDebug("Resync for client '{Name}'", _character.Name);
    }

    /// <summary>
    /// Handle a map-boundary teleport: tell the client which map to render, then
    /// run a full resync so it drops objects from the old map and receives the
    /// new map's objects via the view-delta pipeline.
    /// </summary>
    public void HandleMapChanged()
    {
        if (_character == null || !IsPlaying) return;
        _netState.Send(new PacketMapChange((byte)_character.MapIndex));
        Resync();
    }

    /// <summary>
    /// Re-send DrawPlayer + DrawObject so the owner client re-renders the player
    /// with updated appearance flags (invisible → translucent, war mode, etc.).
    /// The client-side visual state only changes when it receives a fresh draw.
    /// </summary>
    public void SendSelfRedraw()
    {
        if (_character == null || !IsPlaying) return;
        _netState.Send(new PacketDrawPlayer(
            _character.Uid.Value, _character.BodyId, _character.Hue,
            BuildMobileFlags(_character),
            _character.X, _character.Y, _character.Z, (byte)_character.Direction));
        SendDrawObject(_character);
        // 0x20 causes the client to reset its walk sequence counter to 0.
        // Server-side must mirror that reset or the next client walk comes
        // in with seq=0 while expectedSeq still holds the pre-redraw value,
        // producing a seq_mismatch reject storm.
        _netState.WalkSequence = 0;
        _nextMoveTime = 0;
    }

    /// <summary>
    /// Partial resync — re-sends only position and nearby objects without full clear.
    /// Used for minor movement desync corrections.
    /// </summary>
    public void ResyncPosition()
    {
        if (_character == null) return;

        _netState.Send(new PacketDrawPlayer(
            _character.Uid.Value, _character.BodyId, _character.Hue,
            0, _character.X, _character.Y, _character.Z,
            (byte)_character.Direction));
        _netState.WalkSequence = 0;
        _nextMoveTime = 0;
    }

    /// <summary>Re-send the 0x4E PacketPersonalLight packet so the client
    /// applies the current <see cref="Character.LightLevel"/>. Called after
    /// effects that change personal brightness (e.g. Night Sight) —
    /// without this the server-side property change has no visible effect.</summary>
    public void SendPersonalLight()
    {
        if (_character == null || !IsPlaying) return;
        _netState.Send(new PacketPersonalLight(_character.Uid.Value, _character.LightLevel));
    }

    // ==================== Movement ====================

    // ServUO-style fastwalk prevention via time-based throttle
    private long _nextMoveTime;

    public void HandleMove(byte dir, byte seq, uint fastWalkKey)
    {
        if (_character == null) return;
        // NOTE: do NOT silently drop walk packets when IsDead. Source-X
        // ghosts walk freely; if the server eats the request without
        // sending either 0x22 (MoveAck) or 0x21 (MoveReject) the client's
        // walk sequence stalls and the player ends up frozen on screen
        // even though their ghost body is rendered correctly. The
        // post-death "client cannot move" symptom in the death log was
        // exactly this: 0x2C death status arrived, then every subsequent
        // walk packet from the client was silently swallowed here.

        var direction = (Direction)(dir & 0x07);
        bool running = (dir & 0x80) != 0;

        _netState.LastActivityTick = Environment.TickCount64;
        long now = Environment.TickCount64;
        byte expectedSeq = _netState.WalkSequence;

        // Turn-in-place: when the client's MoveRequest direction differs from
        // the character's current facing, the packet is a pure rotation — ACK
        // with the same position, advance the walk sequence, no collision
        // check needed. Source-X CClient::Event_Walk handles this identically.
        if (_character.Direction != direction)
        {
            _character.Direction = direction;
            byte notoRot = GetNotoriety(_character);
            _netState.Send(new PacketMoveAck(seq, notoRot));
            _netState.WalkSequence = (byte)(seq + 1);
            if (_netState.WalkSequence == 0) _netState.WalkSequence = 1;

            // Broadcast facing change so nearby players see the new direction.
            byte flagsRot = BuildMobileFlags(_character);
            byte dirRot = (byte)((byte)_character.Direction | (running ? 0x80 : 0));
            var rotPacket = new PacketMobileMoving(
                _character.Uid.Value, _character.BodyId,
                _character.X, _character.Y, _character.Z, dirRot,
                _character.Hue, flagsRot, notoRot);
            if (BroadcastMoveNearby != null)
                BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, rotPacket, _character.Uid.Value, _character);
            else
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, rotPacket, _character.Uid.Value);
            return;
        }

        // Strict sequence validation (ServUO-style): reject out-of-order walk packets.
        if (expectedSeq != 0 && seq != expectedSeq)
        {
            _logger.LogDebug("[move_reject] reason=seq_mismatch got={Got} expected={Expected} at {X},{Y},{Z}",
                seq, expectedSeq, _character.X, _character.Y, _character.Z);
            _netState.Send(new PacketMoveReject(seq, _character.X, _character.Y, _character.Z, (byte)_character.Direction));
            _netState.WalkSequence = 0;
            return;
        }

        // Fast-walk replay check intentionally dropped: the server never ships
        // the 6-key stack via 0xBF sub 0x01 to the client, so modern clients
        // either send key=0 (skipped by the !=0 guard) or emit locally-generated
        // keys whose rotation we cannot predict. False positives manifested as
        // mid-run "square jumping" rejects. The time-based throttle below is
        // the speedhack barrier.
        _netState.LastFastWalkKey = fastWalkKey;

        // Fastwalk throttle: reject if moving too fast.
        // Tolerance scales with move speed so TCP-buffered walk packets aren't rejected.
        if (_character.PrivLevel < PrivLevel.GM)
        {
            int moveDelay = MovementEngine.GetMoveDelay(_character.IsMounted, running);
            int tolerance = moveDelay * 5; // allow up to 5 predicted moves ahead
            if (_nextMoveTime > 0 && now < _nextMoveTime - tolerance)
            {
                _logger.LogDebug("[move_reject] reason=throttle ahead={Ahead}ms delay={Delay}ms at {X},{Y},{Z}",
                    _nextMoveTime - now, moveDelay, _character.X, _character.Y, _character.Z);
                _netState.Send(new PacketMoveReject(seq, _character.X, _character.Y, _character.Z, (byte)_character.Direction));
                _netState.WalkSequence = 0;
                _nextMoveTime = now;
                return;
            }
        }

        // Execute the move
        bool moved;
        SphereNet.Game.Movement.WalkCheck.Diagnostic moveDiag = default;
        if (_movement != null)
            moved = _movement.TryMoveDetailed(_character, direction, running, seq, out moveDiag);
        else
        {
            GetDirectionDelta(direction, out short dx, out short dy);
            var newPos = new Point3D((short)(_character.X + dx), (short)(_character.Y + dy), _character.Z, _character.MapIndex);
            _character.Direction = direction;
            _world.MoveCharacter(_character, newPos);
            moved = true;
        }

        if (moved)
        {
            // Advance next allowed move time — cap accumulation to prevent
            // cascading rejects when the client sends TCP-buffered walk packets.
            int moveDelay = MovementEngine.GetMoveDelay(_character.IsMounted, running);
            if (_nextMoveTime <= 0 || now >= _nextMoveTime)
                _nextMoveTime = now + moveDelay;
            else
                _nextMoveTime = Math.Min(_nextMoveTime + moveDelay, now + moveDelay * 5);

            byte notoriety = GetNotoriety(_character);
            _netState.Send(new PacketMoveAck(seq, notoriety));

            // NOTE: MoveAck (0x22) carries no Z data, so the client keeps its own
            // predicted Z.  We intentionally do NOT send DrawPlayer here — doing so
            // during active walking corrupts the client's move buffer and causes
            // cascading MoveRejects ("square jumping").  The server tracks the
            // authoritative Z internally and broadcasts it to other players via 0x77.
            // The client's slight Z prediction error is visually imperceptible.

            // Broadcast movement to nearby players (0x77 MobileMoving, NOT 0x20 DrawPlayer)
            byte flags = BuildMobileFlags(_character);
            byte dir77 = (byte)((byte)_character.Direction | (running ? 0x80 : 0));
            var movePacket = new PacketMobileMoving(
                _character.Uid.Value, _character.BodyId,
                _character.X, _character.Y, _character.Z, dir77,
                _character.Hue, flags, notoriety);
            if (BroadcastMoveNearby != null)
                BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
            else
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

            // Expected next sequence from client (0..255, wraps naturally)
            _netState.WalkSequence = (byte)(seq + 1);
        }
        else
        {
            // Attribute the reject to a specific algorithm stage so walk jams
            // can be traced instead of logged as a vague "collision".
            GetDirectionDelta(direction, out short dxLog, out short dyLog);
            short tgtX = (short)(_character.X + dxLog);
            short tgtY = (short)(_character.Y + dyLog);
            string reason;
            if (moveDiag.MobBlocked) reason = "mob_block";
            else if (!moveDiag.ForwardOk) reason = "forward_blocked";
            else if (moveDiag.DiagonalChecked && (!moveDiag.LeftOk || !moveDiag.RightOk))
                reason = $"diagonal_edge left={moveDiag.LeftOk} right={moveDiag.RightOk}";
            else reason = "unknown";

            _logger.LogDebug(
                "[move_reject] {Reason} dir={Dir} run={Run} from {FromX},{FromY},{FromZ} " +
                "target {TgtX},{TgtY} startZ={StartZ} startTop={StartTop} fwdZ={FwdZ} | " +
                "fwdLand=tile=0x{LandTile:X} ({LZ}/{LC}/{LT}) blocks={LB} consider={CL} | " +
                "statics={ST} impassable={IMP} surfaces={SC} items={IC} mobiles={MC} last={Last} | " +
                "tiles=[{Dump}] mobs=[{MobDump}]",
                reason, direction, running, _character.X, _character.Y, _character.Z,
                tgtX, tgtY, moveDiag.StartZ, moveDiag.StartTop, moveDiag.ForwardNewZ,
                moveDiag.FwdLandTileId, moveDiag.FwdLandZ, moveDiag.FwdLandCenter, moveDiag.FwdLandTop,
                moveDiag.FwdLandBlocks, moveDiag.FwdConsiderLand,
                moveDiag.FwdStaticTotal, moveDiag.FwdImpassableCount,
                moveDiag.FwdSurfaceCount, moveDiag.FwdItemSurfaceCount, moveDiag.FwdMobileCount,
                moveDiag.FwdReason, moveDiag.FwdStaticDump, moveDiag.FwdMobileDump);
            _netState.Send(new PacketMoveReject(seq, _character.X, _character.Y, _character.Z, (byte)_character.Direction));
            _netState.WalkSequence = 0;
        }
    }

    // ==================== Speech ====================

    public void HandleSpeech(byte type, ushort hue, ushort font, string text)
    {
        if (_character == null) return;

        if (TryHandleCommandSpeech(text))
            return;

        // Pet commands — "all follow", "all guard", "petname follow" etc.
        if (TryHandlePetCommand(text))
        {
            // Still broadcast the speech so others hear it
        }

        _speech?.ProcessSpeech(_character, text, (TalkMode)type, hue, font);

        // Broadcast speech to nearby clients
        int range = type switch
        {
            8 => 3,  // whisper
            9 => 48, // yell
            _ => 18  // say
        };

        var speechPacket = new PacketSpeechUnicodeOut(
            _character.Uid.Value, _character.BodyId,
            type, hue, font, "TRK", _character.Name, text
        );
        // Send to self first (speaker should see their own message)
        Send(speechPacket);
        // Then broadcast to nearby (excluding self since we already sent)
        BroadcastNearby?.Invoke(_character.Position, range, speechPacket, _character.Uid.Value);
    }

    // ==================== Combat ====================

    public void HandleAttack(uint targetUid)
    {
        if (_character == null || _character.IsDead) return;
        if (!_character.IsInWarMode)
            SetWarMode(true, syncClients: true, preserveTarget: true);

        // Source-X style target clear: attacking 0 resets current fight target.
        if (targetUid == 0 || targetUid == 0xFFFFFFFF)
        {
            _character.FightTarget = Serial.Invalid;
            _character.NextAttackTime = 0;
            return;
        }

        var target = _world.FindChar(new Serial(targetUid));
        if (target == null) return;
        _character.FightTarget = target.Uid;

        // Region PvP enforcement
        if (target.IsPlayer && _character.IsPlayer)
        {
            var region = _world.FindRegion(_character.Position);
            if (region != null && region.IsFlag(Core.Enums.RegionFlag.NoPvP))
            {
                SysMessage(ServerMessages.Get("combat_nopvp"));
                return;
            }
            // Attacking an innocent (neither criminal nor murderer) in a
            // guarded / non-PvP region flags the aggressor criminal. Attacking
            // a red/gray player is self-defense — no flag. Config gate:
            // ATTACKINGISACRIME.
            bool targetIsInnocent = target.IsPlayer && !target.IsCriminal && !target.IsMurderer;
            if (Character.AttackingIsACrimeEnabled && targetIsInnocent &&
                region != null && region.IsFlag(Core.Enums.RegionFlag.Guarded))
            {
                _character.MakeCriminal();
            }

            // Source-X: helping a criminal fight an innocent flags you criminal
            if (Character.HelpingCriminalsIsACrimeEnabled && !_character.IsCriminal &&
                (target.IsCriminal || target.IsMurderer) &&
                target.FightTarget.IsValid)
            {
                var victim = _world.FindChar(target.FightTarget);
                if (victim != null && victim.IsPlayer && !victim.IsCriminal && !victim.IsMurderer)
                    _character.MakeCriminal();
            }
        }

        // Fire @CombatStart — if script blocks, cancel attack
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.CombatStart,
                new TriggerArgs { CharSrc = _character, O1 = target });
            if (result == TriggerResult.True)
                return;
        }

        _character.Memory_Fight_Start(target);
        target.Memory_Fight_Start(_character);

        // Set initial swing delay so the first hit isn't instant
        if (_character.NextAttackTime == 0)
        {
            var w = _character.GetEquippedItem(Layer.OneHanded)
                 ?? _character.GetEquippedItem(Layer.TwoHanded);
            _character.NextAttackTime = Environment.TickCount64 + GetSwingDelayMs(_character, w);
        }

        // Range check — only swing now if already close enough
        var atkWeapon = _character.GetEquippedItem(Layer.OneHanded)
                     ?? _character.GetEquippedItem(Layer.TwoHanded);
        int atkMaxRange = (atkWeapon != null &&
            (atkWeapon.ItemType == ItemType.WeaponBow || atkWeapon.ItemType == ItemType.WeaponXBow))
            ? 10 : 1;
        int atkDist = Math.Max(Math.Abs(_character.X - target.X), Math.Abs(_character.Y - target.Y));
        if (atkDist > atkMaxRange)
            return;

        TrySwingAt(target);
    }

    /// <summary>
    /// Auto-attack tick. Called every server tick — if the player has a
    /// valid FightTarget and the swing timer has elapsed, automatically
    /// performs the next melee/ranged swing. Maps to CChar::Fight_HitTry
    /// in Source-X which runs every tick for any character with a fight
    /// target, giving continuous combat without requiring repeated 0x05
    /// packets from the client.
    /// </summary>
    public void TickCombat()
    {
        if (_character == null || _character.IsDead) return;
        if (!_character.FightTarget.IsValid) return;
        if (!_character.IsInWarMode) return;

        long now = Environment.TickCount64;
        if (now < _character.NextAttackTime) return;

        var target = _world.FindChar(_character.FightTarget);
        if (target == null || target.IsDead || target.IsDeleted)
        {
            _character.FightTarget = Serial.Invalid;
            return;
        }

        var weapon = _character.GetEquippedItem(Layer.OneHanded)
                  ?? _character.GetEquippedItem(Layer.TwoHanded);
        int maxRange = (weapon != null &&
            (weapon.ItemType == ItemType.WeaponBow || weapon.ItemType == ItemType.WeaponXBow))
            ? 10 : 1;
        int dist = Math.Max(Math.Abs(_character.X - target.X), Math.Abs(_character.Y - target.Y));
        if (dist > maxRange)
            return;

        TrySwingAt(target);
    }

    private void TrySwingAt(Character target)
    {
        if (_character == null) return;

        long now = Environment.TickCount64;
        if (now < _character.NextAttackTime)
            return;

        // Source-X CChar::Fight_CanHit gates: dead / paralyzed / sleeping
        // attackers can't swing. Also a STAM<=0 char collapses (CCharAct.cpp
        // OnTick "Stat_GetVal(STAT_DEX) <= 0"), so block the swing entirely
        // and re-check next tick — don't burn the recoil timer.
        if (_character.IsDead) return;

        // Manifest ghost protection: a dead target (peace OR war manifest)
        // is never a valid combat target. Source-X CChar::Fight_IsAttackable
        // returns false on m_pPlayer && IsStatFlag(STATF_DEAD); the
        // translucent manifest is purely cosmetic and exists so plain
        // observers can SEE the ghost without being able to hit it.
        // Without this guard a manifested ghost would take damage and
        // produce a "kill the dead" loop with no corpse / no resurrect.
        if (target == null || target.IsDead)
        {
            _character.FightTarget = Serial.Invalid;
            return;
        }
        if (_character.Stam <= 0)
        {
            _character.NextAttackTime = now + 500;
            return;
        }
        if (_character.IsStatFlag(StatFlag.Freeze) || _character.IsStatFlag(StatFlag.Sleeping))
        {
            _character.NextAttackTime = now + 250;
            return;
        }

        // Spell casting blocks weapon swings (Source-X Skill_Magery /
        // SKTRIG_START path: the cast skill owns m_atFight while it runs).
        // We tolerate the swing finishing the *current* recoil but won't
        // start a new one mid-cast.
        if (_character.TryGetTag("SPELL_CASTING", out _))
        {
            _character.NextAttackTime = now + 500;
            return;
        }

        var weapon = _character.GetEquippedItem(Layer.OneHanded)
                  ?? _character.GetEquippedItem(Layer.TwoHanded);

        _character.NextAttackTime = now + GetSwingDelayMs(_character, weapon);

        // Source-X Fight_Hit: UpdateDir(pCharTarg) before launching the
        // swing animation so the attacker visibly turns to face the target.
        // We do this even on missed swings so combat doesn't look frozen
        // from a side/back angle.
        FaceTarget(target);

        // Ranged LOS check
        if (weapon != null && _character.PrivLevel < PrivLevel.GM &&
            (weapon.ItemType == ItemType.WeaponBow || weapon.ItemType == ItemType.WeaponXBow))
        {
            int rangeDist = Math.Max(Math.Abs(_character.X - target.X), Math.Abs(_character.Y - target.Y));
            if (rangeDist > 1 && !_world.CanSeeLOS(_character.Position, target.Position))
            {
                SysMessage("You cannot see that target.");
                return;
            }

            // Source-X CCharFight: ranged weapons reject point-blank shots and need ammo.
            if (rangeDist <= 1)
            {
                SysMessage(ServerMessages.Get(Msg.CombatArchTooclose));
                return;
            }
            var ammoType = weapon.ItemType == ItemType.WeaponBow ? ItemType.WeaponArrow : ItemType.WeaponBolt;
            if (!HasAmmoInBackpack(ammoType))
            {
                SysMessage(ServerMessages.Get(Msg.CombatArchNoammo));
                return;
            }
        }

        // Each swing burns a small bit of stamina (Source-X
        // Fight_Hit -> UpdateStatVal(STAT_DEX, -1)).
        if (_character.Stam > 0)
            _character.Stam = (short)(_character.Stam - 1);

        int damage = CombatEngine.ResolveAttack(_character, target, weapon);

        if (damage == 0)
        {
            // Source-X CCharFight Hit_Miss: emit attacker miss + target miss text.
            SysMessage(ServerMessages.GetFormatted(Msg.CombatMisss, target.Name));
            // No simple way yet to message the target client; the overhead packet is enough on the source side.
        }

        if (damage > 0)
        {
            if (_lastCombatNotifyTarget != target.Uid.Value)
            {
                _lastCombatNotifyTarget = target.Uid.Value;
                NpcSpeech(_character, ServerMessages.GetFormatted(Msg.CombatAttacks, target.Name));
            }

            _spellEngine?.TryInterruptFromDamage(target, damage);

            if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
            {
                target.FightTarget = _character.Uid;
                target.NextNpcActionTime = 0;
                OnWakeNpc?.Invoke(target);
            }

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Hit,
                new TriggerArgs { CharSrc = _character, O1 = target, N1 = damage });
            _triggerDispatcher?.FireCharTrigger(target, CharTrigger.GetHit,
                new TriggerArgs { CharSrc = _character, N1 = damage });

            if (weapon != null)
                _triggerDispatcher?.FireItemTrigger(weapon, ItemTrigger.Hit,
                    new TriggerArgs { CharSrc = _character, ItemSrc = weapon, O1 = target, N1 = damage });
            var shield = target.GetEquippedItem(Layer.TwoHanded);
            if (shield != null)
                _triggerDispatcher?.FireItemTrigger(shield, ItemTrigger.GetHit,
                    new TriggerArgs { CharSrc = _character, ItemSrc = shield, N1 = damage });

            _logger.LogDebug("{Attacker} hit {Target} for {Dmg} damage",
                _character.Name, target.Name, damage);

            ushort swingAction = GetSwingAction(_character, weapon);
            var swingAnim = new PacketAnimation(_character.Uid.Value, swingAction);
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, swingAnim, 0);

            ushort swingSound = GetSwingSound(weapon);
            var swingSoundPacket = new PacketSound(swingSound, _character.X, _character.Y, _character.Z);
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, swingSoundPacket, 0);

            ushort hitSound = weapon != null ? (ushort)0x0239 : (ushort)0x0135;
            var hitSoundPacket = new PacketSound(hitSound, target.X, target.Y, target.Z);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, hitSoundPacket, 0);

            var getHitAnim = new PacketAnimation(target.Uid.Value, (ushort)AnimationType.GetHit);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, getHitAnim, 0);

            var damagePacket = new PacketDamage(target.Uid.Value, (ushort)Math.Min(damage, ushort.MaxValue));
            BroadcastNearby?.Invoke(target.Position, UpdateRange, damagePacket, 0);

            var healthPacket = new PacketUpdateHealth(
                target.Uid.Value, target.MaxHits, target.Hits);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, healthPacket, 0);

            if (target.Hits <= 0 && !target.IsDead && _deathEngine != null)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Kill,
                    new TriggerArgs { CharSrc = _character, O1 = target });
                _triggerDispatcher?.FireCharTrigger(target, CharTrigger.Death,
                    new TriggerArgs { CharSrc = _character });

                _knownChars.Remove(target.Uid.Value);

                var targetPos = target.Position;
                byte targetDir = (byte)((byte)target.Direction & 0x07);
                var corpse = _deathEngine.ProcessDeath(target, _character);
                _character.FightTarget = Serial.Invalid;

                if (corpse != null)
                {
                    uint corpseWireSerial = corpse.Uid.Value;
                    if (corpse.Amount > 1)
                        corpseWireSerial |= 0x80000000u;

                    if (target.IsPlayer)
                    {
                        _knownItems.Add(corpse.Uid.Value);
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                            targetDir);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpsePacket, 0);

                        // Player corpse: send contents + equip map for paperdoll corpse rendering.
                        foreach (var corpseItem in corpse.Contents)
                        {
                            var containerItem = new PacketContainerItem(
                                corpseItem.Uid.Value,
                                corpseItem.DispIdFull,
                                0,
                                corpseItem.Amount,
                                corpseItem.X,
                                corpseItem.Y,
                                corpse.Uid.Value,
                                corpseItem.Hue,
                                useGridIndex: true);
                            BroadcastNearby?.Invoke(targetPos, UpdateRange, containerItem, 0);
                        }

                        var corpseEquipEntries = new List<(byte Layer, uint ItemSerial)>();
                        var usedLayers = new HashSet<byte>();
                        foreach (var item in corpse.Contents)
                        {
                            byte layer = (byte)item.EquipLayer;
                            if (layer == (byte)Layer.None || layer == (byte)Layer.Face || layer == (byte)Layer.Pack)
                                continue;
                            if (!usedLayers.Add(layer))
                                continue;
                            corpseEquipEntries.Add((layer, item.Uid.Value));
                        }

                        var corpseEquip = new PacketCorpseEquipment(corpse.Uid.Value, corpseEquipEntries);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpseEquip, 0);

                        // NOTE: 0xAF DeathAnimation is NOT broadcast here.
                        // OnCharacterDeath below runs a per-observer dispatch
                        // that sends 0xAF to plain players (which remaps the
                        // mobile in ClassicUO so it disappears) and a
                        // 0x1D + 0x78 ghost mobile pair to staff observers
                        // (which avoids the 0xAF serial-remap so staff can
                        // see the ghost without the duplicate-mobile bug
                        // documented in the death plan). A blanket
                        // BroadcastNearby would defeat that distinction.
                    }
                    else
                    {
                        // NPC corpse — matches both Source-X (PacketDeath +
                        // RemoveFromView) and ServUO (DeathAnimation + Delete
                        // -> RemovePacket) reference flow:
                        //   1) 0x1A WorldItem  (corpse appears in world)
                        //   2) 0xAF DeathAnim  (mobile -> corpse transition)
                        //   3) 0x1D DeleteObj  (remove the dead mobile)
                        // Source-X CObjBase::DeletePrepare() calls
                        // RemoveFromView() which broadcasts 0x1D to all in
                        // range, and ServUO's Mobile.Kill() / OnDeath() chain
                        // ends with NPC.Delete() which sends 0x1D as well.
                        // Without 0x1D the dead mobile lingers in client
                        // collections (ClassicUO's 0xAF only re-keys the
                        // mobile under 0x80000000|serial; the visual entity
                        // is still there until the client receives 0x1D).
                        //
                        // 0x89/0x3C (CorpseEquipment/ContainerContent) are
                        // only sent for human-body corpses; sending them for
                        // monster corpses corrupts the client's input state.
                        _knownItems.Add(corpse.Uid.Value);
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                            targetDir);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpsePacket, 0);

                        var dirToKiller = target.Position.GetDirectionTo(_character.Position);
                        uint npcFallDir = (uint)dirToKiller <= 3 ? 1u : 0u;
                        var deathAnim = new PacketDeathAnimation(target.Uid.Value, corpse.Uid.Value, npcFallDir);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, deathAnim, 0);

                        var removeMobile = new PacketDeleteObject(target.Uid.Value);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, removeMobile, 0);
                    }
                }

                // PvP: notify the dying player's own client so it transitions
                // to ghost (body+hue swap, 0x77 broadcast, 0x20 self, 0x2C
                // death status). Without this the killer sees the corpse but
                // the victim's screen freezes with a still-alive paperdoll.
                if (target.IsPlayer && OnCharacterDeathOfOther != null)
                    OnCharacterDeathOfOther.Invoke(target);
            }

            // Reactive armor may have killed the attacker
            if (_character.Hits <= 0 && !_character.IsDead && _deathEngine != null)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Death,
                    new TriggerArgs { CharSrc = target });
                _deathEngine.ProcessDeath(_character, target);
                OnCharacterDeath();
            }
        }
        else
        {
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.HitMiss,
                new TriggerArgs { CharSrc = _character, O1 = target });

            // Swing animation plays even on miss
            ushort missAction = GetSwingAction(_character, weapon);
            var missAnim = new PacketAnimation(_character.Uid.Value, missAction);
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, missAnim, 0);

            ushort missSwingSound = GetSwingSound(weapon);
            var missSwingSoundPacket = new PacketSound(missSwingSound, _character.X, _character.Y, _character.Z);
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, missSwingSoundPacket, 0);

            var missSound = new PacketSound(0x0234, target.X, target.Y, target.Z);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, missSound, 0);
        }
    }

    /// <summary>
    /// Handle player death — body/hue ghost transition, death effect/sound,
    /// per-observer dispatch (plain players get 0xAF, staff get 0x1D + 0x78
    /// ghost mobile), self ghost render (0x77 + 0x20 + 0x78 self + 0x2C),
    /// and view-cache invalidation. Corpse + corpse equipment are already
    /// broadcast by the kill site (TrySwingAt PvP path or Program.OnNpcKill).
    /// </summary>
    public void OnCharacterDeath()
    {
        if (_character == null) return;

        // ---------------------------------------------------------------
        //   Source-X CChar::Death (CCharAct.cpp) reference order:
        //     1) MakeCorpse + UpdateCanSee(PacketDeath)   ← caller did this
        //     2) SetID(ghost) + SetHue(HUE_DEFAULT)       ← below
        //     3) addPlayerWarMode(off) + addTargCancel    ← below
        //     4) Per-observer dispatch (UpdateCanSee)     ← below
        //     5) PacketDeathMenu(Dead) on own client      ← below
        //
        //   Hue note: 0x4001 (HUE_TRANSLUCENT|1) makes the sprite
        //   see-through, NOT grey. ClassicUO renders the ghost body
        //   (0x192/0x193) as a proper grey shroud when hue == 0
        //   (HUE_DEFAULT). The "transparent ghost" bug from the early
        //   death logs was caused by sending 0x4001 here.
        // ---------------------------------------------------------------

        ushort ghostBody = _character.BodyId == 0x0191 ? (ushort)0x0193 : (ushort)0x0192;
        _character.BodyId = ghostBody;
        _character.Hue = Core.Types.Color.Default;

        // pClient->addPlayerWarMode(off). We only need the local
        // state flip + the 0x72 PacketWarMode echo to the dying
        // client — the per-observer dispatch below carries the
        // post-death flags (War=off implicit, Female bit derived
        // from ghost body) through its 0x78 PacketDrawObject. A
        // syncClients=true here would inject an early 0x77 to staff
        // observers that mutates their cached mobile (Hue/Flags
        // updated, Graphic NOT updated) — that intermediate state
        // can leave ClassicUO's animation atlas pointing at the
        // alive body even after the follow-up 0x1D + 0x78. So we
        // suppress the broadcast and rely on per-observer dispatch.
        if (_character.IsInWarMode)
            SetWarMode(false, syncClients: false, preserveTarget: false);
        // The 0x72 echo is mandatory regardless — ClassicUO's input
        // handler latches on it to release the war-mode toggle and
        // unblock the death menu.
        _netState.Send(new PacketWarModeResponse(false));

        // pClient->addTargCancel. CRITICAL: PacketTarget(0,0) with
        // flags=0 (Neutral) does NOT cancel in ClassicUO — it OPENS
        // a brand-new target cursor (TargetManager.SetTargeting:165:
        // `IsTargeting = cursorType < TargetType.Cancel;`). We use
        // flags=3 (Cancel). The _targetCursorActive guard avoids a
        // spurious 0x6C when no cursor was open — that flash was the
        // "ölen karakterde target çıkıyor" symptom.
        if (_targetCursorActive)
        {
            _netState.Send(new PacketTarget(0x00, 0x00000000, flags: 3));
            ClearPendingTargetState();
        }

        // Death particle + sound — single BroadcastNearby with
        // excludeUid=0 reaches everyone in range INCLUDING the dying
        // player (Source-X UpdateCanSee semantic). A redundant
        // _netState.Send afterwards would double-send and produce the
        // duplicate 0x70/0x54 wire-log entries seen in earlier traces.
        var deathEffect = new PacketEffect(
            0x03,
            _character.Uid.Value, 0,
            0x3735,
            _character.X, _character.Y, (short)_character.Z,
            0, 0, 0,
            10, 30, true, false);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, deathEffect, 0);

        var deathSound = new PacketSound(0x01FE, _character.X, _character.Y, _character.Z);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, deathSound, 0);

        // ---------------------------------------------------------------
        //   Per-observer dispatch (mirror of CChar::UpdateCanSee with the
        //   ghost-visibility filter). ClassicUO's 0xAF DisplayDeath remaps
        //   the dying mobile to (serial | 0x80000000) and removes the
        //   original key from world.Mobiles (PacketHandlers.cs:3711). So:
        //
        //     - PLAIN observer  → 0xAF (mobile vanishes via remap, only
        //                         the corpse + death anim remain visible)
        //                       + server-side cache cleanup (no 0x1D
        //                         needed; the slot is already empty).
        //     - STAFF observer  → 0x1D (delete the living-body mobile)
        //                       + 0x78 ghost mobile (fresh spawn under
        //                         the original serial — safe because we
        //                         never sent 0xAF to this observer, so
        //                         no remap collision).
        //                       + cache marked as ghost so the next view-
        //                         delta tick sees no body change.
        //     - SELF (handled below the loop, not inside) — needs a full
        //       0x77 + 0x20 + 0x78 + 0x2C sequence.
        //
        //   This is the correct mapping for "staff sees ghosts, plain
        //   players don't" (the user's confirmed visibility rule) without
        //   triggering the duplicate-mobile bug that the 0xAF + 0x78
        //   combo produced on the same observer.
        // ---------------------------------------------------------------
        byte ghostFlags = BuildMobileFlags(_character);
        byte ghostNoto = GetNotoriety(_character);
        byte ghostDir = (byte)_character.Direction;
        short cx = _character.X, cy = _character.Y;
        sbyte cz = _character.Z;
        uint victimUid = _character.Uid.Value;

        // Find the corpse the kill site just created so we can wire the
        // 0xAF DeathAnimation correctly (plain observers need the
        // corpse serial to anchor the falling-body animation). One tile
        // search covers the corpse — DeathEngine.PlaceItem positions it
        // exactly at the victim's tile.
        uint corpseSerial = 0;
        if (_world != null)
        {
            foreach (var item in _world.GetItemsInRange(_character.Position, 0))
            {
                if (item.ItemType != ItemType.Corpse) continue;
                if (!item.TryGetTag("OWNER_UID", out string? ownerStr)) continue;
                if (!uint.TryParse(ownerStr, out uint ownerUid)) continue;
                if (ownerUid != victimUid) continue;
                corpseSerial = item.Uid.Value;
                break;
            }
        }

        // Source-X g_Rand.GetValFast(2) — 0/1 forward/backward fall.
        uint fallDir = (uint)Random.Shared.Next(2);
        var deathAnim = new PacketDeathAnimation(victimUid, corpseSerial, fallDir);

        // Pre-build the ghost mobile draw object once; it's identical
        // for every staff observer.
        var ghostEquipment = BuildEquipmentList(_character);
        var ghostDraw = new PacketDrawObject(
            victimUid, ghostBody,
            cx, cy, cz, ghostDir,
            _character.Hue, ghostFlags, ghostNoto,
            ghostEquipment);

        // Follow-up 0x77 — even though ClassicUO's 0x78 path already
        // calls CheckGraphicChange() when GetOrCreateMobile spawns a
        // fresh entity (mobile.Graphic == 0 branch), some 4.x builds
        // skip the animation-atlas reset on the freshly-spawned ghost.
        // A redundant 0x77 with the same body re-runs CheckGraphicChange
        // against the now-current 0x192/0x193 graphic and forces the
        // animation cache to drop whatever leftover frames the alive
        // body left behind. Cheap to send, fully eliminates the
        // "staff still sees alive sprite" symptom.
        var ghostMovingBroadcast = new PacketMobileMoving(
            victimUid, ghostBody,
            cx, cy, cz, ghostDir,
            _character.Hue, ghostFlags, ghostNoto);

        ForEachClientInRange?.Invoke(_character.Position, UpdateRange, victimUid,
            (observerCh, observerClient) =>
            {
                bool canSeeGhost = observerCh.AllShow ||
                    observerCh.PrivLevel >= Core.Enums.PrivLevel.Counsel;
                if (canSeeGhost)
                {
                    // Staff path: send 0x78 ghost draw directly on the
                    // existing mobile serial. ClassicUO's DrawObject
                    // handler calls GetOrCreateMobile which returns the
                    // existing entity, updates Graphic to 0x192/0x193,
                    // and runs CheckGraphicChange() to reload the
                    // animation atlas. No 0x1D needed — sending
                    // DeleteObject first destroys the client-side mobile
                    // and the follow-up 0x78 recreates it, but some
                    // ClassicUO builds don't fully reset the animation
                    // cache on delete+recreate, leaving the alive sprite
                    // visible despite the ghost body being set.
                    observerClient.Send(ghostDraw);
                    observerClient.Send(ghostMovingBroadcast);
                    observerClient.UpdateKnownCharRender(victimUid, ghostBody, _character.Hue,
                        ghostDir, cx, cy, cz);
                }
                else
                {
                    observerClient.Send(deathAnim);
                    observerClient.RemoveKnownChar(victimUid, sendDelete: false);
                }
            });

        // ---------------------------------------------------------------
        //   Self updates — make the ghost form actually render on the
        //   dying player's own screen.
        //
        //   ClassicUO graphic-update reality (verified against
        //   PacketHandlers.cs in 4.x):
        //
        //   * 0x77 (UpdateCharacter) — for self does NOT touch
        //     world.Player.Graphic in older builds; only NotorietyFlag
        //     and (sometimes) flags get applied. CheckGraphicChange is
        //     called against the OLD graphic, leaving the male/human
        //     state in place.
        //
        //   * 0x78 (UpdateObject) — for an existing (non-zero-graphic)
        //     mobile, the body update path is gated by
        //     `mobile.Graphic == 0`, i.e. only fresh spawns get a real
        //     graphic switch. For self the existing mobile always has
        //     the alive body cached, so the ghost graphic NEVER lands.
        //
        //   * 0x20 (UpdatePlayer) — the ONLY canonical path that sets
        //     world.Player.Graphic = newGraphic and follows it with a
        //     CheckGraphicChange + animation-atlas reset. This must be
        //     the first body-bearing packet sent to the dying player.
        //
        //   * 0x88 (OpenPaperdoll) — forces the paperdoll gump to
        //     re-render against the now-updated body so the dying
        //     player sees the grey ghost on the paperdoll too.
        //
        //   * 0x2C (DeathScreen) — opens the death menu UI; the client
        //     echoes RequestWarMode(false) in response.
        //
        //   Send order is therefore 0x20 → 0x77 (CheckGraphicChange
        //   re-trigger, harmless if already correct) → 0x88 → 0x2C →
        //   status. The previous order (0x77 → 0x20 → 0x78) left the
        //   ghost graphic stuck on the dying client because 0x78 self
        //   was a no-op and 0x77 was racing 0x20.
        // ---------------------------------------------------------------
        var drawPacket = new PacketDrawPlayer(
            victimUid, ghostBody, _character.Hue,
            ghostFlags, cx, cy, cz, ghostDir);
        _netState.Send(drawPacket);

        var ghostMoving = new PacketMobileMoving(
            victimUid, ghostBody,
            cx, cy, cz, ghostDir,
            _character.Hue, ghostFlags, ghostNoto);
        _netState.Send(ghostMoving);

        _netState.Send(new PacketDeathStatus(PacketDeathStatus.ActionDead));

        SendCharacterStatus(_character);
        SysMessage(ServerMessages.Get("combat_dead"));
    }

    /// <summary>
    /// Handle resurrection — body restore (ghost → human), Source-X
    /// "Resurrect with Corpse" auto re-equip, self redraw (0x77 + 0x20
    /// + 0x78 self), per-observer dispatch (single 0x78 fresh draw,
    /// works for both plain — never had the ghost — and staff — had
    /// the ghost mobile, 0x78 overwrites it), and view-cache resync so
    /// the next BuildViewDelta tick sees the new living body.
    /// </summary>
    public void OnResurrect()
    {
        if (_character == null || !_character.IsDead) return;

        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.Resurrect,
                new TriggerArgs { CharSrc = _character });
            if (result == TriggerResult.True)
                return;
        }

        _character.Resurrect();

        // Ghost → human body remap. NOTE: polymorphed players store
        // their "form before polymorph" separately in Source-X — this
        // is a TODO when the polymorph system lands; for now plain
        // ghosts (0x192/0x193) are the only inputs we expect.
        ushort restoredBody = _character.BodyId switch
        {
            0x0193 => (ushort)0x0191,
            0x0192 => (ushort)0x0190,
            _      => _character.BodyId,
        };
        _character.BodyId = restoredBody;
        _character.Hue = Core.Types.Color.Default;

        // === Source-X "Resurrect with Corpse" — auto re-equip ===
        // If the resurrected character is standing on (or one tile of)
        // their own corpse, every item that was equipped at death goes
        // back to its original slot via the EQUIPLAYER tag, the rest
        // returns to the backpack, and the (now-empty) corpse is
        // deleted. Returns true iff the corpse was found — used only
        // for the SysMessage and for deciding whether to broadcast the
        // 0x1D corpse-delete (the corpse's own decay path will already
        // emit it, but we want it gone NOW so the resurrected player
        // isn't standing on a "ghost" corpse on every observer's
        // screen).
        bool corpseRestored = _deathEngine?.RestoreFromCorpse(_character) ?? false;

        byte resFlags = BuildMobileFlags(_character);
        byte resNoto = GetNotoriety(_character);
        byte resDir = (byte)_character.Direction;
        short cx = _character.X, cy = _character.Y;
        sbyte cz = _character.Z;
        uint uid = _character.Uid.Value;
        var resEquipment = BuildEquipmentList(_character);

        // Single draw object reused across all paths — same equipment,
        // same body, same hue everywhere.
        var resDraw = new PacketDrawObject(
            uid, restoredBody,
            cx, cy, cz, resDir,
            _character.Hue, resFlags, resNoto,
            resEquipment);

        // === Self redraw ===
        // Symmetrical to OnCharacterDeath: 0x20 is the ONLY packet
        // that actually swaps world.Player.Graphic in ClassicUO, so
        // it MUST go first. 0x77 then triggers a redundant
        // CheckGraphicChange (cheap insurance for older builds), 0x78
        // delivers the restored equipment list, and SendPaperdoll
        // forces the gump to re-render against the new (alive) body.
        _netState.Send(new PacketDrawPlayer(
            uid, restoredBody, _character.Hue,
            resFlags, cx, cy, cz, resDir));

        var resMoving = new PacketMobileMoving(
            uid, restoredBody,
            cx, cy, cz, resDir,
            _character.Hue, resFlags, resNoto);
        _netState.Send(resMoving);

        _netState.Send(resDraw);

        // === Per-observer dispatch ===
        // Plain observer: never saw the ghost (filter dropped it during
        // BuildViewDelta) → 0x78 spawns a brand-new living mobile under
        // the original serial.
        // Staff observer: had the ghost mobile in their world.Mobiles
        // (we sent 0x1D + 0x78 ghost during death and never sent 0xAF
        // so no remap happened) → 0x78 overwrites the body+equipment
        // in-place via UpdateGameObject. Same packet, same outcome,
        // single dispatch path.
        // Either way we update the cache so the next view-delta tick
        // doesn't see a stale ghost-body entry and re-emit a duplicate.
        ForEachClientInRange?.Invoke(_character.Position, UpdateRange, uid,
            (observerCh, observerClient) =>
            {
                observerClient.Send(resDraw);
                observerClient.UpdateKnownCharRender(uid, restoredBody, _character.Hue,
                    resDir, cx, cy, cz);
            });

        // === Resurrect-with-Corpse: client-side state sync ===
        // RestoreFromCorpse mutated the data layer (Equip + AddItem) but
        // did NOT push any wire updates. Without the broadcasts below,
        // ClassicUO observers don't know that backpack/armor came back —
        // the killer would still see a "naked" resurrected mobile, and
        // the resurrected player would see an empty backpack until they
        // close+reopen it (which forces the 0x3C ContainerContent
        // refresh). Source-X CChar::ContentAdd issues the same packet
        // pair: addObject (0x2E PacketWornItem) for layered gear,
        // addContents (0x25 PacketContainerItem) for backpack/loose
        // contents.
        if (corpseRestored)
        {
            // 1) Broadcast every equipped item (skip layers that wouldn't
            //    appear on a paperdoll: None / Face / Pack — Pack itself
            //    rides on the 0x78 above, its CONTENTS need 0x25 below).
            for (int layerIdx = 1; layerIdx <= (int)Layer.Horse; layerIdx++)
            {
                var layer = (Layer)layerIdx;
                if (layer == Layer.Pack || layer == Layer.Face) continue;
                var equip = _character.GetEquippedItem(layer);
                if (equip == null) continue;

                var wornPacket = new PacketWornItem(
                    equip.Uid.Value, equip.DispIdFull, (byte)layer,
                    uid, equip.Hue);
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, wornPacket, 0);
            }

            // 2) Stream the backpack contents back to the resurrecting
            //    player. We push 0x25 unconditionally (Sphere/ServUO
            //    both do) so even if no gump is currently open, the
            //    drag layer / hot-bar references are valid the moment
            //    a gump is opened. Containers nested inside the
            //    backpack (e.g. a pouch) also need their own contents
            //    pushed — we recurse via FindContentItem semantics.
            var pack = _character.Backpack;
            if (pack != null)
            {
                foreach (var child in _world.GetContainerContents(pack.Uid))
                {
                    _netState.Send(new PacketContainerItem(
                        child.Uid.Value, child.DispIdFull, 0,
                        child.Amount, child.X, child.Y,
                        pack.Uid.Value, child.Hue,
                        _netState.IsClientPost6017));
                }
            }
        }

        // Resurrection visual + sound — anchored fixed effect (0x376A
        // heal particle) and chime (0x0214). BroadcastNearby with
        // excludeUid=0 reaches the resurrected player too, so no extra
        // _netState.Send needed.
        var resEffect = new PacketEffect(
            0x03,
            uid, 0,
            0x376A,
            cx, cy, (short)cz,
            0, 0, 0,
            10, 30, true, false);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, resEffect, 0);

        var resSound = new PacketSound(0x0214, cx, cy, cz);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, resSound, 0);

        SendCharacterStatus(_character);
        SysMessage(ServerMessages.Get(corpseRestored
            ? "combat_resurrected_with_corpse"
            : "combat_resurrected"));
    }

    public void HandleWarMode(bool warMode)
    {
        if (_character == null) return;
        _logger.LogDebug("[war_toggle_request] client={ClientId} char=0x{Char:X8} requested={Requested} current={Current}",
            _netState.Id, _character.Uid.Value, warMode ? "war" : "peace", _character.IsInWarMode ? "war" : "peace");
        // @UserWarmode fires before the state flip so a script can abort
        // the toggle by returning 1. Matches Source-X @UserWarmode in
        // CClient::Event_WalkToggleWarmode.
        var triggerArgs = new TriggerArgs { CharSrc = _character, N1 = warMode ? 1 : 0 };
        if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserWarmode, triggerArgs) == TriggerResult.True)
            return;
        SetWarMode(warMode, syncClients: true, preserveTarget: false);
        SysMessage(warMode ? ServerMessages.Get("combat_warmode_on") : ServerMessages.Get("combat_warmode_off"));
    }

    // ==================== Spell Casting ====================

    public void HandleCastSpell(SpellType spell, uint targetUid)
    {
        if (_character == null || _spellEngine == null) return;

        // Fire @SpellCast — if script blocks, don't cast
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SpellCast,
                new TriggerArgs { CharSrc = _character, N1 = (int)spell });
            if (result == TriggerResult.True)
                return;
        }

        // If no explicit target provided, check if the spell needs a target cursor
        if (targetUid == 0)
        {
            var spellDef = _spellEngine.GetSpellDef(spell);
            bool needsTarget = spellDef != null &&
                (spellDef.IsFlag(SpellFlag.TargChar) || spellDef.IsFlag(SpellFlag.TargObj) ||
                 spellDef.IsFlag(SpellFlag.Area) || spellDef.IsFlag(SpellFlag.Field));

            if (needsTarget)
            {
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (_character == null) return;
                    _character.SetTag("SPELL_TARGET_POS_X", x.ToString());
                    _character.SetTag("SPELL_TARGET_POS_Y", y.ToString());
                    _character.SetTag("SPELL_TARGET_POS_Z", z.ToString());
                    HandleCastSpell(spell, serial != 0 ? serial : _character.Uid.Value);
                });
                return;
            }

            // Self-buff spell — target self
            targetUid = _character.Uid.Value;
        }

        var targetPos = _character.Position;
        if (_character.TryGetTag("SPELL_TARGET_POS_X", out string? spx) &&
            _character.TryGetTag("SPELL_TARGET_POS_Y", out string? spy) &&
            _character.TryGetTag("SPELL_TARGET_POS_Z", out string? spz))
        {
            short.TryParse(spx, out short stx);
            short.TryParse(spy, out short sty);
            sbyte.TryParse(spz, out sbyte stz);
            targetPos = new Point3D(stx, sty, stz, _character.MapIndex);
            _character.RemoveTag("SPELL_TARGET_POS_X");
            _character.RemoveTag("SPELL_TARGET_POS_Y");
            _character.RemoveTag("SPELL_TARGET_POS_Z");
        }
        else
        {
            var targetChar = _world.FindChar(new Serial(targetUid));
            if (targetChar != null)
                targetPos = targetChar.Position;
        }

        int castTime = _spellEngine.CastStart(_character, spell, new Serial(targetUid), targetPos);
        if (castTime > 0)
        {
            _character.SetTag("CAST_TIMER", (Environment.TickCount64 + castTime).ToString());
        }
        else
        {
            // Fire @SpellFail
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellFail,
                new TriggerArgs { CharSrc = _character, N1 = (int)spell });
            SysMessage(ServerMessages.Get("spell_cant_cast"));
        }
    }

    public void TickSpellCast()
    {
        if (_character == null || _spellEngine == null) return;

        if (_character.TryGetTag("CAST_TIMER", out string? timerStr) &&
            long.TryParse(timerStr, out long castEnd) &&
            Environment.TickCount64 >= castEnd)
        {
            _character.RemoveTag("CAST_TIMER");

            // Retrieve spell ID before CastDone clears state
            int spellId = 0;
            if (_character.TryGetTag("SPELL_CASTING", out string? spellStr))
                int.TryParse(spellStr, out spellId);

            // Get spell def + target BEFORE CastDone clears state
            var spellDef = _spellEngine.GetSpellDef((SpellType)spellId);
            uint targetUidRaw = 0;
            if (_character.TryGetTag("SPELL_TARGET_UID", out string? tgtStr))
                uint.TryParse(tgtStr, out targetUidRaw);
            var targetChar = targetUidRaw != 0 ? _world.FindChar(new Serial(targetUidRaw)) : null;

            bool castOk = _spellEngine.CastDone(_character);

            if (castOk)
            {
                // --- Spell name message ---
                string spellName = spellDef?.Name ?? $"Spell #{spellId}";
                SysMessage(ServerMessages.GetFormatted("spell_cast_ok", spellName));

                // --- Visual effect (0x70) on target ---
                var effectTarget = targetChar ?? _character;
                ushort effectGraphic = spellDef?.EffectId ?? 0;
                if (effectGraphic != 0)
                {
                    // type 3 = effect at location (on char), type 1 = bolt from src to dst
                    byte effectType = (spellDef != null && spellDef.IsFlag(SpellFlag.FxBolt)) ? (byte)1 : (byte)3;
                    var effectPacket = new PacketEffect(
                        effectType,
                        effectType == 1 ? _character.Uid.Value : effectTarget.Uid.Value,
                        effectTarget.Uid.Value,
                        effectGraphic,
                        effectTarget.X, effectTarget.Y, (short)effectTarget.Z,
                        effectTarget.X, effectTarget.Y, (short)effectTarget.Z,
                        10, 30, true, false);
                    _netState.Send(effectPacket);
                    BroadcastNearby?.Invoke(effectTarget.Position, UpdateRange, effectPacket, _character.Uid.Value);
                }

                // --- Buff icon (0xDF) for beneficial spells with duration ---
                if (spellDef != null && spellDef.IsFlag(SpellFlag.Good) && spellDef.DurationBase > 0)
                {
                    int skillLvl = _character.GetSkill(spellDef.GetPrimarySkill());
                    int durTenths = spellDef.GetDuration(skillLvl);
                    ushort durSec = (ushort)Math.Min(durTenths / 10, ushort.MaxValue);
                    ushort buffIconId = GetBuffIconId((SpellType)spellId);
                    if (buffIconId != 0)
                    {
                        _netState.Send(new PacketBuffIcon(
                            _character.Uid.Value, buffIconId, true, durSec, spellName, ""));
                    }
                }

                // Fire @SpellEffect on caster, @SpellSuccess
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellEffect,
                    new TriggerArgs { CharSrc = _character, N1 = spellId });
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellSuccess,
                    new TriggerArgs { CharSrc = _character, N1 = spellId });

                // Consume scroll if cast was initiated from one
                if (_character.TryGetTag("SCROLL_UID", out string? scrollUidStr))
                {
                    _character.RemoveTag("SCROLL_UID");
                    if (uint.TryParse(scrollUidStr, out uint scrollUid))
                    {
                        var scroll = _world.FindItem(new Serial(scrollUid));
                        if (scroll != null && !scroll.IsDeleted)
                        {
                            if (scroll.Amount > 1)
                                scroll.Amount--;
                            else
                                scroll.Delete();
                        }
                    }
                }
            }
            else
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellFail,
                    new TriggerArgs { CharSrc = _character, N1 = spellId });
            }
        }
    }

    /// <summary>Map spell types to ClassicUO buff icon IDs.</summary>
    private static ushort GetBuffIconId(SpellType spell) => spell switch
    {
        SpellType.ReactiveArmor => 0x03E8,
        SpellType.Protection => 0x03E9,
        SpellType.NightSight => 0x03ED,
        SpellType.MagicReflect => 0x03EC,
        SpellType.Incognito => 0x03EF,
        SpellType.Bless => 0x03EA,
        SpellType.Agility => 0x03EB,
        SpellType.Cunning => 0x03EE,
        SpellType.Strength => 0x03F0,
        SpellType.Invisibility => 0x03F1,
        SpellType.Paralyze => 0x03F2,
        SpellType.Poison => 0x03F3,
        SpellType.Curse => 0x03F6,
        _ => 0,
    };

    /// <summary>
    /// Consolidated client tick: runs combat, spell casting, and stat updates.
    /// Call this once per server tick instead of calling TickCombat, TickSpellCast,
    /// and TickStatUpdate separately. This ensures consistent tick order and
    /// simplifies maintenance (single place to modify if new tick types are added).
    /// </summary>
    public void TickClientState()
    {
        TickCombat();
        TickSpellCast();
        TickStatUpdate();
    }

    /// <summary>
    /// Detect stat changes (from regen, combat, etc.) and send updates to client.
    /// Called each server tick.
    /// </summary>
    public void TickStatUpdate()
    {
        if (_character == null || !IsPlaying) return;

        bool hitsChanged = _character.Hits != _lastHits;
        bool manaChanged = _character.Mana != _lastMana;
        bool stamChanged = _character.Stam != _lastStam;
        if (hitsChanged || manaChanged || stamChanged)
        {
            long now = Environment.TickCount64;
            if (_lastVitalsPacketTick > 0 && now - _lastVitalsPacketTick < VitalsPacketIntervalMs)
                return;

            _lastHits = _character.Hits;
            _lastMana = _character.Mana;
            _lastStam = _character.Stam;
            _lastVitalsPacketTick = now;

            // Send only changed vital packets (avoid A1/A2/A3 spam).
            if (hitsChanged)
            {
                var healthPacket = new PacketUpdateHealth(
                    _character.Uid.Value, _character.MaxHits, _character.Hits);
                _netState.Send(healthPacket);
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, healthPacket, _character.Uid.Value);
            }
            if (manaChanged)
            {
                _netState.Send(new PacketUpdateMana(
                    _character.Uid.Value, _character.MaxMana, _character.Mana));
            }
            if (stamChanged)
            {
                _netState.Send(new PacketUpdateStamina(
                    _character.Uid.Value, _character.MaxStam, _character.Stam));
            }
        }
    }

    // ==================== Double Click / Item Use ====================

    public void HandleDoubleClick(uint uid)
    {
        if (_character == null) return;

        if (uid == _character.Uid.Value)
        {
            // If mounted, dismount on self-dclick
            if (_character.IsMounted && _mountEngine != null)
            {
                uint oldMountItemUid = _character.GetEquippedItem(Layer.Horse)?.Uid.Value ?? 0;
                var npc = _mountEngine.Dismount(_character);

                // Correct Z to terrain after body type change (mounted→foot)
                var mapData = _world.MapData;
                if (mapData != null)
                {
                    sbyte correctedZ = mapData.GetEffectiveZ(_character.MapIndex,
                        _character.X, _character.Y, _character.Z);
                    if (correctedZ != _character.Z)
                    {
                        _logger.LogInformation("[DISMOUNT] Z correction: {OldZ} -> {NewZ}", _character.Z, correctedZ);
                        _character.Position = new Point3D(_character.X, _character.Y, correctedZ, _character.MapIndex);
                    }
                }

                if (npc != null)
                {
                    // Immediately remove the old horse-layer item from all clients to avoid ghost mount visuals.
                    if (oldMountItemUid != 0)
                        BroadcastDeleteObject(oldMountItemUid);

                    // Snap the client back to the server-authoritative rider position
                    // and drop the horse right there. Without the snap the client
                    // keeps its predicted (possibly 1 tile ahead) position while the
                    // server places the NPC at the real rider.Position, producing
                    // the "horse spawns a tile behind me" complaint. With the snap
                    // the player may briefly slide back one tile, but the horse is
                    // always exactly under the character — which is the contract.
                    _nextMoveTime = 0;
                    _netState.WalkSequence = 0;
                    _netState.Send(new PacketMoveReject(0,
                        _character.X, _character.Y, _character.Z,
                        (byte)((byte)_character.Direction & 0x07)));

                    // MobileMoving (0x77) to self + nearby — body update without screen reload.
                    byte flags = BuildMobileFlags(_character);
                    byte dir77 = (byte)((byte)_character.Direction & 0x07);
                    byte noto = GetNotoriety(_character);
                    var movePacket = new PacketMobileMoving(
                        _character.Uid.Value, _character.BodyId,
                        _character.X, _character.Y, _character.Z, dir77,
                        _character.Hue, flags, noto);
                    _netState.Send(movePacket); // self — update own body
                    if (BroadcastMoveNearby != null)
                        BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
                    else
                        BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

                    // Clear Ridden flag AFTER sending all dismount packets.
                    npc.ClearStatFlag(StatFlag.Ridden);

                    // Broadcast the dismounted NPC to nearby clients so it appears
                    // immediately. Without this the pet is invisible until the next
                    // view-delta tick and cannot be seen following the owner.
                    BroadcastCharacterAppear?.Invoke(npc);
                }
                return;
            }
            SendPaperdoll(_character);
            return;
        }

        var item = _world.FindItem(new Serial(uid));
        if (item != null)
        {
            // Fire @DClick on item — if script returns true, block default action
            if (_triggerDispatcher != null)
            {
                var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DClick,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item });
                if (result == TriggerResult.True)
                    return;
            }
            HandleItemUse(item);
            return;
        }

        var ch = _world.FindChar(new Serial(uid));
        if (ch != null)
        {
            // Fire @DClick on character — if script returns true, block default action
            if (_triggerDispatcher != null)
            {
                var result = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.DClick,
                    new TriggerArgs { CharSrc = _character });
                if (result == TriggerResult.True)
                    return;
            }
            if (!ch.IsPlayer && ch.NpcBrain == NpcBrainType.Vendor)
            {
                HandleVendorInteraction(ch);
                return;
            }

            // Mount check — double-click mountable NPC
            if (!ch.IsPlayer && _mountEngine != null &&
                Mounts.MountEngine.IsMountable(ch.BodyId))
            {
                // Already riding — block with message instead of falling through to paperdoll
                if (_character.IsMounted)
                {
                    SysMessage(ServerMessages.Get("mount_already_riding"));
                    return;
                }

                // UO mount-range rule: the mount must be adjacent (within 1 tile).
                // Without this check, a distant mount gets accepted by the server
                // while the client teleports the player to the mount's tile — the
                // classic "I got yanked onto my horse" glitch.
                int dx = Math.Abs(_character.X - ch.X);
                int dy = Math.Abs(_character.Y - ch.Y);
                if (_character.MapIndex != ch.MapIndex || dx > 1 || dy > 1)
                {
                    SysMessage("That is too far away.");
                    return;
                }

                uint mountNpcUid = ch.Uid.Value;
                if (_mountEngine.TryMount(_character, ch))
                {
                    // Correct Z to terrain after body type change (foot→mounted)
                    var mountMapData = _world.MapData;
                    if (mountMapData != null)
                    {
                        sbyte correctedZ = mountMapData.GetEffectiveZ(_character.MapIndex,
                            _character.X, _character.Y, _character.Z);
                        if (correctedZ != _character.Z)
                        {
                            _character.Position = new Point3D(_character.X, _character.Y, correctedZ, _character.MapIndex);
                        }
                    }

                    // Immediately remove the old NPC mount from nearby clients to prevent temporary duplicates.
                    BroadcastDeleteObject(mountNpcUid);

                    // Reset walk state — foot→mount speed transition
                    _netState.WalkSequence = 0;
                    _nextMoveTime = 0;

                    // MoveReject FIRST — clears walk queue + Offset.Z, sets exact position
                    _netState.Send(new PacketMoveReject(0,
                        _character.X, _character.Y, _character.Z,
                        (byte)((byte)_character.Direction & 0x07)));

                    // DrawObject AFTER — body/equipment update with Steps queue already cleared.
                    // BroadcastDrawObject sends to self + nearby clients.
                    BroadcastDrawObject(_character);
                    return;
                }
            }

            SendPaperdoll(ch);
        }
    }

    /// <summary>
    /// Source-X CClient::Cmd_Use_Item parity dispatcher.
    /// The Source-X switch handles ~30 IT_* branches; SphereNet mirrors each
    /// branch to either a real handler or, when the underlying engine is not
    /// yet ported, the matching DEFMSG_ITEMUSE_* + target-cursor prompt so
    /// players see the exact upstream UX. Anything not matched falls through
    /// to DEFMSG_ITEMUSE_CANTTHINK like upstream.
    /// </summary>
    private void HandleItemUse(Item item)
    {
        if (_character == null) return;
        if (_character.IsDead)
        {
            SysMessage(ServerMessages.Get("death_cant_while_dead"));
            return;
        }

        switch (item.ItemType)
        {
            // ---- containers / corpses ----
            case ItemType.Container:
            case ItemType.Corpse:
            case ItemType.TrashCan:
            case ItemType.ShipHold:
                SendOpenContainer(item);
                break;

            case ItemType.ContainerLocked:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                if (FindBackpackKeyFor(item) != null)
                    SysMessage(ServerMessages.Get(Msg.LockHasKey));
                else
                    SysMessage(ServerMessages.Get(Msg.LockContNoKey));
                break;

            case ItemType.ShipHoldLock:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                if (FindBackpackKeyFor(item) != null)
                    SysMessage(ServerMessages.Get(Msg.LockHasKey));
                else
                    SysMessage(ServerMessages.Get(Msg.LockHoldNoKey));
                break;

            // ---- doors ----
            case ItemType.Door:
                ToggleDoor(item);
                break;
            case ItemType.DoorLocked:
                SysMessage(ServerMessages.Get(Msg.ItemuseLocked));
                break;

            // ---- consumables / potions / books ----
            case ItemType.Potion:
                UsePotion(item);
                break;
            case ItemType.Food:
            case ItemType.Fruit:
            case ItemType.Drink:
                _character.Food = (ushort)Math.Min(_character.Food + 5, 60);
                SysMessage(ServerMessages.Get("itemuse_eat_food"));
                if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item }) != TriggerResult.True)
                {
                    item.Delete();
                }
                break;

            case ItemType.Book:
            case ItemType.Message:
                OpenBook(item, item.ItemType == ItemType.Book);
                break;

            case ItemType.Spellbook:
            case ItemType.SpellbookNecro:
            case ItemType.SpellbookPala:
            case ItemType.SpellbookBushido:
            case ItemType.SpellbookNinjitsu:
            case ItemType.SpellbookArcanist:
            case ItemType.SpellbookMystic:
            case ItemType.SpellbookMastery:
            case ItemType.SpellbookExtra:
            {
                ushort scrollOffset = item.ItemType switch
                {
                    ItemType.SpellbookNecro => 101,
                    ItemType.SpellbookPala => 201,
                    ItemType.SpellbookBushido => 401,
                    ItemType.SpellbookNinjitsu => 501,
                    ItemType.SpellbookArcanist => 601,
                    ItemType.SpellbookMystic => 677,
                    ItemType.SpellbookMastery => 701,
                    _ => 1
                };
                ulong spellBits = ((ulong)item.More2 << 32) | item.More1;
                _netState.Send(new PacketSpellbookContent(
                    item.Uid.Value, item.BaseId, scrollOffset, spellBits));
                _netState.Send(new PacketOpenContainer(item.Uid.Value, 0x003E, _netState.IsClientPost7090));
                break;
            }

            // ---- tools that target a follow-up object ----
            case ItemType.Bandage:
                SysMessage(ServerMessages.Get(Msg.ItemuseBandagePromt));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Healing, new Serial(serial)));
                break;

            case ItemType.Lockpick:
                SysMessage(ServerMessages.Get("target_promt"));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Lockpicking, new Serial(serial)));
                break;

            case ItemType.Scissors:
                SysMessage(ServerMessages.Get("target_promt"));
                SetPendingTarget((serial, x, y, z, gfx) => HandleScissorsTarget(item, new Serial(serial)));
                break;

            case ItemType.Tracker:
                SysMessage(ServerMessages.Get(Msg.ItemuseTrackerAttune));
                SetPendingTarget((serial, x, y, z, gfx) => item.SetTag("LINK", serial.ToString()));
                break;

            case ItemType.Key:
            case ItemType.Keyring:
                if (item.ContainedIn != _character.Backpack?.Uid && _character.PrivLevel < PrivLevel.GM)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseKeyFail));
                    break;
                }
                SysMessage(ServerMessages.Get(Msg.ItemuseKeyPromt));
                SetPendingTarget((serial, x, y, z, gfx) => HandleKeyUse(item, new Serial(serial)));
                break;

            case ItemType.HairDye:
                if (_character.GetEquippedItem(Layer.Hair) == null && _character.GetEquippedItem(Layer.FacialHair) == null)
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseDyeNohair));
                    break;
                }
                SysMessage("Choose a new color for your hair."); // Source-X dialog d_hair_dye not yet ported.
                break;

            case ItemType.Dye:
                SysMessage(ServerMessages.Get(Msg.ItemuseDyeVat));
                SetPendingTarget((serial, x, y, z, gfx) => HandleDyePickup(item, new Serial(serial)));
                break;

            case ItemType.DyeVat:
                SysMessage(ServerMessages.Get(Msg.ItemuseDyeTarg));
                SetPendingTarget((serial, x, y, z, gfx) => HandleDyeApply(item, new Serial(serial)));
                break;

            // ---- weapons (target prompt for stab/pluck) ----
            case ItemType.WeaponSword:
            case ItemType.WeaponFence:
            case ItemType.WeaponAxe:
            case ItemType.WeaponMaceSharp:
            case ItemType.WeaponMaceStaff:
            case ItemType.WeaponMaceSmith:
                SysMessage(ServerMessages.Get(Msg.ItemuseWeaponPromt));
                SetPendingTarget((serial, x, y, z, gfx) => { /* tinker/poison etc unimplemented */ });
                break;

            case ItemType.WeaponMaceCrook:
                SysMessage(ServerMessages.Get(Msg.ItemuseCrookPromt));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Herding, new Serial(serial)));
                break;

            case ItemType.WeaponMacePick:
                SysMessage(ServerMessages.GetFormatted(Msg.ItemuseMacepickTarg, item.Name ?? "pick"));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Mining, new Serial(serial), new Point3D(x, y, z)));
                break;

            // ---- pole/sextant/spyglass ----
            case ItemType.FishPole:
                SysMessage(ServerMessages.Get("fishing_promt"));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Fishing, new Serial(serial), new Point3D(x, y, z)));
                break;
            case ItemType.Fish:
                SysMessage(ServerMessages.Get(Msg.ItemuseFishFail));
                break;
            case ItemType.Telescope:
                SysMessage(ServerMessages.Get(Msg.ItemuseTelescope));
                break;
            case ItemType.Sextant:
                SysMessage($"Location: {_character.X}, {_character.Y}, {_character.Z}");
                break;
            case ItemType.SpyGlass:
                SysMessage(ServerMessages.Get(Msg.ItemuseTelescope));
                break;
            case ItemType.Map:
            case ItemType.MapBlank:
                SysMessage("You unroll the map."); // gump pending
                break;

            // ---- ore / forge / ingot (overridable via @DClick trigger) ----
            case ItemType.Ore:
                SysMessage(ServerMessages.Get(Msg.ItemuseForge));
                SetPendingTarget((serial, x, y, z, gfx) => RouteSkillTarget(SkillType.Mining, new Serial(serial), new Point3D(x, y, z)));
                break;
            case ItemType.Forge:
            case ItemType.Ingot:
                OpenCraftingGump(SkillType.Blacksmithing);
                break;

            // ---- crafting tools → default crafting gump (overridable via @DClick trigger) ----
            case ItemType.Mortar:
                OpenCraftingGump(SkillType.Alchemy);
                break;
            case ItemType.Carpentry:
            case ItemType.CarpentryChop:
                OpenCraftingGump(SkillType.Carpentry);
                break;
            case ItemType.CartographyTool:
                OpenCraftingGump(SkillType.Cartography);
                break;
            case ItemType.CookingTool:
                OpenCraftingGump(SkillType.Cooking);
                break;
            case ItemType.TinkerTools:
                OpenCraftingGump(SkillType.Tinkering);
                break;
            case ItemType.SewingKit:
                OpenCraftingGump(SkillType.Tailoring);
                break;
            case ItemType.ScrollBlank:
                OpenCraftingGump(SkillType.Inscription);
                break;

            // ---- ship / sign / shrine / runes ----
            case ItemType.ShipTiller:
                NpcSpeech(_character, ServerMessages.Get(Msg.ItemuseTillerman));
                break;
            case ItemType.Shrine:
                if (_character.IsDead)
                    SysMessage(ServerMessages.Get(Msg.HealingRes));
                else
                    SysMessage(ServerMessages.Get("itemuse_shrine"));
                break;
            case ItemType.Rune:
                SysMessage(ServerMessages.Get(Msg.ItemuseRuneName));
                break;

            // ---- bulletin / game / clock / spawn / animations ----
            case ItemType.BBoard:
                SysMessage("You open the bulletin board."); // bbox gump pending
                break;
            case ItemType.GameBoard:
                if (item.ContainedIn.IsValid)
                    SysMessage(ServerMessages.Get(Msg.ItemuseGameboardFail));
                else
                    SendOpenContainer(item);
                break;
            case ItemType.Clock:
                ObjectMessage(item, FormatLocalGameTime());
                break;
            case ItemType.AnimActive:
                SysMessage(ServerMessages.Get("item_in_use"));
                break;
            case ItemType.SpawnItem:
            case ItemType.SpawnChar:
                if (item.SpawnChar != null)
                {
                    var defName = item.SpawnChar.GetSpawnDefName();
                    if (item.SpawnChar.HasAliveSpawns())
                    {
                        item.SpawnChar.KillAll();
                        item.SpawnChar.ResetTimer();
                        SysMessage($"Spawn cleared: {defName}. Timer reset.");
                    }
                    else
                    {
                        item.SpawnChar.ForceSpawn();
                        SysMessage($"Spawn forced: {defName}. Spawning now.");
                    }
                }
                else
                {
                    SysMessage(ServerMessages.Get(Msg.ItemuseSpawnReset));
                }
                break;

            // ---- spell tools (Source-X routes via CClient::Cmd_Skill_Magery) ----
            case ItemType.Wand:
                if (item.More1 > 0)
                    HandleCastSpell((SpellType)item.More1, 0);
                else
                    SysMessage("This wand has no charges.");
                break;
            case ItemType.Scroll:
                if (item.More1 > 0)
                {
                    var scrollSpell = (SpellType)item.More1;
                    _character.SetTag("SCROLL_UID", item.Uid.Value.ToString());
                    HandleCastSpell(scrollSpell, 0);
                }
                else
                {
                    SysMessage("The scroll is blank.");
                }
                break;

            // ---- crystal ball / cannon ----
            case ItemType.CrystalBall:
                break; // Source-X: gaze, no message.
            case ItemType.CannonBall:
                SysMessage(ServerMessages.GetFormatted(Msg.ItemuseCballPromt, item.Name ?? "cannon ball"));
                break;
            case ItemType.CannonMuzzle:
                SysMessage(ServerMessages.Get(Msg.ItemuseCannonTarg));
                break;

            // ---- containers / signs / multi (existing engines) ----
            case ItemType.StoneGuild:
                OpenGuildStoneGump(item);
                break;
            case ItemType.Multi:
            case ItemType.MultiCustom:
            case ItemType.SignGump:
                OpenHouseSignGump(item);
                break;

            case ItemType.Deed:
                if (_housingEngine != null)
                {
                    var house = _housingEngine.PlaceHouse(_character, item.BaseId, _character.Position);
                    if (house != null)
                    {
                        SysMessage(ServerMessages.Get("house_placed"));
                        if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                                new TriggerArgs { CharSrc = _character, ItemSrc = item }) != TriggerResult.True)
                        {
                            item.Delete();
                        }
                    }
                    else
                    {
                        SysMessage(ServerMessages.Get("house_cant_place"));
                    }
                }
                break;

            // ---- BankBox / VendorBox: anti-cheat reject ----
            // ---- light sources ----
            case ItemType.LightLit:
                item.ItemType = ItemType.LightOut;
                _netState.Send(new PacketSound(0x0047, _character.X, _character.Y, _character.Z));
                BroadcastNearby?.Invoke(item.Position, UpdateRange,
                    new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                        item.X, item.Y, item.Z, item.Hue), 0);
                break;
            case ItemType.LightOut:
                item.ItemType = ItemType.LightLit;
                _netState.Send(new PacketSound(0x0047, _character.X, _character.Y, _character.Z));
                BroadcastNearby?.Invoke(item.Position, UpdateRange,
                    new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                        item.X, item.Y, item.Z, item.Hue), 0);
                break;

            // ---- telepad / switch ----
            case ItemType.Telepad:
            {
                var dest = item.MoreP;
                if (dest.X != 0 || dest.Y != 0)
                {
                    _character.MoveTo(dest);
                    SendSelfRedraw();
                    _netState.Send(new PacketSound(0x01FE, _character.X, _character.Y, _character.Z));
                }
                break;
            }
            case ItemType.Switch:
                _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item });
                break;

            // ---- beverages ----
            case ItemType.Booze:
                _character.Food = (ushort)Math.Min(_character.Food + 2, 60);
                SysMessage("*hic!*");
                if (_triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item }) != TriggerResult.True)
                {
                    item.Delete();
                }
                break;

            // ---- musical instruments ----
            case ItemType.Musical:
                RouteSkillTarget(SkillType.Musicianship, item.Uid);
                break;

            // ---- figurine (pet shrink/unshrink) ----
            case ItemType.Figurine:
            {
                uint linkedSerial = item.More1;
                if (linkedSerial != 0 && _world != null)
                {
                    var pet = _world.FindChar(new Serial(linkedSerial));
                    if (pet != null)
                    {
                        pet.MoveTo(_character.Position);
                        _world.PlaceCharacter(pet, _character.Position);
                        item.Delete();
                        SysMessage("Your pet materializes beside you.");
                    }
                    else
                    {
                        SysMessage("The creature is lost.");
                    }
                }
                else
                {
                    SysMessage(ServerMessages.Get(Msg.MsgFigurineNotyours));
                }
                break;
            }

            // ---- moongate ----
            case ItemType.Moongate:
            {
                var dest = item.MoreP;
                if (dest.X != 0 || dest.Y != 0)
                {
                    _character.MoveTo(dest);
                    SendSelfRedraw();
                    _netState.Send(new PacketSound(0x01FE, _character.X, _character.Y, _character.Z));
                    _netState.Send(new PacketEffect(2, 0, 0, 0x3728,
                        _character.X, _character.Y, (short)_character.Z,
                        _character.X, _character.Y, (short)_character.Z,
                        10, 30, true, false));
                }
                else
                {
                    _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item });
                }
                break;
            }

            // ---- training dummies ----
            case ItemType.TrainDummy:
            {
                ushort animId = (ushort)(item.BaseId == 0x1070 || item.BaseId == 0x1074 ? item.BaseId + 1 : item.BaseId);
                _netState.Send(new PacketSound(0x03B5, item.X, item.Y, item.Z));
                var skill = _character.GetEquippedItem(Layer.TwoHanded) != null
                    ? SkillType.Swordsmanship
                    : SkillType.Wrestling;
                RouteSkillTarget(skill, item.Uid);
                break;
            }
            case ItemType.TrainPickpocket:
                RouteSkillTarget(SkillType.Stealing, item.Uid);
                break;
            case ItemType.ArcheryButte:
                RouteSkillTarget(SkillType.Archery, item.Uid);
                break;

            // ---- kindling / bedroll / campfire ----
            case ItemType.Kindling:
                RouteSkillTarget(SkillType.Camping, item.Uid);
                break;
            case ItemType.Bedroll:
                SysMessage("You lay out the bedroll.");
                RouteSkillTarget(SkillType.Camping, item.Uid);
                break;
            case ItemType.Campfire:
                SysMessage("The fire is warm.");
                break;

            // ---- crafting stations (overridable via @DClick trigger) ----
            case ItemType.Loom:
            case ItemType.SpinWheel:
                OpenCraftingGump(SkillType.Tailoring);
                break;
            case ItemType.Anvil:
                OpenCraftingGump(SkillType.Blacksmithing);
                break;

            // ---- beehive / seed / pitcher ----
            case ItemType.BeeHive:
                SysMessage("You reach into the beehive.");
                break;
            case ItemType.Seed:
                SysMessage("Select where to plant the seed.");
                SetPendingTarget((serial, x, y, z, gfx) => { /* planting pending */ });
                break;
            case ItemType.Pitcher:
                UsePotion(item);
                break;
            case ItemType.PitcherEmpty:
                SysMessage("Select a water source to fill the pitcher.");
                SetPendingTarget((serial, x, y, z, gfx) => { /* fill pending */ });
                break;

            // ---- raw materials ----
            case ItemType.Cotton:
            case ItemType.Wool:
            case ItemType.Feather:
            case ItemType.Fur:
                SysMessage("Use a spinning wheel to process this material.");
                break;
            case ItemType.Thread:
            case ItemType.Yarn:
                SysMessage("Use a loom to weave this material.");
                break;
            case ItemType.Log:
            case ItemType.Board:
                SysMessage("Use a carpentry tool to craft with this.");
                break;
            case ItemType.Shaft:
                SysMessage("Use fletching tools to craft with this.");
                break;
            case ItemType.Bone:
                SysMessage("You examine the bone.");
                break;
            case ItemType.Rope:
                SysMessage("You examine the rope.");
                break;

            // ---- food variants ----
            case ItemType.FoodRaw:
            case ItemType.MeatRaw:
                SysMessage("This must be cooked first.");
                break;

            // ---- comm crystal ----
            case ItemType.CommCrystal:
                SysMessage("The crystal hums softly.");
                break;

            // ---- portcullis ----
            case ItemType.Portculis:
            case ItemType.PortLocked:
                ToggleDoor(item);
                break;

            // ---- fletching tool ----
            case ItemType.Fletching:
                OpenCraftingGump(SkillType.Bowcraft);
                break;

            case ItemType.EqBankBox:
            case ItemType.EqVendorBox:
                _logger.LogWarning("Suspicious dclick on bankbox/vendorbox uid={Uid}", item.Uid.Value);
                break;

            default:
                SysMessage(ServerMessages.Get(Msg.ItemuseCantthink));
                break;
        }
    }

    // ---- helpers used by HandleItemUse target callbacks ----

    /// <summary>Source-X arrow/bolt presence check before ranged swing.</summary>
    private bool HasAmmoInBackpack(ItemType ammo)
    {
        if (_character?.Backpack == null) return false;
        foreach (var it in _character.Backpack.Contents)
            if (it.ItemType == ammo && it.Amount > 0) return true;
        return false;
    }

    /// <summary>Find a key in the player's backpack that opens a locked container/door.</summary>
    private Item? FindBackpackKeyFor(Item locked)
    {
        if (_character?.Backpack == null) return null;
        uint linkId = locked.Uid.Value;
        foreach (var it in _character.Backpack.Contents)
        {
            if (it.ItemType is not (ItemType.Key or ItemType.Keyring)) continue;
            if (it.TryGetTag("LINK", out string? lk) && uint.TryParse(lk, out uint kv) && kv == linkId)
                return it;
        }
        return null;
    }

    /// <summary>Re-enter the active-skill pipeline with a pre-resolved Serial target.</summary>
    private void RouteSkillTarget(SkillType skill, Serial target, Point3D? point = null)
    {
        if (_character == null) return;
        var obj = target.IsValid ? _world.FindObject(target) : null;
        var sink = new InfoSkillSink(this, _character);
        _skillHandlers?.UseActiveSkill(sink, skill, obj, point);
    }

    /// <summary>Source-X uses scissors to convert hides/cloth to leather/bolts.</summary>
    private void HandleScissorsTarget(Item scissors, Serial target)
    {
        var obj = target.IsValid ? _world.FindObject(target) as Item : null;
        if (obj == null) { SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); return; }
        switch (obj.ItemType)
        {
            case ItemType.Hide: obj.ItemType = ItemType.Leather; SysMessage("You cut the hide into leather."); break;
            case ItemType.Cloth: obj.ItemType = ItemType.ClothBolt; SysMessage("You cut the cloth into bolts."); break;
            case ItemType.BandageBlood: obj.Delete(); SysMessage(ServerMessages.Get(Msg.ItemuseBandageClean)); break;
            default: SysMessage(ServerMessages.Get(Msg.ItemuseCantthink)); break;
        }
    }

    /// <summary>Source-X key use: link key, lock/unlock door or container.</summary>
    private void HandleKeyUse(Item key, Serial target)
    {
        var obj = target.IsValid ? _world.FindObject(target) as Item : null;
        if (obj == null) { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNolock)); return; }

        bool linked = key.TryGetTag("LINK", out string? lk) && uint.TryParse(lk, out uint kv) && kv == obj.Uid.Value;
        if (!linked) { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNokey)); return; }

        if (obj.ItemType == ItemType.ContainerLocked) obj.ItemType = ItemType.Container;
        else if (obj.ItemType == ItemType.Container) obj.ItemType = ItemType.ContainerLocked;
        else if (obj.ItemType == ItemType.DoorLocked) obj.ItemType = ItemType.Door;
        else if (obj.ItemType == ItemType.Door) obj.ItemType = ItemType.DoorLocked;
        else { SysMessage(ServerMessages.Get(Msg.ItemuseKeyNolock)); return; }
    }

    /// <summary>Pick a hue from a Dye onto a DyeVat (Source-X two-step).</summary>
    private void HandleDyePickup(Item dye, Serial target)
    {
        var vat = target.IsValid ? _world.FindObject(target) as Item : null;
        if (vat == null || vat.ItemType != ItemType.DyeVat)
        { SysMessage(ServerMessages.Get(Msg.ItemuseDyeFail)); return; }
        vat.SetTag("DYE_HUE", dye.Hue.ToString());
        SysMessage("You apply the dye to the vat.");
    }

    /// <summary>Apply a DyeVat hue to a target item.</summary>
    private void HandleDyeApply(Item vat, Serial target)
    {
        var dest = target.IsValid ? _world.FindObject(target) as Item : null;
        if (dest == null) { SysMessage(ServerMessages.Get(Msg.ItemuseDyeReach)); return; }
        if (vat.TryGetTag("DYE_HUE", out string? hueText) && ushort.TryParse(hueText, out ushort hue))
        {
            dest.Hue = new Core.Types.Color(hue);
            SysMessage("The item changes color.");
        }
    }

    /// <summary>Format a Source-X-style local game time string for IT_CLOCK.</summary>
    private static string FormatLocalGameTime()
    {
        var now = DateTime.Now;
        return $"It is {now.Hour:00}:{now.Minute:00}.";
    }

    /// <summary>
    /// Source-X CChar::NPC_OnHearPetCmd parity. Recognises every PC_* verb
    /// from upstream (FOLLOW/GUARD/STAY/STOP/COME/ATTACK/KILL/FRIEND/UNFRIEND/
    /// TRANSFER/RELEASE/DROP/DROP ALL/EQUIP/STATUS/CASH/BOUGHT/SAMPLES/STOCK/
    /// PRICE/GO/SPEAK/GUARD ME/FOLLOW ME) and routes pets through the matching
    /// PetAIMode + DEFMSG_NPC_PET_* output. Returns true when the input was a
    /// pet command -- caller then suppresses normal speech broadcast.
    /// </summary>
    private bool TryHandlePetCommand(string text)
    {
        if (_character == null) return false;
        string lower = text.ToLowerInvariant().Trim().TrimEnd('.', '!', '?');

        // Pet command vocabulary table mirrors sm_Pet_table in Source-X.
        // Order matters because we longest-prefix match (e.g. "follow me" before "follow").
        ReadOnlySpan<string> vocab =
        [
            "all follow", "all guard", "all stay", "all stop", "all come",
            "all attack", "all kill", "all friend", "all unfriend", "all transfer",
            "all release", "all drop all", "all drop", "all equip", "all status",
            "all guard me", "all follow me", "all go", "all speak",
            "follow me", "guard me", "drop all"
        ];

        // "all <verb>" path.
        if (lower.StartsWith("all "))
        {
            string verb = NormalizePetVerb(lower[4..], allMode: true);
            return DispatchAllPets(verb);
        }

        // "<petname> <verb>" path -- longest-match verb.
        int spaceIdx = lower.IndexOf(' ');
        if (spaceIdx <= 0) return false;
        string name = lower[..spaceIdx];
        string rest = NormalizePetVerb(lower[(spaceIdx + 1)..], allMode: false);
        return DispatchNamedPet(name, rest);
    }

    private static string NormalizePetVerb(string rawVerb, bool allMode)
    {
        string verb = rawVerb.Trim().ToLowerInvariant().TrimEnd('.', '!', '?');
        verb = verb switch
        {
            "kills" => "kill",
            "attacks" => "attack",
            "comes" => "come",
            "follows" => "follow",
            _ => verb
        };

        // Source-style shortcut: "all follow" behaves like "all follow me".
        if (allMode && verb == "follow")
            return "follow me";
        return verb;
    }

    /// <summary>Source-X PC_*: target a single pet by name prefix.</summary>
    private bool DispatchNamedPet(string namePrefix, string verb)
    {
        if (_character == null) return false;
        var pet = CollectCommandablePets(namePrefix).FirstOrDefault();
        if (pet == null)
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        return ApplyPetVerb(pet, verb);
    }

    /// <summary>Source-X PC_*: broadcast verb to every nearby pet of mine.</summary>
    private bool DispatchAllPets(string verb)
    {
        if (_character == null) return false;
        var pets = CollectCommandablePets().ToList();
        if (pets.Count == 0)
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        if (IsPetTargetVerb(verb))
        {
            EmitPetTargetPrompt(pets, verb);
            return true;
        }

        bool any = false;
        foreach (var pet in pets)
            if (ApplyPetVerb(pet, verb)) any = true;
        return any;
    }

    private static bool IsPetTargetVerb(string verb) => verb switch
    {
        "attack" or "kill" or "guard" or "follow" or "go" or
        "friend" or "unfriend" or "transfer" or "release" or
        "price" or "bought" or "samples" or "stock" or "cash" => true,
        _ => false
    };

    /// <summary>
    /// Apply a Source-X PC_* verb to a single pet, emitting the matching
    /// DEFMSG_NPC_PET_* message. Verbs that need a target store a pending
    /// callback so the next click resolves.
    /// </summary>
    private bool ApplyPetVerb(Character pet, string verb)
    {
        if (_character == null) return false;
        if (!pet.CanAcceptPetCommandFrom(_character))
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return false;
        }

        // Source-X: conjured/summoned NPCs can't be transferred or friended
        if (pet.IsSummoned && verb is "transfer" or "friend" or "unfriend")
        {
            NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
            return true;
        }

        // Source-X: dead bonded pets accept only passive commands
        if (pet.IsDead)
        {
            bool allowed = verb is "follow me" or "come" or "stay" or "stop" or "follow";
            if (!allowed)
            {
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                return true;
            }
        }

        switch (verb)
        {
            case "follow me":
                pet.PetAIMode = PetAIMode.Follow;
                pet.SetTag("FOLLOW_TARGET", _character.Uid.Value.ToString());
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "come":
                pet.PetAIMode = PetAIMode.Come;
                pet.SetTag("FOLLOW_TARGET", _character.Uid.Value.ToString());
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "stay":
            case "stop":
                pet.PetAIMode = PetAIMode.Stay;
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "guard me":
                pet.PetAIMode = PetAIMode.Guard;
                pet.SetTag("GUARD_TARGET", _character.Uid.Value.ToString());
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "speak":
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "drop":
                if (pet.Backpack == null || pet.Backpack.Contents.Count == 0)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetCarrynothing));
                    return true;
                }
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    pet.Backpack.RemoveItem(carried);
                    _world.PlaceItemWithDecay(carried, pet.Position);
                }
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "drop all":
                if (pet.Backpack == null || pet.Backpack.Contents.Count == 0)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetCarrynothing));
                    return true;
                }
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    pet.Backpack.RemoveItem(carried);
                    _world.PlaceItemWithDecay(carried, pet.Position);
                }
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetSuccess));
                return true;

            case "equip":
                if (pet.Backpack == null)
                {
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetFailure));
                    return true;
                }
                bool equippedAny = false;
                foreach (var carried in pet.Backpack.Contents.ToArray())
                {
                    Layer layer = ResolveWearableLayer(carried);
                    if (layer == Layer.None || pet.GetEquippedItem(layer) != null)
                        continue;
                    pet.Backpack.RemoveItem(carried);
                    pet.Equip(carried, layer);
                    equippedAny = true;
                }
                NpcSpeech(pet, ServerMessages.Get(equippedAny ? Msg.NpcPetSuccess : Msg.NpcPetFailure));
                return true;

            case "status":
                if (pet.TryGetTag("HIRE_DAYS_LEFT", out string? days))
                    NpcSpeech(pet, ServerMessages.GetFormatted(Msg.NpcPetDaysLeft, days ?? "0"));
                else
                    NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetEmployed));
                return true;

            case "attack":
            case "kill":
            case "guard":
            case "follow":
            case "go":
            case "friend":
            case "unfriend":
            case "transfer":
            case "release":
            case "price":
            case "bought":
            case "samples":
            case "stock":
            case "cash":
                EmitPetTargetPrompt(pet, verb);
                return true;

            default:
                NpcSpeech(pet, ServerMessages.Get(Msg.NpcPetConfused));
                return false;
        }
    }

    /// <summary>
    /// Source-X verbs that need a target open the cursor with the matching
    /// DEFMSG_NPC_PET_TARG_* prompt. The follow-up click is wired into
    /// ApplyPetTarget().
    /// </summary>
    private void EmitPetTargetPrompt(Character pet, string verb)
    {
        string promptKey = verb switch
        {
            "attack" or "kill" => Msg.NpcPetTargAtt,
            "guard"            => Msg.NpcPetTargGuard,
            "follow"           => Msg.NpcPetTargFollow,
            "friend"           => Msg.NpcPetTargFriend,
            "unfriend"         => Msg.NpcPetTargUnfriend,
            "transfer"         => Msg.NpcPetTargTransfer,
            "go"               => Msg.NpcPetTargGo,
            "price"            => Msg.NpcPetSetprice,
            _                  => Msg.NpcPetSuccess,
        };
        SysMessage(ServerMessages.Get(promptKey));
        SetPendingTarget(
            (serial, x, y, z, gfx) => ApplyPetTarget(pet, verb, new Serial(serial), x, y, z),
            cursorType: verb == "go" ? (byte)1 : (byte)0);
    }

    private void EmitPetTargetPrompt(IReadOnlyList<Character> pets, string verb)
    {
        if (pets.Count == 0)
            return;

        string promptKey = verb switch
        {
            "attack" or "kill" => Msg.NpcPetTargAtt,
            "guard" => Msg.NpcPetTargGuard,
            "follow" => Msg.NpcPetTargFollow,
            "friend" => Msg.NpcPetTargFriend,
            "unfriend" => Msg.NpcPetTargUnfriend,
            "transfer" => Msg.NpcPetTargTransfer,
            "go" => Msg.NpcPetTargGo,
            "price" => Msg.NpcPetSetprice,
            _ => Msg.NpcPetSuccess,
        };

        var petUids = pets.Select(p => p.Uid).ToList();
        SysMessage(ServerMessages.Get(promptKey));
        SetPendingTarget((serial, x, y, z, gfx) =>
            {
                foreach (var petUid in petUids)
                {
                    var pet = _world.FindChar(petUid);
                    if (pet == null || pet.IsDeleted || pet.IsDead || _character == null ||
                        !pet.CanAcceptPetCommandFrom(_character))
                    {
                        continue;
                    }

                    ApplyPetTarget(pet, verb, new Serial(serial), x, y, z);
                }
            },
            cursorType: verb == "go" ? (byte)1 : (byte)0);
    }

    /// <summary>Resolve a target picked after EmitPetTargetPrompt and apply the verb.</summary>
    private void ApplyPetTarget(Character pet, string verb, Serial uid, short x, short y, sbyte z)
    {
        if (_character == null) return;
        if (!pet.CanAcceptPetCommandFrom(_character))
        {
            SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
            return;
        }

        var obj = uid.IsValid ? _world.FindObject(uid) : null;

        switch (verb)
        {
            case "attack":
            case "kill":
                if (obj is Character victim && victim != pet &&
                    !victim.IsDead && !victim.IsStatFlag(StatFlag.Invul) &&
                    !victim.IsStatFlag(StatFlag.Ridden))
                {
                    pet.SetTag("ATTACK_TARGET", victim.Uid.Value.ToString());
                    pet.FightTarget = victim.Uid;
                    pet.PetAIMode = PetAIMode.Attack;
                    OnWakeNpc?.Invoke(pet);
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "guard":
                if (obj is Character guarded)
                {
                    pet.SetTag("GUARD_TARGET", guarded.Uid.Value.ToString());
                    pet.PetAIMode = PetAIMode.Guard;
                    SysMessage(ServerMessages.Get(Msg.NpcPetTargGuardSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "follow":
                if (obj is Character followee)
                {
                    pet.SetTag("FOLLOW_TARGET", followee.Uid.Value.ToString());
                    pet.PetAIMode = PetAIMode.Follow;
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "friend":
                if (obj is Character friend && friend.IsPlayer)
                {
                    if (pet.IsSummoned)
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargFriendSummoned));
                    }
                    else if (pet.IsFriendOf(friend.Uid))
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargFriendAlready));
                    else
                    {
                        pet.AddFriend(friend);
                        SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess1, friend.Name));
                        if (friend != _character)
                            SendToChar?.Invoke(friend.Uid, new PacketSpeechUnicodeOut(
                                0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System",
                                ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess2, pet.Name)));
                    }
                }
                break;

            case "unfriend":
                if (obj is Character unfriend && pet.IsFriendOf(unfriend.Uid))
                {
                    pet.RemoveFriend(unfriend);
                    SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargUnfriendSuccess1, unfriend.Name));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetTargUnfriendNotfriend));
                break;

            case "transfer":
                if (obj is Character newOwner && newOwner.IsPlayer)
                {
                    if (pet.IsSummoned)
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetTargTransferSummoned));
                    }
                    else if (pet.TryAssignOwnership(newOwner, newOwner, summoned: false, enforceFollowerCap: true))
                    {
                        pet.PetAIMode = PetAIMode.Follow;
                        SysMessage(ServerMessages.GetFormatted(Msg.NpcPetTargFriendSuccess2, newOwner.Name));
                    }
                    else
                    {
                        SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                    }
                }
                break;

            case "release":
                if (obj is Character releaseOwner && pet.HasOwner(releaseOwner.Uid))
                {
                    pet.ClearOwnership(clearFriends: true);
                    pet.PetAIMode = PetAIMode.Stay;
                    pet.RemoveTag("ATTACK_TARGET");
                    pet.RemoveTag("GUARD_TARGET");
                    pet.RemoveTag("FOLLOW_TARGET");
                    pet.RemoveTag("GO_TARGET");
                    SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                }
                else
                    SysMessage(ServerMessages.Get(Msg.NpcPetFailure));
                break;

            case "go":
                pet.SetTag("GO_TARGET", $"{x},{y},{z},{_character.MapIndex}");
                pet.PetAIMode = PetAIMode.Come;
                SysMessage(ServerMessages.Get(Msg.NpcPetSuccess));
                break;

            case "price":
                if (obj is Item priced)
                {
                    priced.SetTag("PRICE", priced.Price > 0 ? priced.Price.ToString() : "1");
                    SendInputPromptGump(priced, "PRICE", 9);
                }
                break;
        }
    }

    private Layer ResolveWearableLayer(Item item)
    {
        var itemDef = DefinitionLoader.GetItemDef(item.BaseId);
        Layer layer = itemDef?.Layer ?? Layer.None;
        if (layer == Layer.None && _world.MapData != null)
        {
            var tile = _world.MapData.GetItemTileData(item.BaseId);
            if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Wearable) != 0 &&
                tile.Quality > 0 && tile.Quality <= (byte)Layer.Horse)
            {
                layer = (Layer)tile.Quality;
            }
        }
        return layer;
    }

    private IEnumerable<Character> CollectCommandablePets(string? namePrefix = null)
    {
        if (_character == null)
            return Enumerable.Empty<Character>();

        return _world.GetCharsInRange(_character.Position, 12)
            .Where(p =>
                !p.IsPlayer &&
                !p.IsDead &&
                !p.IsDeleted &&
                !p.IsStatFlag(StatFlag.Ridden) &&
                p.CanAcceptPetCommandFrom(_character) &&
                (string.IsNullOrEmpty(namePrefix) ||
                 p.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)));
    }

    private void HandleVendorInteraction(Character vendor)
    {
        if (_character == null) return;

        // Build a buy/sell gump for the vendor
        var gump = new GumpBuilder(_character.Uid.Value, vendor.Uid.Value, 400, 300);
        gump.AddResizePic(0, 0, 5054, 400, 300);
        gump.AddText(30, 20, 0, vendor.GetName());
        gump.AddText(30, 50, 0, "How may I help you?");
        gump.AddButton(30, 100, 4005, 4007, 1);  // Buy
        gump.AddText(70, 100, 0, "Buy");
        gump.AddButton(30, 130, 4005, 4007, 2);  // Sell
        gump.AddText(70, 130, 0, "Sell");
        gump.AddButton(150, 250, 4017, 4019, 0); // Cancel

        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (buttonId == 1)
                SendVendorBuyList(vendor);
            else if (buttonId == 2)
                SendVendorSellList(vendor);
        });
    }

    /// <summary>
    /// Source-X CClient::Cmd_VendorBuy parity. Public entry used when the
    /// player triggers buy via speech ("vendor buy", "buy") or by clicking
    /// the buy gump button. Wraps the private packet-formatting helper so
    /// callers outside this client (e.g. NPC speech dispatch in Program.cs)
    /// don't need to poke private members.
    /// </summary>
    public void OpenVendorBuy(Character vendor) => SendVendorBuyList(vendor);

    /// <summary>
    /// Source-X CClient::Cmd_VendorSell parity. Public entry used when the
    /// player triggers sell via speech or via the vendor gump button.
    /// </summary>
    public void OpenVendorSell(Character vendor) => SendVendorSellList(vendor);

    /// <summary>Send the vendor's buy list (items available for purchase) to the client.</summary>
    private void SendVendorBuyList(Character vendor)
    {
        if (_character == null) return;

        // Auto-restock if needed (TAG.VENDORINV path — used by GM-set
        // inventory definitions).
        if (VendorEngine.NeedsRestock(vendor))
            VendorEngine.RestockVendor(vendor);

        // Source-X parity: vendors restock from their @NPCRestock
        // trigger (SELL=VENDOR_S_*, BUY=VENDOR_B_*) when their stock
        // pack is empty. The spawn-time hook fires this on freshly
        // spawned NPCs, but vendors that were loaded from a prior
        // world save never went through that path. Re-fire on demand
        // so legacy persisted vendors get a stock list as soon as a
        // player tries to buy from them.
        // Vendor's stock lives on LAYER_VENDOR_STOCK (26). ClassicUO's
        // BuyList handler hard-rejects any other layer (Backpack = 21
        // is silently dropped), so we MUST source / reference the
        // dedicated vendor stock container.
        var stockContainer = vendor.GetEquippedItem(Layer.VendorStock);
        if (stockContainer == null ||
            !_world.GetContainerContents(stockContainer.Uid).Any())
        {
            _triggerDispatcher?.FireCharTrigger(vendor,
                SphereNet.Core.Enums.CharTrigger.NPCRestock,
                new SphereNet.Game.Scripting.TriggerArgs { CharSrc = _character });
            // Refresh after restock — the trigger may have created it.
            stockContainer = vendor.GetEquippedItem(Layer.VendorStock);
        }

        // Collect vendor inventory items (items in vendor's "sell" container / buy pack)
        var vendorItems = GetVendorBuyInventory(vendor);
        if (vendorItems.Count == 0 || stockContainer == null)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_no_goods"));
            return;
        }

        // Source-X / RunUO order (CClient::addVendorBuy):
        //   1) 0x2E equip the vendor stock container at LAYER_VENDOR_STOCK
        //      (=ClassicUO Layer.ShopBuyRestock 0x1A) so the client knows
        //      the entity exists.
        //   2) 0x3C container contents — every item that the buy list will
        //      reference. The client uses these entries to look up
        //      itemId/hue/amount when drawing each row of the buy window;
        //      without it the rows are blank.
        //   3) 0x74 vendor buy list — prices + descriptions. ClassicUO's
        //      BuyList(0x74) handler ONLY decorates the items with prices
        //      and display names; it does NOT push them into the
        //      ShopGump's display list. (See ShopGump.Update —
        //      `if (_shopItems.Count == 0) Dispose()` will close the
        //      gump after one frame if nothing was added.)
        //   4) 0x24 OpenContainer with gumpId=0x0030 + VENDOR MOBILE serial
        //      — THIS is what actually opens and populates the buy gump.
        //      The client's OpenContainer handler iterates
        //      vendor.FindItemByLayer(Layer.ShopBuyRestock..ShopBuy) and
        //      calls ShopGump.AddItem for every child item. Skipping this
        //      step is exactly why our buy menu used to "vanish" — the
        //      gump did spawn briefly and then auto-disposed because
        //      `_shopItems` stayed empty.
        var buyPack = stockContainer;
        uint buyContainerSerial = buyPack.Uid.Value;

        // (0) PRE-SYNC the vendor stock container as a worn item.
        //     Equipping at LAYER_VENDOR_STOCK (26 == ClassicUO
        //     Layer.ShopBuyRestock 0x1A) is mandatory: ClassicUO's
        //     BuyList(0x74) handler explicitly checks
        //     `container.Layer == Layer.ShopBuyRestock || == Layer.ShopBuy`
        //     and silently bails out for any other layer (including
        //     Backpack = 0x15).
        _netState.Send(new PacketWornItem(
            buyPack.Uid.Value, buyPack.BaseId, (byte)Layer.VendorStock,
            vendor.Uid.Value, buyPack.Hue.Value));

        // (0b) ALSO equip a container at LAYER_VENDOR_EXTRA (27 ==
        //      ClassicUO Layer.ShopBuy 0x1B). ClassicUO's OpenContainer
        //      handler for gump 0x0030 unconditionally iterates BOTH
        //      ShopBuyRestock and ShopBuy layers and calls `item.Items`
        //      on each — without a NULL-check. If the second layer is
        //      empty, `vendor.FindItemByLayer(Layer.ShopBuy)` returns
        //      null and the client CRASHES with NullReferenceException
        //      the moment we send our 0x24 to open the buy gump.
        //      Source-X NPCs always have both stock containers (LAYER
        //      26 + LAYER 27) for exactly this reason; we lazily mint
        //      the second one here so legacy / freshly-spawned vendors
        //      don't crash the client.
        var extraContainer = vendor.GetEquippedItem(Layer.VendorExtra);
        if (extraContainer == null)
        {
            extraContainer = _world.CreateItem();
            extraContainer.BaseId = 0x408D; // i_vendor_box (Source-X stock graphic)
            vendor.Equip(extraContainer, Layer.VendorExtra);
        }
        _netState.Send(new PacketWornItem(
            extraContainer.Uid.Value, extraContainer.BaseId, (byte)Layer.VendorExtra,
            vendor.Uid.Value, extraContainer.Hue.Value));

        var contentEntries = new List<PacketContainerContents.Entry>(vendorItems.Count);
        for (int i = 0; i < vendorItems.Count; i++)
        {
            var vi = vendorItems[i];
            // Cascade items inside the buy pack so the client can render
            // distinct rows. Five-wide grid matches Source-X / RunUO layout.
            short x = (short)(20 + (i % 5) * 30);
            short y = (short)(20 + (i / 5) * 20);
            contentEntries.Add(new PacketContainerContents.Entry(
                vi.Serial, vi.ItemId, 0, vi.Amount,
                x, y, buyContainerSerial, vi.Hue, (byte)i));
        }
        _netState.Send(new PacketContainerContents(contentEntries, _netState.IsClientPost6017));
        _netState.Send(new PacketVendorBuyList(buyContainerSerial, vendorItems));

        // (4) Open the buy gump. ClassicUO's OpenContainer handler with
        //     gumpId=0x0030 walks vendor.FindItemByLayer(Layer.ShopBuyRestock
        //     .. Layer.ShopBuy), pulls every child item out, and calls
        //     ShopGump.AddItem. Without this packet, the gump that BuyList
        //     creates auto-disposes one frame later because its
        //     `_shopItems` dictionary is empty (see ShopGump.Update).
        //     Note: the serial here is the VENDOR MOBILE — not the
        //     container — because the handler does
        //     `World.Mobiles.Get(serial)`.
        _netState.Send(new PacketOpenContainer(vendor.Uid.Value, 0x0030,
            _netState.IsClientPost7090));
    }

    /// <summary>Send the sell list (items player can sell to this vendor) to the client.</summary>
    private void SendVendorSellList(Character vendor)
    {
        if (_character == null) return;

        var backpack = _character.Backpack;
        if (backpack == null)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nothing_buy"));
            return;
        }

        // Build list of items the vendor will buy from the player's backpack
        var sellItems = new List<VendorItem>();
        foreach (var item in _world.GetContainerContents(backpack.Uid))
        {
            if (item.ItemType == ItemType.Gold) continue; // don't sell gold
            if (item.IsDeleted) continue;

            int price = GetVendorItemSellPrice(vendor, item);
            if (price <= 0) continue;

            sellItems.Add(new VendorItem
            {
                Serial = item.Uid.Value,
                ItemId = item.DispIdFull,
                Hue = item.Hue.Value,
                Amount = (ushort)item.Amount,
                Price = price,
                Name = item.GetName()
            });

            if (sellItems.Count >= 50) break; // limit
        }

        if (sellItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nothing_buy"));
            return;
        }

        _netState.Send(new PacketVendorSellList(vendor.Uid.Value, sellItems));
    }

    /// <summary>
    /// Build vendor buy inventory from vendor's TAG.SELL entries or equipped buy-pack items.
    /// In Sphere, vendor inventory is defined in CHARDEF with item entries.
    /// </summary>
    private List<VendorItem> GetVendorBuyInventory(Character vendor)
    {
        var items = new List<VendorItem>();

        // Items live on LAYER_VENDOR_STOCK (Source-X parity). ClassicUO
        // BuyList(0x74) only accepts containers equipped at that layer
        // (or LAYER_VENDOR_EXTRA = 27) — Backpack-based stock is dropped.
        var vendorPack = vendor.GetEquippedItem(Layer.VendorStock)
                         ?? vendor.GetEquippedItem(Layer.VendorExtra);
        if (vendorPack != null)
        {
            foreach (var item in _world.GetContainerContents(vendorPack.Uid))
            {
                if (item.IsDeleted) continue;

                int price = GetVendorItemPrice(vendor, item);
                items.Add(new VendorItem
                {
                    Serial = item.Uid.Value,
                    ItemId = item.DispIdFull,
                    Hue = item.Hue.Value,
                    Amount = Math.Max((ushort)1, (ushort)item.Amount),
                    Price = price,
                    Name = item.GetName()
                });

                if (items.Count >= 50) break;
            }
        }

        return items;
    }

    // ==================== Crafting Gump ====================

    /// <summary>
    /// Open a crafting gump for the given skill.
    /// Lists available recipes and lets the player select one to craft.
    /// </summary>
    public void OpenCraftingGump(SkillType craftSkill)
    {
        if (_character == null || _craftingEngine == null) return;

        var recipes = _craftingEngine.GetRecipesBySkill(craftSkill);
        if (recipes.Count == 0)
        {
            SysMessage(ServerMessages.Get("craft_no_recipes"));
            return;
        }

        var gump = new GumpBuilder(_character.Uid.Value, 0, 530, 437);
        gump.AddResizePic(0, 0, 5054, 530, 437);
        gump.AddText(15, 15, 0, $"{craftSkill} Menu");

        // Page 0 — recipe list
        int y = 50;
        int buttonId = 100;
        foreach (var recipe in recipes)
        {
            if (y > 390) break;

            string name = string.IsNullOrEmpty(recipe.ResultName)
                ? $"Item 0x{recipe.ResultItemId:X4}"
                : recipe.ResultName;
            bool canMake = _craftingEngine.CanCraft(_character, recipe);
            int hue = canMake ? 0x0044 : 0x0020; // green vs red

            gump.AddButton(15, y, 4005, 4007, buttonId);
            gump.AddText(55, y, hue, name);

            // Show resource info
            if (recipe.Resources.Count > 0)
            {
                var resText = string.Join(", ", recipe.Resources.Select(r => $"{r.Amount}x 0x{r.ItemId:X4}"));
                gump.AddText(280, y, 0, resText);
            }

            y += 22;
            buttonId++;
        }

        // Cancel button
        gump.AddButton(15, 400, 4017, 4019, 0);
        gump.AddText(55, 400, 0, "Close");

        SendGump(gump, (pressedButton, switches, textEntries) =>
        {
            if (pressedButton >= 100)
            {
                int index = (int)(pressedButton - 100);
                if (index < recipes.Count)
                {
                    var recipe = recipes[index];

                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillMakeItem,
                        new TriggerArgs { CharSrc = _character, N1 = (int)craftSkill });

                    var result = _craftingEngine.TryCraft(_character, recipe);

                    if (result != null)
                    {
                        var pack = _character.Backpack;
                        if (pack != null)
                        {
                            var actual = pack.AddItemWithStack(result);
                            if (actual != result)
                                result.Delete();

                            _netState.Send(new PacketContainerItem(
                                actual.Uid.Value, actual.DispIdFull, 0,
                                actual.Amount, actual.X, actual.Y,
                                pack.Uid.Value, actual.Hue,
                                _netState.IsClientPost6017));

                            _triggerDispatcher?.FireItemTrigger(actual, ItemTrigger.Create,
                                new TriggerArgs { CharSrc = _character, ItemSrc = actual });
                        }
                        else
                        {
                            _world.PlaceItemWithDecay(result, _character.Position);
                            _triggerDispatcher?.FireItemTrigger(result, ItemTrigger.Create,
                                new TriggerArgs { CharSrc = _character, ItemSrc = result });
                        }
                        SysMessage(ServerMessages.GetFormatted("craft_success", result.GetName()));
                    }
                    else
                        SysMessage(ServerMessages.Get("craft_fail"));

                    // Re-open gump for continued crafting
                    OpenCraftingGump(craftSkill);
                }
            }
        });
    }

    /// <summary>Handle vendor buy packet (0x3B).</summary>
    public void HandleVendorBuy(uint vendorSerial, byte flag,
        List<SphereNet.Network.Packets.Incoming.VendorBuyEntry> buyItems)
    {
        if (_character == null) return;
        var vendor = _world.FindChar(new Serial(vendorSerial));
        if (vendor == null || vendor.NpcBrain != NpcBrainType.Vendor) return;

        if (flag == 0 || buyItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            return;
        }

        // Fire @Buy trigger on vendor NPC
        _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCAction,
            new TriggerArgs { CharSrc = _character, S1 = "BUY" });

        // Build trade entries from packet data
        var entries = new List<TradeEntry>();
        foreach (var bi in buyItems)
        {
            var item = _world.FindItem(new Serial(bi.ItemSerial));
            if (item == null) continue;

            int price = GetVendorItemPrice(vendor, item);
            entries.Add(new TradeEntry
            {
                ItemUid = item.Uid,
                ItemId = item.BaseId,
                Name = item.GetName(),
                Price = price,
                Amount = bi.Amount
            });
        }

        int result = VendorEngine.ProcessBuy(_character, vendor, entries);
        if (result < 0)
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_nomoney1"));
        else if (result == 0)
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
        else
            NpcSpeech(vendor, ServerMessages.GetFormatted("npc_vendor_b1", result, result == 1 ? "" : "s"));

        RefreshBackpackContents();
        SendCharacterStatus(_character);
    }

    /// <summary>Handle vendor sell packet (0x9F).</summary>
    public void HandleVendorSell(uint vendorSerial,
        List<SphereNet.Network.Packets.Incoming.VendorSellEntry> sellItems)
    {
        if (_character == null) return;
        var vendor = _world.FindChar(new Serial(vendorSerial));
        if (vendor == null || vendor.NpcBrain != NpcBrainType.Vendor) return;

        if (sellItems.Count == 0)
        {
            NpcSpeech(vendor, ServerMessages.Get("npc_vendor_ty"));
            return;
        }

        // Fire @Sell trigger on vendor NPC
        _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCAction,
            new TriggerArgs { CharSrc = _character, S1 = "SELL" });

        // Build trade entries from packet data
        var entries = new List<TradeEntry>();
        foreach (var si in sellItems)
        {
            var item = _world.FindItem(new Serial(si.ItemSerial));
            if (item == null) continue;

            int price = GetVendorItemSellPrice(vendor, item);
            entries.Add(new TradeEntry
            {
                ItemUid = item.Uid,
                ItemId = item.BaseId,
                Name = item.GetName(),
                Price = price,
                Amount = si.Amount
            });
        }

        int result = VendorEngine.ProcessSell(_character, vendor, entries);
        NpcSpeech(vendor, ServerMessages.GetFormatted("npc_vendor_sell_ty", result, result == 1 ? "" : "s"));
        RefreshBackpackContents();
        SendCharacterStatus(_character);
    }

    /// <summary>Get the buy price for an item from vendor inventory. Uses TAG.PRICE or defaults.</summary>
    private static int GetVendorItemPrice(Character vendor, Item item)
    {
        if (item.TryGetTag("PRICE", out string? priceStr) && int.TryParse(priceStr, out int price))
            return price;
        return Math.Max(1, item.BaseId / 10 + 5); // default price
    }

    /// <summary>Get the sell price (what vendor pays the player). Usually half of buy price.</summary>
    private static int GetVendorItemSellPrice(Character vendor, Item item)
    {
        return Math.Max(1, GetVendorItemPrice(vendor, item) / 2);
    }

    /// <summary>
    /// Handle secure trade packet (0x6F).
    /// Actions: 0=display, 1=close, 2=update (check/uncheck accept).
    /// </summary>
    public void HandleSecureTrade(byte action, uint containerSerial, uint param)
    {
        if (_character == null || _tradeManager == null) return;

        var trade = _tradeManager.FindByContainer(containerSerial);
        if (trade == null) return;

        switch (action)
        {
            case 1: // Cancel
                CancelTrade(trade);
                break;
            case 2: // Accept toggle
            {
                bool bothAccepted = trade.ToggleAccept(_character);
                SendTradeUpdateToBoth(trade);

                if (bothAccepted)
                    CompleteTrade(trade);
                break;
            }
        }
    }

    public void InitiateTrade(Character partner, Item? firstItem = null)
    {
        if (_character == null || _tradeManager == null) return;

        var existing = _tradeManager.FindTradeFor(_character);
        if (existing != null) { SysMessage("You are already trading."); return; }

        var partnerTrade = _tradeManager.FindTradeFor(partner);
        if (partnerTrade != null) { SysMessage("They are already trading."); return; }

        var cont1 = _world.CreateItem();
        cont1.BaseId = 0x1E5E;
        cont1.ItemType = Core.Enums.ItemType.Container;
        cont1.Name = "Trade Container";

        var cont2 = _world.CreateItem();
        cont2.BaseId = 0x1E5E;
        cont2.ItemType = Core.Enums.ItemType.Container;
        cont2.Name = "Trade Container";

        var trade = _tradeManager.StartTrade(_character, partner, cont1, cont2);

        _netState.Send(new PacketWorldItem(cont1.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
        _netState.Send(new PacketWorldItem(cont2.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
        _netState.Send(new PacketSecureTradeOpen(
            partner.Uid.Value, cont1.Uid.Value, cont2.Uid.Value, partner.GetName()));

        SendTradeToPartner?.Invoke(partner, _character, cont1, cont2);

        if (firstItem != null)
        {
            cont1.AddItem(firstItem);
            _netState.Send(new PacketContainerItem(
                firstItem.Uid.Value, firstItem.DispIdFull, 0,
                firstItem.Amount, 30, 30,
                cont1.Uid.Value, firstItem.Hue, _netState.IsClientPost6017));
            SendTradeItemToPartner?.Invoke(partner, firstItem, cont1);
        }
    }

    private void CancelTrade(SecureTrade trade)
    {
        var partner = trade.GetPartner(_character!);
        var myCont = trade.GetOwnContainer(_character!);
        var theirCont = trade.GetPartnerContainer(_character!);

        foreach (var item in _world.GetContainerContents(myCont.Uid).ToList())
            PlaceItemInPack(_character!, item);
        foreach (var item in _world.GetContainerContents(theirCont.Uid).ToList())
            PlaceItemInPack(partner, item);

        _netState.Send(new PacketSecureTradeClose(myCont.Uid.Value));
        SendTradeCloseToPartner?.Invoke(partner, theirCont.Uid.Value);

        trade.Cancel();
        _tradeManager!.EndTrade(trade);

        myCont.Delete();
        theirCont.Delete();
    }

    private void CompleteTrade(SecureTrade trade)
    {
        var initiator = trade.Initiator;
        var partner = trade.Partner;
        var cont1 = trade.InitiatorContainer;
        var cont2 = trade.PartnerContainer;

        foreach (var item in _world.GetContainerContents(cont1.Uid).ToList())
            PlaceItemInPack(partner, item);
        foreach (var item in _world.GetContainerContents(cont2.Uid).ToList())
            PlaceItemInPack(initiator, item);

        _netState.Send(new PacketSecureTradeClose(
            trade.GetOwnContainer(_character!).Uid.Value));
        SendTradeCloseToPartner?.Invoke(
            trade.GetPartner(_character!),
            trade.GetPartnerContainer(_character!).Uid.Value);

        trade.Complete();
        _tradeManager!.EndTrade(trade);

        cont1.Delete();
        cont2.Delete();

        SysMessage("Trade complete.");
        SendTradeMessageToPartner?.Invoke(trade.GetPartner(_character!), "Trade complete.");
    }

    private void SendTradeUpdateToBoth(SecureTrade trade)
    {
        var myCont = trade.GetOwnContainer(_character!);
        bool myAcc = _character == trade.Initiator ? trade.InitiatorAccepted : trade.PartnerAccepted;
        bool theirAcc = _character == trade.Initiator ? trade.PartnerAccepted : trade.InitiatorAccepted;
        _netState.Send(new PacketSecureTradeUpdate(myCont.Uid.Value, myAcc, theirAcc));

        var partner = trade.GetPartner(_character!);
        SendTradeUpdateToPartner?.Invoke(partner, trade);
    }

    public Action<Character, Character, Item, Item>? SendTradeToPartner { get; set; }
    public Action<Character, Item, Item>? SendTradeItemToPartner { get; set; }
    public Action<Character, uint>? SendTradeCloseToPartner { get; set; }
    public Action<Character, SecureTrade>? SendTradeUpdateToPartner { get; set; }
    public Action<Character, string>? SendTradeMessageToPartner { get; set; }

    /// <summary>Handle rename request (0x75).</summary>
    public void HandleRename(uint serial, string name)
    {
        if (_character == null) return;

        // Only GM+ can rename
        if (_character.PrivLevel < PrivLevel.GM)
        {
            SysMessage(ServerMessages.Get("rename_no_permission"));
            return;
        }

        var target = _world.FindChar(new Serial(serial));
        if (target != null)
        {
            string oldName = target.Name;
            target.Name = name.Trim();
            SysMessage(ServerMessages.GetFormatted("msg_rename_success", oldName, target.Name));
            return;
        }

        var item = _world.FindItem(new Serial(serial));
        if (item != null)
        {
            item.Name = name.Trim();
            SysMessage(ServerMessages.GetFormatted("rename_item_ok", item.Name));
        }
    }

    /// <summary>Handle client view range change (0xC8).</summary>
    public void HandleViewRange(byte range)
    {
        // Clamp to valid range (4-24)
        if (range < 4) range = 4;
        if (range > 24) range = 24;
        _netState.ViewRange = range;
    }

    /// <summary>Open guild stone gump with member list, options.</summary>
    private void OpenGuildStoneGump(Item stone)
    {
        if (_character == null || _guildManager == null) return;

        var guild = _guildManager.GetGuild(stone.Uid);
        if (guild == null)
        {
            // No guild on this stone yet — offer to create one
            var createGump = new GumpBuilder(_character.Uid.Value, stone.Uid.Value, 400, 300);
            createGump.AddResizePic(0, 0, 5054, 400, 300);
            createGump.AddText(30, 20, 0, "Guild Stone");
            createGump.AddText(30, 50, 0, "No guild is registered to this stone.");
            createGump.AddText(30, 80, 0, "Create a new guild?");
            createGump.AddButton(30, 130, 4005, 4007, 1); // Create
            createGump.AddText(70, 130, 0, "Create Guild");
            createGump.AddButton(150, 250, 4017, 4019, 0); // Cancel

            SendGump(createGump, (buttonId, switches, textEntries) =>
            {
                if (buttonId == 1)
                {
                    var newGuild = _guildManager.CreateGuild(stone.Uid, $"{_character.Name}'s Guild", _character.Uid);
                    SysMessage(ServerMessages.GetFormatted("guild_created", newGuild.Name));
                }
            });
            return;
        }

        // Show guild info gump
        var gump = new GumpBuilder(_character.Uid.Value, stone.Uid.Value, 500, 520);
        gump.AddResizePic(0, 0, 5054, 500, 520);
        gump.AddText(30, 10, 0, $"Guild: {guild.Name}");
        gump.AddText(30, 30, 0, $"Abbreviation: [{guild.Abbreviation}]");
        if (!string.IsNullOrEmpty(guild.Charter))
            gump.AddText(30, 50, 0, $"Charter: {guild.Charter}");
        gump.AddText(30, 70, 0, $"Members: {guild.MemberCount} | Wars: {guild.Wars.Count()} | Allies: {guild.Allies.Count()}");

        // Member list with titles and candidate status
        int y = 100;
        int memberIdx = 0;
        foreach (var member in guild.Members)
        {
            var ch = _world.FindChar(member.CharUid);
            string memberName = ch?.Name ?? $"UID 0x{member.CharUid.Value:X}";
            string privText = member.Priv switch
            {
                GuildPriv.Master => " [Master]",
                GuildPriv.Candidate => " [Candidate]",
                _ => ""
            };
            string titleText = !string.IsNullOrEmpty(member.Title) ? $" ({member.Title})" : "";
            int hue = member.Priv == GuildPriv.Candidate ? 33 : 0; // yellow for candidates
            gump.AddText(50, y, hue, $"{memberName}{privText}{titleText}");
            y += 20;
            memberIdx++;
            if (y > 350) break;
        }

        var myMember = guild.FindMember(_character.Uid);
        int btnY = 370;

        if (myMember == null)
        {
            gump.AddButton(30, btnY, 4005, 4007, 1); // Join
            gump.AddText(70, btnY, 0, "Request to Join");
            btnY += 25;
        }
        else if (myMember.Priv == GuildPriv.Master)
        {
            gump.AddButton(30, btnY, 4005, 4007, 2); // Disband
            gump.AddText(70, btnY, 0, "Disband Guild");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 10); // Accept candidates
            gump.AddText(70, btnY, 0, "Accept Candidate");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 11); // Set title
            gump.AddText(70, btnY, 0, "Set Member Title");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 12); // Declare war
            gump.AddText(70, btnY, 0, "Declare War");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 13); // Declare peace
            gump.AddText(70, btnY, 0, "Declare Peace");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 14); // Set charter
            gump.AddText(70, btnY, 0, "Set Charter");
            gump.AddTextEntry(170, btnY, 250, 20, 0, 1, guild.Charter);
            btnY += 25;
        }
        else
        {
            gump.AddButton(30, btnY, 4005, 4007, 3); // Leave
            gump.AddText(70, btnY, 0, "Leave Guild");
            btnY += 25;
        }
        gump.AddButton(350, 480, 4017, 4019, 0); // Close

        var capturedGuild = guild;
        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            HandleGuildGumpResponse(stone, capturedGuild, buttonId, textEntries);
        });
    }

    private void HandleGuildGumpResponse(Item stone, GuildDef guild, uint buttonId, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null || _guildManager == null) return;

        switch (buttonId)
        {
            case 1: // Join request
                guild.AddRecruit(_character.Uid);
                SysMessage(ServerMessages.Get("guild_join_request"));
                break;
            case 2: // Disband
                _guildManager.RemoveGuild(stone.Uid);
                SysMessage(ServerMessages.Get("guild_disbanded"));
                break;
            case 3: // Leave
                guild.RemoveMember(_character.Uid);
                SysMessage(ServerMessages.Get("guild_left"));
                break;
            case 10: // Accept candidate
                SysMessage(ServerMessages.Get("guild_target_candidate"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (guild.AcceptMember(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("guild_member_added", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("guild_not_candidate", target.Name));
                });
                break;
            case 11: // Set member title
                SysMessage(ServerMessages.Get("guild_target_title"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var member = guild.FindMember(target.Uid);
                    if (member == null) { SysMessage(ServerMessages.Get("guild_not_member")); return; }
                    // Use text entry if provided
                    var titleEntry = textEntries.FirstOrDefault(e => e.Id == 1);
                    if (!string.IsNullOrWhiteSpace(titleEntry.Text))
                    {
                        member.Title = titleEntry.Text.Trim();
                        SysMessage(ServerMessages.GetFormatted("guild_title_set", target.Name, member.Title));
                    }
                    else
                        SysMessage(ServerMessages.Get("guild_no_title"));
                });
                break;
            case 12: // Declare war
                SysMessage(ServerMessages.Get("guild_target_enemy"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    var enemyGuild = _guildManager.GetGuild(targetItem.Uid);
                    if (enemyGuild == null) { SysMessage(ServerMessages.Get("guild_not_stone")); return; }
                    guild.DeclareWar(targetItem.Uid);
                    SysMessage(ServerMessages.GetFormatted("guild_war_declared", enemyGuild.Name));
                });
                break;
            case 13: // Declare peace
                SysMessage(ServerMessages.Get("guild_target_peace"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetItem = _world.FindItem(new Serial(serial));
                    if (targetItem == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    guild.DeclarePeace(targetItem.Uid);
                    SysMessage(ServerMessages.Get("guild_peace_declared"));
                });
                break;
            case 14: // Set charter
            {
                var charterEntry = textEntries.FirstOrDefault(e => e.Id == 1);
                if (!string.IsNullOrWhiteSpace(charterEntry.Text))
                {
                    guild.Charter = charterEntry.Text.Trim();
                    SysMessage(ServerMessages.Get("guild_charter_updated"));
                }
                break;
            }
        }
    }

    /// <summary>Open house management gump from house sign or multi item.</summary>
    private void OpenHouseSignGump(Item signOrMulti)
    {
        if (_character == null || _housingEngine == null) return;

        // Find the house — could be the multi item itself or linked via tag
        var house = _housingEngine.GetHouse(signOrMulti.Uid);
        if (house == null && signOrMulti.TryGetTag("HOUSE_UID", out string? houseUidStr) &&
            uint.TryParse(houseUidStr, out uint houseUid))
        {
            house = _housingEngine.GetHouse(new Serial(houseUid));
        }

        if (house == null)
        {
            SysMessage(ServerMessages.Get("house_not_house"));
            return;
        }

        // Auto-refresh on owner visit
        _housingEngine.OnCharacterEnterHouse(_character, house);

        var priv = house.GetPriv(_character.Uid);
        var ownerCh = _world.FindChar(house.Owner);
        string ownerName = ownerCh?.Name ?? "Unknown";

        var gump = new GumpBuilder(_character.Uid.Value, signOrMulti.Uid.Value, 420, 440);
        gump.AddResizePic(0, 0, 5054, 420, 440);
        gump.AddText(30, 10, 0, "House Management");
        gump.AddText(30, 35, 0, $"Owner: {ownerName}");
        gump.AddText(30, 55, 0, $"Type: {house.Type}");
        gump.AddText(30, 75, 0, $"Storage: {house.Lockdowns.Count}/{house.MaxLockdowns} lockdowns, {house.SecureContainers.Count}/{house.MaxSecure} secure");
        gump.AddText(30, 95, 0, $"Condition: {house.DecayStage}");
        gump.AddText(30, 115, 0, $"Co-Owners: {house.CoOwners.Count}  Friends: {house.Friends.Count}");

        int btnY = 145;
        if (priv is HousePriv.Owner or HousePriv.CoOwner)
        {
            gump.AddButton(30, btnY, 4005, 4007, 1);
            gump.AddText(70, btnY, 0, "Transfer House");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 2);
            gump.AddText(70, btnY, 0, "Demolish House");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 10);
            gump.AddText(70, btnY, 0, "Add Co-Owner");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 11);
            gump.AddText(70, btnY, 0, "Add Friend");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 12);
            gump.AddText(70, btnY, 0, "Remove Co-Owner");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 13);
            gump.AddText(70, btnY, 0, "Remove Friend");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 14);
            gump.AddText(70, btnY, 0, "Lock Down Item");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 15);
            gump.AddText(70, btnY, 0, "Release Lockdown");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 16);
            gump.AddText(70, btnY, 0, "Secure Container");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 17);
            gump.AddText(70, btnY, 0, "Release Secure");
            btnY += 25;
        }
        if (priv == HousePriv.Owner)
        {
            gump.AddButton(30, btnY, 4005, 4007, 20);
            gump.AddText(70, btnY, 0, "Ban Player");
            btnY += 25;
            gump.AddButton(30, btnY, 4005, 4007, 21);
            gump.AddText(70, btnY, 0, "Unban Player");
            btnY += 25;
        }
        if (priv != HousePriv.None && priv != HousePriv.Ban)
        {
            gump.AddButton(30, btnY, 4005, 4007, 3);
            gump.AddText(70, btnY, 0, "Open Door");
            btnY += 25;
        }
        gump.AddButton(280, 400, 4017, 4019, 0); // Close

        var capturedHouse = house;
        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            HandleHouseGumpResponse(signOrMulti, capturedHouse, buttonId);
        });
    }

    private void HandleHouseGumpResponse(Item signOrMulti, House house, uint buttonId)
    {
        if (_character == null || _housingEngine == null) return;

        switch (buttonId)
        {
            case 1: // Transfer — target the new owner
                SysMessage(ServerMessages.Get("house_select_owner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer)
                    {
                        SysMessage(ServerMessages.Get("msg_invalid_target"));
                        return;
                    }
                    house.TransferOwnership(target.Uid);
                    SysMessage(ServerMessages.GetFormatted("house_transferred", target.Name));
                });
                break;
            case 2: // Demolish
                var deed = _housingEngine.RemoveHouse(signOrMulti.Uid, _character);
                if (deed != null)
                    SysMessage(ServerMessages.Get("house_demolished"));
                else
                    SysMessage(ServerMessages.Get("house_cant_demolish"));
                break;
            case 3: // Open door
                SysMessage(ServerMessages.Get("house_door_opened"));
                break;
            case 10: // Add Co-Owner
                SysMessage(ServerMessages.Get("house_add_coowner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddCoOwner(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_added_coowner", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_coowner", target.Name));
                });
                break;
            case 11: // Add Friend
                SysMessage(ServerMessages.Get("house_add_friend"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddFriend(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_added_friend", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_friend", target.Name));
                });
                break;
            case 12: // Remove Co-Owner
                SysMessage(ServerMessages.Get("house_remove_coowner"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveCoOwner(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_removed_coowner", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_coowner"));
                });
                break;
            case 13: // Remove Friend
                SysMessage(ServerMessages.Get("house_remove_friend"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveFriend(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_removed_friend", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_friend"));
                });
                break;
            case 14: // Lock Down Item
                SysMessage(ServerMessages.Get("house_lockdown"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetUid = new Serial(serial);
                    if (house.Lockdown(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_lockdown_ok"));
                    else
                        SysMessage(ServerMessages.Get("house_lockdown_fail"));
                });
                break;
            case 15: // Release Lockdown
                SysMessage(ServerMessages.Get("house_lockdown_release"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetUid = new Serial(serial);
                    if (house.ReleaseLockdown(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_lockdown_released"));
                    else
                        SysMessage(ServerMessages.Get("house_lockdown_not"));
                });
                break;
            case 16: // Secure Container
                SysMessage(ServerMessages.Get("house_secure"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetUid = new Serial(serial);
                    if (house.SecureContainer(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_secure_ok"));
                    else
                        SysMessage(ServerMessages.Get("house_secure_fail"));
                });
                break;
            case 17: // Release Secure
                SysMessage(ServerMessages.Get("house_secure_release"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var targetUid = new Serial(serial);
                    if (house.ReleaseSecure(targetUid, _character.Uid))
                        SysMessage(ServerMessages.Get("house_secure_released"));
                    else
                        SysMessage(ServerMessages.Get("house_secure_not"));
                });
                break;
            case 20: // Ban
                SysMessage(ServerMessages.Get("house_ban"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.AddBan(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_banned", target.Name));
                    else
                        SysMessage(ServerMessages.GetFormatted("house_already_banned", target.Name));
                });
                break;
            case 21: // Unban
                SysMessage(ServerMessages.Get("house_unban"));
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    var target = _world.FindChar(new Serial(serial));
                    if (target == null) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }
                    if (house.RemoveBan(target.Uid))
                        SysMessage(ServerMessages.GetFormatted("house_unbanned", target.Name));
                    else
                        SysMessage(ServerMessages.Get("house_not_banned"));
                });
                break;
        }
    }


    public void OpenDoor()
    {
        if (_character == null) return;
        foreach (var item in _world.GetItemsInRange(_character.Position, 2))
        {
            if (item.ItemType == ItemType.Door || item.ItemType == ItemType.DoorLocked ||
                item.ItemType == ItemType.Portculis || item.ItemType == ItemType.PortLocked)
            {
                ToggleDoor(item);
                return;
            }
        }
    }

    private void ToggleDoor(Item door)
    {
        if (_character == null) return;

        // Door art IDs toggle between open/closed variants (±1 or ±2 offset)
        bool isOpen = door.TryGetTag("DOOR_OPEN", out string? openStr) && openStr == "1";

        if (isOpen)
        {
            door.BaseId = (ushort)(door.BaseId - 1);
            door.RemoveTag("DOOR_OPEN");
        }
        else
        {
            door.BaseId = (ushort)(door.BaseId + 1);
            door.SetTag("DOOR_OPEN", "1");
        }

        // Play door sound and broadcast updated item to nearby clients
        ushort soundId = (ushort)(isOpen ? 0x00F1 : 0x00EA); // close/open sounds
        var soundPacket = new PacketSound(soundId, door.X, door.Y, door.Z);
        BroadcastNearby?.Invoke(door.Position, UpdateRange, soundPacket, 0);

        var itemPacket = new PacketWorldItem(
            door.Uid.Value, door.DispIdFull, door.Amount,
            door.X, door.Y, door.Z, door.Hue);
        BroadcastNearby?.Invoke(door.Position, UpdateRange, itemPacket, 0);
    }

    private void UsePotion(Item potion)
    {
        if (_character == null) return;

        // Determine potion effect from BaseId ranges
        // Common UO potion base IDs: 0x0F06-0x0F0D heal, 0x0F07 cure, 0x0F0B refresh etc.
        string potionType = "heal"; // default
        if (potion.TryGetTag("POTION_TYPE", out string? pType) && pType != null)
            potionType = pType.ToLowerInvariant();

        switch (potionType)
        {
            case "heal":
            case "greatheal":
                int healAmount = potionType == "greatheal" ? 20 : 10;
                _character.Hits = (short)Math.Min(_character.Hits + healAmount, _character.MaxHits);
                SysMessage(ServerMessages.GetFormatted("potion_heal", healAmount));
                break;
            case "cure":
                _character.ClearStatFlag(StatFlag.Poisoned);
                SysMessage(ServerMessages.Get("potion_cured"));
                break;
            case "refresh":
            case "totalrefresh":
                int stamAmount = potionType == "totalrefresh" ? 60 : 25;
                _character.Stam = (short)Math.Min(_character.Stam + stamAmount, _character.MaxStam);
                SysMessage(ServerMessages.GetFormatted("potion_stamina", stamAmount));
                break;
            case "strength":
                _character.Str += 10;
                SysMessage(ServerMessages.Get("potion_str"));
                break;
            case "agility":
                _character.Dex += 10;
                SysMessage(ServerMessages.Get("potion_dex"));
                break;
            default:
                SysMessage(ServerMessages.Get("potion_drink"));
                break;
        }

        // Play drink sound
        var soundPacket = new PacketSound(0x0031, _character.X, _character.Y, _character.Z);
        _netState.Send(soundPacket);

        // Update stats
        SendCharacterStatus(_character);

        // Consume potion. Source-X parity: @Destroy RETURN 1 keeps the bottle.
        if (_triggerDispatcher?.FireItemTrigger(potion, ItemTrigger.Destroy,
                new TriggerArgs { CharSrc = _character, ItemSrc = potion }) != TriggerResult.True)
        {
            potion.Delete();
        }
    }

    /// <summary>Handle UseSkill request (from packet 0x12 or extended command).</summary>
    public void HandleUseSkill(int skillId)
    {
        if (_character == null || _character.IsDead) return;
        if (skillId < 0 || skillId >= (int)SkillType.Qty) return;

        var skill = (SkillType)skillId;

        // Source-X parity: information skills prompt for a target before emitting
        // any message. Route through the info-skill pipeline in BeginInfoSkill so
        // the player sees the exact CClientTarg.cpp text sequence.
        if (SkillHandlers.IsInfoSkill(skill))
        {
            BeginInfoSkill(skill, skillId);
            return;
        }

        // Active skills with parity coverage in ActiveSkillEngine.
        var activeKind = SkillHandlers.GetActiveSkillTarget(skill);
        if (activeKind != SkillHandlers.ActiveSkillTargetKind.Unsupported)
        {
            BeginActiveSkill(skill, skillId, activeKind);
            return;
        }

        // Fire @SkillPreStart — if script blocks, don't use skill
        if (_triggerDispatcher != null)
        {
            var preResult = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (preResult == TriggerResult.True)
                return;
        }

        // Fire @SkillStart — if script blocks, don't use skill
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (result == TriggerResult.True)
                return;
        }

        // Fire @SkillStroke — the main action moment
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
            new TriggerArgs { CharSrc = _character, N1 = skillId });

        bool success = _skillHandlers?.UseSkill(_character, skill) ?? false;

        // Fire @SkillSuccess or @SkillFail
        if (_triggerDispatcher != null)
        {
            var trigger = success ? CharTrigger.SkillSuccess : CharTrigger.SkillFail;
            _triggerDispatcher.FireCharTrigger(_character, trigger,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
        }

        if (success)
            SysMessage(ServerMessages.GetFormatted("skill_use_ok", skill));
        else
            SysMessage(ServerMessages.GetFormatted("skill_use_fail", skill));
    }

    /// <summary>Handle extended command (0xBF sub-commands).</summary>
    public void HandleExtendedCommand(ushort subCmd, byte[] data)
    {
        switch (subCmd)
        {
            case 0x001A: // stat lock change
                if (data.Length >= 2 && _character != null)
                {
                    byte stat = data[0];
                    byte lockVal = data[1];
                    // stat: 0=str, 1=dex, 2=int — store as tags
                    _character.SetTag($"STATLOCK_{stat}", lockVal.ToString());
                }
                break;
            case 0x0013: // context menu request
                if (data.Length >= 4)
                {
                    uint targetSerial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                    SendContextMenu(targetSerial);
                }
                break;
            case 0x0015: // context menu response
                if (data.Length >= 6)
                {
                    uint respSerial = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                    ushort entryTag = (ushort)((data[4] << 8) | data[5]);
                    HandleContextMenuResponse(respSerial, entryTag);
                }
                break;
            case 0x0006: // party commands
                if (data.Length >= 1)
                    HandlePartyCommand(data);
                break;
            case 0x0024: // unknown / unused in most clients
                break;
            case 0x000B: // Chat button on paperdoll — client requests chat window
                if (_character != null)
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserChatButton,
                        new TriggerArgs { CharSrc = _character });
                }
                break;
            case 0x0028: // Guild button on paperdoll
                if (_character != null)
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserGuildButton,
                        new TriggerArgs { CharSrc = _character });
                }
                break;
            case 0x0032: // Quest button on paperdoll
                if (_character != null)
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserQuestButton,
                        new TriggerArgs { CharSrc = _character });
                }
                break;
            case 0x002C: // Invoke virtue — client passes virtue id in data[0]
                if (_character != null && data.Length >= 1)
                {
                    _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserVirtueInvoke,
                        new TriggerArgs { CharSrc = _character, N1 = data[0] });
                }
                break;
        }
    }

    /// <summary>
    /// Handle party sub-commands (0xBF sub 0x0006).
    /// Sub-types: 1=Add, 2=Remove, 3=PrivateMsg, 4=PublicMsg, 6=SetLoot, 8=Accept, 9=Decline.
    /// </summary>
    private void HandlePartyCommand(byte[] data)
    {
        if (_character == null || _partyManager == null) return;
        byte partyCmd = data[0];

        switch (partyCmd)
        {
            case 1: // Add member (invite)
                if (data.Length >= 5)
                {
                    uint targetUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    var target = _world.FindChar(new Serial(targetUid));
                    if (target == null || !target.IsPlayer) { SysMessage(ServerMessages.Get("msg_invalid_target")); return; }

                    var existingParty = _partyManager.FindParty(_character.Uid);
                    if (existingParty != null && existingParty.IsFull) { SysMessage(ServerMessages.Get("party_is_full")); return; }

                    // Fire @PartyInvite trigger on target
                    _triggerDispatcher?.FireCharTrigger(target, CharTrigger.PartyInvite,
                        new TriggerArgs { CharSrc = _character });

                    // Store pending invite and send invite packet to target
                    target.SetTag("PARTY_INVITE_FROM", _character.Uid.Value.ToString());
                    SendToChar?.Invoke(target.Uid, new PacketPartyInvitation(_character.Uid.Value));
                    SysMessage(ServerMessages.GetFormatted("party_invite", target.Name));
                }
                break;

            case 2: // Remove member
                if (data.Length >= 5)
                {
                    uint removeUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party == null) break;
                    if (party.Master != _character.Uid && new Serial(removeUid) != _character.Uid)
                    {
                        SysMessage(ServerMessages.Get("party_notleader"));
                        break;
                    }
                    // Fire @PartyRemove trigger on removed member
                    var removedChar = _world.FindChar(new Serial(removeUid));
                    if (removedChar != null)
                        _triggerDispatcher?.FireCharTrigger(removedChar, CharTrigger.PartyRemove,
                            new TriggerArgs { CharSrc = _character });

                    _partyManager.Leave(new Serial(removeUid));
                    SysMessage(ServerMessages.Get("party_leave_1"));
                    BroadcastPartyUpdate(party, new Serial(removeUid));
                }
                break;

            case 3: // Private party message
                if (data.Length >= 5)
                {
                    uint pmTargetUid = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                    string pmMsg = data.Length > 5
                        ? System.Text.Encoding.BigEndianUnicode.GetString(data, 5, data.Length - 5).TrimEnd('\0')
                        : "";
                    if (!string.IsNullOrEmpty(pmMsg))
                    {
                        SendToChar?.Invoke(new Serial(pmTargetUid),
                            new PacketPartyMessage(_character.Uid.Value, pmMsg, isPrivate: true));
                        SysMessage(ServerMessages.GetFormatted("party_msg", $"{pmTargetUid:X}", pmMsg));
                    }
                }
                break;

            case 4: // Public party message
                if (data.Length >= 2)
                {
                    string msg = System.Text.Encoding.BigEndianUnicode.GetString(data, 1, data.Length - 1).TrimEnd('\0');
                    if (string.IsNullOrWhiteSpace(msg)) break;
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party != null)
                    {
                        var chatPacket = new PacketPartyMessage(_character.Uid.Value, msg);
                        foreach (var memberUid in party.Members)
                            SendToChar?.Invoke(memberUid, chatPacket);
                    }
                }
                break;

            case 6: // Set loot flag
                if (data.Length >= 2)
                {
                    bool canLoot = data[1] != 0;
                    var party = _partyManager.FindParty(_character.Uid);
                    party?.SetLootFlag(_character.Uid, canLoot);
                    SysMessage(canLoot ? "Party loot sharing enabled." : "Party loot sharing disabled.");
                }
                break;

            case 8: // Accept invite
            {
                if (_character.TryGetTag("PARTY_INVITE_FROM", out string? inviterStr) &&
                    uint.TryParse(inviterStr, out uint inviterUid))
                {
                    _partyManager.AcceptInvite(new Serial(inviterUid), _character.Uid);
                    _character.RemoveTag("PARTY_INVITE_FROM");
                    SysMessage(ServerMessages.Get("party_added"));
                    var party = _partyManager.FindParty(_character.Uid);
                    if (party != null) BroadcastPartyUpdate(party);
                }
                break;
            }

            case 9: // Decline invite
            {
                if (_character.TryGetTag("PARTY_INVITE_FROM", out string? declineInviterStr) &&
                    uint.TryParse(declineInviterStr, out uint declineInviterUid))
                {
                    SendToChar?.Invoke(new Serial(declineInviterUid), null!); // notify inviter
                }
                _character.RemoveTag("PARTY_INVITE_FROM");
                SysMessage(ServerMessages.Get("party_decline_2"));
                break;
            }
        }
    }

    /// <summary>Send party member list update to all members.</summary>
    private void BroadcastPartyUpdate(PartyDef party, Serial? removedMember = null)
    {
        var memberSerials = party.Members.Select(m => m.Value).ToArray();
        if (removedMember.HasValue)
        {
            var removePacket = new PacketPartyRemoveMember(removedMember.Value.Value, memberSerials);
            foreach (var memberUid in party.Members)
                SendToChar?.Invoke(memberUid, removePacket);
            SendToChar?.Invoke(removedMember.Value, removePacket);
        }
        else
        {
            var listPacket = new PacketPartyMemberList(memberSerials);
            foreach (var memberUid in party.Members)
                SendToChar?.Invoke(memberUid, listPacket);
        }
    }

    private void SendContextMenu(uint targetSerial)
    {
        if (_character == null) return;

        var entries = new List<(ushort EntryTag, uint ClilocId, ushort Flags)>();

        var ch = _world.FindChar(new Serial(targetSerial));
        if (ch != null)
        {
            entries.Add((1, 3006123, 0)); // Open Paperdoll
            if (ch == _character)
            {
                entries.Add((2, 3006145, 0)); // Open Backpack
            }
            if (!ch.IsPlayer && ch.NpcBrain == NpcBrainType.Vendor)
            {
                entries.Add((3, 3006103, 0)); // Buy
                entries.Add((4, 3006106, 0)); // Sell
            }
            if (!ch.IsPlayer && ch.NpcBrain == NpcBrainType.Banker)
            {
                entries.Add((5, 3006105, 0)); // Open Bankbox
            }
            // Mount / Dismount: exposed as a context-menu action so the client
            // does not require a DoubleClick to saddle. Double-click remains
            // equivalent. Entry is filtered by IsMountable so non-ridable
            // mobs (monsters, humans) don't get a useless "Mount Me" line.
            if (!ch.IsPlayer && ch != _character &&
                Mounts.MountEngine.IsMountable(ch.BodyId))
            {
                entries.Add((6, 3006155, 0)); // Mount Me
            }
            if (ch == _character && _character.IsMounted)
            {
                entries.Add((7, 3006112, 0)); // Dismount
            }
        }

        if (entries.Count > 0)
            _netState.Send(new PacketContextMenu(targetSerial, entries.ToArray()));
    }

    private void HandleContextMenuResponse(uint targetSerial, ushort entryTag)
    {
        if (_character == null) return;

        switch (entryTag)
        {
            case 1: // Open Paperdoll
                var ch = _world.FindChar(new Serial(targetSerial));
                if (ch != null) SendPaperdoll(ch);
                break;
            case 2: // Open Backpack
                if (_character.Backpack != null)
                    SendOpenContainer(_character.Backpack);
                break;
            case 3: // Buy
                var vendor = _world.FindChar(new Serial(targetSerial));
                if (vendor != null) HandleVendorInteraction(vendor);
                break;
            case 4: // Sell
                SysMessage(ServerMessages.Get("vendor_what_sell"));
                break;
            case 5: // Open Bankbox
                SysMessage(ServerMessages.Get("vendor_bank_unavailable"));
                break;
            case 6: // Mount Me
                HandleDoubleClick(targetSerial);
                break;
            case 7: // Dismount
                _mountEngine?.Dismount(_character);
                break;
        }
    }

    // ==================== Single Click ====================

    public void HandleSingleClick(uint uid)
    {
        if (_character == null) return;

        var obj = _world.FindObject(new Serial(uid));
        if (obj == null) return;

        // Fire @Click trigger
        if (_triggerDispatcher != null)
        {
            if (obj is Character clickCh)
            {
                var result = _triggerDispatcher.FireCharTrigger(clickCh, CharTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ScriptConsole = this });
                if (result == TriggerResult.True)
                    return;
            }
            else if (obj is Item clickItem)
            {
                var result = _triggerDispatcher.FireItemTrigger(clickItem, ItemTrigger.Click,
                    new TriggerArgs { CharSrc = _character, ItemSrc = clickItem, ScriptConsole = this });
                if (result == TriggerResult.True)
                    return;
            }
        }

        // Overhead name: for characters, the hue follows notoriety so the
        // label reads blue/green/grey/orange/red/yellow. Items stay grey.
        ushort nameHue = 0x03B2;
        if (obj is Character labelCh)
            nameHue = NotoToHue(GetNotoriety(labelCh));
        _netState.Send(new PacketSpeechUnicodeOut(
            uid, (ushort)(obj is Character c ? c.BodyId : 0),
            6, nameHue, 3, "TRK", "", obj.GetName()));
    }

    /// <summary>Convert a notoriety byte (1-7) to the hue used for
    /// overhead labels and system speech. Values mirror Source-X
    /// CServerConfig::m_iColorNoto* defaults:
    /// good/innocent=0x59 blue, guild-same=0x3f green, neutral=0x3b2 grey,
    /// criminal=0x3b2 grey, guild-war=0x90 orange, evil/murderer=0x22 red,
    /// invul=0x35 yellow.</summary>
    private static ushort NotoToHue(byte noto) => noto switch
    {
        1 => 0x0059, // innocent / blue
        2 => 0x003F, // friend (party/guild-ally) / green
        4 => 0x03B2, // criminal / grey
        5 => 0x0090, // enemy guild / orange
        6 => 0x0022, // murderer / red
        7 => 0x0035, // invulnerable / yellow
        _ => 0x03B2, // neutral / grey (NPC default)
    };

    // ==================== Item Pick Up ====================

    public void HandleItemPickup(uint serial, ushort amount)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null)
        {
            SendPickupFailed(5); // doesn't exist
            return;
        }

        // Fire @Pickup trigger
        if (_triggerDispatcher != null)
        {
            var trigger = item.ContainedIn.IsValid ? ItemTrigger.PickupPack : ItemTrigger.PickupGround;
            var result = _triggerDispatcher.FireItemTrigger(item, trigger,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            if (result == TriggerResult.True)
            {
                SendPickupFailed(1);
                return;
            }
        }

        int dist = _character.Position.GetDistanceTo(item.Position);
        if (dist > 3 && !item.ContainedIn.IsValid && _character.PrivLevel < PrivLevel.GM)
        {
            SendPickupFailed(4); // too far away
            return;
        }

        if (item.IsEquipped)
        {
            var owner = _world.FindChar(item.ContainedIn);
            if (owner != null && owner != _character && _character.PrivLevel < PrivLevel.GM)
            {
                SendPickupFailed(1); // cannot pick up
                return;
            }
            // Fire @Unequip trigger on the item being removed
            if (_triggerDispatcher != null && owner != null)
            {
                var unequipResult = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.Unequip,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item });
                if (unequipResult == TriggerResult.True)
                {
                    SendPickupFailed(1);
                    return;
                }
            }
            owner?.Unequip(item.EquipLayer);
        }
        else if (item.ContainedIn.IsValid)
        {
            var container = _world.FindItem(item.ContainedIn);
            container?.RemoveItem(item);
        }
        else
        {
            var sector = _world.GetSector(item.Position);
            sector?.RemoveItem(item);
        }

        item.ContainedIn = _character.Uid;
        _character.SetTag("DRAGGING", serial.ToString());

        if (item.BaseId == 0x0EED)
            SendCharacterStatus(_character);
    }

    // ==================== Item Drop ====================

    public void HandleItemDrop(uint serial, short x, short y, sbyte z, uint containerUid)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        _character.RemoveTag("DRAGGING");

        if (containerUid != 0 && containerUid != 0xFFFFFFFF)
        {
            var container = _world.FindItem(new Serial(containerUid));
            if (container != null && _tradeManager?.FindByContainer(containerUid) is { } dropTrade)
            {
                if (!dropTrade.IsParticipant(_character))
                {
                    PlaceItemInPack(_character, item);
                    _netState.Send(new PacketDropReject());
                    return;
                }
                var myCont = dropTrade.GetOwnContainer(_character);
                myCont.AddItem(item);
                item.Position = new Point3D(30, 30, 0, _character.MapIndex);
                dropTrade.ResetAcceptance();
                SendTradeUpdateToBoth(dropTrade);
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, 30, 30,
                    myCont.Uid.Value, item.Hue, _netState.IsClientPost6017));
                SendTradeItemToPartner?.Invoke(dropTrade.GetPartner(_character), item, myCont);
                _netState.Send(new PacketDropAck());
                return;
            }
            if (container != null)
            {
                // Capacity enforcement — bank and normal containers have separate limits.
                // Staff bypass so GMs can overstuff chests during testing.
                // On rejection we bounce the item into the dropper's backpack so it
                // is never destroyed; client visually resyncs from our 0x25/0x3C.
                if (_character.PrivLevel < PrivLevel.GM)
                {
                    bool isBank = container.EquipLayer == Layer.BankBox;
                    int currentCount = _world.GetContainerContents(container.Uid).Count();
                    int maxItems = isBank ? _world.MaxBankItems : _world.MaxContainerItems;
                    if (currentCount >= maxItems)
                    {
                        SysMessage(ServerMessages.Get(isBank ? Msg.BvboxFullItems : Msg.ContFullItems));
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                    int weightLimit = isBank ? _world.MaxBankWeight : _world.MaxContainerWeight;
                    if (weightLimit > 0)
                    {
                        int totalWeight = 0;
                        foreach (var b in _world.GetContainerContents(container.Uid))
                            totalWeight += Math.Max(1, (int)b.Amount);
                        if (totalWeight + Math.Max(1, (int)item.Amount) > weightLimit)
                        {
                            SysMessage(ServerMessages.Get(isBank ? Msg.BvboxFullWeight : Msg.ContFullWeight));
                            PlaceItemInPack(_character, item);
                            _netState.Send(new PacketDropReject());
                            return;
                        }
                    }
                }

                // Fire @DropOn_Item
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnItem,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = container });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropReject());
                        return;
                    }
                }
                container.AddItem(item);
                item.Position = new Point3D(x, y, 0, _character.MapIndex);
                // Critical: tell the client the item actually landed in the
                // container. Without 0x25 the client only remembers the
                // earlier pickup → the item silently vanishes from its view.
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, item.X, item.Y,
                    container.Uid.Value, item.Hue,
                    _netState.IsClientPost6017));
                _netState.Send(new PacketDropAck());
                if (item.BaseId == 0x0EED)
                    SendCharacterStatus(_character);
                return;
            }

            var charTarget = _world.FindChar(new Serial(containerUid));
            if (charTarget != null && charTarget == _character)
            {
                // Fire @DropOn_Self
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnSelf,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                }
                PlaceItemInPack(_character, item);
                _netState.Send(new PacketDropAck());
                return;
            }
            else if (charTarget != null)
            {
                // Fire @DropOn_Char
                if (_triggerDispatcher != null)
                {
                    var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.DropOnChar,
                        new TriggerArgs { CharSrc = _character, ItemSrc = item, O1 = charTarget });
                    if (result == TriggerResult.True)
                    {
                        PlaceItemInPack(_character, item);
                        _netState.Send(new PacketDropAck());
                        return;
                    }
                }

                if (charTarget.IsPlayer && _tradeManager != null)
                {
                    InitiateTrade(charTarget, item);
                    _netState.Send(new PacketDropAck());
                    return;
                }

                PlaceItemInPack(charTarget, item);
                _netState.Send(new PacketDropAck());
                return;
            }
        }

        // Source-X parity: @DropOn_Ground RETURN 1 cancels the drop;
        // bounce the item back to the player's pack so the cursor
        // doesn't get stuck and scripts can fully gate ground placement.
        var dropResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.DropOnGround,
            new TriggerArgs { CharSrc = _character, ItemSrc = item });
        if (dropResult == TriggerResult.True)
        {
            PlaceItemInPack(_character, item);
            _netState.Send(new PacketDropReject());
            return;
        }

        _world.PlaceItemWithDecay(item, new Point3D(x, y, z, _character.MapIndex));
        _netState.Send(new PacketDropAck());
    }

    // ==================== Item Equip ====================

    public void HandleItemEquip(uint serial, byte layer, uint charSerial)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        var target = _world.FindChar(new Serial(charSerial));
        if (target == null) target = _character;

        if (target != _character && _character.PrivLevel < PrivLevel.GM) return;

        // Fire @EquipTest — if script blocks, deny equip
        if (_triggerDispatcher != null)
        {
            var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.EquipTest,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            if (result == TriggerResult.True)
                return;
        }

        // Spell interruption on equip change
        _spellEngine?.TryInterruptFromEquip(target);

        target.Equip(item, (Layer)layer);

        // Notify client about the equipped item
        _netState.Send(new PacketWornItem(
            item.Uid.Value, item.DispIdFull, layer,
            target.Uid.Value, item.Hue));

        // Fire @Equip (post-equip notification)
        _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Equip,
            new TriggerArgs { CharSrc = _character, ItemSrc = item });
    }

    // ==================== Status Request ====================

    public void HandleProfileRequest(byte mode, uint serial, string bioText = "")
    {
        if (_character == null) return;

        Character? ch = _world.FindChar(new Serial(serial));
        ch ??= _character;

        if (mode == 1)
        {
            if (ch == _character || _character.PrivLevel >= PrivLevel.GM)
                ch.SetTag("PROFILE_BIO", bioText);
            return;
        }

        string title = string.IsNullOrEmpty(ch.Title)
            ? ch.GetName()
            : $"{ch.GetName()}, {ch.Title}";

        string profile = ch.TryGetTag("PROFILE_BIO", out string? bio) && bio != null ? bio : "";
        _netState.Send(new PacketProfileResponse(ch.Uid.Value, title, profile));
    }

    public void HandleStatusRequest(byte type, uint serial)
    {
        if (_character == null) return;

        if (type == 4 || type == 0) // status
        {
            Character? ch = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
                ch = _world.FindChar(new Serial(serial));

            // Some clients may request status with invalid/empty serial after resync.
            // Fallback to self so status bars are never blank.
            ch ??= _character;

            // Self status is always allowed; other mobiles require visibility/range.
            if (ch != _character && !CanSendStatusFor(ch))
                return;

            // @UserStats fires when the client opens the status window
            // on *its own* character. Matches Source-X CClient::Event_StatusRequest.
            if (ch == _character)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserStats,
                    new TriggerArgs { CharSrc = _character });
            }

            SendCharacterStatus(ch, includeExtendedStats: ch == _character);
        }
        else if (type == 5) // skill list
        {
            SendSkillList();
        }
    }

    // ==================== Target Response ====================

    public void HandleTargetResponse(byte type, uint targetId, uint serial, short x, short y, sbyte z, ushort graphic)
    {
        if (_character == null) return;
        _targetCursorActive = false;
        bool targetCancelled = IsTargetCancelled(serial, x, y, z, graphic);
        if (targetCancelled)
        {
            // Hard-cancel all pending target flows to avoid any stale state from triggering
            // a resync/teleport path on the next target packet.
            _pendingTeleTarget = false;
            _pendingAddToken = null;
            _pendingRemoveTarget = false;
            _pendingXVerb = null;
            _pendingXVerbArgs = "";
            _pendingAreaVerb = null;
            _pendingAreaRange = 0;
            _pendingControlTarget = false;
            _pendingDupeTarget = false;
            _pendingHealTarget = false;
            _pendingKillTarget = false;
            _pendingBankTarget = false;
            _pendingSummonToTarget = false;
            _pendingMountTarget = false;
            _pendingSummonCageTarget = false;
            _pendingTargetFunction = null;
            _pendingTargetArgs = "";
            _pendingTargetAllowGround = false;
            _pendingTargetItemUid = Serial.Invalid;
            _pendingScriptNewItem = null;
            _lastScriptTargetPoint = null;
            _pendingTargetCallback = null;

            if (_character.TryGetTag("CAST_SPELL", out _))
                _character.RemoveTag("CAST_SPELL");
            _character.RemoveTag("TARGP");
            _character.RemoveTag("TARG.X");
            _character.RemoveTag("TARG.Y");
            _character.RemoveTag("TARG.Z");
            _character.RemoveTag("TARG.MAP");
            _character.RemoveTag("TARG.UID");

            SysMessage(ServerMessages.Get("target_cancel_1"));
            return;
        }

        // Callback-based target (housing, etc.)
        if (_pendingTargetCallback != null)
        {
            var cb = _pendingTargetCallback;
            _pendingTargetCallback = null;
            cb(serial, x, y, z, graphic);
            return;
        }

        if (_pendingTeleTarget)
        {
            _pendingTeleTarget = false;

            Point3D? destination = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var obj = _world.FindObject(new Serial(serial));
                if (obj is Character targetChar)
                {
                    destination = targetChar.Position;
                }
                else if (obj is Item targetItem)
                {
                    destination = targetItem.Position;
                }
            }

            destination ??= new Point3D(x, y, z, _character.MapIndex);

            // Snap Z to the nearest walkable surface. Clients pick the Z of
            // whatever tile the mouse overlaps — frequently a rooftop or a
            // static plane. Landing there strands the player: every subsequent
            // step gets rejected by climb/cliff checks (~150 MoveReject spam
            // on `.mtele 1493,1639,40` observed in logs).
            var mdata = _world.MapData;
            if (mdata != null)
            {
                var d = destination.Value;
                sbyte walkZ = mdata.GetEffectiveZ(_character.MapIndex, d.X, d.Y, (sbyte)d.Z);
                if (walkZ != d.Z)
                    destination = new Point3D(d.X, d.Y, walkZ, _character.MapIndex);
            }

            _world.MoveCharacter(_character, destination.Value);
            Resync();
            _mountEngine?.EnsureMountedState(_character);
            // Broadcast full appearance (including mount) to nearby clients at new location.
            BroadcastDrawObject(_character);
            SysMessage(ServerMessages.GetFormatted("gm_teleported_dest", destination.Value));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingAddToken))
        {
            string addToken = _pendingAddToken;
            _pendingAddToken = null;

            Point3D targetPos = new Point3D(x, y, z, _character.MapIndex);
            uint targetSerial = serial;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var obj = _world.FindObject(new Serial(serial));
                if (obj != null)
                    targetPos = obj.Position;
            }

            if (!TryAddAtTarget(addToken, targetPos, targetSerial))
                SysMessage(ServerMessages.GetFormatted("gm_unknown_add", addToken));
            return;
        }

        if (_pendingRemoveTarget)
        {
            _pendingRemoveTarget = false;

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            if (RemoveTargetedObject(serial))
                SysMessage(ServerMessages.GetFormatted("gm_removed", $"{serial:X8}"));
            else
                SysMessage(ServerMessages.Get("target_cant_remove"));
            return;
        }

        if (_pendingResurrectTarget)
        {
            _pendingResurrectTarget = false;

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            // Try the picked serial as a character first; if it's a corpse,
            // fall back to the OWNER_UID tag the DeathEngine wrote on it.
            var victim = _world.FindChar(new Serial(serial));
            if (victim == null)
            {
                var corpse = _world.FindItem(new Serial(serial));
                if (corpse != null && corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
                    uint.TryParse(ownerStr, out uint ownerUid))
                {
                    victim = _world.FindChar(new Serial(ownerUid));
                }
            }

            if (victim == null)
            {
                SysMessage("Resurrect: cannot identify a character from that target.");
                return;
            }
            if (!victim.IsDead)
            {
                SysMessage($"'{victim.Name}' is not dead.");
                return;
            }

            OnResurrectOther?.Invoke(victim);
            SysMessage($"Resurrected '{victim.Name}'.");
            return;
        }

        if (_pendingInspectTarget)
        {
            _pendingInspectTarget = false;
            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }
            var infoObj = _world.FindObject(new Serial(serial));
            if (infoObj != null)
                OpenInspectPropDialog(infoObj, 0);
            else
                SysMessage(ServerMessages.GetFormatted("gm_object_serial", $"{serial:X8}"));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_pendingShowArgs))
        {
            string showArgs = _pendingShowArgs;
            _pendingShowArgs = null;

            if (_commands == null || serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            _commands.ExecuteShowForTarget(_character, showArgs, serial);
            return;
        }

        if (_pendingEditArgs != null)
        {
            string editArgs = _pendingEditArgs;
            _pendingEditArgs = null;

            if (_commands == null || serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            _commands.ExecuteEditForTarget(_character, editArgs, serial);
            return;
        }

        // ---- Phase C: NUKE / NUKECHAR / NUDGE area handlers ----
        if (!string.IsNullOrEmpty(_pendingAreaVerb))
        {
            string areaVerb = _pendingAreaVerb!;
            int areaRange = _pendingAreaRange;
            _pendingAreaVerb = null;
            _pendingAreaRange = 0;

            // Resolve the centre. If the GM clicked on an object use its
            // position so NUDGE/NUKE applied to a chest also covers the
            // surrounding tiles, mirroring Source-X's box centre behaviour.
            Point3D centre;
            if (serial != 0 && serial != 0xFFFFFFFF)
            {
                var picked = _world.FindObject(new Serial(serial));
                centre = picked?.Position ?? new Point3D(x, y, z, _character.MapIndex);
            }
            else
            {
                centre = new Point3D(x, y, z, _character.MapIndex);
            }

            int affected = ExecuteAreaVerb(areaVerb, centre, areaRange);
            switch (areaVerb)
            {
                case "NUKE":
                    SysMessage(ServerMessages.GetFormatted("gm_nuke_done", affected));
                    break;
                case "NUKECHAR":
                    SysMessage(ServerMessages.GetFormatted("gm_nukechar_done", affected));
                    break;
                case "NUDGE":
                    SysMessage(ServerMessages.GetFormatted("gm_nudge_done", affected));
                    break;
            }
            return;
        }

        if (_pendingControlTarget)
        {
            _pendingControlTarget = false;
            var npc = ResolvePickedChar(serial);
            if (npc == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            npc.TryAssignOwnership(_character, _character, summoned: false, enforceFollowerCap: false);
            SysMessage(ServerMessages.GetFormatted("gm_control_done", npc.Name));
            return;
        }

        if (_pendingDupeTarget)
        {
            _pendingDupeTarget = false;
            var pickedItem = serial != 0 && serial != 0xFFFFFFFF
                ? _world.FindItem(new Serial(serial))
                : null;
            if (pickedItem == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            var dup = DuplicateItem(pickedItem);
            if (dup != null)
                SysMessage(ServerMessages.GetFormatted("gm_dupe_done",
                    pickedItem.Name ?? "item", dup.Uid.Value.ToString("X8")));
            else
                SysMessage(ServerMessages.Get("target_cant_remove"));
            return;
        }

        if (_pendingHealTarget)
        {
            _pendingHealTarget = false;
            var victim = ResolvePickedChar(serial);
            if (victim == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            if (victim.IsDead) victim.Resurrect();
            victim.Hits = victim.MaxHits;
            victim.Mana = victim.MaxMana;
            victim.Stam = victim.MaxStam;
            SysMessage(ServerMessages.GetFormatted("gm_heal_done", victim.Name));
            return;
        }

        if (_pendingKillTarget)
        {
            _pendingKillTarget = false;
            var victim = ResolvePickedChar(serial);
            if (victim == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            OnKillTarget?.Invoke(_character!, victim);
            return;
        }

        if (_pendingBankTarget)
        {
            _pendingBankTarget = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            // We open the picked char's bank on *our* client. Source-X
            // Eq's the BankBox onto the picked char then sends the owner
            // GM a 0x24 OpenContainer; the bank items are then drawn
            // from that container's content.
            OpenForeignBank(picked);
            return;
        }

        if (_pendingSummonToTarget)
        {
            _pendingSummonToTarget = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            _world.MoveCharacter(picked, _character.Position);
            BroadcastDrawObject(picked);
            SysMessage(ServerMessages.GetFormatted("gm_summonto_done", picked.Name));
            return;
        }

        if (_pendingMountTarget)
        {
            _pendingMountTarget = false;
            var npc = ResolvePickedChar(serial);
            if (npc == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            if (_mountEngine == null || !_mountEngine.TryMount(_character, npc))
            {
                SysMessage(ServerMessages.Get("gm_mount_failed"));
                return;
            }
            BroadcastDrawObject(_character);
            SysMessage(ServerMessages.GetFormatted("gm_mount_done", npc.Name));
            return;
        }

        if (_pendingSummonCageTarget)
        {
            _pendingSummonCageTarget = false;
            var picked = ResolvePickedChar(serial);
            if (picked == null) { SysMessage(ServerMessages.Get("target_must_object")); return; }
            // Source-X CV_SUMMONCAGE: teleport the victim to the GM and
            // ring them with iron-bar items. We use BaseId 0x0008/0x0009
            // for vertical/horizontal bars (DEFNAMES i_bars_*).
            _world.MoveCharacter(picked, _character.Position);
            BroadcastDrawObject(picked);
            SpawnCageAround(picked.Position);
            SysMessage(ServerMessages.GetFormatted("gm_summoncage_done", picked.Name));
            return;
        }

        // Source-X CClient.cpp:921 — generic X-prefix verb fallback:
        // resolve the picked object and apply the inner verb to it via
        // SpeechEngine.ExecuteVerbForTarget. Mirrors C++ addTargetVerb.
        if (!string.IsNullOrEmpty(_pendingXVerb))
        {
            string verb = _pendingXVerb!;
            string xargs = _pendingXVerbArgs;
            _pendingXVerb = null;
            _pendingXVerbArgs = "";

            if (serial == 0 || serial == 0xFFFFFFFF)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            IScriptObj? obj = (IScriptObj?)_world.FindChar(new Serial(serial))
                ?? _world.FindItem(new Serial(serial));
            if (obj == null)
            {
                SysMessage(ServerMessages.GetFormatted("gm_object_serial", $"{serial:X8}"));
                return;
            }

            // Snapshot the relevant fields we may need to broadcast on
            // change (position / appearance) before mutating the target.
            Point3D? posBefore = (obj as Character)?.Position;
            ushort bodyBefore = (obj as Character)?.BodyId ?? 0;
            ushort hueBefore = (obj as Character)?.Hue.Value ?? 0;

            bool ok = _commands?.ExecuteVerbForTarget(_character, verb, xargs, obj) ?? false;

            if (ok)
            {
                SysMessage(ServerMessages.GetFormatted("gm_xverb_applied", verb, obj.GetName()));

                if (obj is Character ch)
                {
                    bool moved = posBefore.HasValue && !ch.Position.Equals(posBefore.Value);
                    bool appearance = ch.BodyId != bodyBefore || ch.Hue.Value != hueBefore;
                    if (moved)
                    {
                        _world.MoveCharacter(ch, ch.Position);
                        if (ch == _character) Resync();
                        BroadcastDrawObject(ch);
                    }
                    else if (appearance)
                    {
                        BroadcastDrawObject(ch);
                    }
                }
            }
            else
            {
                SysMessage(ServerMessages.GetFormatted("gm_xverb_failed", verb, obj.GetName()));
            }
            return;
        }

        if (!string.IsNullOrEmpty(_pendingTargetFunction) && _triggerDispatcher?.Runner != null)
        {
            string func = _pendingTargetFunction;
            _pendingTargetFunction = null;
            bool allowGround = _pendingTargetAllowGround;
            _pendingTargetAllowGround = false;
            var pendingItemUid = _pendingTargetItemUid;
            _pendingTargetItemUid = Serial.Invalid;
            _lastScriptTargetPoint = new Point3D(x, y, z, _character.MapIndex);
            _character.SetTag("TARGP", $"{x},{y},{z},{_character.MapIndex}");
            _character.SetTag("TARG.X", x.ToString());
            _character.SetTag("TARG.Y", y.ToString());
            _character.SetTag("TARG.Z", z.ToString());
            _character.SetTag("TARG.MAP", _character.MapIndex.ToString());
            _character.SetTag("TARG.UID", $"0{serial:X}");

            IScriptObj? argo = null;
            if (serial != 0 && serial != 0xFFFFFFFF)
                argo = _world.FindObject(new Serial(serial));
            if (argo == null && !allowGround)
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                return;
            }

            var trigArgs = new ExecTriggerArgs(_character, 0, 0, _pendingTargetArgs)
            {
                Object1 = argo,
                Object2 = pendingItemUid.IsValid
                    ? ((IScriptObj?)_world.FindItem(pendingItemUid) ?? _character)
                    : _character
            };
            _pendingTargetArgs = "";

            // Snapshot position before running the script function so we can
            // detect if it moved the character (e.g. SRC.GO <TARGP>).
            // We cannot rely on _lastScriptTargetPoint because the script may
            // chain another TARGETF which calls ClearPendingTargetState and
            // clears _lastScriptTargetPoint before we get back here.
            var posBefore = _character.Position;
            _triggerDispatcher.Runner.RunFunction(func, _character, this, trigArgs);
            if (_character != null && !_character.Position.Equals(posBefore))
            {
                _world.MoveCharacter(_character, _character.Position);
                Resync();
                _mountEngine?.EnsureMountedState(_character);
                BroadcastDrawObject(_character);
            }
            return;
        }

        if (_character.TryGetTag("CAST_SPELL", out string? spellStr) &&
            Enum.TryParse<SpellType>(spellStr, out var spell))
        {
            _character.RemoveTag("CAST_SPELL");
            HandleCastSpell(spell, serial);
        }
    }

    private static bool IsTargetCancelled(uint serial, short x, short y, sbyte z, ushort graphic)
    {
        // Classic cancel payload variant (seen in some clients): serial=0, x=y=0xFFFF.
        if (serial == 0 && (ushort)x == 0xFFFF && (ushort)y == 0xFFFF)
            return true;

        // Client cancel is most commonly serial=0xFFFFFFFF.
        if (serial == 0xFFFFFFFF)
            return true;

        // Some clients send a fully-zero target payload on ESC.
        if (serial == 0 && x == 0 && y == 0 && z == 0 && graphic == 0)
            return true;

        // Legacy cancel payload variant with -1 coordinates.
        if (serial == 0 && x == -1 && y == -1 && z == -1)
            return true;

        // Additional client cancel variant: serial=0 with out-of-world x/y.
        // Note: z < 0 is valid (caves/dungeons), only reject impossible x/y.
        if (serial == 0 && (x < 0 || y < 0))
            return true;

        // Another observed cancel form: invalid serial + no model + max coords.
        if (serial == 0 && graphic == 0 && ((ushort)x == 0xFFFF || (ushort)y == 0xFFFF))
            return true;

        return false;
    }

    // ==================== Gump Response ====================

    public void HandleGumpResponse(uint serial, uint gumpId, uint buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null) return;

        if (!string.IsNullOrWhiteSpace(_pendingDialogCloseFunction))
        {
            string closeFn = _pendingDialogCloseFunction;
            _pendingDialogCloseFunction = null;
            var trigArgs = new ExecTriggerArgs(_character, (int)buttonId, (int)gumpId, _pendingDialogArgs)
            {
                Object1 = _character,
                Object2 = _character
            };

            // Allow script-provided close function tokens like CTAG0.HELP_TYPE.
            if (TryResolveScriptVariable(closeFn, _character, trigArgs, out string resolvedCloseFn) &&
                !string.IsNullOrWhiteSpace(resolvedCloseFn))
            {
                closeFn = resolvedCloseFn;
            }

            closeFn = closeFn.Trim().Trim(',', ';');
            if (closeFn.Equals("DIALOGCLOSE", StringComparison.OrdinalIgnoreCase) ||
                closeFn.Equals("DIALOGCLOSE()", StringComparison.OrdinalIgnoreCase))
            {
                closeFn = $"f_dialogclose_{_pendingDialogArgs}";
            }
            if (string.IsNullOrWhiteSpace(closeFn))
                closeFn = $"f_dialogclose_{_pendingDialogArgs}";

            _pendingDialogArgs = "";
            if (_triggerDispatcher?.Runner != null)
            {
                // Script-first fallback chain:
                // 1) explicit/variable-resolved close function
                // 2) default f_dialogclose_<dialogId>
                if (!_triggerDispatcher.Runner.TryRunFunction(closeFn, _character, this, trigArgs, out _))
                {
                    string defaultCloseFn = $"f_dialogclose_{trigArgs.ArgString.Trim().Trim(',', ';')}";
                    _triggerDispatcher.Runner.TryRunFunction(defaultCloseFn, _character, this, trigArgs, out _);
                }
            }
        }

        // Route to registered callback if present
        if (_gumpCallbacks.TryGetValue(gumpId, out var callback))
        {
            _gumpCallbacks.Remove(gumpId);
            callback(buttonId, switches, textEntries);
            return;
        }

        _logger.LogDebug("GumpResponse: serial=0x{S:X}, gumpId=0x{G:X}, button={B}",
            serial, gumpId, buttonId);
    }

    // ==================== Gump Sending ====================

    /// <summary>Send a gump dialog to the client. Optionally register a response callback.</summary>
    public void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null)
    {
        if (_character == null) return;

        if (callback != null)
            _gumpCallbacks[gump.GumpId] = callback;

        string layout = gump.BuildLayoutString();
        int gx = gump.ExplicitX ?? (gump.Width > 0 ? (800 - gump.Width) / 2 : 50);
        int gy = gump.ExplicitY ?? (gump.Height > 0 ? (600 - gump.Height) / 2 : 50);
        _netState.Send(new PacketGumpDialog(
            gump.Serial, gump.GumpId, gx, gy, layout, gump.Texts));
    }

    /// <summary>Set a callback-based target cursor. Used by housing, pets, etc.</summary>
    private void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1)
    {
        if (_targetCursorActive)
            _netState.Send(new PacketTarget(0x00, 0x00000000, flags: 3));

        ClearPendingTargetState();
        _pendingTargetCallback = callback;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(cursorType, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    // ==================== Information Skills ====================

    /// <summary>
    /// Fires trigger chain (PreStart/Start/Stroke) for the information skill,
    /// then asks the client for a target cursor. Selected target is resolved
    /// to the actual Character/Item and pushed into <see cref="SkillHandlers.UseInfoSkill"/>.
    /// </summary>
    private void BeginInfoSkill(SkillType skill, int skillId)
    {
        if (_character == null) return;

        if (_triggerDispatcher != null)
        {
            var pre = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (pre == TriggerResult.True) return;

            var start = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (start == TriggerResult.True) return;
        }

        SysMessage($"What do you wish to use your {skill} skill on?");
        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;

            var uid = new Serial(serial);
            Objects.ObjBase? target = uid.IsValid ? _world.FindObject(uid) : null;

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink = new InfoSkillSink(this, _character);
            bool ok = _skillHandlers?.UseInfoSkill(sink, skill, target) ?? false;

            if (_triggerDispatcher != null)
            {
                var trigger = ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail;
                _triggerDispatcher.FireCharTrigger(_character, trigger,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
        });
    }

    /// <summary>
    /// Active-skill driver. Skills with <see cref="SkillHandlers.ActiveSkillTargetKind.None"/>
    /// run immediately (Hiding, Meditation, ...). Character/Item-target skills
    /// open a target cursor and resolve the picked Serial via the world before
    /// invoking <see cref="SkillHandlers.UseActiveSkill"/>. Trigger chain
    /// (PreStart/Start/Stroke/Success/Fail) is preserved.
    /// </summary>
    private void BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind)
    {
        if (_character == null) return;

        if (_triggerDispatcher != null)
        {
            var pre = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (pre == TriggerResult.True) return;

            var start = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (start == TriggerResult.True) return;
        }

        // No-target path: fire stroke, run engine, fire success/fail.
        if (kind == SkillHandlers.ActiveSkillTargetKind.None)
        {
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink0 = new InfoSkillSink(this, _character);
            bool ok0 = _skillHandlers?.UseActiveSkill(sink0, skill, null) ?? false;

            if (_triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(_character,
                    ok0 ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
            return;
        }

        // Menu path: show category selection gump (Tracking).
        if (kind == SkillHandlers.ActiveSkillTargetKind.Menu)
        {
            ShowTrackingMenu(skill, skillId);
            return;
        }

        // Target-required path.
        SysMessage(kind switch
        {
            SkillHandlers.ActiveSkillTargetKind.Item => $"What item do you wish to use your {skill} skill on?",
            SkillHandlers.ActiveSkillTargetKind.Ground => $"Where do you wish to use your {skill} skill?",
            _ => $"Whom do you wish to use your {skill} skill on?"
        });

        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;

            var uid = new Serial(serial);
            Objects.ObjBase? target = uid.IsValid ? _world.FindObject(uid) : null;
            var point = new Point3D(x, y, z);

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink = new InfoSkillSink(this, _character);
            bool ok = _skillHandlers?.UseActiveSkill(sink, skill, target, point) ?? false;

            if (_triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(_character,
                    ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
        });
    }

    private void ShowTrackingMenu(SkillType skill, int skillId)
    {
        if (_character == null) return;

        var gump = new GumpBuilder(_character.Uid.Value, 0, 300, 220);
        gump.AddResizePic(0, 0, 5054, 300, 220);
        gump.AddText(30, 15, 0, "What do you wish to track?");
        gump.AddButton(30, 55, 4005, 4007, 1);
        gump.AddText(70, 55, 0, "Animals");
        gump.AddButton(30, 85, 4005, 4007, 2);
        gump.AddText(70, 85, 0, "Monsters");
        gump.AddButton(30, 115, 4005, 4007, 3);
        gump.AddText(70, 115, 0, "Humans");
        gump.AddButton(150, 175, 4017, 4019, 0);
        gump.AddText(190, 175, 0, "Cancel");

        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (_character == null || buttonId == 0) return;

            var category = buttonId switch
            {
                1 => Skills.Information.ActiveSkillEngine.TrackingCategory.Animals,
                2 => Skills.Information.ActiveSkillEngine.TrackingCategory.Monsters,
                3 => Skills.Information.ActiveSkillEngine.TrackingCategory.Humans,
                _ => Skills.Information.ActiveSkillEngine.TrackingCategory.Animals,
            };

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink = new InfoSkillSink(this, _character);
            bool ok = Skills.Information.ActiveSkillEngine.Tracking(sink, category);

            if (_triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(_character,
                    ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
        });
    }

    /// <summary>
    /// Glue between the skill engines and the client's network layer.
    /// Implements both <see cref="Skills.Information.IInfoSkillSink"/> and
    /// <see cref="Skills.Information.IActiveSkillSink"/> so the engines can
    /// emit overhead text, emote poses, sounds, and consume backpack items.
    /// </summary>
    private sealed class InfoSkillSink : Skills.Information.IActiveSkillSink
    {
        private readonly GameClient _client;
        public InfoSkillSink(GameClient client, Character self) { _client = client; Self = self; }
        public Character Self { get; }
        public Random Random => System.Random.Shared;
        public Game.World.GameWorld World => _client._world;

        public void SysMessage(string text) => _client.SysMessage(text);
        public void ObjectMessage(Objects.ObjBase target, string text) => _client.ObjectMessage(target, text);
        public void Emote(string text) => _client.NpcSpeech(Self, text);
        public void Sound(ushort soundId) =>
            _client._netState.Send(new PacketSound(soundId, (short)Self.Position.X, (short)Self.Position.Y, Self.Position.Z));

        public Item? FindBackpackItem(Core.Enums.ItemType type)
        {
            var pack = Self.Backpack;
            if (pack == null) return null;
            foreach (var it in pack.Contents)
            {
                if (it.ItemType == type) return it;
            }
            // One level deep so common pouches resolve.
            foreach (var it in pack.Contents)
            {
                if (it.ItemType is Core.Enums.ItemType.Container or Core.Enums.ItemType.ContainerLocked)
                {
                    foreach (var inner in it.Contents)
                        if (inner.ItemType == type) return inner;
                }
            }
            return null;
        }

        public void ConsumeAmount(Item item, ushort amount = 1)
        {
            if (item.Amount > amount)
            {
                item.Amount = (ushort)(item.Amount - amount);
                return;
            }
            // Drop from container.
            var holder = _client._world.FindObject(item.ContainedIn);
            if (holder is Item parent) parent.RemoveItem(item);
            item.Delete();
        }

        public void DeliverItem(Item item)
        {
            var pack = Self.Backpack;
            if (pack == null)
            {
                _client._world.PlaceItemWithDecay(item, Self.Position);
                return;
            }

            var actual = pack.AddItemWithStack(item);
            if (actual != item)
                item.Delete();

            _client._netState.Send(new PacketContainerItem(
                actual.Uid.Value, actual.DispIdFull, 0,
                actual.Amount, actual.X, actual.Y,
                pack.Uid.Value, actual.Hue,
                _client._netState.IsClientPost6017));
        }
    }

    /// <summary>Source-X addObjMessage: overhead speech over any ObjBase.</summary>
    internal void ObjectMessage(Objects.ObjBase target, string text)
    {
        uint uid;
        ushort body;
        string name;
        switch (target)
        {
            case Character ch:
                uid = ch.Uid.Value; body = ch.BodyId; name = ch.GetName();
                break;
            case Item it:
                uid = it.Uid.Value; body = it.BaseId; name = it.Name ?? "";
                break;
            default:
                SysMessage(text); return;
        }
        var packet = new PacketSpeechUnicodeOut(uid, body, 0, 0x03B2, 3, "ENU", name, text);
        _netState.Send(packet);
    }

    // ==================== Help Menu ====================

    public void HandleHelpRequest()
    {
        if (_character == null) return;

        // Script-first parity:
        // If [FUNCTION f_onclient_helppage] exists and runs, skip the built-in fallback.
        if (_triggerDispatcher?.Runner != null)
        {
            var trigArgs = new ExecTriggerArgs(_character, 0, 0, string.Empty)
            {
                Object1 = _character,
                Object2 = _character
            };
            if (_triggerDispatcher.Runner.TryRunFunction("f_onclient_helppage", _character, this, trigArgs, out _))
                return;
        }

        OpenNamedDialog("d_helppage", 1);
    }

    // ==================== AOS Tooltip ====================

    public void HandleAOSTooltip(uint serial)
    {
        if (_character == null) return;
        if (_world.ToolTipMode == 0) return;

        var obj = _world.FindObject(new Serial(serial));
        if (obj == null) return;

        var propList = new List<(uint ClilocId, string Args)>
        {
            (1050045, obj.GetName()) // generic name cliloc
        };

        // Enrich tooltips for items
        if (obj is Item item)
        {
            switch (item.ItemType)
            {
                case ItemType.WeaponMaceSmith:
                case ItemType.WeaponMaceSharp:
                case ItemType.WeaponSword:
                case ItemType.WeaponFence:
                case ItemType.WeaponBow:
                case ItemType.WeaponAxe:
                case ItemType.WeaponXBow:
                case ItemType.WeaponMaceStaff:
                case ItemType.WeaponMaceCrook:
                case ItemType.WeaponMacePick:
                case ItemType.WeaponThrowing:
                case ItemType.WeaponWhip:
                    // Weapon damage — try reading from tags or CombatEngine lookup
                    if (item.TryGetTag("DAM", out string? damStr) && damStr != null)
                        propList.Add((1061168, $"\t{damStr}")); // weapon damage cliloc
                    if (item.TryGetTag("SPEED", out string? speedStr) && speedStr != null)
                        propList.Add((1061167, $"\t{speedStr}")); // weapon speed cliloc
                    break;

                case ItemType.Armor:
                case ItemType.ArmorLeather:
                case ItemType.ArmorBone:
                case ItemType.ArmorChain:
                case ItemType.ArmorRing:
                case ItemType.Shield:
                    if (item.TryGetTag("ARMOR", out string? armorStr) && armorStr != null)
                        propList.Add((1060448, $"\t{armorStr}")); // physical resist
                    if (item.TryGetTag("DURABILITY", out string? durStr) && durStr != null)
                        propList.Add((1060639, $"\t{durStr}")); // durability
                    break;

                case ItemType.Container:
                case ItemType.ContainerLocked:
                    propList.Add((1050044, $"\t{item.ContentCount}\t125")); // items/max items
                    propList.Add((1072789, $"\t{item.TotalWeight}")); // weight
                    break;
            }
        }

        var props = propList.ToArray();
        // Deterministic hash — .NET GetHashCode() is randomized per process
        uint hash = StableStringHash(obj.GetName());
        foreach (var (clilocId, args) in props)
            hash = hash * 31 + (uint)clilocId + StableStringHash(args);

        // Skip entirely if we already sent this exact hash for this serial.
        // The client already has the tooltip data — no need to resend 0xDC.
        if (_tooltipHashCache.TryGetValue(serial, out uint cachedHash) && cachedHash == hash)
            return;
        _tooltipHashCache[serial] = hash;

        _netState.Send(new PacketOPLData(serial, hash, props));
        _netState.Send(new PacketOPLInfo(serial, hash));
    }

    // ==================== Trade ====================

    public void HandleTradeRequest(uint targetUid)
    {
        if (_character == null || _tradeManager == null) return;
        var target = _world.FindChar(new Serial(targetUid));
        if (target == null || !target.IsPlayer) return;
        InitiateTrade(target);
    }

    // ==================== Party ====================

    public void HandlePartyInvite(uint targetUid)
    {
        if (_character == null || _partyManager == null) return;
        var target = _world.FindChar(new Serial(targetUid));
        if (target == null || !target.IsPlayer) return;
        _triggerDispatcher?.FireCharTrigger(target, CharTrigger.PartyInvite,
            new TriggerArgs { CharSrc = _character });
        _partyManager.AcceptInvite(_character.Uid, target.Uid);
    }

    public void HandlePartyLeave()
    {
        if (_character == null || _partyManager == null) return;
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.PartyLeave,
            new TriggerArgs { CharSrc = _character });
        _partyManager.Leave(_character.Uid);
    }

    // ==================== Client Version ====================

    public void HandleClientVersion(string version)
    {
        _logger.LogDebug("Client version: {Ver}", version);

        // Parse version string (e.g. "7.0.20.0") into the numeric format used by NetState
        if (!string.IsNullOrEmpty(version) && _netState.ClientVersionNumber == 0)
        {
            var parts = version.Split('.');
            if (parts.Length >= 3 &&
                uint.TryParse(parts[0], out uint major) &&
                uint.TryParse(parts[1], out uint minor) &&
                uint.TryParse(parts[2], out uint rev))
            {
                uint patch = parts.Length > 3 && uint.TryParse(parts[3], out uint p) ? p : 0;
                _netState.ClientVersionNumber = major * 10_000_000 + minor * 1_000_000 + rev * 1_000 + patch;
                _logger.LogInformation("Client version detected from 0xBD: {Ver} → {Num}", version, _netState.ClientVersionNumber);
            }
        }
    }

    // ==================== Client Update Loop ====================

    /// <summary>
    /// Source-X CClient::addObjMessage loop. Sends newly visible objects and
    /// removes objects that went out of range. Called each server tick.
    /// </summary>
    public void UpdateClientView()
    {
        var delta = BuildViewDelta();
        if (delta != null)
            ApplyViewDelta(delta);
    }

    public sealed class ClientViewDelta
    {
        public HashSet<uint> CurrentChars { get; } = [];
        public HashSet<uint> CurrentItems { get; } = [];
        public List<(Character Character, bool HiddenAsAllShow)> NewChars { get; } = [];
        public List<Character> UpdatedChars { get; } = [];
        public List<(Item Item, bool HiddenAsAllShow)> NewItems { get; } = [];
    }

    /// <summary>
    /// Build a readonly visibility delta. Safe for parallel build phase.
    /// Only runs for clients with ViewNeedsRefresh — idle clients skip entirely.
    /// </summary>
    public ClientViewDelta? BuildViewDelta()
    {
        if (_character == null || !IsPlaying) return null;
        if (_character.IsReplaySpectator) return null;

        int range = _netState.ViewRange;
        var center = _character.Position;
        var delta = new ClientViewDelta();

        foreach (var ch in _world.GetCharsInRange(center, range))
        {
            if (ch == _character || ch.IsDeleted) continue;
            if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) continue;

            bool isOfflinePlayer = ch.IsPlayer && !ch.IsOnline;
            if (isOfflinePlayer && !_character.AllShow)
                continue;

            bool isHidden = ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);
            bool canSeeHidden = _character.AllShow ||
                (_character.PrivLevel >= Core.Enums.PrivLevel.Counsel &&
                 _character.PrivLevel >= ch.PrivLevel);

            if (isHidden && !canSeeHidden)
                continue;

            bool ghostManifested = ch.IsDead && ch.IsInWarMode;
            if (ch.IsDead && !_character.IsDead && !ghostManifested)
            {
                bool canSeeGhosts = _character.AllShow ||
                    _character.PrivLevel >= Core.Enums.PrivLevel.Counsel ||
                    _character.IsStatFlag(Core.Enums.StatFlag.SpiritSpeak);
                if (!canSeeGhosts)
                    continue;
            }

            uint uid = ch.Uid.Value;
            delta.CurrentChars.Add(uid);

            bool hiddenAsAllShow = isOfflinePlayer || (isHidden && canSeeHidden);
            if (!_knownChars.Contains(uid))
                delta.NewChars.Add((ch, hiddenAsAllShow));
            else
                delta.UpdatedChars.Add(ch);
        }

        bool isStaff = _character.PrivLevel >= Core.Enums.PrivLevel.Counsel;
        foreach (var item in _world.GetItemsInRange(center, range))
        {
            if (item.IsDeleted || item.IsEquipped || !item.IsOnGround) continue;
            bool isInvis = item.IsAttr(Core.Enums.ObjAttributes.Invis);
            if (isInvis && !_character.AllShow && !isStaff)
                continue;

            uint uid = item.Uid.Value;
            delta.CurrentItems.Add(uid);
            if (!_knownItems.Contains(uid))
                delta.NewItems.Add((item, isInvis && (_character.AllShow || isStaff)));
        }

        return delta;
    }

    /// <summary>
    /// Apply previously built delta and perform packet I/O + known-set mutation.
    /// Must run on single-thread apply phase.
    /// </summary>
    public void ApplyViewDelta(ClientViewDelta delta)
    {
        if (_character == null || !IsPlaying) return;

        foreach (var (ch, hiddenAsAllShow) in delta.NewChars)
        {
            if (hiddenAsAllShow)
                SendDrawObjectHidden(ch);
            else
                SendDrawObject(ch);

            uint uid = ch.Uid.Value;
            _knownChars.Add(uid);
            _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
        }

        foreach (var ch in delta.UpdatedChars)
        {
            uint uid = ch.Uid.Value;
            bool posChanged = false;
            bool bodyChanged = false;
            if (_lastKnownPos.TryGetValue(uid, out var last))
            {
                posChanged = last.X != ch.X || last.Y != ch.Y || last.Z != ch.Z || last.Dir != (byte)ch.Direction;
                bodyChanged = last.Body != ch.BodyId || last.Hue != ch.Hue;
            }
            else
            {
                posChanged = true;
            }

            bool manifestGhost = ch.IsDead && ch.IsInWarMode &&
                !_character!.AllShow &&
                _character.PrivLevel < Core.Enums.PrivLevel.Counsel &&
                !_character.IsDead;
            bool isOfflinePlayer = ch.IsPlayer && !ch.IsOnline;
            bool isHidden = ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);
            bool canSeeHidden = _character.AllShow ||
                (_character.PrivLevel >= Core.Enums.PrivLevel.Counsel &&
                 _character.PrivLevel >= ch.PrivLevel);
            bool hiddenAsAllShow = isOfflinePlayer || (isHidden && canSeeHidden);

            if (bodyChanged)
            {
                if (hiddenAsAllShow)
                    SendDrawObjectHidden(ch);
                else if (manifestGhost)
                    SendDrawObjectWithHue(ch, 0x4001);
                else
                    SendDrawObject(ch);
            }
            else if (posChanged)
            {
                if (hiddenAsAllShow)
                    SendUpdateMobileHidden(ch);
                else if (manifestGhost)
                    SendUpdateMobileWithHue(ch, 0x4001);
                else
                    SendUpdateMobile(ch);
            }

            if (posChanged || bodyChanged)
                _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
        }

        foreach (var (item, hiddenAsAllShow) in delta.NewItems)
        {
            if (hiddenAsAllShow)
                SendWorldItemWithHue(item, 0x4001);
            else
                SendWorldItem(item);
            _knownItems.Add(item.Uid.Value);
        }

        var staleChars = new List<uint>();
        foreach (uint uid in _knownChars)
        {
            if (!delta.CurrentChars.Contains(uid))
            {
                _netState.Send(new PacketDeleteObject(uid));
                staleChars.Add(uid);
            }
        }
        foreach (uint uid in staleChars)
        {
            _knownChars.Remove(uid);
            _lastKnownPos.Remove(uid);
        }

        var staleItems = new List<uint>();
        foreach (uint uid in _knownItems)
        {
            if (!delta.CurrentItems.Contains(uid))
            {
                _netState.Send(new PacketDeleteObject(uid));
                staleItems.Add(uid);
            }
        }
        foreach (uint uid in staleItems)
            _knownItems.Remove(uid);
    }

    /// <summary>
    /// Update this client's _lastKnownPos for a character that was just broadcast via 0x77.
    /// Prevents the view delta from sending a duplicate 0x77 for the same position.
    /// </summary>
    public void UpdateKnownCharPosition(Character ch)
    {
        uint uid = ch.Uid.Value;
        if (_knownChars.Contains(uid))
            _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
    }

    /// <summary>Returns true if this client already tracks the given mobile (has sent 0x78 spawn).</summary>
    public bool HasKnownChar(uint uid) => _knownChars.Contains(uid);

    /// <summary>
    /// Update this client's known-character cache to reflect a body/hue
    /// change that we already broadcast out-of-band — e.g. the ghost
    /// transition during death (body=0x192, hue=0) or the living-body
    /// restore during resurrect (body=0x190, hue=skin). This prevents
    /// the next BuildViewDelta tick from detecting a stale
    /// <c>bodyChanged</c> and re-emitting a duplicate 0x78 with the new
    /// body — which would race the per-observer dispatch and either
    /// produce a duplicate ghost mobile (after 0xAF remap) or just
    /// repeat the spawn packet for no reason.
    ///
    /// If the UID is not currently in <c>_knownChars</c> the call is a
    /// no-op (use this in resurrect to safely re-sync everyone, including
    /// observers who never had the mobile in cache because the ghost
    /// was hidden from them).
    /// </summary>
    public void UpdateKnownCharRender(uint uid, ushort newBody, ushort newHue, byte direction, short x, short y, sbyte z)
    {
        if (_knownChars.Contains(uid))
            _lastKnownPos[uid] = (x, y, z, direction, newBody, newHue);
    }

    /// <summary>
    /// Drop the given character from this client's known-character set,
    /// optionally emitting a 0x1D PacketDeleteObject so ClassicUO removes
    /// the mobile from world.Mobiles immediately.
    ///
    /// <paramref name="sendDelete"/> = false: only clears server-side
    /// cache. Use this in the death dispatch for plain observers — the
    /// 0xAF DisplayDeath we already sent re-keys the mobile to
    /// <c>serial | 0x80000000</c> in ClassicUO, so a follow-up 0x1D with
    /// the original serial would target a now-empty slot (no-op, just
    /// wasted bandwidth). Without this option we'd ALSO double-clean the
    /// killer's view on every PvP death.
    ///
    /// <paramref name="sendDelete"/> = true (default): emit the 0x1D as
    /// well — useful when the dying mobile was never announced via 0xAF
    /// to this observer (e.g. cleanup after a teleport, or for a plain
    /// observer who came in range AFTER the death animation had already
    /// played for everyone else).
    ///
    /// Idempotent: safe to call when the UID is not currently known.
    /// </summary>
    public void RemoveKnownChar(uint uid, bool sendDelete = true)
    {
        if (_knownChars.Remove(uid))
        {
            _lastKnownPos.Remove(uid);
            if (sendDelete)
                _netState.Send(new PacketDeleteObject(uid));
        }
    }

    /// <summary>
    /// Called by BroadcastCharacterAppear to immediately show a character on this client.
    /// Each client renders from its own perspective (notoriety, AllShow, etc.).
    /// </summary>
    public void NotifyCharacterAppear(Character ch)
    {
        if (_character == null || !IsPlaying) return;
        if (ch == _character) return;
        if (ch.Position.Map != _character.Position.Map) return;
        if (!InRange(_character.Position, ch.Position, _netState.ViewRange)) return;

        // === Source-X ghost visibility (mirror of BuildViewDelta filter) ===
        // A dead/ghost character is invisible to LIVING observers unless
        // the observer is staff (Counsel+) or has AllShow toggled, OR the
        // ghost has manifested (war mode). Without this guard, a
        // login/teleport BroadcastCharacterAppear would push the ghost
        // mobile to plain players and cause exactly the duplicate-mobile
        // bug 0xAF was supposed to prevent.
        bool isStaffViewer = _character.AllShow ||
            _character.PrivLevel >= Core.Enums.PrivLevel.Counsel;
        bool ghostManifested = ch.IsDead && ch.IsInWarMode;

        if (ch.IsDead && !_character.IsDead && !ghostManifested && !isStaffViewer)
            return;

        uint uid = ch.Uid.Value;
        // Manifested ghost renders translucent grey (hue 0x4001) for plain
        // observers; staff already see ghosts in their normal hue (HUE_DEFAULT).
        if (ghostManifested && !isStaffViewer && !_character.IsDead)
            SendDrawObjectWithHue(ch, 0x4001);
        else
            SendDrawObject(ch);

        _knownChars.Add(uid);
        _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
    }

    /// <summary>
    /// Object-centric move notification for NPC movement. Handles enter-range (0x78),
    /// leave-range (0x1D), and position-update (0x77).
    /// </summary>
    public void NotifyCharMoved(Character ch, Point3D oldPos)
    {
        if (_character == null || !IsPlaying) return;
        if (ch == _character) return;
        if (ch.IsDeleted) return;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

        int range = _netState.ViewRange;
        bool wasInRange = InRange(_character.Position, oldPos, range) && oldPos.Map == _character.Position.Map;
        bool nowInRange = InRange(_character.Position, ch.Position, range);

        uint uid = ch.Uid.Value;

        if (!wasInRange && nowInRange)
        {
            NotifyCharacterAppear(ch);
        }
        else if (wasInRange && !nowInRange)
        {
            RemoveKnownChar(uid, sendDelete: true);
        }
        else if (wasInRange && nowInRange && _knownChars.Contains(uid))
        {
            if (_lastKnownPos.TryGetValue(uid, out var last))
            {
                bool posChanged = last.X != ch.X || last.Y != ch.Y || last.Z != ch.Z || last.Dir != (byte)ch.Direction;
                if (!posChanged) return;
            }
            SendUpdateMobile(ch);
            _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
        }
    }

    /// <summary>
    /// Player enter/leave range notification. Only handles enter-range (0x78) and
    /// leave-range (0x1D). Still-in-range 0x77 is handled by BroadcastMoveNearby.
    /// </summary>
    public void NotifyCharEnterLeave(Character ch, Point3D oldPos)
    {
        if (_character == null || !IsPlaying) return;
        if (ch == _character) return;
        if (ch.IsDeleted) return;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

        int range = _netState.ViewRange;
        bool wasInRange = InRange(_character.Position, oldPos, range) && oldPos.Map == _character.Position.Map;
        bool nowInRange = InRange(_character.Position, ch.Position, range);

        uint uid = ch.Uid.Value;

        if (!wasInRange && nowInRange)
            NotifyCharacterAppear(ch);
        else if (wasInRange && !nowInRange)
            RemoveKnownChar(uid, sendDelete: true);
    }

    private static bool InRange(Point3D a, Point3D b, int range)
    {
        if (a.Map != b.Map) return false;
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return dx <= range && dy <= range;
    }

    // ==================== Outgoing Packet Helpers ====================

    private void SendDrawObject(Character ch)
    {
        var equipment = BuildEquipmentList(ch);
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);

        _netState.Send(new PacketDrawObject(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z,
            (byte)ch.Direction, ch.Hue, flags, noto,
            equipment
        ));
    }

    public void BeginTeleportTarget()
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingTeleTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginRemoveTarget()
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingRemoveTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>.XRESURRECT — opens a target cursor; the picked mobile (or
    /// the owner of the picked corpse) is resurrected via OnResurrectOther.</summary>
    public void BeginResurrectTarget()
    {
        if (_character == null) return;
        if (_targetCursorActive) return;
        ClearPendingTargetState();
        _pendingResurrectTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>.info without a UID — opens a target cursor; whatever
    /// the GM clicks on lands in <see cref="ShowInspectDialog"/>.</summary>
    public void BeginInspectTarget()
    {
        if (_character == null) return;
        if (_targetCursorActive) return;
        ClearPendingTargetState();
        _pendingInspectTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginAddTarget(string addToken)
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingAddToken = addToken.Trim();
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginShowTarget(string showArgs)
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingShowArgs = string.IsNullOrWhiteSpace(showArgs) ? "EVENTS" : showArgs.Trim();
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginEditTarget(string editArgs)
    {
        if (_character == null)
            return;
        if (_targetCursorActive)
            return;

        ClearPendingTargetState();
        _pendingEditArgs = editArgs?.Trim() ?? "";
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>Source-X parity (CClient.cpp:921 <c>addTargetVerb</c>):
    /// stash an inner verb + arg pair, open a target cursor, and apply
    /// the verb to whatever the GM picks. Used by the generic X-prefix
    /// fallback (.xhits, .xcolor, .xinvul, ...).</summary>
    public void BeginXVerbTarget(string verb, string args)
    {
        if (_character == null) return;
        if (_targetCursorActive) return;
        if (string.IsNullOrEmpty(verb)) return;

        ClearPendingTargetState();
        _pendingXVerb = verb.Trim();
        _pendingXVerbArgs = args?.Trim() ?? "";
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>Source-X CV_NUKE / CV_NUKECHAR / CV_NUDGE: open a
    /// ground-target cursor and treat the picked tile as the centre of
    /// an axis-aligned area of half-extent <paramref name="range"/>.
    /// We deviate from Source-X (which prompts for two corner tiles —
    /// see CClient_functions.tbl 'NUKE'); a single pick + fixed range
    /// keeps the wire round-trip lean and is enough for GM cleanup.</summary>
    public void BeginAreaTarget(string verb, int range)
    {
        if (_character == null) return;
        if (_targetCursorActive) return;
        if (string.IsNullOrEmpty(verb)) return;

        ClearPendingTargetState();
        _pendingAreaVerb = verb.Trim().ToUpperInvariant();
        _pendingAreaRange = Math.Clamp(range, 1, 32);
        _targetCursorActive = true;
        // type=1 (ground allowed), so the GM can pick an empty tile.
        _netState.Send(new PacketTarget(1, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginControlTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingControlTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginDupeTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingDupeTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginHealTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingHealTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginKillTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingKillTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginBankTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingBankTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginSummonToTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingSummonToTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginMountTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingMountTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    public void BeginSummonCageTarget()
    {
        if (_character == null || _targetCursorActive) return;
        ClearPendingTargetState();
        _pendingSummonCageTarget = true;
        _targetCursorActive = true;
        _netState.Send(new PacketTarget(0, (uint)Random.Shared.Next(1, int.MaxValue)));
    }

    /// <summary>Source-X CV_ANIM. Plays the given action on this client's
    /// own character so the GM can verify animation IDs visually.</summary>
    public void PlayOwnAnimation(ushort animId)
    {
        if (_character == null) return;
        var pkt = new PacketAnimation(_character.Uid.Value, animId);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, pkt, 0);
    }

    /// <summary>Convenience wrapper used by SpeechEngine event hookup —
    /// dismount the GM's character if currently mounted.</summary>
    public void UnmountSelf()
    {
        if (_character == null || _mountEngine == null) return;
        _mountEngine.Dismount(_character);
    }

    private void ClearPendingTargetState()
    {
        _pendingTeleTarget = false;
        _pendingAddToken = null;
        _pendingShowArgs = null;
        _pendingEditArgs = null;
        _pendingXVerb = null;
        _pendingXVerbArgs = "";
        _pendingAreaVerb = null;
        _pendingAreaRange = 0;
        _pendingControlTarget = false;
        _pendingDupeTarget = false;
        _pendingHealTarget = false;
        _pendingBankTarget = false;
        _pendingSummonToTarget = false;
        _pendingMountTarget = false;
        _pendingSummonCageTarget = false;
        _pendingRemoveTarget = false;
        _pendingResurrectTarget = false;
        _pendingInspectTarget = false;
        _pendingTargetFunction = null;
        _pendingTargetArgs = "";
        _pendingTargetAllowGround = false;
        _pendingTargetItemUid = Serial.Invalid;
        _pendingScriptNewItem = null;
        _lastScriptTargetPoint = null;
        _targetCursorActive = false;
    }

    /// <summary>Source-X Cmd_EditItem parity (CClientUse.cpp:577).
    /// If the target is a container (Item with contents, or Character with
    /// equipment) we display a 0x7C item-list menu so the GM can pick a
    /// child object to inspect.  Non-containers go straight to the prop
    /// dialog.</summary>
    public void ShowInspectDialog(uint uid, int requestedPage = 0)
    {
        if (_character == null) return;
        ObjBase? obj = _world.FindObject(new Serial(uid));
        if (obj == null)
        {
            SysMessage(ServerMessages.GetFormatted("gm_object_serial", $"{uid:X8}"));
            return;
        }

        var childItems = CollectContainerChildren(obj);
        if (childItems.Count == 0)
        {
            OpenInspectPropDialog(obj, requestedPage);
            return;
        }

        var entries = new List<MenuItemEntry>();
        var uids = new List<uint>();
        var mems = new List<Item?>();
        int max = Math.Min(childItems.Count, 254);
        for (int i = 0; i < max; i++)
        {
            var item = childItems[i];
            uids.Add(item.Uid.Value);
            ushort hue = 0;
            if (item.ItemType == Core.Enums.ItemType.EqMemoryObj)
            {
                var targetName = item.Link.IsValid ? (_world.FindObject(item.Link)?.Name ?? "?") : "?";
                entries.Add(new MenuItemEntry(item.BaseId, hue,
                    $"Memory: {targetName} [{item.GetMemoryTypes()}]"));
                mems.Add(item);
            }
            else
            {
                ushort rawHue = item.Hue;
                if (rawHue != 0)
                    hue = rawHue == 1 ? (ushort)0x7FF : (ushort)(rawHue - 1);
                entries.Add(new MenuItemEntry(item.BaseId, hue, item.Name));
                mems.Add(null);
            }
        }

        _pendingEditMenuUids = uids.ToArray();
        _pendingEditMenuMemories = mems.ToArray();
        _netState.Send(new PacketMenuDisplay(
            obj.Uid.Value, EditMenuId,
            $"Contents of {obj.Name}", entries));
    }

    public void HandleEditMenuChoice(ushort index)
    {
        if (_character == null) return;
        var uids = _pendingEditMenuUids;
        var mems = _pendingEditMenuMemories;
        _pendingEditMenuUids = null;
        _pendingEditMenuMemories = null;

        if (uids == null || index == 0 || index > uids.Length)
            return;

        uint picked = uids[index - 1];

        if (picked == 0 && mems != null && index - 1 < mems.Length)
        {
            var mem = mems[index - 1];
            if (mem != null)
            {
                var targetName = mem.Link.IsValid ? (_world.FindObject(mem.Link)?.Name ?? "?") : "?";
                SysMessage($"[Memory] Link=0x{mem.Link.Value:X8} ({targetName})");
                SysMessage($"  Types={mem.GetMemoryTypes()} Pos={mem.MoreP}");
                return;
            }
        }

        ShowInspectDialog(picked);
    }

    public void OpenInspectPropDialog(ObjBase obj, int requestedPage)
    {
        if (obj is Character)
            SendSkillList();

        string dialogId = obj is Item ? "d_itemprop1" : "d_charprop1";
        int page = Math.Max(0, requestedPage);
        if (OpenNamedDialog(dialogId, page, obj))
            return;

        SysMessage(ServerMessages.GetFormatted("gm_object_not_found", $"{obj.Uid.Value:X8}"));
    }

    private static List<Item> CollectContainerChildren(ObjBase obj)
    {
        var result = new List<Item>();
        if (obj is Item item && item.Contents.Count > 0)
        {
            foreach (var child in item.Contents)
                result.Add(child);
        }
        else if (obj is Character ch)
        {
            for (int i = 0; i < (int)Layer.Qty; i++)
            {
                var eq = ch.GetEquippedItem((Layer)i);
                if (eq != null)
                    result.Add(eq);
            }
            foreach (var mem in ch.Memories)
                result.Add(mem);
        }
        return result;
    }

    public void ShowTextDialog(string title, IReadOnlyList<string> lines)
    {
        if (_character == null)
            return;

        string combined = string.Join("\n", lines);
        var gump = new GumpBuilder(_character.Uid.Value, (uint)Math.Abs($"showdlg:{title}".GetHashCode()), 640, 420);
        gump.AddResizePic(0, 0, 5054, 640, 420);
        // Keep close button available by default for utility dialogs.
        gump.AddText(20, 15, 0, title);
        gump.AddHtmlGump(20, 45, 600, 180, EscapeHtml(combined).Replace("\n", "<br>"), true, true);
        // Text entry area allows easy select/copy by user.
        gump.AddText(20, 235, 0, "Copy-ready text:");
        gump.AddTextEntry(20, 260, 600, 120, 0, 1, combined);
        gump.AddButton(280, 390, 4005, 4007, 0);
        SendGump(gump);
    }

    private void SendUpdateMobile(Character ch)
    {
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);
        _netState.Send(new PacketMobileMoving(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z, (byte)ch.Direction,
            ch.Hue, flags, noto
        ));
    }

    private void SendUpdateMobileWithHue(Character ch, ushort hue)
    {
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);
        _netState.Send(new PacketMobileMoving(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z, (byte)ch.Direction,
            hue, flags, noto
        ));
    }

    private void SendUpdateMobileHidden(Character ch)
    {
        byte flags = (byte)(BuildMobileFlags(ch) | 0x80);
        byte noto = GetNotoriety(ch);
        _netState.Send(new PacketMobileMoving(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z, (byte)ch.Direction,
            ch.Hue, flags, noto
        ));
    }

    private void SendDrawObjectWithHue(Character ch, ushort hue)
    {
        var equipment = BuildEquipmentList(ch);
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);

        _netState.Send(new PacketDrawObject(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z,
            (byte)ch.Direction, hue, flags, noto,
            equipment
        ));
    }

    private void SendDrawObjectHidden(Character ch)
    {
        var equipment = BuildEquipmentList(ch);
        byte flags = (byte)(BuildMobileFlags(ch) | 0x80);
        byte noto = GetNotoriety(ch);

        _netState.Send(new PacketDrawObject(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z,
            (byte)ch.Direction, ch.Hue, flags, noto,
            equipment
        ));
    }

    private void SendWorldItem(Item item)
    {
        _netState.Send(new PacketWorldItem(
            item.Uid.Value, item.DispIdFull, item.Amount,
            item.X, item.Y, item.Z, item.Hue
        ));
    }

    private void SendWorldItemWithHue(Item item, ushort hue)
    {
        _netState.Send(new PacketWorldItem(
            item.Uid.Value, item.DispIdFull, item.Amount,
            item.X, item.Y, item.Z, hue
        ));
    }

    /// <summary>Place a dragged item into the target character's backpack and
    /// send the client a 0x25 ContainerItem packet so it actually appears there.
    /// Without the packet the client only sees the previous 0x1D delete and
    /// treats the item as gone — classic "drop onto mobile = item vanishes"
    /// bug. If the backpack is somehow missing we recreate one so the item
    /// doesn't simply get lost.</summary>
    private void PlaceItemInPack(Character target, Item item)
    {
        var pack = target.Backpack;
        if (pack == null && target.IsPlayer)
        {
            EnsurePlayerBackpack(target);
            pack = target.Backpack;
        }
        if (pack == null)
        {
            // NPC without a pack: fall back to equip layer Pack or drop at feet.
            _world.PlaceItem(item, target.Position);
            return;
        }

        pack.AddItem(item);
        item.Position = new Point3D(0, 0, 0, target.MapIndex);

        // Owner-side visual: only clients that already have the pack "open"
        // need the 0x25 update; but Sphere/ServUO both send it unconditionally
        // because it's cheap and keeps drag preview consistent. Send to the
        // owner who initiated the drop.
        _netState.Send(new PacketContainerItem(
            item.Uid.Value, item.DispIdFull, 0,
            item.Amount, item.X, item.Y,
            pack.Uid.Value, item.Hue,
            _netState.IsClientPost6017));

        if (item.BaseId == 0x0EED && target == _character)
            SendCharacterStatus(_character);
    }

    private void SendOpenContainer(Item container)
    {
        // Source-X CClient::addContainerSetup parity: before opening
        // any container the client must already know about that
        // container as either a worn item (0x2E) or a world item
        // (0x1A). Otherwise the 0x24 OpenContainer is silently
        // dropped because the client can't resolve the serial.
        // Bank box / backpack open from a fresh login (or right after
        // we lazily-create the bank box) is the common case where this
        // pre-broadcast is missing.
        var parentChar = container.ContainedIn.IsValid
            ? _world.FindChar(container.ContainedIn)
            : null;
        if (parentChar != null)
        {
            byte layer = (byte)container.EquipLayer;
            _netState.Send(new PacketWornItem(
                container.Uid.Value, container.BaseId, layer,
                parentChar.Uid.Value, container.Hue.Value));
        }

        // Per-container gump selection (Source-X CItemBase::IsTypeContainer
        // returns m_ttContainer.m_idGump = TDATA2; ServUO does the equivalent
        // via Data/containers.cfg ItemID→GumpID lookup).
        //
        // Resolution order:
        //   1) ITEMDEF.TDATA2 if the script supplied one — script wins.
        //   2) Built-in fallback table for the well-known UO container
        //      graphics (matches ServUO's Data/containers.cfg shipped table).
        //   3) Bank-box layer fallback to 0x004A (silver bank chest, used
        //      with item ids 0xE7C / 0x9AB) — keeps GM-spawned bank boxes
        //      that have no itemdef render correctly.
        //   4) Generic bag fallback 0x003C.
        ushort gumpId = ResolveContainerGump(container);
        _netState.Send(new PacketOpenContainer(container.Uid.Value, gumpId, _netState.IsClientPost7090));

        foreach (var child in _world.GetContainerContents(container.Uid))
        {
            _netState.Send(new PacketContainerItem(
                child.Uid.Value, child.DispIdFull, 0,
                child.Amount, child.X, child.Y,
                container.Uid.Value, child.Hue,
                _netState.IsClientPost6017
            ));
        }
    }

    /// <summary>
    /// Resolve the container gump (0x24 second word) for a given container item.
    /// Mirrors Source-X CItemBase::IsTypeContainer (TDATA2 = m_idGump) and the
    /// ServUO Data/containers.cfg fallback table. Adding a new container ID to
    /// the built-in table or to ITEMDEF.TDATA2 in script is enough — no other
    /// code path needs to know about it.
    /// </summary>
    private static ushort ResolveContainerGump(Item container)
    {
        // 1) Script-supplied TDATA2 wins.
        var idef = Definitions.DefinitionLoader.GetItemDef(container.BaseId);
        if (idef != null && idef.TData2 != 0)
            return (ushort)(idef.TData2 & 0xFFFF);

        // 2) Built-in ItemID -> GumpID table. Mirrors the ServUO
        // Data/containers.cfg shipped table; covers the well-known UO
        // container graphics so vanilla content renders correctly even
        // when no scripted ITEMDEF is loaded.
        switch (container.BaseId)
        {
            // 0x4A — silver bank chest (BankBox)
            case 0xE7C:
            case 0x9AB:
                return 0x004A;
            // 0x3D — small wooden chest with iron bands
            case 0xE76:
            case 0x2256:
            case 0x2257:
                return 0x003D;
            // 0x3E — wooden box (no banding)
            case 0xE77:
            case 0xE7F:
                return 0x003E;
            // 0x3F — gold-banded chest
            case 0xE7A:
            case 0x24D5:
            case 0x24D6:
            case 0x24D9:
            case 0x24DA:
                return 0x003F;
            // 0x42 — pouch / standard backpack
            case 0xE40:
            case 0xE41:
                return 0x0042;
            // 0x43 — wooden chest with no bands
            case 0xE7D:
            case 0x9AA:
                return 0x0043;
            // 0x44 — large wood box
            case 0xE7E:
            case 0x9A9:
            case 0xE3C:
            case 0xE3D:
            case 0xE3E:
            case 0xE3F:
                return 0x0044;
            // 0x49 — small wooden chest gilt edges
            case 0xE42:
            case 0xE43:
                return 0x0049;
            // 0x4B — large metal chest
            case 0xE80:
            case 0x9A8:
                return 0x004B;
            default:
                break;
        }

        // 3) Layer-based bank-box fallback (covers GM-spawned bank boxes
        // whose itemId we don't recognize).
        if (container.EquipLayer == Layer.BankBox)
            return 0x004A;

        // 4) Generic bag.
        return 0x003C;
    }

    /// <summary>
    /// Open the player's bank box. Creates it if it doesn't exist.
    /// The bank box is a container item stored on the character at a special layer.
    /// </summary>
    public void OpenBankBox()
    {
        if (_character == null) return;

        // Look for existing bank box item on the character
        var bankBox = _character.GetEquippedItem(Layer.BankBox);
        if (bankBox == null)
        {
            // Create bank box
            bankBox = _world.CreateItem();
            bankBox.BaseId = 0x09AB; // bank box container graphic
            bankBox.ItemType = ItemType.Container;
            bankBox.Name = "Bank Box";
            _character.Equip(bankBox, Layer.BankBox);
        }

        SendOpenContainer(bankBox);
    }

    private readonly Dictionary<uint, long> _paperdollThrottle = [];

    public void SendPaperdoll(Character ch)
    {
        long now = Environment.TickCount64;
        if (_paperdollThrottle.TryGetValue(ch.Uid.Value, out long last) && now - last < 2000)
            return;
        _paperdollThrottle[ch.Uid.Value] = now;

        string title = string.IsNullOrEmpty(ch.Title)
            ? ch.GetName()
            : $"{ch.GetName()}, {ch.Title}";
        byte paperdollFlags = 0;
        if (_character != null && ch == _character) paperdollFlags |= 0x01; // can edit (own paperdoll)
        _netState.Send(new PacketOpenPaperdoll(ch.Uid.Value, title, paperdollFlags));

        SendCharacterStatus(ch, includeExtendedStats: ch == _character);
    }

    private void RefreshBackpackContents()
    {
        if (_character == null) return;
        var pack = _character.Backpack;
        if (pack == null) return;

        foreach (var child in _world.GetContainerContents(pack.Uid))
        {
            _netState.Send(new PacketContainerItem(
                child.Uid.Value, child.DispIdFull, 0,
                child.Amount, child.X, child.Y,
                pack.Uid.Value, child.Hue,
                _netState.IsClientPost6017));
        }
    }

    public void SendCharacterStatus(Character ch, bool includeExtendedStats = true)
    {
        // Expansion level matched to client version capabilities
        byte expansion;
        if (_netState.IsClientPost7090)
            expansion = 5; // ML (SA client can handle ML fields)
        else if (_netState.IsClientPost6017)
            expansion = 4; // SE
        else if (_netState.ClientVersionNumber >= 40_000_000)
            expansion = 3; // AOS
        else
            expansion = 0; // pre-AOS
        string statusName = ResolveStatusName(ch);
        var (hits, maxHits) = NormalizeStatusPair(ch.Hits, ch.MaxHits, ch.Str);
        var (stam, maxStam) = NormalizeStatusPair(ch.Stam, ch.MaxStam, ch.Dex);
        var (mana, maxMana) = NormalizeStatusPair(ch.Mana, ch.MaxMana, ch.Int);

        int gold = 0;
        var pack = ch.Backpack;
        if (pack != null)
            foreach (var gi in pack.Contents)
                if (gi.BaseId == 0x0EED) gold += gi.Amount;

        _netState.Send(new PacketStatusFull(
            ch.Uid.Value, statusName,
            hits, maxHits,
            ch.Str, ch.Dex, ch.Int,
            stam, maxStam, mana, maxMana,
            gold, (ushort)0, (ushort)0,
            ch.Fame, ch.Karma, 0, expansion
        ));

        // Keep self bars synchronized on clients that rely on A1/A2/A3 updates.
        if (_character != null && ch == _character)
        {
            _netState.Send(new PacketUpdateHealth(ch.Uid.Value, maxHits, hits));
            _netState.Send(new PacketUpdateMana(ch.Uid.Value, maxMana, mana));
            _netState.Send(new PacketUpdateStamina(ch.Uid.Value, maxStam, stam));
        }
    }

    private static uint StableStringHash(string s)
    {
        uint hash = 5381;
        foreach (char c in s)
            hash = ((hash << 5) + hash) ^ c;
        return hash;
    }

    private static (short Cur, short Max) NormalizeStatusPair(short cur, short max, short fallbackBase)
    {
        short safeMax = max > 0 ? max : (short)Math.Max(1, (int)fallbackBase);
        short safeCur = (short)Math.Clamp(cur, (short)0, safeMax);
        return (safeCur, safeMax);
    }

    private void BroadcastDeleteObject(uint uid)
    {
        _netState.Send(new PacketDeleteObject(uid));
        _knownChars.Remove(uid);
        _knownItems.Remove(uid);
        _lastKnownPos.Remove(uid);
        // excludeUid must be the CHARACTER's UID (not the deleted object's UID)
        // so the sending client is excluded from the broadcast (already got direct send).
        BroadcastNearby?.Invoke(_character?.Position ?? Point3D.Zero, UpdateRange, new PacketDeleteObject(uid), _character?.Uid.Value ?? 0);
    }

    private void BroadcastDrawObject(Character ch)
    {
        var equipment = BuildEquipmentList(ch);
        byte flags = BuildMobileFlags(ch);
        byte noto = GetNotoriety(ch);
        var drawObj = new PacketDrawObject(
            ch.Uid.Value, ch.BodyId,
            ch.X, ch.Y, ch.Z,
            (byte)ch.Direction, ch.Hue, flags, noto,
            equipment);
        _netState.Send(drawObj);
        // Use BroadcastMoveNearby (if available) to also update receiving clients'
        // _lastKnownPos cache, preventing a duplicate 0x77 from the next view delta.
        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby.Invoke(ch.Position, UpdateRange, drawObj, _character?.Uid.Value ?? 0, ch);
        else
            BroadcastNearby?.Invoke(ch.Position, UpdateRange, drawObj, _character?.Uid.Value ?? 0);
    }

    /// <summary>Resolves a target serial to a <see cref="Character"/>.
    /// If the serial is a corpse, falls back to the corpse's
    /// <c>OWNER_UID</c> tag (set by <see cref="DeathEngine"/>).</summary>
    private Character? ResolvePickedChar(uint uid)
    {
        if (uid == 0 || uid == 0xFFFFFFFF) return null;
        var ch = _world.FindChar(new Serial(uid));
        if (ch != null) return ch;
        var corpse = _world.FindItem(new Serial(uid));
        if (corpse != null && corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
            uint.TryParse(ownerStr, out uint ownerUid))
            return _world.FindChar(new Serial(ownerUid));
        return null;
    }

    /// <summary>Source-X CV_NUKE / CV_NUKECHAR / CV_NUDGE area
    /// implementation. Iterates the world sectors around
    /// <paramref name="centre"/> at <paramref name="range"/> tiles and
    /// applies the verb. Returns the number of objects affected.</summary>
    private int ExecuteAreaVerb(string verb, Point3D centre, int range)
    {
        if (_character == null) return 0;
        int affected = 0;
        switch (verb)
        {
            case "NUKE":
            {
                // Snapshot first — DeleteObject mutates the sector lists.
                var items = _world.GetItemsInRange(centre, range).ToList();
                foreach (var item in items)
                {
                    if (item.IsEquipped) continue;          // GM gear safe
                    if (item.ContainedIn.IsValid) continue; // bag contents safe
                    BroadcastDeleteObject(item.Uid.Value);
                    _world.DeleteObject(item);
                    item.Delete();
                    affected++;
                }
                break;
            }
            case "NUKECHAR":
            {
                var chars = _world.GetCharsInRange(centre, range).ToList();
                foreach (var ch in chars)
                {
                    if (ch == _character) continue;
                    if (ch.IsPlayer) continue;              // never auto-purge real players
                    BroadcastDeleteObject(ch.Uid.Value);
                    _world.DeleteObject(ch);
                    ch.Delete();
                    affected++;
                }
                break;
            }
            case "NUDGE":
            {
                // Source-X reads TARG.X/Y/Z TAGs as the displacement.
                // We default to (0, 0, +1) when the GM has not set them
                // — useful for "lift items 1 tile up to clear floors".
                int dx = TryGetIntTag("NUDGE.DX", 0);
                int dy = TryGetIntTag("NUDGE.DY", 0);
                int dz = TryGetIntTag("NUDGE.DZ", 1);
                if (dx == 0 && dy == 0 && dz == 0) dz = 1;
                foreach (var item in _world.GetItemsInRange(centre, range).ToList())
                {
                    if (item.ContainedIn.IsValid) continue;
                    var p = item.Position;
                    var np = new Point3D(
                        (short)(p.X + dx),
                        (short)(p.Y + dy),
                        (sbyte)(p.Z + dz),
                        p.Map);
                    BroadcastDeleteObject(item.Uid.Value);
                    _world.PlaceItem(item, np);
                    BroadcastNearby?.Invoke(np, UpdateRange,
                        new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                            item.X, item.Y, item.Z, item.Hue), 0);
                    affected++;
                }
                break;
            }
        }
        return affected;
    }

    private int TryGetIntTag(string key, int defaultValue)
    {
        if (_character != null && _character.TryGetTag(key, out string? v) &&
            int.TryParse(v, out int n))
            return n;
        return defaultValue;
    }

    /// <summary>Source-X CV_DUPE: clones an item next to the original.
    /// Copies BaseId, Hue and Amount; container/equipped duplicates are
    /// not supported (matches Source-X CClient_functions.tbl behaviour
    /// for items in the world).</summary>
    private Item? DuplicateItem(Item src)
    {
        if (_character == null) return null;
        var dup = _world.CreateItem();
        dup.BaseId = src.BaseId;
        dup.Hue = src.Hue;
        dup.Amount = src.Amount > 0 ? src.Amount : (ushort)1;
        if (!string.IsNullOrEmpty(src.Name)) dup.Name = src.Name;
        if (src.ContainedIn.IsValid)
            PlaceItemInPack(_character, dup);
        else
            _world.PlaceItem(dup, src.Position);
        BroadcastNearby?.Invoke(dup.Position, UpdateRange,
            new PacketWorldItem(dup.Uid.Value, dup.DispIdFull, dup.Amount,
                dup.X, dup.Y, dup.Z, dup.Hue), 0);
        return dup;
    }

    /// <summary>Source-X CV_SUMMONCAGE helper: drop iron-bar items in
    /// the 8 tiles surrounding <paramref name="centre"/> so the victim
    /// can't walk away. The bars are real items (visible to the client)
    /// and persist until manually removed.</summary>
    private void SpawnCageAround(Point3D centre)
    {
        // Bar graphics:  0x0084 vertical, 0x0086 horizontal (Source-X
        // i_bars_v / i_bars_h). We skip diagonal corners — the picture
        // is a "+" pattern around the victim, enough to block movement.
        var ring = new (short dx, short dy, ushort gfx)[]
        {
            ( 0, -1, 0x0086), ( 0,  1, 0x0086),
            (-1,  0, 0x0084), ( 1,  0, 0x0084),
        };
        foreach (var (dx, dy, gfx) in ring)
        {
            var bar = _world.CreateItem();
            bar.BaseId = gfx;
            bar.Amount = 1;
            var p = new Point3D((short)(centre.X + dx), (short)(centre.Y + dy),
                centre.Z, centre.Map);
            _world.PlaceItem(bar, p);
            BroadcastNearby?.Invoke(p, UpdateRange,
                new PacketWorldItem(bar.Uid.Value, bar.DispIdFull, bar.Amount,
                    bar.X, bar.Y, bar.Z, bar.Hue), 0);
        }
    }

    /// <summary>Source-X CV_BANK with a target arg: opens the picked
    /// character's bank box on this client. Mirrors CChar::Use_Obj
    /// for BankBox layer items.</summary>
    private void OpenForeignBank(Character victim)
    {
        var bank = victim.GetEquippedItem(Layer.BankBox);
        if (bank == null)
        {
            // Conjure a transient empty bank box so the GM still gets a UI.
            OpenBankBox();
            return;
        }
        // Reuse the standard open-container flow on the bank serial.
        _netState.Send(new PacketOpenContainer(bank.Uid.Value, 0x003C));
    }

    private bool RemoveTargetedObject(uint uid)
    {
        if (_character == null)
            return false;
        if (uid == _character.Uid.Value)
            return false;

        var item = _world.FindItem(new Serial(uid));
        if (item != null)
        {
            _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Destroy,
                new TriggerArgs { CharSrc = _character, ItemSrc = item });
            BroadcastDeleteObject(uid);
            _world.DeleteObject(item);
            item.Delete();
            return true;
        }

        var ch = _world.FindChar(new Serial(uid));
        if (ch != null)
        {
            if (ch == _character)
                return false;

            _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Destroy,
                new TriggerArgs { CharSrc = _character });
            BroadcastDeleteObject(uid);
            _world.DeleteObject(ch);
            ch.Delete();
            return true;
        }

        return false;
    }

    private bool TryAddAtTarget(string token, Point3D targetPos, uint targetSerial = 0)
    {
        if (_character == null || _commands?.Resources == null)
            return false;

        string cleaned = token
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (cleaned.Length == 0)
            return false;

        var resources = _commands.Resources;
        var rid = resources.ResolveDefName(cleaned);
        if (rid.IsValid)
        {
            if (rid.Type == ResType.ItemDef)
            {
                ushort dispId = ResolveItemDispId(rid.Index);
                if (dispId == 0)
                {
                    SysMessage(ServerMessages.GetFormatted("gm_item_no_graphic", cleaned));
                    return true;
                }

                var item = _world.CreateItem();
                item.BaseId = dispId;
                item.Name = cleaned;

                var namedDef = DefinitionLoader.GetItemDef(rid.Index);
                if (namedDef != null)
                {
                    item.ItemType = namedDef.Type;
                    if (!string.IsNullOrWhiteSpace(namedDef.Name))
                        item.Name = DefinitionLoader.ResolveNames(namedDef.Name);
                    foreach (var ev in namedDef.Events)
                        if (!item.Events.Contains(ev))
                            item.Events.Add(ev);

                    if (rid.Index != dispId)
                        item.SetTag("SCRIPTDEF", rid.Index.ToString());
                }

                PlaceAddedItem(item, targetPos, targetSerial);
                SysMessage(ServerMessages.GetFormatted("gm_item_created", cleaned, $"{dispId:X}"));
                return true;
            }

            if (rid.Type == ResType.CharDef)
            {
                var npc = CreateNpcFromDef(rid.Index, cleaned);
                _logger.LogDebug(
                    "[npc_spawn] BEFORE @Create: def='{Def}' STR={Str} MaxHits={MH} Hits={H} DEX={Dex} INT={Int}",
                    cleaned, npc.Str, npc.MaxHits, npc.Hits, npc.Dex, npc.Int);
                _world.PlaceCharacter(npc, targetPos);
                var preCreateBrain = npc.NpcBrain;
                _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.Create, new TriggerArgs { CharSrc = _character });
                _logger.LogDebug(
                    "[npc_spawn] AFTER @Create: def='{Def}' STR={Str} MaxHits={MH} Hits={H}",
                    cleaned, npc.Str, npc.MaxHits, npc.Hits);
                FinalizeNpcBrain(npc);
                _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.CreateLoot, new TriggerArgs { CharSrc = _character });
                npc.Hits = npc.MaxHits;
                npc.Stam = npc.MaxStam;
                npc.Mana = npc.MaxMana;
                _logger.LogDebug(
                    "[npc_spawn] AFTER @CreateLoot: def='{Def}' STR={Str} MaxHits={MH} Hits={H} brain={Brain}",
                    cleaned, npc.Str, npc.MaxHits, npc.Hits, npc.NpcBrain);
                BroadcastDrawObject(npc);
                SysMessage(ServerMessages.GetFormatted("gm_npc_created2", npc.Name, $"{rid.Index:X}", targetPos));
                return true;
            }
        }

        string num = cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? cleaned[2..] : cleaned;
        bool parsed = ushort.TryParse(num, System.Globalization.NumberStyles.HexNumber, null, out ushort idHex) ||
                      ushort.TryParse(cleaned, out idHex);
        if (!parsed)
            return false;

        bool hasItemDef = resources.GetResource(ResType.ItemDef, idHex) != null;
        bool hasCharDef = resources.GetResource(ResType.CharDef, idHex) != null;
        if (hasItemDef || !hasCharDef)
        {
            var item = _world.CreateItem();
            item.BaseId = idHex;
            item.Name = $"Item_{idHex:X}";
            PlaceAddedItem(item, targetPos, targetSerial);
            SysMessage(ServerMessages.GetFormatted("gm_item_created_hex", $"{idHex:X}", targetPos));
            return true;
        }

        var createdNpc = CreateNpcFromDef(idHex, $"NPC_{idHex:X}");
        _world.PlaceCharacter(createdNpc, targetPos);
        _triggerDispatcher?.FireCharTrigger(createdNpc, CharTrigger.Create, new TriggerArgs { CharSrc = _character });
        FinalizeNpcBrain(createdNpc);
        _triggerDispatcher?.FireCharTrigger(createdNpc, CharTrigger.CreateLoot, new TriggerArgs { CharSrc = _character });
        createdNpc.Hits = createdNpc.MaxHits;
        createdNpc.Stam = createdNpc.MaxStam;
        createdNpc.Mana = createdNpc.MaxMana;
        BroadcastDrawObject(createdNpc);
        SysMessage(ServerMessages.GetFormatted("gm_npc_created_hex", createdNpc.Name, $"{idHex:X}", targetPos));
        return true;
    }

    private void PlaceAddedItem(Item item, Point3D groundPos, uint targetSerial)
    {
        if (targetSerial != 0 && targetSerial != 0xFFFFFFFF)
        {
            var targetChar = _world.FindChar(new Serial(targetSerial));
            if (targetChar != null)
            {
                PlaceItemInPack(targetChar, item);
                return;
            }
            var targetContainer = _world.FindItem(new Serial(targetSerial));
            if (targetContainer != null && targetContainer.ItemType is ItemType.Container or ItemType.ContainerLocked)
            {
                targetContainer.AddItem(item);
                _netState.Send(new PacketContainerItem(
                    item.Uid.Value, item.DispIdFull, 0,
                    item.Amount, item.X, item.Y,
                    targetContainer.Uid.Value, item.Hue,
                    _netState.IsClientPost6017));
                return;
            }
        }
        _world.PlaceItem(item, groundPos);
    }

    /// <summary>Walk the ITEMDEF chain to find the concrete UO art ID.
    /// A scripted itemdef may set <c>id=</c> to another defname that in
    /// turn resolves to a hex graphic (common Sphere pattern:
    /// <c>[itemdef i_moongate] id=i_moongate_blue</c>, where
    /// <c>i_moongate_blue</c> is <c>[itemdef 0f6c]</c>). Returns 0 when
    /// no numeric graphic can be reached within a small lookup bound —
    /// the caller treats that as "unknown graphic" and aborts the add.</summary>
    private static ushort ResolveItemDispId(int defIndex)
    {
        for (int hop = 0; hop < 8; hop++)
        {
            var d = DefinitionLoader.GetItemDef(defIndex);

            // Numeric itemdef ([ITEMDEF 0f6c]) is keyed by its hex value.
            // If no def exists at that hex, the index IS the graphic.
            if (d == null) return (ushort)(defIndex & 0xFFFF);

            // Def exists but has no explicit ID/DISPID. For numeric-range
            // sections (<= 0xFFFF) the section header itself is the
            // graphic — treat defIndex as the graphic. For hash-range
            // (named) sections without a DispIndex, we truly can't
            // resolve a graphic and the add fails.
            if (d.DispIndex == 0)
                return defIndex <= 0xFFFF ? (ushort)defIndex : (ushort)0;

            // DispIndex may itself point to another named itemdef (hash
            // index that resolves through _itemDefs). Follow the chain.
            if (DefinitionLoader.GetItemDef(d.DispIndex) is { } next && next != d)
            {
                defIndex = d.DispIndex;
                continue;
            }
            return d.DispIndex;
        }
        return 0;
    }

    private Character CreateNpcFromDef(int defIndexOrBaseId, string fallbackName)
    {
        var npc = _world.CreateCharacter();
        ushort safeBaseId = (ushort)Math.Clamp(defIndexOrBaseId, 0, ushort.MaxValue);
        npc.BaseId = safeBaseId;
        // Trigger / CharDef lookups need the full 24-bit defname hash, the
        // ushort BaseId truncates it and routes c_alchemist's @Create to
        // c_man (brain=Human) and misses c_banker entirely (brain=Animal).
        npc.CharDefIndex = defIndexOrBaseId;
        npc.Name = fallbackName;
        npc.BodyId = safeBaseId;
        npc.IsPlayer = false;

        var charDef = DefinitionLoader.GetCharDef(defIndexOrBaseId);
        if (charDef != null)
        {
            ushort resolvedBody = ResolveCharBodyId(charDef, safeBaseId);
            npc.BodyId = resolvedBody;
            // BaseId mirrors the resolved display body for legacy
            // consumers (mounting / click behaviour). Trigger / CharDef
            // lookups now go through CharDefIndex (full 24-bit defname
            // hash), so the c_alchemist→c_man aliasing no longer
            // hijacks @Create or brain selection.
            npc.BaseId = resolvedBody;
            if (!string.IsNullOrWhiteSpace(charDef.Name))
                npc.Name = DefinitionLoader.ResolveNames(charDef.Name);

            int strVal = charDef.StrMax > 0 ? charDef.StrMax : Math.Max(1, charDef.StrMin);
            int dexVal = charDef.DexMax > 0 ? charDef.DexMax : Math.Max(1, charDef.DexMin);
            int intVal = charDef.IntMax > 0 ? charDef.IntMax : Math.Max(1, charDef.IntMin);

            npc.Str = (short)Math.Clamp(strVal, 1, short.MaxValue);
            npc.Dex = (short)Math.Clamp(dexVal, 1, short.MaxValue);
            npc.Int = (short)Math.Clamp(intVal, 1, short.MaxValue);

            int hits = charDef.HitsMax > 0 ? charDef.HitsMax : Math.Max(1, strVal);
            short maxHits = (short)Math.Clamp(hits, 1, short.MaxValue);
            npc.MaxHits = maxHits;
            npc.Hits = maxHits;
            npc.MaxMana = npc.Int;
            npc.Mana = npc.Int;
            npc.MaxStam = npc.Dex;
            npc.Stam = npc.Dex;

            if (charDef.NpcBrain != NpcBrainType.None)
                npc.NpcBrain = charDef.NpcBrain;

            string? colorText = charDef.TagDefs.Get("COLOR");
            if (TryParseHue(colorText, out ushort hue))
                npc.Hue = new Color(hue);

            // Elemental damage percentages
            if (charDef.DamFire != 0) npc.DamFire = charDef.DamFire;
            if (charDef.DamCold != 0) npc.DamCold = charDef.DamCold;
            if (charDef.DamPoison != 0) npc.DamPoison = charDef.DamPoison;
            if (charDef.DamEnergy != 0) npc.DamEnergy = charDef.DamEnergy;
            if (charDef.DamPhysical != 0) npc.DamPhysical = charDef.DamPhysical;
            else if (charDef.DamFire != 0 || charDef.DamCold != 0 || charDef.DamPoison != 0 || charDef.DamEnergy != 0)
                npc.DamPhysical = (short)(100 - charDef.DamFire - charDef.DamCold - charDef.DamPoison - charDef.DamEnergy);

            // Equip ITEMNEWBIE items
            EquipNewbieItems(npc, charDef);
        }
        else
        {
            npc.Str = 50; npc.Dex = 50; npc.Int = 50;
            npc.MaxHits = 50; npc.Hits = 50;
            npc.MaxMana = 50; npc.Mana = 50;
            npc.MaxStam = 50; npc.Stam = 50;
        }

        // Brain finalisation (Animal fallback + @NPCRestock for vendors)
        // intentionally happens AFTER @Create runs — see FinalizeNpcBrain.
        // Sphere scripts set NPC=brain_vendor inside ON=@Create, so the brain
        // is only known once that trigger has executed.

        return npc;
    }

    /// <summary>
    /// Apply the post-@Create brain rules: default to Animal when nothing
    /// set a brain, and fire @NPCRestock for vendors so they come stocked.
    /// Call this AFTER FireCharTrigger(Create), never before.
    /// </summary>
    private void FinalizeNpcBrain(Character npc)
    {
        if (npc.NpcBrain == NpcBrainType.None)
            npc.NpcBrain = NpcBrainType.Animal;

        if (npc.NpcBrain == NpcBrainType.Vendor)
        {
            _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCRestock,
                new TriggerArgs { CharSrc = npc });
        }
    }

    private ushort ResolveCharBodyId(CharDef charDef, ushort fallbackBaseId)
    {
        if (charDef.DispIndex > 0)
            return charDef.DispIndex;

        string alias = charDef.DisplayIdRef?.Trim() ?? "";
        if (alias.Length == 0 || _commands?.Resources == null)
            return fallbackBaseId;

        var rid = _commands.Resources.ResolveDefName(alias);
        if (rid.IsValid && rid.Type == ResType.CharDef)
        {
            var refDef = DefinitionLoader.GetCharDef(rid.Index);
            if (refDef?.DispIndex > 0)
                return refDef.DispIndex;

            if (rid.Index >= 0 && rid.Index <= ushort.MaxValue)
                return (ushort)rid.Index;
        }

        return fallbackBaseId;
    }

    private static bool TryParseHue(string? value, out ushort hue)
    {
        hue = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(v[2..], System.Globalization.NumberStyles.HexNumber, null, out hue);
        if (v.StartsWith('0') && v.Length > 1 &&
            ushort.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out ushort hexHue))
        {
            hue = hexHue;
            return true;
        }
        return ushort.TryParse(v, out hue);
    }

    private void EquipNewbieItems(Character npc, CharDef charDef)
    {
        if (charDef.NewbieItems.Count == 0 || _commands?.Resources == null)
            return;

        var resources = _commands.Resources;
        ushort lastHue = 0; // for COLOR=match_hair / match_*
        foreach (var entry in charDef.NewbieItems)
        {
            // Resolve random_* / weighted-template pools to a single itemdef.
            string pickedName = TemplateEngine.PickRandomItemDefName(entry.DefName);
            if (string.IsNullOrWhiteSpace(pickedName))
                continue;

            var rid = resources.ResolveDefName(pickedName);
            if (!rid.IsValid || rid.Type != ResType.ItemDef)
                continue;

            var item = _world.CreateItem();
            var itemDef = DefinitionLoader.GetItemDef(rid.Index);
            // Defname ITEMDEFs (i_shirt_plain, …) live under a 32-bit
            // string hash, but their wire graphic is in DispIndex —
            // see TemplateEngine.ResolveDispId. Truncating rid.Index
            // to ushort previously gave newbies random-tile clothing
            // (lava breastplates, window-shutter shirts).
            ushort dispId = 0;
            if (itemDef != null)
            {
                if (itemDef.DispIndex != 0) dispId = itemDef.DispIndex;
                else if (itemDef.DupItemId != 0) dispId = itemDef.DupItemId;
            }
            if (dispId == 0 && rid.Index <= 0xFFFF) dispId = (ushort)rid.Index;
            if (dispId == 0) continue;
            item.BaseId = dispId;

            // Store raw NAME= template; Item.GetName() resolves
            // %plural/singular% markers per Amount on every read.
            if (itemDef != null && !string.IsNullOrWhiteSpace(itemDef.Name))
                item.Name = itemDef.Name;

            // Amount: explicit wins; else dice roll; else leave default (1).
            int amount = entry.Amount;
            if (amount <= 0 && !string.IsNullOrWhiteSpace(entry.Dice))
                amount = RollSphereDice(entry.Dice);
            if (amount > 1)
                item.Amount = (ushort)Math.Min(amount, ushort.MaxValue);

            // Color: colors_* defname → random hue from the range;
            // "match_<prev>" → re-use the last resolved hue so a hair /
            // beard pair share the tint.
            if (!string.IsNullOrWhiteSpace(entry.Color))
            {
                ushort hue = ResolveColorDefName(entry.Color!, lastHue);
                if (hue != 0)
                {
                    item.Hue = new Color(hue);
                    lastHue = hue;
                }
            }

            Layer layer = itemDef?.Layer ?? Layer.None;
            if (layer == Layer.None && _world.MapData != null)
            {
                var tile = _world.MapData.GetItemTileData(item.BaseId);
                if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Wearable) != 0 &&
                    tile.Quality > 0 && tile.Quality <= (byte)Layer.Horse)
                {
                    layer = (Layer)tile.Quality;
                }
            }
            if (layer == Layer.None)
            {
                var pack = npc.Backpack;
                if (pack == null)
                {
                    pack = _world.CreateItem();
                    pack.BaseId = 0x0E75;
                    npc.Equip(pack, Layer.Pack);
                }
                pack.AddItem(item);
            }
            else
            {
                npc.Equip(item, layer);
            }
        }
    }

    /// <summary>Very small Sphere dice roller. Supports R&lt;max&gt;
    /// (1..max) and NdM (N M-sided). Anything unrecognised falls back
    /// to 1 so a broken script line never silently spawns a 0-amount
    /// item.</summary>
    private static int RollSphereDice(string expr)
    {
        expr = expr.Trim();
        if (expr.Length == 0) return 1;
        if ((expr[0] == 'R' || expr[0] == 'r') &&
            int.TryParse(expr.AsSpan(1), out int max) && max > 0)
            return Random.Shared.Next(1, max + 1);
        int dIdx = expr.IndexOf('d');
        if (dIdx < 0) dIdx = expr.IndexOf('D');
        if (dIdx > 0 &&
            int.TryParse(expr.AsSpan(0, dIdx), out int n) && n > 0 &&
            int.TryParse(expr.AsSpan(dIdx + 1), out int sides) && sides > 0)
        {
            int total = 0;
            for (int i = 0; i < n; i++) total += Random.Shared.Next(1, sides + 1);
            return total;
        }
        return int.TryParse(expr, out int literal) && literal > 0 ? literal : 1;
    }

    /// <summary>Resolve a <c>colors_*</c> / <c>match_*</c> defname to an
    /// actual hue value. Source-X defines <c>colors_skin</c>,
    /// <c>colors_hair</c>, <c>colors_red</c> etc. as DEF[NAME] entries
    /// containing <c>{low high}</c> ranges; we mirror the common ones
    /// inline so scripts using the standard palette work without the
    /// color-defs scp file. Unknown names fall through to numeric parse.</summary>
    private static ushort ResolveColorDefName(string name, ushort lastHue)
    {
        string n = name.Trim();
        if (string.IsNullOrEmpty(n)) return 0;
        // match_hair / match_skin / match_* → use the previously picked hue.
        if (n.StartsWith("match_", StringComparison.OrdinalIgnoreCase))
            return lastHue;

        // Canonical palette ranges (inclusive) — matches classic
        // Sphere defaults shipped with the standard script pack.
        (ushort lo, ushort hi) = n.ToLowerInvariant() switch
        {
            "colors_skin" => ((ushort)0x03EA, (ushort)0x03F2),
            "colors_hair" => ((ushort)0x044E, (ushort)0x0455),
            "colors_red" => ((ushort)0x0020, (ushort)0x002C),
            "colors_orange" => ((ushort)0x002D, (ushort)0x0038),
            "colors_yellow" => ((ushort)0x0039, (ushort)0x0044),
            "colors_green" => ((ushort)0x0059, (ushort)0x0062),
            "colors_blue" => ((ushort)0x0053, (ushort)0x0058),
            "colors_purple" => ((ushort)0x0010, (ushort)0x001E),
            "colors_neutral" => ((ushort)0x03B0, (ushort)0x03B4),
            "colors_all" => ((ushort)0x0002, (ushort)0x03E9),
            _ => ((ushort)0, (ushort)0),
        };
        if (lo != 0 || hi != 0)
            return (ushort)Random.Shared.Next(lo, hi + 1);

        // Last resort: numeric hex/dec literal (COLOR=0x0481 style).
        if (TryParseHue(n, out ushort direct))
            return direct;
        return 0;
    }

    private void ShowHelpPageDialog(int requestedPage)
    {
        if (_character == null)
            return;

        int page = Math.Clamp(requestedPage, 1, 4);
        _character.SetTag("help_type", page.ToString());

        string[] menu = ["Genel", "Yardim", "Stuck", "Istatistik"];

        var gump = new GumpBuilder(_character.Uid.Value, (uint)Math.Abs("d_helppage".GetHashCode()), 500, 360);
        gump.AddResizePic(0, 0, 5054, 500, 360)
            .AddResizePic(15, 15, 2620, 130, 300)
            .AddResizePic(155, 15, 2620, 330, 300)
            .AddText(30, 25, 0x0481, "Help")
            .AddText(175, 25, 0x0481, "Bilgi");

        for (int i = 0; i < menu.Length; i++)
        {
            int idx = i + 1;
            int y = 65 + (i * 42);
            gump.AddButton(28, y, 4005, 4007, idx)
                .AddText(62, y + 2, idx == page ? (ushort)0x0021 : (ushort)0x0481, menu[i]);
        }

        string pageTitle = menu[page - 1];
        gump.AddText(175, 60, 0x0481, pageTitle);

        switch (page)
        {
            case 1:
                gump.AddHtmlGump(175, 90, 280, 160,
                    "Genel yardim menusu.<br><br>Detayli sistemler daha sonra script tarafindan doldurulabilir.",
                    true, true);
                break;
            case 2:
                gump.AddHtmlGump(175, 90, 280, 120,
                    "Sorunun varsa staff'a page atabilir veya mevcut page durumunu kontrol edebilirsin.",
                    true, true)
                    .AddButton(175, 235, 4005, 4007, 21)
                    .AddText(210, 237, 0x0481, "Page")
                    .AddButton(300, 235, 4005, 4007, 22)
                    .AddText(335, 237, 0x0481, "Page List");
                break;
            case 3:
                gump.AddHtmlGump(175, 90, 280, 120,
                    "Karakterin takildiysa uygun bir guvenli nokta secerek cikabilirsin.",
                    true, true)
                    .AddButton(175, 235, 4005, 4007, 30)
                    .AddText(210, 237, 0x0481, "Town")
                    .AddButton(300, 235, 4005, 4007, 31)
                    .AddText(335, 237, 0x0481, "Inn");
                break;
            case 4:
            {
                var stats = _world.GetStats();
                gump.AddHtmlGump(175, 90, 280, 160,
                    $"Online Oyuncu: {_world.GetAllObjects().OfType<Character>().Count(c => c.IsPlayer && c.IsOnline)}<br>" +
                    $"Yaratik Sayisi: {stats.Chars}<br>" +
                    $"Esya Sayisi: {stats.Items}<br>" +
                    $"Sektor Sayisi: {stats.Sectors}",
                    true, true);
                break;
            }
        }

        gump.AddButton(455, 22, 4017, 4019, 0);

        SendGump(gump, (buttonId, _, _) =>
        {
            if (_character == null)
                return;
            if (buttonId == 0)
                return;

            if (buttonId is >= 1 and <= 4)
            {
                ShowHelpPageDialog((int)buttonId);
                return;
            }

            if (buttonId is >= 30 and <= 31)
            {
                SysMessage(ServerMessages.Get("msg_stuck_script"));
                return;
            }

            if (buttonId == 21)
            {
                SysMessage(ServerMessages.Get("msg_page_script"));
                return;
            }

            if (buttonId == 22)
            {
                SysMessage(ServerMessages.Get("msg_pagelist_script"));
            }
        });
    }

    /// <summary>Open a script-defined dialog ([DIALOG &lt;name&gt;] sections)
    /// on this client. Returns false when the dialog name cannot be
    /// resolved — caller logs or sysmessages accordingly. Public so
    /// admin commands (".dialog") and script-command handlers share the
    /// same code path.</summary>
    public bool TryShowScriptDialog(string dialogId, int requestedPage)
        => TryShowScriptDialog(dialogId, requestedPage, subject: null);

    /// <summary>Open a script DIALOG section. When <paramref name="subject"/>
    /// is non-null, bare property reads inside the dialog resolve against
    /// that object first (Source-X CLIMODE_DIALOG pObj semantics) — needed
    /// by d_charprop1 / d_itemprop1 where the gump is bound to an inspected
    /// target instead of the GM.</summary>
    public bool TryShowScriptDialog(string dialogId, int requestedPage, ObjBase? subject)
    {
        if (_character == null || _commands?.Resources == null)
            return false;

        if (!TryFindDialogSections(dialogId, out var layoutSection))
            return false;

        var textLines = LoadDialogTextLines(dialogId);

        var prevSubject = _dialogSubjectUid;
        _dialogSubjectUid = subject?.Uid ?? Serial.Invalid;
        try
        {
            return RenderScriptDialog(dialogId, requestedPage, layoutSection, subject?.Uid ?? Serial.Invalid, textLines);
        }
        finally
        {
            _dialogSubjectUid = prevSubject;
        }
    }

    private bool RenderScriptDialog(string dialogId, int requestedPage,
        SphereNet.Scripting.Parsing.ScriptSection layoutSection, Serial subjectUid,
        List<string>? textLines = null)
    {
        if (_character == null) return false;

        int currentPage = Math.Max(0, requestedPage);

        // Sphere dialog first line is the screen position "x,y".
        // Source-X reads this via s.ReadKey() before processing controls.
        int dialogX = 0, dialogY = 0;
        if (layoutSection.Keys.Count > 0)
        {
            string firstLine = layoutSection.Keys[0].Key.Trim();
            var posParts = firstLine.Split(',', StringSplitOptions.TrimEntries);
            if (posParts.Length >= 2 && int.TryParse(posParts[0], out int px) && int.TryParse(posParts[1], out int py))
            {
                dialogX = px;
                dialogY = py;
            }
        }

        var gump = new GumpBuilder(_character.Uid.Value, (uint)Math.Abs(dialogId.GetHashCode()))
        {
            ExplicitX = dialogX,
            ExplicitY = dialogY
        };
        int originX = 0, originY = 0;
        int cursorX = 0, cursorY = 0;
        // Separate "row tracker" for the `*N` operator. Sphere treats *N as a
        // fresh row step independent of the +/- cursor used for column work.
        int rowCursorX = 0, rowCursorY = 0;
        // Sphere/UO page semantics: content emitted before the first PAGE
        // marker belongs to page 0 (shared/common) and must render
        // immediately. Some imported dialogs (e.g. d_admin_player_tweak)
        // never declare an explicit PAGE 0 header, so starting hidden would
        // drop the entire layout and produce an almost-empty 0xDD packet.
        bool currentPageVisible = true;

        // Per-call local variable scope for LOCAL.x= assignments and
        // <local.x> / <dlocal.x> references — used by Sphere dialog
        // scripts that loop over a list (FOR) and emit a row per
        // iteration. Resolvers below look here first before delegating.
        var dialogLocals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Expand FOR / WHILE / IF blocks into a flat key sequence so the
        // render switch below can remain a linear walk. Each unrolled
        // copy of a loop body runs with the iterator's value substituted
        // into <local._for> / <local.n> / etc. before render commands see
        // the args — matching Sphere's runtime-expansion behaviour.
        var expandedKeys = ExpandDialogScriptKeys(layoutSection.Keys, dialogLocals, requestedPage);

        // Diagnostic: count of commands per page post-expansion. If page 4
        // (FLAGS) comes out empty while the others are populated, the
        // FOR/IF expansion isn't unrolling into output.
        {
            int currentP = 0;
            var perPage = new Dictionary<int, int>();
            foreach (var k in expandedKeys)
            {
                string ck = k.Key.Trim().ToUpperInvariant();
                if (ck == "PAGE" && int.TryParse(k.Arg.Trim(), out int np))
                {
                    currentP = np;
                    continue;
                }
                perPage[currentP] = perPage.GetValueOrDefault(currentP) + 1;
            }
            _logger.LogDebug("[dialog_expand] id={Id} keys={Total} pages={Pages}",
                dialogId, expandedKeys.Count,
                string.Join(", ", perPage.Select(kv => $"p{kv.Key}:{kv.Value}")));
        }

        foreach (var key in expandedKeys)
        {
            string cmd = key.Key.Trim().ToUpperInvariant();
            string args = key.Arg;
            switch (cmd)
            {
                case "NOMOVE":
                    gump.SetNoMove();
                    break;
                case "NOCLOSE":
                    gump.SetNoClose();
                    break;
                case "NODISPOSE":
                    gump.SetNoDispose();
                    break;
                case "PAGE":
                {
                    // UO page model is CLIENT-side: every page element
                    // lives in the same gump packet, the client switches
                    // visibility when a page-nav button fires. Emit a
                    // `{ page N }` marker and let every subsequent
                    // element render under that tag.
                    // Source-X does NOT reset m_iOriginX/m_iOriginY on
                    // PAGE — the DORIGIN baseline persists across pages
                    // so that PAGE 1 content can use +N offsets relative
                    // to the last DORIGIN set on PAGE 0.
                    int pageNo = ParseIntToken(args);
                    gump.SetPage(pageNo);
                    currentPageVisible = true;
                    break;
                }
                case "DORIGIN":
                {
                    var parts = SplitTokens(args, 2);
                    if (parts.Length >= 2)
                    {
                        // Sphere semantics: DORIGIN seeds the coordinate
                        // baseline for subsequent +/* resolution; commands
                        // that follow are already expressed in dialog-space.
                        // Adding originX/originY again at emit-time causes a
                        // double offset (notably d_spawn's right-side groups
                        // jump far to the right). Keep the runtime origin at
                        // zero and move the baseline cursors instead.
                        originX = 0;
                        originY = 0;
                        cursorX = ParseIntToken(parts[0]);
                        cursorY = ParseIntToken(parts[1]);
                        rowCursorX = cursorX;
                        rowCursorY = cursorY;
                    }
                    break;
                }
                case "RESIZE":
                case "RESIZEPIC":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 5);
                    if (parts.Length >= 5)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddResizePic(x, y, ParseIntToken(parts[2]), ParseIntToken(parts[3]), ParseIntToken(parts[4]));
                    }
                    else if (cmd == "RESIZE" && parts.Length == 4)
                    {
                        // Sphere RESIZE shorthand: x,y,width,height (no gumpId)
                        // Uses default background gump 9200.
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddResizePic(x, y, 9200, ParseIntToken(parts[2]), ParseIntToken(parts[3]));
                    }
                    break;
                }
                case "GUMPIC":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 4);
                    if (parts.Length >= 3)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int gumpId = ParseIntToken(parts[2]);
                        int hue = parts.Length >= 4 ? ParseIntToken(parts[3]) : 0;
                        gump.AddGumpPic(x, y, gumpId, hue);
                    }
                    break;
                }
                case "GUMPPICTILED":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 5);
                    if (parts.Length >= 5)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddGumpPicTiled(x, y, ParseIntToken(parts[2]), ParseIntToken(parts[3]), ParseIntToken(parts[4]));
                    }
                    break;
                }
                case "BUTTON":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 7);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddButton(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[6]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]));
                    }
                    break;
                }
                case "DHTMLGUMP":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 6, keepRemainder: true);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        string html = ResolveDialogHtml(parts[6], _character);
                        gump.AddHtmlGump(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            html,
                            ParseIntToken(parts[4]) != 0,
                            ParseIntToken(parts[5]) != 0);
                    }
                    break;
                }
                case "HTMLGUMP":
                {
                    if (!currentPageVisible) break;
                    // HTMLGUMP x y w h textIndex hasBackground hasScrollbar
                    var parts = SplitTokens(args, 7);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int idx = ParseIntToken(parts[4]);
                        string html = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            html = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddHtmlGump(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            html,
                            ParseIntToken(parts[5]) != 0,
                            ParseIntToken(parts[6]) != 0);
                    }
                    break;
                }
                case "DCROPPEDTEXT":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 5, keepRemainder: true);
                    if (parts.Length >= 6)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddCroppedText(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ResolveDialogHtml(parts[5], _character));
                    }
                    break;
                }
                case "CROPPEDTEXT":
                {
                    if (!currentPageVisible) break;
                    // CROPPEDTEXT x y w h hue textIndex
                    var parts = SplitTokens(args, 6);
                    if (parts.Length >= 6)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int idx = ParseIntToken(parts[5]);
                        string txt = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            txt = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddCroppedText(
                            x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            txt);
                    }
                    break;
                }
                case "DTEXT":
                {
                    if (!currentPageVisible) break;
                    // DTEXT x y hue text...
                    var parts = SplitTokens(args, 3, keepRemainder: true);
                    if (parts.Length >= 4)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddText(x, y, ParseIntToken(parts[2]),
                            ResolveDialogHtml(parts[3], _character));
                    }
                    break;
                }
                case "TEXT":
                {
                    if (!currentPageVisible) break;
                    // TEXT x y hue textIndex — index into [dialog NAME text] section
                    var parts = SplitTokens(args, 4);
                    if (parts.Length >= 4)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int hue = ParseIntToken(parts[2]);
                        int idx = ParseIntToken(parts[3]);
                        string txt = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            txt = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddText(x, y, hue, txt);
                    }
                    break;
                }
                case "CHECKERTRANS":
                {
                    if (!currentPageVisible) break;
                    // CHECKERTRANS x y w h
                    var parts = SplitTokens(args, 4);
                    if (parts.Length >= 4)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddCheckerTrans(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]));
                    }
                    break;
                }
                case "CHECKBOX":
                {
                    if (!currentPageVisible) break;
                    // CHECKBOX x y uncheckedGumpId checkedGumpId initialState switchId
                    var parts = SplitTokens(args, 6);
                    if (parts.Length >= 6)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddCheckbox(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]) != 0,
                            ParseIntToken(parts[5]));
                    }
                    break;
                }
                case "RADIO":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 6);
                    if (parts.Length >= 6)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddRadio(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]) != 0,
                            ParseIntToken(parts[5]));
                    }
                    break;
                }
                case "DTEXTENTRY":
                {
                    if (!currentPageVisible) break;
                    // DTEXTENTRY x y w h hue entryId initialText...
                    var parts = SplitTokens(args, 6, keepRemainder: true);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddTextEntry(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]),
                            ResolveDialogHtml(parts[6], _character));
                    }
                    break;
                }
                case "TEXTENTRY":
                {
                    if (!currentPageVisible) break;
                    // TEXTENTRY x y w h hue entryId textIndex
                    var parts = SplitTokens(args, 7);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int idx = ParseIntToken(parts[6]);
                        string txt = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            txt = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddTextEntry(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]),
                            txt);
                    }
                    break;
                }
                case "DTEXTENTRYLIMITED":
                {
                    if (!currentPageVisible) break;
                    // DTEXTENTRYLIMITED x y w h hue entryId maxChars initialText...
                    var parts = SplitTokens(args, 7, keepRemainder: true);
                    if (parts.Length >= 8)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int maxLen = ParseIntToken(parts[6]);
                        gump.AddTextEntryLimited(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]),
                            ResolveDialogHtml(parts[7], _character),
                            maxLen);
                    }
                    break;
                }
                case "TEXTENTRYLIMITED":
                {
                    if (!currentPageVisible) break;
                    // TEXTENTRYLIMITED x y w h hue entryId textIndex maxChars
                    var parts = SplitTokens(args, 8);
                    if (parts.Length >= 8)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        int idx = ParseIntToken(parts[6]);
                        int maxLen = ParseIntToken(parts[7]);
                        string txt = "";
                        if (textLines != null && idx >= 0 && idx < textLines.Count)
                            txt = ResolveDialogHtml(textLines[idx], _character);
                        gump.AddTextEntryLimited(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]),
                            txt,
                            maxLen);
                    }
                    break;
                }
                case "TILEPIC":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 3);
                    if (parts.Length >= 3)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddTilePic(x, y, ParseIntToken(parts[2]));
                    }
                    break;
                }
                case "TILEPICHUE":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 4);
                    if (parts.Length >= 4)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddTilePicHue(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]));
                    }
                    break;
                }
                case "GROUP":
                {
                    int g = ParseIntToken(args);
                    gump.AddGroup(g);
                    break;
                }
                case "XMFHTMLGUMP":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 7);
                    if (parts.Length >= 7)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddXmfHtmlGump(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            (uint)ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]) != 0,
                            ParseIntToken(parts[6]) != 0);
                    }
                    break;
                }
                case "XMFHTMLGUMPCOLOR":
                {
                    if (!currentPageVisible) break;
                    var parts = SplitTokens(args, 8);
                    if (parts.Length >= 8)
                    {
                        int x = ResolveDialogCoord(parts[0], ref cursorX, ref rowCursorX) + originX;
                        int y = ResolveDialogCoord(parts[1], ref cursorY, ref rowCursorY) + originY;
                        gump.AddXmfHtmlGumpColor(x, y,
                            ParseIntToken(parts[2]),
                            ParseIntToken(parts[3]),
                            (uint)ParseIntToken(parts[4]),
                            ParseIntToken(parts[5]) != 0,
                            ParseIntToken(parts[6]) != 0,
                            ParseIntToken(parts[7]));
                    }
                    break;
                }
            }
        }

        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (_character == null)
                return;
            var prevSubject = _dialogSubjectUid;
            _dialogSubjectUid = subjectUid;
            try
            {
            // Try the script's [Dialog d_xxx Button] handler first. If a matching
            // ON=buttonId block exists, its body runs and we're done. Otherwise
            // fall back to page navigation behaviour so the old in-dialog page
            // buttons still work. Button 0 = close/escape — still needs to run
            // the ON=0 handler (e.g. ClearCTags).
            if (TryRunScriptDialogButton(dialogId, (int)buttonId, switches, textEntries))
                return;

            if (buttonId == 0)
                return;

            if (buttonId is >= 1 and <= 5000)
            {
                ObjBase? subject = subjectUid.IsValid ? _world.FindObject(subjectUid) : null;
                TryShowScriptDialog(dialogId, (int)buttonId, subject);
            }
            }
            finally
            {
                _dialogSubjectUid = prevSubject;
            }
        });

        return true;
    }

    /// <summary>Execute the script's <c>[Dialog d_xxx Button]</c> <c>ON=buttonId</c>
    /// handler. Wires the dialog response (buttonId, switches, text entries)
    /// into the expression parser so <c>&lt;ArgN&gt;</c>, <c>&lt;Argtxt[N]&gt;</c>
    /// and <c>&lt;Argchk[N]&gt;</c> resolve correctly during evaluation.</summary>
    private bool TryRunScriptDialogButton(string dialogId, int buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        if (_character == null) return false;
        if (_triggerDispatcher?.Runner == null || _commands?.Resources == null) return false;

        if (!TryFindDialogButtonSection(dialogId, out var buttonSection))
            return false;

        // Build a lookup for Argtxt[N] / Argchk[N].
        var textById = new Dictionary<ushort, string>();
        foreach (var te in textEntries)
            textById[te.Id] = te.Text;
        var switchSet = new HashSet<uint>(switches);

        string? Resolve(string varExpr)
        {
            string upper = varExpr.ToUpperInvariant();
            if (upper == "ARGN") return buttonId.ToString();
            if (upper == "ARGV") return buttonId.ToString();
            // ARGCHK (no brackets) — 1 if any switch is flipped, 0 else.
            // ARGCHKID — the ID of the first selected switch (0 if none).
            // Sphere dialogs rely on these two to gate "OK" handlers:
            //   elseif !(<argchk>) src.sysmessage You did not select …
            //   local.n=<eval <argchkid>-1>
            if (upper == "ARGCHK") return switchSet.Count > 0 ? "1" : "0";
            if (upper == "ARGCHKID")
            {
                uint first = 0;
                foreach (var s in switchSet) { if (first == 0 || s < first) first = s; }
                return first.ToString();
            }

            if (TryParseIndexedAccessor(upper, "ARGTXT", out int txtIdx))
                return textById.TryGetValue((ushort)txtIdx, out var txt) ? txt : "";
            if (TryParseIndexedAccessor(upper, "ARGCHK", out int chkIdx))
                return switchSet.Contains((uint)chkIdx) ? "1" : "0";
            // ARGV[N] falls through to the interpreter's default handling
            // so the updated args.ArgString (from a script-side "args=…"
            // assignment) is tokenised, not the button id.
            return null;
        }

        var parser = _triggerDispatcher.Runner.Interpreter.Expressions;
        if (parser == null) return false;

        var prev = parser.DialogArgResolver;
        parser.DialogArgResolver = Resolve;
        try
        {
            var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(_character)
            {
                Number1 = buttonId,
                ArgString = buttonId.ToString(),
            };

            // Snapshot visible character state BEFORE the handler runs so
            // any property assignment (src.p=…, src.body=…, src.hue=…,
            // src.flags=…) can be broadcast as a concrete view update
            // afterwards. Without this the TrySetProperty path silently
            // updates private fields and nearby clients keep rendering
            // the old snapshot.
            var posBefore = _character.Position;
            ushort bodyBefore = _character.BodyId;
            ushort hueBefore = _character.Hue.Value;
            var flagsBefore = _character.StatFlags;

            bool ran = _triggerDispatcher.Runner.TryRunDialogButton(
                buttonSection, buttonId, _character, this, trigArgs);
            if (ran && _character != null)
            {
                bool moved = !_character.Position.Equals(posBefore);
                bool appearance =
                    _character.BodyId != bodyBefore ||
                    _character.Hue.Value != hueBefore ||
                    _character.StatFlags != flagsBefore;

                if (moved)
                {
                    _world.MoveCharacter(_character, _character.Position);
                    Resync();
                    _mountEngine?.EnsureMountedState(_character);
                    BroadcastDrawObject(_character);
                }
                else if (appearance)
                {
                    // No teleport, but body / hue / flag changed (e.g.
                    // statf_hidden via |=). Re-send DrawObject so the
                    // client reflects the new appearance without a
                    // full resync.
                    BroadcastDrawObject(_character);
                }
            }
            return ran;
        }
        finally
        {
            parser.DialogArgResolver = prev;
        }
    }

    private static bool TryParseIndexedAccessor(string upperVar, string prefix, out int index)
    {
        index = 0;
        if (!upperVar.StartsWith(prefix + "[", StringComparison.Ordinal)) return false;
        int close = upperVar.IndexOf(']');
        if (close <= prefix.Length + 1) return false;
        string num = upperVar.Substring(prefix.Length + 1, close - prefix.Length - 1);
        return int.TryParse(num, out index);
    }

    /// <summary>Pre-expand FOR / WHILE / IF / LOCAL blocks in a dialog's
    /// key sequence. Dialog scripts mix render verbs (BUTTON, DTEXT, …)
    /// with control-flow verbs Sphere's interpreter otherwise handles at
    /// runtime. The outer parser walks the section linearly, so we flatten
    /// loops into their rendered copies and evaluate IFs up front.
    /// <paramref name="locals"/> is shared with the caller so LOCAL.x=
    /// assignments stay visible to later expression resolution.</summary>
    private List<SphereNet.Scripting.Parsing.ScriptKey> ExpandDialogScriptKeys(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input,
        Dictionary<string, string> locals,
        int dialogArgN1)
    {
        var output = new List<SphereNet.Scripting.Parsing.ScriptKey>(input.Count);
        ExpandRange(input, 0, input.Count, output, locals, dialogArgN1);
        return output;
    }

    private void ExpandRange(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end,
        List<SphereNet.Scripting.Parsing.ScriptKey> output,
        Dictionary<string, string> locals,
        int dialogArgN1)
    {
        int i = start;
        while (i < end)
        {
            var k = input[i];
            string cmd = k.Key.Trim().ToUpperInvariant();
            string args = k.Arg;

            if (cmd == "IF")
            {
                int ifEnd = FindBlockEnd(input, i + 1, end, "IF", "ENDIF");
                if (ifEnd < 0) { i = end; break; }
                // Split the IF body into IF / ELIF / ELSE branches.
                var branches = SplitIfBranches(input, i + 1, ifEnd);
                string resolvedCond = ResolveInlineExpressions(args, locals, dialogArgN1);
                bool taken = EvaluateDialogCondition(resolvedCond);
                int chosenStart = -1, chosenEnd = -1;
                if (taken) { chosenStart = branches[0].Start; chosenEnd = branches[0].End; }
                else
                {
                    for (int b = 1; b < branches.Count && chosenStart < 0; b++)
                    {
                        var br = branches[b];
                        if (br.Keyword == "ELSE")
                        { chosenStart = br.Start; chosenEnd = br.End; break; }
                        if (br.Keyword == "ELIF" || br.Keyword == "ELSEIF")
                        {
                            string elifCond = ResolveInlineExpressions(br.Condition!, locals, dialogArgN1);
                            if (EvaluateDialogCondition(elifCond))
                            { chosenStart = br.Start; chosenEnd = br.End; break; }
                        }
                    }
                }
                if (chosenStart >= 0)
                    ExpandRange(input, chosenStart, chosenEnd, output, locals, dialogArgN1);
                i = ifEnd + 1;
                continue;
            }

            if (cmd == "FOR")
            {
                // FOR N  / FOR START END / FOR VAR START END.
                int forEnd = FindBlockEnd(input, i + 1, end, "FOR", "ENDFOR");
                if (forEnd < 0) { i = end; break; }
                string resolved = ResolveInlineExpressions(args, locals, dialogArgN1);
                ParseForRange(resolved, out string? iterName, out long from, out long to);
                const long maxIter = 500;
                long count = Math.Min(maxIter, to - from + 1);
                string? savedFor = locals.TryGetValue("_FOR", out var sf) ? sf : null;
                string? savedNamed = (iterName != null && locals.TryGetValue(iterName, out var sv)) ? sv : null;
                for (long it = 0; it < count; it++)
                {
                    string cur = (from + it).ToString();
                    locals["_FOR"] = cur;
                    if (iterName != null)
                        locals[iterName] = cur;
                    ExpandRange(input, i + 1, forEnd, output, locals, dialogArgN1);
                }
                if (savedFor != null) locals["_FOR"] = savedFor; else locals.Remove("_FOR");
                if (iterName != null)
                {
                    if (savedNamed != null) locals[iterName] = savedNamed;
                    else locals.Remove(iterName);
                }
                i = forEnd + 1;
                continue;
            }

            // Dialog scripts frequently seed runtime state in the layout body
            // before drawing controls:
            //   ARGS <def.npctype_<dctag0.spawn_type>_spawn>
            //   SRC.CTAG0.spawn_type 1
            // Keep those side-effect lines in the pre-expansion pass so
            // later <ARGV[...]> / <DARGV> and CTAG-dependent expressions
            // resolve correctly.
            if (cmd == "ARGS")
            {
                string resolvedArgs = string.IsNullOrEmpty(args)
                    ? ""
                    : ResolveInlineExpressions(args, locals, dialogArgN1);
                locals["__ARGS"] = resolvedArgs;
                i++;
                continue;
            }
            if ((cmd.StartsWith("SRC.CTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.CTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("SRC.DCTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
                 cmd.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase)) &&
                _character != null)
            {
                string prop = cmd.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase) ? cmd[4..] : cmd;
                string resolvedVal = string.IsNullOrEmpty(args)
                    ? ""
                    : ResolveInlineExpressions(args, locals, dialogArgN1);
                _character.TrySetProperty(prop, resolvedVal);
                i++;
                continue;
            }

            if (cmd == "WHILE")
            {
                int whileEnd = FindBlockEnd(input, i + 1, end, "WHILE", "ENDWHILE");
                if (whileEnd < 0) { i = end; break; }
                const int maxIter = 500;
                int iter = 0;
                while (iter < maxIter)
                {
                    string resolved = ResolveInlineExpressions(args, locals, dialogArgN1);
                    if (!EvaluateDialogCondition(resolved)) break;
                    ExpandRange(input, i + 1, whileEnd, output, locals, dialogArgN1);
                    iter++;
                }
                i = whileEnd + 1;
                continue;
            }

            // REFn = <uid> — scope-local object reference. Storage
            // lives in the same `locals` dict under the key "REFn" so
            // subsequent <REFn> / <REFn.property> lookups see it.
            // Scripts like the admin panel rely on this to point rows
            // at account / character objects:
            //   REF1=<SERV.ACCOUNT.<Eval <CTag0.Dialog.Admin.Index>+1>>
            //   <DEF.admin_flag_1>: <REF1.NAME>
            if (cmd.Length > 3 && cmd.StartsWith("REF", StringComparison.OrdinalIgnoreCase) &&
                char.IsDigit(cmd[3]) && !cmd.Contains('.'))
            {
                string resolved = string.IsNullOrEmpty(args) ? "" : ResolveInlineExpressions(args, locals, dialogArgN1);
                locals[cmd.ToUpperInvariant()] = resolved;
                i++;
                continue;
            }

            if (cmd.StartsWith("LOCAL.", StringComparison.OrdinalIgnoreCase))
            {
                // LOCAL.x = value  or  LOCAL.x += N
                string nameAndOp = cmd[6..]; // after "LOCAL."
                // Detect compound operator in args: "+= 1", "= 5", "1", etc.
                string varName = nameAndOp;
                string valueExpr = args;
                char opCh = ' ';
                if (valueExpr.Length > 0)
                {
                    var trimmed = valueExpr.TrimStart();
                    if (trimmed.StartsWith("+=", StringComparison.Ordinal)) { opCh = '+'; valueExpr = trimmed[2..]; }
                    else if (trimmed.StartsWith("-=", StringComparison.Ordinal)) { opCh = '-'; valueExpr = trimmed[2..]; }
                    else if (trimmed.StartsWith("=", StringComparison.Ordinal)) { opCh = '='; valueExpr = trimmed[1..]; }
                }
                string resolved = ResolveInlineExpressions(valueExpr.Trim(), locals, dialogArgN1);
                long num = ParseLongToken(resolved);
                long current = locals.TryGetValue(varName, out var cur) && long.TryParse(cur, out long pv) ? pv : 0;
                long next = opCh switch
                {
                    '+' => current + num,
                    '-' => current - num,
                    _ => num,
                };
                locals[varName] = next.ToString();
                i++;
                continue;
            }

            // Render command — inline-resolve the args and emit. Local
            // scope flows into the args via ResolveInlineExpressions so
            // <local.x> / <eval …> inside BUTTON / DTEXT / RADIO /
            // RESIZEPIC coordinates come out as concrete numbers.
            string resolvedArg = string.IsNullOrEmpty(args)
                ? args
                : ResolveInlineExpressions(args, locals, dialogArgN1);
            output.Add(new SphereNet.Scripting.Parsing.ScriptKey(k.Key, resolvedArg));
            i++;
        }
    }

    /// <summary>Find the matching end keyword, honouring nested blocks.
    /// Returns the absolute index of the end keyword, or -1 if unmatched.</summary>
    private static int FindBlockEnd(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end,
        string openKeyword, string endKeyword)
    {
        int depth = 1;
        for (int i = start; i < end; i++)
        {
            string k = input[i].Key.Trim().ToUpperInvariant();
            if (k == openKeyword) depth++;
            else if (k == endKeyword) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private readonly record struct IfBranch(string Keyword, int Start, int End, string? Condition);

    private static List<IfBranch> SplitIfBranches(
        IReadOnlyList<SphereNet.Scripting.Parsing.ScriptKey> input, int start, int end)
    {
        // Branch boundaries at ELIF / ELSEIF / ELSE at depth 0 (relative
        // to the outer IF we're inside). Nested IFs count as depth.
        var list = new List<IfBranch>();
        int depth = 0;
        int segStart = start;
        string curKeyword = "IF";
        string? curCondition = null;
        for (int i = start; i < end; i++)
        {
            string k = input[i].Key.Trim().ToUpperInvariant();
            if (k == "IF") { depth++; continue; }
            if (k == "ENDIF") { depth--; continue; }
            if (depth != 0) continue;
            if (k == "ELSE" || k == "ELIF" || k == "ELSEIF")
            {
                list.Add(new IfBranch(curKeyword, segStart, i, curCondition));
                curKeyword = k;
                curCondition = (k != "ELSE") ? input[i].Arg : null;
                segStart = i + 1;
            }
        }
        list.Add(new IfBranch(curKeyword, segStart, end, curCondition));
        return list;
    }

    private static void ParseForRange(string expr, out string? iterName, out long from, out long to)
    {
        iterName = null;
        from = 0; to = 0;
        var parts = expr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            // FOR N — loops 0 to N-1 in Sphere.
            long.TryParse(parts[0], out long n);
            to = n - 1;
        }
        else if (parts.Length >= 2)
        {
            // FOR var start end
            if (!long.TryParse(parts[0], out from))
            {
                iterName = parts[0];
                if (parts.Length >= 3)
                {
                    long.TryParse(parts[1], out from);
                    long.TryParse(parts[2], out to);
                }
                return;
            }
            long.TryParse(parts[1], out to);
        }
    }

    private static long ParseLongToken(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out long hv))
            return hv;
        return long.TryParse(s, out long v) ? v : 0;
    }

    /// <summary>Cheap-and-cheerful truthiness for IF / WHILE. An expression
    /// is truthy if it evaluates to non-zero; strings compare as text.
    /// The Sphere parser accepts relational operators, which
    /// ExpressionParser already understands — we just evaluate through it.</summary>
    private bool EvaluateDialogCondition(string condition)
    {
        string c = condition.Trim();
        if (string.IsNullOrEmpty(c)) return false;
        if (c.Length >= 2 && c[0] == '(' && c[^1] == ')')
            c = c[1..^1].Trim();
        if (string.IsNullOrEmpty(c) || c == "0")
            return false;

        bool hasOperator = c.AsSpan().IndexOfAny("!+-*/%&|()<>=~^") >= 0;

        var parser = new ExpressionParser();
        long v = parser.Evaluate(c.AsSpan());
        if (v != 0)
            return true;
        if (hasOperator)
            return false;
        bool truthy = !long.TryParse(c, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _);
        return truthy;
    }

    /// <summary>Resolve &lt;local.x&gt; / &lt;dlocal.x&gt; / &lt;eval …&gt;
    /// / &lt;def0.…&gt; / &lt;src.…&gt; etc. in an argument string, using
    /// the dialog-local scope for LOCAL references. <paramref name="dialogArgN1"/>
    /// feeds &lt;argn1&gt; / &lt;argn&gt; so the Sphere "page &lt;argn1&gt;"
    /// pattern in dialog layouts resolves to the page the dialog was
    /// opened on (e.g. sdialog d_moongates &lt;eval &lt;src.p.m&gt;+1&gt;).</summary>
    private string ResolveInlineExpressions(string input, Dictionary<string, string> locals, int dialogArgN1 = 0)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('<') < 0) return input;

        var servResolver = _triggerDispatcher?.Runner?.Interpreter?.ServerPropertyResolver;
        var parser = new ExpressionParser
        {
            VariableResolver = varName =>
            {
                string upper = varName.ToUpperInvariant();
                if (upper == "ARGN" || upper == "ARGN1")
                    return dialogArgN1.ToString();
                if (upper == "ARGS" || upper == "DARGS")
                    return locals.TryGetValue("__ARGS", out var av) ? av : "";
                if (upper == "DARGV")
                {
                    string rawArgs = locals.TryGetValue("__ARGS", out var av) ? av : "";
                    var toks = rawArgs.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    return toks.Length.ToString();
                }
                if (upper.StartsWith("ARGV", StringComparison.Ordinal))
                {
                    string rawArgs = locals.TryGetValue("__ARGS", out var av) ? av : "";
                    var toks = rawArgs.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    string suffix = upper.Length > 4 ? upper[4..] : "";
                    if (suffix.StartsWith("[", StringComparison.Ordinal) && suffix.EndsWith("]", StringComparison.Ordinal) && suffix.Length > 2)
                        suffix = suffix[1..^1];
                    if (int.TryParse(suffix, out int idx) && idx >= 0 && idx < toks.Length)
                        return toks[idx];
                    return "";
                }
                // Uninitialised LOCAL / DLOCAL return "0" per Sphere
                // convention — scripts read them as zero before the first
                // assignment (common pattern: "while <dlocal.n>" where the
                // counter is bumped inside the body).
                if (upper.StartsWith("LOCAL."))
                    return locals.TryGetValue(upper[6..], out var lv) ? lv : "0";
                if (upper.StartsWith("DLOCAL."))
                    return locals.TryGetValue(upper[7..], out var dlv) ? dlv : "0";

                // REFn and REFn.property — dialog-scoped object references.
                // REF1..REF999 are stored in the same locals dict keyed as
                // "REFN" (upper). `<REFn>` returns the stored reference
                // string (usually a UID); `<REFn.property>` looks up the
                // referenced object via the REF_GET protocol so its
                // properties flow into the rendered layout.
                if (upper.Length > 3 && upper.StartsWith("REF", StringComparison.Ordinal) &&
                    char.IsDigit(upper[3]))
                {
                    int dotIdx = upper.IndexOf('.');
                    string refKey = dotIdx > 0 ? upper[..dotIdx] : upper;
                    string? refVal = locals.TryGetValue(refKey, out var rv) ? rv : null;
                    if (dotIdx < 0) return refVal ?? "0";
                    if (string.IsNullOrEmpty(refVal) || refVal == "0") return "0";
                    string subProp = upper[(dotIdx + 1)..];
                    return servResolver?.Invoke($"_REF_GET={refVal}|{subProp}") ?? "0";
                }

                // CTAG0.X / CTAG.X / DCTAG0.X / DCTAG.X on the current character. Reads from
                // the client-session CTag map (Source-X CClient::m_TagDefs
                // parity), not the persistent TAG storage. Defaults to
                // "0" when unset — Sphere convention.
                if (upper.StartsWith("CTAG0.") || upper.StartsWith("CTAG.") ||
                    upper.StartsWith("DCTAG0.") || upper.StartsWith("DCTAG."))
                {
                    int dot = upper.IndexOf('.');
                    string tagKey = upper[(dot + 1)..];
                    string? tagVal = _character?.CTags.Get(tagKey);
                    if (string.IsNullOrEmpty(tagVal) && tagKey.Equals("ACCOUNTLANG", StringComparison.OrdinalIgnoreCase))
                    {
                        string fallbackLang = GetEffectiveAccountLang();
                        if (!string.IsNullOrEmpty(fallbackLang))
                            return fallbackLang;
                    }
                    return tagVal ?? "0";
                }
                if ((upper.StartsWith("DEF.") || upper.StartsWith("DEF0.")) &&
                    _commands?.Resources != null)
                {
                    string origKey = varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase)
                        ? varName[4..]
                        : varName[5..];
                    if (_commands.Resources.TryGetDefValue(origKey, out string defTextVal))
                    {
                        string stripped = StripSurroundingQuotes(defTextVal);
                        return stripped;
                    }
                    var defRid = _commands.Resources.ResolveDefName(origKey);
                    if (defRid.IsValid) return defRid.Index.ToString();
                    return "0";
                }
                if ((upper.StartsWith("SRC.") || upper.StartsWith("DSRC.")) && _character != null)
                {
                    int d = upper.IndexOf('.');
                    string sub = upper[(d + 1)..];
                    if (_character.TryGetProperty(sub, out string srcVal))
                    {
                        if ((sub.Equals("CTAG0.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase) ||
                             sub.Equals("CTAG.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(srcVal) || srcVal == "0"))
                            return GetEffectiveAccountLang();
                        return srcVal;
                    }
                }
                if (upper.StartsWith("SERV.") && servResolver != null)
                    return servResolver(upper[5..]);

                // Dialog subject (CLIMODE_DIALOG pObj) wins over GM
                // properties for bare reads — <BODY>, <STR>, <NAME>
                // inside d_charprop1 refer to the inspected target,
                // not the GM. Fall back to GM when subject misses so
                // admin-style dialogs keep their existing behaviour.
                if (_dialogSubjectUid.IsValid)
                {
                    var subj = _world.FindObject(_dialogSubjectUid);
                    if (subj != null)
                    {
                        // Sphere <I.*> alias = the subject itself.
                        //   <I.STR> → subject STR, <I.0> → skill 0 level.
                        // Character.TryGetProperty doesn't know the "I."
                        // prefix, so strip it here and delegate the rest.
                        string lookup = upper.StartsWith("I.", StringComparison.Ordinal)
                            ? upper[2..]
                            : upper;
                        // A bare number on Character resolves to that skill's
                        // current level — matches Source-X CChar::r_WriteVal
                        // on an integer key.
                        if (subj is Character subjCh && int.TryParse(lookup, out int skillIdx)
                            && skillIdx >= 0 && skillIdx < (int)SkillType.Qty)
                            return subjCh.GetSkill((SkillType)skillIdx).ToString();
                        if (subj.TryGetProperty(lookup, out string subjProp))
                            return subjProp;
                    }
                }
                if (_character != null && _character.TryGetProperty(upper, out string charProp))
                    return charProp;

                // Last-resort delegation: the same resolver the script
                // interpreter uses at runtime covers ACCOUNT.x,
                // ISEVENT.x, ISDIALOGOPEN.x, VAR0.x, GETREFTYPE, and
                // other dialog-common accessors.
                if (_character != null &&
                    TryResolveScriptVariable(upper, _character, null, out string fallback))
                    return fallback;

                // Bare defname constants used in script arithmetic/bit tests,
                // e.g. <statf_insubstantial>, <memory_ipet>.
                if (_commands?.Resources != null && IsPlainDefToken(upper))
                {
                    var rid = _commands.Resources.ResolveDefName(upper);
                    if (rid.IsValid) return rid.Index.ToString();
                }

                return null;
            },
        };
        return parser.EvaluateStr(input);
    }

    private bool TryFindDialogButtonSection(string dialogId, out SphereNet.Scripting.Parsing.ScriptSection buttonSection)
    {
        buttonSection = null!;
        if (_commands?.Resources == null) return false;

        foreach (var script in _commands.Resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                var sections = file.ReadAllSections();
                foreach (var section in sections)
                {
                    if (!section.Name.Equals("DIALOG", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Header forms: "d_xxx BUTTON" / "d_xxx TEXT" / etc.
                    // Split on whitespace and match (first=id, second=BUTTON).
                    var parts = section.Argument.Split(
                        new[] { ' ', '\t' },
                        2,
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    if (!parts[0].Equals(dialogId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!parts[1].Equals("BUTTON", StringComparison.OrdinalIgnoreCase)) continue;

                    buttonSection = section;
                    return true;
                }
            }
            finally
            {
                script.Close();
            }
        }
        return false;
    }

    private bool TryFindDialogSections(string dialogId, out SphereNet.Scripting.Parsing.ScriptSection layoutSection)
    {
        layoutSection = null!;
        if (_commands?.Resources == null)
            return false;

        foreach (var script in _commands.Resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                var sections = file.ReadAllSections();
                foreach (var section in sections)
                {
                    if (!section.Name.Equals("DIALOG", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string arg = section.Argument.Trim();
                    if (arg.Equals(dialogId, StringComparison.OrdinalIgnoreCase))
                    {
                        layoutSection = section;
                        return true;
                    }
                }
            }
            finally
            {
                script.Close();
            }
        }

        return false;
    }

    private List<string> LoadDialogTextLines(string dialogId)
    {
        var lines = new List<string>();
        if (_commands?.Resources == null) return lines;

        foreach (var script in _commands.Resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                var sections = file.ReadAllSections();
                foreach (var section in sections)
                {
                    if (!section.Name.Equals("DIALOG", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parts = section.Argument.Split(
                        new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    if (!parts[0].Equals(dialogId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!parts[1].Equals("TEXT", StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (var key in section.Keys)
                    {
                        string line = string.IsNullOrEmpty(key.Arg)
                            ? key.Key
                            : $"{key.Key} {key.Arg}";
                        lines.Add(line.TrimEnd());
                    }
                    return lines;
                }
            }
            finally
            {
                script.Close();
            }
        }
        return lines;
    }

    private bool TryFindMenuSection(string menuDefname, out SphereNet.Scripting.Parsing.ScriptSection menuSection)
    {
        menuSection = null!;
        if (_commands?.Resources == null)
            return false;

        foreach (var script in _commands.Resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                var sections = file.ReadAllSections();
                foreach (var section in sections)
                {
                    if (!section.Name.Equals("MENU", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string arg = section.Argument.Trim();
                    if (arg.Equals(menuDefname, StringComparison.OrdinalIgnoreCase))
                    {
                        menuSection = section;
                        return true;
                    }
                }
            }
            finally
            {
                script.Close();
            }
        }

        return false;
    }

    private string ResolveDialogHtml(string html, IScriptObj target)
    {
        // Delegate through the same resolver chain the interpreter uses.
        // SERV.*, RTIME, RTICKS, REFn.property, etc. all live on the server
        // property resolver — without routing dialog text through it we lost
        // the most common Sphere gump substitutions (<Serv.Servname>, …).
        var servResolver = _triggerDispatcher?.Runner?.Interpreter?.ServerPropertyResolver;
        var parser = new ExpressionParser
        {
            VariableResolver = varName =>
            {
                if (varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) &&
                    _commands?.Resources != null &&
                    _commands.Resources.TryGetDefValue(varName[4..], out string defVal))
                {
                    return defVal;
                }

                if (TryResolveScriptVariable(varName, target, null, out string runtimeVal))
                    return runtimeVal;

                // Source/target routing: Src.X resolves through the admin's
                // own character. Admin dialogs reference <Src.Version>,
                // <Src.Account>, <Src.CTag0.…>, etc.
                if (varName.StartsWith("SRC.", StringComparison.OrdinalIgnoreCase))
                {
                    string subProp = varName[4..];
                    if (_character != null && _character.TryGetProperty(subProp, out string srcProp))
                    {
                        if ((subProp.Equals("CTAG0.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase) ||
                             subProp.Equals("CTAG.ACCOUNTLANG", StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrEmpty(srcProp) || srcProp == "0"))
                            return GetEffectiveAccountLang();
                        return srcProp;
                    }
                }

                if (target.TryGetProperty(varName, out string prop))
                    return prop;

                // SERV.* / RTIME / RTICKS — delegate to the runtime resolver.
                if (servResolver != null)
                {
                    if (varName.StartsWith("SERV.", StringComparison.OrdinalIgnoreCase))
                    {
                        string servProp = varName[5..];
                        string? servVal = servResolver(servProp);
                        if (servVal != null) return servVal;
                    }
                    if (varName.StartsWith("RTIME", StringComparison.OrdinalIgnoreCase) ||
                        varName.StartsWith("RTICKS", StringComparison.OrdinalIgnoreCase))
                    {
                        string? rVal = servResolver(varName);
                        if (rVal != null) return rVal;
                    }
                    // Bare server metrics (CLIENTS, ACCOUNTS, CHARS, ITEMS, VERSION,
                    // SERVNAME, TIME, SAVECOUNT, MEM, REGEN0-3) as fallback.
                    string? bare = servResolver(varName);
                    if (bare != null) return bare;
                }

                if (_commands?.Resources != null && IsPlainDefToken(varName))
                {
                    var rid = _commands.Resources.ResolveDefName(varName);
                    if (rid.IsValid) return rid.Index.ToString();
                }

                return null;
            }
        };

        return parser.EvaluateStr(html);
    }

    private static bool IsPlainDefToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        foreach (char ch in token)
        {
            bool ok = char.IsLetterOrDigit(ch) || ch is '_' or '.';
            if (!ok) return false;
        }
        return true;
    }

    private string GetEffectiveAccountLang()
    {
        if (_character != null && _character.TryGetProperty("ACCOUNT.LANG", out string langRaw))
        {
            string lang = (langRaw ?? "").Trim().ToUpperInvariant();
            if (lang.Length == 0) return "ENG";
            return lang switch
            {
                "ENU" => "ENG",
                "FRB" or "FRC" => "FRA",
                "ESN" => "ESP",
                _ => lang
            };
        }
        return "ENG";
    }

    /// <summary>Resolve a dialog coordinate token.
    /// Formats:
    ///   N      — absolute (resets cursor to N)
    ///   +N     — cursor += N
    ///   *N     — rowCursor += N; cursor = rowCursor (next-row step, independent
    ///            of the +/- column walk)
    /// <paramref name="rowCursor"/> may alias <paramref name="cursor"/> when the
    /// caller hasn't wired a separate row tracker (old call sites).</summary>
    private static int ResolveDialogCoord(string token, ref int cursor, ref int rowCursor)
    {
        // Sphere DORIGIN coord rules (verified against d_SphereAdmin_PlayerTweak):
        //   bare N : SET baseline to N and return it (origin offset reset)
        //   +N     : return baseline + N (NON-mutating row-relative offset)
        //   -N     : return baseline - N (NON-mutating row-relative offset)
        //   *N     : baseline += N, return baseline (advance the row)
        //
        // The earlier "+N means cursor += N" reading was wrong: with the
        // DORIGIN block
        //     DText  +35 +0   _Properties
        //     Button +0  -2   4005 4006 0 3 0
        // the button has to land at origin.x (X=5), to the LEFT of the
        // text. Cumulative cursor logic stuck the button at X=40 on top
        // of the label, which was the visible "buttons drift sideways /
        // text is unreadable" symptom in d_SphereAdmin_PlayerTweak.
        // `cursor` is kept around as a back-compat alias mirroring the
        // baseline so callers that still pass it observe the same value
        // as `rowCursor`.
        token = token.Trim();
        if (token.StartsWith('+'))
        {
            int delta = ParseIntToken(token[1..]);
            return rowCursor + delta;
        }
        if (token.StartsWith('-'))
        {
            int delta = ParseIntToken(token[1..]);
            return rowCursor - delta;
        }
        if (token.StartsWith('*'))
        {
            int delta = ParseIntToken(token[1..]);
            rowCursor += delta;
            cursor = rowCursor;
            return rowCursor;
        }

        rowCursor = ParseIntToken(token);
        cursor = rowCursor;
        return rowCursor;
    }

    private static int ResolveDialogCoord(string token, ref int cursor)
    {
        int dummy = cursor;
        return ResolveDialogCoord(token, ref cursor, ref dummy);
    }

    /// <summary>DEFNAME text values in Sphere scripts often ship wrapped
    /// in double quotes (<c>CharFlag.1 "Invulnerable"</c>). The quotes
    /// are a Sphere source-lexer convention, not part of the payload —
    /// strip a single matched pair when resolving so the gump label
    /// reads "Invulnerable" instead of <c>"Invulnerable"</c>.</summary>
    private static string StripSurroundingQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static int ParseIntToken(string token)
    {
        token = token.Trim();
        if (token.Length == 0)
            return 0;

        // Sphere convention (matches ScriptKey.TryParseNumber and Source-X
        // CExpression::GetVal): a leading '0' on a multi-digit token marks
        // the value as HEX. Without this rule "0480" silently parsed as
        // decimal 480 instead of 0x480 (1152) and admin gump hues came
        // out as random off-spectrum colors — the d_SphereAdmin_PlayerTweak
        // labels were rendered in colors the client treats as
        // near-invisible (the "yazılar okunmuyor" symptom).

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token[2..], System.Globalization.NumberStyles.HexNumber, null, out int hex))
            return hex;

        if (token.Length > 1 && token[0] == '0' &&
            int.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out int legacyHex))
            return legacyHex;

        if (int.TryParse(token, out int dec))
            return dec;

        return 0;
    }

    private static string[] SplitTokens(string input, int minLeadingTokens, bool keepRemainder = false)
    {
        if (!keepRemainder)
            return input.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var parts = new List<string>();
        string text = input.Trim();
        int i = 0;
        while (i < text.Length && parts.Count < minLeadingTokens)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            if (i >= text.Length) break;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ',') i++;
            parts.Add(text[start..i]);
        }

        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
        parts.Add(i < text.Length ? text[i..] : "");
        return parts.ToArray();
    }

    private bool CanSendStatusFor(Character ch)
    {
        if (_character == null)
            return false;
        if (ch == _character)
            return true;
        if (ch.MapIndex != _character.MapIndex)
            return false;

        int range = Math.Max(5, (int)_netState.ViewRange);
        return _character.Position.GetDistanceTo(ch.Position) <= range;
    }

    private static string ResolveStatusName(Character ch)
    {
        string name = ch.GetName()
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (name.Length > 0)
            return name;

        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        name = def?.Name?.Trim() ?? "";
        if (name.Length == 0)
            name = def?.DefName?.Trim() ?? "";

        if (name.Length == 0)
            name = ch.IsPlayer ? "Player" : $"NPC_{ch.BodyId:X}";

        return name.Length > 30 ? name[..30] : name;
    }

    public void SendSkillList()
    {
        if (_character == null) return;

        var skills = new List<(ushort Id, ushort Value, ushort RawValue, byte Lock, ushort Cap)>();
        for (int i = 0; i < (int)SkillType.Qty && i < 58; i++)
        {
            ushort val = _character.GetSkill((SkillType)i);
            byte lockState = _character.GetSkillLock((SkillType)i);
            skills.Add(((ushort)i, val, val, lockState, 1000)); // cap=100.0 (1000 in tenths)
        }
        _netState.Send(new PacketSkillList(skills.ToArray()));
    }

    private void SendPickupFailed(byte reason)
    {
        var buf = new PacketBuffer(2);
        buf.WriteByte(0x27);
        buf.WriteByte(reason);
        _netState.Send(buf);
    }

    private static (uint Serial, ushort ItemId, byte Layer, ushort Hue)[] BuildEquipmentList(Character ch)
    {
        var list = new List<(uint, ushort, byte, ushort)>();
        for (int i = 0; i <= (int)Layer.Horse; i++)
        {
            var item = ch.GetEquippedItem((Layer)i);
            if (item == null) continue;
            list.Add((item.Uid.Value, item.DispIdFull, (byte)i, item.Hue));
        }
        return list.ToArray();
    }

    /// <summary>
    /// Build the network MobileFlags byte (0x77/0x78/0x20 packets).
    ///
    /// Wire format mirrors ClassicUO <c>MobileFlags</c>:
    ///   0x01 = Frozen
    ///   0x02 = Female  (NOT "Dead" — ghost state is read from body ID
    ///                   0x192/0x193 client-side; setting 0x02 on a male
    ///                   ghost makes ClassicUO short-circuit
    ///                   <c>CheckGraphicChange()</c> because the cached
    ///                   IsFemale state contradicts the male body, and
    ///                   the sprite stays on the previous human atlas —
    ///                   the root cause of the "ghost body never
    ///                   renders" bug for both self and staff observers
    ///                   in the death rebuild logs.)
    ///   0x04 = Flying / Poisoned (client interprets per body type)
    ///   0x08 = YellowBar
    ///   0x10 = IgnoreMobiles
    ///   0x40 = WarMode
    ///   0x80 = Hidden / Invisible
    ///
    /// Female is derived from the body ID (Source-X
    /// <c>CChar::IsFemale()</c> returns the same lookup): human female
    /// = 0x191, female ghost = 0x193.
    /// </summary>
    private static byte BuildMobileFlags(Character ch)
    {
        byte flags = 0;
        if (ch.IsInvisible) flags |= 0x80;
        if (ch.IsInWarMode) flags |= 0x40;
        if (ch.BodyId == 0x0191 || ch.BodyId == 0x0193) flags |= 0x02;
        if (ch.IsStatFlag(StatFlag.Freeze)) flags |= 0x01;
        return flags;
    }

    /// <summary>
    /// Turn the player to face <paramref name="target"/> and broadcast the
    /// new facing to nearby clients via 0x77. Mirrors Source-X
    /// <c>CChar::UpdateDir(pCharTarg)</c> -> <c>UpdateMove(GetTopPoint())</c>:
    /// when an NPC starts a swing or a spell, the engine first turns the
    /// caster/attacker so that the animation plays in the correct
    /// direction. Without this, melee/cast animations look broken from
    /// the side and bow shots may visually fly the wrong way.
    /// Skips the broadcast (but still updates state) when facing is
    /// already correct, to avoid packet spam during continuous combat.
    /// </summary>
    public void FaceTarget(Character target)
    {
        if (_character == null || target == null) return;
        if (target.Position.Equals(_character.Position)) return;

        var newDir = _character.Position.GetDirectionTo(target.Position);
        if (newDir == _character.Direction) return;

        _character.Direction = newDir;

        byte flags = BuildMobileFlags(_character);
        byte noto = GetNotoriety(_character);
        byte dirByte = (byte)((byte)_character.Direction & 0x07);

        var pkt = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z, dirByte,
            _character.Hue, flags, noto);

        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, pkt, _character.Uid.Value, _character);
        else
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, pkt, _character.Uid.Value);
    }

    private bool TryHandleCommandSpeech(string text)
    {
        if (_character == null || _commands == null)
            return false;

        // Some clients may prepend invisible/null whitespace-like chars in unicode speech.
        // Normalize before checking command prefix to keep command parsing resilient.
        string normalized = text
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .TrimStart(' ', '\t', '\r', '\n');
        if (normalized.Length <= 1)
            return false;

        char prefix = _commands.CommandPrefix;
        // Accept '.' and '/' regardless of configured prefix for Source-X compatibility.
        if (normalized[0] != prefix && normalized[0] != '.' && normalized[0] != '/')
            return false;

        string commandLine = normalized[1..]
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrEmpty(commandLine))
            return true;

        _logger.LogDebug("[command_dispatch] account={Account} char=0x{Char:X8} raw='{Raw}' normalized='{Norm}' prefix='{Prefix}' cmd='{Cmd}'",
            _account?.Name ?? "?", _character.Uid.Value, text, normalized, prefix, commandLine);

        var posBefore = _character.Position;
        var result = _commands.TryExecute(_character, commandLine);
        switch (result)
        {
            case CommandResult.Executed:
                if (!_character.Position.Equals(posBefore))
                {
                    // Teleport-like commands (.GO, .JAIL, script-based moves, etc.) must
                    // force a client-side world refresh so the player actually relocates.
                    Resync();
                    _mountEngine?.EnsureMountedState(_character);
                    BroadcastDrawObject(_character);
                }
                return true;

            case CommandResult.InsufficientPriv:
                var required = _commands.GetRequiredPrivLevel(commandLine) ?? PrivLevel.Counsel;
                SysMessage(ServerMessages.GetFormatted("gm_insuf_priv", required, _character.PrivLevel));
                _logger.LogDebug("[command_priv_reject] account={Account} accountPLEVEL={AccLvl} char=0x{Char:X8} charPrivLevel={ChLvl} cmd='{Cmd}' required={Req}",
                    _account?.Name ?? "?", _account?.PrivLevel, _character.Uid.Value, _character.PrivLevel, commandLine, required);
                return true;

            case CommandResult.NotFound:
                SysMessage(ServerMessages.GetFormatted("cmd_invalid", commandLine));
                _logger.LogDebug("[speech_fallback] account={Account} char=0x{Char:X8} unknownCmd='{Cmd}'",
                    _account?.Name ?? "?", _character.Uid.Value, commandLine);
                return true;

            case CommandResult.Failed:
                _logger.LogDebug("[command_failed] account={Account} char=0x{Char:X8} cmd='{Cmd}'",
                    _account?.Name ?? "?", _character.Uid.Value, commandLine);
                return true;

            default:
                return false;
        }
    }

    private void SetWarMode(bool warMode, bool syncClients, bool preserveTarget)
    {
        if (_character == null) return;

        bool oldState = _character.IsInWarMode;
        if (warMode)
            _character.SetStatFlag(StatFlag.War);
        else
            _character.ClearStatFlag(StatFlag.War);

        if (!warMode && !preserveTarget)
        {
            _character.FightTarget = Serial.Invalid;
            _character.NextAttackTime = 0;
        }

        if (!syncClients) return;

        // Send 0x72 war mode confirmation — client expects this to actually toggle
        _netState.Send(new PacketWarModeResponse(warMode));

        // Broadcast appearance update to nearby players. For a LIVING
        // character a single 0x77 update is enough — every observer
        // already has the mobile in their world.Mobiles.
        //
        // Ghosts are special. While in peace mode the ghost is hidden
        // from plain observers via the BuildViewDelta filter (their
        // _knownChars never tracked the mobile). A blanket 0x77
        // broadcast on a peace→war transition would target a serial
        // that ClassicUO doesn't know about and silently drop.
        // So for a manifesting/un-manifesting ghost we skip the
        // BroadcastNearby and use per-observer dispatch instead:
        //
        //   peace → war (manifest):
        //     plain observer  → 0x78 PacketDrawObject (hue 0x4001
        //                       translucent grey) + cache add so the
        //                       next view-delta tick doesn't double-spawn
        //     staff observer  → 0x77 normal update (already had the
        //                       ghost mobile in cache)
        //
        //   war → peace (un-manifest):
        //     plain observer  → 0x1D delete + cache drop
        //     staff observer  → 0x77 normal update
        //
        //   self                → 0x77 always (own client always knows
        //                         the mobile)
        byte warFlags = BuildMobileFlags(_character);
        byte warNoto = GetNotoriety(_character);
        var mobileMoving = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z, (byte)_character.Direction,
            _character.Hue, warFlags, warNoto);

        if (_character.IsDead && ForEachClientInRange != null)
        {
            uint selfUid = _character.Uid.Value;

            ForEachClientInRange.Invoke(_character.Position, UpdateRange, selfUid,
                (observerCh, observerClient) =>
                {
                    bool isStaff = observerCh.AllShow ||
                        observerCh.PrivLevel >= Core.Enums.PrivLevel.Counsel;
                    if (isStaff)
                    {
                        observerClient.Send(mobileMoving);
                        return;
                    }

                    if (warMode)
                    {
                        // Manifest: spawn ghost as translucent grey on this
                        // plain observer's client and start tracking it so
                        // BuildViewDelta will keep it in sync (manifested
                        // ghosts are now in the delta filter's allow-list).
                        // NotifyCharacterAppear handles the hue=0x4001 draw
                        // and _knownChars insert in one call (mirroring the
                        // login/teleport entry path).
                        observerClient.NotifyCharacterAppear(_character);
                    }
                    else
                    {
                        // Un-manifest: drop the mobile from the plain
                        // observer's view so they no longer see it.
                        observerClient.RemoveKnownChar(selfUid, sendDelete: true);
                    }
                });
        }
        else
        {
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, mobileMoving,
                _character.Uid.Value);
        }

        _logger.LogDebug("[war_mode] client={ClientId} char=0x{Char:X8} {Old}->{New}",
            _netState.Id, _character.Uid.Value, oldState ? "war" : "peace", _character.IsInWarMode ? "war" : "peace");
    }

    public static int GetSwingDelayMs(Character attacker, Item? weapon)
        => CombatEngine.GetSwingDelayMs(attacker, weapon);

    /// <summary>Map a weapon (or bare fists) to the correct humanoid
    /// 0x6E animation action index. Ranged weapons trigger the nock/fire
    /// action; blades use the slash; blunt/maces use the overhead swing;
    /// unarmed uses the wrestling punch. Exact values come from
    /// ServUO MobileAnimation / Source-X AnimationRange tables.</summary>
    private static ushort GetSwingAction(Character attacker, Item? weapon)
    {
        bool mounted = attacker.IsMounted;

        if (weapon == null)
            return mounted ? (ushort)AnimationType.HorseSlap : (ushort)AnimationType.AttackWrestle;

        bool twoHand = weapon.EquipLayer == Layer.TwoHanded;

        if (mounted)
        {
            return weapon.ItemType switch
            {
                ItemType.WeaponBow => (ushort)AnimationType.HorseAttackBow,
                ItemType.WeaponXBow => (ushort)AnimationType.HorseAttackXBow,
                _ => (ushort)AnimationType.HorseAttack,
            };
        }

        return weapon.ItemType switch
        {
            ItemType.WeaponBow => (ushort)AnimationType.AttackBow,
            ItemType.WeaponXBow => (ushort)AnimationType.AttackXBow,
            ItemType.WeaponSword => twoHand
                ? (ushort)AnimationType.Attack2HSlash : (ushort)AnimationType.AttackWeapon,
            ItemType.WeaponAxe => twoHand
                ? (ushort)AnimationType.Attack2HPierce : (ushort)AnimationType.Attack1HPierce,
            ItemType.WeaponFence => twoHand
                ? (ushort)AnimationType.Attack2HPierce : (ushort)AnimationType.Attack1HPierce,
            ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or
            ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or
            ItemType.WeaponMacePick or ItemType.WeaponWhip => twoHand
                ? (ushort)AnimationType.Attack2HBash : (ushort)AnimationType.Attack1HBash,
            ItemType.WeaponThrowing => (ushort)AnimationType.Attack2HBash,
            _ => (ushort)AnimationType.AttackWeapon,
        };
    }

    public static ushort GetNpcSwingAction(Character npc)
    {
        bool isHumanBody = npc.BodyId == 400 || npc.BodyId == 401;
        if (!isHumanBody)
            return 4; // ANIM_MON_ATTACK1

        var weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        return GetSwingAction(npc, weapon);
    }

    private static ushort GetSwingSound(Item? weapon)
    {
        if (weapon == null)
            return 0x023A; // unarmed whoosh
        return weapon.ItemType switch
        {
            Core.Enums.ItemType.WeaponBow or
            Core.Enums.ItemType.WeaponXBow => 0x0223,  // bow draw
            Core.Enums.ItemType.WeaponSword or
            Core.Enums.ItemType.WeaponAxe or
            Core.Enums.ItemType.WeaponFence => 0x023B, // blade swing
            Core.Enums.ItemType.WeaponMaceSmith or
            Core.Enums.ItemType.WeaponMaceSharp or
            Core.Enums.ItemType.WeaponMaceStaff or
            Core.Enums.ItemType.WeaponMaceCrook or
            Core.Enums.ItemType.WeaponMacePick or
            Core.Enums.ItemType.WeaponWhip => 0x023D,  // mace swing
            Core.Enums.ItemType.WeaponThrowing => 0x0238,
            _ => 0x023B,
        };
    }

    /// <summary>Compute the UO notoriety byte for <paramref name="ch"/> as
    /// seen by this client's character. The client reads this byte (part of
    /// 0x77/0x78/etc.) to pick the overhead-name hue:
    /// 1=blue/innocent, 2=green/friend, 3=grey/neutral NPC,
    /// 4=grey/criminal, 5=orange/enemy-guild, 6=red/murderer,
    /// 7=yellow/invul. Returning 1 for everyone (as we did until now)
    /// rendered every mobile in neutral grey. Source-X:
    /// CChar::Noto_GetFlag / Noto_CalcFlag in CCharNotoriety.cpp.</summary>
    private byte GetNotoriety(Character ch)
    {
        if (_character == null || ch == _character)
            return 1; // self reads as innocent (blue)

        // Invulnerable first — always yellow regardless of karma/guild.
        if (ch.IsStatFlag(StatFlag.Invul))
            return 7;

        // Incognito — always grey (Source-X: STATF_INCOGNITO → NOTO_NEUTRAL)
        if (ch.IsStatFlag(StatFlag.Incognito))
            return 3;

        // Arena region — everyone neutral (Source-X: REGION_FLAG_ARENA)
        var targetRegion = _world?.FindRegion(ch.Position);
        if (targetRegion != null && targetRegion.IsFlag(RegionFlag.Arena))
            return 3;

        // Red zone — reversed notoriety (Source-X: REGION_FLAG_RED)
        bool isRedZone = targetRegion != null && targetRegion.IsFlag(RegionFlag.RedZone);
        if (isRedZone)
        {
            if (ch.IsMurderer) return 1; // murderers are "innocent" in red zones
            if (ch.Karma > 0) return 6;  // good karma is "evil" in red zones
        }

        // Murderers are red even if they share a guild with the viewer.
        if (ch.IsMurderer)
            return 6;

        // Active criminal flag from MakeCriminal()
        if (ch.IsCriminal || ch.IsStatFlag(StatFlag.Criminal))
            return 4;

        // Guild relations: same guild / ally = green, at-war = orange.
        var guildMgr = Character.ResolveGuildManager?.Invoke(_character.Uid);
        if (guildMgr != null)
        {
            var myGuild = guildMgr.FindGuildFor(_character.Uid);
            var theirGuild = guildMgr.FindGuildFor(ch.Uid);
            if (myGuild != null && theirGuild != null)
            {
                if (myGuild == theirGuild) return 2; // same guild → green
                if (myGuild.IsAlliedWith(theirGuild.StoneUid)) return 2; // ally → green
                if (myGuild.IsAtWarWith(theirGuild.StoneUid)) return 5; // enemy → orange
            }
        }

        // Party members read as green.
        var myParty = Character.ResolvePartyFinder?.Invoke(_character.Uid);
        if (myParty != null && myParty.IsMember(ch.Uid))
            return 2;

        // PermaGrey tag — Source-X: NOTO.PERMAGREY
        if (ch.TryGetTag("NOTO.PERMAGREY", out string? pg) && pg == "1")
            return 3;

        bool isActuallyPlayer = ch.IsPlayer || ch.TryGetTag("ACCOUNT", out _);
        if (!isActuallyPlayer)
            return GetNpcNotoriety(ch);

        return 1; // default player → innocent / blue
    }

    /// <summary>Notoriety for non-player mobiles. Source-X Noto_CalcFlag
    /// for NPCs mixes brain type and karma:
    ///  - monster / berserk / dragon brain → red (always hostile)
    ///  - healer / banker → yellow (protected / invul-by-role)
    ///  - vendor / stable / guard / human → blue (friendly townfolk)
    ///  - animal → grey (neutral wildlife, huntable)
    ///  - karma overrides: very negative → red, negative → grey criminal,
    ///    very positive → blue — lets scripts flip a normally-blue
    ///    townsfolk into a red renegade via SET KARMA.</summary>
    /// <summary>Source-X Noto_IsEvil + Noto_CalcFlag for NPCs. Evil thresholds
    /// differ per brain type: Monster/Dragon karma&lt;0, Berserk always,
    /// Animal karma&lt;=-800, NPC karma&lt;=-3000.</summary>
    private static byte GetNpcNotoriety(Character ch)
    {
        switch (ch.NpcBrain)
        {
            case NpcBrainType.Monster:
            case NpcBrainType.Dragon:
            case NpcBrainType.Berserk:
                return 6; // always hostile / red
            case NpcBrainType.Healer:
            case NpcBrainType.Banker:
                return 7; // yellow — invul by role
            case NpcBrainType.Guard:
                return 1; // blue — law enforcement
            case NpcBrainType.Vendor:
            case NpcBrainType.Stable:
            case NpcBrainType.Human:
                if (ch.Karma <= -3000) return 6; // evil NPC
                if (ch.Karma <= -500) return 4;  // criminal NPC
                return 1; // friendly
            case NpcBrainType.Animal:
                if (ch.Karma <= -800) return 6; // evil animal
                return 3; // neutral wildlife
            default:
                if (ch.Karma <= -3000) return 6;
                if (ch.Karma <= -500) return 4;
                return ch.Karma > 500 ? (byte)1 : (byte)3;
        }
    }

    // ==================== ITextConsole ====================

    public PrivLevel GetPrivLevel() => _account?.PrivLevel ?? PrivLevel.Guest;

    public void SysMessage(string text)
    {
        string msg = ResolveMessage(text);
        _netState.Send(new PacketSpeechUnicodeOut(
            0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System", msg
        ));
    }

    public void SysMessage(string text, ushort hue)
    {
        string msg = ResolveMessage(text);
        _netState.Send(new PacketSpeechUnicodeOut(
            0xFFFFFFFF, 0xFFFF, 6, hue, 3, "TRK", "System", msg
        ));
    }

    /// <summary>Send speech from an NPC to this client (overhead text above the NPC).</summary>
    private void NpcSpeech(Character npc, string text)
    {
        var packet = new PacketSpeechUnicodeOut(
            npc.Uid.Value, npc.BodyId, 0, 0x03B2, 3, "TRK", npc.GetName(), text);
        BroadcastNearby?.Invoke(npc.Position, 18, packet, 0);
    }

    public string GetName() => _account?.Name ?? "?";

    public bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs)
    {
        if (_character == null) return false;

        string cmd = key.Trim();
        string upper = cmd.ToUpperInvariant();

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
        if (upper == "MESSAGE")
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
                        if (srcRefObj.TryExecuteCommand(subCmd, subArg, this))
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
                if (srcObj.TryExecuteCommand(cmd2, arg2, this))
                    return true;
                _ = TryExecuteScriptCommand(srcObj, cmd2, arg2, triggerArgs);
            }
            return true;
        }

        if (upper is "TARGETF" or "TARGETFG")
        {
            string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            if (_targetCursorActive)
                return true;
            ClearPendingTargetState();
            _pendingTargetFunction = parts[0];
            _pendingTargetArgs = parts.Length > 1 ? parts[1].Trim() : "";
            _pendingTargetAllowGround = upper == "TARGETFG";
            _pendingTargetItemUid = target is Item ti ? ti.Uid : Serial.Invalid;
            _targetCursorActive = true;
            byte tType = (byte)(upper == "TARGETFG" ? 1 : 0);
            _netState.Send(new PacketTarget(tType, (uint)Random.Shared.Next(1, int.MaxValue)));
            return true;
        }

        if (upper is "TARGET" or "TARGETG")
        {
            if (_targetCursorActive)
                return true;
            ClearPendingTargetState();
            _pendingTargetAllowGround = upper == "TARGETG";
            _targetCursorActive = true;
            byte tType = (byte)(upper == "TARGETG" ? 1 : 0);
            _netState.Send(new PacketTarget(tType, (uint)Random.Shared.Next(1, int.MaxValue)));
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

            string question = keys[0].Arg.Length > 0 ? $"{keys[0].Key} {keys[0].Arg}" : keys[0].Key;
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

        if (upper == "DIALOGCLOSE")
        {
            // Compatibility bridge: many scripts call DIALOGCLOSE before reopening.
            // We don't currently keep a server-side open-dialog registry, so treat as no-op.
            return true;
        }

        // SDIALOG = "send dialog", a Sphere alias for DIALOG used by some
        // shards' script packs. Accept both so imported scripts don't
        // need to be rewritten.
        if (upper == "DIALOG" || upper == "SDIALOG")
        {
            string raw = args.Trim();
            string dialogId = "script_dialog";
            string closeSpec = "";
            int requestedPage = 1;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                int sep = raw.IndexOfAny([' ', ',']);
                if (sep < 0)
                {
                    dialogId = raw;
                }
                else
                {
                    dialogId = raw[..sep];
                    closeSpec = raw[(sep + 1)..].TrimStart(' ', ',');
                }
            }

            dialogId = dialogId.Trim().Trim(',', ';');
            if (string.IsNullOrWhiteSpace(dialogId))
                dialogId = "script_dialog";

            if (!string.IsNullOrWhiteSpace(closeSpec))
            {
                string[] dialogTokens = closeSpec.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (dialogTokens.Length > 0 && int.TryParse(dialogTokens[0], out int parsedPage))
                    requestedPage = parsedPage;
            }

            if (OpenNamedDialog(dialogId, requestedPage))
                return true;

            string closeFn = "";
            if (!string.IsNullOrWhiteSpace(closeSpec))
            {
                string[] tokens = closeSpec.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length > 0)
                {
                    if (tokens[0].Equals("DIALOGCLOSE", StringComparison.OrdinalIgnoreCase))
                    {
                        closeFn = tokens.Length > 1 ? tokens[1] : "";
                    }
                    else
                    {
                        closeFn = tokens[0];
                    }
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
            item.BaseId = (ushort)rid.Index;
            item.Name = defName;
            _pendingScriptNewItem = item;
            return true;
        }

        if (upper.StartsWith("NEW.", StringComparison.Ordinal))
        {
            if (_pendingScriptNewItem == null) return true;
            string sub = cmd[4..].ToUpperInvariant();
            switch (sub)
            {
                case "EQUIP":
                    _character.Backpack ??= _world.CreateItem();
                    _character.Backpack.Name = "Backpack";
                    _character.Equip(_character.Backpack, Layer.Pack);
                    _character.Backpack.AddItem(_pendingScriptNewItem);
                    _pendingScriptNewItem = null;
                    return true;
                case "CONT":
                {
                    var trimmed = args.Trim();
                    if (trimmed.Length > 0 && trimmed != "-1")
                    {
                        uint cval = ObjBase.ParseHexOrDecUInt(trimmed);
                        var cont = _world.FindObject(new Serial(cval)) as Item;
                        if (cont != null) { cont.AddItem(_pendingScriptNewItem); return true; }
                    }
                    _character.Backpack ??= _world.CreateItem();
                    _character.Backpack.AddItem(_pendingScriptNewItem);
                    return true;
                }
                default:
                    _pendingScriptNewItem.TrySetProperty(sub, args);
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

                        _ = runner.TryRunFunction(funcName, player, this, callArgs, out _);
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

        if (upper == "BUY")
        {
            // Vendor buy — placeholder until full vendor buy/sell packet support
            return true;
        }

        if (upper == "BYE")
        {
            // End NPC interaction
            return true;
        }

        if (upper.StartsWith("SERV.", StringComparison.Ordinal))
        {
            // Known but not yet fully implemented service verbs should not crash scripts.
            _logger.LogDebug("Script SERV verb not fully implemented: {Verb} {Args}", key, args);
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
                    ScriptFileHandle.DeleteFile(_scriptFile.FilePath != "" ? Path.GetDirectoryName(_scriptFile.FilePath) ?? "" : "", args);
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
                    if (!_scriptLdb.ConnectFile(fileName, out string err))
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

        return false;
    }

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
        if (varName.StartsWith("ISDIALOGOPEN.", StringComparison.OrdinalIgnoreCase))
        {
            // We currently don't track open dialogs server-side; keep compatibility checks false.
            value = "0";
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
                    value = _scriptFile.ReadChar();
                    return true;
                case "READBYTE":
                    value = _scriptFile.ReadByte();
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
                    // FILE.READLINE n, FILE.SEEK pos, FILE.FILELINES path, FILE.FILEEXIST path
                    if (fileProp.StartsWith("READLINE"))
                    {
                        string lineArg = fileProp.Length > 8 ? fileProp[8..].Trim() : "";
                        // Argument may also come after space: FILE.READLINE 3
                        if (string.IsNullOrEmpty(lineArg) && varName.Length > 13)
                            lineArg = varName[13..].Trim();
                        int lineNum = 0;
                        if (!string.IsNullOrEmpty(lineArg))
                            int.TryParse(lineArg, out lineNum);
                        value = _scriptFile.ReadLine(lineNum);
                        return true;
                    }
                    if (fileProp.StartsWith("SEEK"))
                    {
                        string seekArg = fileProp.Length > 4 ? fileProp[4..].Trim() : "";
                        if (string.IsNullOrEmpty(seekArg) && varName.Length > 9)
                            seekArg = varName[9..].Trim();
                        _scriptFile.Seek(seekArg);
                        value = _scriptFile.Position.ToString();
                        return true;
                    }
                    if (fileProp.StartsWith("FILELINES"))
                    {
                        string flArg = fileProp.Length > 9 ? fileProp[9..].Trim() : "";
                        if (string.IsNullOrEmpty(flArg) && varName.Length > 14)
                            flArg = varName[14..].Trim();
                        value = ScriptFileHandle.GetFileLines(
                            Path.GetDirectoryName(_scriptFile.FilePath) ?? "", flArg).ToString();
                        return true;
                    }
                    if (fileProp.StartsWith("FILEEXIST"))
                    {
                        string feArg = fileProp.Length > 9 ? fileProp[9..].Trim() : "";
                        if (string.IsNullOrEmpty(feArg) && varName.Length > 14)
                            feArg = varName[14..].Trim();
                        value = ScriptFileHandle.FileExists(
                            Path.GetDirectoryName(_scriptFile.FilePath) ?? "", feArg) ? "1" : "0";
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
            var p = _lastScriptTargetPoint ?? _character.Position;
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
            return ch.GetMemoryEntriesByType(args);
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

    private string ResolveMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _defMessageLookup == null)
            return text;

        string key = text.Trim();
        if (key.StartsWith('@'))
            key = key[1..];
        if (key.StartsWith("DEFMSG.", StringComparison.OrdinalIgnoreCase))
            key = key[7..];
        if (key.Contains(' '))
            return text;

        return _defMessageLookup(key) ?? text;
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

    private static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    // ==================== Phase 1: Critical Stability Handlers ====================

    /// <summary>Handle death menu response (0x2C).</summary>
    public void HandleDeathMenu(byte action)
    {
        if (_character == null) return;

        switch (action)
        {
            case 0: // Client requesting death menu — ignore, we already sent it
                break;
            case 1: // Resurrect
                _logger.LogDebug("[death_menu] char=0x{Uid:X8} chose resurrect", _character.Uid.Value);
                OnResurrect();
                break;
            case 2: // Stay as ghost
                _logger.LogDebug("[death_menu] char=0x{Uid:X8} chose ghost", _character.Uid.Value);
                break;
        }
    }

    /// <summary>Handle character delete from char select screen (0x83).</summary>
    public void HandleCharDelete(int charIndex, string password)
    {
        if (_account == null) return;

        // Verify password
        if (!_account.CheckPassword(password))
        {
            _netState.Send(new PacketCharDeleteResult(1)); // 1=bad password
            return;
        }

        var charUid = _account.GetCharSlot(charIndex);
        if (!charUid.IsValid)
        {
            _netState.Send(new PacketCharDeleteResult(1));
            return;
        }

        var ch = _world.FindChar(charUid);
        if (ch != null)
        {
            if (ch.IsOnline)
            {
                _netState.Send(new PacketCharDeleteResult(5)); // 5=char in world
                return;
            }

            _logger.LogInformation("Deleting character '{Name}' (0x{Uid:X8}) from account '{Acct}'",
                ch.Name, charUid.Value, _account.Name);
            ch.Delete();
            _world.DeleteObject(ch);
        }

        _account.SetCharSlot(charIndex, Serial.Invalid);

        // Send success + new char list
        _netState.Send(new PacketCharDeleteResult(0));
        var charNames = _account.GetCharNames(uid => _world.FindChar(uid)?.GetName());
        _netState.Send(new PacketCharList(charNames).Build());
    }

    /// <summary>Handle dye response from color picker (0x95).</summary>
    public void HandleDyeResponse(uint itemSerial, ushort hue)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(itemSerial));
        if (item == null) return;

        // Only GM can dye any item; players need a dye vat interaction (handled by script)
        if (_account?.PrivLevel < PrivLevel.GM)
        {
            SysMessage(ServerMessages.Get("itemuse_dye_fail"));
            return;
        }

        _logger.LogDebug("[dye_response] char=0x{Uid:X8} item=0x{Item:X8} hue={Hue}",
            _character.Uid.Value, itemSerial, hue);
        item.Hue = new Core.Types.Color(hue);

        // Refresh item for nearby clients
        var itemPacket = new PacketWorldItem(
            item.Uid.Value, item.DispIdFull, item.Amount,
            item.X, item.Y, item.Z, item.Hue);
        BroadcastNearby?.Invoke(item.Position, UpdateRange, itemPacket, 0);
    }

    private Action<uint, uint, uint, string>? _pendingPromptCallback;
    private uint _pendingPromptId;

    /// <summary>Send a text prompt to the client and register a callback for the response.</summary>
    public void SendPrompt(uint promptId, string message, Action<uint, uint, uint, string>? callback = null)
    {
        if (_character == null) return;
        _pendingPromptId = promptId;
        _pendingPromptCallback = callback;
        _netState.Send(new PacketPromptRequest(_character.Uid.Value, promptId, message).Build());
    }

    /// <summary>Handle prompt response (0x9A) — rune names, house signs, etc.</summary>
    public void HandlePromptResponse(uint serial, uint promptId, uint type, string text)
    {
        if (_character == null) return;

        _logger.LogDebug("[prompt_response] char=0x{Uid:X8} promptId={PromptId} type={Type} text='{Text}'",
            _character.Uid.Value, promptId, type, text);

        if (type == 0)
        {
            // Cancelled
            _pendingPromptCallback = null;
            return;
        }

        // Dispatch to pending callback
        if (_pendingPromptCallback != null)
        {
            _pendingPromptCallback(serial, promptId, type, text);
            _pendingPromptCallback = null;
            return;
        }

        // Default: try to set the name of the target item (rune, house sign)
        var item = _world.FindItem(new Serial(serial));
        if (item != null && !string.IsNullOrWhiteSpace(text))
        {
            item.Name = text.Trim();
            SysMessage(ServerMessages.GetFormatted("msg_name_set", item.Name));
        }
    }

    /// <summary>Handle old-style menu choice response (0x7D).</summary>
    public void HandleMenuChoice(uint serial, ushort menuId, ushort index, ushort modelId)
    {
        if (_character == null) return;

        _logger.LogDebug("[menu_choice] char=0x{Uid:X8} serial=0x{Serial:X8} menuId={MenuId} index={Index} modelId=0x{Model:X4}",
            _character.Uid.Value, serial, menuId, index, modelId);

        if (menuId == EditMenuId)
        {
            HandleEditMenuChoice(index);
            return;
        }

        var options = _pendingMenuOptions;
        var defname = _pendingMenuDefname;
        _pendingMenuOptions = null;
        _pendingMenuDefname = "";

        if (index == 0)
        {
            // Cancel — fire @Cancel trigger if a MENU section trigger handler exists
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserExtCmd,
                new TriggerArgs { CharSrc = _character, S1 = $"menu_{defname}_cancel" });
            return;
        }

        if (options != null && index >= 1 && index <= options.Count)
        {
            var chosen = options[index - 1];
            foreach (var scriptKey in chosen.Script)
            {
                TryExecuteScriptCommand(_character, scriptKey.Key, scriptKey.Arg, null);
            }
            return;
        }

        // Fallback: generic trigger for unhandled menus
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserExtCmd,
            new TriggerArgs { CharSrc = _character, S1 = $"menu_{menuId}_{index}" });
    }

    // ==================== Phase 2: Content Feature Handlers ====================

    private void OpenBook(Item book, bool writable)
    {
        if (_character == null) return;

        string title = book.TryGetTag("BOOK_TITLE", out string? t) && t != null ? t : book.GetName();
        string author = book.TryGetTag("BOOK_AUTHOR", out string? a) && a != null ? a : "";
        ushort pageCount = 16;
        if (book.TryGetTag("BOOK_PAGES", out string? ps) && ushort.TryParse(ps, out ushort pc))
            pageCount = pc;

        _netState.Send(new PacketBookHeaderOut(
            book.Uid.Value, writable, pageCount, title, author));

        var pages = new List<(ushort PageNum, string[] Lines)>();
        for (ushort i = 1; i <= pageCount; i++)
        {
            string[] lines;
            if (book.TryGetTag($"PAGE_{i}", out string? content) && !string.IsNullOrEmpty(content))
                lines = content.Split('\n');
            else
                lines = [];
            pages.Add((i, lines));
        }

        _netState.Send(new PacketBookPageContent(
            book.Uid.Value, pages.ToArray()));
    }

    /// <summary>Handle book page read/write (0x66).</summary>
    public void HandleBookPage(uint serial, List<(ushort PageNum, string[] Lines)> pages)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        foreach (var (pageNum, lines) in pages)
        {
            if (lines.Length == 0)
            {
                // Read request — send page content back
                string[] pageLines;
                if (item.TryGetTag($"PAGE_{pageNum}", out string? content) && !string.IsNullOrEmpty(content))
                    pageLines = content.Split('\n');
                else
                    pageLines = [];

                _netState.Send(new PacketBookPageContent(
                    serial, [(pageNum, pageLines)]));
                continue;
            }

            // Write request — store page content in tags
            string pageContent = string.Join("\n", lines);
            item.SetTag($"PAGE_{pageNum}", pageContent);
        }
    }

    /// <summary>Handle book header change (0x93).</summary>
    public void HandleBookHeader(uint serial, bool writable, string title, string author)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        _logger.LogDebug("[book_header] item=0x{Item:X8} title='{Title}' author='{Author}'",
            serial, title, author);

        if (writable)
        {
            item.SetTag("BOOK_TITLE", title);
            item.SetTag("BOOK_AUTHOR", author);
        }
    }

    /// <summary>Handle bulletin board list request (0x71 sub 3).</summary>
    public void HandleBulletinBoardRequestList(uint boardSerial)
    {
        if (_character == null) return;
        _logger.LogDebug("[bboard_list] char=0x{Uid:X8} board=0x{Board:X8}",
            _character.Uid.Value, boardSerial);
        // Bulletin board content is managed via scripts/TAGs
    }

    /// <summary>Handle bulletin board message read (0x71 sub 4).</summary>
    public void HandleBulletinBoardRequestMessage(uint boardSerial, uint msgSerial)
    {
        if (_character == null) return;
        _logger.LogDebug("[bboard_read] board=0x{Board:X8} msg=0x{Msg:X8}", boardSerial, msgSerial);
    }

    /// <summary>Handle bulletin board post (0x71 sub 5).</summary>
    public void HandleBulletinBoardPost(uint boardSerial, uint replyTo, string subject, string[] bodyLines)
    {
        if (_character == null) return;
        _logger.LogDebug("[bboard_post] board=0x{Board:X8} subject='{Subject}' lines={Lines}",
            boardSerial, subject, bodyLines.Length);
        SysMessage(ServerMessages.Get("msg_message_posted"));
    }

    /// <summary>Handle bulletin board delete (0x71 sub 6).</summary>
    public void HandleBulletinBoardDelete(uint boardSerial, uint msgSerial)
    {
        if (_character == null) return;
        _logger.LogDebug("[bboard_delete] board=0x{Board:X8} msg=0x{Msg:X8}", boardSerial, msgSerial);
    }

    /// <summary>Handle map detail request (0x90).</summary>
    public void HandleMapDetail(uint serial)
    {
        if (_character == null) return;
        _logger.LogDebug("[map_detail] char=0x{Uid:X8} map=0x{Serial:X8}",
            _character.Uid.Value, serial);
        // Map detail rendering is handled client-side with MUL data
    }

    /// <summary>Handle map pin edit (0x56).</summary>
    public void HandleMapPinEdit(uint serial, byte action, byte pinId, ushort x, ushort y)
    {
        if (_character == null) return;

        var item = _world.FindItem(new Serial(serial));
        if (item == null) return;

        _logger.LogDebug("[map_pin] item=0x{Item:X8} action={Action} pin={PinId} x={X} y={Y}",
            serial, action, pinId, x, y);

        switch (action)
        {
            case 1: // Add pin
                item.SetTag($"PIN_{pinId}", $"{x},{y}");
                break;
            case 6: // Insert pin
                item.SetTag($"PIN_{pinId}", $"{x},{y}");
                break;
            case 7: // Move pin
                item.SetTag($"PIN_{pinId}", $"{x},{y}");
                break;
        }
    }

    // ==================== Phase 3: Client Compatibility Handlers ====================

    /// <summary>Handle 0xAC Gump Value Input reply (response to a 0xAB
    /// dialog opened by the Source-X <c>INPDLG</c> verb). Looks up the
    /// pending <c>(serial, context)</c> entry stored when the prompt was
    /// sent and writes <paramref name="text"/> into the named property
    /// on the target object via <c>TrySetProperty</c>.</summary>
    public void HandleGumpTextEntry(uint serial, ushort context, byte action, string text)
    {
        if (_character == null) return;

        var key = (serial, context);
        if (!_pendingInputDlg.TryGetValue(key, out var propName))
        {
            _logger.LogDebug("[inpdlg] unexpected text input: serial=0x{S:X8} ctx=0x{C:X4}", serial, context);
            return;
        }
        _pendingInputDlg.Remove(key);

        if (action == 0)
        {
            _logger.LogDebug("[inpdlg] cancelled by user (serial=0x{S:X8} prop={P})", serial, propName);
            return;
        }

        IScriptObj? target = _world.FindChar(new Serial(serial)) as IScriptObj
            ?? _world.FindItem(new Serial(serial)) as IScriptObj;
        if (target == null)
        {
            _logger.LogDebug("[inpdlg] target serial 0x{S:X8} no longer exists", serial);
            return;
        }

        // Source-X parity: a single "#" means "default value" — currently
        // we just clear the property (TrySetProperty empty arg).
        string value = text == "#" ? "" : text;

        var posBefore = (target as Character)?.Position;
        ushort bodyBefore = (target as Character)?.BodyId ?? 0;
        ushort hueBefore = (target as Character)?.Hue.Value ?? 0;

        if (!target.TrySetProperty(propName, value))
        {
            // Source-X falls back to executing the verb if it isn't a
            // straight property — handles "INPDLG ANIM 30" style edits.
            target.TryExecuteCommand(propName, value, this);
        }

        if (target is Character ch)
        {
            bool moved = posBefore.HasValue && !ch.Position.Equals(posBefore.Value);
            bool appearance = ch.BodyId != bodyBefore || ch.Hue.Value != hueBefore;
            if (moved)
            {
                _world.MoveCharacter(ch, ch.Position);
                if (ch == _character)
                    Resync();
                BroadcastDrawObject(ch);
            }
            else if (appearance)
            {
                BroadcastDrawObject(ch);
            }
        }
    }

    /// <summary>
    /// Open a Source-X style <c>INPDLG</c> input prompt on this client.
    /// The user types a value into a small text-entry gump; on submit,
    /// <see cref="HandleGumpTextEntry"/> writes that value into
    /// <paramref name="propName"/> on <paramref name="target"/>.
    /// </summary>
    public void SendInputPromptGump(IScriptObj target, string propName, int maxLength)
    {
        if (target == null || string.IsNullOrWhiteSpace(propName))
            return;

        uint targetSerial = 0;
        if (target is Character ch) targetSerial = ch.Uid.Value;
        else if (target is Item it) targetSerial = it.Uid.Value;
        else return;

        ushort context = unchecked(_nextInputDlgContext++);
        if (_nextInputDlgContext == 0)
            _nextInputDlgContext = 0x1000;

        _pendingInputDlg[(targetSerial, context)] = propName;

        string current = ".";
        if (target.TryGetProperty(propName, out var cur) && !string.IsNullOrEmpty(cur))
            current = cur;

        string caption = $"{propName} (# = default)";
        string description = string.IsNullOrEmpty(current) ? "." : current;

        var packet = new PacketGumpValueInput(
            targetSerial,
            context,
            caption,
            description,
            (uint)Math.Max(1, maxLength),
            PacketGumpValueInput.InputStyle.TextEdit,
            cancel: true);
        _netState.Send(packet);
    }

    /// <summary>Handle all names request (0x98).</summary>
    public void HandleAllNamesRequest(uint serial)
    {
        if (_character == null) return;

        var ch = _world.FindChar(new Serial(serial));
        if (ch != null)
        {
            _netState.Send(new PacketAllNamesResponse(serial, ch.GetName()).Build());
            return;
        }

        var item = _world.FindItem(new Serial(serial));
        if (item != null)
        {
            _netState.Send(new PacketAllNamesResponse(serial, item.GetName()).Build());
        }
    }

    // ==================== Helpers ====================

    private static void GetDirectionDelta(Direction dir, out short dx, out short dy)
    {
        dx = 0; dy = 0;
        switch (dir)
        {
            case Direction.North: dy = -1; break;
            case Direction.NorthEast: dx = 1; dy = -1; break;
            case Direction.East: dx = 1; break;
            case Direction.SouthEast: dx = 1; dy = 1; break;
            case Direction.South: dy = 1; break;
            case Direction.SouthWest: dx = -1; dy = 1; break;
            case Direction.West: dx = -1; break;
            case Direction.NorthWest: dx = -1; dy = -1; break;
        }
    }
}
