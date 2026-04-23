using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Objects.Characters;

/// <summary>
/// Character instance (player or NPC). Maps to CChar in Source-X.
/// </summary>
public class Character : ObjBase
{
    /// <summary>
    /// Optional diagnostic sink. Wired by the host (Program.cs) so
    /// trace lines emitted from inside Character (vendor restock,
    /// trigger verb dispatch, etc.) reach the same logger pipeline
    /// as the rest of the server. Avoids needing an ILogger field on
    /// every Character instance.
    /// </summary>
    public static Action<string>? Diagnostic { get; set; }


    // Static delegate for guild resolution (set in Program.cs)
    public static Func<Serial, Guild.GuildManager?>? ResolveGuildManager;
    // Static delegate for party resolution (set in Program.cs)
    public static Func<Serial, Party.PartyDef?>? ResolvePartyFinder;
    // Static delegate for party manager (set in Program.cs) — needed for commands
    public static Func<Party.PartyManager?>? ResolvePartyManager;
    // Static delegate for account resolution from character UID (set in Program.cs)
    public static Func<Serial, Account?>? ResolveAccountForChar;
    // Static delegate for character lookup by UID — used for ACCOUNT.CHAR.N.NAME
    // chain and admin dialog references (set in Program.cs).
    public static Func<Serial, Character?>? ResolveCharByUid;

    // Static delegate used by script verbs that need to emit a packet
    // directly to the owning client (ADDBUFF / REMOVEBUFF / SYSMESSAGELOC
    // / ARROWQUEST / MIDILIST …). Wired in Program.cs against the
    // connected-clients dictionary. Returns silently when the character
    // has no active client.
    public static Action<Character, SphereNet.Network.Packets.PacketWriter>? SendPacketToOwner;

    /// <summary>Disconnect the character's owning client (DISCONNECT/KICK
    /// verbs from the admin dialogs). Wired in Program.cs against the
    /// network manager. Optional second arg signals "ban" semantics so
    /// the implementation can flag the account.</summary>
    public static Action<Character, bool>? DisconnectClient;

    /// <summary>Resend tooltip cliloc packet to every client viewing the
    /// character. Sphere admin dialog uses ResendTooltip after stat-
    /// flag toggles so the tooltip refresh is immediate, not on next
    /// move tick.</summary>
    public static Action<Character>? ResendTooltipForAll;

    /// <summary>Open the inspect/info dialog on a target character. Used
    /// by the script <c>INFO</c> verb when no UID arg is supplied
    /// (admin function dialog "Info" row).</summary>
    public static Action<Character>? OpenInfoDialog;

    /// <summary>Pop a target cursor for the GM's TELE verb. Source-X
    /// behaviour: GM picks ground/object, server moves the GM there.</summary>
    public static Action<Character>? BeginTeleTarget;

    /// <summary>Drop a 'cage' of stone walls around the character.
    /// Reuses the SUMMONCAGE speech command path.</summary>
    public static Action<Character>? SummonCageAround;

    /// <summary>Begin follow-target on a UID. Sphere admin dialog
    /// player tweak does <c>Src.Follow &lt;UID&gt;</c> to make the GM
    /// auto-walk after a player.</summary>
    public static Action<Character, uint>? FollowUid;

    /// <summary>Resolve client metadata (reported version, client type
    /// flags) for the active session of <paramref name="ch"/>. Returns
    /// (0, ClassicWindows) when the character is offline or no session
    /// info is recorded. Wired in Program.cs.</summary>
    public static Func<Character, (int ReportedCliver, ClientType Type)>? ResolveClientInfo;

    /// <summary>Broadcast a packet to every connected client whose
    /// character is within <paramref name="range"/> tiles of
    /// <paramref name="origin"/> on the same map. Used by script verbs
    /// that need observer-visible side effects (ANIM/SOUND/EFFECT/BOW
    /// /SALUTE/BARK). Wired in Program.cs against the global
    /// BroadcastNearby helper.</summary>
    public static Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby;

    /// <summary>Open this character's paperdoll on the owning client.
    /// Used by the script <c>DCLICK</c> verb when the target is the
    /// character itself (Source-X CChar::OnDoubleClickFromSelf).</summary>
    public static Action<Character>? OpenPaperdollForOwner;

    /// <summary>Open this character's backpack on the owning client.
    /// Used by the script <c>PACK</c> verb (Source-X
    /// CChar::Use_BackpackOpen).</summary>
    public static Action<Character>? OpenBackpackForOwner;

    /// <summary>Open this character's bank box on the owning client.
    /// Used by the script <c>BANK</c> verb and admin shortcut.</summary>
    public static Action<Character>? OpenBankboxForOwner;


    /// <summary>UO client family flags exposed to scripts via
    /// <c>ClientisKr</c> / <c>Is3D</c> / <c>IsEnhanced</c>.</summary>
    public enum ClientType
    {
        ClassicWindows = 0,
        Classic3D = 1,
        KingdomReborn = 2,
        Enhanced = 3,
    }

    public enum MemoryRelationType
    {
        Owner,
        Friend,
        Controller,
    }

    private sealed class ScriptMemoryEntry(Character subject, Character? link, string memoryType) : IScriptObj
    {
        public string GetName() => memoryType;

        public bool TryGetProperty(string key, out string value)
        {
            value = "";
            string upper = key.ToUpperInvariant();
            if (upper == "ISVALID")
            {
                value = link != null ? "1" : "0";
                return true;
            }

            if (upper == "TYPE")
            {
                value = memoryType;
                return true;
            }

            if (upper == "UID")
            {
                value = link != null ? $"0{link.Uid.Value:X8}" : "0";
                return true;
            }

            if (upper == "LINK")
            {
                value = link != null ? $"0{link.Uid.Value:X8}" : "0";
                return true;
            }

            if (upper.StartsWith("LINK.", StringComparison.Ordinal))
            {
                if (link == null)
                {
                    value = "";
                    return true;
                }

                return link.TryGetProperty(key["LINK.".Length..], out value);
            }

            if (upper == "OWNER")
            {
                value = $"0{subject.Uid.Value:X8}";
                return true;
            }

            if (upper.StartsWith("OWNER.", StringComparison.Ordinal))
                return subject.TryGetProperty(key["OWNER.".Length..], out value);

            return false;
        }

