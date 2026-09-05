using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;

namespace SphereNet.Game.Objects.Characters;

/// <summary>
/// Character instance (player or NPC). Maps to CChar in Source-X.
/// </summary>
public partial class Character : ObjBase
{
    /// <summary>
    /// Optional diagnostic sink. Wired by the host (Program.cs) so
    /// trace lines emitted from inside Character (vendor restock,
    /// trigger verb dispatch, etc.) reach the same logger pipeline
    /// as the rest of the server. Avoids needing an ILogger field on
    /// every Character instance.
    /// </summary>
    public static Action<string>? Diagnostic { get; set; }

    public static Action<Character, Character?>? OnLifecycleKill;
    public static Action<Character>? OnLifecycleResurrect;
    /// <summary>Fired by the script DISMOUNT verb. The host routes it through
    /// MountEngine.Dismount (restores the mount NPC) and the rider's client
    /// update; when unset the verb falls back to clearing the OnHorse flag.</summary>
    public static Action<Character>? OnScriptDismount;

    /// <summary>Script MOUNT verb on an NPC (Source-X Horse_Mount): seat the
    /// NPC's own owner on it. Args: (npc). Wired to MountEngine.TryMount.
    /// The staff-horse script (REF1.MOUNT after NEW.MAKEMYPET SRC) needs it.</summary>
    public static Func<Character, bool>? OnScriptMount;

    /// <summary>Script EQUIP verb (Source-X CHV_EQUIP ItemEquip): equip the
    /// item on this character at its definition layer AND fire @Equip. Wired
    /// in the host so layer resolution and the trigger dispatch stay out of
    /// the pure object model. Args: (wearer, item) → equipped.</summary>
    public static Func<Character, Item, bool>? ScriptEquipItem;

    /// <summary>Fired by the script BOUNCE/DROP verbs. Arg: true = release
    /// the dragged item (DRAGGING tag) to the ground, false = bounce it back
    /// into the backpack. The host resolves the owning client so the drag
    /// cursor is cancelled (0x27) and the item view updated.</summary>
    public static Action<Character, bool>? OnDragRelease;

    /// <summary>Cancel the client's drag cursor (0x27) WITHOUT moving the item.
    /// Death needs this: it takes the held item and applies the equipment rules to
    /// it itself, so a hook that also re-packs the item would place it twice.</summary>
    public static Action<Character>? OnDragCancel;

    /// <summary>Fired before a severely lost NPC (far past its home leash)
    /// teleports home (Source-X @NPCLostTeleport). Return true to cancel the
    /// teleport — the NPC walks back instead.</summary>
    public static Func<Character, bool>? OnNpcLostTeleport;
    /// <summary>Fired when a timed jail sentence expires and the character
    /// should be released (move out of jail, clear Freeze, resync).</summary>
    public static Action<Character>? OnJailReleaseRequested;
    /// <summary>Fired each time hunger decays so scripts can react via the
    /// @Hunger trigger.</summary>
    public static Action<Character>? OnHungerDecay;
    /// <summary>Fires the @Criminal trigger before a character is flagged
    /// criminal. Returns null to cancel the flag (RETURN 1 / script forgave the
    /// crime), or the criminal-flag duration in seconds (ARGN1; 0 = engine
    /// default CriminalTimerSeconds).</summary>
    public static Func<Character, int?>? OnCriminalCheck;

    /// <summary>CANCAST.&lt;spell&gt; property backend (Source-X CHC_CANCAST):
    /// wired to a SpellEngine check (mana, skill req, region antimagic).</summary>
    public static Func<Character, int, bool>? OnCanCastCheck;

    // Static delegate for guild resolution (set in Program.cs)
    public static Func<Serial, Guild.GuildManager?>? ResolveGuildManager;
    // Static delegate for party resolution (set in Program.cs)
    public static Func<Serial, Party.PartyDef?>? ResolvePartyFinder;
    // Static delegate for party manager (set in Program.cs) — needed for commands
    public static Func<Party.PartyManager?>? ResolvePartyManager;

    /// <summary>Removes the active spell effect represented by an IT_SPELL
    /// memory item (Source-X: deleting the memory runs Spell_Effect_Remove).
    /// Wired to SpellEngine.RemoveEffectByMemory; invoked when a script
    /// REMOVEs the memory (the Scripts-X Reaper/Stone Form toggles do this
    /// via FINDID.&lt;RUNE_ITEM&gt;.REMOVE). Returns true when an effect was
    /// found and reverted.</summary>
    public static Func<Item, bool>? SpellMemoryEffectRemover;

    /// <summary>Source-X NPC_WantThisItem: how badly this NPC wants an item
    /// (0-100). Wired to NpcAI.GetWantScore; the give-item flow refuses
    /// unwanted gifts by default when this returns 0.</summary>
    public static Func<Character, Item, int>? NpcWantThisItem;

    /// <summary>Source-X Food_CanEat: can this creature eat the item under
    /// its chardef FOODTYPE diet? Wired to NpcAI.NpcCanEat; unwired (tests)
    /// falls back to the generic edible item classes.</summary>
    public static Func<Character, Item, bool>? NpcCanEatFood;
    // Static delegate for account resolution from character UID (set in Program.cs)
    public static Func<Serial, Account?>? ResolveAccountForChar;
    // Static delegate for character lookup by UID — used for ACCOUNT.CHAR.N.NAME
    // chain and admin dialog references (set in Program.cs).
    public static Func<Serial, Character?>? ResolveCharByUid;
    /// <summary>Resolve house multi UIDs owned by a character. Wired by the
    /// server against HousingEngine so script tokens like HOUSES and HOUSE.0
    /// report live runtime state instead of a stale placeholder.</summary>
    public static Func<Serial, IReadOnlyList<Serial>>? ResolveHouseUidsByOwner;
    /// <summary>Resolve ship multi UIDs owned by a character. Wired by the
    /// server against ShipEngine for SHIPS and SHIP.N script tokens.</summary>
    public static Func<Serial, IReadOnlyList<Serial>>? ResolveShipUidsByOwner;

    // Static delegate used by script verbs that need to emit a packet
    // directly to the owning client (ADDBUFF / REMOVEBUFF / SYSMESSAGELOC
    // / ARROWQUEST / MIDILIST …). Wired in Program.cs against the
    // connected-clients dictionary. Returns silently when the character
    // has no active client.
    public static Action<Character, SphereNet.Network.Packets.PacketWriter>? SendPacketToOwner;

    /// <summary>Notify the owning client that a native buff icon changed.
    /// Args: character, icon, add/remove, remaining duration in seconds, and the
    /// cliloc formatter arguments (Source-X addBuff pptcArgs — the stat deltas a
    /// buff tooltip interpolates). Null/empty means the icon's description cliloc
    /// takes no arguments.</summary>
    public static Action<Character, BuffIcon, bool, ushort, string[]?>? OnClientBuffChanged;

    /// <summary>Ask the character's own client to rebuild its view on the next
    /// tick. Needed when a state change alters what THIS character may see
    /// (dying or being resurrected under DEADCANNOTSEELIVING), which the
    /// movement-driven refresh would otherwise not notice until the next step.
    /// </summary>
    public static Action<Character>? OnOwnViewRefreshNeeded;

    /// <summary>Broadcast a health-bar colour update (Source-X PacketHealthBarUpdate,
    /// 0x17) to observers when the character's poisoned/frozen state changes — the
    /// green/yellow health-bar tint on SA+/KR clients.</summary>
    public static Action<Character>? OnHealthBarStatusChanged;

    /// <summary>Called after magical invisibility is revealed so its timed
    /// spell memory and client buff icon can be retired together.</summary>
    public static Action<Character>? OnHiddenStateCleared;

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

    /// <summary>Fired after BODY/CHARDEF/COLOR changes so online clients
    /// receive an updated 0x78 DrawObject.</summary>
    public static Action<Character>? OnAppearanceChanged;

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
    public new static Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby;

    /// <summary>Broadcast a combat damage popup to nearby clients, selecting the
    /// wire format per recipient version (0x0B for >=4.0.7a, 0xBF.0x22 for older,
    /// nothing below 4.0.0). Args: (origin, range, defenderSerial, rawDamage,
    /// excludeUid). Wired in Program.cs; a single shared buffer per version bucket
    /// preserves the broadcast fast path. Unwired (e.g. unit tests) it is a no-op,
    /// like <see cref="BroadcastNearby"/>.</summary>
    public static Action<Point3D, int, uint, int, uint>? BroadcastDamageNearby;

    /// <summary>Broadcast a direction-only 0x77 update after script FACE.</summary>
    public static Action<Character>? OnFacingChanged;

    /// <summary>Open this character's paperdoll on the owning client.
    /// Used by the script <c>DCLICK</c> verb when the target is the
    /// character itself (Source-X CChar::OnDoubleClickFromSelf).</summary>
    public static Action<Character>? OpenPaperdollForOwner;

    /// <summary>HEAR verb routing (Source-X CChar CHV_HEAR): players get the
    /// text as a private sysmessage on their client, NPCs process it as heard
    /// speech. Wired by the host, which owns both surfaces.</summary>
    public static Action<Character, string, Character?>? OnHearRouted;

    /// <summary>GOCLI resolver: the n-th online client's character (0-based).
    /// Wired by the host (client list lives at the network layer).</summary>
    public static Func<int, Character?>? FindCharByClientIndex;

    /// <summary>GOSOCK resolver: the character on the client with the given
    /// socket id. Wired by the host.</summary>
    public static Func<int, Character?>? FindCharBySocketId;

    /// <summary>AFK verb state (Source-X models it as the napping action).</summary>
    private bool _isAfk;
    public bool IsAfk => _isAfk;

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
            if (upper is "ISVALID" or "ISVALIDE")
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


    // NPC fields
    private NpcBrainType _npcBrain;
    private Serial _npcMaster = Serial.Invalid;
    private ushort _npcFood;

    // Runtime EVENTS list (from CHARDEF + dynamically added)
    private readonly List<ResourceId> _events = [];
    private readonly List<ResourceId> _dspeech = [];

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

    public override void SetTag(string key, string value)
    {
        if (TrySetStatLockTag(key, value))
            return;

        base.SetTag(key, value);
    }

    private bool TrySetStatLockTag(string key, string value)
    {
        string statKey = key;
        if (statKey.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase) ||
            statKey.StartsWith("TAG0.", StringComparison.OrdinalIgnoreCase) ||
            statKey.StartsWith("DTAG.", StringComparison.OrdinalIgnoreCase) ||
            statKey.StartsWith("DTAG0.", StringComparison.OrdinalIgnoreCase))
        {
            int dotIdx = statKey.IndexOf('.');
            statKey = dotIdx >= 0 ? statKey[(dotIdx + 1)..] : "";
        }

        if (!statKey.StartsWith("STATLOCK.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (int.TryParse(statKey.AsSpan("STATLOCK.".Length), out int statIdx) &&
            byte.TryParse(value, out byte lockVal))
        {
            SetStatLock(statIdx, lockVal);
        }

        return true;
    }
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
    public const short UnlimitedHomeDistance = short.MaxValue;
    private short _homeDist = UnlimitedHomeDistance;
    private Point3D _home;
    private short _actPri;
    // Source-X SPEECHCOLOROVERRIDE semantics: 0 = no override, speech goes out
    // with HUE_TEXT_DEF (0x03B2). A non-zero value colors this char's speech.
    private ushort _speechColor;

    // Followers
    private byte _maxFollower = 5;
    private byte _curFollower;

    // Resist caps
    private short _resPhysicalMax = 70;
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
    /// <summary>Poison runtime state (decomposition slice 2) — the members
    /// below delegate so the public API stays unchanged.</summary>
    public CharacterPoisonState Poison => _poison ??= new CharacterPoisonState(this);
    private CharacterPoisonState? _poison;
    private long _nextFieldTick;  // next time standing-in-field damage is applied

    /// <summary>Fired when an attacker flagged ATTACKER.n.IGNORE=1 lands a
    /// hit (Source-X @HitIgnore). Args: victim, attacker uid. Return true to
    /// clear the ignore flag (the script un-ignored the attacker).</summary>
    public static Func<Character, Serial, bool>? OnHitIgnored;

    /// <summary>Combat runtime state (attacker log, criminal/murder counters)
    /// — first slice of the Character decomposition. The members below
    /// delegate so the public API and script surface stay unchanged.</summary>
    public CharacterCombatState CombatState => _combat ??= new CharacterCombatState(this);
    private CharacterCombatState? _combat;

    // Criminal / murderer state lives in CharacterCombatState (Combat).
    public int CriminalTimerRemainingSeconds
    {
        get => CombatState.CriminalTimerRemainingSeconds;
        set => CombatState.CriminalTimerRemainingSeconds = value;
    }

    public int MurderDecayRemainingSeconds
    {
        get => CombatState.MurderDecayRemainingSeconds;
        set => CombatState.MurderDecayRemainingSeconds = value;
    }

