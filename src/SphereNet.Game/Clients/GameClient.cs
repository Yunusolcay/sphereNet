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
    // Source-X FEATURE* settings are independent per-expansion capability
    // masks. They are translated to 0xB9/0xA9 wire flags during login.
    public static int ServerFeatureT2A { get; set; } = 0x03;
    public static int ServerFeatureLBR { get; set; } = 0x03;
    public static int ServerFeatureAOS { get; set; } = 0x0F;
    public static int ServerFeatureSE { get; set; } = 0x03;
    public static int ServerFeatureML { get; set; } = 0x01;
    public static int ServerFeatureKR { get; set; }
    public static int ServerFeatureSA { get; set; } = 0x03;
    public static int ServerFeatureTOL { get; set; } = 0x01;
    public static int ServerFeatureExtra { get; set; }
    public static int ServerMaxCharsPerAccount { get; set; } = 7;
    /// <summary>sphere.ini MinCharDeleteTime in DAYS: a character younger than
    /// this cannot be deleted from the char-select screen (Source-X
    /// Setup_Delete; Counsel+ accounts bypass). 0 disables the gate.</summary>
    public static int ServerMinCharDeleteDays { get; set; } = 7;
    public static bool ServerAutoResDisp { get; set; } = true;
    public static int ServerToolTipMode { get; set; } = 1;
    public static OptionFlags ServerOptionFlags { get; set; } = OptionFlags.FileCommands | OptionFlags.Buffs;
    public static NotorietyHueSettings NotorietyHues { get; set; } = new();
    public static int ClientLingerSeconds { get; set; } = 60;
    public static Func<string, Point3D?>? BotSpawnLocationProvider;

    /// <summary>Builds the 0xA8 login server list for a connecting client (config-driven
    /// self entry + any configured extra shards). Wired by the host; null → single
    /// hardcoded fallback entry.</summary>
    public static Func<NetState, IReadOnlyList<SphereNet.Network.Packets.Outgoing.ServerListEntry>>? ServerListProvider;

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
    private CustomHousingEngine? _customHousing;
    private Chat.ChatEngine? _chatEngine;
    private GuildManager? _guildManager;
    private Mounts.MountEngine? _mountEngine;
    private TriggerDispatcher? _triggerDispatcher;
    private ScriptSystemHooks? _systemHooks;
    public static Action<Character>? OnWakeNpc;
    private ScriptDbAdapter? _scriptDb;
    private ScriptDbAdapter? _scriptLdb;
    private ScriptDbAdapter? _scriptMdb;
    private string _scriptDatabaseRoot = AppContext.BaseDirectory;
    private ScriptFileHandle? _scriptFile;
    private Func<string, string?>? _defMessageLookup;

    /// <summary>Callback to broadcast a packet to all clients whose character is near a point.</summary>
    public Action<Point3D, int, PacketWriter, uint>? BroadcastNearby { get; set; }
    /// <summary>Broadcast movement and update nearby clients' View.LastKnownPos to prevent duplicate 0x77.</summary>
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

    /// <summary>Appearance data from 0xF8/0x00 packet, consumed once during
    /// new character creation inside HandleCharSelect.</summary>
    public CharCreateInfo? PendingCharCreate { get; set; }

    private Account? _account;
    private Character? _character;

    /// <summary>View-delta bookkeeping (decomposition phase 2).</summary>
    internal ClientViewCache View => _view;
    private readonly ClientViewCache _view = new();
    /// <summary>Open gump/dialog bookkeeping (decomposition phase 1).</summary>
    internal ClientGumpRegistry Gumps => _gumps;
    private readonly ClientGumpRegistry _gumps = new();
    /// <summary>Target-cursor state machine (decomposition phase 1).</summary>
    internal ClientTargetState Targets => _targets;
    private readonly ClientTargetState _targets = new();
    private string? _pendingDialogCloseFunction;
    private string _pendingDialogArgs = "";
    /// <summary>Dialog close-function state bridges for extracted handler
    /// classes (decomposition phase 3) — the fields themselves move into the
    /// dialog handler in phase 3e.</summary>
    internal string? PendingDialogCloseFunction
    {
        get => _pendingDialogCloseFunction;
        set => _pendingDialogCloseFunction = value;
    }
    internal string PendingDialogArgs
    {
        get => _pendingDialogArgs;
        set => _pendingDialogArgs = value;
    }
    internal List<MenuOptionEntry>? _pendingMenuOptions;
    internal ushort _pendingMenuId;
    internal string _pendingMenuDefname = "";
    private const ushort EditMenuId = 0xFFED;
    private uint[]? _pendingEditMenuUids;
    private Item?[]? _pendingEditMenuMemories;
    internal short _lastHits, _lastMana, _lastStam;
    internal long _lastVitalsPacketTick;
    internal const int VitalsPacketIntervalMs = 250;
    internal const int UpdateRange = 18;

    public NetState NetState => _netState;
    /// <summary>World access for extracted handler classes (decomposition phase 3).</summary>
    internal GameWorld World => _world;
    internal TriggerDispatcher? Triggers => _triggerDispatcher;
    internal HousingEngine? Housing => _housingEngine;
    internal TradeManager? TradeM => _tradeManager;
    internal CraftingEngine? CraftE => _craftingEngine;
    internal CommandHandler? Cmds => _commands;
    internal MovementEngine? MoveEng => _movement;
    internal SpeechEngine? SpeechEng => _speech;
    internal DeathEngine? DeathEng => _deathEngine;
    internal ScriptFileHandle? ScriptFile => _scriptFile;
    internal ScriptDbAdapter? ScriptDb => _scriptDb;
    internal ScriptDbAdapter? ScriptLdb => _scriptLdb;
    internal ScriptDbAdapter? ScriptMdb => _scriptMdb;
    internal string ScriptDatabaseRoot => _scriptDatabaseRoot;
    internal GuildManager? GuildM => _guildManager;
    internal PartyManager? PartyM => _partyManager;
    internal SpellEngine? Spells => _spellEngine;
    internal SkillHandlers? SkillH => _skillHandlers;
    internal Mounts.MountEngine? MountE => _mountEngine;
    internal ILogger Log => _logger;
    public Account? Account => _account;
    public Character? Character => _character;

    /// <summary>CTextConsole::GetChar — the character this console speaks for.</summary>
    public SphereNet.Core.Interfaces.IScriptObj? GetSourceChar() => _character;
    public bool IsPlaying => _character != null && !_character.IsDeleted;
    public bool HasPendingTarget => Targets.CursorActive;

    /// <summary>A disconnect changes the party like any other departure, so the people
    /// still connected are told: Source-X takes the same RemoveMember path when a
    /// character disconnects (SetDisconnected, CChar.cpp:528 -> CParty.cpp:296), which
    /// sends the new member list, the leader change and the map pins. SphereNet mutated
    /// the membership in memory and sent nothing, so the rest of the party went on
    /// showing a member - or a leader - who had gone.</summary>
    private void LeavePartyOnDisconnect()
    {
        if (_character == null || _partyManager == null)
            return;

        var party = _partyManager.FindParty(_character.Uid);
        if (party == null)
            return;

        var membersBefore = party.Members.ToList();
        _partyManager.Leave(_character.Uid);

        uint[] remaining = party.Members.Select(m => m.Value).ToArray();
        foreach (var uid in membersBefore)
        {
            if (uid == _character.Uid)
                continue;
            SendToChar?.Invoke(uid, new PacketPartyRemoveMember(_character.Uid.Value, remaining));
            SendToChar?.Invoke(uid, new PacketWaypointRemove(_character.Uid.Value));
        }
    }

    /// <summary>Called when the network connection is closed. Marks character as offline.</summary>
    public void OnDisconnect()
    {
        if (_character != null)
        {
            bool wasOnline = _character.IsOnline;
            long utcNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool campingSafe = _character.TryGetTag("CAMPING_SAFE_LOGOUT_UNTIL", out string? safeText) &&
                long.TryParse(safeText, out long safeUntil) && utcNow <= safeUntil;
            bool instantRegion = _world.FindRegion(_character.Position)?.IsFlag(RegionFlag.InstaLogout) == true;
            bool safeLogout = campingSafe || instantRegion || _character.PrivLevel >= PrivLevel.GM;
            bool linger = wasOnline && !safeLogout && !_character.IsDead && ClientLingerSeconds > 0;

            _logger.LogInformation("[LOGOUT] '{Name}' pos: {X},{Y},{Z} map={Map}",
                _character.Name, _character.X, _character.Y, _character.Z, _character.Position.Map);

            int abortedSkill = _character.ClearActiveSkillPending();
            if (abortedSkill >= 0)
                Character.ActiveSkillAborted?.Invoke(_character, abortedSkill);
            _character.InterruptMeditation();
            _character.ClearCastState();
            _worldFeatures?.CancelPendingCraftOnDisconnect();
            if (Targets.SkillCancelId >= 0)
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillTargetCancel,
                    new TriggerArgs { CharSrc = _character, N1 = Targets.SkillCancelId });
            if (_character.TryGetTag("SKILL_MENU_PENDING", out string? menuSkillText) &&
                int.TryParse(menuSkillText, out int menuSkill))
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillAbort,
                    new TriggerArgs { CharSrc = _character, N1 = menuSkill });

            if (!linger)
            {
                BroadcastNearby?.Invoke(_character.Position, UpdateRange,
                    new PacketDeleteObject(_character.Uid.Value), _character.Uid.Value);
            }

            _systemHooks?.DispatchClient("disconnect", _character, _account);
            AbortActiveTradeOnDisconnect();
            ChatOnDisconnect();
            LeavePartyOnDisconnect();
            EngineTags.StripEphemeral(_character);
            if (linger)
                _character.SetTag("CLIENT_LINGER_UNTIL",
                    (utcNow + (long)ClientLingerSeconds * 1000L).ToString());

            Targets.Callback = null;
            Targets.CursorActive = false;
            Dialogs.PendingInputDlg.Clear();
            _pendingMenuOptions = null;
            _pendingEditMenuUids = null;
            _pendingEditMenuMemories = null;
            if (Targets.ScriptNewItem != null)
            {
                _world.RemoveItem(Targets.ScriptNewItem);
                Targets.ScriptNewItem = null;
            }
            Targets.SkillCancelId = -1;
            Targets.Dupe = false;
            Targets.Heal = false;
            Targets.Kill = false;
            Targets.Bank = false;
            Targets.SummonTo = false;
            Targets.Mount = false;
            Targets.SummonCage = false;

            _character.IsOnline = false;
            _character.CTags.RemoveByPrefix("");
            OnCharacterOffline?.Invoke(_character);
            if (!linger)
                _world.RemoveOnlinePlayer(_character);
            View.KnownItems.Clear();
            View.KnownChars.Clear();
            View.KnownDoorOverrides.Clear();
            Gumps.ActiveGumps.Clear();
            Gumps.Callbacks.Clear();
            View.LastKnownPos.Clear();
            View.LastKnownItemState.Clear();
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
        _netState.PacketDebugClassifier = ClassifyPacketDebug;
    }

    private string ClassifyPacketDebug(ReadOnlySpan<byte> data)
    {
        if (!TryReadPacketSerial(data, out uint rawSerial))
            return "packet";

        var serial = new Serial(rawSerial);
        var ch = _world.FindChar(serial);
        if (ch != null)
            return ch.IsPlayer ? "player" : "npc";

        if (_world.FindItem(serial) != null)
            return "item";

        return (rawSerial & 0x40000000) != 0 ? "item" : "packet";
    }

    private static bool TryReadPacketSerial(ReadOnlySpan<byte> data, out uint serial)
    {
        serial = 0;
        if (data.Length == 0)
            return false;

        int offset = data[0] switch
        {
            0x1A => data.Length >= 7 ? 3 : -1,
            0x1D => data.Length >= 5 ? 1 : -1,
            0x2E => data.Length >= 5 ? 1 : -1,
            0x78 => data.Length >= 7 ? 3 : -1,
            0x77 or 0x20 or 0x11 or 0x88 or 0xAE => data.Length >= 5 ? 1 : -1,
            _ => -1
        };

        if (offset < 0 || data.Length < offset + 4)
            return false;

        serial = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        return true;
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
        Mounts.MountEngine? mountEngine = null,
        CustomHousingEngine? customHousing = null,
        Chat.ChatEngine? chatEngine = null)
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
        _customHousing = customHousing;
        _chatEngine = chatEngine;

        if (_spellEngine != null && triggerDispatcher != null)
            _spellEngine.TriggerDispatcher = triggerDispatcher;
    }

    internal bool TryMountCharacter(Character mount)
    {
        if (_character == null || _mountEngine == null)
            return false;

        var args = new TriggerArgs
        {
            CharSrc = _character,
            O1 = mount,
            N1 = Mounts.MountEngine.GetMountItemId(mount.BodyId)
        };
        if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Mount, args) == TriggerResult.True)
            return false;

        ushort mountItemId = (ushort)Math.Clamp(args.N1, 0, ushort.MaxValue);
        return _mountEngine.TryMount(_character, mount, mountItemId);
    }

    internal Character? DismountCharacter()
    {
        if (_character == null || _mountEngine == null)
            return null;

        return _mountEngine.Dismount(_character, mount =>
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.Dismount,
                new TriggerArgs { CharSrc = _character, O1 = mount }) == TriggerResult.True);
    }

    public void SetScriptServices(
        ScriptSystemHooks? systemHooks = null,
        ScriptDbAdapter? scriptDb = null,
        Func<string, string?>? defMessageLookup = null,
        ScriptFileHandle? scriptFile = null,
        ScriptDbAdapter? scriptLdb = null,
        string? scriptDatabaseRoot = null,
        ScriptDbAdapter? scriptMdb = null)
    {
        _systemHooks = systemHooks;
        _scriptDb = scriptDb;
        _scriptLdb = scriptLdb;
        _scriptMdb = scriptMdb;
        if (!string.IsNullOrWhiteSpace(scriptDatabaseRoot))
            _scriptDatabaseRoot = scriptDatabaseRoot;
        _defMessageLookup = defMessageLookup;
        _scriptFile = scriptFile;
    }
}

public sealed record NotorietyHueSettings(
    ushort Good = 0x0059,
    ushort GoodNpc = 0x0059,
    ushort GuildSame = 0x003F,
    ushort Neutral = 0x03B2,
    ushort Criminal = 0x03B2,
    ushort GuildWar = 0x0090,
    ushort Evil = 0x0022,
    ushort Invul = 0x0035,
    ushort InvulGameMaster = 0x000B,
    ushort Default = 0x03B2);