        public bool TryExecuteCommand(string key, string args, ITextConsole source) => false;
        public bool TrySetProperty(string key, string value) => false;
        public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args) => TriggerResult.Default;
    }

    private bool _isDeleted;
    private bool _isPlayer;

    // Client-session tags (CTAG.X / CTAG0.X). In Source-X these live on
    // the CClient, which is destroyed on disconnect — so CTag is *not*
    // persistent across login, despite running on a character. We mirror
    // that: CTags hang off the Character but GameClient clears them at
    // logout. Scripts often use CTags to stash per-session UI state
    // (open dialog page, admin list counter, etc.).
    private readonly SphereNet.Scripting.Variables.VarMap _cTags = new();

    /// <summary>Client-session tag map (CTAG.X / CTAG0.X). Cleared when
    /// the owning client disconnects so a new login starts fresh.</summary>
    public SphereNet.Scripting.Variables.VarMap CTags => _cTags;

    // Stats
    private short _str, _dex, _int;
    private short _hits, _mana, _stam;
    private short _maxHits, _maxMana, _maxStam;

    // Skills (Source-X: SKILL_QTY = 99)
    private readonly ushort[] _skillValues = new ushort[(int)SkillType.Qty];
    private readonly byte[] _skillLocks = new byte[(int)SkillType.Qty]; // 0=up, 1=down, 2=locked

    // Equipment and inventory
    // Source-X parity: every LAYER_* slot — including BankBox(29),
    // VendorStock/Buy/Extra and Special — must have a backing slot.
    // Sizing this to Layer.Horse(+1) silently dropped any Equip()
    // call for high-numbered layers (e.g. bank box), which made
    // GetEquippedItem(Layer.BankBox) return null and forced
    // OpenBankBox to spawn a fresh empty box on every "bank" word.
    private readonly Item?[] _equipment = new Item?[(int)Layer.Qty];
    private Item? _backpack;

    private readonly List<Item> _memories = [];

    // NPC fields
    private NpcBrainType _npcBrain;
    private Serial _npcMaster = Serial.Invalid;
    private ushort _npcFood;

    // Runtime EVENTS list (from CHARDEF + dynamically added)
    private readonly List<ResourceId> _events = [];

    // Character state
    private Direction _direction;
    private StatFlag _statFlags;
    private PrivLevel _privLevel;
    private short _fame;
    private short _karma;
    private ushort _bodyId;
    private byte _lightLevel;

    // Original/base stats (before modifiers)
    private short _oStr, _oDex, _oInt;
    private short _modStr, _modDex, _modInt;
    private short _modAr;
    private short _modMaxWeight;

    // Appearance
    private ushort _oBody;
    private ushort _oSkin;

    // Combat extras
    private short _luck;
    private bool _nightSight;
    private short _stepStealth;

    // Experience
    private int _exp;
    private short _level;

    // Player state
    private short _deaths;
    private string _profile = "";
    private uint _pFlag;
    private int _tithing;
    private byte _speedMode;
    // Client viewport dimensions (0xBF sub 0x1C). Defaulted to 800x600
    // until the client reports otherwise.
    private ushort _screenWidth = 800;
    private ushort _screenHeight = 600;
    /// <summary>Update the character's cached viewport size. Called from
    /// the 0xBF sub 0x1C handler.</summary>
    public void SetScreenSize(ushort width, ushort height)
    {
        _screenWidth = width;
        _screenHeight = height;
    }

    // NPC state
    private short _homeDist = 10;
    private Point3D _home;
    private short _actPri;
    private ushort _speechColor = 0x0035;

    // Followers
    private byte _maxFollower = 5;
    private byte _curFollower;

    // Resist caps
    private short _resFireMax = 70;
    private short _resColdMax = 70;
    private short _resPoisonMax = 70;
    private short _resEnergyMax = 70;

    // View
    private byte _visualRange = 18;
    private bool _emoteAct;
    private byte _font = 3;

    // Action context
    private Serial _act = Serial.Invalid;
    private int _actArg1, _actArg2, _actArg3;
    private Point3D _actP;
    private Serial _actPrv = Serial.Invalid;
    private int _actDiff;
    private SkillType _action = (SkillType)(-1);

    // Timing
    private long _createTime;

    // Elemental resists (0-100, percentage)
    private short _resPhysical;
    private short _resFire;
    private short _resCold;
    private short _resPoison;
    private short _resEnergy;

    // Elemental damage percentages (Source-X: DAM_FIRE, DAM_COLD, etc.)
    private short _damPhysical = 100;
    private short _damFire;
    private short _damCold;
    private short _damPoison;
    private short _damEnergy;

    // Poison state
    private byte _poisonLevel; // 0=none, 1=lesser, 2=normal, 3=greater, 4=deadly, 5=lethal
    private long _nextPoisonTick;
    private int _poisonTicksRemaining;

    // Per-attacker damage log (ATTACKER / ATTACKER.LAST / ATTACKER.MAX /
    // ATTACKER.n.DAM / ELAPSED / UID). Source-X: CChar::m_lastAttackers.
    // Entries accumulate while combat is active; cleared by ClearAttackers
    // (death, resurrect, or script). Insertion order is preserved so
    // ATTACKER.LAST reads the most-recent hit.
    public readonly struct AttackerRecord(Serial uid, int totalDamage, long lastHitTick)
    {
        public Serial Uid { get; } = uid;
        public int TotalDamage { get; } = totalDamage;
        public long LastHitTick { get; } = lastHitTick;
    }
    private readonly List<AttackerRecord> _attackers = new();

    // Criminal / murderer state
    private long _criminalTimer;       // TickCount64 when criminal flag expires (0 = not criminal)
    private short _kills;              // murder count
    private long _nextMurderDecayTick; // next TickCount64 at which one kill will decay

    /// <summary>Seconds a character stays criminal (gray) after committing a crime. Set from sphere.ini CRIMINALTIMER.</summary>
    public static int CriminalTimerSeconds { get; set; } = 180;
    /// <summary>Murder count threshold that triggers the murderer (red) flag. sphere.ini MURDERMINCOUNT.</summary>
    public static int MurderMinCount { get; set; } = 5;
    /// <summary>Seconds between automatic murder-count decays (online time). sphere.ini MURDERDECAYTIME.</summary>
    public static int MurderDecayTimeSeconds { get; set; } = 28800;
    /// <summary>Whether attacking an innocent turns the aggressor criminal. sphere.ini ATTACKINGISACRIME.</summary>
    public static bool AttackingIsACrimeEnabled { get; set; } = true;
    /// <summary>Whether helping a criminal fight an innocent flags you criminal. sphere.ini HELPINGCRIMINALSISACRIME.</summary>
    public static bool HelpingCriminalsIsACrimeEnabled { get; set; }
    /// <summary>Whether failed snooping flags the snooper criminal. sphere.ini SNOOPCRIMINAL.</summary>
    public static bool SnoopCriminalEnabled { get; set; } = true;
    /// <summary>Whether spells must consume reagents from backpack. sphere.ini REAGENTSREQUIRED.</summary>
    public static bool ReagentsRequiredEnabled { get; set; } = true;

    // Regen timers (ms)
    private long _nextHitRegen;
    private long _nextManaRegen;
    private long _nextStamRegen;
    private long _nextFoodDecay;
    private ushort _food = 40; // 0-60, hunger level (Sphere style)
    private string _title = ""; // Paperdoll title (Sphere: src.title)
    private bool _allShow; // Runtime-only GM flag, not saved
    private bool _allMove; // Runtime-only GM flag: bypass collision when walking. Not saved.
    private bool _privShow; // Runtime-only: show priv level tag above head
    private bool _isOnline; // Has active client connection
    private int _skillClass = 0;

    public override bool IsDeleted => _isDeleted;
    public bool IsPlayer { get => _isPlayer; set => _isPlayer = value; }

    /// <summary>True when a GameClient is actively controlling this character.</summary>
    public bool IsOnline { get => _isOnline; set => _isOnline = value; }
    public int SkillClass { get => _skillClass; set => _skillClass = Math.Max(0, value); }
    public string Title { get => _title; set => _title = value ?? ""; }

    /// <summary>GM AllShow mode — shows invisible objects with a grey hue. Runtime-only, not persisted.</summary>
    public bool AllShow { get => _allShow; set => _allShow = value; }

    /// <summary>GM AllMove mode — bypass walk collision (walls, statics, mobiles).
    /// Requires PrivLevel.GM+ to be honored. Runtime-only, not persisted.</summary>
    public bool AllMove { get => _allMove; set => _allMove = value; }

    // --- Stats ---
    public short Str
    {
        get => _str;
        set
        {
            short old = _str;
            _str = value;
            if (_maxHits == old || _maxHits <= 0)
            {
                _maxHits = value;
                if (_hits > _maxHits) _hits = _maxHits;
            }
            MarkDirty(DirtyFlag.Stats);
        }
    }
    public short Dex
    {
        get => _dex;
        set
        {
            short old = _dex;
            _dex = value;
            if (_maxStam == old || _maxStam <= 0)
            {
                _maxStam = value;
                if (_stam > _maxStam) _stam = _maxStam;
            }
            MarkDirty(DirtyFlag.Stats);
        }
    }
    public short Int
    {
        get => _int;
        set
        {
            short old = _int;
            _int = value;
            if (_maxMana == old || _maxMana <= 0)
            {
                _maxMana = value;
                if (_mana > _maxMana) _mana = _maxMana;
            }
            MarkDirty(DirtyFlag.Stats);
        }
    }
    public short Hits
    {
        get => _hits;
        set
        {
            short cap = _maxHits > 0 ? _maxHits : (short)Math.Max((int)1, (int)_str);
            var v = Math.Clamp(value, (short)0, cap);
            if (v != _hits) { _hits = v; MarkDirty(DirtyFlag.Stats); }
        }
    }
    public short Mana
    {
        get => _mana;
        set
        {
            short cap = _maxMana > 0 ? _maxMana : (short)Math.Max((int)1, (int)_int);
            var v = Math.Clamp(value, (short)0, cap);
            if (v != _mana) { _mana = v; MarkDirty(DirtyFlag.Stats); }
        }
    }
    public short Stam
    {
        get => _stam;
        set
        {
            short cap = _maxStam > 0 ? _maxStam : (short)Math.Max((int)1, (int)_dex);
            var v = Math.Clamp(value, (short)0, cap);
            if (v != _stam) { _stam = v; MarkDirty(DirtyFlag.Stats); }
        }
    }
    public short MaxHits { get => _maxHits; set { if (value != _maxHits) { _maxHits = value; if (_hits > _maxHits) _hits = _maxHits; MarkDirty(DirtyFlag.Stats); } } }
    public short MaxMana { get => _maxMana; set { if (value != _maxMana) { _maxMana = value; if (_mana > _maxMana) _mana = _maxMana; MarkDirty(DirtyFlag.Stats); } } }
    public short MaxStam { get => _maxStam; set { if (value != _maxStam) { _maxStam = value; if (_stam > _maxStam) _stam = _maxStam; MarkDirty(DirtyFlag.Stats); } } }

    public ushort BodyId { get => _bodyId; set { _bodyId = value; MarkDirty(DirtyFlag.Body); } }

    /// <summary>
    /// Full-width CHARDEF resource index (24-bit, defname hashes can exceed
    /// ushort). Used by trigger and definition lookups
    /// (TriggerDispatcher / SpeechEngine / InfoSkillExtensions). The legacy
    /// <see cref="BaseId"/> property stays at the display body Id and is
    /// truncated to 16 bits — clamping the chardef hash through it
    /// previously resolved c_alchemist's @Create to c_man and c_banker's
    /// to nothing at all (no banker brain). Falls back to BaseId so any
    /// caller that didn't set it explicitly (loaded from save, players,
    /// numeric body NPCs) still works.
    /// </summary>
    public int CharDefIndex
    {
        get => _charDefIndex != 0 ? _charDefIndex : BaseId;
        set => _charDefIndex = value;
    }
    private int _charDefIndex;
    public Direction Direction { get => _direction; set { if (value != _direction) { _direction = value; MarkDirty(DirtyFlag.Direction); } } }
    public StatFlag StatFlags { get => _statFlags; set => _statFlags = value; }
    public PrivLevel PrivLevel
    {
        get
        {
            var acct = ResolveAccountForChar?.Invoke(Uid);
            return acct?.PrivLevel ?? _privLevel;
        }
        set
        {
            _privLevel = value;
            if (_isPlayer)
            {
                var account = ResolveAccountForChar?.Invoke(Uid);
                if (account != null && value > account.PrivLevel)
                    account.PrivLevel = value;
            }
        }
    }

    public short Fame { get => _fame; set => _fame = value; }
    public short Karma { get => _karma; set => _karma = value; }
    public byte LightLevel { get => _lightLevel; set => _lightLevel = value; }

    // Original/base stats
    public short OStr { get => _oStr; set => _oStr = value; }
    public short ODex { get => _oDex; set => _oDex = value; }
    public short OInt { get => _oInt; set => _oInt = value; }
    public short ModStr { get => _modStr; set => _modStr = value; }
    public short ModDex { get => _modDex; set => _modDex = value; }
    public short ModInt { get => _modInt; set => _modInt = value; }
    public short ModAr { get => _modAr; set => _modAr = value; }
    public short ModMaxWeight { get => _modMaxWeight; set => _modMaxWeight = value; }

    // Appearance originals
    public ushort OBody { get => _oBody; set => _oBody = value; }
    public ushort OSkin { get => _oSkin; set => _oSkin = value; }

    // Combat extras
    public short Luck { get => _luck; set => _luck = value; }
    public bool NightSight { get => _nightSight; set => _nightSight = value; }
    public short StepStealth { get => _stepStealth; set => _stepStealth = value; }

    // Experience
    public int Exp { get => _exp; set => _exp = value; }
    public short Level { get => _level; set => _level = value; }

    // Player state
    public short Deaths { get => _deaths; set => _deaths = value; }
    public string Profile { get => _profile; set => _profile = value ?? ""; }
    public uint PFlag { get => _pFlag; set => _pFlag = value; }
    public int Tithing { get => _tithing; set => _tithing = value; }
    public byte SpeedMode { get => _speedMode; set => _speedMode = value; }

    // NPC state
    public short HomeDist { get => _homeDist; set => _homeDist = value; }
    public Point3D Home { get => _home; set => _home = value; }
    public short ActPri { get => _actPri; set => _actPri = value; }
    public ushort SpeechColor { get => _speechColor; set => _speechColor = value; }

    // Followers
    public byte MaxFollower { get => _maxFollower; set => _maxFollower = value; }
    public byte CurFollower
    {
        get
        {
            var world = ResolveWorld?.Invoke();
            if (world == null)
                return _curFollower;

            int count = 0;
            foreach (var obj in world.GetAllObjects())
            {
                if (obj is not Character creature || creature == this || creature.IsDeleted)
                    continue;
                if (!creature.HasOwner(this.Uid))
                    continue;
                if (creature.IsStatFlag(StatFlag.Ridden))
                    continue;
                count++;
            }

            _curFollower = (byte)Math.Clamp(count, 0, byte.MaxValue);
            return _curFollower;
        }
        set => _curFollower = value;
    }

    // Resist caps
    public short ResFireMax { get => _resFireMax; set => _resFireMax = value; }
    public short ResColdMax { get => _resColdMax; set => _resColdMax = value; }
    public short ResPoisonMax { get => _resPoisonMax; set => _resPoisonMax = value; }
    public short ResEnergyMax { get => _resEnergyMax; set => _resEnergyMax = value; }

    // View
    public byte VisualRange { get => _visualRange; set => _visualRange = value; }
    public bool EmoteAct { get => _emoteAct; set => _emoteAct = value; }
    public byte Font { get => _font; set => _font = value; }

    // Action context
    public Serial Act { get => _act; set => _act = value; }
    public int ActArg1 { get => _actArg1; set => _actArg1 = value; }
    public int ActArg2 { get => _actArg2; set => _actArg2 = value; }
    public int ActArg3 { get => _actArg3; set => _actArg3 = value; }
    public Point3D ActP { get => _actP; set => _actP = value; }
    public Serial ActPrv { get => _actPrv; set => _actPrv = value; }
    public int ActDiff { get => _actDiff; set => _actDiff = value; }
    public SkillType Action { get => _action; set => _action = value; }

    // Timing
    public long CreateTime { get => _createTime; set => _createTime = value; }

    // Elemental resists (0-100%)
    public short ResPhysical { get => _resPhysical; set => _resPhysical = value; }
    public short ResFire { get => _resFire; set => _resFire = value; }
    public short ResCold { get => _resCold; set => _resCold = value; }
    public short ResPoison { get => _resPoison; set => _resPoison = value; }
    public short ResEnergy { get => _resEnergy; set => _resEnergy = value; }

    // Elemental damage percentages (total should be 100%)
    public short DamPhysical { get => _damPhysical; set => _damPhysical = value; }
    public short DamFire { get => _damFire; set => _damFire = value; }
    public short DamCold { get => _damCold; set => _damCold = value; }
    public short DamPoison { get => _damPoison; set => _damPoison = value; }
    public short DamEnergy { get => _damEnergy; set => _damEnergy = value; }

    // Poison
    public byte PoisonLevel { get => _poisonLevel; set => _poisonLevel = value; }
    public bool IsPoisoned => _poisonLevel > 0;
    public short Kills { get => _kills; set => _kills = value; }
    public bool IsCriminal => _criminalTimer > 0 && Environment.TickCount64 < _criminalTimer;
    public bool IsMurderer => _kills >= MurderMinCount;

    /// <summary>Mark this character criminal (gray) and arm the decay timer. Called
    /// by HandleAttack, snooping, theft, etc. Overwrites any existing timer (i.e.
    /// a fresh crime refreshes the countdown, matching Source-X behaviour).</summary>
    public void MakeCriminal()
    {
        SetStatFlag(StatFlag.Criminal);
        _criminalTimer = Environment.TickCount64 + CriminalTimerSeconds * 1000L;
    }

    /// <summary>Called once per world tick. Clears expired criminal flag and
    /// decays one kill every MurderDecayTimeSeconds of online time.</summary>
    public void TickNotorietyDecay(long nowMs)
    {
        if (_criminalTimer > 0 && nowMs >= _criminalTimer)
        {
            _criminalTimer = 0;
            if (IsStatFlag(StatFlag.Criminal))
                ClearStatFlag(StatFlag.Criminal);
        }

        if (_kills > 0 && MurderDecayTimeSeconds > 0)
        {
            if (_nextMurderDecayTick == 0)
                _nextMurderDecayTick = nowMs + MurderDecayTimeSeconds * 1000L;
            else if (nowMs >= _nextMurderDecayTick)
            {
                _kills--;
                _nextMurderDecayTick = nowMs + MurderDecayTimeSeconds * 1000L;
            }
        }
        else
        {
            _nextMurderDecayTick = 0;
        }
    }

    /// <summary>Apply poison to this character. Level: 1=lesser, 2=normal, 3=greater, 4=deadly, 5=lethal.</summary>
    public void ApplyPoison(byte level)
    {
        _poisonLevel = Math.Max(_poisonLevel, level);
        _poisonTicksRemaining = level switch
        {
            1 => 5, 2 => 8, 3 => 12, 4 => 16, _ => 20
        };
        _nextPoisonTick = Environment.TickCount64 + GetPoisonTickInterval();
        StatFlags |= StatFlag.Poisoned;
    }

    /// <summary>Cure poison.</summary>
    public void CurePoison()
    {
        _poisonLevel = 0;
        _poisonTicksRemaining = 0;
        _nextPoisonTick = 0;
        StatFlags &= ~StatFlag.Poisoned;
    }

    /// <summary>Set criminal timer (duration in ms).</summary>
    public void SetCriminal(long durationMs = 120_000)
    {
        _criminalTimer = Environment.TickCount64 + durationMs;
    }

    private int GetPoisonTickInterval() => _poisonLevel switch
    {
        1 => 4000, 2 => 3000, 3 => 2500, 4 => 2000, _ => 1500
    };

    private int GetPoisonDamage() => _poisonLevel switch
    {
        1 => Random.Shared.Next(2, 5),
        2 => Random.Shared.Next(3, 8),
        3 => Random.Shared.Next(5, 12),
        4 => Random.Shared.Next(8, 20),
        _ => Random.Shared.Next(12, 30)
    };

    /// <summary>Process poison tick. Returns damage dealt, 0 if no tick.</summary>
    public int ProcessPoisonTick(long now)
    {
        if (_poisonLevel == 0 || _poisonTicksRemaining <= 0) return 0;
        if (now < _nextPoisonTick) return 0;

        _nextPoisonTick = now + GetPoisonTickInterval();
        _poisonTicksRemaining--;

        int damage = GetPoisonDamage();
        // Apply poison resist
        int resistPct = Math.Clamp(_resPoison, (short)0, (short)80);
        damage = damage * (100 - resistPct) / 100;
        damage = Math.Max(1, damage);

        Hits = (short)Math.Max(0, Hits - damage);

        if (_poisonTicksRemaining <= 0)
            CurePoison();

        return damage;
    }

    // Hunger
    public ushort Food { get => _food; set => _food = Math.Min(value, (ushort)60); }
    public bool IsHungry => _food <= 0;

    // NPC
    public NpcBrainType NpcBrain { get => _npcBrain; set => _npcBrain = value; }
    public Serial NpcMaster
    {
        get
        {
            if (_npcMaster.IsValid)
                return _npcMaster;
            return ParseSerialTag("OWNER_UID", Serial.Invalid);
        }
        set => SetOwnerControllerRaw(value, value, mirrorLegacySummon: false);
    }
    public ushort NpcFood { get => _npcFood; set => _npcFood = value; }

    /// <summary>Pet AI mode — controls pet behavior when owned by a player.</summary>
    public PetAIMode PetAIMode { get; set; } = PetAIMode.Follow;

    /// <summary>Runtime EVENTS list. Populated from CHARDEF Events + dynamically added at runtime.</summary>
    public List<ResourceId> Events => _events;

    // Combat fields
    public Serial FightTarget { get; set; } = Serial.Invalid;
    public long NextAttackTime { get; set; }
    public long NextNpcActionTime { get; set; }

    // NPC spell list — populated from CHARDEF or spellbook items
    private List<SpellType>? _npcSpells;
    public IReadOnlyList<SpellType> NpcSpells => _npcSpells ?? (IReadOnlyList<SpellType>)Array.Empty<SpellType>();
    public void NpcSpellAdd(SpellType spell)
    {
        _npcSpells ??= [];
        if (!_npcSpells.Contains(spell))
            _npcSpells.Add(spell);
    }

    // NPC flee state
    public int FleeStepsCurrent { get; set; }
    public int FleeStepsMax { get; set; }

    // --- Stat Flags ---
    public bool IsStatFlag(StatFlag flag) => (_statFlags & flag) != 0;
    public void SetStatFlag(StatFlag flag) { _statFlags |= flag; MarkDirty(DirtyFlag.StatFlags); }
    public void ClearStatFlag(StatFlag flag) { _statFlags &= ~flag; MarkDirty(DirtyFlag.StatFlags); }

    public bool IsDead => IsStatFlag(StatFlag.Dead);
    public bool IsInWarMode => IsStatFlag(StatFlag.War);
    public bool IsInvisible => IsStatFlag(StatFlag.Invisible);
    public bool IsMounted => IsStatFlag(StatFlag.OnHorse);

    public bool IsSummoned =>
        TryGetTag("SUMMON_MASTER", out string? sm) && ParseSerial(sm).IsValid ||
        TryGetTag("SUMMON_DURATION", out _) ||
        TryGetTag("SUMMON_EXPIRE_TICK", out _);

    public Serial OwnerSerial => ParseSerialTag("OWNER_UID", fallback: _npcMaster);

    public Serial ControllerSerial
    {
        get
        {
            Serial controller = ParseSerialTag("CONTROLLER_UID", Serial.Invalid);
            return controller.IsValid ? controller : OwnerSerial;
        }
    }

    public Character? ResolveOwnerCharacter()
    {
        var world = ResolveWorld?.Invoke();
        return world?.FindChar(OwnerSerial);
    }

    public Character? ResolveControllerCharacter()
    {
        var world = ResolveWorld?.Invoke();
        return world?.FindChar(ControllerSerial);
    }

    public bool HasOwner(Serial ownerUid) => ownerUid.IsValid && OwnerSerial == ownerUid;

    public bool HasController(Serial controllerUid) => controllerUid.IsValid && ControllerSerial == controllerUid;

    public bool IsFriendOf(Serial charUid)
    {
        if (!charUid.IsValid)
            return false;
        return TryGetTag($"FRIEND_{charUid.Value}", out string? raw) &&
            !string.IsNullOrWhiteSpace(raw) &&
            !raw.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    public bool CanAcceptPetCommandFrom(Character? issuer, bool allowFriends = true)
    {
        if (issuer == null || issuer.IsDead)
            return false;
        if (issuer.PrivLevel >= Core.Enums.PrivLevel.GM)
            return true;
        if (HasController(issuer.Uid) || HasOwner(issuer.Uid))
            return true;
        if (allowFriends && IsFriendOf(issuer.Uid))
            return true;
        return false;
    }

    public bool TryAssignOwnership(Character? owner, Character? controller = null,
        bool summoned = false, bool enforceFollowerCap = false)
    {
        Serial ownerUid = owner?.Uid ?? Serial.Invalid;
        Serial controllerUid = controller?.Uid ?? ownerUid;

        if (enforceFollowerCap && owner != null && !HasOwner(owner.Uid) &&
            owner.CurFollower >= owner.MaxFollower)
        {
            return false;
        }

        SetOwnerControllerRaw(ownerUid, controllerUid, mirrorLegacySummon: summoned);
        if (owner != null)
            SetTag("OWNER_UUID", owner.Uuid.ToString("D"));
        if (controller != null)
            SetTag("CONTROLLER_UUID", controller.Uuid.ToString("D"));
        if (summoned && owner != null)
            SetTag("SUMMON_MASTER_UUID", owner.Uuid.ToString("D"));
        if (ownerUid.IsValid)
        {
            SetStatFlag(StatFlag.Pet);
            if (_npcFood == 0)
                _npcFood = 50;
        }
        else
        {
            ClearStatFlag(StatFlag.Pet);
        }

        var world = ResolveWorld?.Invoke();
        if (world != null)
        {
            owner?.CurFollower.ToString(); // forces lazy recalc cache
            controller?.CurFollower.ToString();
        }

        return true;
    }

    public void ClearOwnership(bool clearFriends = false)
    {
        SetOwnerControllerRaw(Serial.Invalid, Serial.Invalid, mirrorLegacySummon: false);
        if (clearFriends)
        {
            var toRemove = new List<string>();
            foreach (var kvp in Tags.GetAll())
            {
                if (kvp.Key.StartsWith("FRIEND_", StringComparison.OrdinalIgnoreCase))
                    toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
                RemoveTag(key);
        }

        ClearStatFlag(StatFlag.Pet);
    }

    public void NormalizePlayerSkillClass()
    {
        if (!_isPlayer)
            return;

        if (_skillClass < 0)
        {
            _skillClass = 0;
            return;
        }

        if (_skillClass != 0 && DefinitionLoader.GetSkillClassDef(_skillClass) == null)
            _skillClass = 0;
    }

    public bool AddFriend(Character friend)
    {
        if (friend == null || !friend.Uid.IsValid)
            return false;
        SetTag($"FRIEND_{friend.Uid.Value}", "1");
        Memory_AddObjTypes(friend.Uid, MemoryType.Friend);
        return true;
    }

    public bool RemoveFriend(Character friend)
    {
        if (friend == null || !friend.Uid.IsValid)
            return false;
        var mem = Memory_FindObjTypes(friend.Uid, MemoryType.Friend);
        if (mem != null) Memory_ClearTypes(mem, MemoryType.Friend);
        return Tags.Remove($"FRIEND_{friend.Uid.Value}");
    }

    public IReadOnlyList<Item> Memories => _memories;

    public Item? Memory_FindObj(Serial uid)
    {
        for (int i = 0; i < _memories.Count; i++)
        {
            var m = _memories[i];
            if (m.ItemType == ItemType.EqMemoryObj && m.Link == uid)
                return m;
        }
        return null;
    }

    public Item? Memory_FindTypes(MemoryType flags)
    {
        if (flags == MemoryType.None) return null;
        for (int i = 0; i < _memories.Count; i++)
        {
            if (_memories[i].IsMemoryTypes(flags))
                return _memories[i];
        }
        return null;
    }

    public Item? Memory_FindObjTypes(Serial uid, MemoryType flags)
    {
        var mem = Memory_FindObj(uid);
        if (mem == null) return null;
        return mem.IsMemoryTypes(flags) ? mem : null;
    }

    public Item Memory_CreateObj(Serial uid, MemoryType flags)
    {
        var mem = new Item
        {
            ItemType = ItemType.EqMemoryObj,
            BaseId = 0x1F14,
            Name = "Memory",
        };
        mem.Link = uid;
        mem.SetAttr(ObjAttributes.Newbie);
        mem.IsEquipped = true;
        mem.EquipLayer = Layer.Special;
        mem.ContainedIn = Uid;

        mem.SetMemoryTypes(flags);
        mem.MoreP = Position;
        Memory_UpdateFlags(mem);

        _memories.Add(mem);
        return mem;
    }

    public Item Memory_AddObjTypes(Serial uid, MemoryType flags)
    {
        var mem = Memory_FindObj(uid);
        if (mem == null)
            return Memory_CreateObj(uid, flags);
        Memory_AddTypes(mem, flags);
        return mem;
    }

    public void Memory_AddTypes(Item mem, MemoryType flags)
    {
        mem.SetMemoryTypes(mem.GetMemoryTypes() | flags);
        mem.MoreP = Position;
        Memory_UpdateFlags(mem);
    }

    public bool Memory_UpdateClearTypes(Item mem, MemoryType flags)
    {
        var prev = mem.GetMemoryTypes();
        var remaining = prev & ~flags;
        mem.SetMemoryTypes(remaining);

        if ((flags & MemoryType.IPet) != 0 && (prev & MemoryType.IPet) != 0)
        {
            if (Memory_FindTypes(MemoryType.IPet) == null)
                ClearStatFlag(StatFlag.Pet);
        }

        if (remaining == MemoryType.None)
            return false;

        return Memory_UpdateFlags(mem);
    }

    public bool Memory_ClearTypes(Item mem, MemoryType flags)
    {
        if (Memory_UpdateClearTypes(mem, flags))
            return true;
        Memory_Delete(mem);
        return false;
    }

    public void Memory_ClearAllTypes(MemoryType flags)
    {
        for (int i = _memories.Count - 1; i >= 0; i--)
        {
            var m = _memories[i];
            if (!m.IsMemoryTypes(flags)) continue;
            Memory_ClearTypes(m, flags);
        }
    }

    public void Memory_Delete(Item mem)
    {
        _memories.Remove(mem);
    }

    public bool Memory_UpdateFlags(Item mem)
    {
        var flags = mem.GetMemoryTypes();
        if (flags == MemoryType.None) return false;

        long timeout;
        if ((flags & MemoryType.IPet) != 0)
            SetStatFlag(StatFlag.Pet);

        if ((flags & MemoryType.Fight) != 0)
            timeout = 30_000;
        else if ((flags & (MemoryType.IPet | MemoryType.Guard | MemoryType.Guild | MemoryType.Town)) != 0)
            timeout = -1;
        else if (!IsPlayer)
            timeout = 5 * 60_000;
        else
            timeout = 20 * 60_000;

        mem.SetTimeout(timeout);
        return true;
    }

    public bool Memory_OnTick(Item mem)
    {
        if (mem.Link == Serial.Invalid)
            return false;

        if (mem.IsMemoryTypes(MemoryType.Fight))
            return Memory_Fight_OnTick(mem);

        if (mem.IsMemoryTypes(MemoryType.IPet | MemoryType.Guard | MemoryType.Guild | MemoryType.Town))
            return true;

        return false;
    }

    public void Memory_Fight_Start(Character target)
    {
        var mem = Memory_FindObj(target.Uid);
        MemoryType aggFlags;

        if (mem == null)
        {
            var targMem = target.Memory_FindObj(Uid);
            if (targMem != null)
            {
                if (targMem.IsMemoryTypes(MemoryType.IAggressor))
                    aggFlags = MemoryType.HarmedBy;
                else if (targMem.IsMemoryTypes(MemoryType.HarmedBy | MemoryType.SawCrime | MemoryType.Aggreived))
                    aggFlags = MemoryType.IAggressor;
                else
                    aggFlags = MemoryType.None;
            }
            else
            {
                aggFlags = MemoryType.IAggressor;
            }
            Memory_CreateObj(target.Uid, MemoryType.Fight | aggFlags);
            return;
        }

        if (mem.IsMemoryTypes(MemoryType.HarmedBy | MemoryType.SawCrime | MemoryType.Aggreived))
            aggFlags = MemoryType.None;
        else
            aggFlags = MemoryType.IAggressor;

        Memory_AddTypes(mem, MemoryType.Fight | aggFlags);
    }

    private bool Memory_Fight_OnTick(Item mem)
    {
        var world = ResolveWorld?.Invoke();
        if (world == null) return false;

        var target = world.FindChar(mem.Link);
        if (target == null || target.IsDeleted || target.IsStatFlag(StatFlag.Dead))
        {
            Memory_ClearTypes(mem, MemoryType.Fight | MemoryType.IAggressor | MemoryType.Aggreived);
            return true;
        }

        int dist = Position.GetDistanceTo(target.Position);
        if (dist > 32)
        {
            Memory_ClearTypes(mem, MemoryType.Fight | MemoryType.IAggressor | MemoryType.Aggreived);
            return true;
        }

        mem.SetTimeout(30_000);
        return true;
    }

    public IReadOnlyList<IScriptObj> GetMemoryEntriesByType(string rawType)
    {
        string normalized = NormalizeMemoryType(rawType);
        var result = new List<IScriptObj>();

        MemoryType? filter = normalized switch
        {
            "MEMORY_IPET" or "MEMORY_OWNER" => MemoryType.IPet,
            "MEMORY_FRIEND" => MemoryType.Friend,
            "MEMORY_FIGHT" => MemoryType.Fight,
            "MEMORY_GUARD" => MemoryType.Guard,
            "MEMORY_GUILD" => MemoryType.Guild,
            "MEMORY_TOWN" => MemoryType.Town,
            "MEMORY_SAWCRIME" => MemoryType.SawCrime,
            "MEMORY_IAGGRESSOR" => MemoryType.IAggressor,
            "MEMORY_HARMEDBY" => MemoryType.HarmedBy,
            "MEMORY_AGGREIVED" => MemoryType.Aggreived,
            "MEMORY_SPEAK" => MemoryType.Speak,
            _ => null
        };

        if (filter.HasValue)
        {
            for (int i = 0; i < _memories.Count; i++)
            {
                if (_memories[i].IsMemoryTypes(filter.Value))
                    result.Add(_memories[i]);
            }
        }

        return result;
    }

    public IScriptObj? FindMemoryEntry(string rawType)
    {
        var list = GetMemoryEntriesByType(rawType);
        return list.Count > 0 ? list[0] : null;
    }

    public bool TickPetOwnershipTimers(long nowMs)
    {
        if (!OwnerSerial.IsValid && !IsSummoned)
            return false;

        if (TryGetTag("SUMMON_EXPIRE_TICK", out string? expireRaw) &&
            long.TryParse(expireRaw, out long expireTick) &&
            expireTick > 0 && nowMs >= expireTick)
        {
            return true;
        }

        if (IsSummoned)
            return false;

        long nextTick = Tags.GetInt("PET_NEXT_LOYALTY_TICK", 0);
        if (nextTick == 0)
        {
            SetTag("PET_NEXT_LOYALTY_TICK", (nowMs + 60_000).ToString());
            return false;
        }

        if (nowMs < nextTick)
            return false;

        SetTag("PET_NEXT_LOYALTY_TICK", (nowMs + 60_000).ToString());
        if (_npcFood > 0)
            _npcFood--;

        if (_npcFood == 0)
        {
            ClearOwnership(clearFriends: false);
            PetAIMode = PetAIMode.Stay;
            return false;
        }

        return false;
    }

    private static Serial ParseSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Serial.Invalid;

        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(raw.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint from0x))
        {
            return new Serial(from0x);
        }

        if (raw.StartsWith('0') && raw.Length > 1 && !raw.Contains(',') &&
            uint.TryParse(raw.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint fromSphere))
        {
            return new Serial(fromSphere);
        }

        return uint.TryParse(raw, out uint dec) ? new Serial(dec) : Serial.Invalid;
    }

    private Serial ParseSerialTag(string key, Serial fallback = default)
    {
        return TryGetTag(key, out string? raw) ? ParseSerial(raw) : fallback;
    }

    private static string NormalizeMemoryType(string rawType)
    {
        string upper = rawType.Trim().ToUpperInvariant();
        return upper switch
        {
            "IPET" or "PET" or "OWNER" or "MEMORY_IPET" or "MEMORY_OWNER" => "MEMORY_IPET",
            "CONTROLLER" or "MEMORY_CONTROLLER" => "MEMORY_CONTROLLER",
            "FRIEND" or "MEMORY_FRIEND" => "MEMORY_FRIEND",
            _ => upper.StartsWith("MEMORY_", StringComparison.Ordinal) ? upper : "MEMORY_" + upper,
        };
    }

    private void SetOwnerControllerRaw(Serial ownerUid, Serial controllerUid, bool mirrorLegacySummon)
    {
        _npcMaster = ownerUid;

        if (ownerUid.IsValid)
        {
            SetTag("OWNER_UID", ownerUid.Value.ToString());
            SetTag("MEMORYLINK.MEMORY_IPET", ownerUid.Value.ToString());
            Memory_AddObjTypes(ownerUid, MemoryType.IPet);
        }
        else
        {
            RemoveTag("OWNER_UID");
            RemoveTag("OWNER_UUID");
            RemoveTag("MEMORYLINK.MEMORY_IPET");
            Memory_ClearAllTypes(MemoryType.IPet);
        }

        if (controllerUid.IsValid)
        {
            SetTag("CONTROLLER_UID", controllerUid.Value.ToString());
            SetTag("MEMORYLINK.MEMORY_CONTROLLER", controllerUid.Value.ToString());
        }
        else
        {
            RemoveTag("CONTROLLER_UID");
            RemoveTag("CONTROLLER_UUID");
            RemoveTag("MEMORYLINK.MEMORY_CONTROLLER");
        }

        if (mirrorLegacySummon && ownerUid.IsValid)
        {
            SetTag("SUMMON_MASTER", ownerUid.Value.ToString());
        }
        else if (!mirrorLegacySummon)
        {
            RemoveTag("SUMMON_MASTER");
            RemoveTag("SUMMON_MASTER_UUID");
        }
    }

    // --- Skills ---
    public ushort GetSkill(SkillType skill) =>
        (int)skill < _skillValues.Length ? _skillValues[(int)skill] : (ushort)0;

    public void SetSkill(SkillType skill, ushort value)
    {
        if ((int)skill < _skillValues.Length) _skillValues[(int)skill] = value;
    }

    public byte GetSkillLock(SkillType skill) =>
        (int)skill < _skillLocks.Length ? _skillLocks[(int)skill] : (byte)2;

    public void SetSkillLock(SkillType skill, byte lockState)
    {
        if ((int)skill < _skillLocks.Length) _skillLocks[(int)skill] = lockState;
    }

    // --- Equipment ---
    public Item? GetEquippedItem(Layer layer)
    {
        int idx = (int)layer;
        return idx >= 0 && idx < _equipment.Length ? _equipment[idx] : null;
    }

    public bool Equip(Item item, Layer layer)
    {
        int idx = (int)layer;
        if (idx < 0 || idx >= _equipment.Length) return false;

        if (_equipment[idx] != null)
            Unequip(layer);

        _equipment[idx] = item;
        item.IsEquipped = true;
        item.EquipLayer = layer;
        item.ContainedIn = Uid;
        MarkDirty(DirtyFlag.Equip);
        return true;
    }

    public Item? Unequip(Layer layer)
    {
        int idx = (int)layer;
        if (idx < 0 || idx >= _equipment.Length) return null;

        var item = _equipment[idx];
        if (item == null) return null;

        _equipment[idx] = null;
        item.IsEquipped = false;
        item.ContainedIn = Serial.Invalid;
        MarkDirty(DirtyFlag.Equip);
        return item;
    }

    public Item? Backpack
    {
        get => _backpack ?? GetEquippedItem(Layer.Pack);
        set => _backpack = value;
    }

    // --- Movement ---
    public bool CanMove => !IsDead && _stam > 0;

    /// <summary>Teleport this character to <paramref name="target"/>.
    /// Routes through GameWorld.MoveCharacter so the character actually
    /// moves between sectors — a plain <c>Position = ...</c> would leave
    /// it registered in its old sector and invisible to BroadcastNearby.</summary>
    public void MoveTo(Point3D target)
    {
        var world = ResolveWorld?.Invoke();
        if (world != null)
            world.MoveCharacter(this, target);
        else
            Position = target;
    }

    // --- Attacker log ---
    /// <summary>Read-only view of the current attacker log. Most recent hit
    /// is at the end of the list (ATTACKER.LAST).</summary>
    public IReadOnlyList<AttackerRecord> Attackers => _attackers;

    /// <summary>Add <paramref name="damage"/> to the running total for
    /// <paramref name="attackerUid"/> and stamp the current tick. Called
    /// from combat / spell damage paths. No-op for self-damage.</summary>
    public void RecordAttack(Serial attackerUid, int damage)
    {
        if (attackerUid == Uid || attackerUid == Serial.Invalid || damage <= 0)
            return;
        long now = Environment.TickCount64;
        for (int i = 0; i < _attackers.Count; i++)
        {
            if (_attackers[i].Uid == attackerUid)
            {
                _attackers[i] = new AttackerRecord(attackerUid, _attackers[i].TotalDamage + damage, now);
                // Move this entry to the end so ATTACKER.LAST reflects it
                if (i != _attackers.Count - 1)
                {
                    var rec = _attackers[i];
                    _attackers.RemoveAt(i);
                    _attackers.Add(rec);
                }
                return;
            }
        }
        _attackers.Add(new AttackerRecord(attackerUid, damage, now));
    }

    public void ClearAttackers() => _attackers.Clear();

    // --- Death ---
    public void Kill()
    {
        _hits = 0;
        SetStatFlag(StatFlag.Dead);
        MarkDirty(DirtyFlag.StatFlags | DirtyFlag.Stats);
    }

    public void Resurrect()
    {
        ClearStatFlag(StatFlag.Dead);
        _hits = (short)(_maxHits / 2);
        _attackers.Clear();

        // Source-X: murderer resurrection penalties (stat/skill loss ~1%)
        if (IsMurderer && IsPlayer)
        {
            _str = (short)Math.Max(1, _str - Math.Max((short)1, (short)(_str / 100)));
            _dex = (short)Math.Max(1, _dex - Math.Max((short)1, (short)(_dex / 100)));
            _int = (short)Math.Max(1, _int - Math.Max((short)1, (short)(_int / 100)));
            for (int i = 0; i < _skillValues.Length; i++)
            {
                if (_skillValues[i] > 0)
                    _skillValues[i] = (ushort)Math.Max(0, _skillValues[i] - Math.Max(1, _skillValues[i] / 100));
            }
        }
    }

    public void Delete()
    {
        _isDeleted = true;
    }

    // --- IScriptObj overrides ---
    public override bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();

        // <MemoryFindType.<def>[.isValid|.Link[.<prop>]]> — Source-X
        // memory inspection. We only have a partial memory engine so we
        // synthesize the two cases the admin dialogs care about:
        //   memory_guild → resolve the player's guild from GuildManager
        // and pretend the guild stone is the linked object. Everything
        // else returns "0"/empty so the dialog renders the "no value"
        // path instead of a literal "<MemoryFindType...>".
        if (upper.StartsWith("MEMORYFINDTYPE.", StringComparison.Ordinal))
        {
            string rest = upper["MEMORYFINDTYPE.".Length..];
            int dot1 = rest.IndexOf('.');
            string memType = dot1 < 0 ? rest : rest[..dot1];
            string sub = dot1 < 0 ? "" : rest[(dot1 + 1)..];

            if (memType.Equals("MEMORY_GUILD", StringComparison.OrdinalIgnoreCase))
            {
                var gm = ResolveGuildManager?.Invoke(Uid);
                var guild = gm?.FindGuildFor(Uid);
                if (guild == null)
                {
                    if (sub.Equals("ISVALID", StringComparison.OrdinalIgnoreCase))
                    { value = "0"; return true; }
                    value = "0";
                    return true;
                }

                if (sub.Length == 0)
                {
                    value = $"0{guild.StoneUid.Value:X8}";
                    return true;
                }
                if (sub.Equals("ISVALID", StringComparison.OrdinalIgnoreCase))
                { value = "1"; return true; }
                if (sub.StartsWith("LINK", StringComparison.OrdinalIgnoreCase))
                {
                    string linkSub = sub.Length > 4 && sub[4] == '.' ? sub[5..] : "";
                    if (linkSub.Length == 0)
                    {
                        value = $"0{guild.StoneUid.Value:X8}";
                        return true;
                    }
                    if (linkSub.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                    { value = guild.Name ?? ""; return true; }
                    if (linkSub.Equals("P", StringComparison.OrdinalIgnoreCase))
                    {
                        // Guild stone position not modelled; fall back
                        // to the master's location so "Go <...Link.P>"
                        // still goes somewhere sensible (admin dialog
                        // row 688).
                        var masterMember = guild.GetMaster();
                        var master = masterMember != null
                            ? ResolveCharByUid?.Invoke(masterMember.CharUid) : null;
                        value = master != null ? master.Position.ToString() : Position.ToString();
                        return true;
                    }
                }
            }

            var memory = FindMemoryEntry(memType);
            if (memory != null)
            {
                if (sub.Length == 0)
                    return memory.TryGetProperty("LINK", out value);
                if (sub.Equals("ISVALID", StringComparison.OrdinalIgnoreCase))
                { value = "1"; return true; }
                return memory.TryGetProperty(sub, out value);
            }

            // Unknown memory or sub: return false-y so callers using
            // `<isEmpty ...>` or `If <...isValid>` take the empty branch.
            if (sub.Equals("ISVALID", StringComparison.OrdinalIgnoreCase))
            { value = "0"; return true; }
            value = "";
            return true;
        }

        // <House.N> / <Ship.N> indexed UID accessor — stubbed to 0
        // until the housing engine ships, mirroring the count stub
        // above so the dialog never iterates past the empty case.
        if (upper.StartsWith("HOUSE.", StringComparison.Ordinal) ||
            upper.StartsWith("SHIP.", StringComparison.Ordinal))
        {
            value = "0";
            return true;
        }

        // <FindLayer(N)> → UID of item equipped on layer N, or 0.
        // <FindLayer(N).property> → property on that item.
        if (upper.StartsWith("FINDLAYER(", StringComparison.Ordinal))
        {
            int closeParen = upper.IndexOf(')');
            if (closeParen > 10)
            {
                string layerStr = upper.Substring(10, closeParen - 10);
                if (int.TryParse(layerStr, out int layerNum))
                {
                    var worn = GetEquippedItem((Layer)layerNum);
                    if (worn == null) { value = "0"; return true; }
                    string tail = upper[(closeParen + 1)..];
                    if (string.IsNullOrEmpty(tail))
                    {
                        value = $"0{worn.Uid.Value:X8}";
                        return true;
                    }
                    if (tail.StartsWith(".", StringComparison.Ordinal))
                        return worn.TryGetProperty(tail[1..], out value);
                }
            }
        }

        // <Account> alone → account name. <Account.X> → delegate to Account
        // object. <Account.Char.N.Subkey> → follow through to the nth slot
        // character and resolve Subkey on it (admin dialog uses this for
        // per-slot name/uid display).
        if (upper == "ACCOUNT")
        {
            var acc = ResolveAccountForChar?.Invoke(Uid);
            value = acc?.Name ?? (TryGetTag("ACCOUNT", out string? tagName) ? (tagName ?? "") : "");
            return true;
        }
        if (upper.StartsWith("ACCOUNT.", StringComparison.Ordinal))
        {
            var acc = ResolveAccountForChar?.Invoke(Uid);
            if (acc == null)
            {
                value = "";
                return true;
            }

            string subKey = upper[8..];
            if (subKey.StartsWith("CHAR.", StringComparison.Ordinal))
            {
                string rest = subKey[5..];
                int dot = rest.IndexOf('.');
                string slotStr = dot < 0 ? rest : rest[..dot];
                if (int.TryParse(slotStr, out int slotIdx))
                {
                    var slotUid = acc.GetCharSlot(slotIdx);
                    if (!slotUid.IsValid) { value = "0"; return true; }
                    if (dot < 0) { value = $"0{slotUid.Value:X8}"; return true; }
                    string charSub = rest[(dot + 1)..];
                    if (charSub == "UID") { value = $"0{slotUid.Value:X8}"; return true; }
                    var otherChar = ResolveCharByUid?.Invoke(slotUid);
                    if (otherChar != null)
                        return otherChar.TryGetProperty(charSub, out value);
                    value = "0";
                    return true;
                }
            }

            return acc.TryGetProperty(subKey, out value);
        }

        switch (upper)
        {
            case "STR": value = _str.ToString(); return true;
            case "DEX": value = _dex.ToString(); return true;
            case "INT": value = _int.ToString(); return true;
            case "HITS": value = _hits.ToString(); return true;
            case "MANA": value = _mana.ToString(); return true;
            case "STAM": value = _stam.ToString(); return true;
            case "MAXHITS": value = _maxHits.ToString(); return true;
            case "MAXMANA": value = _maxMana.ToString(); return true;
            case "MAXSTAM": value = _maxStam.ToString(); return true;
            case "BODY": value = $"0{_bodyId:X}"; return true;
            case "DIR": value = ((byte)_direction).ToString(); return true;
            case "FLAGS": value = ((uint)_statFlags).ToString(); return true;
            case "FAME": value = _fame.ToString(); return true;
            case "KARMA": value = _karma.ToString(); return true;
            case "ISGM": value = (PrivLevel >= PrivLevel.GM) ? "1" : "0"; return true;
            case "GM": value = (PrivLevel >= PrivLevel.GM) ? "1" : "0"; return true;
            case "INVUL": value = IsStatFlag(StatFlag.Invul) ? "1" : "0"; return true;
            case "ALLSHOW": value = _allShow ? "1" : "0"; return true;
            case "PRIVSHOW": value = _privShow ? "1" : "0"; return true;
            case "ISPLAYER": value = _isPlayer ? "1" : "0"; return true;
            case "ISNPC": value = (!_isPlayer && _npcBrain != NpcBrainType.None) ? "1" : "0"; return true;
            case "NPCBRAIN": value = ((int)_npcBrain).ToString(); return true;
            case "DAM":
            {
                var dam = Combat.CombatEngine.NpcDamageDefLookup?.Invoke(_charDefIndex);
                value = dam.HasValue ? $"{dam.Value.Min},{dam.Value.Max}" : "0,0";
                return true;
            }
            case "FOOD": value = _food.ToString(); return true;
            case "PRIVLEVEL": value = ((int)PrivLevel).ToString(); return true;
            case "ISMOUNTED": value = IsMounted ? "1" : "0"; return true;
            case "ISDEAD": value = IsDead ? "1" : "0"; return true;
            case "ISINWAR": value = IsInWarMode ? "1" : "0"; return true;
            case "TITLE": value = _title; return true;
            case "SKILLCLASS": value = _skillClass.ToString(); return true;
            // <SkillClass.Statsum> / <SkillClass.SkillSum> / .Name
            // d_admin_PlayerTweak shows the active class cap
            // next to STR/DEX/INT to make stat editing safer.
            case "SKILLCLASS.STATSUM":
            case "SKILLCLASS.STATSUMMAX":
            {
                var def = SphereNet.Game.Definitions.DefinitionLoader
                    .GetSkillClassDef(_skillClass);
                value = (def?.StatSumMax ?? 225).ToString();
                return true;
            }
            case "SKILLCLASS.SKILLSUM":
            case "SKILLCLASS.SKILLSUMMAX":
            case "SKILLCLASS.MAXSKILLS":
            {
                var def = SphereNet.Game.Definitions.DefinitionLoader
                    .GetSkillClassDef(_skillClass);
                value = (def?.SkillSumMax ?? 7000).ToString();
                return true;
            }
            case "SKILLCLASS.NAME":
            {
                var def = SphereNet.Game.Definitions.DefinitionLoader
                    .GetSkillClassDef(_skillClass);
                value = def?.Name ?? "";
                return true;
            }
            // Source-X client introspection (CClient::GetReportedVer
            // and friends). Sphere admin search row uses these to
            // decorate online players with their client kind.
            case "REPORTEDCLIVER":
            {
                var info = ResolveClientInfo?.Invoke(this) ?? (0, ClientType.ClassicWindows);
                value = info.ReportedCliver.ToString();
                return true;
            }
            case "CLIENTISKR":
            {
                var info = ResolveClientInfo?.Invoke(this) ?? (0, ClientType.ClassicWindows);
                value = info.Type == ClientType.KingdomReborn ? "1" : "0";
                return true;
            }
            case "IS3D":
            {
                var info = ResolveClientInfo?.Invoke(this) ?? (0, ClientType.ClassicWindows);
                value = info.Type == ClientType.Classic3D ? "1" : "0";
                return true;
            }
            case "ISENHANCED":
            {
                var info = ResolveClientInfo?.Invoke(this) ?? (0, ClientType.ClassicWindows);
                value = info.Type == ClientType.Enhanced ? "1" : "0";
                return true;
            }
            // Houses / Ships ownership counts. We do not have a multi-
            // tile housing engine yet; report 0 so the admin dialog
            // renders the "Empty" branch instead of an unresolved
            // <Houses> token. Indexed accessors (HOUSE.N / SHIP.N)
            // similarly return "0" via the prefix branch below.
            case "HOUSES":
            case "SHIPS":
                value = "0";
                return true;
            case "REGION":
                value = (TryGetTag("CURRENT_REGION_UID", out string? regionUid) ? regionUid : "") ?? "";
                return true;
            case "REGION.NAME":
                value = (TryGetTag("CURRENT_REGION", out string? regionName) ? regionName : "") ?? "";
                return true;
            case "ROOM":
                value = (TryGetTag("CURRENT_ROOM", out string? roomUid) ? roomUid : "") ?? "";
                return true;
            case "RESPHYSICAL": value = _resPhysical.ToString(); return true;
            case "RESFIRE": value = _resFire.ToString(); return true;
            case "RESCOLD": value = _resCold.ToString(); return true;
            case "RESPOISON": value = _resPoison.ToString(); return true;
            case "RESENERGY": value = _resEnergy.ToString(); return true;
            case "KILLS": value = _kills.ToString(); return true;
            case "CRIMINAL": value = IsCriminal ? "1" : "0"; return true;
            case "MURDERER": value = IsMurderer ? "1" : "0"; return true;
            case "POISONLEVEL": value = _poisonLevel.ToString(); return true;
            case "TARGP":
                if (TryGetTag("TARGP", out string? targPoint))
                {
                    value = targPoint ?? "0,0,0,0";
                    return true;
                }
                value = "0,0,0,0";
                return true;

            // --- New direct field properties ---
            case "OSTR": value = _oStr.ToString(); return true;
            case "ODEX": value = _oDex.ToString(); return true;
            case "OINT": value = _oInt.ToString(); return true;
            case "MODSTR": value = _modStr.ToString(); return true;
            case "MODDEX": value = _modDex.ToString(); return true;
            case "MODINT": value = _modInt.ToString(); return true;
            case "MODAR": value = _modAr.ToString(); return true;
            case "MODMAXWEIGHT": value = _modMaxWeight.ToString(); return true;
            case "OBODY": value = $"0{_oBody:X}"; return true;
            case "OSKIN": value = _oSkin.ToString(); return true;
            case "LUCK": value = _luck.ToString(); return true;
            case "NIGHTSIGHT": value = _nightSight ? "1" : "0"; return true;
            case "STONE": value = IsStatFlag(StatFlag.Stone) ? "1" : "0"; return true;
            case "STEPSTEALTH": value = _stepStealth.ToString(); return true;
            case "EXP": value = _exp.ToString(); return true;
            case "LEVEL": value = _level.ToString(); return true;
            case "DEATHS": value = _deaths.ToString(); return true;
            case "HOMEDIST": value = _homeDist.ToString(); return true;
            case "HOME":
                value = $"{_home.X},{_home.Y},{_home.Z},{_home.Map}";
                return true;
            case "ACTPRI": value = _actPri.ToString(); return true;
            case "SPEECHCOLOR": value = _speechColor.ToString(); return true;
            case "MAXFOLLOWER": value = _maxFollower.ToString(); return true;
            case "CURFOLLOWER": value = CurFollower.ToString(); return true;
            case "RESFIREMAX": value = _resFireMax.ToString(); return true;
            case "RESCOLDMAX": value = _resColdMax.ToString(); return true;
            case "RESPOISONMAX": value = _resPoisonMax.ToString(); return true;
            case "RESENERGYMAX": value = _resEnergyMax.ToString(); return true;
            case "DAMPHYSICAL": value = _damPhysical.ToString(); return true;
            case "DAMFIRE": value = _damFire.ToString(); return true;
            case "DAMCOLD": value = _damCold.ToString(); return true;
            case "DAMPOISON": value = _damPoison.ToString(); return true;
            case "DAMENERGY": value = _damEnergy.ToString(); return true;
            case "VISUALRANGE": value = _visualRange.ToString(); return true;
            case "EMOTEACT": value = _emoteAct ? "1" : "0"; return true;
            case "FONT": value = _font.ToString(); return true;
            case "PROFILE": value = _profile; return true;
            case "PFLAG": value = _pFlag.ToString(); return true;
            case "TITHING": value = _tithing.ToString(); return true;
            case "SPEEDMODE": value = _speedMode.ToString(); return true;
            case "LIGHT": value = _lightLevel.ToString(); return true;
            case "SCREENSIZE":
                // The 0xBF sub 0x1C viewport-size report would populate
                // _screenWidth / _screenHeight. Until that's wired, return
                // the classic 800x600 assumption so scripts that branch on
                // aspect ratio still get a sane value.
                value = $"{_screenWidth},{_screenHeight}";
                return true;
            case "SCREENSIZE.X": value = _screenWidth.ToString(); return true;
            case "SCREENSIZE.Y": value = _screenHeight.ToString(); return true;
            case "ACT": value = _act == Serial.Invalid ? "0" : $"0{_act.Value:X}"; return true;
            case "ACTARG1": value = _actArg1.ToString(); return true;
            case "ACTARG2": value = _actArg2.ToString(); return true;
            case "ACTARG3": value = _actArg3.ToString(); return true;
            case "ACTP":
                value = $"{_actP.X},{_actP.Y},{_actP.Z},{_actP.Map}";
                return true;
            case "ACTPRV": value = _actPrv == Serial.Invalid ? "0" : $"0{_actPrv.Value:X}"; return true;
            case "ACTDIFF": value = _actDiff.ToString(); return true;
            case "ACTION": value = ((int)_action).ToString(); return true;
            case "CREATETIME":
            case "CREATE": value = (_createTime / 1000).ToString(); return true;
            case "ISONLINE": value = _isOnline ? "1" : "0"; return true;

            // --- Calculated properties ---
            case "SERIAL": value = $"0{Uid.Value:X}"; return true;
            case "ISCHAR": value = "1"; return true;
            case "ISITEM": value = "0"; return true;
            case "DISPIDDEC": value = _bodyId.ToString(); return true;
            case "HEIGHT": value = "10"; return true;
            case "ISVENDOR": value = _npcBrain == NpcBrainType.Vendor ? "1" : "0"; return true;
            case "AC":
            {
                int ac = 0;
                for (int i = 0; i < _equipment.Length; i++)
                {
                    var eq = _equipment[i];
                    if (eq != null) ac += eq.Quality / 2;
                }
                value = ac.ToString();
                return true;
            }
            case "WEIGHT": value = "0"; return true;
            case "MAXWEIGHT":
                value = ((_str * 7 / 2) + 40 + _modMaxWeight).ToString();
                return true;
            case "RANGE":
            {
                var weapon = GetEquippedItem(Layer.OneHanded) ?? GetEquippedItem(Layer.TwoHanded);
                value = weapon != null ? "1" : "1"; // Range from weapon — stub, default 1
                return true;
            }
            case "AGE":
                value = _createTime > 0
                    ? ((Environment.TickCount64 - _createTime) / 1000).ToString()
                    : "0";
                return true;
            case "ISINPARTY":
            {
                var party = ResolvePartyFinder?.Invoke(Uid);
                value = party != null ? "1" : "0";
                return true;
            }
            case "SKILLTOTAL":
            {
                int total = 0;
                for (int i = 0; i < _skillValues.Length; i++) total += _skillValues[i];
                value = total.ToString();
                return true;
            }
            case "COUNT":
            {
                int cnt = 0;
                for (int i = 0; i < _equipment.Length; i++)
                    if (_equipment[i] != null) cnt++;
                value = cnt.ToString();
                return true;
            }
            case "FCOUNT":
            {
                int cnt = 0;
                for (int i = 0; i < _equipment.Length; i++)
                    if (_equipment[i] != null) cnt++;
                var pack = Backpack;
                if (pack != null) cnt += pack.ContentCount;
                value = cnt.ToString();
                return true;
            }
            case "GOLD":
            {
                int gold = 0;
                var pack = Backpack;
                if (pack != null)
                {
                    foreach (var item in pack.Contents)
                        if (item.BaseId == 0x0EED) gold += item.Amount;
                }
                value = gold.ToString();
                return true;
            }
            case "BANKBALANCE":
            {
                value = TryGetTag("BANKBALANCE", out string? bb) ? (bb ?? "0") : "0";
                return true;
            }
            case "SEX":
            {
                // Standard UO female bodies
                bool female = _bodyId == 0x0191 || _bodyId == 0x025E || _bodyId == 0x029B;
                value = female ? "1" : "0";
                return true;
            }
            case "GUILDABBREV":
            {
                value = TryGetTag("GUILD.ABBREV", out string? abbrev) ? (abbrev ?? "") : "";
                return true;
            }
            case "TOWNABBREV":
            {
                value = TryGetTag("TOWN.ABBREV", out string? abbrev) ? (abbrev ?? "") : "";
                return true;
            }
            case "ATTACKER":
            {
                // Bare ATTACKER returns the count of distinct attackers,
                // matching Source-X CChar::r_WriteVal ("ATTACKER").
                value = _attackers.Count.ToString();
                return true;
            }
        }

        // ATTACKER.LAST / ATTACKER.MAX / ATTACKER.n.{DAM|ELAPSED|UID}
        if (upper.StartsWith("ATTACKER.", StringComparison.Ordinal))
        {
            string tail = upper.Substring("ATTACKER.".Length);
            if (tail == "LAST")
            {
                value = _attackers.Count > 0
                    ? "0x" + _attackers[^1].Uid.Value.ToString("X")
                    : "0";
                return true;
            }
            if (tail == "MAX")
            {
                if (_attackers.Count == 0) { value = "0"; return true; }
                int bestIdx = 0;
                for (int i = 1; i < _attackers.Count; i++)
                    if (_attackers[i].TotalDamage > _attackers[bestIdx].TotalDamage)
                        bestIdx = i;
                value = "0x" + _attackers[bestIdx].Uid.Value.ToString("X");
                return true;
            }
            // ATTACKER.n.{DAM|ELAPSED|UID}
            int dot = tail.IndexOf('.');
            if (dot > 0 && int.TryParse(tail.AsSpan(0, dot), out int idx)
                && idx >= 0 && idx < _attackers.Count)
            {
                string sub = tail.Substring(dot + 1);
                var rec = _attackers[idx];
                switch (sub)
                {
                    case "DAM":
                        value = rec.TotalDamage.ToString();
                        return true;
                    case "ELAPSED":
                        // Seconds since last hit from this attacker
                        value = Math.Max(0L, (Environment.TickCount64 - rec.LastHitTick) / 1000L).ToString();
                        return true;
                    case "UID":
                        value = "0x" + rec.Uid.Value.ToString("X");
                        return true;
                }
            }
        }

        // --- Prefix/reference properties ---
        if (upper.StartsWith("TARG.", StringComparison.Ordinal))
        {
            if (TryGetTag(key, out string? targVal))
            {
                value = targVal ?? "0";
                return true;
            }
            value = "0";
            return true;
        }

        // OWNER / OWNER.xxx
        if (upper == "OWNER" || upper.StartsWith("OWNER.", StringComparison.Ordinal))
        {
            Serial ownerUid = OwnerSerial;
            if (!ownerUid.IsValid) { value = "0"; return true; }
            if (upper == "OWNER") { value = $"0{ownerUid.Value:X}"; return true; }
            var world = ResolveWorld?.Invoke();
            var owner = world?.FindObject(ownerUid) as Character;
            if (owner != null)
            {
                string subKey = key["OWNER.".Length..];
                return owner.TryGetProperty(subKey, out value);
            }
            value = "0";
            return true;
        }

        if (upper == "CONTROLLER" || upper.StartsWith("CONTROLLER.", StringComparison.Ordinal))
        {
            Serial controllerUid = ControllerSerial;
            if (!controllerUid.IsValid) { value = "0"; return true; }
            if (upper == "CONTROLLER") { value = $"0{controllerUid.Value:X}"; return true; }
            var world = ResolveWorld?.Invoke();
            var controller = world?.FindObject(controllerUid) as Character;
            if (controller != null)
            {
                string subKey = key["CONTROLLER.".Length..];
                return controller.TryGetProperty(subKey, out value);
            }
            value = "0";
            return true;
        }

        // WEAPON / WEAPON.xxx
        if (upper == "WEAPON" || upper.StartsWith("WEAPON.", StringComparison.Ordinal))
        {
            var weapon = GetEquippedItem(Layer.OneHanded) ?? GetEquippedItem(Layer.TwoHanded);
            if (weapon == null) { value = "0"; return true; }
            if (upper == "WEAPON") { value = $"0{weapon.Uid.Value:X}"; return true; }
            string subKey = key["WEAPON.".Length..];
            return weapon.TryGetProperty(subKey, out value);
        }

        // MOUNT
        if (upper == "MOUNT")
        {
            var mount = GetEquippedItem(Layer.Horse);
            value = mount != null ? $"0{mount.Uid.Value:X}" : "0";
            return true;
        }

        // SPAWNITEM
        if (upper == "SPAWNITEM")
        {
            value = TryGetTag("SPAWNITEM", out string? sp) ? (sp ?? "0") : "0";
            return true;
        }

        // TOPOBJ
        if (upper == "TOPOBJ")
        {
            value = $"0{Uid.Value:X}";
            return true;
        }

        // TYPEDEF
        if (upper == "TYPEDEF")
        {
            value = TryGetTag("CHARDEF", out string? cd) ? (cd ?? $"0{CharDefIndex:X}") : $"0{CharDefIndex:X}";
            return true;
        }

        // FINDLAYER.n
        if (upper.StartsWith("FINDLAYER.", StringComparison.Ordinal))
        {
            if (int.TryParse(upper.AsSpan("FINDLAYER.".Length), out int layerIdx))
            {
                if (layerIdx >= 0 && layerIdx < _equipment.Length && _equipment[layerIdx] != null)
                    value = $"0{_equipment[layerIdx]!.Uid.Value:X}";
                else
                    value = "0";
                return true;
            }
        }

        // FINDID.xxx
        if (upper.StartsWith("FINDID.", StringComparison.Ordinal))
        {
            string idStr = key["FINDID.".Length..].Trim();
            if (TryParseHexOrDecUshort(idStr, out ushort findId))
            {
                // Search equipment
                for (int i = 0; i < _equipment.Length; i++)
                {
                    if (_equipment[i] != null && _equipment[i]!.BaseId == findId)
                    {
                        value = $"0{_equipment[i]!.Uid.Value:X}";
                        return true;
                    }
                }
                // Search pack
                var pack = Backpack;
                if (pack != null)
                {
                    foreach (var item in pack.Contents)
                    {
                        if (item.BaseId == findId)
                        {
                            value = $"0{item.Uid.Value:X}";
                            return true;
                        }
                    }
                }
            }
            value = "0";
            return true;
        }

        // FINDTYPE.xxx
        if (upper.StartsWith("FINDTYPE.", StringComparison.Ordinal))
        {
            string typeStr = key["FINDTYPE.".Length..].Trim();
            if (TryParseHexOrDecUshort(typeStr, out ushort findType))
            {
                for (int i = 0; i < _equipment.Length; i++)
                {
                    if (_equipment[i] != null && _equipment[i]!.BaseId == findType)
                    {
                        value = $"0{_equipment[i]!.Uid.Value:X}";
                        return true;
                    }
                }
                var pack = Backpack;
                if (pack != null)
                {
                    foreach (var item in pack.Contents)
                    {
                        if (item.BaseId == findType)
                        {
                            value = $"0{item.Uid.Value:X}";
                            return true;
                        }
                    }
                }
            }
            value = "0";
            return true;
        }

        // FINDCONT.n — nth equipped item
        if (upper.StartsWith("FINDCONT.", StringComparison.Ordinal))
        {
            if (int.TryParse(upper.AsSpan("FINDCONT.".Length), out int n))
            {
                int idx = 0;
                for (int i = 0; i < _equipment.Length; i++)
                {
                    if (_equipment[i] != null)
                    {
                        if (idx == n)
                        {
                            value = $"0{_equipment[i]!.Uid.Value:X}";
                            return true;
                        }
                        idx++;
                    }
                }
            }
            value = "0";
            return true;
        }

        // SKILLLOCK.n
        if (upper.StartsWith("SKILLLOCK.", StringComparison.Ordinal))
        {
            if (int.TryParse(upper.AsSpan("SKILLLOCK.".Length), out int skillIdx) &&
                skillIdx >= 0 && skillIdx < _skillLocks.Length)
            {
                value = _skillLocks[skillIdx].ToString();
                return true;
            }
            value = "0";
            return true;
        }

        // STATLOCK.n (TAG-based)
        if (upper.StartsWith("STATLOCK.", StringComparison.Ordinal))
        {
            string statIdx = upper["STATLOCK.".Length..];
            value = TryGetTag($"STATLOCK.{statIdx}", out string? sl) ? (sl ?? "0") : "0";
            return true;
        }

        // SKILLTOTAL +N / SKILLTOTAL -N — totals filtered by threshold.
        // Matches Source-X r_WriteVal: "+<amount>" sums skills >= amount,
        // "-<amount>" sums skills < amount. Skill values are tenths of a
        // percent (0-1000), so +500 = skills >= 50.0%.
        if (upper.StartsWith("SKILLTOTAL ", StringComparison.Ordinal))
        {
            string arg = key.Substring("SKILLTOTAL ".Length).Trim();
            if (arg.Length > 1 && (arg[0] == '+' || arg[0] == '-')
                && int.TryParse(arg.AsSpan(1), out int threshold))
            {
                int total = 0;
                for (int i = 0; i < _skillValues.Length; i++)
                {
                    ushort v = _skillValues[i];
                    if (arg[0] == '+' ? v >= threshold : v < threshold)
                        total += v;
                }
                value = total.ToString();
                return true;
            }
        }

        // SKILLBEST.n — nth highest skill
        if (upper.StartsWith("SKILLBEST.", StringComparison.Ordinal))
        {
            if (int.TryParse(upper.AsSpan("SKILLBEST.".Length), out int rank) && rank >= 0)
            {
                var sorted = new List<(int Index, ushort Value)>();
                for (int i = 0; i < _skillValues.Length; i++)
                    if (_skillValues[i] > 0) sorted.Add((i, _skillValues[i]));
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                value = rank < sorted.Count ? sorted[rank].Index.ToString() : "-1";
                return true;
            }
            value = "-1";
            return true;
        }

        // MEMORY.xxx / MEMORYFIND.xxx (ownership-aware)
        if (upper.StartsWith("MEMORY.", StringComparison.Ordinal) ||
            upper.StartsWith("MEMORYFIND.", StringComparison.Ordinal))
        {
            if (upper.StartsWith("MEMORYFIND.", StringComparison.Ordinal))
            {
                string memType = key["MEMORYFIND.".Length..];
                var entry = FindMemoryEntry(memType);
                if (entry != null && entry.TryGetProperty("LINK", out value))
                    return true;
            }

            value = TryGetTag(key, out string? mv) ? (mv ?? "0") : "0";
            return true;
        }

        // Guild/stone member properties
        if (TryGetGuildProperty(key, out value))
            return true;

        // Party properties
        if (TryGetPartyProperty(key, out value))
            return true;

        // Skill name-based read (MAGICRESISTANCE, TACTICS, etc.)
        if (_skillNameMap.TryGetValue(upper, out var readSkill))
        {
            value = GetSkill(readSkill).ToString();
            return true;
        }

        return base.TryGetProperty(key, out value);
    }

    private static bool TryParseHexOrDecUshort(string val, out ushort result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (val.StartsWith('0') && val.Length > 1 && !val.Contains('.'))
            return ushort.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);
        return ushort.TryParse(val, out result);
    }

    private static bool TryParseShortSingleOrRange(string value, out short result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string v = value.Trim();
        if (v.Length >= 2 && v[0] == '{' && v[^1] == '}')
            v = v[1..^1].Trim();

        if (short.TryParse(v, out result))
            return true;

        var parts = v.Split(new[] { ',', ' ' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        if (!short.TryParse(parts[0], out short a) || !short.TryParse(parts[1], out short b))
            return false;

        int min = Math.Min(a, b);
        int max = Math.Max(a, b);
        result = (short)Random.Shared.Next(min, max + 1);
        return true;
    }

    public override bool TrySetProperty(string key, string value)
    {
        if (!TryNormalizeScriptValue(value, out string normalized))
            normalized = value;

        // SELL=/BUY= are vendor-restock VERBS, not properties; the
        // ScriptInterpreter's ExecuteLine first tries TrySetProperty
        // and then TryExecuteCommand, so any accidental "true" return
        // here would silently swallow the verb and leave the vendor
        // stock empty. Force the verb path by short-circuiting
        // property recognition.
        if (key.Equals("SELL", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("BUY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        switch (key.ToUpperInvariant())
        {
            case "STR": if (short.TryParse(normalized, out short sv)) Str = sv; return true;
            case "DEX": if (short.TryParse(normalized, out short dv)) Dex = dv; return true;
            case "INT": if (short.TryParse(normalized, out short iv)) Int = iv; return true;
            case "HITS": if (short.TryParse(normalized, out short hv)) Hits = hv; return true;
            case "MANA": if (short.TryParse(normalized, out short mv)) Mana = mv; return true;
            case "STAM": if (short.TryParse(normalized, out short stv)) Stam = stv; return true;
            case "MAXHITS": if (short.TryParse(normalized, out short mhv)) MaxHits = mhv; return true;
            case "MAXMANA": if (short.TryParse(normalized, out short mmv)) MaxMana = mmv; return true;
            case "MAXSTAM": if (short.TryParse(normalized, out short msv)) MaxStam = msv; return true;
            case "BODY": if (TryParseHexOrDecUshort(normalized, out ushort bv)) _bodyId = bv; return true;
            case "DIR": if (byte.TryParse(normalized, out byte drv)) _direction = (Direction)drv; return true;
            case "FAME":
                if (TryParseShortSingleOrRange(normalized, out short fv))
                    _fame = fv;
                return true;
            case "KARMA":
                if (TryParseShortSingleOrRange(normalized, out short kv))
                    _karma = kv;
                return true;
            case "NPC":
            case "NPCBRAIN":
                if (TryParseNpcBrain(normalized, out var brain)) _npcBrain = brain;
                return true;
            case "DAM":
                return true;
            case "NPCSPELL":
                if (int.TryParse(normalized, out int spellId) && Enum.IsDefined(typeof(SpellType), spellId))
                    NpcSpellAdd((SpellType)spellId);
                return true;
            case "SKILLCLASS":
                if (string.IsNullOrWhiteSpace(normalized))
                    _skillClass = 0;
                else if (TryParseSkillClassValue(normalized, out int classId))
                    _skillClass = Math.Max(0, classId);
                return true;
            case "TITLE": _title = value; return true;
            case "FLAGS": if (uint.TryParse(normalized, out uint flagsVal)) _statFlags = (StatFlag)flagsVal; return true;
            // KILLS handled below with POISONLEVEL
            case "CRIMINAL":
                if (normalized == "1" || normalized.Equals("true", StringComparison.OrdinalIgnoreCase))
                    SetCriminal();
                return true;
            case "FOOD":
                if (ushort.TryParse(normalized, out ushort foodVal)) _food = foodVal;
                return true;
            case "RESPHYSICAL": if (short.TryParse(normalized, out short rpv)) _resPhysical = rpv; return true;
            case "RESFIRE": if (short.TryParse(normalized, out short rfv)) _resFire = rfv; return true;
            case "RESCOLD": if (short.TryParse(normalized, out short rcv)) _resCold = rcv; return true;
            case "RESPOISON": if (short.TryParse(normalized, out short rpov)) _resPoison = rpov; return true;
            case "RESENERGY": if (short.TryParse(normalized, out short rev)) _resEnergy = rev; return true;
            case "KILLS": if (short.TryParse(normalized, out short killsVal)) _kills = killsVal; return true;
            case "POISONLEVEL":
                if (byte.TryParse(normalized, out byte plv))
                {
                    if (plv > 0) ApplyPoison(plv);
                    else CurePoison();
                }
                return true;

            // --- New writable properties ---
            case "OSTR": if (short.TryParse(normalized, out short osv)) _oStr = osv; return true;
            case "ODEX": if (short.TryParse(normalized, out short odv)) _oDex = odv; return true;
            case "OINT": if (short.TryParse(normalized, out short oiv)) _oInt = oiv; return true;
            case "MODSTR": if (short.TryParse(normalized, out short msv2)) _modStr = msv2; return true;
            case "MODDEX": if (short.TryParse(normalized, out short mdv)) _modDex = mdv; return true;
            case "MODINT": if (short.TryParse(normalized, out short miv)) _modInt = miv; return true;
            case "MODAR": if (short.TryParse(normalized, out short mav)) _modAr = mav; return true;
            case "MODMAXWEIGHT": if (short.TryParse(normalized, out short mmwv)) _modMaxWeight = mmwv; return true;
            case "OBODY": if (ushort.TryParse(normalized, out ushort obv)) _oBody = obv; return true;
            case "OSKIN": if (ushort.TryParse(normalized, out ushort oskinv)) _oSkin = oskinv; return true;
            case "LUCK": if (short.TryParse(normalized, out short luckv)) _luck = luckv; return true;
            case "GM":
            {
                bool isGm = normalized != "0" && !string.IsNullOrEmpty(normalized);
                PrivLevel = isGm ? PrivLevel.GM : PrivLevel.Player;
                return true;
            }
            case "INVUL":
                if (normalized != "0" && !string.IsNullOrEmpty(normalized))
                    SetStatFlag(StatFlag.Invul);
                else
                    ClearStatFlag(StatFlag.Invul);
                return true;
            case "ALLSHOW":
                _allShow = normalized != "0" && !string.IsNullOrEmpty(normalized);
                return true;
            case "PRIVSHOW":
                _privShow = normalized != "0" && !string.IsNullOrEmpty(normalized);
                return true;
            case "PRIVLEVEL":
                if (int.TryParse(normalized, out int plvSet))
                    PrivLevel = (PrivLevel)plvSet;
                return true;
            case "NIGHTSIGHT":
                _nightSight = normalized != "0" && !string.IsNullOrEmpty(normalized);
                return true;
            case "STEPSTEALTH": if (short.TryParse(normalized, out short ssv)) _stepStealth = ssv; return true;
            case "STONE":
                if (normalized != "0" && !string.IsNullOrEmpty(normalized))
                    SetStatFlag(StatFlag.Stone);
                else
                    ClearStatFlag(StatFlag.Stone);
                return true;
            case "EXP": if (int.TryParse(normalized, out int expv)) _exp = expv; return true;
            case "LEVEL": if (short.TryParse(normalized, out short lvv)) _level = lvv; return true;
            case "DEATHS": if (short.TryParse(normalized, out short dthv)) _deaths = dthv; return true;
            case "HOMEDIST": if (short.TryParse(normalized, out short hdv)) _homeDist = hdv; return true;
            case "HOME":
            {
                var hp = normalized.Split(',', StringSplitOptions.TrimEntries);
                if (hp.Length >= 2 && short.TryParse(hp[0], out short hx) && short.TryParse(hp[1], out short hy))
                {
                    sbyte hz = hp.Length > 2 && sbyte.TryParse(hp[2], out sbyte tz) ? tz : (sbyte)0;
                    byte hm = hp.Length > 3 && byte.TryParse(hp[3], out byte tm) ? tm : (byte)0;
                    _home = new Point3D(hx, hy, hz, hm);
                }
                return true;
            }
            case "ACTPRI": if (short.TryParse(normalized, out short apv)) _actPri = apv; return true;
            case "SPEECHCOLOR": if (ushort.TryParse(normalized, out ushort scv)) _speechColor = scv; return true;
            case "MAXFOLLOWER": if (byte.TryParse(normalized, out byte mfv)) _maxFollower = mfv; return true;
            case "CURFOLLOWER": if (byte.TryParse(normalized, out byte cfv)) _curFollower = cfv; return true;
            case "OWNER":
            case "OWNER_UID":
            case "NPCMASTER":
            {
                Serial ownerUid = ParseSerial(normalized);
                if (!ownerUid.IsValid)
                {
                    ClearOwnership(clearFriends: false);
                }
                else
                {
                    SetOwnerControllerRaw(ownerUid, ownerUid, mirrorLegacySummon: IsSummoned);
                    SetStatFlag(StatFlag.Pet);
                }
                return true;
            }
            case "CONTROLLER":
            case "CONTROLLER_UID":
            {
                Serial controllerUid = ParseSerial(normalized);
                SetOwnerControllerRaw(OwnerSerial, controllerUid, mirrorLegacySummon: IsSummoned);
                return true;
            }
            case "RESFIREMAX": if (short.TryParse(normalized, out short rfmv)) _resFireMax = rfmv; return true;
            case "RESCOLDMAX": if (short.TryParse(normalized, out short rcmv)) _resColdMax = rcmv; return true;
            case "RESPOISONMAX": if (short.TryParse(normalized, out short rpmv)) _resPoisonMax = rpmv; return true;
            case "RESENERGYMAX": if (short.TryParse(normalized, out short remv)) _resEnergyMax = remv; return true;
            case "DAMPHYSICAL": if (short.TryParse(normalized, out short dpv)) _damPhysical = dpv; return true;
            case "DAMFIRE": if (short.TryParse(normalized, out short dfv)) _damFire = dfv; return true;
            case "DAMCOLD": if (short.TryParse(normalized, out short dcv)) _damCold = dcv; return true;
            case "DAMPOISON": if (short.TryParse(normalized, out short dpov)) _damPoison = dpov; return true;
            case "DAMENERGY": if (short.TryParse(normalized, out short dev)) _damEnergy = dev; return true;
            case "VISUALRANGE": if (byte.TryParse(normalized, out byte vrv)) _visualRange = vrv; return true;
            case "EMOTEACT":
                _emoteAct = normalized != "0" && !string.IsNullOrEmpty(normalized);
                return true;
            case "FONT": if (byte.TryParse(normalized, out byte fontv)) _font = fontv; return true;
            case "PROFILE": _profile = value; return true;
            case "PFLAG": if (uint.TryParse(normalized, out uint pfv)) _pFlag = pfv; return true;
            case "TITHING": if (int.TryParse(normalized, out int tithv)) _tithing = tithv; return true;
            case "SPEEDMODE": if (byte.TryParse(normalized, out byte smv)) _speedMode = smv; return true;
            case "LIGHT": if (byte.TryParse(normalized, out byte ltv)) _lightLevel = ltv; return true;
            case "ACT":
            {
                if (normalized == "0" || string.IsNullOrEmpty(normalized))
                    _act = Serial.Invalid;
                else if (uint.TryParse(normalized.TrimStart('0').TrimStart('x', 'X'),
                    System.Globalization.NumberStyles.HexNumber, null, out uint actUid))
                    _act = new Serial(actUid);
                return true;
            }
            case "ACTARG1": if (int.TryParse(normalized, out int a1v)) _actArg1 = a1v; return true;
            case "ACTARG2": if (int.TryParse(normalized, out int a2v)) _actArg2 = a2v; return true;
            case "ACTARG3": if (int.TryParse(normalized, out int a3v)) _actArg3 = a3v; return true;
            case "ACTP":
            {
                var ap = normalized.Split(',', StringSplitOptions.TrimEntries);
                if (ap.Length >= 2 && short.TryParse(ap[0], out short ax) && short.TryParse(ap[1], out short ay))
                {
                    sbyte az = ap.Length > 2 && sbyte.TryParse(ap[2], out sbyte tz) ? tz : (sbyte)0;
                    byte am = ap.Length > 3 && byte.TryParse(ap[3], out byte tm) ? tm : (byte)0;
                    _actP = new Point3D(ax, ay, az, am);
                }
                return true;
            }
            case "ACTPRV":
            {
                if (normalized == "0" || string.IsNullOrEmpty(normalized))
                    _actPrv = Serial.Invalid;
                else if (uint.TryParse(normalized.TrimStart('0').TrimStart('x', 'X'),
                    System.Globalization.NumberStyles.HexNumber, null, out uint aprvUid))
                    _actPrv = new Serial(aprvUid);
                return true;
            }
            case "ACTDIFF": if (int.TryParse(normalized, out int adv)) _actDiff = adv; return true;
            case "ACTION":
                if (int.TryParse(normalized, out int actv)) _action = (SkillType)actv;
                return true;
            case "CREATE":
            case "CREATETIME":
                if (long.TryParse(normalized, out long ctv)) _createTime = ctv * 1000;
                return true;
            case "GOLD":
            {
                if (int.TryParse(normalized, out int goldVal))
                {
                    var pack = Backpack;
                    if (pack != null)
                    {
                        // Remove existing gold
                        foreach (var item in pack.Contents.ToList())
                            if (item.BaseId == 0x0EED) item.Delete();
                        // Setting gold is typically done via NEWGOLD command
                        if (goldVal > 0)
                            SetTag("PENDGOLD", goldVal.ToString());
                    }
                }
                return true;
            }
        }

        // SKILLLOCK.n
        var upperKey = key.ToUpperInvariant();
        if (upperKey.StartsWith("SKILLLOCK.", StringComparison.Ordinal))
        {
            if (int.TryParse(upperKey.AsSpan("SKILLLOCK.".Length), out int skillIdx) &&
                skillIdx >= 0 && skillIdx < _skillLocks.Length &&
                byte.TryParse(normalized, out byte lockVal))
            {
                _skillLocks[skillIdx] = lockVal;
            }
            return true;
        }

        // STATLOCK.n (TAG-based)
        if (upperKey.StartsWith("STATLOCK.", StringComparison.Ordinal))
        {
            SetTag($"STATLOCK.{upperKey["STATLOCK.".Length..]}", normalized);
            return true;
        }

        // MEMORY.xxx (TAG-based)
        if (upperKey.StartsWith("MEMORY.", StringComparison.Ordinal))
        {
            SetTag(key, normalized);
            return true;
        }

        // Guild/stone member properties
        if (TrySetGuildProperty(key, normalized))
            return true;

        // Party properties
        if (TrySetPartyProperty(key, normalized))
            return true;

        // Skill name-based assignment (MAGICRESISTANCE={84.0 100.0}, TACTICS=97.0, etc.)
        if (TrySetSkillByName(upperKey, normalized))
            return true;

        return base.TrySetProperty(key, value);
    }

    private static bool TryNormalizeScriptValue(string value, out string normalized)
    {
        normalized = value.Trim();
        if (normalized.StartsWith('{') && normalized.EndsWith('}') && normalized.Length > 2)
        {
            string inner = normalized[1..^1].Trim();
            var parts = inner.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 &&
                double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minVal) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double maxVal))
            {
                if (maxVal < minVal)
                    (minVal, maxVal) = (maxVal, minVal);
                int pick = Random.Shared.Next((int)Math.Round(minVal), (int)Math.Round(maxVal) + 1);
                normalized = pick.ToString();
                return true;
            }
        }

        if (normalized.Contains('.') &&
            double.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double decimalValue))
        {
            normalized = ((int)Math.Round(decimalValue * 10d)).ToString();
            return true;
        }

        return true;
    }

    private static readonly Dictionary<string, SkillType> _skillNameMap = BuildSkillNameMap();

    private static Dictionary<string, SkillType> BuildSkillNameMap()
    {
        var map = new Dictionary<string, SkillType>(StringComparer.OrdinalIgnoreCase);
        foreach (SkillType st in Enum.GetValues<SkillType>())
        {
            if (st == SkillType.None || st == SkillType.Qty) continue;
            map[st.ToString()] = st;
        }
        // Source-X alternate names
        map["ANIMALLORE"] = SkillType.AnimalLore;
        map["ARMSLORE"] = SkillType.ArmsLore;
        map["DETECTINGHIDDEN"] = SkillType.DetectingHidden;
        map["DETECTHIDDEN"] = SkillType.DetectingHidden;
        map["EVALINT"] = SkillType.EvalInt;
        map["EVALUATINGINTELLIGENCE"] = SkillType.EvalInt;
        map["EVALUATEINTEL"] = SkillType.EvalInt;
        map["ITEMID"] = SkillType.ItemId;
        map["ITEMIDENTIFICATION"] = SkillType.ItemId;
        map["MACEFIGHTING"] = SkillType.MaceFighting;
        map["MAGICRESISTANCE"] = SkillType.MagicResistance;
        map["RESISTINGSPELLS"] = SkillType.MagicResistance;
        map["REMOVETRAP"] = SkillType.RemoveTrap;
        map["SPIRITSPEAK"] = SkillType.SpiritSpeak;
        map["TASTEID"] = SkillType.TasteId;
        map["TASTEIDENTIFICATION"] = SkillType.TasteId;
        return map;
    }

    private bool TrySetSkillByName(string upperKey, string normalized)
    {
        if (!_skillNameMap.TryGetValue(upperKey, out var skillType))
            return false;
        if (ushort.TryParse(normalized, out ushort skillVal))
            SetSkill(skillType, skillVal);
        return true;
    }

    private static bool TryParseNpcBrain(string value, out NpcBrainType brain)
    {
        brain = NpcBrainType.None;
        string normalized = value.Trim();

        // Source-X save token form: "NPC_HUMAN", "NPC_BANKER", etc. Strip
        // the NPC_ / BRAIN_ prefix before the enum parse so those names
        // land on the matching enum value. Without this, legacy saves
        // loaded with NPC=NPC_BANKER default to None, and the
        // banker/vendor/healer speech dispatch below never fires.
        if (normalized.StartsWith("npc_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];
        else if (normalized.StartsWith("brain_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[6..];

        if (Enum.TryParse(normalized, true, out NpcBrainType named))
        {
            brain = named;
            return true;
        }

        if (int.TryParse(value, out int numeric))
        {
            brain = (NpcBrainType)numeric;
            return true;
        }

        return false;
    }

    private static bool TryParseSkillClassValue(string value, out int classId)
    {
        classId = 0;
        string v = value.Trim();
        if (int.TryParse(v, out int numeric))
        {
            classId = numeric;
            return true;
        }

        var fromLoader = DefinitionLoader.GetSkillClassDef(v);
        if (fromLoader != null)
        {
            classId = fromLoader.Id.Index;
            return true;
        }

        return false;
    }

    public override bool TryExecuteCommand(string key, string args, ITextConsole source)
    {
        // Source-X chained method dispatch on the equipped-layer slot:
        //   Src.FindLayer(21).Empty   → empty the worn pack
        //   Src.FindLayer(11).Remove  → strip the worn helmet
        // Resolve the layer to an item and re-route the trailing verb
        // there so we do not have to add per-accessor handlers.
        if (key.StartsWith("FINDLAYER(", StringComparison.OrdinalIgnoreCase))
        {
            int closeParen = key.IndexOf(')');
            if (closeParen > 10)
            {
                string layerStr = key.Substring(10, closeParen - 10);
                string tail = key[(closeParen + 1)..].TrimStart('.');
                if (int.TryParse(layerStr, out int layerNum) && tail.Length > 0)
                {
                    var worn = GetEquippedItem((Layer)layerNum);
                    if (worn == null || worn.IsDeleted) return true;
                    if (tail.Equals("REMOVE", StringComparison.OrdinalIgnoreCase))
                    {
                        worn.Delete();
                        return true;
                    }
                    return worn.TryExecuteCommand(tail, args, source);
                }
            }
        }

        switch (key.ToUpperInvariant())
        {
            case "NEWITEM":
            {
                // NEWITEM i_gold — creates item and stores as NEW context
                // The actual item creation is handled by the script engine callback
                NewItemId = args.Trim();
                return true;
            }
            case "EQUIP":
            {
                PendingEquip = true;
                return true;
            }
            case "NPCSPELL":
            {
                var raw = args.Trim();
                if (int.TryParse(raw, out int sid) && Enum.IsDefined(typeof(SpellType), sid))
                    NpcSpellAdd((SpellType)sid);
                else if (Enum.TryParse<SpellType>(raw, true, out var st))
                    NpcSpellAdd(st);
                return true;
            }
            case "CONSUME":
            {
                // Source-X CChar::Spell_CastDone / r_Verb CONSUME:
                //   CONSUME [amount [defname|0xBASEID]]
                // Walks the backpack subtree, decrementing the matching
                // item's Amount and deleting it when the stack is empty.
                // Empty defname falls back to "consume one of any item"
                // — matching r_Verb behaviour where omitted args mean
                // "the last NEWITEM defname". Returns silently if the
                // requested resource is not present (Sphere never errors
                // out on missing reagent counts; scripts gate on RESTEST
                // before invoking CONSUME).
                int wantAmount = 1;
                string defArg = "";
                var consumeParts = (args ?? "").Split(
                    new[] { ' ', '\t' }, 2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (consumeParts.Length > 0 && int.TryParse(consumeParts[0], out int parsedAmount) && parsedAmount > 0)
                {
                    wantAmount = parsedAmount;
                    if (consumeParts.Length > 1) defArg = consumeParts[1].Trim();
                }
                else if (consumeParts.Length > 0)
                {
                    defArg = (args ?? "").Trim();
                }
                if (string.IsNullOrEmpty(defArg)) defArg = NewItemId ?? "";

                ushort wantBaseId = ResolveItemBaseIdForVerb(defArg);
                if (wantBaseId == 0) return true;

                ConsumeFromContainer(Backpack, wantBaseId, ref wantAmount);
                return true;
            }
            case "KILL":
                Kill();
                return true;
            case "RESURRECT":
                Resurrect();
                return true;
            case "ANIM":
            {
                // Source-X CChar::r_Verb ANIM action[,frameCount[,repeatCount[,backwards[,repeat[,delay]]]]]
                // Broadcasts the 0x6E animation packet to every client
                // observing the character. Only the action id is
                // mandatory; the remaining tokens reuse Source-X
                // defaults (frameCount=7, repeatCount=1, fwd, no
                // repeat, delay=0) when omitted.
                var aparts = (args ?? "").Split(
                    new[] { ',', ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (aparts.Length == 0 || !TryParseScriptUShort(aparts[0], out ushort animAction))
                    return true;
                ushort animFrames = aparts.Length > 1 && TryParseScriptUShort(aparts[1], out ushort fc) ? fc : (ushort)7;
                ushort animRepeats = aparts.Length > 2 && TryParseScriptUShort(aparts[2], out ushort rc) ? rc : (ushort)1;
                bool animBackwards = aparts.Length > 3 && aparts[3] != "0";
                bool animRepeat = aparts.Length > 4 && aparts[4] != "0";
                byte animDelay = aparts.Length > 5 && byte.TryParse(aparts[5], out byte d) ? d : (byte)0;
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketAnimation(
                        Uid.Value, animAction, animFrames, animRepeats,
                        forward: !animBackwards, repeat: animRepeat, delay: animDelay),
                    0);
                return true;
            }
            case "GO":
            {
                // GO x,y,z,map
                var parts = args.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 &&
                    short.TryParse(parts[0], out short gx) &&
                    short.TryParse(parts[1], out short gy))
                {
                    sbyte gz = parts.Length > 2 && sbyte.TryParse(parts[2], out sbyte tz) ? tz : Z;
                    byte gm = parts.Length > 3 && byte.TryParse(parts[3], out byte tm) ? tm : MapIndex;
                    MoveTo(new Point3D(gx, gy, gz, gm));
                }
                return true;
            }
            case "FACE":
            {
                if (byte.TryParse(args, out byte dir))
                    _direction = (Direction)(dir & 0x07);
                return true;
            }
            case "BOUNCE":
            {
                // Source-X CChar::r_Verb BOUNCE bounces whatever the
                // character is currently dragging back to its origin
                // container. We don't model a server-side drag cursor
                // (the 0x25 pickup is handled inline by GameClient and
                // either places or rejects the item in the same tick),
                // so there's nothing to bounce here. The verb is left
                // wired for script compatibility — calling it is safe
                // and silent.
                return true;
            }
            case "SOUND":
            {
                // SOUND id[, mode]. Broadcasts 0x54 to nearby observers
                // at the character's tile. Mode default 1 (looped=false).
                var sparts = (args ?? "").Split(
                    new[] { ',', ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (sparts.Length == 0 || !TryParseScriptUShort(sparts[0], out ushort soundId))
                    return true;
                byte sndMode = sparts.Length > 1 && byte.TryParse(sparts[1], out byte m) ? m : (byte)1;
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketSound(soundId, X, Y, Z, sndMode),
                    0);
                return true;
            }
            case "EFFECT":
            {
                // Source-X r_Verb EFFECT type, id, speed, duration, explode[, hue, render]
                // We honour the first five tokens which cover all
                // standard admin/spell scripts (the optional hue/render
                // pair feeds the colored-particle packet which we don't
                // implement yet — defaults to 0/0).
                var eparts = (args ?? "").Split(
                    new[] { ',', ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (eparts.Length < 2
                    || !byte.TryParse(eparts[0], out byte effType)
                    || !TryParseScriptUShort(eparts[1], out ushort effId))
                    return true;
                byte effSpeed = eparts.Length > 2 && byte.TryParse(eparts[2], out byte sp) ? sp : (byte)5;
                byte effDur = eparts.Length > 3 && byte.TryParse(eparts[3], out byte du) ? du : (byte)1;
                bool effExplode = eparts.Length > 4 && eparts[4] != "0";
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketEffect(
                        effType, Uid.Value, Uid.Value, effId,
                        X, Y, Z, X, Y, Z,
                        effSpeed, effDur, fixedDir: true, explode: effExplode),
                    0);
                return true;
            }

            // --- New commands ---
            case "ALLSKILLS":
            {
                if (ushort.TryParse(args.Trim(), out ushort skillVal))
                {
                    for (int i = 0; i < _skillValues.Length; i++)
                        _skillValues[i] = skillVal;
                }
                return true;
            }
            case "SKILLGAIN":
            {
                // Source-X: invokes the skill-gain check with a given
                // difficulty. Args: "<skill_id>,<difficulty>" where
                // difficulty is 0-100.
                var parts = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2
                    && int.TryParse(parts[0], out int skillId)
                    && int.TryParse(parts[1], out int diff)
                    && skillId >= 0 && skillId < (int)SkillType.Qty)
                {
                    Skills.SkillEngine.GainExperience(this, (SkillType)skillId, diff);
                }
                return true;
            }
            case "SELL":
            case "BUY":
            {
                PopulateVendorStock(args.Trim(), buySide: key.Equals("BUY", StringComparison.OrdinalIgnoreCase));
                return true;
            }
            case "ITEM":
            case "ITEMNEWBIE":
            {
                // @Create trigger body verb: spawn and equip an item on
                // this NPC. Args: "defname[,amount[,dice]]". random_*
                // defnames resolve via TemplateEngine to a single
                // itemdef. Remembers the spawned item so a following
                // COLOR= verb can tint it, matching Source-X write-
                // order semantics (ITEM then COLOR pairs).
                var parts = args.Split(',', StringSplitOptions.TrimEntries);
                string name = parts.Length > 0 ? parts[0] : "";
                int amount = 0;
                if (parts.Length >= 2 && int.TryParse(parts[1], out int a) && a > 0)
                    amount = a;
                string? dice = parts.Length >= 3 ? parts[2] : null;
                SpawnAndEquipItem(name, amount, dice);
                return true;
            }
            case "COLOR":
            {
                // Applies the hue to the last item spawned via ITEM=.
                // "match_*" reuses the last resolved hue — lets a
                // facial_hair share colour with the hair ITEM that
                // came right before it.
                ushort hue = ResolveColorArg(args.Trim(), _lastVerbHue);
                if (hue == 0) return true;
                if (_lastCreatedItem != null && !_lastCreatedItem.IsDeleted)
                    _lastCreatedItem.Hue = new Core.Types.Color(hue);
                else
                    Hue = new Core.Types.Color(hue); // no item yet → tint the NPC body
                _lastVerbHue = hue;
                return true;
            }
            case "ADDBUFF":
            {
                // Source-X: ADDBUFF icon, cliloc1, cliloc2, time, arg1, arg2, arg3
                // We honour icon + duration + a single plain-text arg
                // string (concatenation of any args past the time slot)
                // using the existing PacketBuffIcon code path. Cliloc
                // indirection — clients without a cliloc.mul entry for
                // arg placeholders just render empty strings, so we
                // prefer the raw-string fallback.
                var parts = args.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 1 && TryParseScriptUShort(parts[0], out ushort icon))
                {
                    ushort duration = 0;
                    if (parts.Length >= 4 && TryParseScriptUShort(parts[3], out ushort d))
                        duration = d;
                    string argText = parts.Length >= 5
                        ? string.Join(" ", parts[4..]).Trim()
                        : "";
                    SendPacketToOwner?.Invoke(this, new SphereNet.Network.Packets.Outgoing.PacketBuffIcon(
                        Uid.Value, icon, true, duration, argText, ""));
                }
                return true;
            }
            case "REMOVEBUFF":
            {
                // Source-X: REMOVEBUFF icon
                if (TryParseScriptUShort(args.Trim(), out ushort icon))
                {
                    SendPacketToOwner?.Invoke(this, new SphereNet.Network.Packets.Outgoing.PacketBuffIcon(
                        Uid.Value, icon, false));
                }
                return true;
            }
            case "SYSMESSAGELOC":
            {
                // Source-X: SYSMESSAGELOC hue, cliloc_id, args
                var parts = args.Split(',', 3, StringSplitOptions.TrimEntries);
                if (parts.Length >= 2
                    && TryParseScriptUShort(parts[0], out ushort hue)
                    && uint.TryParse(parts[1], out uint cliloc))
                {
                    string argText = parts.Length >= 3 ? parts[2] : "";
                    SendPacketToOwner?.Invoke(this, new SphereNet.Network.Packets.Outgoing.PacketClilocMessage(
                        Serial.Invalid.Value, 0xFFFF, 6 /* system */, hue, 3, cliloc, "System", argText));
                }
                return true;
            }
            case "SYSMESSAGEUA":
            {
                // Source-X: SYSMESSAGEUA hue, font, mode, language, text.
                // Route through the existing unicode speech packet with
                // serial=0xFFFFFFFF (system origin).
                var parts = args.Split(',', 5, StringSplitOptions.TrimEntries);
                if (parts.Length >= 5
                    && TryParseScriptUShort(parts[0], out ushort hue)
                    && TryParseScriptUShort(parts[1], out ushort font)
                    && byte.TryParse(parts[2], out byte mode))
                {
                    string lang = parts[3];
                    string text = parts[4];
                    SendPacketToOwner?.Invoke(this, new SphereNet.Network.Packets.Outgoing.PacketSpeechUnicodeOut(
                        0xFFFFFFFF, 0xFFFF, mode, hue, font, lang, "System", text));
                }
                return true;
            }
            case "ARROWQUEST":
            {
                // Source-X: ARROWQUEST x, y (both 0 disables).
                var parts = args.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 2
                    && TryParseScriptUShort(parts[0], out ushort ax)
                    && TryParseScriptUShort(parts[1], out ushort ay))
                {
                    bool active = ax != 0 || ay != 0;
                    SendPacketToOwner?.Invoke(this, new SphereNet.Network.Packets.Outgoing.PacketArrowQuest(
                        active, ax, ay));
                }
                return true;
            }
            case "MIDILIST":
            {
                // Source-X: MIDILIST music1, music2, ... — pick one at
                // random and tell the client to play it.
                var parts = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var pick = parts[Random.Shared.Next(parts.Length)];
                    if (TryParseScriptUShort(pick, out ushort musicId))
                    {
                        SendPacketToOwner?.Invoke(this, new SphereNet.Network.Packets.Outgoing.PacketPlayMusic(musicId));
                    }
                }
                return true;
            }
            case "INVIS":
            {
                // If argument given, set explicitly; otherwise toggle
                if (!string.IsNullOrEmpty(args?.Trim()))
                {
                    if (args.Trim() != "0")
                        SetStatFlag(StatFlag.Invisible);
                    else
                        ClearStatFlag(StatFlag.Invisible);
                }
                else
                {
                    if (IsStatFlag(StatFlag.Invisible))
                        ClearStatFlag(StatFlag.Invisible);
                    else
                        SetStatFlag(StatFlag.Invisible);
                }
                return true;
            }
            case "INVUL":
            {
                // If argument given, set explicitly; otherwise toggle
                if (!string.IsNullOrEmpty(args?.Trim()))
                {
                    if (args.Trim() != "0")
                        SetStatFlag(StatFlag.Invul);
                    else
                        ClearStatFlag(StatFlag.Invul);
                }
                else
                {
                    if (IsStatFlag(StatFlag.Invul))
                        ClearStatFlag(StatFlag.Invul);
                    else
                        SetStatFlag(StatFlag.Invul);
                }
                return true;
            }
            case "DISMOUNT":
            {
                ClearStatFlag(StatFlag.OnHorse);
                return true;
            }
            case "NEWGOLD":
            {
                NewItemId = "i_gold";
                if (int.TryParse(args.Trim(), out int goldAmt) && goldAmt > 0)
                    SetTag("NEWITEM_AMOUNT", goldAmt.ToString());
                return true;
            }
            case "NEWLOOT":
            {
                NewItemId = args.Trim();
                return true;
            }
            case "POLY":
            {
                if (ushort.TryParse(args.Trim(), out ushort polyBody))
                {
                    if (_oBody == 0) _oBody = _bodyId;
                    _bodyId = polyBody;
                    SetStatFlag(StatFlag.Polymorph);
                    MarkDirty(DirtyFlag.Body);
                }
                return true;
            }
            case "SLEEP":
            {
                SetStatFlag(StatFlag.Sleeping);
                return true;
            }
            case "SUICIDE":
            {
                Kill();
                return true;
            }
            case "RELEASE":
            {
                _npcMaster = Serial.Invalid;
                ClearStatFlag(StatFlag.Pet);
                return true;
            }
            case "BARK":
            {
                // Source-X CChar::SoundChar(CRESND_RAND) — emit the
                // body's idle/death sound. We don't ship the per-body
                // sound table yet, so synthesise a tile-relative sound
                // from the body id (good enough to make the verb
                // audible) and broadcast it.
                ushort barkSnd = (ushort)(0x0001 + (BodyId & 0x00FF));
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketSound(barkSnd, X, Y, Z),
                    0);
                return true;
            }
            case "BOW":
            {
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketAnimation(Uid.Value, 32),
                    0);
                return true;
            }
            case "SALUTE":
            {
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketAnimation(Uid.Value, 33),
                    0);
                return true;
            }
            case "DCLICK":
            {
                // Source-X CChar::r_Verb DCLICK with no arg = dclick
                // self. Open the paperdoll on the owning client. The
                // optional UID arg form is dispatched by the script
                // engine as <UID.0xN.DCLICK> on the target object.
                OpenPaperdollForOwner?.Invoke(this);
                return true;
            }
            case "FLIP":
            {
                _direction = (Direction)(((byte)_direction + 1) & 0x07);
                MarkDirty(DirtyFlag.Direction);
                return true;
            }
            case "FIXWEIGHT":
            {
                // Source-X CChar::r_Verb FIXWEIGHT recomputes the cached
                // carry weight by walking the inventory. We don't cache
                // weight separately — Item.TotalWeight is computed on
                // demand — but the verb is also expected to refresh the
                // status bar so the client picks up the new value, so
                // mark the stat block dirty.
                MarkDirty(DirtyFlag.Stats);
                return true;
            }
            case "UPDATE":
            case "UPDATEX":
            case "REMOVEFROMVIEW":
            {
                MarkDirty((DirtyFlag)0xFFFFFFFF);
                return true;
            }
            case "DROP":
            {
                // Source-X CChar::r_Verb DROP releases the dragged item
                // to the ground. SphereNet handles drag cursors purely
                // in the GameClient packet path (0x25 pickup → 0x07/0x08
                // drop within the same tick); there is no persistent
                // server-side "in hand" item to drop here. The verb is
                // accepted for script compatibility and is a safe
                // no-op.
                return true;
            }
            case "PACK":
            {
                OpenBackpackForOwner?.Invoke(this);
                return true;
            }
            case "BANK":
            {
                OpenBankboxForOwner?.Invoke(this);
                return true;
            }
            case "HUNGRY":
            {
                // Source-X CChar::r_Verb HUNGRY [amount] — adjust food
                // level. Positive arg = set, negative = decrement,
                // omitted = single tick of hunger (matches the @Hunger
                // trigger CONSUME path). Triggers a stat refresh so the
                // client picks up the FOOD bar change.
                string hraw = (args ?? "").Trim();
                if (hraw.Length == 0)
                {
                    if (_food > 0) _food--;
                }
                else if (int.TryParse(hraw, out int hAmount))
                {
                    if (hAmount < 0)
                        _food = (ushort)Math.Max(0, _food + hAmount);
                    else
                        _food = (ushort)Math.Min(60, hAmount);
                }
                MarkDirty(DirtyFlag.Stats);
                return true;
            }
            case "NUDGEUP":
            {
                if (int.TryParse(args.Trim(), out int nup) && nup != 0)
                {
                    var pos = Position;
                    Position = new Point3D(pos.X, pos.Y, (sbyte)Math.Clamp(pos.Z + nup, -128, 127), pos.Map);
                }
                return true;
            }
            case "NUDGEDOWN":
            {
                if (int.TryParse(args.Trim(), out int ndn) && ndn != 0)
                {
                    var pos = Position;
                    Position = new Point3D(pos.X, pos.Y, (sbyte)Math.Clamp(pos.Z - ndn, -128, 127), pos.Map);
                }
                return true;
            }
            case "PRIVSET":
            {
                if (int.TryParse(args.Trim(), out int plv))
                    PrivLevel = (PrivLevel)plv;
                return true;
            }
            case "FORGIVE":
            {
                RemoveTag("JAIL");
                RemoveTag("JAIL_EXPIRE");
                _kills = 0;
                _criminalTimer = 0;
                return true;
            }
            case "JAIL":
            {
                string cell = args.Trim();
                SetTag("JAIL", string.IsNullOrEmpty(cell) ? "1" : cell);
                return true;
            }

            // --- Admin dialog verbs (Source-X CChar verb table) ---

            case "DISCONNECT":
            {
                DisconnectClient?.Invoke(this, false);
                return true;
            }
            case "KICK":
            {
                // Kick = disconnect + ban marker (SourceX flips the
                // account's "blocked" bit). We pass ban=true and let the
                // wired delegate decide how to enforce it.
                DisconnectClient?.Invoke(this, true);
                return true;
            }
            case "RESENDTOOLTIP":
            {
                ResendTooltipForAll?.Invoke(this);
                return true;
            }
            case "INFO":
            {
                // Source-X <X>.INFO opens the per-target inspect dialog.
                // No-arg form is the typical script call (admin dialog
                // "Src.info" / per-row INFO button).
                OpenInfoDialog?.Invoke(this);
                return true;
            }
            case "TELE":
            {
                // Bare TELE on a character begins the targeted teleport
                // flow. Source-X also accepts "TELE x,y,z[,m]" — we
                // forward to the existing GO verb in that case so the
                // coordinate parsing stays in one place.
                if (!string.IsNullOrWhiteSpace(args) && args.Contains(','))
                    return TryExecuteCommand("GO", args, source);
                BeginTeleTarget?.Invoke(this);
                return true;
            }
            case "T":
            {
                // Source-X CChar 'T' verb is the alias for TELE used by
                // some script packs. Treat it identically.
                if (!string.IsNullOrWhiteSpace(args) && args.Contains(','))
                    return TryExecuteCommand("GO", args, source);
                BeginTeleTarget?.Invoke(this);
                return true;
            }
            case "NIGHTSIGHT":
            {
                if (!string.IsNullOrEmpty(args?.Trim()))
                    _nightSight = args.Trim() != "0";
                else
                    _nightSight = !_nightSight;
                MarkDirty(DirtyFlag.Stats);
                return true;
            }
            case "FOLLOW":
            {
                string raw = args?.Trim() ?? "";
                if (raw.Length == 0) return true;
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    raw = raw[2..];
                else if (raw.StartsWith('0') && raw.Length > 1)
                    raw = raw[1..];
                if (uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out uint followUid))
                    FollowUid?.Invoke(this, followUid);
                return true;
            }
            case "SUMMONCAGE":
            {
                SummonCageAround?.Invoke(this);
                return true;
            }
            case "CLEARCTAGS":
            {
                // Standalone form: drop every CTag under the given
                // prefix on this character. Sphere admin dialogs
                // call <c>ClearCTags Dialog.Admin</c> directly (no
                // PARTY. prefix) to reset the admin UI state.
                CTags.RemoveByPrefix(args?.Trim() ?? string.Empty);
                return true;
            }
        }

        // PARTY.* commands
        if (key.StartsWith("PARTY.", StringComparison.OrdinalIgnoreCase))
        {
            var sub = key["PARTY.".Length..].ToUpperInvariant();
            if (TryExecutePartyCommand(sub, args, source))
                return true;
        }

        return base.TryExecuteCommand(key, args, source);
    }

    /// <summary>Pending NEWITEM creation id (set by script NEWITEM command).</summary>
    public string? NewItemId { get; set; }

    /// <summary>Pending EQUIP flag (set by script EQUIP command).</summary>
    public bool PendingEquip { get; set; }

    // --- Guild/stone member properties ---

    private bool TryGetGuildProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "ACCOUNTGOLD":
            case "ISCANDIDATE":
            case "ISMASTER":
            case "LOYALTO":
            case "PRIV":
            case "PRIVNAME":
            case "GUILD.TITLE":
            case "SHOWABBREV":
                break;
            default:
                return false;
        }

        var gm = ResolveGuildManager?.Invoke(Uid);
        if (gm == null) return false;
        var guild = gm.FindGuildFor(Uid);
        if (guild == null) { value = "0"; return true; }
        var member = guild.FindMember(Uid);
        if (member == null) { value = "0"; return true; }

        switch (upper)
        {
            case "ACCOUNTGOLD":
                value = member.AccountGold.ToString();
                return true;
            case "ISCANDIDATE":
                value = member.Priv == Guild.GuildPriv.Candidate ? "1" : "0";
                return true;
            case "ISMASTER":
                value = member.Priv == Guild.GuildPriv.Master ? "1" : "0";
                return true;
            case "LOYALTO":
                value = member.LoyalTo == Serial.Invalid ? "0" : $"0{member.LoyalTo.Value:X}";
                return true;
            case "PRIV":
                value = ((byte)member.Priv).ToString();
                return true;
            case "PRIVNAME":
                value = member.Priv switch
                {
                    Guild.GuildPriv.Candidate => "CANDIDATE",
                    Guild.GuildPriv.Member => "MEMBER",
                    Guild.GuildPriv.Master => "MASTER",
                    Guild.GuildPriv.Accepted => "ACCEPTED",
                    Guild.GuildPriv.Enemy => "ENEMY",
                    Guild.GuildPriv.Ally => "ALLY",
                    _ => "UNUSED"
                };
                return true;
            case "GUILD.TITLE":
                value = member.Title;
                return true;
            case "SHOWABBREV":
                value = member.ShowAbbrev ? "1" : "0";
                return true;
        }
        return false;
    }

    private bool TrySetGuildProperty(string key, string value)
    {
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "ACCOUNTGOLD":
            case "LOYALTO":
            case "PRIV":
            case "GUILD.TITLE":
            case "SHOWABBREV":
                break;
            default:
                return false;
        }

        var gm = ResolveGuildManager?.Invoke(Uid);
        if (gm == null) return false;
        var guild = gm.FindGuildFor(Uid);
        if (guild == null) return false;
        var member = guild.FindMember(Uid);
        if (member == null) return false;

        switch (upper)
        {
            case "ACCOUNTGOLD":
                if (int.TryParse(value, out int ag)) member.AccountGold = ag;
                return true;
            case "LOYALTO":
                if (value == "0" || string.IsNullOrEmpty(value))
                    member.LoyalTo = Serial.Invalid;
                else if (uint.TryParse(value.TrimStart('0').TrimStart('x', 'X'),
                    System.Globalization.NumberStyles.HexNumber, null, out uint loyalUid))
                    member.LoyalTo = new Serial(loyalUid);
                return true;
            case "PRIV":
                if (byte.TryParse(value, out byte pv)) member.Priv = (Guild.GuildPriv)pv;
                return true;
            case "GUILD.TITLE":
                member.Title = value;
                return true;
            case "SHOWABBREV":
                member.ShowAbbrev = value != "0";
                return true;
        }
        return false;
    }

    // --- Party properties ---

    private bool TryGetPartyProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();
        if (!upper.StartsWith("PARTY.")) return false;

        var party = ResolvePartyFinder?.Invoke(Uid);

        var sub = upper["PARTY.".Length..];

        switch (sub)
        {
            case "MASTER":
                value = party != null ? $"0{party.Master.Value:X}" : "0";
                return true;
            case "MEMBERS":
                value = party?.MemberCount.ToString() ?? "0";
                return true;
            case "LOOT":
                value = party?.GetLootFlag(Uid) == true ? "1" : "0";
                return true;
            case "TAGCOUNT":
                value = party?.TagCount.ToString() ?? "0";
                return true;
        }

        // PARTY.MEMBER.n
        if (sub.StartsWith("MEMBER.") && int.TryParse(sub["MEMBER.".Length..], out int memberIdx))
        {
            if (party != null && memberIdx >= 0 && memberIdx < party.MemberCount)
                value = $"0{party.Members[memberIdx].Value:X}";
            else
                value = "0";
            return true;
        }

        // PARTY.ISSAMEPARTYOF uid
        if (sub.StartsWith("ISSAMEPARTYOF"))
        {
            var uidPart = sub.Length > "ISSAMEPARTYOF".Length
                ? sub["ISSAMEPARTYOF".Length..].TrimStart('.', ' ')
                : "";
            if (uint.TryParse(uidPart.TrimStart('0').TrimStart('x', 'X'),
                System.Globalization.NumberStyles.HexNumber, null, out uint otherUid))
            {
                value = party?.IsMember(new Serial(otherUid)) == true ? "1" : "0";
            }
            else
                value = "0";
            return true;
        }

        // PARTY.TAG.key
        if (sub.StartsWith("TAG."))
        {
            var tagKey = key["PARTY.TAG.".Length..]; // preserve original case
            if (party != null && party.TryGetTag(tagKey, out string tagVal))
                value = tagVal;
            else
                value = "";
            return true;
        }

        // PARTY.TAGAT.n / PARTY.TAGAT.n.KEY / PARTY.TAGAT.n.VAL
        if (sub.StartsWith("TAGAT."))
        {
            var rest = sub["TAGAT.".Length..];
            var dotParts = rest.Split('.', 2);
            if (int.TryParse(dotParts[0], out int tagIdx) && party != null)
            {
                var (tagKey, tagVal) = party.TagAt(tagIdx);
                if (dotParts.Length > 1)
                {
                    value = dotParts[1] switch
                    {
                        "KEY" => tagKey,
                        "VAL" => tagVal,
                        _ => tagKey
                    };
                }
                else
                    value = $"{tagKey}={tagVal}";
            }
            return true;
        }

        return false;
    }

    private bool TrySetPartyProperty(string key, string value)
    {
        var upper = key.ToUpperInvariant();
        if (!upper.StartsWith("PARTY.")) return false;

        var party = ResolvePartyFinder?.Invoke(Uid);
        if (party == null) return false;

        var sub = upper["PARTY.".Length..];

        switch (sub)
        {
            case "LOOT":
                party.SetLootFlag(Uid, value != "0" && !string.IsNullOrEmpty(value));
                return true;
            case "MASTER":
                if (uint.TryParse(value.TrimStart('0').TrimStart('x', 'X'),
                    System.Globalization.NumberStyles.HexNumber, null, out uint masterUid))
                    party.SetMaster(new Serial(masterUid));
                return true;
        }

        // PARTY.TAG.key
        if (sub.StartsWith("TAG."))
        {
            var tagKey = key["PARTY.TAG.".Length..];
            if (string.IsNullOrEmpty(value))
                party.RemoveTag(tagKey);
            else
                party.SetTag(tagKey, value);
            return true;
        }

        return false;
    }

    private bool TryExecutePartyCommand(string sub, string args, ITextConsole source)
    {
        var pm = ResolvePartyManager?.Invoke();

        switch (sub)
        {
            case "ADDMEMBER":
            {
                if (pm == null) return true;
                if (uint.TryParse(args.Trim().TrimStart('0').TrimStart('x', 'X'),
                    System.Globalization.NumberStyles.HexNumber, null, out uint targetUid))
                {
                    var party = pm.FindParty(Uid);
                    if (party == null)
                        party = pm.CreateParty(Uid);
                    party.AddMember(new Serial(targetUid));
                }
                return true;
            }
            case "ADDMEMBERFORCED":
            {
                if (pm == null) return true;
                if (uint.TryParse(args.Trim().TrimStart('0').TrimStart('x', 'X'),
                    System.Globalization.NumberStyles.HexNumber, null, out uint targetUid))
                {
                    pm.ForceAddMember(Uid, new Serial(targetUid));
                }
                return true;
            }
            case "REMOVEMEMBER":
            {
                if (pm == null) return true;
                var party = pm.FindParty(Uid);
                if (party == null) return true;
                var arg = args.Trim();
                if (arg.StartsWith('@') && int.TryParse(arg[1..], out int idx))
                {
                    if (idx >= 0 && idx < party.MemberCount)
                        pm.Leave(party.Members[idx]);
                }
                else if (uint.TryParse(arg.TrimStart('0').TrimStart('x', 'X'),
                    System.Globalization.NumberStyles.HexNumber, null, out uint removeUid))
                {
                    pm.Leave(new Serial(removeUid));
                }
                return true;
            }
            case "DISBAND":
            {
                if (pm == null) return true;
                var party = pm.FindParty(Uid);
                if (party != null) pm.Disband(party.Master);
                return true;
            }
            case "SYSMESSAGE":
            {
                // Stub — actual system message delivery needs GameClient context
                return true;
            }
            case "CLEARTAGS":
            {
                var party = ResolvePartyFinder?.Invoke(Uid);
                party?.ClearTags();
                return true;
            }
            case "CLEARCTAGS":
            {
                // Source-X verb: drop every *client-session* tag under
                // the given prefix (e.g. CLEARCTAGS Dialog.Admin removes
                // Dialog.Admin.*). No argument → wipe all client tags.
                // CTags live on the active client and die with the
                // session; this verb fires from dialog close handlers
                // and refresh buttons to reset admin-panel-style state.
                CTags.RemoveByPrefix(args ?? string.Empty);
                return true;
            }
            case "TAGLIST":
            {
                // Stub — debug tag list
                return true;
            }
        }
        return false;
    }

    public override bool OnTick()
    {
        if (IsDead) return true;

        long now = Environment.TickCount64;

        // HP regen: every 6s base, affected by hunger
        if (now >= _nextHitRegen && _hits < _maxHits)
        {
            int regenAmount = _food > 0 ? 1 : 0;
            if (_str > 50) regenAmount += 1;
            _hits = (short)Math.Min(_hits + Math.Max(1, regenAmount), _maxHits);
            _nextHitRegen = now + 6000;
        }

        // Mana regen: every 4s base, improved by meditation
        if (now >= _nextManaRegen && _mana < _maxMana)
        {
            int regenAmount = 1;
            if (IsStatFlag(StatFlag.Meditation)) regenAmount += 2;
            _mana = (short)Math.Min(_mana + regenAmount, _maxMana);
            _nextManaRegen = now + 4000;
        }

        // Stam regen: Source-X scales with DEX — higher DEX = faster regen.
        // NPC base interval 1.5s, player 3s. Regen amount scales with MaxStam.
        if (now >= _nextStamRegen && _stam < _maxStam)
        {
            int regenAmt = _isPlayer ? 1 : Math.Max(1, _maxStam / 20);
            _stam = (short)Math.Min(_stam + regenAmt, _maxStam);
            _nextStamRegen = now + (_isPlayer ? 3000 : 1500);
        }

        // Hunger decay: every 10 minutes
        if (_isPlayer && now >= _nextFoodDecay)
        {
            if (_food > 0) _food--;
            _nextFoodDecay = now + 600_000;
        }

        // Poison tick
        ProcessPoisonTick(now);

        // Criminal timer expiry
        if (_criminalTimer > 0 && now >= _criminalTimer)
            _criminalTimer = 0;

        // Memory item ticks
        for (int i = _memories.Count - 1; i >= 0; i--)
        {
            var mem = _memories[i];
            long mt = mem.Timeout;
            if (mt > 0 && now >= mt)
            {
                mem.SetTimeout(0);
                if (!Memory_OnTick(mem))
                    _memories.RemoveAt(i);
            }
        }

        return true;
    }

    // Transient state used by the @Create verb sequence. Both fields
    // live only inside the trigger run — they are cleared by
    // Resurrect() and are not persisted because they're purely a
    // write-order cache for ITEM/COLOR pairs.
    private Items.Item? _lastCreatedItem;
    private ushort _lastVerbHue;

    /// <summary>Resolve a script item argument ("0x1F00", "i_gold",
    /// "1234") down to a wire-side BaseId for FIND/CONSUME-style
    /// verbs. Returns 0 when the token can't be matched, signalling
    /// the caller to no-op (Sphere never errors on bad defnames here;
    /// scripts pre-check via RESTEST/RESCOUNT).</summary>
    private static ushort ResolveItemBaseIdForVerb(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;
        string t = token.Trim();

        // Hex literal (with or without 0x / leading zero per Sphere convention).
        ReadOnlySpan<char> span = t;
        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
        {
            return ushort.TryParse(span[2..], System.Globalization.NumberStyles.HexNumber, null, out ushort hex)
                ? hex : (ushort)0;
        }
        if (span.Length > 1 && span[0] == '0')
        {
            return ushort.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out ushort hex2)
                ? hex2 : (ushort)0;
        }

        // Try defname → ItemDef → DispIndex (wire graphic). Fall back to
        // the resource id when the def has no explicit DispIndex (matches
        // SpawnAndEquipItem's resolution).
        var resources = Definitions.DefinitionLoader.StaticResources;
        if (resources != null)
        {
            var rid = resources.ResolveDefName(t);
            if (rid.IsValid && rid.Type == Core.Enums.ResType.ItemDef)
            {
                var idef = Definitions.DefinitionLoader.GetItemDef(rid.Index);
                if (idef != null)
                {
                    if (idef.DispIndex != 0) return idef.DispIndex;
                    if (idef.DupItemId != 0) return idef.DupItemId;
                }
                if (rid.Index <= 0xFFFF) return (ushort)rid.Index;
            }
        }

        // Plain decimal id (rare but valid for explicit DISPID values).
        return ushort.TryParse(t, out ushort dec) ? dec : (ushort)0;
    }

    /// <summary>Walk the container subtree and decrement
    /// <paramref name="want"/> matching items in place, deleting
    /// stacks that reach zero. Stops as soon as <paramref name="want"/>
    /// hits zero. Mirrors Source-X CChar::ResourceConsume.</summary>
    private static void ConsumeFromContainer(Items.Item? container, ushort baseId, ref int want)
    {
        if (container == null || want <= 0) return;
        var snapshot = new List<Items.Item>(container.Contents);
        foreach (var child in snapshot)
        {
            if (want <= 0) return;
            if (child.IsDeleted) continue;
            if (child.BaseId == baseId)
            {
                int take = Math.Min(want, child.Amount);
                if (take >= child.Amount)
                {
                    container.RemoveItem(child);
                    child.Delete();
                }
                else
                {
                    child.Amount = (ushort)(child.Amount - take);
                }
                want -= take;
            }
            else if (child.ContentCount > 0)
            {
                ConsumeFromContainer(child, baseId, ref want);
            }
        }
    }

    /// <summary>Spawn an item by defname and put it on this NPC at
    /// its natural layer (falls back to the backpack). Resolves
    /// random_* defname pools via TemplateEngine.PickRandomItemDefName
    /// so @Create ITEM=random_shirts_human lands as a concrete shirt.</summary>
    private void SpawnAndEquipItem(string defname, int amount, string? dice)
    {
        if (string.IsNullOrWhiteSpace(defname)) return;
        var world = ResolveWorld?.Invoke();
        if (world == null) return;

        string picked = Definitions.TemplateEngine.PickRandomItemDefName(defname);
        if (string.IsNullOrWhiteSpace(picked)) return;

        var resources = Definitions.DefinitionLoader.StaticResources;
        if (resources == null) return;
        var rid = resources.ResolveDefName(picked);
        if (!rid.IsValid || rid.Type != Core.Enums.ResType.ItemDef)
            return;

        var item = world.CreateItem();
        var idef = Definitions.DefinitionLoader.GetItemDef(rid.Index);
        // Wire-side graphic (DispIndex) is distinct from the script-side
        // resource index for defname ITEMDEFs — see TemplateEngine.ResolveDispId
        // for the full rationale. (ushort)rid.Index used to truncate the
        // 32-bit hash and produced random graphics on equipment.
        ushort dispId = 0;
        if (idef != null)
        {
            if (idef.DispIndex != 0) dispId = idef.DispIndex;
            else if (idef.DupItemId != 0) dispId = idef.DupItemId;
        }
        if (dispId == 0 && rid.Index <= 0xFFFF) dispId = (ushort)rid.Index;
        if (dispId == 0) return;
        item.BaseId = dispId;
        // Store raw NAME= template; Item.GetName() resolves
        // %plural/singular% markers per current Amount on every read.
        if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
            item.Name = idef.Name;

        // Dice roll wins over explicit amount for stackables.
        int finalAmount = amount;
        if (finalAmount <= 0 && !string.IsNullOrWhiteSpace(dice))
            finalAmount = RollDice(dice!);
        if (finalAmount > 1)
            item.Amount = (ushort)Math.Min(finalAmount, ushort.MaxValue);

        var layer = idef?.Layer ?? Core.Enums.Layer.None;
        if (layer == Core.Enums.Layer.None)
            layer = ResolveTileDataLayer(world, item.BaseId);

        if (layer == Core.Enums.Layer.None)
        {
            var pack = Backpack;
            if (pack == null)
            {
                pack = world.CreateItem();
                pack.BaseId = 0x0E75;
                Equip(pack, Core.Enums.Layer.Pack);
            }
            pack.AddItem(item);
        }
        else
        {
            Equip(item, layer);
        }

        _lastCreatedItem = item;
    }

    /// <summary>Resolve an equip layer from <c>tiledata.mul</c>. Source-X
    /// scripts rarely set LAYER= on ITEMDEFs (e.g. i_shirt_plain has it
    /// commented out) and rely on the client data: when the item tile's
    /// Wearable flag is set, its Quality byte carries the layer index.
    /// Returns Layer.None when the item isn't wearable or the map data
    /// isn't loaded.</summary>
    private static Core.Enums.Layer ResolveTileDataLayer(World.GameWorld world, ushort baseId)
    {
        if (world.MapData == null) return Core.Enums.Layer.None;
        var tile = world.MapData.GetItemTileData(baseId);
        if ((tile.Flags & MapData.Tiles.TileFlag.Wearable) == 0)
            return Core.Enums.Layer.None;
        byte q = tile.Quality;
        if (q == 0 || q >= (byte)Core.Enums.Layer.Horse + 1)
            return Core.Enums.Layer.None;
        return (Core.Enums.Layer)q;
    }

    /// <summary>Resolve a COLOR= arg (colors_red, match_hair, 0x0481 …).
    /// Shared between the root-level EquipNewbieItems path and the
    /// trigger-body COLOR= verb so both agree on the palette.</summary>
    private static ushort ResolveColorArg(string arg, ushort lastHue)
    {
        if (string.IsNullOrWhiteSpace(arg)) return 0;
        string n = arg.Trim();
        if (n.StartsWith("match_", StringComparison.OrdinalIgnoreCase))
            return lastHue;

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

        // Hex / decimal literal fallback (COLOR=0x0481).
        if (n.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(n.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hx))
            return hx;
        return ushort.TryParse(n, out ushort dec) ? dec : (ushort)0;
    }

    /// <summary>Sphere dice expression roller (R5 / 2d6 / 1d10+2).
    /// Kept small and defensive — unrecognised expressions default to 1
    /// so a broken line never silently mints a zero-amount stack.</summary>
    private static int RollDice(string expr)
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

    /// <summary>Populate the vendor's stock from a <c>VENDOR_S_*</c> /
    /// <c>VENDOR_B_*</c> template name. Called from the SELL= / BUY=
    /// verbs inside an @NPCRestock trigger. Items land in the vendor's
    /// backpack so the existing buy gump can read them; BUY= lists also
    /// save the template name to TAG.VENDOR_BUY_LIST so a future sell
    /// gump can filter what the vendor is willing to purchase.</summary>
    private void PopulateVendorStock(string templateDefName, bool buySide)
    {
        if (string.IsNullOrWhiteSpace(templateDefName))
            return;

        if (buySide)
        {
            // BUY lists don't spawn items — they only configure what
            // the vendor accepts when the player tries to sell. Store
            // the defname; the sell gump will resolve it at open time.
            SetTag("VENDOR_BUY_LIST", templateDefName);
            return;
        }

        // SELL side — spawn every entry from the template into the
        // dedicated vendor STOCK container (Layer.VendorStock = 26).
        // ClassicUO's BuyList (0x74) handler hard-checks that the
        // referenced container is equipped at Layer.ShopBuyRestock
        // (0x1A == 26) or Layer.ShopBuy (0x1B == 27); items dropped
        // into the regular Backpack (Layer.Pack = 21) are silently
        // rejected and the buy gump never opens. Match Source-X
        // CChar::NPC_Vendor_Restock which uses LAYER_VENDOR_STOCK.
        var world = ResolveWorld?.Invoke();
        if (world == null) return;
        var pack = GetEquippedItem(Layer.VendorStock);
        if (pack == null)
        {
            pack = world.CreateItem();
            pack.BaseId = 0x408D; // i_vendor_box (Source-X stock graphic)
            Equip(pack, Layer.VendorStock);
        }

        int spawned = 0, considered = 0, resolveFails = 0;
        foreach (var (entryName, entryAmount) in
                 Definitions.TemplateEngine.EnumerateSequential(templateDefName))
        {
            considered++;
            string picked = Definitions.TemplateEngine.PickRandomItemDefName(entryName);
            if (string.IsNullOrWhiteSpace(picked)) { resolveFails++; continue; }

            var resources = Definitions.DefinitionLoader.StaticResources;
            if (resources == null) continue;
            // Source-X parity: an [ITEMDEF i_potion_refresh] block lives
            // under a string-hashed *resource* index (e.g. 0x40B2C7E1)
            // but ships `ID=0x0F0E` as the wire graphic. We need BOTH:
            //   - defIndex (32-bit hash) → look up the ItemDef
            //   - dispId   (16-bit graphic) → wire-side BaseId / tile
            // Truncating defIndex to a ushort yields random visible
            // graphics (lava / window-shutter / elven-plate were the
            // classic symptoms before this split — see ResolveDispId).
            int defIndex = Definitions.TemplateEngine.ResolveItemDefIndex(resources, picked);
            if (defIndex == 0) { resolveFails++; continue; }
            ushort dispId = Definitions.TemplateEngine.ResolveDispId(resources, picked);
            if (dispId == 0) { resolveFails++; continue; }

            var item = world.CreateItem();
            var idef = Definitions.DefinitionLoader.GetItemDef(defIndex);
            item.BaseId = dispId;
            // NAME= templates carry %plural/singular% markers — store
            // the RAW template; Item.GetName() pluralizes per Amount on
            // every read (CItem::GetName parity, CItem.cpp:1769).
            if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
                item.Name = idef.Name;
            if (entryAmount > 1)
                item.Amount = (ushort)Math.Min(entryAmount, ushort.MaxValue);
            pack.AddItem(item);
            spawned++;
        }

        SetTag("VENDOR_LAST_RESTOCK", Environment.TickCount64.ToString());
    }

    // Script number parser: accepts 0xNN hex, decimal. Unlike
    // ScriptKey.TryParseNumber we don't apply the Source-X "leading
    // zero = hex" convention here because icon IDs are routinely
    // written with a zero pad (e.g. "0007") but meant as decimal.
    private static bool TryParseScriptUShort(string text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        text = text.Trim();
        if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
            return ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return ushort.TryParse(text, out value);
    }
}