    /// <summary>Wearable layer for an item: ITEMDEF LAYER first, then the
    /// tiledata Wearable quality byte (same rule the client equip path uses).</summary>
    private static Layer ResolveWearLayer(Item item)
    {
        var itemDef = DefinitionLoader.GetItemDef(item.BaseId);
        Layer layer = itemDef?.Layer ?? Layer.None;
        if (layer == Layer.None)
        {
            var md = ResolveWorld?.Invoke()?.MapData;
            if (md != null)
            {
                var tile = md.GetItemTileData(item.BaseId);
                if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Wearable) != 0 &&
                    tile.Quality > 0 && tile.Quality <= (byte)Layer.Horse)
                    layer = (Layer)tile.Quality;
            }
        }
        return layer;
    }

    /// <summary>Seconds a character stays criminal (gray) after committing a crime. Set from sphere.ini CRIMINALTIMER.</summary>
    public static int CriminalTimerSeconds { get; set; } = 180;

    /// <summary>sphere.ini DEADCANNOTSEELIVING (Source-X m_fDeadCannotSeeLiving).
    /// 0 disables the rule; 1 hides living NPCs from a ghost; 2 additionally
    /// stops an NPC from seeing living players it does not belong to.</summary>
    public static int DeadCannotSeeLiving { get; set; }

    /// <summary>Whether this (dead) character may see <paramref name="target"/>
    /// (Source-X CChar::CanSeeAsDead). A ghost keeps seeing other ghosts, living
    /// players, its own pets and healers, but loses sight of ordinary living
    /// NPCs. Upstream applies this only as a one-shot screen wipe at the moment
    /// of death; here it is a standing rule the per-observer view delta consults
    /// every tick, so the NPCs stay hidden while the ghost walks around and
    /// reappear the instant it is resurrected.</summary>
    public bool CanSeeAsDead(Character target)
    {
        int mode = DeadCannotSeeLiving;
        if (mode == 0 || target.IsDead || PrivLevel >= PrivLevel.GM)
            return true;

        if (target.IsPlayer)
        {
            // Only an NPC observer is restricted here, and only at mode 2 — a
            // player ghost always keeps seeing the living players around it.
            return IsPlayer || mode != 2 || HasOwner(target.Uid);
        }

        // A living NPC: visible only if it is my own pet or a healer.
        return target.HasOwner(Uid) || target.NpcBrain == NpcBrainType.Healer;
    }
    /// <summary>Murder count threshold that triggers the murderer (red) flag. sphere.ini MURDERMINCOUNT.</summary>
    public static int MurderMinCount { get; set; } = 5;
    /// <summary>Seconds between automatic murder-count decays (online time). sphere.ini MURDERDECAYTIME.</summary>
    public static int MurderDecayTimeSeconds { get; set; } = 28800;
    /// <summary>Karma below which a player is red with zero murders. sphere.ini PLAYEREVIL (Source-X m_iPlayerKarmaEvil).</summary>
    public static int PlayerKarmaEvil { get; set; } = -8000;
    /// <summary>Karma below which a player is grey. sphere.ini PLAYERNEUTRAL (Source-X m_iPlayerKarmaNeutral).</summary>
    public static int PlayerKarmaNeutral { get; set; } = -2000;
    /// <summary>Whether attacking an innocent turns the aggressor criminal. sphere.ini ATTACKINGISACRIME.</summary>
    public static bool AttackingIsACrimeEnabled { get; set; } = true;
    /// <summary>Whether beneficially helping a criminal flags the helper.
    /// sphere.ini HELPINGCRIMINALSISACRIME.</summary>
    public static bool HelpingCriminalsIsACrimeEnabled { get; set; }
    /// <summary>Whether failed snooping flags the snooper criminal. sphere.ini SNOOPCRIMINAL.</summary>
    public static bool SnoopCriminalEnabled { get; set; } = true;
    /// <summary>Whether spells must consume reagents from backpack. sphere.ini REAGENTSREQUIRED.</summary>
    public static bool ReagentsRequiredEnabled { get; set; } = true;
    /// <summary>Players must have the spell in an accessible spellbook to
    /// cast it from memory (reference Spell_CanCast book check). Scroll and
    /// wand casts bypass the book.</summary>
    public static bool SpellbookRequiredEnabled { get; set; } = true;

    /// <summary>COMBATFLAGS bitfield from sphere.ini.</summary>
    public static int CombatFlags { get; set; }
    /// <summary>COMBATDAMAGEERA from sphere.ini.</summary>
    public static int CombatDamageEra { get; set; }
    /// <summary>COMBATHITCHANCEERA from sphere.ini.</summary>
    public static int CombatHitChanceEra { get; set; }
    /// <summary>COMBATSPEEDERA from sphere.ini.</summary>
    public static int CombatSpeedEra { get; set; }
    /// <summary>COMBATPARRYINGERA bitmask from sphere.ini.</summary>
    public static int CombatParryingEra { get; set; } =
        (int)(ParryEraFlags.PreSeFormula | ParryEraFlags.ShieldBlock);
    /// <summary>FEATURESE mask; bit 0x02 enables Ninja/Samurai systems.</summary>
    public static int FeatureSE { get; set; }
    /// <summary>FEATUREAOS mask; bit 0x02 enables AOS update-B combat systems.</summary>
    public static int FeatureAOS { get; set; }
    /// <summary>RACIALFLAGS mask from sphere.ini.</summary>
    public static int RacialFlags { get; set; }
    /// <summary>SPEEDSCALEFACTOR used by Source-X swing-speed formulas.</summary>
    public static int CombatSpeedScaleFactor { get; set; } = 15000;
    /// <summary>ARCHERYMINDIST from sphere.ini.</summary>
    public static int ArcheryMinDist { get; set; } = 1;
    /// <summary>ARCHERYMAXDIST from sphere.ini.</summary>
    public static int ArcheryMaxDist { get; set; } = 12;
    /// <summary>COMBATARCHERYMOVEMENTDELAY from sphere.ini (ms).</summary>
    public static int CombatArcheryMovementDelay { get; set; }
    /// <summary>COMBATMELEEMOVEMENTDELAY from sphere.ini (ms). When > 0,
    /// melee swings are blocked for this duration after the last movement step.</summary>
    public static int CombatMeleeMovementDelay { get; set; }
    /// <summary>MAGICFLAGS bitfield from sphere.ini.</summary>
    public static int MagicFlags { get; set; }
    /// <summary>EQUIPPEDCAST from sphere.ini — allow casting with weapons equipped.</summary>
    public static bool EquippedCastEnabled { get; set; }
    public static bool ReagentLossAbort { get; set; }
    public static bool ReagentLossFail { get; set; }
    public static bool ManaLossAbort { get; set; }
    public static bool ManaLossFail { get; set; }
    public static int ManaLossPercent { get; set; } = 100;
    /// <summary>Combat retreat distance in tiles. sphere.ini MAPVIEWRADAR (0 = MapViewSize).</summary>
    public static int MapViewRadarTiles { get; set; } = 18;
    /// <summary>Attacker inactivity timeout in seconds. 0 = disabled. sphere.ini ATTACKERTIMEOUT.</summary>
    public static int AttackerTimeoutSeconds { get; set; }
    // Regeneration delays in SECONDS to recover one point (Source-X
    // CServerConfig m_iRegenRate, REGEN0=STR/hits, REGEN1=INT/mana, REGEN2=DEX/stam).
    public static int RegenHitsSeconds { get; set; } = 40;
    public static int RegenManaSeconds { get; set; } = 20;
    public static int RegenStamSeconds { get; set; } = 10;
    /// <summary>Global food decay: seconds per hunger point (Source-X REGEN3, default 60 min).</summary>
    public static int RegenFoodSeconds { get; set; } = 3600;

    /// <summary>Refresh notoriety for nearby clients after memory changes (NotoSave_Update).</summary>
    public static Action<Character>? NotoSaveUpdate { get; set; }
    /// <summary>Send a system message to the character's client (cowardice, etc.).</summary>
    public static Action<Character, string>? SendOwnerMessage { get; set; }
    /// <summary>Fired when a delayed skill is aborted (movement, etc.). Arg: skill index.</summary>
    public static Action<Character, int>? ActiveSkillAborted { get; set; }
    /// <summary>Fired when HP loss should cancel client-owned action state that
    /// is not stored on Character (currently the crafting stroke loop).</summary>
    public static Action<Character>? OnDamageActionInterrupt { get; set; }

    /// <summary>Fired once per stealth movement step (Source-X @StepStealth).</summary>
    public static Action<Character>? OnStepStealth { get; set; }

    /// <summary>Fired before a kill applies a Fame delta (Source-X @FameChange).
    /// Arg: proposed delta. Return null to cancel the change, or the (possibly
    /// script-adjusted) delta to apply.</summary>
    public static Func<Character, int, int?>? OnFameChanging { get; set; }

    /// <summary>Fired before a kill applies a Karma delta (Source-X @KarmaChange).
    /// Arg: proposed delta. Return null to cancel the change, or the (possibly
    /// script-adjusted) delta to apply.</summary>
    public static Func<Character, int, int?>? OnKarmaChanging { get; set; }

    /// <summary>Fired before EXP changes (Source-X @ExpChange). Arg: proposed
    /// delta. Return null to cancel the change, or the (possibly
    /// script-adjusted) delta to apply.</summary>
    public static Func<Character, int, int?>? OnExpChanging { get; set; }

    /// <summary>Fired after a level threshold crossing changed the level
    /// (Source-X @ExpLevelChange). Arg: the new level.</summary>
    public static Action<Character, short>? OnExpLevelChanged { get; set; }

    /// <summary>Decision returned from the @MurderMark hook: the final kill count
    /// (null = block the mark + criminal flag entirely), and whether the kill also
    /// arms the temporary criminal flag (Source-X ARGN2 make-criminal toggle).</summary>
    public readonly record struct MurderMarkDecision(int? Count, bool MakeCriminal);

    /// <summary>Fired before a player-vs-player kill records a murder count
    /// (Source-X @MurderMark). Args: killer, victim, proposed new murder count.
    /// Returns the recorded count + whether to arm the criminal flag.</summary>
    public static Func<Character, Character, int, MurderMarkDecision>? OnMurderMark { get; set; }

    /// <summary>Fired when a new attacker first enters this character's combat
    /// (attacker) list (Source-X @CombatAdd). Arg: attacker UID.</summary>
    public static Action<Character, Serial>? OnCombatAdd { get; set; }

    /// <summary>Fired when an attacker is removed from the combat list
    /// (Source-X @CombatDelete). Arg: removed attacker UID.</summary>
    public static Action<Character, Serial>? OnCombatDelete { get; set; }

    /// <summary>Fired when the last attacker leaves and combat ends — the
    /// attacker list transitions to empty (Source-X @CombatEnd).</summary>
    public static Action<Character>? OnCombatEnd { get; set; }

    /// <summary>Fired when one murder count decays off (Source-X @MurderDecay).
    /// Args: self, the new (decremented) kill count. Returns the seconds until the
    /// NEXT decay (ARGN2 readback; 0 = engine default MurderDecayTimeSeconds).</summary>
    public static Func<Character, int, int>? OnMurderDecay { get; set; }

    /// <summary>Overrides the notoriety byte a viewer sees of a subject
    /// (Source-X @NotoSend). Args: viewer, subject, computed noto. Returns the
    /// final noto. Installed ONLY when @NotoSend is actually hooked (IsTrigUsed
    /// gate), so the hot ComputeNotoriety path pays just a null check otherwise.</summary>
    public static Func<Character, Character, byte, byte>? OnNotoSend { get; set; }

    /// <summary>Resolve my full notoriety flag as seen by a viewer — Source-X
    /// Noto_GetFlag, used by the &lt;NOTOGETFLAG uid&gt; script property. Args:
    /// subject (me), viewer. Wired to GameClient.ComputeNotoriety.</summary>
    public static Func<Character, Character, byte>? ResolveNotoFlag { get; set; }
    public static Action<Character, string, ITextConsole>? OnScriptSpellEffect { get; set; }

    /// <summary>Fired when a moving character shoves past another character's tile
    /// (Source-X @PersonalSpace). Args: mover, the character being pushed past.</summary>
    public static Action<Character, Character>? OnPersonalSpace { get; set; }

    /// <summary>Fired when a temporary spell effect/buff is applied to a character
    /// (Source-X @EffectAdd). Args: target, spell id. Installed only when hooked
    /// (IsTrigUsed gate), so applying a buff is a null check otherwise.</summary>
    public static Action<Character, int>? OnEffectAdd { get; set; }

    /// <summary>Fired before hidden/invisible state is dropped (Source-X
    /// @Reveal). Return false to keep the character concealed. Installed only
    /// when hooked (IsTrigUsed gate), so reveals are a null check otherwise.</summary>
    public static Func<Character, bool>? OnRevealing { get; set; }

    /// <summary>Fired when a timed spell effect is applied to a character
    /// (Source-X @SpellEffectAdd). Args: target, caster (null = unattributed),
    /// spell id. Installed only when hooked (IsTrigUsed gate).</summary>
    public static Action<Character, Character?, int>? OnSpellEffectAdd { get; set; }

    /// <summary>Fired when a timed spell effect is removed from a character —
    /// expiry, re-cast refresh or death cleanup, NOT the transient save-time
    /// revert (Source-X @SpellEffectRemove). Args: target, spell id. Installed
    /// only when hooked (IsTrigUsed gate).</summary>
    public static Action<Character, int>? OnSpellEffectRemove { get; set; }

    /// <summary>Fired before each periodic spell-effect tick applies (Source-X
    /// @SpellEffectTick / SPELLFLAG_TICK). The wiring seeds the script LOCAL
    /// contract from the context, fires the trigger and writes script
    /// overrides back into it. Return false to destroy the effect (cure)
    /// without applying this tick. Installed only when hooked (IsTrigUsed
    /// gate), so the per-tick cost is a null check otherwise.</summary>
    public static Func<Character, SpellEffectTickContext, bool>? OnSpellEffectTick { get; set; }

    /// <summary>Fired when a pet's loyalty reaches zero and it is about to go wild
    /// (Source-X @PetDesert). Args: pet, owner (may be null). Return true to cancel
    /// the desertion — the pet keeps serving.</summary>
    public static Func<Character, Character?, bool>? OnPetDesert { get; set; }

    /// <summary>Script NEWNPC verb (Source-X SSV_NEWNPC). Args: invoker,
    /// chardef name/id → the spawned NPC (null on unknown def). Wired to the
    /// client spawn pipeline in Program.</summary>
    public static Func<Character, string, Character?>? SpawnNpcFromScript { get; set; }

    /// <summary>Source-X SKILL verb bridge to the host-owned SkillHandlers
    /// instance. Kept as a hook to avoid making Character own world services.</summary>
    public static Func<Character, SkillType, bool>? OnScriptSkillUse { get; set; }

    /// <summary>Fired when a character is sent to jail (Source-X @Jail). Args:
    /// jailed character, sentence minutes (0 = indefinite).</summary>
    public static Action<Character, int>? OnJailed { get; set; }

    /// <summary>Fired when a memory item is equipped on a character (Source-X item
    /// @MemoryEquip). Arg: the memory item. Installed only when hooked (item
    /// IsTrigUsed gate), so the frequent combat-memory path is a null check
    /// otherwise.</summary>
    public static Action<Item>? OnMemoryEquip { get; set; }

    /// <summary>Fired when a character's perceived light level changes — e.g.
    /// crossing a surface/dungeon boundary (Source-X @EnvironChange). Arg: the new
    /// light level. Driven by <see cref="UpdateEnvironLight"/>.</summary>
    public static Action<Character, int>? OnEnvironChange { get; set; }

    // Last perceived light level for @EnvironChange detection (-1 = no baseline yet).
    private int _lastEnvironLight = -1;
    private int _lastEnvironWeather = -1;
    private int _lastEnvironSeason = -1;

    /// <summary>Fired before a "quick" (instant, no-delay) skill check resolves
    /// (Source-X @SkillUseQuick). Args: character, skill id, difficulty. Return true
    /// to cancel the use entirely (no roll, no gain — treated as a failure).
    /// Installed only when hooked (IsTrigUsed gate).</summary>
    /// <summary>@SkillUseQuick hook, fired AFTER the success roll. Args: char,
    /// skillId, difficulty, result (1=success/0=fail). Returns the final result
    /// (the script may flip it) or a negative value to cancel the use.</summary>
    public static Func<Character, int, int, int, int>? OnSkillUseQuick { get; set; }

    public delegate int SkillUseQuickDetailedHook(Character ch, int skillId,
        ref int difficulty, int result);
    /// <summary>Full Source-X hook contract. Return a negative value to cancel
    /// the use without gain, otherwise return the final 1/0 result; difficulty
    /// is writable through ARGN2.</summary>
    public static SkillUseQuickDetailedHook? OnSkillUseQuickDetailed { get; set; }

    /// <summary>Fired when an NPC perceives a player it has not seen recently
    /// (Source-X @NPCSeeNewPlayer). Args: the NPC, the newly-seen player. Installed
    /// only when hooked (IsTrigUsed gate), so the perception scan is free otherwise.</summary>
    public static Action<Character, Character>? OnNpcSeeNewPlayer { get; set; }

    // Per-NPC memory of recently-seen players (player uid -> last-seen tick) for
    // @NPCSeeNewPlayer first-sight detection.
    private Dictionary<uint, long>? _seenPlayers;

    /// <summary>Record that this NPC perceives <paramref name="player"/> and fire
    /// @NPCSeeNewPlayer (via <see cref="OnNpcSeeNewPlayer"/>) when it is a NEW
    /// sighting — the player was not seen within the last <paramref name="ttlMs"/>.
    /// Returns whether it was a new sighting.</summary>
    public bool SeeNewPlayer(Character player, long nowMs, long ttlMs = 60_000)
    {
        _seenPlayers ??= new();
        uint uid = player.Uid.Value;
        bool isNew = !_seenPlayers.TryGetValue(uid, out long last) || nowMs - last > ttlMs;
        _seenPlayers[uid] = nowMs;
        if (!isNew) return false;

        // Prune stale entries opportunistically so the table can't grow unbounded.
        if (_seenPlayers.Count > 32)
        {
            List<uint>? stale = null;
            foreach (var kv in _seenPlayers)
                if (nowMs - kv.Value > ttlMs) (stale ??= []).Add(kv.Key);
            if (stale != null)
                foreach (var k in stale) _seenPlayers.Remove(k);
        }

        OnNpcSeeNewPlayer?.Invoke(this, player);
        return true;
    }

    /// <summary>Record the character's current perceived light level and fire
    /// @EnvironChange (via <see cref="OnEnvironChange"/>) when it actually changes.
    /// The first call only establishes the baseline (no fire), so entering the
    /// world does not spuriously trigger an environment change.</summary>
    public void UpdateEnvironLight(int light)
    {
        UpdateEnvironment(light,
            _lastEnvironWeather >= 0 ? _lastEnvironWeather : 0,
            _lastEnvironSeason >= 0 ? _lastEnvironSeason : 0);
    }

    /// <summary>Source-X CSector environment snapshot. A change in sector light,
    /// weather or season fires @EnvironChange once; the first observation only
    /// establishes the login baseline.</summary>
    public void UpdateEnvironment(int light, int weather, int season)
    {
        bool hasBaseline = _lastEnvironLight >= 0 && _lastEnvironWeather >= 0 && _lastEnvironSeason >= 0;
        bool changed = hasBaseline && (light != _lastEnvironLight ||
            weather != _lastEnvironWeather || season != _lastEnvironSeason);
        _lastEnvironLight = light;
        _lastEnvironWeather = weather;
        _lastEnvironSeason = season;
        if (changed)
            OnEnvironChange?.Invoke(this, light);
    }

    /// <summary>TickCount64 of the last successful move — archery movement delay gate.</summary>
    public long LastMoveTick { get; set; }

    // Last weapon wielded, for the EquipLastWeapon client macro (0xD7 0x1E).
    // Runtime-only (Source-X keeps m_uidWeaponLast in RAM), not persisted.
    private Serial _lastWeaponUid;
    /// <summary>Uid of the last weapon this character equipped (0xD7 EquipLastWeapon).</summary>
    public Serial LastWeaponUid => _lastWeaponUid;

    // Regen timers (ms)
    private long _nextHitRegen;
    private long _nextManaRegen;
    private long _nextStamRegen;
    private long _nextFoodDecay;

    // Per-char regen RATE overrides in ms (Source-X CChar REGENHITS/MANA/STAM/FOOD).
    // 0 = fall back to the global rate; > 0 = per-char ms; < 0 = never regen this stat.
    // Mirrors CChar::Stats_GetRegenRate (CCharStat.cpp:586).
    private long _regenHitsRateMs;
    private long _regenManaRateMs;
    private long _regenStamRateMs;
    private long _regenFoodRateMs;

    /// <summary>Per-char hit regen rate in ms (0 = use the global rate). Persisted.</summary>
    public long RegenHitsRateMs => _regenHitsRateMs;
    /// <summary>Per-char mana regen rate in ms (0 = use the global rate). Persisted.</summary>
    public long RegenManaRateMs => _regenManaRateMs;
    /// <summary>Per-char stam regen rate in ms (0 = use the global rate). Persisted.</summary>
    public long RegenStamRateMs => _regenStamRateMs;
    /// <summary>Per-char food decay rate in ms (0 = use the global rate). Persisted.</summary>
    public long RegenFoodRateMs => _regenFoodRateMs;

    // Per-char regen AMOUNT override (Source-X CChar m_regenVal, points recovered
    // per regen event; effective value is max(1, stored)). 0 = the default of 1.
    private ushort _regenValHits;
    private ushort _regenValMana;
    private ushort _regenValStam;
    private ushort _regenValFood;
    /// <summary>Per-char points recovered per hit-regen event (0 = default 1). Persisted.</summary>
    public ushort RegenValHits => _regenValHits;
    /// <summary>Per-char points recovered per mana-regen event (0 = default 1). Persisted.</summary>
    public ushort RegenValMana => _regenValMana;
    /// <summary>Per-char points recovered per stam-regen event (0 = default). Persisted.</summary>
    public ushort RegenValStam => _regenValStam;
    /// <summary>Per-char food points lost per decay event (0 = default 1). Persisted.</summary>
    public ushort RegenValFood => _regenValFood;
    private ushort _food = 40; // 0-60, hunger level (Sphere style)
    private string _title = ""; // Paperdoll title (Sphere: src.title)
    private bool _allShow; // Runtime-only GM flag, not saved
    private bool _allMove; // Runtime-only GM flag: bypass collision when walking. Not saved.
    private bool _isReplaySpectator;
    private bool _privShow; // Runtime-only: show priv level tag above head
    private bool _isOnline; // Has active client connection
    private int _skillClass = 0;

    public override bool IsDeleted => _isDeleted;
    public bool IsPlayer { get => _isPlayer; set => _isPlayer = value; }

    /// <summary>Wall-clock creation time (unix seconds). Persisted as the
    /// Source-X CREATE key (age in tenths of a second); 0 = unknown (legacy
    /// save), treated as old enough for the MinCharDeleteTime gate.</summary>
    public long CreatedUtcSeconds { get; set; }

    /// <summary>True when a GameClient is actively controlling this character.</summary>
    public bool IsOnline { get => _isOnline; set => _isOnline = value; }

    /// <summary>True while a disconnected character remains attackable in the
    /// world for the configured client-linger period.</summary>
    public bool IsClientLingering =>
        TryGetTag("CLIENT_LINGER_UNTIL", out string? value) &&
        long.TryParse(value, out _);
    public int SkillClass { get => _skillClass; set => _skillClass = Math.Max(0, value); }
    public string Title { get => _title; set => _title = value ?? ""; }

    /// <summary>GM AllShow mode — shows invisible objects with a grey hue. Runtime-only, not persisted.</summary>
    public bool AllShow { get => _allShow; set => _allShow = value; }

    /// <summary>GM AllMove mode — bypass walk collision (walls, statics, mobiles).
    /// Requires PrivLevel.GM+ to be honored. Runtime-only, not persisted.</summary>
    public bool AllMove { get => _allMove; set => _allMove = value; }

    public bool IsReplaySpectator { get => _isReplaySpectator; set => _isReplaySpectator = value; }

    // --- Stats ---
    public short Str
    {
        get => _str;
        set
        {
            if (value < 0) value = 0;
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
            if (value < 0) value = 0;
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
            if (value < 0) value = 0;
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
            int effMax = CombatEngine.EffectiveMaxHits(this);
            short cap = effMax > 0 ? (short)Math.Min(short.MaxValue, effMax) : (short)Math.Max((int)1, (int)_str);
            var v = Math.Clamp(value, (short)0, cap);
            if (v != _hits)
            {
                bool tookDamage = v < _hits;
                _hits = v;
                MarkDirty(DirtyFlag.Stats);
                if (tookDamage)
                {
                    int abortedSkill = ClearActiveSkillPending();
                    if (abortedSkill >= 0)
                        ActiveSkillAborted?.Invoke(this, abortedSkill);
                    InterruptMeditation();
                    OnDamageActionInterrupt?.Invoke(this);
                }
            }
        }
    }
    public short Mana
    {
        get => _mana;
        set
        {
            int effMax = CombatEngine.EffectiveMaxMana(this);
            short cap = effMax > 0 ? (short)Math.Min(short.MaxValue, effMax) : (short)Math.Max((int)1, (int)_int);
            var v = Math.Clamp(value, (short)0, cap);
            if (v != _mana) { _mana = v; MarkDirty(DirtyFlag.Stats); }
        }
    }
    public short Stam
    {
        get => _stam;
        set
        {
            int effMax = CombatEngine.EffectiveMaxStam(this);
            short cap = effMax > 0 ? (short)Math.Min(short.MaxValue, effMax) : (short)Math.Max((int)1, (int)_dex);
            var v = Math.Clamp(value, (short)0, cap);
            if (v != _stam) { _stam = v; MarkDirty(DirtyFlag.Stats); }
        }
    }
    // Max-pool getters report the EFFECTIVE pool (base field + equipped
    // BONUSHITSMAX/BONUSMANAMAX/BONUSSTAMMAX, derived on read via CombatEngine).
    // Setters write the BASE field only; the base is what persists (see BaseMax*).
    public short MaxHits { get => (short)Math.Min(short.MaxValue, CombatEngine.EffectiveMaxHits(this)); set { var v = Math.Max((short)1, value); if (v != _maxHits) { _maxHits = v; if (_hits > _maxHits) _hits = _maxHits; MarkDirty(DirtyFlag.Stats); } } }
    public short MaxMana { get => (short)Math.Min(short.MaxValue, CombatEngine.EffectiveMaxMana(this)); set { var v = Math.Max((short)0, value); if (v != _maxMana) { _maxMana = v; if (_mana > _maxMana) _mana = _maxMana; MarkDirty(DirtyFlag.Stats); } } }
    public short MaxStam { get => (short)Math.Min(short.MaxValue, CombatEngine.EffectiveMaxStam(this)); set { var v = Math.Max((short)0, value); if (v != _maxStam) { _maxStam = v; if (_stam > _maxStam) _stam = _maxStam; MarkDirty(DirtyFlag.Stats); } } }

    // Raw base max pools (no suit bonus). Used by the world-save path so the suit
    // contribution is never persisted as base, and by the effective-read helpers.
    public short BaseMaxHits => _maxHits;
    public short BaseMaxMana => _maxMana;
    public short BaseMaxStam => _maxStam;

    public ushort BodyId { get => _bodyId; set { _bodyId = value; MarkDirty(DirtyFlag.Body); } }

    /// <summary>True for the standard UO female bodies (human/elf/gargoyle),
    /// used to pick the gendered get-hit/death vocalizations.</summary>
    public bool IsFemale => _bodyId == 0x0191 || _bodyId == 0x025E || _bodyId == 0x029B;
    public bool IsHuman => _bodyId is 0x0190 or 0x0191 or 0x0192 or 0x0193;
    public bool IsGargoyle => _bodyId is 0x029A or 0x029B or 0x02B6 or 0x02B7;

    /// <summary>Player-facing name (overhead label, corpse, tooltips).
    /// Falls back to CHARDEF NAME when the runtime name is blank or
    /// still an unresolved template.</summary>
    public override string GetName() => GetDisplayName();

    public string GetDisplayName()
    {
        string raw = (Name ?? "").Trim();
        if (!string.IsNullOrEmpty(raw) && !raw.Contains('#'))
            return raw;

        var def = DefinitionLoader.GetCharDef(_charDefIndex != 0 ? _charDefIndex : CharDefIndex);
        if (def != null && !string.IsNullOrWhiteSpace(def.Name))
        {
            string fromDef = DefinitionLoader.ResolveNames(def.Name).Trim();
            if (!string.IsNullOrEmpty(fromDef))
                return fromDef;
        }

        return string.IsNullOrEmpty(raw) ? "creature" : raw;
    }

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
    public Direction Direction
    {
        get => _direction;
        set
        {
            var masked = (Direction)((byte)value & 0x07);
            if (masked != _direction)
            {
                _direction = masked;
                MarkDirty(DirtyFlag.Direction);
            }
        }
    }
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
    /// <summary>Transient AR supplied by the active Protection spell-memory.
    /// Kept separate from MODAR so save-time effect reversion cannot persist
    /// the temporary bonus as a permanent character modifier.</summary>
    internal int ProtectionArmor { get; set; }
    /// <summary>Transient LAYER_SPELL_Polymorph marker used by the Horrific
    /// Beast combat modifiers.</summary>
    internal bool HorrificBeastActive { get; set; }
    /// <summary>Transient LAYER_SPELL_Polymorph marker for Necromancy Wraith
    /// Form: while set, damaging melee hits drain the target's mana to the
    /// attacker (reference CCharFight.cpp:2299-2304).</summary>
    internal bool WraithFormActive { get; set; }
    /// <summary>Transient LAYER_SPELL_Curse_Weapon level (reference
    /// m_itSpell.m_spelllevel). Added to the attacker's HITLEECHLIFE percent
    /// while a weapon is equipped (reference CCharFight.cpp:2272-2275).</summary>
    internal int CurseWeaponLevel { get; set; }
    /// <summary>Transient Necromancy Mind Rot marker: while set, the victim's
    /// spell mana cost is raised (reference LOWERMANACOST -10 =&gt; +10%).</summary>
    internal bool MindRotActive { get; set; }
    /// <summary>Transient Necromancy Lich Form marker (reference SPELL_Lich_Form):
    /// a LAYER_SPELL_Polymorph form that shifts elemental resists.</summary>
    internal bool LichFormActive { get; set; }
    /// <summary>Transient Necromancy Vampiric Embrace marker (reference
    /// SPELL_Vampiric_Embrace): a form that leeches life on hit.</summary>
    internal bool VampiricEmbraceActive { get; set; }
    /// <summary>Necromancy Blood Oath: the bonded enemy whose blows against this
    /// character are reflected (reference LAYER_SPELL_Blood_Oath m_uidLink), and
    /// the reflect percent base (reference m_spelllevel).</summary>
    internal Serial BloodOathEnemy { get; set; }
    internal int BloodOathLevel { get; set; }
    /// <summary>Necromancy Evil Omen: a one-shot marker that makes the next
    /// harmful effect on this character land harder, then is consumed (reference
    /// LAYER_SPELL_Evil_Omen). Expiry is lazy — read via <see cref="ConsumeEvilOmen"/>.</summary>
    internal bool EvilOmenActive { get; set; }
    internal long EvilOmenExpireTick { get; set; }

    /// <summary>Consume the Evil Omen marker: returns true (and clears it) only
    /// when it is set and unexpired; an expired marker is cleared and returns
    /// false. Reference: the omen memory is deleted on the first harmful hit.</summary>
    internal bool ConsumeEvilOmen()
    {
        if (!EvilOmenActive)
            return false;
        bool live = Environment.TickCount64 < EvilOmenExpireTick;
        EvilOmenActive = false;
        return live;
    }
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

    /// <summary>Experience needed for the first level (sphere.ini LEVELNEXTAT
    /// equivalent). 0 disables the level system entirely.</summary>
    public static int LevelNextAt { get; set; } = 1000;
    /// <summary>When true each level step costs double the previous one
    /// (Source-X LEVEL_MODE_DOUBLE); false = linear steps.</summary>
    public static bool LevelModeDouble { get; set; } = true;

    /// <summary>
    /// Change experience through the trigger pipeline (Source-X
    /// CChar::ChangeExperience): @ExpChange may adjust or cancel the delta;
    /// crossing a level threshold fires @ExpLevelChange with the new level.
    /// </summary>
    public void ChangeExperience(int delta)
    {
        if (delta != 0 && OnExpChanging != null)
        {
            int? adjusted = OnExpChanging(this, delta);
            if (adjusted == null)
                return;
            delta = adjusted.Value;
        }
        if (delta == 0)
            return;

        _exp = (int)Math.Clamp((long)_exp + delta, 0, int.MaxValue);

        short newLevel = ComputeLevel(_exp);
        if (newLevel != _level)
        {
            _level = newLevel;
            OnExpLevelChanged?.Invoke(this, newLevel);
        }
    }

    /// <summary>Level for a total experience value: linear steps of
    /// <see cref="LevelNextAt"/>, or doubling steps in double mode.</summary>
    public static short ComputeLevel(int exp)
    {
        if (LevelNextAt <= 0)
            return 0;
        short level = 0;
        long threshold = 0, step = LevelNextAt;
        while (level < short.MaxValue)
        {
            threshold += step;
            if (exp < threshold || threshold > int.MaxValue)
                break;
            level++;
            if (LevelModeDouble)
                step *= 2;
        }
        return level;
    }

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

    /// <summary>How many follower/control slots this creature occupies when
    /// owned (from CHARDEF FOLLOWERSLOTS; large creatures cost several). Minimum 1.
    /// A script-set FOLLOWERSLOTS override (Source-X SetDefNum) wins when present.</summary>
    public int ControlSlots =>
        _followerSlotsOverride ?? Math.Max(1, DefinitionLoader.GetCharDef(CharDefIndex)?.FollowerSlots ?? 1);
    private int? _followerSlotsOverride;

    // Blood splatter hue (Source-X CChar::_wBloodHue). 0 = default. Persisted when set.
    private ushort _bloodHue;
    /// <summary>Blood splatter hue override (0 = default).</summary>
    public ushort BloodHue => _bloodHue;
    /// <summary>Per-char follower-slot override, or null to use the chardef value.</summary>
    public int? FollowerSlotsOverride => _followerSlotsOverride;

    public byte CurFollower
    {
        get
        {
            var world = ResolveWorld?.Invoke();
            if (world == null)
                return _curFollower;

            // The recompute below walks every world object; on large worlds
            // that is a full 100K+ scan per read, and status packets (0x34)
            // read this on every open bar refresh. Serve a short-lived cache;
            // ownership changes invalidate it via InvalidateFollowerCount().
            long nowMs = Environment.TickCount64;
            if (_curFollowerScanMs != 0 && nowMs - _curFollowerScanMs < CurFollowerCacheMs)
                return _curFollower;

            int count = 0;
            foreach (var creature in world.GetAllCharactersSnapshot())
            {
                if (creature == this || creature.IsDeleted)
                    continue;
                if (!creature.HasOwner(this.Uid))
                    continue;
                if (creature.IsStatFlag(StatFlag.Ridden))
                    continue;
                // Sum each pet's control-slot cost, not a flat 1 — otherwise five
                // multi-slot creatures (e.g. dragons) all fit under a 5 cap.
                count += creature.ControlSlots;
            }

            _curFollower = (byte)Math.Clamp(count, 0, byte.MaxValue);
            _curFollowerScanMs = nowMs;
            return _curFollower;
        }
        set => _curFollower = value;
    }

    private const int CurFollowerCacheMs = 2000;
    private long _curFollowerScanMs; // 0 = cache dirty, next read rescans

    /// <summary>Force the next CurFollower read to rescan the world
    /// (called when a pet's ownership changes).</summary>
    public void InvalidateFollowerCount() => _curFollowerScanMs = 0;

    /// <summary>Invalidate the follower-count cache of this pet's owner —
    /// mounting/dismounting (Ridden) changes what the owner's scan counts.</summary>
    private void InvalidateOwnerFollowerCount()
    {
        if (!_npcMaster.IsValid) return;
        var world = ResolveWorld?.Invoke();
        world?.FindChar(_npcMaster)?.InvalidateFollowerCount();
    }

    // Resist caps
    public short ResFireMax { get => _resFireMax; set => _resFireMax = value; }
    public short ResPhysicalMax { get => _resPhysicalMax; set => _resPhysicalMax = value; }
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
    public byte PoisonLevel { get => Poison.Level; set => Poison.Level = value; }
    public bool IsPoisoned => Poison.IsPoisoned;
    public short Kills { get => CombatState.Kills; set => CombatState.Kills = value; }
    public bool IsCriminal => CombatState.IsCriminal;
    public bool IsMurderer => CombatState.IsMurderer;
    /// <summary>True when the character should be treated as a criminal for heal/help checks.</summary>
    public bool IsFlaggedAsCriminal => IsCriminal || IsMurderer || IsStatFlag(StatFlag.Criminal);

    /// <summary>If HELPINGCRIMINALSISACrime is enabled, flag the helper criminal when aiding a criminal.</summary>
    public void FlagForHelpingCriminalIfNeeded(Character beneficiary)
    {
        if (!HelpingCriminalsIsACrimeEnabled || !IsPlayer || beneficiary == this)
            return;
        if (beneficiary.IsFlaggedAsCriminal)
            MakeCriminal();
    }

    /// <summary>Clear in-progress delayed skill state. Returns skill id if one was pending.</summary>
    public int ClearActiveSkillPending()
    {
        if (_skillPendingId < 0)
            return -1;

        int skillId = _skillPendingId;
        _skillPendingId = -1;
        _skillDelayEnd = 0;
        _skillStrokeNext = 0;
        _skillStrokeCount = 0;
        _skillPendingTarget = Serial.Invalid;
        _hasSkillPendingPoint = false;
        _skillPendingIsInfo = false;
        return skillId;
    }

    public bool HasActiveSkillPending() => _skillPendingId >= 0;

    public bool InterruptMeditation()
    {
        if (!IsStatFlag(StatFlag.Meditation))
            return false;
        ClearStatFlag(StatFlag.Meditation);
        ActiveSkillAborted?.Invoke(this, (int)SkillType.Meditation);
        return true;
    }

    /// <summary>Drop hidden/invisible/stealth-walk state (cast, combat, step
    /// expiry). Runs the @Reveal trigger first (Source-X CChar::Reveal); a
    /// script returning 1 keeps the character concealed. Returns true when
    /// the state was actually dropped.</summary>
    public bool ClearHiddenState()
    {
        if (!IsStatFlag(StatFlag.Hidden) && !IsStatFlag(StatFlag.Invisible) && StepStealth == 0)
            return false;
        if (OnRevealing != null && !OnRevealing(this))
            return false;
        bool wasInvisible = IsStatFlag(StatFlag.Invisible);
        ClearStatFlag(StatFlag.Hidden);
        ClearStatFlag(StatFlag.Invisible);
        StepStealth = 0;
        if (wasInvisible)
            OnHiddenStateCleared?.Invoke(this);
        return true;
    }

    /// <summary>Mark this character criminal (gray) and arm the decay timer. Called
    /// by HandleAttack, snooping, theft, etc. Overwrites any existing timer (i.e.
    /// a fresh crime refreshes the countdown, matching Source-X behaviour).</summary>
    public void MakeCriminal()
    {
        // @Criminal trigger — RETURN 1 (null) cancels the flag; a returned
        // duration (ARGN1) overrides the default criminal-timer seconds.
        long durationMs = CriminalTimerSeconds * 1000L;
        if (OnCriminalCheck != null)
        {
            int? decision = OnCriminalCheck(this);
            if (decision == null) return;
            if (decision.Value > 0) durationMs = decision.Value * 1000L;
        }
        SetStatFlag(StatFlag.Criminal);
        CombatState.SetCriminal(durationMs);
    }

    /// <summary>Called once per world tick. Clears expired criminal flag and
    /// decays one kill every MurderDecayTimeSeconds of online time.</summary>
    public void TickNotorietyDecay(long nowMs) => CombatState.TickNotorietyDecay(nowMs);

    /// <summary>Apply poison to this character. Level: 1=lesser, 2=normal, 3=greater, 4=deadly, 5=lethal.</summary>
    public void ApplyPoison(byte level) => ApplyPoison(level, Serial.Invalid);

    public void ApplyPoison(byte level, Serial source) => Poison.Apply(level, source);

    /// <summary>Typed field touch (SpellEngine.ApplyFieldTouch, wired by the
    /// host): fire damages, poison poisons, paralyze freezes, barriers are
    /// inert. Returns true when the spell engine handled the touch.</summary>
    public static Func<Character, Item, bool>? FieldTouchHook;

    /// <summary>Apply damage from any fire/poison field on the character's
    /// current tile. Sector-indexed lookup, so this is cheap per tick.</summary>
    private void ApplyStandingFieldDamage()
    {
        if (IsDead) return;
        var world = ResolveWorld?.Invoke();
        if (world == null) return;

        foreach (var item in world.GetItemsInRange(Position, 0))
        {
            if (item.IsDeleted) continue;
            if (item.Position.X != X || item.Position.Y != Y) continue;
            if (FieldTouchHook != null && item.TryGetTag("FIELD_SPELL", out _))
            {
                if (FieldTouchHook(this, item))
                    return; // one field tick per cycle is enough
                continue;
            }
            if (!item.TryGetTag("FIELD_DAMAGE", out string? fdStr) ||
                !int.TryParse(fdStr, out int dmg) || dmg <= 0)
                continue;

            Character? caster = null;
            if (item.TryGetTag("FIELD_CASTER", out string? cStr) &&
                uint.TryParse(cStr, out uint cuid))
                caster = ResolveCharByUid?.Invoke(new Serial(cuid));

            Hits = (short)Math.Max(0, Hits - dmg);
            if (caster != null && caster != this)
                RecordAttack(caster.Uid, dmg);

            BroadcastDamageNearby?.Invoke(Position, 18, Uid.Value, dmg, 0);
            BroadcastNearby?.Invoke(Position, 18,
                new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                    Uid.Value, MaxHits, Hits), 0);

            if (Hits <= 0 && !IsDead)
            {
                if (OnLifecycleKill != null) OnLifecycleKill(this, caster);
                else Kill();
            }
            return; // one field tick per cycle is enough
        }
    }

    /// <summary>Cure poison.</summary>
    public void CurePoison() => Poison.Cure();

    /// <summary>Set criminal timer (duration in ms).</summary>
    public void SetCriminal(long durationMs = 120_000) => CombatState.SetCriminal(durationMs);

    /// <summary>True if this character is serving a timed jail sentence whose
    /// time has expired. The release time is stored in the JAIL_RELEASE tag as
    /// DateTime UTC ticks so it survives server reboots (0 = indefinite, no tag
    /// = not jailed). Returns false for indefinite sentences and non-jailed chars.</summary>
    public bool IsJailExpired()
    {
        if (!TryGetTag("JAIL_RELEASE", out string? tag)) return false;
        if (!long.TryParse(tag, out long releaseUtcTicks)) return false;
        if (releaseUtcTicks <= 0) return false; // indefinite
        return DateTime.UtcNow.Ticks >= releaseUtcTicks;
    }

    /// <summary>Process poison tick. Returns damage dealt, 0 if no tick.</summary>
    public int ProcessPoisonTick(long now) => Poison.ProcessTick(now);

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

    /// <summary>Per-char dynamic SPEECH list (Source-X CChar m_Speech / DSPEECH). These
    /// speech-fragment resources fire — before the CharDef's static SPEECH list — when
    /// this char HEARS speech, letting scripts attach reactions (e.g. town/guild speech
    /// blocks) to individual chars at runtime. Read membership via ISDSPEECH.&lt;name&gt;.</summary>
    public List<ResourceId> DSpeech => _dspeech;

    // Combat fields
    private Serial _fightTarget = Serial.Invalid;
    public Serial FightTarget
    {
        get => _fightTarget;
        set
        {
            // A windup belongs to exactly one fight target. Target switches,
            // combat clears and death must not leave an orphaned pending hit
            // that blocks every future swing.
            if (value != _fightTarget && HasPendingHit && value != PendingHitTarget)
                ClearPendingHit();
            if (value.IsValid && value != _fightTarget)
                InterruptMeditation();
            _fightTarget = value;
        }
    }
    public long NextAttackTime { get; set; }
    public SwingState CombatSwingState { get; private set; } = SwingState.Ready;
    public long CombatSwingStateUntil { get; private set; }
    public long NextNpcActionTime { get; set; }
    /// <summary>Earliest tick at which a target-less NPC may run a full
    /// acquire scan again (throttles the hot FindBestTarget path). Runtime-only.</summary>
    public long NextNpcReacquireTime { get; set; }

    public void SetCombatSwingState(SwingState state, long untilMs = 0)
    {
        CombatSwingState = state;
        CombatSwingStateUntil = untilMs;
    }

    public void RefreshCombatSwingState(long nowMs)
    {
        if (CombatSwingState is SwingState.Swinging or SwingState.Equipping or SwingState.EquippingNoWait &&
            CombatSwingStateUntil > 0 && nowMs >= CombatSwingStateUntil)
        {
            CombatSwingState = SwingState.Ready;
            CombatSwingStateUntil = 0;
        }
    }

    public void BeginSwingRecoil(long nowMs, int delayMs)
    {
        NextAttackTime = nowMs + Math.Max(delayMs, 0);
        SetCombatSwingState(SwingState.Swinging, NextAttackTime);
    }

    // ---- Two-phase swing (Source-X windup -> hit). Runtime-only state. ----

    /// <summary>Tick at which a started swing's hit resolves. 0 = no pending hit.</summary>
    public long SwingHitTime { get; set; }
    /// <summary>The target a started-but-not-yet-resolved swing will hit.</summary>
    public Serial PendingHitTarget { get; set; } = Serial.Invalid;
    /// <summary>Weapon committed by the swing. Prevents equipment swapping
    /// during windup from changing its reach, ammo class and damage.</summary>
    public Serial PendingHitWeapon { get; private set; } = Serial.Invalid;
    public bool PendingHitWeaponCaptured { get; private set; }
    /// <summary>Per-swing @HitCheck LOCAL.Recoil_NoRange decision.</summary>
    public bool PendingHitSwingNoRange { get; private set; }
    /// <summary>Committed effective range (includes an NPC CHARDEF RANGE).</summary>
    public int PendingHitRangeMin { get; private set; } = -1;
    public int PendingHitRangeMax { get; private set; } = -1;
    /// <summary>Latest tick a SWING_NORANGE hit may keep waiting for reach/LoS.</summary>
    public long PendingHitDeadline { get; set; }
    public bool HasPendingHit => PendingHitTarget.IsValid;

    /// <summary>Start a swing's windup: arm the pending hit at <paramref name="hitDelayMs"/>
    /// from now, begin the recoil (next swing at <paramref name="recoilMs"/>), and enter
    /// the Swinging state. With hitDelayMs == 0 the caller resolves the hit immediately
    /// (atomic) — identical to the old <see cref="BeginSwingRecoil"/> path.</summary>
    public void BeginSwingWindup(long nowMs, int hitDelayMs, int recoilMs, Serial targetUid,
        long deadlineMs, Serial? weaponUid = null, bool swingNoRange = false,
        int rangeMin = -1, int rangeMax = -1)
    {
        SwingHitTime = nowMs + Math.Max(hitDelayMs, 0);
        PendingHitTarget = targetUid;
        PendingHitWeaponCaptured = weaponUid.HasValue;
        PendingHitWeapon = weaponUid ?? Serial.Invalid;
        PendingHitSwingNoRange = swingNoRange;
        PendingHitRangeMin = rangeMin;
        PendingHitRangeMax = rangeMax;
        PendingHitDeadline = deadlineMs;
        NextAttackTime = nowMs + Math.Max(recoilMs, 0);
        SetCombatSwingState(SwingState.Swinging, NextAttackTime);
    }

    public void ClearPendingHit()
    {
        PendingHitTarget = Serial.Invalid;
        PendingHitWeapon = Serial.Invalid;
        PendingHitWeaponCaptured = false;
        PendingHitSwingNoRange = false;
        PendingHitRangeMin = -1;
        PendingHitRangeMax = -1;
        SwingHitTime = 0;
        PendingHitDeadline = 0;
    }

    public void BeginEquipSwingWait(long nowMs, int delayMs, bool noWait)
    {
        if (noWait)
        {
            SetCombatSwingState(SwingState.EquippingNoWait, nowMs);
            if (NextAttackTime == 0 || NextAttackTime > nowMs)
                NextAttackTime = nowMs;
            RefreshCombatSwingState(nowMs);
            return;
        }

        NextAttackTime = Math.Max(NextAttackTime, nowMs + Math.Max(delayMs, 0));
        SetCombatSwingState(SwingState.Equipping, NextAttackTime);
    }

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
    public void SetStatFlag(StatFlag flag)
    {
        var oldFlags = _statFlags;
        _statFlags |= flag;
        MarkDirty(DirtyFlag.StatFlags);
        if ((oldFlags & StatFlag.Hidden) == 0 && (_statFlags & StatFlag.Hidden) != 0)
            OnClientBuffChanged?.Invoke(this, BuffIcon.Hidden, true, 0, null);
        if ((oldFlags & StatFlag.Meditation) == 0 && (_statFlags & StatFlag.Meditation) != 0)
            OnClientBuffChanged?.Invoke(this, BuffIcon.ActiveMeditation, true, 0, null);
        // Source-X raises the criminal-status icon alongside STATF_CRIMINAL
        // (CCharAct.cpp:352, LAYER_FLAG_Criminal).
        if ((oldFlags & StatFlag.Criminal) == 0 && (_statFlags & StatFlag.Criminal) != 0)
            OnClientBuffChanged?.Invoke(this, BuffIcon.CriminalStatus, true, 0, null);
        // Dying changes what this character is allowed to see, so its own view
        // has to be rebuilt rather than waiting for the next step.
        if ((oldFlags & StatFlag.Dead) == 0 && (_statFlags & StatFlag.Dead) != 0)
            OnOwnViewRefreshNeeded?.Invoke(this);
        if ((oldFlags & StatFlag.Ridden) == 0 && (_statFlags & StatFlag.Ridden) != 0)
            InvalidateOwnerFollowerCount();
    }

    public void ClearStatFlag(StatFlag flag)
    {
        var oldFlags = _statFlags;
        _statFlags &= ~flag;
        MarkDirty(DirtyFlag.StatFlags);
        if ((oldFlags & StatFlag.Hidden) != 0 && (_statFlags & StatFlag.Hidden) == 0)
            OnClientBuffChanged?.Invoke(this, BuffIcon.Hidden, false, 0, null);
        if ((oldFlags & StatFlag.Meditation) != 0 && (_statFlags & StatFlag.Meditation) == 0)
            OnClientBuffChanged?.Invoke(this, BuffIcon.ActiveMeditation, false, 0, null);
        // Source-X CCharAct.cpp:460 — the icon clears with the criminal flag.
        if ((oldFlags & StatFlag.Criminal) != 0 && (_statFlags & StatFlag.Criminal) == 0)
            OnClientBuffChanged?.Invoke(this, BuffIcon.CriminalStatus, false, 0, null);
        // Resurrecting restores sight of everything hidden while dead.
        if ((oldFlags & StatFlag.Dead) != 0 && (_statFlags & StatFlag.Dead) == 0)
            OnOwnViewRefreshNeeded?.Invoke(this);
        if ((oldFlags & StatFlag.Ridden) != 0 && (_statFlags & StatFlag.Ridden) == 0)
            InvalidateOwnerFollowerCount();
    }

    public bool IsDead => IsStatFlag(StatFlag.Dead);
    public bool IsInWarMode => IsStatFlag(StatFlag.War);
    public bool IsInvisible => IsStatFlag(StatFlag.Invisible);
    public bool IsMounted => IsStatFlag(StatFlag.OnHorse);

    public bool ClearTransientVisualState()
    {
        var beforeFlags = _statFlags;
        ushort beforeHue = Hue.Value;

        ClearStatFlag(StatFlag.Freeze);
        ClearStatFlag(StatFlag.Invisible);
        ClearStatFlag(StatFlag.Hidden);
        ClearStatFlag(StatFlag.Reflection);
        ClearStatFlag(StatFlag.Reactive);
        ClearStatFlag(StatFlag.NightSight);

        if (Hue.Value == 0x03EC && !IsStatFlag(StatFlag.Reflection))
            Hue = Core.Types.Color.Default;

        return beforeFlags != _statFlags || beforeHue != Hue.Value;
    }

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

    public bool IsBonded
    {
        get => TryGetTag("BONDED", out string? v) && v == "1";
        set { if (value) SetTag("BONDED", "1"); else RemoveTag("BONDED"); MarkDirty(DirtyFlag.StatFlags); }
    }

    public long BondingStartTick
    {
        get => TryGetTag("BONDING_START", out string? v) && long.TryParse(v, out long t) ? t : 0;
        set { if (value > 0) SetTag("BONDING_START", value.ToString()); else RemoveTag("BONDING_START"); }
    }

    public void TickBonding(long nowTick, long bondingDurationMs = 604800000)
    {
        if (IsBonded || !OwnerSerial.IsValid || !IsStatFlag(StatFlag.Pet)) return;
        long start = BondingStartTick;
        if (start <= 0)
        {
            BondingStartTick = nowTick;
            return;
        }
        if (nowTick - start >= bondingDurationMs)
        {
            IsBonded = true;
            RemoveTag("BONDING_START");
        }
    }

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
            owner.CurFollower + ControlSlots > owner.MaxFollower)
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

    /// <summary>Memory-item subsystem (decomposition slice 3) — the
    /// Memory_* members below delegate so the public API stays unchanged.</summary>
    public CharacterMemoryState MemoryState => _memoryState ??= new CharacterMemoryState(this);
    private CharacterMemoryState? _memoryState;

    public IReadOnlyList<Item> Memories => MemoryState.Items;

    public Item? Memory_FindObj(Serial uid) => MemoryState.FindObj(uid);

    public Item? Memory_FindTypes(MemoryType flags) => MemoryState.FindTypes(flags);

    public Item? Memory_FindObjTypes(Serial uid, MemoryType flags) => MemoryState.FindObjTypes(uid, flags);

    public Item Memory_CreateObj(Serial uid, MemoryType flags) => MemoryState.CreateObj(uid, flags);

    /// <summary>Create the IT_SPELL memory item for an active spell effect
    /// (Source-X Spell_Effect_Create). See <see cref="CharacterMemoryState.CreateSpellEffect"/>.</summary>
    public Item Memory_CreateSpellEffect(int spellId, ushort graphic, int level, Serial source, string name)
        => MemoryState.CreateSpellEffect(spellId, graphic, level, source, name);

    public Item Memory_AddObjTypes(Serial uid, MemoryType flags) => MemoryState.AddObjTypes(uid, flags);

    public void Memory_AddTypes(Item mem, MemoryType flags) => MemoryState.AddTypes(mem, flags);

    public bool Memory_UpdateClearTypes(Item mem, MemoryType flags) => MemoryState.UpdateClearTypes(mem, flags);

    public bool Memory_ClearTypes(Item mem, MemoryType flags) => MemoryState.ClearTypes(mem, flags);

    public void Memory_ClearAllTypes(MemoryType flags) => MemoryState.ClearAllTypes(flags);

    public void Memory_Delete(Item mem) => MemoryState.Delete(mem);

    /// <summary>FINDID item search across worn equipment, the hidden memory
    /// layer and the backpack — Source-X ContentFind covers every equipped
    /// item, spell memories included (the Scripts-X form toggles look up the
    /// form's RUNE_ITEM memory this way). Memories rank BEFORE the pack:
    /// they are equipped items, and a form memory shares its graphic with
    /// the rune scroll — pack-first would let the toggle eat a spare scroll
    /// while the form stays on.</summary>
    private Item? FindItemByBaseId(ushort baseId)
    {
        for (int i = 0; i < _equipment.Length; i++)
            if (_equipment[i] != null && _equipment[i]!.BaseId == baseId)
                return _equipment[i];
        foreach (var mem in Memories)
            if (!mem.IsDeleted && mem.BaseId == baseId)
                return mem;
        var pack = Backpack;
        if (pack != null)
            foreach (var item in pack.Contents)
                if (item.BaseId == baseId)
                    return item;
        return null;
    }

    /// <summary>Resolve a FINDLAYER layer argument to a layer index. Accepts a
    /// plain number or a layer DEFNAME (layer_hair, layer_beard, ...), mirroring
    /// Source-X's expression-evaluated layer arg.</summary>
    private static bool TryResolveLayerToken(string token, out int layerIdx)
    {
        token = token.Trim();
        if (int.TryParse(token, out layerIdx))
            return true;
        if (DefinitionLoader.StaticResources?.TryResolveDefNameValue(token, out long defVal) == true)
        {
            layerIdx = (int)defVal;
            return true;
        }
        layerIdx = -1;
        return false;
    }

    public bool Memory_UpdateFlags(Item mem) => MemoryState.UpdateFlags(mem);

    public bool Memory_OnTick(Item mem) => MemoryState.OnMemoryTick(mem);

    public void Memory_Fight_Start(Character target) => MemoryState.Fight_Start(target);

    public IReadOnlyList<IScriptObj> GetMemoryEntriesByType(string rawType, World.GameWorld? world = null)
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
            "MEMORY_ISPAWNED" => MemoryType.ISpawned,
            "MEMORY_FOLLOW" => MemoryType.Follow,
            "MEMORY_IRRITATEDBY" => MemoryType.IrritatedBy,
            _ => null
        };

        if (filter.HasValue)
        {
            var resolveWorld = world ?? ResolveWorld?.Invoke();
            for (int i = 0; i < MemoryState.Items.Count; i++)
            {
                var memory = MemoryState.Items[i];
                if (!memory.IsMemoryTypes(filter.Value))
                    continue;

                var link = resolveWorld?.FindChar(memory.Link);
                result.Add(link != null ? new ScriptMemoryEntry(this, link, normalized) : memory);
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
        // Hirelings are paid in gold (handled by the AI wage tick), so hunger
        // does not erode their loyalty.
        bool isHireling = TryGetTag("HIRE_WAGE", out _);
        if (!isHireling && _npcFood > 0)
            _npcFood--;

        var petOwner = OwnerSerial.IsValid ? ResolveCharByUid?.Invoke(OwnerSerial) : null;

        if (_npcFood == 0)
        {
            // @PetDesert (Source-X) — fires before the pet goes wild; a script may
            // RETURN 1 to cancel the desertion and keep the pet serving.
            if (OnPetDesert != null && OnPetDesert(this, petOwner))
                return false;

            // Warn the owner instead of letting the pet go feral silently.
            if (petOwner != null)
                SendOwnerMessage?.Invoke(petOwner, ServerMessages.GetFormatted("pet_gone_wild", Name));
            ClearOwnership(clearFriends: false);
            PetAIMode = PetAIMode.Stay;
            return false;
        }

        // Escalating loyalty warnings as the pet grows hungry/unhappy.
        if (petOwner != null && (_npcFood == 15 || _npcFood == 10 || _npcFood == 5))
            SendOwnerMessage?.Invoke(petOwner, ServerMessages.GetFormatted("pet_loyalty_low", Name));

        return false;
    }

    /// <summary>ISVALID plus the pack's pervasive ISVALIDE alias.</summary>
    private static bool IsValidQueryToken(string s) =>
        s.Equals("ISVALID", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("ISVALIDE", StringComparison.OrdinalIgnoreCase);

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

    private static bool TryParseMemoryTypeName(string raw, out MemoryType type)
    {
        type = MemoryType.None;
        string norm = NormalizeMemoryType(raw);
        if (norm switch
        {
            "MEMORY_FIGHT" => (type = MemoryType.Fight) != 0,
            "MEMORY_IAGGRESSOR" => (type = MemoryType.IAggressor) != 0,
            "MEMORY_HARMEDBY" => (type = MemoryType.HarmedBy) != 0,
            "MEMORY_AGGREIVED" => (type = MemoryType.Aggreived) != 0,
            "MEMORY_SAWCRIME" => (type = MemoryType.SawCrime) != 0,
            "MEMORY_IPET" or "MEMORY_OWNER" => (type = MemoryType.IPet) != 0,
            "MEMORY_FRIEND" => (type = MemoryType.Friend) != 0,
            "MEMORY_GUARD" => (type = MemoryType.Guard) != 0,
            "MEMORY_GUILD" => (type = MemoryType.Guild) != 0,
            "MEMORY_TOWN" => (type = MemoryType.Town) != 0,
            "MEMORY_SPEAK" => (type = MemoryType.Speak) != 0,
            "MEMORY_ISPAWNED" => (type = MemoryType.ISpawned) != 0,
            "MEMORY_FOLLOW" => (type = MemoryType.Follow) != 0,
            "MEMORY_IRRITATEDBY" => (type = MemoryType.IrritatedBy) != 0,
            _ => false
        })
            return true;

        if (TryParseHexOrDecUshort(norm, out ushort bits))
        {
            type = (MemoryType)bits;
            return type != MemoryType.None;
        }
        return false;
    }

    private void SetOwnerControllerRaw(Serial ownerUid, Serial controllerUid, bool mirrorLegacySummon)
    {
        // Ownership is changing on this pet — the follower-count cache of the
        // old and new owner (and controller) no longer reflects reality.
        var followerWorld = ResolveWorld?.Invoke();
        if (followerWorld != null)
        {
            if (_npcMaster.IsValid && _npcMaster != ownerUid)
                followerWorld.FindChar(_npcMaster)?.InvalidateFollowerCount();
            if (ownerUid.IsValid)
                followerWorld.FindChar(ownerUid)?.InvalidateFollowerCount();
            if (controllerUid.IsValid && controllerUid != ownerUid)
                followerWorld.FindChar(controllerUid)?.InvalidateFollowerCount();
        }

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
    public ushort GetSkill(SkillType skill)
    {
        int index = (int)skill;
        return index >= 0 && index < _skillValues.Length ? _skillValues[index] : (ushort)0;
    }

    public void SetSkill(SkillType skill, ushort value)
    {
        int index = (int)skill;
        if (index >= 0 && index < _skillValues.Length)
            _skillValues[index] = value;
    }

    /// <summary>Pre-set @SkillChange hook (Source-X CTRIG_SkillChange as a setter
    /// guard): fired before a RUNTIME skill value-set so a script can modify the new
    /// value (by ref) or cancel it (return true). Load / spawn / decay / gain use the
    /// raw <see cref="SetSkill"/> and never fire this. Installed only when @SkillChange
    /// is actually hooked, so the common path is a single null check.</summary>
    public delegate bool SkillChangeHook(Character ch, SkillType skill, int oldValue, ref int newValue);
    public static SkillChangeHook? OnSkillChange { get; set; }

    /// <summary>Runtime skill setter: fires the cancelable pre-set @SkillChange hook,
    /// then applies the (possibly script-adjusted) value. Returns false if a script
    /// cancelled the change. Use for player- / script-initiated changes (GM command,
    /// property assignment); world-load and spawn use the raw <see cref="SetSkill"/>.</summary>
    public bool SetSkillRuntime(SkillType skill, int value)
    {
        int index = (int)skill;
        if (index < 0 || index >= _skillValues.Length)
            return false;
        int oldValue = GetSkill(skill);
        int newValue = value;
        if (OnSkillChange != null && OnSkillChange(this, skill, oldValue, ref newValue))
            return false;
        SetSkill(skill, (ushort)Math.Clamp(newValue, 0, ushort.MaxValue));
        return true;
    }

    public byte GetSkillLock(SkillType skill)
    {
        int index = (int)skill;
        return index >= 0 && index < _skillLocks.Length ? _skillLocks[index] : (byte)2;
    }

    public void SetSkillLock(SkillType skill, byte lockState)
    {
        int index = (int)skill;
        if (index >= 0 && index < _skillLocks.Length)
            _skillLocks[index] = Math.Min(lockState, (byte)2);
    }

    // --- Equipment ---
    public Item? GetEquippedItem(Layer layer)
    {
        int idx = (int)layer;
        return idx >= 0 && idx < _equipment.Length ? _equipment[idx] : null;
    }

    public bool Equip(Item item, Layer layer)
    {
        if (item.IsDeleted) return false;
        // A two-handed weapon (TWOHANDS=Y / bow / xbow) must occupy the
        // TwoHanded layer. UO tiledata marks some of them as the OneHanded layer,
        // and the client picks the attack animation from the equipped layer — a
        // bow on OneHanded animates like an empty-handed punch. Promote it so the
        // bow swings correctly. (Source-X equips two-handers on LAYER_HAND2.)
        if (layer == Layer.OneHanded && item.IsTwoHanded)
            layer = Layer.TwoHanded;

        int idx = (int)layer;
        if (idx < 0 || idx >= _equipment.Length) return false;

        // Keep one authoritative parent/layer. Script and engine paths can call
        // Equip directly without first removing the item from a container or a
        // previous wearer; retaining both references duplicates save/weight state.
        var world = ResolveWorld?.Invoke();
        if (item.ContainedIn.IsValid && world != null)
        {
            var oldParent = world.FindObject(item.ContainedIn);
            if (oldParent is Item oldContainer)
                oldContainer.RemoveItem(item);
            else if (oldParent is Character oldWearer && item.IsEquipped &&
                     oldWearer.GetEquippedItem(item.EquipLayer) == item)
                oldWearer.Unequip(item.EquipLayer);
        }
        else if (world != null)
        {
            world.HideFromSector(item);
        }

        var displaced = _equipment[idx];
        if (displaced != null && !ReferenceEquals(displaced, item))
        {
            Unequip(layer);
            bool bounced = layer != Layer.Pack && Backpack != null &&
                !ReferenceEquals(Backpack, displaced) && Backpack.TryAddItem(displaced);
            if (!bounced && world != null)
                world.PlaceItemWithDecay(displaced, Position);
        }

        _equipment[idx] = item;
        if (layer == Layer.Pack)
            _backpack = item;
        item.IsEquipped = true;
        item.EquipLayer = layer;
        item.ContainedIn = Uid;
        // Track the last weapon wielded for the EquipLastWeapon client macro
        // (Source-X CChar::m_uidWeaponLast, set on equip, CCharAct.cpp:314).
        if ((layer == Layer.OneHanded || layer == Layer.TwoHanded) && item.IsWeaponType)
            _lastWeaponUid = item.Uid;
        MarkDirty(DirtyFlag.Equip);
        return true;
    }

    /// <summary>Reason an equip was denied (Source-X CChar::CanEquipLayer).</summary>
    public enum EquipDenial
    {
        None = 0,
        InvalidLayer, // not a real wearable slot (0, Dragging/31+, out of range)
        LayerBlocked, // the layer is worn by something that cannot be moved (cursed)
        TooWeak,      // wearer below the item's REQSTR
    }

    /// <summary>
    /// Central equip gate: validates the target layer is a real wearable slot and
    /// the wearer meets the item's REQSTR. Hand-conflict resolution, @EquipTest
    /// and ownership stay at the (packet) call site — this is the shared rule that
    /// script/engine <see cref="Equip"/> callers would otherwise bypass. Maps to
    /// Source-X CCharStatus.cpp CanEquipLayer / CanEquipStr. GM bypasses.
    /// </summary>
    public bool CanEquip(Item item, Layer layer, out EquipDenial denial)
    {
        denial = EquipDenial.None;
        if (PrivLevel >= Core.Enums.PrivLevel.GM) return true;

        int idx = (int)layer;
        if (idx <= 0 || idx >= (int)Layer.Dragging || idx >= _equipment.Length)
        {
            denial = EquipDenial.InvalidLayer;
            return false;
        }

        int reqStr = item.ReqStr;
        if (reqStr > 0 && CombatEngine.EffectiveStr(this) < reqStr)
        {
            denial = EquipDenial.TooWeak;
            return false;
        }

        // Source-X CanEquipLayer (CCharStatus.cpp:470): a busy layer is only freed
        // if what is on it can actually be moved. Without this, equipping a plain
        // weapon displaced a CURSED one into the pack - the very move the cursed
        // flag exists to refuse.
        var occupant = _equipment[idx];
        if (occupant != null && !ReferenceEquals(occupant, item) &&
            !Items.ItemMoveRules.CanMove(this, occupant, out _))
        {
            denial = EquipDenial.LayerBlocked;
            return false;
        }

        return true;
    }

    public Item? Unequip(Layer layer)
    {
        int idx = (int)layer;
        if (idx < 0 || idx >= _equipment.Length) return null;

        var item = _equipment[idx];
        if (item == null) return null;

        _equipment[idx] = null;
        if (layer == Layer.Pack && ReferenceEquals(_backpack, item))
            _backpack = null;
        item.IsEquipped = false;
        item.ContainedIn = Serial.Invalid;
        MarkDirty(DirtyFlag.Equip);

        // Source-X Stat_AddMaxMod on unequip clamps the current pool down to the
        // lowered adjusted max. The item is already out of _equipment, so the Max*
        // getters now report the reduced effective pool; truncate any current that
        // now sits above it (a +HITSMAX/MANAMAX/STAMMAX piece coming off).
        bool poolClamped = false;
        if (_hits > MaxHits) { _hits = MaxHits; poolClamped = true; }
        if (_mana > MaxMana) { _mana = MaxMana; poolClamped = true; }
        if (_stam > MaxStam) { _stam = MaxStam; poolClamped = true; }
        if (poolClamped) MarkDirty(DirtyFlag.Stats);

        return item;
    }

    public Item? Backpack
    {
        get
        {
            // Never hand back a deleted pack: a GM .remove on the backpack
            // clears the equip slot but this cached field kept the dead
            // reference, so pack==null checks never fired, GetPackSafe-style
            // recreation never ran, and items dropped "into the pack" landed
            // inside a deleted container and vanished.
            if (_backpack != null && !_backpack.IsDeleted)
                return _backpack;
            _backpack = null;
            var equipped = GetEquippedItem(Layer.Pack);
            return equipped == null || equipped.IsDeleted ? null : equipped;
        }
        set => _backpack = value;
    }

    /// <summary>Drop the cached backpack reference if it points at
    /// <paramref name="item"/> (called when that item is deleted).</summary>
    public void ClearBackpackReference(Item item)
    {
        if (ReferenceEquals(_backpack, item))
            _backpack = null;
    }

    public int GetTotalWeight()
        => GetTotalWeightTenths() / Item.WeightUnits;

    /// <summary>Exact equipped/container-tree weight in tenths of a stone.</summary>
    public int GetTotalWeightTenths()
    {
        long total = 0;
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _equipment.Length; i++)
        {
            var eq = _equipment[i];
            // Bank/vendor/special layers are service containers or engine
            // memories, not carried inventory. Counting bank gold here makes a
            // withdrawal impossible merely because the account has savings.
            if (i >= (int)Layer.VendorStock)
                continue;
            if (eq != null && seen.Add(eq))
            {
                total += eq.TotalWeightTenths;
                if (total >= int.MaxValue)
                    return int.MaxValue;
            }
        }
        return (int)total;
    }

    /// <summary>Maximum carry weight in whole stones (Source-X: Str*7/2 + 40 + mod).
    /// Uses effective STR so an equipped +STR suit raises the carry ceiling.</summary>
    public int MaxWeight => (CombatEngine.EffectiveStr(this) * 7 / 2) + 40 + _modMaxWeight;

    /// <summary>True when this character can carry <paramref name="item"/>'s weight on
    /// top of what it already holds (Source-X CChar::CanCarry). Used to bounce a
    /// freshly gathered/crafted item to the ground instead of overloading the pack.</summary>
    public bool CanCarry(Item item)
    {
        long incomingTenths = item.TotalWeightTenths;
        return (long)GetTotalWeightTenths() + incomingTenths <=
               (long)Math.Max(0, MaxWeight) * Item.WeightUnits;
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

    // --- Attacker log (state + logic in CharacterCombatState) ---
    /// <summary>Read-only view of the current attacker log. Most recent hit
    /// is at the end of the list (ATTACKER.LAST).</summary>
    public IReadOnlyList<AttackerRecord> Attackers => CombatState.Attackers;

    /// <summary>Add <paramref name="damage"/> to the running total for
    /// <paramref name="attackerUid"/> and stamp the current tick. Called
    /// from combat / spell damage paths. No-op for self-damage.</summary>
    public void RecordAttack(Serial attackerUid, int damage) =>
        CombatState.RecordAttack(attackerUid, damage);

    /// <summary>Set/clear the ATTACKER.n.IGNORE flag for an attacker already
    /// in the log. Returns false when the uid is not an attacker.</summary>
    public bool SetAttackerIgnored(Serial attackerUid, bool ignored) =>
        CombatState.SetAttackerIgnored(attackerUid, ignored);

    public void ClearAttackers() => CombatState.ClearAttackers();

    /// <summary>Index of <paramref name="uid"/> in the attacker log, or -1.</summary>
    public int Attacker_GetIndex(Serial uid) => CombatState.GetIndex(uid);

    /// <summary>Seconds since the last hit from <paramref name="uid"/>, or -1 when unknown.</summary>
    public long Attacker_GetElapsedSeconds(Serial uid) => CombatState.GetElapsedSeconds(uid);

    /// <summary>Remove one attacker entry (fight retreat / timeout).</summary>
    public void Attacker_Delete(Serial uid) => CombatState.Delete(uid);

    // --- Death ---

    /// <summary>Resurrection HP percentage (Source-X sphere.ini
    /// HITPOINTPERCENTONREZ, m_iHitpointPercentOnRez default 10).</summary>
    public static int HitpointPercentOnRez { get; set; } = 10;

    /// <summary>sphere.ini PACKETDEATHANIMATION (Source-X m_iPacketDeathAnimation,
    /// default on): send the 0x2C death-screen packet to a dying client. When
    /// off, the client never enters ClassicUO's 1.5s death-screen freeze and
    /// the plain player-update redraw carries the ghost transition.</summary>
    public static bool PacketDeathAnimationEnabled { get; set; } = true;

    public void Kill()
    {
        int abortedSkill = ClearActiveSkillPending();
        if (abortedSkill >= 0)
            ActiveSkillAborted?.Invoke(this, abortedSkill);
        InterruptMeditation();
        ClearCastState();
        _hits = 0;
        CurePoison();
        ClearPendingHit();
        FightTarget = Serial.Invalid;
        // Source-X CChar::Death: Reveal() + StatFlag_Clear(STONE|FREEZE|
        // HIDDEN|SLEEPING|HOVERING) — the ghost must not inherit held states
        // (a hidden victim previously stayed an unseen ghost).
        ClearStatFlag(StatFlag.Hidden);
        ClearStatFlag(StatFlag.Invisible);
        ClearStatFlag(StatFlag.Freeze);
        ClearStatFlag(StatFlag.Stone);
        ClearStatFlag(StatFlag.Sleeping);
        ClearStatFlag(StatFlag.Hovering);
        SetStatFlag(StatFlag.Dead);
        MarkDirty(DirtyFlag.StatFlags | DirtyFlag.Stats);
    }

    public void Resurrect()
    {
        if (!IsDead) return;
        // Source-X Spell_Resurrection: an antimagic region refuses the rez
        // (REGION_ANTIMAGIC_ALL → SphereNet RegionFlag.NoMagic); a GM ghost
        // bypasses (the reference fNoFail).
        if (PrivLevel < PrivLevel.GM)
        {
            var region = ResolveWorld?.Invoke()?.FindRegion(Position);
            if (region != null && region.NoMagic)
                return;
        }
        ClearStatFlag(StatFlag.Dead);
        // Source-X Spell_Resurrection: StatFlag_Clear(STATF_DEAD|STATF_INSUBSTANTIAL).
        ClearStatFlag(StatFlag.Insubstantial);
        CurePoison();
        ClearStatFlag(StatFlag.Hidden);
        // Source-X: MaxHits × HITPOINTPERCENTONREZ / 100 (sphere.ini), floored
        // at 1 HP — a 0-HP resurrect would re-kill the character next tick.
        _hits = (short)Math.Max(1, _maxHits * Math.Clamp(HitpointPercentOnRez, 0, 100) / 100);
        CombatState.ClearAttackers();
        ClearPendingHit();
        FightTarget = Serial.Invalid;

        // Source-X: murderer resurrection penalties (stat/skill loss ~1%)
        if (IsMurderer && IsPlayer)
        {
            short oldStr = _str, oldDex = _dex, oldInt = _int;
            _str = (short)Math.Max(1, _str - Math.Max((short)1, (short)(_str / 100)));
            _dex = (short)Math.Max(1, _dex - Math.Max((short)1, (short)(_dex / 100)));
            _int = (short)Math.Max(1, _int - Math.Max((short)1, (short)(_int / 100)));
            if (_str < oldStr) SkillEngine.OnStatDecrease?.Invoke(this, 0, _str);
            if (_dex < oldDex) SkillEngine.OnStatDecrease?.Invoke(this, 1, _dex);
            if (_int < oldInt) SkillEngine.OnStatDecrease?.Invoke(this, 2, _int);
            for (int i = 0; i < _skillValues.Length; i++)
            {
                if (_skillValues[i] > 0)
                {
                    _skillValues[i] = (ushort)Math.Max(0,
                        _skillValues[i] - Math.Max(1, _skillValues[i] / 100));
                    if (i < SkillEngine.BaseSkillCount)
                        SkillEngine.OnSkillDecrease?.Invoke(this, (SkillType)i, _skillValues[i]);
                }
            }
        }

        MarkDirty(DirtyFlag.StatFlags | DirtyFlag.Stats);
    }

    public void Delete()
    {
        int abortedSkill = ClearActiveSkillPending();
        if (abortedSkill >= 0)
            ActiveSkillAborted?.Invoke(this, abortedSkill);
        InterruptMeditation();
        ClearCastState();
        _isDeleted = true;
        CombatState.ClearAttackers();
        MemoryState.Clear();
        ClearPendingHit();
        FightTarget = Serial.Invalid;
    }

    // --- IScriptObj overrides ---
    public override bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();

        // AOS on-hit combat properties (HITLEECHLIFE, HITFIREBALL, ...) are
        // tag-backed like the Slayer faction pair — persisted with the char
        // and readable by the combat engine's on-hit pipeline.
        if (AosOnHitProperties.Contains(upper))
        {
            value = TryGetTag(upper, out var aosv) ? aosv ?? "0" : "0";
            return true;
        }
        if (SpellCastingProperties.Contains(upper))
        {
            value = SphereNet.Game.Magic.SpellEngine.GetCastingPropertyValue(this, upper).ToString();
            return true;
        }
        if (upper == CombatSpeedProperties.IncreaseSwingSpeed)
        {
            value = CombatEngine.GetEquipmentPropertyValue(this, upper).ToString();
            return true;
        }

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
                    if (IsValidQueryToken(sub))
                    { value = "0"; return true; }
                    value = "0";
                    return true;
                }

                if (sub.Length == 0)
                {
                    value = $"0{guild.StoneUid.Value:X8}";
                    return true;
                }
                if (IsValidQueryToken(sub))
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
                if (IsValidQueryToken(sub))
                { value = "1"; return true; }
                return memory.TryGetProperty(sub, out value);
            }

            // Unknown memory or sub: return false-y so callers using
            // `<isEmpty ...>` or `If <...isValid>` take the empty branch.
            if (IsValidQueryToken(sub))
            { value = "0"; return true; }
            value = "";
            return true;
        }

        if (TryResolveOwnedObjectToken(upper, "HOUSE.", Uid, ResolveHouseUidsByOwner, out value) ||
            TryResolveOwnedObjectToken(upper, "SHIP.", Uid, ResolveShipUidsByOwner, out value))
        {
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

        if (upper.StartsWith("STATPERCENT", StringComparison.Ordinal))
        {
            // STATPERCENT.<stat> — current pool as a percentage of its adjusted max
            // (Source-X GetStatPercent). Sphere maps STR→hits, DEX→stam, INT→mana.
            string statKey = upper.Length > 11 ? upper[11..].TrimStart('.') : "STR";
            (int cur, int max) = statKey switch
            {
                "STR" or "HITS" => (_hits, (int)_maxHits),
                "DEX" or "STAM" => (_stam, (int)_maxStam),
                "INT" or "MANA" => (_mana, (int)_maxMana),
                _ => (0, 0),
            };
            value = (max <= 0 ? 0 : (cur * 100 + max / 2) / max).ToString();
            return true;
        }

        if (upper.StartsWith("SKILLMOD", StringComparison.Ordinal) &&
            int.TryParse(upper.AsSpan(8), out int skillModIdx) && skillModIdx >= 0)
        {
            // SkillMod<n> — reference Skill_GetAdjusted uiBonSkill: the per-skill
            // effective bonus applied on top of the base. Stored as a tag so it
            // persists; read live by SkillEngine.GetSkillModBonus.
            value = TryGetTag($"SkillMod{skillModIdx}", out string? sm) && sm != null ? sm : "0";
            return true;
        }

        switch (upper)
        {
            case "STR": value = _str.ToString(); return true;
            case "DEX": value = _dex.ToString(); return true;
            case "INT": value = _int.ToString(); return true;
            // STR+DEX+INT (Source-X Stat_GetSum). The pack's stat-overflow
            // guards (<SRC.STATTOTAL> > <SRC.SKILLCLASS.STATSUM>) read 0
            // without it, so those checks never tripped.
            case "STATTOTAL":
            case "STATSUM": value = (_str + _dex + _int).ToString(); return true;
            case "HITS":
            case "HITPOINTS": value = _hits.ToString(); return true; // Source-X CHC_HITPOINTS == CHC_HITS
            case "MANA": value = _mana.ToString(); return true;
            case "STAM":
            case "STAMINA": value = _stam.ToString(); return true; // Source-X CHC_STAMINA == CHC_STAM
            case "MAXHITS": value = _maxHits.ToString(); return true;
            case "MAXMANA": value = _maxMana.ToString(); return true;
            case "MAXSTAM": value = _maxStam.ToString(); return true;
            case "BLOODCOLOR": value = $"0{_bloodHue:X}"; return true; // Source-X FormatHex(_wBloodHue)
            case "FOLLOWERSLOTS": value = ControlSlots.ToString(); return true;
            case "BODY": value = FormatBodyProperty(); return true;
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
            case "CAN":
            {
                var cdef = Definitions.DefinitionLoader.GetCharDef(_charDefIndex);
                value = cdef != null ? $"0{(uint)cdef.Can:X}" : "0";
                return true;
            }
            case "FOOD": value = _food.ToString(); return true;
            // MAXFOOD / VIRTUALGOLD are stored as tags by their setters (Source-X
            // CHC_MAXFOOD / CHC_VIRTUALGOLD are readable). Default 0 when unset;
            // the food dialog falls back to SERV.CHARDEF.<id>.MAXFOOD from there.
            case "MAXFOOD":
                value = TryGetTag("MAXFOOD", out string? mf) ? (mf ?? "0") : "0";
                return true;
            case "VIRTUALGOLD":
                value = TryGetTag("VIRTUALGOLD", out string? vg) ? (vg ?? "0") : "0";
                return true;
            case "PRIVLEVEL": value = ((int)PrivLevel).ToString(); return true;
            case "ISMOUNTED": value = IsMounted ? "1" : "0"; return true;
            case "ISDEAD": value = IsDead ? "1" : "0"; return true;
            case "BONDED": value = IsBonded ? "1" : "0"; return true;
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
            case "HOUSES":
            {
                value = (ResolveHouseUidsByOwner?.Invoke(Uid).Count ?? 0).ToString();
                return true;
            }
            case "SHIPS":
            {
                value = (ResolveShipUidsByOwner?.Invoke(Uid).Count ?? 0).ToString();
                return true;
            }
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
            case "KILLS": value = Kills.ToString(); return true;
            case "CRIMINAL": value = IsCriminal ? "1" : "0"; return true;
            case "MURDERER": value = IsMurderer ? "1" : "0"; return true;
            case "POISONLEVEL": value = Poison.Level.ToString(); return true;
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
            case "SPEECHCOLOR":
            case "SPEECHCOLOROVERRIDE": value = _speechColor.ToString(); return true;
            case "MAXFOLLOWER": value = _maxFollower.ToString(); return true;
            case "CURFOLLOWER": value = CurFollower.ToString(); return true;
            // Per-char regen rate overrides (Source-X CChar). Non-D = seconds, D = tenths.
            case "REGENHITS": value = (EffectiveRegenRateMsForRead(_regenHitsRateMs, RegenHitsSeconds, 40000) / 1000).ToString(); return true;
            case "REGENHITSD": value = (EffectiveRegenRateMsForRead(_regenHitsRateMs, RegenHitsSeconds, 40000) / 100).ToString(); return true;
            case "REGENMANA": value = (EffectiveRegenRateMsForRead(_regenManaRateMs, RegenManaSeconds, 20000) / 1000).ToString(); return true;
            case "REGENMANAD": value = (EffectiveRegenRateMsForRead(_regenManaRateMs, RegenManaSeconds, 20000) / 100).ToString(); return true;
            case "REGENSTAM": value = (EffectiveRegenRateMsForRead(_regenStamRateMs, RegenStamSeconds, 10000) / 1000).ToString(); return true;
            case "REGENSTAMD": value = (EffectiveRegenRateMsForRead(_regenStamRateMs, RegenStamSeconds, 10000) / 100).ToString(); return true;
            case "REGENFOOD": value = (EffectiveRegenRateMsForRead(_regenFoodRateMs, RegenFoodSeconds, 3_600_000) / 1000).ToString(); return true;
            case "REGENFOODD": value = (EffectiveRegenRateMsForRead(_regenFoodRateMs, RegenFoodSeconds, 3_600_000) / 100).ToString(); return true;
            // Per-char regen amounts (Source-X Stats_GetRegenVal → max(1, stored)).
            case "REGENVALHITS": value = Math.Max(1, (int)_regenValHits).ToString(); return true;
            case "REGENVALMANA": value = Math.Max(1, (int)_regenValMana).ToString(); return true;
            case "REGENVALSTAM": value = Math.Max(1, (int)_regenValStam).ToString(); return true;
            case "REGENVALFOOD": value = Math.Max(1, (int)_regenValFood).ToString(); return true;
            case "RESPHYSICALMAX": value = _resPhysicalMax.ToString(); return true;
            case "RESFIREMAX": value = _resFireMax.ToString(); return true;
            case "RESCOLDMAX": value = _resColdMax.ToString(); return true;
            case "RESPOISONMAX": value = _resPoisonMax.ToString(); return true;
            case "RESENERGYMAX": value = _resEnergyMax.ToString(); return true;
            // Slayer-system NPC faction (Source-X CCPropsChar) — tag-backed so
            // it persists with the char and flows in from CHARDEF def-tags.
            case "FACTION_GROUP": value = TryGetTag("FACTION_GROUP", out var facG) ? facG ?? "0" : "0"; return true;
            case "FACTION_SPECIES": value = TryGetTag("FACTION_SPECIES", out var facS) ? facS ?? "0" : "0"; return true;
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
            case "FIGHTTARGET": value = FightTarget.IsValid ? $"0{FightTarget.Value:X}" : "0"; return true;
            case "OWNER":
            case "NPCMASTER": value = NpcMaster.IsValid ? $"0{NpcMaster.Value:X}" : "0"; return true;
            case "SWINGSTATE": value = ((int)CombatSwingState).ToString(); return true;
            case "SWINGSTATE.NAME": value = CombatSwingState.ToString(); return true;
            case "SWINGREMAIN":
            {
                long remain = CombatSwingStateUntil > 0 ? CombatSwingStateUntil - Environment.TickCount64 : 0;
                value = Math.Max(0, remain).ToString();
                return true;
            }
            case "PETAI":
            case "PETAIMODE": value = ((int)PetAIMode).ToString(); return true;
            case "FLEESTEPS": value = FleeStepsCurrent.ToString(); return true;
            case "FLEESTEPSMAX": value = FleeStepsMax.ToString(); return true;
            case "CREATETIME":
            case "CREATE": value = (_createTime / 1000).ToString(); return true;
            case "ISONLINE": value = _isOnline ? "1" : "0"; return true;

            // --- Calculated properties ---
            case "SERIAL": value = $"0{Uid.Value:X}"; return true;
            case "ISCHAR": value = "1"; return true;
            case "ISITEM": value = "0"; return true;
            case "DISPIDDEC": value = _bodyId.ToString(); return true;
            case "BASEID": value = $"0{BaseId:X}"; return true;
            case "DUID": value = Uid.Value.ToString(); return true;
            case "HEIGHT":
            {
                var hdef = Definitions.DefinitionLoader.GetCharDef(_charDefIndex);
                value = hdef != null && hdef.Height > 0 ? hdef.Height.ToString() : "10";
                return true;
            }
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
            case "ARMOR":
            case "AR":
                // Total worn armour rating (Source-X OC_ARMOR / CHC_AR).
                value = Combat.CombatEngine.CalcArmorDefense(this).ToString();
                return true;
            case "WEIGHT": value = GetTotalWeight().ToString(); return true;
            case "MAXWEIGHT":
                value = ((_str * 7 / 2) + 40 + _modMaxWeight).ToString();
                return true;
            case "RANGE":
            {
                var weapon = GetEquippedItem(Layer.OneHanded) ?? GetEquippedItem(Layer.TwoHanded);
                if (weapon != null)
                {
                    var wDef = Definitions.DefinitionLoader.GetItemDef(weapon.BaseId);
                    value = wDef != null && wDef.RangeMax > 0 ? wDef.RangeMax.ToString() : "1";
                }
                else
                {
                    value = "1";
                }
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
                value = CombatState.Attackers.Count.ToString();
                return true;
            }
        }

        // CANSEELOS / CANSEE — accept either a '.' or whitespace before the uid
        // argument (Source-X CObjBase OC_CANSEELOS/OC_CANSEE: SKIP_SEPARATORS +
        // GETNONWHITESPACE, so <SRC.CANSEELOS <uid>> and CANSEELOS.<uid> both work).
        {
            string? canSeeArg = null;
            bool canSeeLosMode = false;
            if (upper.StartsWith("CANSEELOS", StringComparison.Ordinal) &&
                (upper.Length == 9 || !char.IsLetterOrDigit(upper[9])))
            {
                canSeeLosMode = true;
                canSeeArg = upper.Length > 9 ? key[9..].TrimStart('.', ' ', '\t') : "";
            }
            else if (upper.StartsWith("CANSEE", StringComparison.Ordinal) &&
                     (upper.Length == 6 || !char.IsLetterOrDigit(upper[6])))
            {
                canSeeArg = upper.Length > 6 ? key[6..].TrimStart('.', ' ', '\t') : "";
            }
            if (canSeeArg != null)
            {
                var world = ResolveWorld?.Invoke();
                var serial = ParseSerial(canSeeArg);
                if (world != null && serial.IsValid)
                {
                    var target = world.FindObject(serial);
                    if (target != null)
                    {
                        bool los = world.CanSeeLOS(Position, target.Position);
                        if (canSeeLosMode)
                        {
                            value = los ? "1" : "0";
                        }
                        else
                        {
                            int dist = Position.GetDistanceTo(target.Position);
                            value = (los && dist <= (_visualRange > 0 ? _visualRange : 18)) ? "1" : "0";
                        }
                        return true;
                    }
                }
                value = "0";
                return true;
            }
        }

        // NOTOGETFLAG <uid> [allowIncog] [allowInvul] — my notoriety flag as
        // seen by the target char (Source-X CHC_NOTOGETFLAG → Noto_GetFlag).
        if (upper.StartsWith("NOTOGETFLAG", StringComparison.Ordinal))
        {
            value = "0";
            string rest = key.Length > 11 ? key[11..].Trim() : "";
            var world = ResolveWorld?.Invoke();
            if (rest.Length > 0 && world != null)
            {
                string uidTok = rest.Split([' ', ',', '\t'],
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                var viewer = world.FindObject(ParseSerial(uidTok)) as Character;
                if (viewer != null && ResolveNotoFlag != null)
                    value = ((int)ResolveNotoFlag(this, viewer)).ToString();
            }
            return true;
        }

        // CANMOVE <dir> — can I step one tile in <dir>? Reports the destination
        // tile's passability (Source-X CHC_CANMOVE → CheckValidMove).
        if (upper.StartsWith("CANMOVE", StringComparison.Ordinal))
        {
            value = "0";
            string dirTok = key.Length > 7 ? key[7..].Trim() : "";
            var world = ResolveWorld?.Invoke();
            if (world?.MapData != null && TryParseDirectionToken(dirTok, out Direction d))
            {
                GetDirectionStep(d, out int dx, out int dy);
                value = world.MapData.IsPassable(MapIndex, X + dx, Y + dy, Z) ? "1" : "0";
            }
            return true;
        }

        // ATTACKER.LAST / ATTACKER.MAX / ATTACKER.n.{DAM|ELAPSED|UID}
        if (upper.StartsWith("ATTACKER.", StringComparison.Ordinal))
        {
            string tail = upper.Substring("ATTACKER.".Length);
            if (tail == "LAST")
            {
                value = CombatState.Attackers.Count > 0
                    ? "0x" + CombatState.Attackers[^1].Uid.Value.ToString("X")
                    : "0";
                return true;
            }
            if (tail == "MAX")
            {
                if (CombatState.Attackers.Count == 0) { value = "0"; return true; }
                int bestIdx = 0;
                for (int i = 1; i < CombatState.Attackers.Count; i++)
                    if (CombatState.Attackers[i].TotalDamage > CombatState.Attackers[bestIdx].TotalDamage)
                        bestIdx = i;
                value = "0x" + CombatState.Attackers[bestIdx].Uid.Value.ToString("X");
                return true;
            }
            // ATTACKER.n.{DAM|ELAPSED|UID}
            int dot = tail.IndexOf('.');
            if (dot > 0 && int.TryParse(tail.AsSpan(0, dot), out int idx)
                && idx >= 0 && idx < CombatState.Attackers.Count)
            {
                string sub = tail.Substring(dot + 1);
                var rec = CombatState.Attackers[idx];
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
                    case "IGNORE":
                        value = rec.Ignored ? "1" : "0";
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

        // FINDLAYER.<layer>[.<property>] — <layer> may be a number or a layer
        // DEFNAME (layer_hair=11, layer_beard=16, ...). Source-X expression-
        // evaluates the arg (CChar LayerFind(Exp_GetSingle(...))). Chains to the
        // found item's properties like the FINDLAYER(N) paren form.
        if (upper.StartsWith("FINDLAYER.", StringComparison.Ordinal))
        {
            string rest = key["FINDLAYER.".Length..];
            int dotIdx = rest.IndexOf('.');
            string layerToken = dotIdx < 0 ? rest : rest[..dotIdx];
            string tail = dotIdx < 0 ? "" : rest[(dotIdx + 1)..];
            if (TryResolveLayerToken(layerToken, out int layerIdx))
            {
                var worn = GetEquippedItem((Layer)layerIdx);
                if (worn == null) { value = "0"; return true; }
                if (tail.Length == 0) { value = $"0{worn.Uid.Value:X}"; return true; }
                return worn.TryGetProperty(tail, out value);
            }
            value = "0";
            return true;
        }

        // FINDID.xxx  (see FindItemByBaseId for the search scope)
        if (upper.StartsWith("FINDID.", StringComparison.Ordinal))
        {
            string idStr = key["FINDID.".Length..].Trim();
            if (TryParseHexOrDecUshort(idStr, out ushort findId))
            {
                var found = FindItemByBaseId(findId);
                if (found != null)
                {
                    value = $"0{found.Uid.Value:X}";
                    return true;
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

        // FINDUID.uid — check if a specific UID is on this character
        if (upper.StartsWith("FINDUID.", StringComparison.Ordinal))
        {
            string uidStr = key["FINDUID.".Length..].Trim();
            Serial findUid = ParseSerial(uidStr);
            if (findUid.IsValid)
            {
                for (int i = 0; i < _equipment.Length; i++)
                {
                    if (_equipment[i] != null && _equipment[i]!.Uid == findUid)
                    {
                        value = $"0{findUid.Value:X}";
                        return true;
                    }
                }
                var pack = Backpack;
                if (pack != null)
                {
                    foreach (var item in pack.Contents)
                    {
                        if (item.Uid == findUid)
                        {
                            value = $"0{findUid.Value:X}";
                            return true;
                        }
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

        // STATLOCK.n
        if (upper.StartsWith("STATLOCK.", StringComparison.Ordinal))
        {
            if (int.TryParse(upper.AsSpan("STATLOCK.".Length), out int statIdx))
            {
                value = GetStatLock(statIdx).ToString();
                return true;
            }
            value = "0";
            return true;
        }

        // StatLock[n] bracket syntax (Sphere save/script format)
        if (upper.StartsWith("STATLOCK[", StringComparison.Ordinal) && upper.Contains(']'))
        {
            int si = upper.IndexOf('[');
            int ei = upper.IndexOf(']');
            if (int.TryParse(upper.AsSpan(si + 1, ei - si - 1), out int statIdx))
            {
                value = GetStatLock(statIdx).ToString();
                return true;
            }
            value = "0";
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
            else
            {
                string memPart = upper["MEMORY.".Length..];
                if (TryParseMemoryTypeName(memPart, out MemoryType mt))
                {
                    value = Memory_FindTypes(mt) != null ? "1" : "0";
                    return true;
                }
            }

            value = TryGetTag(key, out string? mv) ? (mv ?? "0") : "0";
            return true;
        }

        // Guild/stone member properties (PRIV, ISMASTER, GUILD.TITLE, ...)
        if (TryGetGuildProperty(key, out value))
            return true;

        // Char GUILD / TOWN keyword: resolve to the member's stone and forward
        // the trailing sub-key to it (Source-X CCharPlayer GUILD/TOWN via
        // Guild_Find). Tried AFTER the member-prop switch so <GUILD.TITLE>
        // keeps its member meaning while <GUILD>/<GUILD.MASTERUID>/
        // <TOWN.MEMBERFROMUID.x.PRIV> resolve to the stone. The whole
        // stone-driven guild/town script backend keys off this.
        if (TryResolveStoneKeyword(key, out value))
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

        // Source-X CHC_ISSTUCK (IsStuck(true)): frozen/petrified counts as
        // stuck; otherwise stuck means no adjacent cardinal tile is passable.
        if (upper == "ISSTUCK")
        {
            if (IsStatFlag(StatFlag.Freeze) || IsStatFlag(StatFlag.Stone))
            {
                value = "1";
                return true;
            }
            var stuckWorld = ObjBase.ResolveWorld?.Invoke();
            var stuckMap = stuckWorld?.MapData;
            if (stuckMap == null) { value = "0"; return true; }
            bool anyOpen =
                stuckMap.IsPassable(MapIndex, X, Y - 1, Z) ||
                stuckMap.IsPassable(MapIndex, X + 1, Y, Z) ||
                stuckMap.IsPassable(MapIndex, X, Y + 1, Z) ||
                stuckMap.IsPassable(MapIndex, X - 1, Y, Z);
            value = anyOpen ? "0" : "1";
            return true;
        }

        // Source-X CHC_CANCAST.<spell>: could this char cast the spell right
        // now (mana / skill req / region antimagic — via the engine hook).
        if (upper.StartsWith("CANCAST.", StringComparison.Ordinal) ||
            upper.StartsWith("CANCAST ", StringComparison.Ordinal))
        {
            string spellStr = upper[8..].Trim();
            int spellId = 0;
            if (int.TryParse(spellStr, out int sid) && sid > 0)
                spellId = sid;
            else if (Enum.TryParse(spellStr.Replace(" ", ""), ignoreCase: true, out SpellType spEnum))
                spellId = (int)spEnum;
            value = spellId > 0 && (OnCanCastCheck?.Invoke(this, spellId) ?? false) ? "1" : "0";
            return true;
        }

        // Source-X CHC_CANMOVE.<dir>: can the char take one step that way —
        // NPC AI scripts probe walls with it before pathing.
        if (upper.StartsWith("CANMOVE.", StringComparison.Ordinal) ||
            upper.StartsWith("CANMOVE ", StringComparison.Ordinal))
        {
            string dirStr = upper[8..].Trim();
            (int dx, int dy)? delta = dirStr switch
            {
                "N" or "NORTH" or "0" => (0, -1),
                "NE" or "NORTHEAST" or "1" => (1, -1),
                "E" or "EAST" or "2" => (1, 0),
                "SE" or "SOUTHEAST" or "3" => (1, 1),
                "S" or "SOUTH" or "4" => (0, 1),
                "SW" or "SOUTHWEST" or "5" => (-1, 1),
                "W" or "WEST" or "6" => (-1, 0),
                "NW" or "NORTHWEST" or "7" => (-1, -1),
                _ => null,
            };
            if (delta == null) { value = "0"; return true; }
            var moveWorld = ObjBase.ResolveWorld?.Invoke();
            bool canStep = !IsStatFlag(StatFlag.Freeze) && !IsStatFlag(StatFlag.Stone) &&
                moveWorld?.MapData != null &&
                moveWorld.MapData.IsPassable(MapIndex, X + delta.Value.dx, Y + delta.Value.dy, Z);
            value = canStep ? "1" : "0";
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

    private static bool TryResolveOwnedObjectToken(string upperKey, string prefix, Serial ownerUid,
        Func<Serial, IReadOnlyList<Serial>>? resolver, out string value)
    {
        value = "";
        if (!upperKey.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        if (!int.TryParse(upperKey[prefix.Length..], out int index) || index < 0)
        {
            value = "0";
            return true;
        }

        var uids = resolver?.Invoke(ownerUid) ?? [];
        value = index < uids.Count ? $"0{uids[index].Value:X8}" : "0";
        return true;
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
        if (!TryNormalizeScriptValue(key, value, out string normalized))
            normalized = value;

        // AOS on-hit combat properties are tag-backed (see TryGetProperty).
        if (AosOnHitProperties.Contains(key))
        {
            SetTag(key.ToUpperInvariant(), normalized);
            return true;
        }
        if (SpellCastingProperties.Contains(key))
        {
            SetTag(key.ToUpperInvariant(), normalized);
            return true;
        }
        if (key.Equals(CombatSpeedProperties.IncreaseSwingSpeed, StringComparison.OrdinalIgnoreCase))
        {
            SetTag(CombatSpeedProperties.IncreaseSwingSpeed, normalized);
            return true;
        }

        // SkillMod<n> — reference Skill_GetAdjusted uiBonSkill: the per-skill
        // effective bonus. Persisted as a tag; setting 0 clears it. Written by
        // @Equip/@UnEquip scripts (bare key, no TAG. prefix, matching Sphere).
        if (key.StartsWith("SKILLMOD", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key.AsSpan(8), out int skillModIdx) && skillModIdx >= 0)
        {
            string tagKey = $"SkillMod{skillModIdx}";
            if (int.TryParse(normalized, out int sm) && sm != 0)
                SetTag(tagKey, sm.ToString());
            else
                RemoveTag(tagKey);
            return true;
        }

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

        // ATTACKER.n.IGNORE=0/1 — script-controlled ignore flag on an
        // attacker-log entry; a later hit from an ignored attacker fires
        // @HitIgnore (Source-X parity).
        if (key.StartsWith("ATTACKER.", StringComparison.OrdinalIgnoreCase))
        {
            string atail = key["ATTACKER.".Length..].ToUpperInvariant();
            int adot = atail.IndexOf('.');
            if (adot > 0 && atail[(adot + 1)..] == "IGNORE"
                && int.TryParse(atail.AsSpan(0, adot), out int aidx)
                && aidx >= 0 && aidx < CombatState.Attackers.Count)
            {
                SetAttackerIgnored(CombatState.Attackers[aidx].Uid, normalized != "0");
                return true;
            }
            return false;
        }

        switch (key.ToUpperInvariant())
        {
            case "STR": if (short.TryParse(normalized, out short sv)) Str = sv; return true;
            case "DEX": if (short.TryParse(normalized, out short dv)) Dex = dv; return true;
            case "INT": if (short.TryParse(normalized, out short iv)) Int = iv; return true;
            case "HITS":
            case "HITPOINTS": if (short.TryParse(normalized, out short hv)) Hits = hv; return true;
            case "MANA": if (short.TryParse(normalized, out short mv)) Mana = mv; return true;
            case "STAM":
            case "STAMINA": if (short.TryParse(normalized, out short stv)) Stam = stv; return true;
            case "MAXHITS": if (short.TryParse(normalized, out short mhv)) MaxHits = mhv; return true;
            case "MAXMANA": if (short.TryParse(normalized, out short mmv)) MaxMana = mmv; return true;
            case "MAXSTAM": if (short.TryParse(normalized, out short msv)) MaxStam = msv; return true;
            case "BLOODCOLOR":
                if (TryParseHexOrDecUshort(normalized, out ushort bcv)) _bloodHue = bcv;
                return true;
            case "FOLLOWERSLOTS":
                if (int.TryParse(normalized, out int fsv)) _followerSlotsOverride = Math.Max(0, fsv);
                return true;
            case "BODY":
                if (TryParseHexOrDecUshort(normalized, out ushort bv))
                {
                    _bodyId = bv;
                    BaseId = bv;
                    NotifyAppearanceChanged();
                    return true;
                }
                if (CharDefHelper.TryApplyDefName(this, normalized, DefinitionLoader.StaticResources))
                    return true;
                if (CharDefHelper.EnsureDisplayBody(this, DefinitionLoader.StaticResources))
                {
                    RefreshAppearance();
                    return true;
                }
                return false;
            case "CHARDEF":
            case "TYPEDEF":
                if (CharDefHelper.TryApplyDefName(this, normalized, DefinitionLoader.StaticResources))
                    return true;
                return true;
            case "DIR":
                if (byte.TryParse(normalized, out byte drv))
                {
                    Direction = (Direction)drv;
                    return true;
                }
                return false;
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
                if (TryParseSpellTypeValue(normalized, out var spell))
                    NpcSpellAdd(spell);
                return true;
            case "SKILLCLASS":
                if (string.IsNullOrWhiteSpace(normalized))
                    _skillClass = 0;
                else if (TryParseSkillClassValue(normalized, out int classId))
                    _skillClass = Math.Max(0, classId);
                return true;
            case "TITLE": _title = value; return true;
            case "FLAGS":
            {
                uint flagsHex = ParseNamedFlagMask(normalized);
                _statFlags = (StatFlag)flagsHex;
                return true;
            }
            // KILLS handled below with POISONLEVEL
            case "CRIMINAL":
                if (normalized == "1" || normalized.Equals("true", StringComparison.OrdinalIgnoreCase))
                    SetCriminal();
                return true;
            case "FOOD":
                // Clamp to the 0-60 range like the Food property setter, so the
                // script path can't push hunger past the client/regen ceiling.
                if (ushort.TryParse(normalized, out ushort foodVal)) Food = foodVal;
                return true;
            case "TIMER":
                // Legacy Sphere saves stamp the char's transient AI-tick
                // countdown; scheduling restarts fresh after import — accept
                // and discard instead of parking a meaningless SAVE. tag.
                return true;
            case "RESPHYSICAL": if (short.TryParse(normalized, out short rpv)) _resPhysical = rpv; return true;
            case "RESFIRE": if (short.TryParse(normalized, out short rfv)) _resFire = rfv; return true;
            case "RESCOLD": if (short.TryParse(normalized, out short rcv)) _resCold = rcv; return true;
            case "RESPOISON": if (short.TryParse(normalized, out short rpov)) _resPoison = rpov; return true;
            case "RESENERGY": if (short.TryParse(normalized, out short rev)) _resEnergy = rev; return true;
            case "KILLS": if (short.TryParse(normalized, out short killsVal)) Kills = killsVal; return true;
            case "CRIMINALTIMER": if (int.TryParse(normalized, out int ctSec) && ctSec > 0) CriminalTimerRemainingSeconds = ctSec; return true;
            case "MURDERDECAY": if (int.TryParse(normalized, out int mdSec) && mdSec > 0) MurderDecayRemainingSeconds = mdSec; return true;
            case "POISONLEVEL":
                if (byte.TryParse(normalized, out byte plv))
                {
                    if (plv > 0) ApplyPoison(plv);
                    else CurePoison();
                }
                return true;

            // Source-X client m_Targ_Prv_UID — the target cursor's carried
            // uid. Stored so scripts that preset it (SRC.TARGPRV=<uid> before
            // a REVEAL/target) read it back; the cursor flow itself is driven
            // by the target packet.
            case "TARGPRV": SetTag("TARGPRV", normalized); return true;

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
            case "PLEVEL":
                if (int.TryParse(normalized, out int plvSet) && plvSet >= 0 && plvSet <= (int)PrivLevel.Owner)
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
            case "EXP":
                // Route through ChangeExperience so script writes fire
                // @ExpChange/@ExpLevelChange like Source-X EXP assignment.
                if (int.TryParse(normalized, out int expv))
                    ChangeExperience(expv - _exp);
                return true;
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
            case "SPEECHCOLOR":
            case "SPEECHCOLOROVERRIDE": if (ushort.TryParse(normalized, out ushort scv)) _speechColor = scv; return true;
            case "MAXFOLLOWER": if (byte.TryParse(normalized, out byte mfv)) _maxFollower = mfv; return true;
            case "CURFOLLOWER": if (byte.TryParse(normalized, out byte cfv)) _curFollower = cfv; return true;
            // Per-char regen rate overrides (Source-X CChar). Non-D stores seconds*1000,
            // D stores tenths*100; both write the same ms field. <0 = never regen.
            case "REGENHITS": if (long.TryParse(normalized, out long rhs)) _regenHitsRateMs = rhs * 1000; return true;
            case "REGENHITSD": if (long.TryParse(normalized, out long rht)) _regenHitsRateMs = rht * 100; return true;
            case "REGENMANA": if (long.TryParse(normalized, out long rms)) _regenManaRateMs = rms * 1000; return true;
            case "REGENMANAD": if (long.TryParse(normalized, out long rmt)) _regenManaRateMs = rmt * 100; return true;
            case "REGENSTAM": if (long.TryParse(normalized, out long rss)) _regenStamRateMs = rss * 1000; return true;
            case "REGENSTAMD": if (long.TryParse(normalized, out long rst)) _regenStamRateMs = rst * 100; return true;
            case "REGENFOOD": if (long.TryParse(normalized, out long rfs)) _regenFoodRateMs = rfs * 1000; return true;
            case "REGENFOODD": if (long.TryParse(normalized, out long rft)) _regenFoodRateMs = rft * 100; return true;
            case "REGENVALHITS": if (ushort.TryParse(normalized, out ushort rvh)) _regenValHits = rvh; return true;
            case "REGENVALMANA": if (ushort.TryParse(normalized, out ushort rvm)) _regenValMana = rvm; return true;
            case "REGENVALSTAM": if (ushort.TryParse(normalized, out ushort rvs)) _regenValStam = rvs; return true;
            case "REGENVALFOOD": if (ushort.TryParse(normalized, out ushort rvf)) _regenValFood = rvf; return true;
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
            case "BONDED":
                IsBonded = normalized != "0" && !string.IsNullOrEmpty(normalized);
                return true;
            case "TIMERF": // restore a persisted TIMERF/TIMERFMS timer (world load)
                return TryLoadTimerFEntry(value);
            case "POISON": // restore an active poison (world load): level|ticks|remainingMs|source
            {
                var pp = value.Split('|');
                if (pp.Length >= 3 && byte.TryParse(pp[0], out byte plvl)
                    && int.TryParse(pp[1], out int pticks) && long.TryParse(pp[2], out long premMs))
                {
                    Serial psrc = pp.Length > 3 ? ParseSerial(pp[3]) : Serial.Invalid;
                    Poison.Restore(plvl, pticks, premMs, psrc);
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
            case "RESPHYSICALMAX": if (short.TryParse(normalized, out short rphmv)) _resPhysicalMax = rphmv; return true;
            case "RESFIREMAX": if (short.TryParse(normalized, out short rfmv)) _resFireMax = rfmv; return true;
            case "RESCOLDMAX": if (short.TryParse(normalized, out short rcmv)) _resColdMax = rcmv; return true;
            case "RESPOISONMAX": if (short.TryParse(normalized, out short rpmv)) _resPoisonMax = rpmv; return true;
            case "RESENERGYMAX": if (short.TryParse(normalized, out short remv)) _resEnergyMax = remv; return true;
            case "FACTION_GROUP": SetTag("FACTION_GROUP", normalized); return true;
            case "FACTION_SPECIES": SetTag("FACTION_SPECIES", normalized); return true;
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
            case "FIGHTTARGET":
            {
                FightTarget = ParseSerial(normalized);
                return true;
            }
            case "PETAI":
            case "PETAIMODE":
            {
                if (byte.TryParse(normalized, out byte mode) && Enum.IsDefined(typeof(PetAIMode), mode))
                    PetAIMode = (PetAIMode)mode;
                else if (Enum.TryParse<PetAIMode>(normalized, ignoreCase: true, out var parsedMode))
                    PetAIMode = parsedMode;
                return true;
            }
            case "FLEESTEPS":
                if (int.TryParse(normalized, out int fleeSteps)) FleeStepsCurrent = Math.Max(0, fleeSteps);
                return true;
            case "FLEESTEPSMAX":
                if (int.TryParse(normalized, out int fleeMax)) FleeStepsMax = Math.Max(0, fleeMax);
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
                            if (item.BaseId == 0x0EED) item.RemoveFromWorld();
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

        // STATLOCK.n
        if (upperKey.StartsWith("STATLOCK.", StringComparison.Ordinal))
        {
            if (int.TryParse(upperKey.AsSpan("STATLOCK.".Length), out int statIdx) &&
                byte.TryParse(normalized, out byte lockVal))
            {
                SetStatLock(statIdx, lockVal);
            }
            return true;
        }

        // MEMORY.xxx — real memory flags when the suffix is a known type name
        if (upperKey.StartsWith("MEMORY.", StringComparison.Ordinal))
        {
            string memPart = upperKey["MEMORY.".Length..];
            if (TryParseMemoryTypeName(memPart, out MemoryType mt))
            {
                if (normalized is "0" or "")
                    Memory_ClearAllTypes(mt);
                else
                {
                    Serial link = ParseSerial(normalized);
                    if (link.IsValid)
                        Memory_AddObjTypes(link, mt);
                }
                return true;
            }
            SetTag(key, normalized);
            return true;
        }

        // StatLock[n] bracket syntax (Sphere format)
        if (upperKey.StartsWith("STATLOCK[", StringComparison.Ordinal) && upperKey.Contains(']'))
        {
            int si = upperKey.IndexOf('[');
            int ei = upperKey.IndexOf(']');
            if (int.TryParse(upperKey.AsSpan(si + 1, ei - si - 1), out int slIdx) &&
                byte.TryParse(normalized, out byte slVal))
            {
                SetStatLock(slIdx, slVal);
            }
            return true;
        }

        // SkillLock[n] bracket syntax (Sphere format)
        if (upperKey.StartsWith("SKILLLOCK[", StringComparison.Ordinal) && upperKey.Contains(']'))
        {
            int si = upperKey.IndexOf('[');
            int ei = upperKey.IndexOf(']');
            if (int.TryParse(upperKey.AsSpan(si + 1, ei - si - 1), out int skIdx) &&
                skIdx >= 0 && skIdx < _skillLocks.Length &&
                byte.TryParse(normalized, out byte skLockVal))
            {
                _skillLocks[skIdx] = skLockVal;
            }
            return true;
        }

        // Sphere NPC/char properties — round-trip as TAGs
        switch (upperKey)
        {
            case "OKARMA":
                if (TryParseShortSingleOrRange(normalized, out short okv)) _karma = okv;
                return true;
            case "OFAME":
                if (TryParseShortSingleOrRange(normalized, out short ofv)) _fame = ofv;
                return true;
            case "OFOOD":
                if (ushort.TryParse(normalized, out ushort ofoodVal)) _food = ofoodVal;
                return true;
            case "MAXFOOD":
                SetTag("MAXFOOD", normalized);
                return true;
            case "EMOTECOLOR": case "VIRTUALGOLD":
            case "LASTUSED": case "LASTDISCONNECTED": case "NEED": case "SPAWNITEM":
                SetTag(upperKey, value);
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

    private static bool TryNormalizeScriptValue(string key, string value, out string normalized)
    {
        normalized = value.Trim();
        bool skillValue = _skillNameMap.ContainsKey(key.Trim());
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
                double scale = skillValue ? 10d : 1d;
                int min = (int)Math.Round(minVal * scale);
                int max = (int)Math.Round(maxVal * scale);
                int pick = Random.Shared.Next(min, max + 1);
                normalized = pick.ToString();
                return true;
            }
        }

        if (normalized.Contains('.') &&
            double.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double decimalValue))
        {
            normalized = ((int)Math.Round(decimalValue * (skillValue ? 10d : 1d))).ToString();
            return true;
        }

        return true;
    }

    private static uint ParseNamedFlagMask(string value)
    {
        uint result = 0;
        foreach (string token in value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            uint numeric = ParseHexOrDecUInt(token);
            if (numeric != 0 || token.Trim() == "0")
            {
                result |= numeric;
                continue;
            }
            if (DefinitionLoader.StaticResources?.TryResolveDefNameValue(token.Trim(), out long resolved) == true)
                result |= unchecked((uint)resolved);
        }
        return result;
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
        map["DISCORDANCE"] = SkillType.Enticement; // post-UOR name for skill 15
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
            SetSkillRuntime(skillType, skillVal); // runtime set — fires cancelable @SkillChange
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

        // Pack alias whose def name differs from the enum member: the
        // Scripts-X stablemaster brain is brain_animal_trainer=7, which is
        // NpcBrainType.Stable here. Without this, @Create NPC=... failed the
        // parse and stablemasters kept the pre-spawn Monster fallback brain.
        if (normalized.Equals("animal_trainer", StringComparison.OrdinalIgnoreCase))
        {
            brain = NpcBrainType.Stable;
            return true;
        }

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

    private static bool TryParseSpellTypeValue(string value, out SpellType spell)
    {
        spell = SpellType.None;
        string normalized = value.Trim();

        // SpellType is backed by ushort; Enum.IsDefined throws ArgumentException
        // unless the boxed value is the exact underlying type, so range-check
        // and cast to ushort before probing.
        if (int.TryParse(normalized, out int numeric) &&
            numeric >= 0 && numeric <= ushort.MaxValue &&
            Enum.IsDefined(typeof(SpellType), (ushort)numeric))
        {
            spell = (SpellType)numeric;
            return true;
        }

        if (normalized.StartsWith("spell_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[6..];
        else if (normalized.StartsWith("s_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        if (Enum.TryParse<SpellType>(normalized, true, out var named))
        {
            spell = named;
            return true;
        }

        var resources = DefinitionLoader.StaticResources;
        var rid = resources?.ResolveDefName(value.Trim()) ?? ResourceId.Invalid;
        if (rid.IsValid && rid.Type == ResType.SpellDef &&
            rid.Index >= 0 && rid.Index <= ushort.MaxValue &&
            Enum.IsDefined(typeof(SpellType), (ushort)rid.Index))
        {
            spell = (SpellType)rid.Index;
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
        if (key.Equals("SOUND", StringComparison.OrdinalIgnoreCase))
            return EmitScriptSound(args);
        if (key.Equals("NOTOUPDATE", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("NOTOCLEAR", StringComparison.OrdinalIgnoreCase))
        {
            NotoSaveUpdate?.Invoke(this);
            return true;
        }
        if (key.Equals("SPELLEFFECT", StringComparison.OrdinalIgnoreCase))
        {
            OnScriptSpellEffect?.Invoke(this, args, source);
            return true;
        }
        if (key.Equals("EFFECT", StringComparison.OrdinalIgnoreCase))
            return EmitScriptEffect(args);
        if (key.Equals("FACE", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseScriptByte(args, out byte dir))
            {
                var newDirection = (Direction)(dir & 0x07);
                if (newDirection != _direction)
                {
                    _direction = newDirection;
                    OnFacingChanged?.Invoke(this);
                }
            }
            return true;
        }

        // Source-X chained dispatch on FINDID:
        //   FINDID.i_scroll_reaper_form.REMOVE
        // Resolve the item across equipment, pack AND the hidden memory
        // layer, then route the trailing verb. Removing an IT_SPELL memory
        // must also remove its active effect (Source-X: deleting the memory
        // runs Spell_Effect_Remove) — the Scripts-X form toggles rely on it.
        if (key.StartsWith("FINDID.", StringComparison.OrdinalIgnoreCase))
        {
            string chain = key["FINDID.".Length..];
            int chainDot = chain.IndexOf('.');
            if (chainDot > 0 &&
                TryParseHexOrDecUshort(chain[..chainDot].Trim(), out ushort chainId))
            {
                string tail = chain[(chainDot + 1)..].Trim();
                var found = FindItemByBaseId(chainId);
                if (found == null || found.IsDeleted || tail.Length == 0)
                    return true;
                if (tail.Equals("REMOVE", StringComparison.OrdinalIgnoreCase))
                {
                    if (found.ItemType == ItemType.Spell && Memories.Contains(found))
                    {
                        if (SpellMemoryEffectRemover?.Invoke(found) != true)
                        {
                            Memory_Delete(found);
                            found.Delete();
                        }
                    }
                    else
                    {
                        found.RemoveFromWorld();
                    }
                    return true;
                }
                return found.TryExecuteCommand(tail, args, source);
            }
        }

        // Chained dispatch on the dot form (Source-X CChar LayerFind with an
        // expression-evaluated layer arg):
        //   Src.FindLayer.layer_hair.REMOVE      → strip the worn hair
        //   Src.FindLayer.layer_hair.COLOR <hue> → recolor it (property write)
        // <layer> may be a number or a layer DEFNAME.
        if (key.StartsWith("FINDLAYER.", StringComparison.OrdinalIgnoreCase))
        {
            string chain = key["FINDLAYER.".Length..];
            int chainDot = chain.IndexOf('.');
            if (chainDot > 0)
            {
                string tail = chain[(chainDot + 1)..].Trim();
                if (TryResolveLayerToken(chain[..chainDot], out int layerNum) && tail.Length > 0)
                {
                    var worn = GetEquippedItem((Layer)layerNum);
                    if (worn == null || worn.IsDeleted) return true;
                    if (tail.Equals("REMOVE", StringComparison.OrdinalIgnoreCase))
                    {
                        worn.RemoveFromWorld();
                        return true;
                    }
                    if (worn.TryExecuteCommand(tail, args, source))
                        return true;
                    return args.Length > 0 && worn.TrySetProperty(tail, args);
                }
            }
        }

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
                        worn.RemoveFromWorld();
                        return true;
                    }
                    // Property-set fallback: FINDLAYER(5).COLOR <hue> is a
                    // property write, not a verb (the flash-robe recolor).
                    if (worn.TryExecuteCommand(tail, args, source))
                        return true;
                    return args.Length > 0 && worn.TrySetProperty(tail, args);
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
            case "DUPE":
            {
                var world = ResolveWorld?.Invoke();
                if (world == null) return false;

                var clone = world.CreateCharacter();
                clone.BaseId = BaseId;
                clone.BodyId = BodyId;
                clone.Name = Name;
                clone.Hue = Hue;
                clone.Direction = Direction;
                clone.Str = Str;
                clone.Dex = Dex;
                clone.Int = Int;
                clone.MaxHits = MaxHits;
                clone.Hits = Hits;
                clone.MaxStam = MaxStam;
                clone.Stam = Stam;
                clone.MaxMana = MaxMana;
                clone.Mana = Mana;
                clone.NpcBrain = NpcBrain;
                foreach (var (tagKey, tagValue) in Tags.GetAll())
                {
                    if (!EngineTags.IsEphemeral(tagKey))
                        clone.Tags.Set(tagKey, tagValue);
                }
                foreach (SkillType skill in Enum.GetValues<SkillType>())
                {
                    if (skill != SkillType.None && skill < SkillType.Qty)
                        clone.SetSkill(skill, GetSkill(skill));
                }

                if (!world.PlaceCharacter(clone, Position))
                {
                    world.DeleteObject(clone);
                    clone.Delete();
                    return false;
                }
                return true;
            }
            case "SKILL":
            {
                string skillToken = (args ?? "").Trim();
                // Source-X CHV_SKILL: SKILL FAIL (FindSkillKey → SKILL_NONE)
                // cancels the character's active skill. SphereNet used to drop
                // it because FAIL is neither a skill name nor an id.
                if (skillToken.Equals("FAIL", StringComparison.OrdinalIgnoreCase) ||
                    skillToken.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
                    skillToken == "-1")
                {
                    int abortedSkill = ClearActiveSkillPending();
                    if (abortedSkill >= 0)
                        ActiveSkillAborted?.Invoke(this, abortedSkill);
                    return true;
                }
                SkillType skill;
                if (!_skillNameMap.TryGetValue(skillToken, out skill))
                {
                    if (!int.TryParse(skillToken, out int skillId) ||
                        skillId < 0 || skillId >= (int)SkillType.Qty)
                        return false;
                    skill = (SkillType)skillId;
                }

                Action = skill;
                return OnScriptSkillUse?.Invoke(this, skill) ?? true;
            }
            case "EQUIP":
            {
                // Source-X CHV_EQUIP: SRC.EQUIP <uid> equips that item on this
                // character and fires @Equip. The arg used to be dropped into
                // an unused PendingEquip flag, so script-created gear (combat
                // bonus memories, etc.) was never worn and its @Equip effect
                // never ran. An arg-less EQUIP keeps the legacy no-op flag.
                var equipUid = ParseSerial((args ?? "").Trim());
                if (equipUid.IsValid)
                {
                    var equipWorld = ResolveWorld?.Invoke();
                    var toEquip = equipWorld?.FindObject(equipUid) as Item;
                    if (toEquip != null && ScriptEquipItem != null)
                        return ScriptEquipItem(this, toEquip);
                }
                PendingEquip = true;
                return true;
            }
            case "NPCSPELL":
            {
                var raw = args.Trim();
                if (TryParseSpellTypeValue(raw, out var spell))
                    NpcSpellAdd(spell);
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
                if (OnLifecycleKill != null) OnLifecycleKill(this, null);
                else Kill();
                return true;
            case "RESURRECT":
                if (OnLifecycleResurrect != null) OnLifecycleResurrect(this);
                else Resurrect();
                return true;
            case "CURE":
                CurePoison();
                return true;
            case "REVEAL":
                ClearStatFlag(StatFlag.Hidden);
                ClearStatFlag(StatFlag.Invisible);
                return true;
            case "ATTACK":
            case "KILLTARGET":
            {
                // ATTACK <uid> — set this character's combat target.
                if (!string.IsNullOrWhiteSpace(args))
                {
                    var atkTarget = ParseSerial(args.Trim());
                    if (atkTarget.IsValid)
                    {
                        FightTarget = atkTarget;
                        SetStatFlag(StatFlag.War);
                    }
                }
                return true;
            }
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
                if (IsMounted)
                    animAction = MapAnimToMounted(animAction);
                else
                    animAction = Combat.BodyAnimTranslator.Translate(BodyId, animAction);
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
            // Source-X GO* teleport variants — previously GM-speech-only, so
            // scripted <char>.GOUID/<char>.GOCHAR were silent no-ops.
            case "GOUID":
            case "GOITEMID":
            {
                var world = Objects.ObjBase.ResolveWorld?.Invoke();
                if (world == null) return true;
                string uidStr = args.Trim();
                if (uidStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) uidStr = uidStr[2..];
                else if (uidStr.StartsWith('0') && uidStr.Length > 1) uidStr = uidStr[1..];
                if (uint.TryParse(uidStr, System.Globalization.NumberStyles.HexNumber, null, out uint goUid))
                {
                    var dest = world.FindObject(new Serial(goUid));
                    if (dest != null)
                        MoveTo(dest.GetTopLevelPosition());
                }
                return true;
            }
            case "GOCHAR":
            {
                // GOCHAR <name> — teleport to the first character matching the name.
                var world = Objects.ObjBase.ResolveWorld?.Invoke();
                string wanted = args.Trim();
                if (world == null || wanted.Length == 0) return true;
                foreach (var obj in world.GetAllObjects())
                {
                    if (obj is Character gc && !gc.IsDeleted &&
                        gc != this &&
                        string.Equals(gc.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        MoveTo(gc.Position);
                        break;
                    }
                }
                return true;
            }
            case "GOCHARID":
            {
                // Source-X CHV_GOCHARID — teleport to the first char whose
                // creature/body id (or chardef) matches the argument.
                var world = Objects.ObjBase.ResolveWorld?.Invoke();
                string wanted = args.Trim();
                if (world == null || wanted.Length == 0) return true;
                long wantedId = 0;
                if (!SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(wanted.AsSpan(), out wantedId))
                {
                    var rid = Definitions.DefinitionLoader.StaticResources?.ResolveDefName(wanted)
                        ?? Core.Types.ResourceId.Invalid;
                    if (rid.IsValid && rid.Type == Core.Enums.ResType.CharDef)
                        wantedId = rid.Index;
                }
                if (wantedId == 0) return true;
                foreach (var obj in world.GetAllObjects())
                {
                    if (obj is Character gc && !gc.IsDeleted && gc != this &&
                        (gc.BodyId == wantedId || gc.CharDefIndex == wantedId))
                    {
                        MoveTo(gc.Position);
                        break;
                    }
                }
                return true;
            }
            case "GOTYPE":
            {
                // Source-X CHV_GOTYPE — teleport to the first item of the
                // given IT_TYPE (numeric or t_* typedef name).
                var world = Objects.ObjBase.ResolveWorld?.Invoke();
                string wanted = args.Trim();
                if (world == null || wanted.Length == 0) return true;
                long typeVal = 0;
                if (!SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(wanted.AsSpan(), out typeVal))
                {
                    var res = Definitions.DefinitionLoader.StaticResources;
                    if (res != null)
                    {
                        var rid = res.ResolveDefName(wanted);
                        if (rid.IsValid && rid.Type == Core.Enums.ResType.TypeDef)
                            typeVal = rid.Index;
                        else if (res.TryResolveDefNameValue(wanted, out long defVal))
                            typeVal = defVal;
                    }
                }
                if (typeVal == 0) return true;
                foreach (var obj in world.GetAllObjects())
                {
                    if (obj is Items.Item it && !it.IsDeleted && (long)it.ItemType == typeVal)
                    {
                        MoveTo(it.GetTopLevelPosition());
                        break;
                    }
                }
                return true;
            }
            case "GOCLI":
            case "GOSOCK":
            {
                // Source-X TeleportToCli — nth online client / by socket id.
                // Client enumeration lives at the host; resolved via hook.
                if (!int.TryParse(args.Trim(), out int cliArg)) cliArg = 0;
                var found = key.Equals("GOCLI", StringComparison.OrdinalIgnoreCase)
                    ? FindCharByClientIndex?.Invoke(cliArg)
                    : FindCharBySocketId?.Invoke(cliArg);
                if (found != null && found != this)
                    MoveTo(found.Position);
                return true;
            }
            case "AFK":
            {
                // Source-X CHV_AFK — the AFK "flag" is the napping action;
                // arg forces ON (nonzero), no/zero arg toggles.
                bool force = args.Trim().Length > 0 &&
                    SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(args.Trim().AsSpan(), out long av) && av != 0;
                bool newMode = args.Trim().Length > 0 ? force : !_isAfk;
                if (newMode != _isAfk)
                {
                    _isAfk = newMode;
                    source.SysMessage(newMode ? "You are now AFK." : "You are no longer AFK.");
                }
                return true;
            }
            case "HEAR":
            {
                // Source-X CHV_HEAR — route text to this char only: players
                // see a sysmessage, NPCs process it as heard speech.
                OnHearRouted?.Invoke(this, args, null);
                return true;
            }
            case "UNDERWEAR":
            {
                // Source-X CHV_UNDERWEAR — flip the human "nude" hue bit.
                if (!IsPlayer && BodyId is not (0x190 or 0x191 or 0x25D or 0x25E))
                    return true;
                Hue = new Color((ushort)(Hue.Value ^ 0x8000));
                MarkDirty(DirtyFlag.Hue);
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
                // Source-X CChar::r_Verb BOUNCE: return the dragged item
                // (DRAGGING tag, set by the pickup packet) to the backpack
                // and cancel the client drag cursor. The host hook resolves
                // the owning client for the packet work; headless fallback
                // just re-packs the item silently.
                OnDragRelease?.Invoke(this, false);
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
                        // EFFECT_BOLT (type 0) moves and must rotate (oneDirection
                        // false); stationary effect types keep it fixed.
                        effSpeed, effDur, fixedDir: effType != 0, explode: effExplode),
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
                SpawnAndEquipItem(name, amount, dice,
                    newbie: key.Equals("ITEMNEWBIE", StringComparison.OrdinalIgnoreCase));
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
                // Preserve both cliloc identifiers, duration and every
                // formatter argument in their native packet fields.
                var parts = args.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 1 && TryParseScriptUShort(parts[0], out ushort icon))
                {
                    if (!IsValidBuffIcon(icon))
                        return true; // Source-X CClient.cpp:1071 "Invalid AddBuff icon ID"
                    uint titleCliloc = parts.Length >= 2 && TryParseScriptUInt(parts[1], out uint c1) ? c1 : 0;
                    uint descriptionCliloc = parts.Length >= 3 && TryParseScriptUInt(parts[2], out uint c2) ? c2 : 0;
                    ushort duration = 0;
                    if (parts.Length >= 4 && TryParseScriptUShort(parts[3], out ushort d))
                        duration = d;
                    string[] buffArgs = parts.Length >= 5 ? parts[4..] : [];
                    SendPacketToOwner?.Invoke(this, new SphereNet.Network.Packets.Outgoing.PacketBuffIcon(
                        Uid.Value, icon, true, duration, titleCliloc, descriptionCliloc, buffArgs));
                }
                return true;
            }
            case "REMOVEBUFF":
            {
                // Source-X: REMOVEBUFF icon
                if (TryParseScriptUShort(args.Trim(), out ushort icon) && IsValidBuffIcon(icon))
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
            case "SYSMESSAGELOCEX":
            {
                // Source-X: hue,cliloc,affixFlags,affix,args...
                var parts = args.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 4
                    && TryParseScriptUShort(parts[0], out ushort hue)
                    && TryParseScriptUInt(parts[1], out uint cliloc))
                {
                    int flags = parts.Length > 2 && int.TryParse(parts[2], out int parsedFlags)
                        ? parsedFlags : 0;
                    string affix = parts[3];
                    string argText = parts.Length > 4 ? string.Join('\t', parts[4..]) : "";
                    SendPacketToOwner?.Invoke(this,
                        new SphereNet.Network.Packets.Outgoing.PacketClilocMessageAffix(
                            Serial.Invalid.Value, 0xFFFF, 6, hue, 3, cliloc,
                            "System", affix, argText,
                            prepend: (flags & 0x01) != 0,
                            system: (flags & 0x02) != 0));
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
            case "NEWNPC":
            {
                // Source-X SSV_NEWNPC. Source-X creates the NPC unplaced until
                // NEW.P= — SphereNet has no unplaced-char limbo, so the NPC
                // lands at the invoker's feet (the usable default) and is
                // reachable as <SERV.LASTNEWCHAR>.
                SpawnNpcFromScript?.Invoke(this, args?.Trim() ?? "");
                return true;
            }
            case "VISIBLE":
            {
                // Source-X CHV_VISIBLE: drop invisibility/hiding outright —
                // legacy scripts pair it with INVIS.
                ClearStatFlag(StatFlag.Invisible);
                ClearStatFlag(StatFlag.Hidden);
                MarkDirty(DirtyFlag.StatFlags);
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
                if (OnScriptDismount != null)
                    OnScriptDismount(this);
                else
                    ClearStatFlag(StatFlag.OnHorse);
                return true;
            }
            case "MOUNT":
                // Source-X Horse_Mount as a VERB on the NPC: seat its owner.
                // (The same token is a property read elsewhere — SRC.MOUNT —
                // returning the worn mount item; the verb path is the write.)
                OnScriptMount?.Invoke(this);
                return true;
            case "MAKEMYPET":
            {
                // Source-X MAKEMYPET <char>: make THIS npc the argument's pet.
                var petWorld = ResolveWorld?.Invoke();
                Serial ownerUid = ParseSerial(args.Trim());
                var newOwner = ownerUid.IsValid
                    ? petWorld?.FindObject(ownerUid) as Character
                    : null;
                if (newOwner != null)
                {
                    SetStatFlag(StatFlag.Pet);
                    TryAssignOwnership(newOwner, newOwner, summoned: false,
                        enforceFollowerCap: false);
                }
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
                ushort polyBody = ResolvePolyBody(args);
                if (polyBody != 0)
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
            case "WAKE":
            {
                // Source-X CHV_WAKE — the missing counterpart of SLEEP.
                ClearStatFlag(StatFlag.Sleeping);
                return true;
            }
            case "EQUIPHALO":
            {
                // Source-X CHV_EQUIPHALO: conjure a glowing light source into
                // the off-hand (ITEMID_LIGHT_SRC); optional arg = seconds to last.
                var haloWorld = Objects.ObjBase.ResolveWorld?.Invoke();
                if (haloWorld == null) return true;
                var halo = haloWorld.CreateItem();
                halo.BaseId = 0x1647;
                halo.ItemType = ItemType.LightLit;
                halo.Name = "a glowing halo";
                halo.SetAttr(ObjAttributes.Move_Never);
                if (long.TryParse(args.Trim(), out long haloSec) && haloSec > 0)
                    halo.SetTimeout(Environment.TickCount64 + haloSec * 1000L);
                Equip(halo, Layer.TwoHanded);
                return true;
            }
            case "EQUIPARMOR":
            {
                // Source-X ItemEquipArmor: pull wearable armor out of the pack
                // into every free armor layer.
                if (Backpack == null) return true;
                foreach (var candidate in Backpack.Contents.ToArray())
                {
                    if (candidate.GetArmorDefense() <= 0) continue;
                    var layer = ResolveWearLayer(candidate);
                    if (layer == Layer.None || GetEquippedItem(layer) != null) continue;
                    if (!CanEquip(candidate, layer, out _)) continue;
                    Backpack.RemoveItem(candidate);
                    Equip(candidate, layer);
                }
                return true;
            }
            case "EQUIPWEAPON":
            {
                // Source-X ItemEquipWeapon: equip the best weapon in the pack
                // (highest attack rating as the proxy) when the hands are free.
                if (Backpack == null || GetEquippedItem(Layer.OneHanded) != null ||
                    GetEquippedItem(Layer.TwoHanded) != null)
                    return true;
                Item? best = null;
                int bestScore = -1;
                foreach (var candidate in Backpack.Contents)
                {
                    var layer = ResolveWearLayer(candidate);
                    if (layer is not (Layer.OneHanded or Layer.TwoHanded)) continue;
                    int score = candidate.GetWeaponAttack();
                    if (score > bestScore) { bestScore = score; best = candidate; }
                }
                if (best != null && bestScore > 0)
                {
                    var layer = ResolveWearLayer(best);
                    if (CanEquip(best, layer, out _))
                    {
                        Backpack.RemoveItem(best);
                        Equip(best, layer);
                    }
                }
                return true;
            }
            case "SUICIDE":
            {
                if (OnLifecycleKill != null) OnLifecycleKill(this, null);
                else Kill();
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
                ushort bowAnim = IsMounted
                    ? MapAnimToMounted(32)
                    : Combat.BodyAnimTranslator.Translate(BodyId, 32);
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketAnimation(Uid.Value, bowAnim),
                    0);
                return true;
            }
            case "SALUTE":
            {
                ushort saluteAnim = IsMounted
                    ? MapAnimToMounted(33)
                    : Combat.BodyAnimTranslator.Translate(BodyId, 33);
                BroadcastNearby?.Invoke(Position, 18,
                    new SphereNet.Network.Packets.Outgoing.PacketAnimation(Uid.Value, saluteAnim),
                    0);
                return true;
            }
            // Overhead speech (Source-X CChar SAY/SAYU/EMOTE): broadcast to
            // nearby clients as the character's spoken line — NOT a private
            // system message to the source (the base ObjBase handler does the
            // latter, which left NPC dialog/barks invisible). SAY = ASCII
            // (0x1C), SAYU/SAYUC = Unicode (0xAE), EMOTE = emote message type.
            case "SAY":
                BroadcastSpeech(0, unicode: false, args);
                return true;
            case "SAYU":
            case "SAYUC":
                BroadcastSpeech(0, unicode: true, args);
                return true;
            case "EMOTE":
                BroadcastSpeech(2, unicode: false, args);
                return true;
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
                // Source-X CChar::r_Verb DROP: release the dragged item
                // (DRAGGING tag) to the ground at the character's feet.
                OnDragRelease?.Invoke(this, true);
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
                // Source-X CHV_FORGIVE releases from jail. Only trigger the real
                // (speech-path) release when actually jailed, so .forgive on a free
                // char never teleports them; the release hook unfreezes + teleports.
                if (TryGetTag("JAIL_RELEASE", out _))
                    OnJailReleaseRequested?.Invoke(this);
                CombatState.Forgive();
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
            case "UNEQUIP":
            {
                // Source-X CHV_UNEQUIP <uid>: bounce the given item into my
                // backpack (or drop at my feet when it will not fit).
                var world = Objects.ObjBase.ResolveWorld?.Invoke();
                if (world == null || !TryParseVerbUid(args, out uint unUid)) return false;
                var item = world.FindItem(new Serial(unUid));
                if (item == null || item.IsDeleted) return false;
                return BounceItemToPack(item, world);
            }
            case "SUMMONTO":
            {
                // Source-X CHV_SUMMONTO: teleport me to the summoner (SRC). When
                // the caller is a client we move to it; an explicit uid argument
                // overrides and moves me to that object instead.
                var world = Objects.ObjBase.ResolveWorld?.Invoke();
                if (world != null && TryParseVerbUid(args, out uint toUid))
                {
                    var dest = world.FindObject(new Serial(toUid));
                    if (dest != null) { MoveTo(dest.GetTopLevelPosition()); return true; }
                    return false;
                }
                var srcChar = (source as IClientContext)?.Character;
                if (srcChar != null) { MoveTo(srcChar.Position); return true; }
                return false;
            }
            case "WHERE":
            {
                // Source-X CHV_WHERE: report my location to the caller.
                var world = Objects.ObjBase.ResolveWorld?.Invoke();
                string regionName = world?.FindRegion(Position)?.Name ?? "";
                source.SysMessage(regionName.Length > 0
                    ? $"{Name} is in {regionName} at {X},{Y},{Z} (map {Position.Map})."
                    : $"{Name} is at {X},{Y},{Z} (map {Position.Map}).");
                return true;
            }
            case "CONTROL":
            {
                // Source-X CHV_CONTROL: the calling client takes control
                // (ownership) of me. Mirrors the GM .CONTROL target result.
                var gm = (source as IClientContext)?.Character;
                if (gm == null) return false;
                TryAssignOwnership(gm, gm, summoned: false, enforceFollowerCap: false);
                source.SysMessage($"You now control {Name}.");
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

    /// <summary>Parse a direction token — numeric 0-7 or a compass code
    /// (N/NE/E/SE/S/SW/W/NW), matching Source-X GetDirStr.</summary>
    private static bool TryParseDirectionToken(string token, out Direction dir)
    {
        dir = Direction.North;
        token = token.Trim();
        if (token.Length == 0) return false;
        if (int.TryParse(token, out int n) && n >= 0 && n <= 7)
        {
            dir = (Direction)n;
            return true;
        }
        switch (token.ToUpperInvariant())
        {
            case "N": dir = Direction.North; return true;
            case "NE": dir = Direction.NorthEast; return true;
            case "E": dir = Direction.East; return true;
            case "SE": dir = Direction.SouthEast; return true;
            case "S": dir = Direction.South; return true;
            case "SW": dir = Direction.SouthWest; return true;
            case "W": dir = Direction.West; return true;
            case "NW": dir = Direction.NorthWest; return true;
            default: return false;
        }
    }

    /// <summary>One-tile step delta for a direction (Source-X CPointMap::Move).</summary>
    private static void GetDirectionStep(Direction dir, out int dx, out int dy)
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

    /// <summary>Parse a verb argument as a hex/decimal object serial
    /// (Source-X GetArgVal on a UID). Accepts 0x-, leading-0- and bare forms.</summary>
    private static bool TryParseVerbUid(string args, out uint uid)
    {
        uid = 0;
        string raw = args.Trim();
        if (raw.Length == 0) return false;
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw[2..];
        else if (raw.StartsWith('0') && raw.Length > 1) raw = raw[1..];
        return uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out uid);
    }

    /// <summary>Detach an item from wherever it is and drop it into my backpack,
    /// falling back to the ground at my feet — Source-X CChar::ItemBounce.</summary>
    private bool BounceItemToPack(Item item, World.GameWorld world)
    {
        if (item.IsEquipped && item.ContainedIn == Uid)
        {
            Unequip(item.EquipLayer);
        }
        else
        {
            var parent = world.FindObject(item.ContainedIn);
            if (parent is Item container)
                container.RemoveItem(item);
            else if (parent is Character wearer && wearer.GetEquippedItem(item.EquipLayer) == item)
                wearer.Unequip(item.EquipLayer);
            else
                world.HideFromSector(item);
        }

        var pack = Backpack;
        if (pack != null && !ReferenceEquals(pack, item) && pack.TryAddItem(item))
            return true;
        world.PlaceItemWithDecay(item, Position);
        return true;
    }

    /// <summary>Pending NEWITEM creation id (set by script NEWITEM command).</summary>
    public string? NewItemId { get; set; }

    /// <summary>Pending EQUIP flag (set by script EQUIP command).</summary>
    public bool PendingEquip { get; set; }

    // --- Guild/stone member properties ---

    /// <summary>Source-X CCharPlayer GUILD/TOWN keyword: the char's guild or
    /// town STONE, with the trailing sub-key forwarded to that stone item
    /// (&lt;GUILD.MASTERUID&gt;, &lt;TOWN.MEMBERFROMUID.x.PRIV&gt;, ...). A bare
    /// &lt;GUILD&gt;/&lt;TOWN&gt; returns the stone uid (empty when unaffiliated,
    /// so IF (&lt;GUILD&gt;) reads false).</summary>
    private bool TryResolveStoneKeyword(string key, out string value)
    {
        value = "";
        string upper = key.ToUpperInvariant();
        bool isTown;
        string sub;
        if (upper == "GUILD") { isTown = false; sub = ""; }
        else if (upper == "TOWN") { isTown = true; sub = ""; }
        else if (upper.StartsWith("GUILD.", StringComparison.Ordinal)) { isTown = false; sub = key[6..]; }
        else if (upper.StartsWith("TOWN.", StringComparison.Ordinal)) { isTown = true; sub = key[5..]; }
        else return false;

        var gm = ResolveGuildManager?.Invoke(Uid);
        var rec = isTown ? gm?.FindTownFor(Uid) : gm?.FindGuildFor(Uid);
        if (rec == null)
        {
            // Unaffiliated: <GUILD> is empty/false; a sub-key is likewise empty.
            value = "";
            return true;
        }
        if (sub.Length == 0)
        {
            value = $"0{rec.StoneUid.Value:X}";
            return true;
        }
        var stone = ResolveWorld?.Invoke()?.FindObject(rec.StoneUid);
        if (stone == null)
        {
            value = "";
            return true;
        }
        return stone.TryGetProperty(sub, out value);
    }

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
            case "GUILDTITLE": // Source-X CStoneMember TITLE alias
            case "SHOWABBREV":
                break;
            default:
                return false;
        }

        var gm = ResolveGuildManager?.Invoke(Uid);
        if (gm == null) return false;
        var guild = gm.FindGuildRecordFor(Uid);
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
            case "GUILDTITLE":
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
            case "GUILDTITLE": // Source-X CStoneMember TITLE alias
            case "SHOWABBREV":
                break;
            default:
                return false;
        }

        var gm = ResolveGuildManager?.Invoke(Uid);
        if (gm == null) return false;
        var guild = gm.FindGuildRecordFor(Uid);
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
            case "GUILDTITLE":
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
        if (!upper.StartsWith("PARTY.", StringComparison.Ordinal)) return false;

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

        // PARTY.MASTER.<sub> — forward to the party master as a reference
        // (Source-X CParty r_GetRef), so <PARTY.MASTER.NAME>/.REGION.NAME chain.
        if (sub.StartsWith("MASTER.", StringComparison.Ordinal))
        {
            string msub = key["PARTY.MASTER.".Length..]; // preserve case
            var masterChar = party != null ? ResolveWorld?.Invoke()?.FindChar(party.Master) : null;
            value = masterChar != null && masterChar.TryGetProperty(msub, out var mval) ? mval : "0";
            return true;
        }

        // PARTY.MEMBER.n  and  PARTY.MEMBER.n.<sub> (forward to the member char)
        if (sub.StartsWith("MEMBER.", StringComparison.Ordinal))
        {
            string rest = sub["MEMBER.".Length..];
            int d = rest.IndexOf('.');
            string idxStr = d < 0 ? rest : rest[..d];
            if (int.TryParse(idxStr, out int memberIdx) &&
                party != null && memberIdx >= 0 && memberIdx < party.MemberCount)
            {
                var memberSerial = party.Members[memberIdx];
                if (d < 0)
                {
                    value = $"0{memberSerial.Value:X}";
                    return true;
                }
                string msub = key[("PARTY.MEMBER.".Length + idxStr.Length + 1)..]; // preserve case
                var memberChar = ResolveWorld?.Invoke()?.FindChar(memberSerial);
                value = memberChar != null && memberChar.TryGetProperty(msub, out var mv) ? mv : "0";
                return true;
            }
            value = "0";
            return true;
        }

        // PARTY.ISSAMEPARTYOF uid
        if (sub.StartsWith("ISSAMEPARTYOF", StringComparison.Ordinal))
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
        if (sub.StartsWith("TAG.", StringComparison.Ordinal))
        {
            var tagKey = key["PARTY.TAG.".Length..]; // preserve original case
            if (party != null && party.TryGetTag(tagKey, out string tagVal))
                value = tagVal;
            else
                value = "";
            return true;
        }

        // PARTY.TAGAT.n / PARTY.TAGAT.n.KEY / PARTY.TAGAT.n.VAL
        if (sub.StartsWith("TAGAT.", StringComparison.Ordinal))
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
        if (!upper.StartsWith("PARTY.", StringComparison.Ordinal)) return false;

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
        if (sub.StartsWith("TAG.", StringComparison.Ordinal))
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
            case "CREATE":
            {
                // Source-X CParty PDV_CREATE — ensure this char has a party (as
                // master). The party-invite flow calls PARTY.CREATE <flag>,<uid>;
                // any token resolving to a real char is added as a member (the
                // leading flag resolves to no char and is skipped).
                if (pm == null) return true;
                var party = pm.FindParty(Uid) ?? pm.CreateParty(Uid);
                var world = ResolveWorld?.Invoke();
                foreach (var tok in args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (uint.TryParse(tok.TrimStart('0').TrimStart('x', 'X'),
                            System.Globalization.NumberStyles.HexNumber, null, out uint memUid) &&
                        memUid != Uid.Value && world?.FindChar(new Serial(memUid)) != null)
                    {
                        party.AddMember(new Serial(memUid));
                    }
                }
                return true;
            }
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
                if (SendPacketToOwner != null && !string.IsNullOrEmpty(args))
                {
                    SendPacketToOwner(this, new SphereNet.Network.Packets.Outgoing.PacketSpeechUnicodeOut(
                        0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System", args));
                }
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
                CTags.RemoveByPrefix(args?.Trim() ?? string.Empty);
                return true;
            }
            case "TAGLIST":
            {
                // Source-X CV_TAGLIST — dump this character's TAGs to the console.
                foreach (var (k, v) in Tags.GetAll())
                    source.SysMessage($"TAG.{k} = {v}");
                return true;
            }
        }
        return false;
    }

    public override bool OnTick()
    {
        long now = Environment.TickCount64;

        if (IsStatFlag(StatFlag.SpiritSpeak) &&
            TryGetTag("SPIRITSPEAK_UNTIL", out string? spiritUntilText) &&
            long.TryParse(spiritUntilText, out long spiritUntil) &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= spiritUntil)
        {
            ClearStatFlag(StatFlag.SpiritSpeak);
            RemoveTag("SPIRITSPEAK_UNTIL");
        }

        if (IsDead) return true;

        // HP regen: Source/Sphere REGEN0 interval, affected by hunger. Suspended while poisoned
        // so the poison can actually whittle the victim down.
        long hitRateMs = ResolveRegenRateMs(_regenHitsRateMs, RegenHitsSeconds, 40000);
        if (hitRateMs >= 0 && now >= _nextHitRegen && _hits < _maxHits && Poison.Level == 0)
        {
            int regenAmount = _food > 0 ? (_regenValHits > 0 ? _regenValHits : 1) : 0;
            // Source-X CCharStat::Stats_Regen: the human race regens +2 extra hp.
            if (regenAmount > 0 && IsHuman)
                regenAmount += 2;
            if (regenAmount > 0)
            {
                _hits = (short)Math.Min(_hits + regenAmount, _maxHits);
                MarkDirty(DirtyFlag.Stats);
            }
            _nextHitRegen = now + hitRateMs;
        }

        // Mana regen: Source/Sphere REGEN1 (STAT_INT) interval. Source-X Stats_GetRegenVal
        // returns a flat base (maximum(1, REGENVAL)) per tick — it is NOT scaled
        // by INT. The old INT/50 let a high-INT caster (lich, elemental: INT 400+)
        // regen 8-9 mana every 3s, matching or exceeding its spend, so its pool
        // never ran dry and it cast forever. Meditation is the way to regen faster.
        long manaRateMs = ResolveRegenRateMs(_regenManaRateMs, RegenManaSeconds, 20000);
        if (manaRateMs >= 0 && now >= _nextManaRegen && _mana < _maxMana)
        {
            int regenAmount = _regenValMana > 0 ? _regenValMana : 1;
            if (IsStatFlag(StatFlag.Meditation))
                regenAmount += Math.Max(1, SkillEngine.GetEffect(SkillType.Meditation,
                    SkillEngine.GetAdjustedSkill(this, SkillType.Meditation), 1));
            int focus = SkillEngine.GetAdjustedSkill(this, SkillType.Focus);
            if (focus > 0 && SkillEngine.UseQuick(this, SkillType.Focus, focus / 10))
                regenAmount += focus / 200;
            _mana = (short)Math.Min(_mana + regenAmount, _maxMana);
            if (_mana >= _maxMana && IsStatFlag(StatFlag.Meditation))
                ClearStatFlag(StatFlag.Meditation);
            MarkDirty(DirtyFlag.Stats);
            _nextManaRegen = now + manaRateMs;
        }

        // Stam regen: Source-X Stats_GetRegenVal is a flat max(1, REGENVAL) per
        // tick — same as hits/mana; it is NOT scaled by DEX or MaxStam. The old
        // dex/30 and maxStam/20 fallbacks were invented and made big creatures
        // recover stamina many times faster than reference.
        long stamRateMs = ResolveRegenRateMs(_regenStamRateMs, RegenStamSeconds, 10000);
        if (stamRateMs >= 0 && now >= _nextStamRegen && _stam < _maxStam)
        {
            int regenAmt = _regenValStam > 0 ? _regenValStam : 1;
            int focus = SkillEngine.GetAdjustedSkill(this, SkillType.Focus);
            if (focus > 0 && SkillEngine.UseQuick(this, SkillType.Focus, focus / 10))
                regenAmt += focus / 100;
            _stam = (short)Math.Min(_stam + regenAmt, _maxStam);
            MarkDirty(DirtyFlag.Stats);
            _nextStamRegen = now + stamRateMs;
        }

        // Hunger decay: Source-X m_iRegenRate[STAT_FOOD] = 60 minutes per point.
        long foodRateMs = ResolveRegenRateMs(_regenFoodRateMs, RegenFoodSeconds, 3_600_000);
        if (_isPlayer && foodRateMs >= 0 && now >= _nextFoodDecay)
        {
            if (_food > 0)
                _food = (ushort)Math.Max(0, _food - (_regenValFood > 0 ? _regenValFood : 1));
            _nextFoodDecay = now + foodRateMs;

            // Let scripts react to hunger (@Hunger trigger).
            OnHungerDecay?.Invoke(this);

            // Source-X starvation only halts regeneration (Stats_Regen checks
            // IsHungry); it never drains stamina or health. An invented
            // "starvation bite" used to chip stam/hp here — a hungry AFK
            // character slowly damaged itself, which the reference never does.
        }

        // Poison tick
        ProcessPoisonTick(now);

        // Field damage tick — periodic damage while standing in a fire/poison
        // field, so a stationary victim still takes damage (not only on step).
        if (now >= _nextFieldTick)
        {
            _nextFieldTick = now + 1000;
            ApplyStandingFieldDamage();
        }

        // Criminal timer expiry
        CombatState.ExpireCriminalTimer(now);

        // Timed jail auto-release. Gated on Freeze so only jailed/frozen chars
        // pay the tag lookup; the release hook clears Freeze so it fires once.
        if (IsStatFlag(StatFlag.Freeze) && IsJailExpired())
            OnJailReleaseRequested?.Invoke(this);

        // Memory item ticks
        MemoryState.Tick(now);

        return true;
    }

    // Source-X REGENx rates are seconds-to-recover-one-point (CServerConfig.cpp:1075
    // multiplies the ini value by MSECS_PER_SEC). A zero/unset rate uses the fallback.
    internal static int RegenSecondsToMs(int seconds, int fallbackMs)
    {
        return seconds > 0 ? seconds * 1000 : fallbackMs;
    }

    // Resolves a stat's effective regen delay (ms), mirroring CChar::Stats_GetRegenRate:
    // per-char < 0 = never regen (returns -1), == 0 = fall back to the global rate,
    // > 0 = per-char override.
    private static long ResolveRegenRateMs(long perCharMs, int globalSeconds, int fallbackMs)
    {
        if (perCharMs < 0) return -1;
        if (perCharMs > 0) return perCharMs;
        return RegenSecondsToMs(globalSeconds, fallbackMs);
    }

    // Effective rate for a property READ: honours a per-char override (incl. a
    // negative "never" value), otherwise reports the global rate. In ms.
    private static long EffectiveRegenRateMsForRead(long perCharMs, int globalSeconds, int fallbackMs)
        => perCharMs != 0 ? perCharMs : RegenSecondsToMs(globalSeconds, fallbackMs);

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
                    child.RemoveFromWorld();
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
    private void SpawnAndEquipItem(string defname, int amount, string? dice, bool newbie = false)
    {
        if (string.IsNullOrWhiteSpace(defname)) return;
        var world = ResolveWorld?.Invoke();
        if (world == null) return;

        string picked = Definitions.TemplateEngine.PickRandomItemDefName(defname);
        if (string.IsNullOrWhiteSpace(picked)) return;

        var resources = Definitions.DefinitionLoader.StaticResources;
        if (resources == null) return;
        var rid = resources.ResolveDefName(picked);
        if (rid.IsValid && rid.Type == Core.Enums.ResType.Template)
        {
            // Sequential template in defname position (ITEM=loot_demon_balron):
            // Source-X CItem::CreateHeader accepts RES_TEMPLATE and pours the
            // whole row list into the char's pack (CItem.cpp:494/515 →
            // ReadTemplate). PickRandomItemDefName already consumed pure
            // random-pool templates, so what reaches here is a loot list.
            var lootPack = Backpack;
            if (lootPack == null)
            {
                lootPack = world.CreateItem();
                lootPack.BaseId = 0x0E75;
                Equip(lootPack, Core.Enums.Layer.Pack);
            }
            SpawnLootTemplateInto(world, lootPack, picked, depth: 0);
            return;
        }
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
        Definitions.ItemDefHelper.ApplyInstanceMetadata(item, rid.Index,
            setDisplayId: false, setName: false);
        // Store raw NAME= template; Item.GetName() resolves
        // %plural/singular% markers per current Amount on every read.
        if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
            item.Name = idef.Name;

        // ITEMNEWBIE (Source-X ATTR_NEWBIE): the item stays with its owner on
        // death instead of dropping to the corpse — worn gear from @NPCRestock
        // must not become loot while the plain ITEM= backpack loot drops.
        if (newbie)
            item.SetAttr(Core.Enums.ObjAttributes.Newbie);

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
            if (!pack.TryAddItem(item))
            {
                world.RemoveItem(item);
                return;
            }
        }
        else
        {
            Equip(item, layer);
        }

        _lastCreatedItem = item;
    }

    /// <summary>
    /// Expand a sequential loot template into <paramref name="dest"/> —
    /// Source-X CItem::ReadTemplate (CItem.cpp:586): ITEM rows spawn in order
    /// (each row rolls its own R-chance and dice amount, CreateHeader),
    /// CONTAINER rows redirect the following ITEM rows into a sub-container,
    /// and a nested template pours into the current container.
    /// </summary>
    private void SpawnLootTemplateInto(World.GameWorld world, Item dest, string templateName, int depth)
    {
        if (depth > 4) return; // cycle guard, mirrors TemplateEngine.MaxNestedResolves intent
        var tpl = Definitions.DefinitionLoader.GetTemplateDef(templateName);
        if (tpl == null) return;

        var current = dest;
        foreach (var entry in tpl.ItemEntries)
        {
            if (entry.IsContainer)
            {
                var bag = CreateLootTemplateItem(world, entry.DefName, 1);
                if (bag != null && dest.TryAddItem(bag))
                    current = bag;
                continue; // bag failed → keep pouring into the previous container
            }

            if (!Definitions.TemplateEngine.TryRollTemplateRow(entry, out int amount))
                continue;

            string picked = Definitions.TemplateEngine.PickRandomItemDefName(entry.DefName);
            if (string.IsNullOrWhiteSpace(picked)) continue;

            var resources = Definitions.DefinitionLoader.StaticResources;
            var rid = resources?.ResolveDefName(picked) ?? default;
            if (rid.IsValid && rid.Type == Core.Enums.ResType.Template)
            {
                SpawnLootTemplateInto(world, current, picked, depth + 1);
                continue;
            }

            var item = CreateLootTemplateItem(world, picked, amount);
            if (item == null) continue;
            if (!current.TryAddItem(item))
            {
                world.RemoveItem(item);
                // The container filled up — Source-X keeps reading, later rows
                // may be smaller/stack; do the same instead of aborting.
            }
        }
    }

    /// <summary>Create one loot item from a resolved itemdef defname —
    /// graphic/metadata resolution identical to SpawnAndEquipItem's single-item
    /// path, but never equips (template loot always lands in a container).</summary>
    private Item? CreateLootTemplateItem(World.GameWorld world, string defname, int amount)
    {
        string picked = Definitions.TemplateEngine.PickRandomItemDefName(defname);
        if (string.IsNullOrWhiteSpace(picked)) return null;
        var resources = Definitions.DefinitionLoader.StaticResources;
        if (resources == null) return null;
        var rid = resources.ResolveDefName(picked);
        int defIndex = rid.IsValid && rid.Type == Core.Enums.ResType.ItemDef ? rid.Index : 0;
        if (defIndex == 0)
        {
            // Numeric row (ITEM=0EED,...): Sphere's leading-zero-hex / 0x
            // convention — the graphic id IS the itemdef index.
            if (!SphereNet.Scripting.Parsing.ScriptKey.TryParseNumber(picked.AsSpan(), out long numeric) ||
                numeric <= 0 || numeric > ushort.MaxValue)
                return null;
            defIndex = (int)numeric;
        }

        var idef = Definitions.DefinitionLoader.GetItemDef(defIndex);
        ushort dispId = 0;
        if (idef != null)
        {
            if (idef.DispIndex != 0) dispId = idef.DispIndex;
            else if (idef.DupItemId != 0) dispId = idef.DupItemId;
        }
        if (dispId == 0 && defIndex <= 0xFFFF) dispId = (ushort)defIndex;
        if (dispId == 0) return null;

        var item = world.CreateItem();
        item.BaseId = dispId;
        Definitions.ItemDefHelper.ApplyInstanceMetadata(item, defIndex,
            setDisplayId: false, setName: false);
        if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
            item.Name = idef.Name;
        if (amount > 1)
            item.Amount = (ushort)Math.Min(amount, ushort.MaxValue);
        return item;
    }

    /// <summary>Broadcast an overhead spoken line from this character to nearby
    /// clients. Parses an optional Sphere <c>@color,font,mode</c> prefix for the
    /// hue. SAY uses ASCII (0x1C); SAYU/SAYUC use Unicode (0xAE); EMOTE passes a
    /// non-zero message type.</summary>
    /// <summary>PROPLIST diagnostic surface (Source-X OV_PROPLIST).</summary>
    protected override IEnumerable<string> EnumeratePropListKeys() =>
        ["NAME", "COLOR", "P", "TIMER", "BODY", "STR", "DEX", "INT",
         "HITS", "MAXHITS", "MANA", "MAXMANA", "STAM", "MAXSTAM",
         "FAME", "KARMA", "TITLE", "FLAGS", "NPC"];

    protected override void DumpBaseProperties(Action<string> sink)
    {
        var def = Definitions.DefinitionLoader.GetCharDef(
            _charDefIndex != 0 ? _charDefIndex : CharDefIndex);
        if (def == null) return;
        if (!string.IsNullOrEmpty(def.DefName)) sink($"DEFNAME={def.DefName}");
        if (!string.IsNullOrEmpty(def.Name)) sink($"NAME={def.Name}");
        sink($"ID=0{def.Id.Index:X}");
        if (def.DispIndex != 0) sink($"DISPID=0{def.DispIndex:X}");
    }

    protected override void DumpBaseTags(Action<string> sink)
    {
        var def = Definitions.DefinitionLoader.GetCharDef(
            _charDefIndex != 0 ? _charDefIndex : CharDefIndex);
        if (def == null) return;
        foreach (var (k, v) in def.TagDefs.GetAll())
            sink($"{k}={v}");
    }

    private void BroadcastSpeech(byte msgType, bool unicode, string rawArgs)
    {
        ushort hue = 0x03B2;
        string text = rawArgs?.Trim() ?? "";
        if (text.StartsWith('@'))
        {
            int sp = text.IndexOfAny(new[] { ' ', '\t' });
            string spec = sp >= 0 ? text[1..sp] : text[1..];
            text = sp >= 0 ? text[(sp + 1)..].Trim() : "";
            var f = spec.Split(',');
            if (f.Length > 0 && f[0].Length > 0 &&
                ushort.TryParse(f[0], System.Globalization.NumberStyles.HexNumber, null, out ushort c))
                hue = c;
        }
        if (string.IsNullOrEmpty(text)) return;

        SphereNet.Network.Packets.PacketWriter pkt = unicode
            ? new SphereNet.Network.Packets.Outgoing.PacketSpeechUnicodeOut(
                Uid.Value, BodyId, msgType, hue, 3, "ENU", GetName(), text)
            : new SphereNet.Network.Packets.Outgoing.PacketSpeechOut(
                Uid.Value, BodyId, msgType, hue, 3, GetName(), text);
        BroadcastNearby?.Invoke(Position, 18, pkt, 0);
    }

    /// <summary>
    /// Materialise this NPC's deferred loot into a corpse at death.
    /// Plain (non-wearable) chardef <c>ITEM=</c> entries that are not
    /// flagged <c>ITEMNEWBIE</c> are NOT spawned onto the living NPC —
    /// they are rolled here when the corpse is created. This mirrors
    /// Source-X (CTRIG_CreateLoot fires just before MakeCorpse) and keeps
    /// living NPCs from carrying transient loot items that would otherwise
    /// be written into every world save. Wearable gear (weapons/armour) is
    /// still equipped at spawn and dropped by the normal unequip path.
    /// </summary>
    public void MaterializeDeathLoot(Item corpse)
    {
        if (corpse == null) return;
        var world = ResolveWorld?.Invoke();
        if (world == null) return;
        var charDef = Definitions.DefinitionLoader.GetCharDef(
            _charDefIndex != 0 ? _charDefIndex : CharDefIndex);
        if (charDef == null) return;
        var resources = Definitions.DefinitionLoader.StaticResources;
        if (resources == null) return;

        ushort lastHue = 0;
        foreach (var entry in charDef.NewbieItems)
        {
            if (entry.Newbie) continue;              // ITEMNEWBIE = NPC's own gear, never loot
            if (string.IsNullOrWhiteSpace(entry.DefName)) continue;

            string picked = Definitions.TemplateEngine.PickRandomItemDefName(entry.DefName);
            if (string.IsNullOrWhiteSpace(picked)) continue;
            var rid = resources.ResolveDefName(picked);
            if (!rid.IsValid || rid.Type != Core.Enums.ResType.ItemDef) continue;

            var idef = Definitions.DefinitionLoader.GetItemDef(rid.Index);
            ushort dispId = 0;
            if (idef != null)
            {
                if (idef.DispIndex != 0) dispId = idef.DispIndex;
                else if (idef.DupItemId != 0) dispId = idef.DupItemId;
            }
            if (dispId == 0 && rid.Index <= 0xFFFF) dispId = (ushort)rid.Index;
            if (dispId == 0) continue;

            // Wearable gear is equipped at spawn (combat / appearance) and
            // reaches the corpse via the unequip path — only non-wearable
            // loot is deferred to here.
            var layer = idef?.Layer ?? Core.Enums.Layer.None;
            if (layer == Core.Enums.Layer.None)
                layer = ResolveTileDataLayer(world, dispId);
            if (layer != Core.Enums.Layer.None) continue;

            var item = world.CreateItem();
            item.BaseId = dispId;
            Definitions.ItemDefHelper.ApplyInstanceMetadata(item, rid.Index,
                setDisplayId: false, setName: false);
            if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
                item.Name = idef.Name;

            int amount = entry.Amount;
            if (amount <= 0 && !string.IsNullOrWhiteSpace(entry.Dice))
                amount = RollDice(entry.Dice!);
            if (amount > 1)
                item.Amount = (ushort)Math.Min(amount, ushort.MaxValue);

            if (!string.IsNullOrWhiteSpace(entry.Color))
            {
                ushort hue = ResolveColorArg(entry.Color!, lastHue);
                if (hue != 0) { item.Hue = new Core.Types.Color(hue); lastHue = hue; }
            }

            if (!corpse.TryAddItem(item))
                world.PlaceItemWithDecay(item, corpse.Position);
        }
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
    /// <summary>
    /// Rebuild the virtual vendor stock from the persisted SELL template
    /// tag. The stock container and its items are intentionally excluded
    /// from the world save (rebuilt on demand), so a vendor loaded from a
    /// prior save has no stock until this runs on first open.
    /// </summary>
    public void RebuildVendorStock()
    {
        if (TryGetTag("VENDOR_SELL_LIST", out string? defName) &&
            !string.IsNullOrWhiteSpace(defName))
            PopulateVendorStock(defName!, buySide: false);
    }

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

        // Persist the SELL template defname (not the items). The stock
        // container and its items are VIRTUAL: never written to the world
        // save, rebuilt on demand from this tag when a player opens the
        // vendor after a load / restart. This is what keeps a populated
        // vendor from leaving ~20 transient stock items per NPC in every
        // save file.
        SetTag("VENDOR_SELL_LIST", templateDefName);

        var pack = GetEquippedItem(Layer.VendorStock);
        if (pack == null)
        {
            pack = world.CreateItem();
            pack.BaseId = 0x408D; // i_vendor_box (Source-X stock graphic)
            Equip(pack, Layer.VendorStock);
        }
        else
        {
            // A restock refills to the template levels, it does not stack a second
            // copy on top. Clear the existing (virtual) stock first so a periodic
            // @NPCRestock — which re-runs SELL= over an already-stocked pack —
            // doesn't append a duplicate of every row (the reported "same item
            // shows up twice" doubling that compounded on each 10-minute restock).
            foreach (var old in world.GetContainerContents(pack.Uid).ToList())
                world.RemoveItem(old);
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
            // Source-X NPC_Vendor_Restock materialises stock through
            // CreateTemplate → GenerateScript, so @Create runs on every stock
            // item. This path built bare items (BaseId+name+price only): no
            // ITEMDEF routing tags, no type stamp, no @Create — a bought ship
            // deed carried no MORE reference ("that deed is blank"), and any
            // @Create-driven item (magic gear, scripted deeds) sold by a
            // vendor lost its scripted creation entirely. Display id and name
            // stay under this method's control.
            Definitions.ItemDefHelper.ApplyInstanceMetadata(item, defIndex,
                setDisplayId: false, setName: false);
            // NAME= templates carry %plural/singular% markers — store
            // the RAW template; Item.GetName() pluralizes per Amount on
            // every read (CItem::GetName parity, CItem.cpp:1769).
            if (idef != null && !string.IsNullOrWhiteSpace(idef.Name))
                item.Name = idef.Name;
            // Stamp the buy price from the itemdef VALUE (Source-X uses
            // VALUE for vendor pricing). Without this the server-side
            // price check fell back to 0 and rejected the purchase even
            // though the client displayed a fallback price.
            if (idef != null)
            {
                int value = idef.ValueMin > 0 && idef.ValueMax > 0
                    ? (idef.ValueMin + idef.ValueMax) / 2
                    : Math.Max(idef.ValueMin, idef.ValueMax);
                if (value > 0)
                {
                    item.Price = value;
                    item.SetTag("PRICE", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            if (entryAmount > 1)
                item.Amount = (ushort)Math.Min(entryAmount, ushort.MaxValue);
            if (!pack.TryAddItem(item))
            {
                world.RemoveItem(item);
                break;
            }
            spawned++;
        }

        SetTag("VENDOR_LAST_RESTOCK", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
    }

    // Script number parser: accepts 0xNN hex, decimal. Unlike
    // ScriptKey.TryParseNumber we don't apply the Source-X "leading
    // zero = hex" convention here because icon IDs are routinely
    // written with a zero pad (e.g. "0007") but meant as decimal.
    /// <summary>Resolve a POLY argument to a body id: a numeric body, a
    /// chardef defname, or a weighted "{ c_a 1 c_b 1 }" list.</summary>
    internal static ushort ResolvePolyBody(string args)
    {
        string arg = args.Trim();
        if (arg.Length == 0)
            return 0;

        if (arg.StartsWith('{') && arg.EndsWith('}'))
        {
            var tokens = arg[1..^1].Split([' ', '\t'],
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var picks = new List<(string Name, int Weight)>();
            for (int i = 0; i + 1 < tokens.Length; i += 2)
            {
                int w = SphereNet.Scripting.Definitions.ValueCurve.ParseSphereNumber(tokens[i + 1]);
                picks.Add((tokens[i], Math.Max(1, w)));
            }
            if (picks.Count == 0)
                return 0;
            int total = 0;
            foreach (var p in picks) total += p.Weight;
            int roll = Random.Shared.Next(total);
            foreach (var p in picks)
            {
                roll -= p.Weight;
                if (roll < 0) { arg = p.Name; break; }
            }
        }

        if (ushort.TryParse(arg, out ushort numeric))
            return numeric;

        int defIdx = Definitions.CharDefHelper.ResolveDefIndex(arg, Definitions.DefinitionLoader.StaticResources);
        if (defIdx <= 0)
            return 0;
        return Definitions.CharDefHelper.ResolveBodyId(defIdx, Definitions.DefinitionLoader.StaticResources);
    }

    private static ushort MapAnimToMounted(ushort action)
    {
        return action switch
        {
            (ushort)AnimationType.CastDirected or
            (ushort)AnimationType.CastArea => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.AttackWeapon or
            (ushort)AnimationType.Attack1HPierce or
            (ushort)AnimationType.Attack1HBash or
            (ushort)AnimationType.Attack2HBash or
            (ushort)AnimationType.Attack2HSlash or
            (ushort)AnimationType.Attack2HPierce or
            (ushort)AnimationType.AttackWrestle => (ushort)AnimationType.HorseAttack,
            (ushort)AnimationType.AttackBow => (ushort)AnimationType.HorseAttackBow,
            (ushort)AnimationType.AttackXBow => (ushort)AnimationType.HorseAttackXBow,
            (ushort)AnimationType.GetHit => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.Block => (ushort)AnimationType.HorseSlap,
            (ushort)AnimationType.Bow or
            (ushort)AnimationType.Salute or
            (ushort)AnimationType.Eat => (ushort)AnimationType.HorseSlap,
            _ => action
        };
    }

    private string FormatBodyProperty()
    {
        string? defname = CharDefHelper.ResolveDefName(_charDefIndex);
        if (string.IsNullOrEmpty(defname) && TryGetTag("CHARDEF", out string? tag) && !string.IsNullOrEmpty(tag))
            defname = tag;
        return !string.IsNullOrEmpty(defname) ? defname : $"0{_bodyId:X}";
    }

    private void NotifyAppearanceChanged() => OnAppearanceChanged?.Invoke(this);

    /// <summary>Broadcast appearance/body updates to clients (including self).</summary>
    public void RefreshAppearance() => OnAppearanceChanged?.Invoke(this);
}
