using System.Globalization;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Objects.Items;

/// <summary>
/// World item instance. Maps to CItem in Source-X.
/// Represents a single item in the game world with type, amount, containment.
/// </summary>
public class Item : ObjBase
{
    // Static delegates set by Program.cs for cross-module resolution
    public static Func<Serial, Ships.Ship?>? ResolveShip;
    public static Func<Serial, House?>? ResolveHouse;

    /// <summary>Engine-routed REDEED (HousingEngine full teardown): removes
    /// the registry entry and the dynamic house region too. A direct
    /// house.Redeed left a ghost region and a stale house-count slot.</summary>
    public static Func<Serial, Item?>? RedeedHouse;

    /// <summary>Ship-engine REDEED (dry-dock: cargo crate + full teardown,
    /// deed delivered to the owner). Ships had no in-game decommission path.</summary>
    public static Func<Serial, Item?>? RedeedShip;
    public static Func<Ships.ShipEngine?>? ResolveShipEngine;
    public new static Func<World.GameWorld>? ResolveWorld;
    public static Func<Serial, Guild.GuildDef?>? ResolveGuild;
    public static Func<Guild.GuildManager?>? ResolveGuildManager;
    public static Func<Serial, Character?>? ResolveGuildCharacter;
    public static Func<Item, Serial, string, bool>? ExecuteGuildMemberCommand;
    public static Func<Item, Serial, string, bool>? ExecuteGuildRelationCommand;
    public static Func<string, ushort>? ResolveDefName;

    /// <summary>Fires the itemdef <c>@Create</c> trigger once per newly
    /// materialised instance — Source-X <c>CItem::GenerateScript</c> →
    /// <c>OnTrigger(ITRIG_Create)</c>. This is the sole hook that turns a magic
    /// weapon's script body (<c>MOREY</c>/<c>ATTR</c>/<c>HITPOINTS</c>/<c>COLOR</c>,
    /// sub-item spawns) into an actual magic item, so loot/spawn/NEWITEM/vendor/
    /// carve/.add/craft all rely on it. Wired at startup; left null in unit tests
    /// that don't exercise triggers. The world load never routes through the
    /// creation helper, so reloaded items never re-fire @Create.</summary>
    public static Action<Item>? CreateTriggerHook;
    private bool _createTriggerFired;

    /// <summary>Run the itemdef <c>@Create</c> trigger exactly once for this
    /// instance. Guarded so re-stamping metadata never double-applies a magic
    /// @Create body (which would, e.g., stack the damage bonus every call).</summary>
    public void FireCreateTrigger()
    {
        if (_createTriggerFired) return;
        _createTriggerFired = true;
        CreateTriggerHook?.Invoke(this);
    }

    // Resolves a MULTIDEF defname (e.g. "m_small_ship_n") to its multi id.
    // Returns the multi id (which may legitimately be 0 — ship multis sit at the
    // start of multi.mul), or -1 when the name is not a multi definition.
    public static Func<string, int>? ResolveMultiDefId;
    public static Action<Item>? OnVisualUpdate;
    /// <summary>Invoked by MULTICREATE when a script registers a multi
    /// as a house at runtime. Program.cs wires this to
    /// HousingEngine.RegisterExistingMulti so the region tracker knows
    /// about the new house without waiting for the next save cycle.</summary>
    public static Action<Item>? OnHouseRegister;

    /// <summary>Invoked when a corpse's decay timer elapses. Program.cs wires this
    /// to a handler that scatters the corpse contents (or, for a player corpse,
    /// stages it to bones for a second decay window). Returns true when the corpse
    /// was consumed and should be deleted, false when it was staged (re-armed) and
    /// must be kept. Done via a callback so Item.cs stays free of world plumbing.</summary>
    public static Func<Item, bool>? OnCorpseDecay;

    /// <summary>Invoked when an item's TIMER expires. Program.cs wires this
    /// to fire the @Timer trigger via TriggerDispatcher. Returns the
    /// trigger result so OnTick can decide: True=keep, False=delete,
    /// Default=no handler (keep unless ATTR_DECAY).</summary>
    public static Func<Item, TriggerResult?>? OnTimerExpired;

    /// <summary>Invoked when TYPE is set to a spawn type at runtime.
    /// Program.cs wires this to InitializeSpawnComponent so script-driven
    /// spawner creation works without waiting for a world reload.</summary>
    public static Action<Item>? OnSpawnTypeChanged;

    /// <summary>Invoked by the script OPEN verb (Source-X CV_OPEN). The host
    /// opens this container on the acting console's client with script
    /// authority (no snoop/trap gate). Unset → verb stays an ack-only no-op
    /// so headless script runs don't fail.</summary>
    public static Action<Item, ITextConsole>? OnScriptOpen;

    /// <summary>Invoked by the script DCLICK/USE verbs (Source-X CV_DCLICK).
    /// The host routes it through the acting client's double-click path so
    /// doors, potions, containers etc. behave exactly like a real dclick.</summary>
    public static Action<Item, ITextConsole>? OnScriptDClick;

    /// <summary>Invoked by the spawner START/STOP verbs. Arg: true = started,
    /// false = stopped. The host wires it to the @Start/@Stop item triggers.</summary>
    public static Action<Item, bool>? OnSpawnStartStop;

    private ItemType _type;
    private ushort _amount = 1;
    private Serial _containedIn = Serial.Invalid;
    private byte _containerGridIndex;
    private readonly List<Item> _contents = [];
    private bool _isDeleted;

    // Faz 1: Core item fields
    private uint _more1;
    private uint _more2;
    private uint _moreB;
    private Point3D _moreP;
    private Serial _link = Serial.Invalid;
    private int _price;
    // Source-X CItemVendable: quality starts at 0 and is only set by crafting;
    // the old invented default of 50 inflated ranged-VALUE vendor prices and
    // stamped QUALITY=50 on every plain item.
    private ushort _quality;
    private int _hitsCur;
    private int _hitsMax;
    private Serial _crafter = Serial.Invalid;
    private ushort _usesRemaining;

    private ushort _dispId;

    // TDATA instance overrides (default 0)
    private uint _tdata1;
    private uint _tdata2;
    private uint _tdata3;
    private uint _tdata4;

    public uint TData1 { get => _tdata1; set => _tdata1 = value; }
    public uint TData2 { get => _tdata2; set => _tdata2 = value; }
    public uint TData3 { get => _tdata3; set => _tdata3 = value; }
    public uint TData4 { get => _tdata4; set => _tdata4 = value; }

    /// <summary>Resolve the definition that created this instance. Named
    /// ITEMDEFs keep their 32-bit identity in SCRIPTDEF while BaseId contains
    /// only the 16-bit client graphic, so BaseId alone is not sufficient.</summary>
    private SphereNet.Scripting.Definitions.ItemDef? ResolveDefinition() =>
        DefinitionLoader.GetItemDef(ItemDefHelper.ResolveInstanceDefIndex(this));

    /// <summary>PROPLIST diagnostic surface (Source-X OV_PROPLIST).</summary>
    protected override IEnumerable<string> EnumeratePropListKeys() =>
        ["NAME", "COLOR", "P", "TIMER", "LINK", "TYPE", "AMOUNT", "ATTR",
         "MORE1", "MORE2", "MOREP", "CONT", "DISPID", "WEIGHT"];

    protected override void DumpBaseProperties(Action<string> sink)
    {
        var def = ResolveDefinition();
        if (def == null) return;
        if (!string.IsNullOrEmpty(def.DefName)) sink($"DEFNAME={def.DefName}");
        if (!string.IsNullOrEmpty(def.Name)) sink($"NAME={def.Name}");
        sink($"ID=0{def.Id.Index:X}");
        if (def.DispIndex != 0) sink($"DISPID=0{def.DispIndex:X}");
        sink($"TYPE={def.Type}");
        if (def.Weight != 0) sink($"WEIGHT={def.Weight}");
    }

    protected override void DumpBaseTags(Action<string> sink)
    {
        var def = ResolveDefinition();
        if (def == null) return;
        foreach (var (k, v) in def.TagDefs.GetAll())
            sink($"{k}={v}");
    }

    // Runtime EVENTS list (from ITEMDEF + dynamically added)
    private readonly List<ResourceId> _events = [];

    /// <summary>Runtime EVENTS list. Populated from ITEMDEF Events + dynamically added at runtime.</summary>
    public List<ResourceId> Events => _events;

    /// <summary>Attached spawn component (for IT_SPAWN_CHAR / IT_SPAWN_ITEM).</summary>
    public SpawnComponent? SpawnChar { get; set; }
    public ItemSpawnComponent? SpawnItem { get; set; }
    /// <summary>Champion spawn state machine (Source-X CCChampion) — attached
    /// alongside SpawnChar when the item type is t_spawn_champion.</summary>
    public ChampionComponent? Champion { get; set; }

    public ItemType ItemType
    {
        get
        {
            if (_type != ItemType.Normal)
                return _type;
            var def = ResolveDefinition();
            if (def?.Type is ItemType defType && defType != ItemType.Normal)
                return defType;
            var world = ResolveWorld?.Invoke();
            if (DoorHelper.IsDoorGraphic(world?.MapData, BaseId) ||
                DoorHelper.IsDoorGraphic(world?.MapData, DispIdFull))
                return ItemType.Door;
            return ItemType.Normal;
        }
        set
        {
            // Keep the owning sector's listen-item count (speech gate) balanced
            // when a script retypes an item already on the ground. This setter
            // runs in every item construction, so stay allocation/lookup-free
            // unless the LISTEN classification could actually flip: the
            // def-derived type is identical on both sides of the assignment, so
            // it only needs resolving when a side is raw-Normal (masked by def).
            var oldRaw = _type;
            _type = value;
            if (oldRaw == value || !IsOnGround)
                return;

            var defType = ItemType.Normal;
            if (oldRaw == ItemType.Normal || value == ItemType.Normal)
                defType = ResolveDefinition()?.Type ?? ItemType.Normal;
            var oldEffective = oldRaw == ItemType.Normal ? defType : oldRaw;
            var newEffective = value == ItemType.Normal ? defType : value;

            // A multi-classification flip keeps the world's multi index
            // balanced — walk geometry iterates that index instead of a
            // 32-tile spatial query per step.
            bool wasMulti = IsMultiItemType(oldEffective);
            bool isMulti = IsMultiItemType(newEffective);
            if (wasMulti != isMulti)
                ResolveWorld?.Invoke()?.NotifyGroundItemMultiChanged(this, isMulti);

            if (World.Sectors.Sector.IsListenItemType(oldEffective) ==
                World.Sectors.Sector.IsListenItemType(newEffective))
                return;

            ResolveWorld?.Invoke()?.NotifyGroundItemTypeChanged(this, oldEffective, newEffective);
        }
    }

    /// <summary>Multi-structure classification for the world's multi index
    /// (houses, ships, addons, custom foundations).</summary>
    internal static bool IsMultiItemType(ItemType t) =>
        t is ItemType.Multi or ItemType.MultiCustom or ItemType.MultiAddon or ItemType.Ship;

    /// <summary>Top of the container chain — the wearing character or the
    /// outermost item (Source-X GetTopLevelObj). Used by the chained
    /// TOPOBJ.&lt;verb&gt; dispatch.</summary>
    internal ObjBase? ResolveTopObject()
    {
        var world = ResolveWorld?.Invoke();
        if (world == null) return this;
        ObjBase cur = this;
        for (int depth = 0; depth < 64; depth++)
        {
            if (cur is not Item ci || !ci.ContainedIn.IsValid) break;
            var parent = world.FindObject(ci.ContainedIn);
            if (parent == null) break;
            cur = parent;
        }
        return cur;
    }

    /// <summary>The raw instance type field, before the lazy def-resolution the
    /// <see cref="ItemType"/> getter applies. Diagnostics only: raw-<c>_type</c>
    /// readers (IsStaticBlock, spellbook/book/map/ship/multi/container checks) see
    /// THIS, so a value of <see cref="ItemType.Normal"/> on an item whose def has a
    /// real type means the instance was never materialised and those readers
    /// misbehave. <see cref="Diagnostics.WorldInvariantAuditor"/> checks it.</summary>
    internal ItemType RawType => _type;

    /// <summary>Copy the resolved ITEMDEF's TYPE and TDATA onto the instance when
    /// they are still at their defaults. Script-created items get these via
    /// ItemDefHelper.ApplyInstanceMetadata, but a legacy save stores neither TYPE
    /// nor TDATA (WorldSaver never writes them — the type is defined by the
    /// itemdef), so a loaded item's raw _type stayed Normal. The lazy ItemType
    /// getter hid that for callers that use the property, but the ~20 code paths
    /// that read the raw _type field (spellbook/book/map/ship/multi/container
    /// checks, IsStaticBlock, FindContentByType) and the &lt;TYPE&gt; script read
    /// saw Normal. Materializing once after load makes a legacy instance behave
    /// exactly like a script-created one. Set via the field (not the property), so
    /// a spawn type does not fire OnSpawnTypeChanged here — InitializeSpawnItems
    /// still builds the component from the resolved type afterwards. The save
    /// format is unchanged because neither TYPE nor TDATA is persisted.</summary>
    public void MaterializeDefinitionType()
    {
        if (_type != ItemType.Normal &&
            _tdata1 != 0 && _tdata2 != 0 && _tdata3 != 0 && _tdata4 != 0)
            return;
        var def = ResolveDefinition();
        if (def == null) return;
        if (_type == ItemType.Normal && def.Type != ItemType.Normal)
            _type = def.Type;
        if (_tdata1 == 0) _tdata1 = def.TData1;
        if (_tdata2 == 0) _tdata2 = def.TData2;
        if (_tdata3 == 0) _tdata3 = def.TData3;
        if (_tdata4 == 0) _tdata4 = def.TData4;
    }

    public ushort Amount
    {
        get => _amount;
        set { _amount = Math.Max((ushort)1, value); MarkDirty(DirtyFlag.Amount); }
    }

    /// <summary>UID of the parent container or character (CONT in sphere scripts).</summary>
    public Serial ContainedIn
    {
        get => _containedIn;
        set
        {
            var oldVal = _containedIn;
            _containedIn = value;
            MarkDirty(DirtyFlag.Container);

            // Update container reverse index
            var world = ResolveWorld?.Invoke();
            if (world != null)
            {
                if (oldVal.IsValid)
                    world.ContainerIndexRemove(oldVal.Value, this);
                if (value.IsValid)
                    world.ContainerIndexAdd(value.Value, this);
            }
        }
    }

    public byte ContainerGridIndex
    {
        get => _containerGridIndex;
        set => _containerGridIndex = value;
    }

    public bool IsOnGround => !_containedIn.IsValid;
    public bool IsEquipped { get; set; }
    public Layer EquipLayer { get; set; }
    public byte Direction { get; set; }

    /// <summary>The wearable layer this item equips on: its explicit
    /// <see cref="EquipLayer"/> when set, otherwise the itemdef's default layer.
    /// Classic Sphere saves store an equipped item under <c>CONT=&lt;char&gt;</c>
    /// with no <c>LAYER=</c>; the layer comes from the itemdef (a shirt → torso,
    /// a backpack → pack). <see cref="Layer.None"/> when the item is not wearable.</summary>
    public Layer ResolveEquipLayer() =>
        EquipLayer != Layer.None ? EquipLayer : (ResolveDefinition()?.Layer ?? Layer.None);

    /// <summary>The itemdef's HEIGHT, as the location-touch window reads it.</summary>
    public byte DefHeight => ResolveDefinition()?.Height ?? 0;

    /// <summary>Whether this item is close enough in Z to touch a character standing
    /// at <paramref name="charZ"/> — that is, whether stepping onto or standing on the
    /// tile should reach the item at all.
    ///
    /// Source-X CheckLocation (CCharAct.cpp:4934) computes <c>zdiff = itemZ - charZ</c>,
    /// counts the item's height as at least 3, and skips the item when
    /// <c>zdiff &gt; height</c> or <c>zdiff &lt; -3</c>. Without it a field, trap or
    /// moongate lying on the floor below reaches whoever stands on the same X/Y one
    /// storey up. The reference makes this test BEFORE @STEP, so a skipped item fires
    /// no trigger either.</summary>
    public bool IsWithinStepHeight(sbyte charZ)
    {
        int zdiff = Z - charZ;
        return zdiff <= Math.Max((byte)3, DefHeight) && zdiff >= -3;
    }

    /// <summary>Last-resort equip layer derived from the itemdef TYPE, for wearables
    /// whose fixed slot the client stores in tiledata but some tiledata builds leave
    /// as 0 (hair/beard) — and for the lone unlabelled container a char carries, its
    /// backpack. Only the deterministic-slot types map; <see cref="Layer.None"/>
    /// otherwise (a t_clothing shirt vs robe can't be told apart by type alone).</summary>
    public Layer ResolveEquipLayerByType() =>
        (ResolveDefinition()?.Type ?? ItemType.Normal) switch
        {
            ItemType.Hair => Layer.Hair,
            ItemType.Beard => Layer.FacialHair,
            ItemType.Container => Layer.Pack,
            _ => Layer.None
        };

    /// <summary>Full display id. Returns DISPID override when set, otherwise BaseId.</summary>
    public ushort DispIdFull
    {
        get
        {
            if (_dispId != 0) return _dispId;
            // Source-X CItem::SetAmount remaps ONLY ore by stack size; every other
            // pile (arrows, bandages, ingots, gold, …) keeps its base graphic and
            // the client renders the stack count. Wave 138 used DUPELIST as a
            // generic amount ramp, but DUPELIST is a flip/alias list in Sphere
            // (e.g. i_arrow → 0f3e), not a size ramp, so it wrongly reskinned
            // stacked arrows/bandages. The ore ramp uses the hardcoded ITEMID_ORE
            // graphics: 1=ORE_1, 2=ORE_2, 3=ORE_3, 4+=ORE_4 (CItem.cpp sm_Item_Ore).
            // Resolve the type via the instance override / itemdef directly — the
            // ItemType getter probes DispIdFull (door check) and would recurse.
            ItemType resolvedType = _type != ItemType.Normal
                ? _type
                : ResolveDefinition()?.Type ?? ItemType.Normal;
            if (resolvedType == ItemType.Ore)
            {
                return _amount switch
                {
                    <= 1 => (ushort)0x19B7, // ITEMID_ORE_1
                    2 => (ushort)0x19BA,    // ITEMID_ORE_2
                    3 => (ushort)0x19B8,    // ITEMID_ORE_3
                    _ => (ushort)0x19B9,    // ITEMID_ORE_4
                };
            }
            return BaseId;
        }
    }

    public ushort DispIdOverride => _dispId;

    /// <summary>Whether this item is a stackable "pile" (CAN_I_PILE, or the
    /// tiledata Generic flag as the reference seeds it). Only pile items use
    /// DUPELIST as an amount→graphic ramp. Uses BaseId for the tiledata lookup
    /// to avoid recursing through DispIdFull.</summary>
    private bool IsPileBase(SphereNet.Scripting.Definitions.ItemDef? def)
    {
        if (def != null && (def.Can & CanFlags.I_Pile) != 0) return true;
        var mapData = ResolveWorld?.Invoke()?.MapData;
        return mapData != null &&
            (mapData.GetItemTileData(BaseId).Flags & SphereNet.MapData.Tiles.TileFlag.Generic) != 0;
    }

    /// <summary>True when this item is a stackable pile (CAN_I_PILE or tiledata
    /// Generic), so its <see cref="Amount"/> stacks and is shown on the label.</summary>
    public bool IsPile => IsPileBase(ResolveDefinition());

    /// <summary>Strength required to equip this item (ITEMDEF REQSTR / Source-X
    /// CItemBase::m_ttEquippable.m_StrReq). A per-instance OVERRIDE.REQSTR tag
    /// wins over the def (Source-X allows a per-item requirement); 0 = none.</summary>
    public int ReqStr
    {
        get
        {
            if (TryGetTag("OVERRIDE.REQSTR", out string? raw) && int.TryParse(raw, out int v))
                return v;
            return ResolveDefinition()?.ReqStr ?? 0;
        }
    }

    /// <summary>Name shown on single-click and the property tooltip. Source-X
    /// CItem::GetNameFull prefixes the amount for a stacked pile
    /// ("1234 gold coins"); a single item (or a non-pile) shows just its
    /// pluralized <see cref="GetName"/>.</summary>
    public string GetDisplayName()
    {
        string name = GetName();
        if (_amount > 1 && IsPile && !string.IsNullOrEmpty(name))
            return $"{_amount} {name}";
        return name;
    }

    /// <summary>
    /// Source-X-faithful display name. Mirrors <c>CItem::GetName()</c>
    /// in <c>CItem.cpp</c>: applies <c>%plural/singular%</c> NAME=
    /// template rules from <c>CItemBase::GetNamePluralize</c> using the
    /// current <see cref="Amount"/>. Without this override the client
    /// receives raw template text like "Black Pearl%s%" or
    /// "loa%ves/f%" in vendor lists, click-name responses, tooltips,
    /// and crafting menus. Corpse names skip pluralization to match
    /// Source-X (<c>!IsType(IT_CORPSE)</c> branch in CItem.cpp:1769).
    /// </summary>
    public override string GetName()
    {
        string raw = base.GetName();
        if (string.IsNullOrEmpty(raw))
        {
            // No per-instance name was stamped. Most items that aren't created
            // through the vendor/NEWITEM paths (loot drops, spawned ground
            // items, resources) keep _name="" — fall back to the itemdef NAME=
            // so single-click labels and tooltips still read the base name.
            // Source-X CItem::GetName resolves the base name from the type def
            // the same way. Without this the client showed a blank label on
            // single click because GetName() returned "".
            var def = ResolveDefinition();
            if (def != null && !string.IsNullOrWhiteSpace(def.Name))
                raw = DefinitionLoader.ResolveNames(def.Name);
        }
        if (string.IsNullOrEmpty(raw))
        {
            // Last resort: the tiledata tile name. UO tiledata stores names with
            // the %s% plural marker (resolved by Pluralize below). For an item
            // whose itemdef has no NAME= (e.g. i_bottle_empty), sending an empty
            // name let the client render its OWN raw tiledata string with the
            // marker intact — "empty bottle %s%". Resolving it server-side sends
            // a clean "empty bottle".
            var md = ResolveWorld?.Invoke()?.MapData;
            if (md != null)
                raw = md.GetItemTileData(BaseId).Name ?? "";
        }
        if (string.IsNullOrEmpty(raw))
            return raw;
        if (raw.IndexOf('%') < 0)
            return raw;
        bool plural = (_amount != 1) && _type != ItemType.Corpse;
        return SphereNet.Scripting.Definitions.ItemDef.Pluralize(raw, plural);
    }

    /// <summary>Decay time in milliseconds. 0 = no decay.</summary>
    public long DecayTime { get; set; }

    public override bool IsDeleted => _isDeleted;

    /// <summary>Whether this item blocks movement (static terrain obstacle).</summary>
    public bool IsStaticBlock => _type is ItemType.Wall or ItemType.Door or ItemType.DoorLocked;

    // Faz 1: Core field public accessors
    public uint More1 { get => _more1; set => _more1 = value; }
    public uint More2 { get => _more2; set => _more2 = value; }
    public uint MoreB { get => _moreB; set => _moreB = value; }
    public Point3D MoreP { get => _moreP; set => _moreP = value; }

    /// <summary>The graphic this door shows when it is in its OTHER state. Source-X
    /// keeps it as the DOOROPENID property and swaps the two on every use
    /// (Use_DoorNew, CItem.cpp:4649/4681); zero means the door has no custom art and
    /// falls back to the classic hinge table. Stored as a tag so it persists and a
    /// script can set it.</summary>
    public ushort DoorOpenId
    {
        get => TryGetTag("DOOROPENID", out string? raw) &&
            ushort.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out ushort id)
                ? id : (ushort)0;
        set
        {
            if (value == 0) RemoveTag("DOOROPENID");
            else SetTag("DOOROPENID", value.ToString("X"));
        }
    }
    public Serial Crafter { get => _crafter; set => _crafter = value; }
    public ushort UsesRemaining { get => _usesRemaining; set => _usesRemaining = value; }
    public Serial Link { get => _link; set => _link = value; }
    public int Price { get => _price; set => _price = value; }
    public ushort Quality { get => _quality; set => _quality = value; }

    /// <summary>Current durability (0 = unset). Source-X item hits field.</summary>
    public int HitsCur
    {
        get
        {
            MigrateHitsFromTags();
            return _hitsCur;
        }
        set => _hitsCur = Math.Max(0, value);
    }

    /// <summary>Maximum durability (0 = unset).</summary>
    public int HitsMax
    {
        get
        {
            MigrateHitsFromTags();
            return _hitsMax;
        }
        set => _hitsMax = Math.Max(0, value);
    }

    /// <summary>One-time import from legacy TAG.HITS / TAG.HITSMAX saves.</summary>
    public void MigrateHitsFromTags()
    {
        if (_hitsCur == 0 && TryGetTag("HITS", out string? hc) && int.TryParse(hc, out int c) && c > 0)
        {
            _hitsCur = c;
            RemoveTag("HITS");
        }
        if (_hitsMax == 0 && TryGetTag("HITSMAX", out string? hm) && int.TryParse(hm, out int m) && m > 0)
        {
            _hitsMax = m;
            RemoveTag("HITSMAX");
        }
        else if (_hitsMax == 0 && TryGetTag("MAXHITS", out string? mh) && int.TryParse(mh, out int mx) && mx > 0)
        {
            _hitsMax = mx;
            RemoveTag("MAXHITS");
        }
    }

    /// <summary>Mark location for recall runes (Source-X m_morep).</summary>
    public void SetRuneMark(Point3D mark)
    {
        _moreP = mark;
        ClearRuneTags();
    }

    /// <summary>One-time import from legacy TAG.RUNE_* saves.</summary>
    public void MigrateRuneFromTags()
    {
        if (!TryGetTag("RUNE_X", out string? rx) || !TryGetTag("RUNE_Y", out string? ry) ||
            !short.TryParse(rx, out short x) || !short.TryParse(ry, out short y))
            return;

        sbyte z = 0;
        byte map = 0;
        if (TryGetTag("RUNE_Z", out string? rz))
            sbyte.TryParse(rz, out z);
        if (TryGetTag("RUNE_MAP", out string? rm))
            byte.TryParse(rm, out map);

        _moreP = new Point3D(x, y, z, map);
        ClearRuneTags();
    }

    public bool TryGetRuneMark(out Point3D mark)
    {
        MigrateRuneFromTags();
        if (_moreP.X == 0 && _moreP.Y == 0)
        {
            mark = default;
            return false;
        }

        mark = _moreP;
        return true;
    }

    public bool HasRuneMark => TryGetRuneMark(out _);

    private void ClearRuneTags()
    {
        RemoveTag("RUNE_X");
        RemoveTag("RUNE_Y");
        RemoveTag("RUNE_Z");
        RemoveTag("RUNE_MAP");
    }

    public MemoryType GetMemoryTypes() => (MemoryType)Hue.Value;
    public void SetMemoryTypes(MemoryType flags) => Hue = new Core.Types.Color((ushort)flags);
    public bool IsMemoryTypes(MemoryType flags) =>
        _type == ItemType.EqMemoryObj && (GetMemoryTypes() & flags) != 0;

    public override void SetTag(string key, string value)
    {
        if (TrySetRuneTag(key, value))
            return;

        base.SetTag(key, value);
    }

    private bool TrySetRuneTag(string key, string value)
    {
        string runeKey = key;
        if (runeKey.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase) ||
            runeKey.StartsWith("TAG0.", StringComparison.OrdinalIgnoreCase) ||
            runeKey.StartsWith("DTAG.", StringComparison.OrdinalIgnoreCase) ||
            runeKey.StartsWith("DTAG0.", StringComparison.OrdinalIgnoreCase))
        {
            int dotIdx = runeKey.IndexOf('.');
            runeKey = dotIdx >= 0 ? runeKey[(dotIdx + 1)..] : "";
        }

        switch (runeKey.ToUpperInvariant())
        {
            case "RUNE_X":
                if (short.TryParse(value, out short x))
                    _moreP = new Point3D(x, _moreP.Y, _moreP.Z, _moreP.Map);
                return true;
            case "RUNE_Y":
                if (short.TryParse(value, out short y))
                    _moreP = new Point3D(_moreP.X, y, _moreP.Z, _moreP.Map);
                return true;
            case "RUNE_Z":
                if (sbyte.TryParse(value, out sbyte z))
                    _moreP = new Point3D(_moreP.X, _moreP.Y, z, _moreP.Map);
                return true;
            case "RUNE_MAP":
                if (byte.TryParse(value, out byte map))
                    _moreP = new Point3D(_moreP.X, _moreP.Y, _moreP.Z, map);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Weapon swing speed read from ITEMDEF.SPEED. 0 = unspecified
    /// (combat code falls back to a sensible default). Used by
    /// <c>GetSwingDelayMs</c> to compute swing recoil per Source-X
    /// <c>Calc_CombatAttackSpeed</c>.</summary>
    public int Speed
    {
        get
        {
            if (TryGetTag("OVERRIDE.SPEED", out string? instanceOverride) &&
                int.TryParse(instanceOverride, out int instanceSpeed))
                return Math.Clamp(instanceSpeed, 0, byte.MaxValue);
            var def = ResolveDefinition();
            if (def != null && int.TryParse(def.TagDefs.Get("OVERRIDE.SPEED"), out int defSpeed))
                return Math.Clamp(defSpeed, 0, byte.MaxValue);
            return def?.Speed ?? 0;
        }
    }

    public const int WeightUnits = 10;

    // Per-item weight override in tenths of a stone (Source-X CItem::m_weight,
    // settable via BASEWEIGHT). Null = use the ITEMDEF/tiledata weight. Persisted.
    private int? _weightOverride;
    /// <summary>Per-item base-weight override (tenths of a stone), or null for the def value.</summary>
    public int? WeightOverride => _weightOverride;

    /// <summary>Item weight in tenths of a stone. A per-item BASEWEIGHT override
    /// wins; otherwise reads from ITEMDEF, then tiledata.</summary>
    public int Weight
    {
        get
        {
            if (_weightOverride.HasValue)
                return _weightOverride.Value;

            var def = ResolveDefinition();
            if (def is { HasWeight: true })
                return def.Weight;

            var mapData = ResolveWorld?.Invoke()?.MapData;
            if (mapData != null)
            {
                int tileWeight = mapData.GetItemTileData(BaseId).Weight;
                if (tileWeight > 0 && tileWeight < 0xFF)
                    return tileWeight * WeightUnits;
            }

            return 1;
        }
    }

    /// <summary>True if this is a 2-handed weapon. Used for swing speed
    /// weight bonus and to block shield equip.</summary>
    public bool IsTwoHanded
    {
        get
        {
            var def = ResolveDefinition();
            if (def?.TwoHands == true) return true;

            // Sitting on the two-handed layer is NOT enough on its own: a shield
            // rides there too, and calling it two-handed made every one-handed
            // equip bounce the shield out of the wearer's other hand. Source-X
            // asks CCPropsItemWeapon::CanSubscribe - is this a weapon? - before
            // pairing the two hands (CanEquipLayer, CCharStatus.cpp:410). The
            // layer still counts for a real weapon whose def is missing, which is
            // what the check was there for.
            return EquipLayer == Layer.TwoHanded && IsWeaponType;
        }
    }

    /// <summary>True if this item is a wieldable weapon (excludes shields).
    /// Used by the EquipLastWeapon client macro to track the last held weapon.</summary>
    public bool IsWeaponType => ItemType is
        ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or ItemType.WeaponSword or
        ItemType.WeaponFence or ItemType.WeaponBow or ItemType.WeaponMaceStaff or
        ItemType.WeaponMaceCrook or ItemType.WeaponMacePick or ItemType.WeaponAxe or
        ItemType.WeaponXBow or ItemType.WeaponThrowing or ItemType.WeaponWhip;

    /// <summary>A figurine is being deleted, so the pet stored inside it can go with
    /// it (wired to PetFigurine). Source-X ends the stored creature's lifecycle in
    /// CItem::DeleteCleanup for IT_FIGURINE (CItem.cpp:209) - it is disconnected and
    /// unreachable, so leaving it behind only strands it in the world tables.</summary>
    public static Action<Item>? FigurineDeletedHook;

    public void Delete()
    {
        SpawnChar?.KillAll();
        FigurineDeletedHook?.Invoke(this);
        _isDeleted = true;
        _contents.Clear();
    }

    /// <summary>
    /// Fully unlink this item from the world — object table, parent container,
    /// equipment slot and sector — and then mark it deleted. This is the
    /// high-level counterpart to <see cref="Delete"/>, which only flags the item
    /// and clears its own contents (leaving the item registered in the world,
    /// so the slot/object table would retain a dead reference). Falls back to the
    /// low-level flag-set when no world is wired (e.g. unit tests).
    /// </summary>
    /// <summary>Fires @Unequip when a WORN item leaves the character through
    /// a script/engine path (Source-X ItemBounce / RemoveSelf run
    /// ITRIG_UnEquip). The client pickup path fires it itself, so this hook is
    /// invoked only from the engine-side unequip/remove paths — its cleanup
    /// block (e.g. clearing a combat-bonus freeze) would otherwise never run.</summary>
    public static Action<Item, Characters.Character>? OnItemUnequipped;

    /// <summary>How long a hive rests between refills, and how much it holds
    /// (Source-X 15 minutes, up to 5).</summary>
    public const long BeeHiveRefillMs = 15 * 60 * 1000;
    public const uint BeeHiveMaxHoney = 5;

    /// <summary>Source-X WOOLGROWTHTIME default: half an hour before a shorn sheep
    /// is worth shearing again (CServerConfig.cpp:229).</summary>
    public const long WoolGrowthMs = 30 * 60 * 1000;

    private const ushort SheepBody = 0x00CF;
    private const ushort ShornSheepBody = 0x00DF;

    /// <summary>Put this item into a timed animation state and remember what to come
    /// back to. Source-X SetAnim (CItem.cpp:4128) saves the graphic and the type in
    /// m_itAnim, switches the item to IT_ANIM_ACTIVE and arms the timer - which is
    /// what keeps a workstation busy: while it is IT_ANIM_ACTIVE it no longer answers
    /// as the station it was.</summary>
    public void SetAnim(ushort animId, long durationMs)
    {
        More1 = DispIdFull;
        More2 = (uint)_type;
        _dispId = animId;
        _type = ItemType.AnimActive;
        SetTimeout(Environment.TickCount64 + durationMs);
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
    }

    /// <summary>Come back out of the animation state SetAnim put us in.</summary>
    private void EndAnim()
    {
        if (More1 != 0) _dispId = (ushort)More1;
        _type = More2 != 0 ? (ItemType)More2 : ItemType.Normal;
        More1 = 0;
        More2 = 0;
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
    }

    /// <summary>A shorn sheep's fleece has grown back (Source-X OnTickEquip
    /// LAYER_FLAG_Wool, CCharAct.cpp:4067). The marker is spent either way.</summary>
    private void RegrowWool()
    {
        var world = ResolveWorld?.Invoke();
        if (world?.FindChar(ContainedIn) is { } sheep && sheep.BodyId == ShornSheepBody)
            sheep.BodyId = SheepBody;
        Delete();
    }

    public void RemoveFromWorld()
    {
        var world = ResolveWorld?.Invoke();
        // A worn item being removed (script .REMOVE, engine cleanup) must fire
        // @Unequip while it is still equipped, before it leaves the world.
        if (IsEquipped && _containedIn.IsValid &&
            world?.FindObject(_containedIn) is Characters.Character wornWearer &&
            wornWearer.GetEquippedItem(EquipLayer) == this)
        {
            OnItemUnequipped?.Invoke(this, wornWearer);
        }
        if (world != null)
            world.RemoveItem(this);
        else
            Delete();
    }

    // --- Container functionality ---

    public IReadOnlyList<Item> Contents => _contents;
    public int ContentCount => _contents.Count;

    // Source-X MAX_ITEMS_CONT (sphereproto.h): the classic client cannot
    // render more than 255 items in one container; the old 500 ceiling let
    // containers grow past what the protocol can show.
    public const int MaxContainerItems = 255;

    /// <summary>Try to add an item without silently losing the operation when the
    /// container is full. Returns true only when containment was established.</summary>
    public bool TryAddItem(Item item)
    {
        if (item == this || IsDeleted || item.IsDeleted) return false;
        if (_contents.Contains(item))
            return item.ContainedIn == Uid;
        if (_contents.Count >= MaxContainerItems)
            return false;
        if (item.ContainsInSubtree(this)) return false;

        // Keep the containment graph singular. A number of engine paths add an
        // item directly (trade/vendor/script delivery) rather than performing a
        // packet pickup first; leaving the old parent list intact makes the same
        // object appear in two containers and corrupts weight/save traversal.
        var world = ResolveWorld?.Invoke();
        if (item.ContainedIn.IsValid && item.ContainedIn != Uid && world != null)
        {
            var oldParent = world.FindObject(item.ContainedIn);
            if (oldParent is Item oldContainer)
                oldContainer.RemoveItem(item);
            else if (oldParent is Character oldWearer && item.IsEquipped &&
                     oldWearer.GetEquippedItem(item.EquipLayer) == item)
                oldWearer.Unequip(item.EquipLayer);
        }
        else if (!item.ContainedIn.IsValid && world != null)
        {
            world.HideFromSector(item);
        }
        item.IsEquipped = false;

        _contents.Add(item);
        item.ContainedIn = Uid;
        if (item.X == 0 && item.Y == 0)
            AssignRandomContainerPosition(this, item);
        NotifyTradeContentChanged();
        return true;
    }

    /// <summary>Add an item to this container. Returns false when the add would
    /// create a containment cycle (or the container is full) — callers loading
    /// from a corrupt save use this to quarantine the item instead of leaving a
    /// cyclic ContainedIn.</summary>
    public bool AddItem(Item item) => TryAddItem(item);

    private bool ContainsInSubtree(Item target)
    {
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Item>(_contents);
        while (pending.Count > 0)
        {
            var child = pending.Pop();
            if (!seen.Add(child))
                continue;
            if (ReferenceEquals(child, target)) return true;
            foreach (var nested in child._contents)
                pending.Push(nested);
        }
        return false;
    }

    /// <summary>Global default max stack size for stackable items
    /// (Source-X CServerConfig m_iItemsMaxAmount, ini ITEMSMAXAMOUNT, default 60000).</summary>
    public static int ItemsMaxAmount { get; set; } = 60000;

    // Per-item MAXAMOUNT override (Source-X CItem MaxAmount defnum). Null = global.
    private int? _maxAmountOverride;
    /// <summary>Per-item stack-size override, or null to use the global default.</summary>
    public int? MaxAmountOverride => _maxAmountOverride;

    /// <summary>True if this item type can form a stack (Source-X
    /// CItemBase::IsStackableType: CAN_I_PILE or the tiledata Generic flag). The
    /// itemdef flag is seeded from tiledata at load, but packs rarely script it,
    /// so the tiledata fallback keeps ore/ingot/log piles merging.</summary>
    /// <summary>
    /// Whether a resource search may descend into this container. Source-X
    /// CItemContainer::IsSearchable (CItemContainer.cpp:760) excludes the bank box,
    /// a vendor box, the trade window and a locked container - so reagents in your
    /// bank or in a trade you have open are not spent on a spell.
    /// </summary>
    public bool IsSearchableContainer =>
        _type is not (ItemType.ContainerLocked or ItemType.EqBankBox or
                      ItemType.EqVendorBox or ItemType.EqTradeWindow);

    public bool IsStackable
    {
        get
        {
            if (_type is ItemType.Container or ItemType.ContainerLocked or
                ItemType.Corpse or ItemType.EqMemoryObj or ItemType.SpawnChar or
                ItemType.SpawnItem or ItemType.Multi or ItemType.MultiCustom)
                return false;

            var def = ResolveDefinition();
            if (def != null && (def.Can & CanFlags.I_Pile) != 0)
                return true;

            var mapData = ResolveWorld?.Invoke()?.MapData;
            if (mapData != null)
            {
                var tile = mapData.GetItemTileData(DispIdFull);
                if ((tile.Flags & SphereNet.MapData.Tiles.TileFlag.Generic) != 0)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Effective max stack size (Source-X GetMaxAmount): a per-item
    /// override, else the global default, clamped to the ushort amount field.
    /// Non-stackable items return 0.</summary>
    public int MaxAmount => IsStackable ? Math.Min(_maxAmountOverride ?? ItemsMaxAmount, ushort.MaxValue) : 0;

    public bool CanStackWith(Item other)
    {
        if (other == this || other.IsDeleted) return false;
        if (BaseId != other.BaseId) return false;
        if (Hue != other.Hue) return false;

        if (!IsStackable)
            return false;

        if (_more1 != other._more1 || _more2 != other._more2) return false;
        if (_moreB != other._moreB || _moreP != other._moreP || _link != other._link ||
            _price != other._price || _quality != other._quality ||
            _hitsCur != other._hitsCur || _hitsMax != other._hitsMax ||
            _crafter != other._crafter || _usesRemaining != other._usesRemaining ||
            _dispId != other._dispId || _type != other._type ||
            _tdata1 != other._tdata1 || _tdata2 != other._tdata2 ||
            _tdata3 != other._tdata3 || _tdata4 != other._tdata4 ||
            !string.Equals(Name, other.Name, StringComparison.Ordinal))
            return false;

        // Two piles only merge when their tags match. Source-X CItem::Stack
        // compares m_TagDefs (and the ATTR_* flags, which SphereNet stores as
        // tags) before merging; merging stacks with differing tags would
        // silently drop one set. Cheap: the vast majority of piles carry no tags.
        if (!TagsEqual(Tags, other.Tags)) return false;
        return true;
    }

    /// <summary>Order-independent equality of two tag maps (used to gate stack
    /// merges). Returns true when both hold the same keys with the same values.</summary>
    private static bool TagsEqual(SphereNet.Scripting.Variables.VarMap a,
        SphereNet.Scripting.Variables.VarMap b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var kv in a.GetAll())
        {
            var bv = b.Get(kv.Key);
            if (bv == null || !string.Equals(bv, kv.Value, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>Copy all per-instance state from another item onto this one,
    /// EXCEPT amount and containment/position. Used when splitting a stack so the
    /// left-behind remainder is a full clone (Source-X CreateDupeItem), not just
    /// id/hue/more — otherwise custom tags, attributes, durability, price/link,
    /// timers and TDATA overrides on the original are silently dropped from the
    /// remainder. Amount, ContainedIn, grid index, position and equip state are
    /// deliberately left for the caller to set.</summary>
    public void CopyStackInstanceStateFrom(Item src)
    {
        BaseId = src.BaseId;
        Attributes = src.Attributes;
        Hue = src.Hue;
        Name = src.Name;
        _type = src._type; // direct field — avoid the ItemType setter's spawn/def side effects
        Direction = src.Direction;
        DecayTime = src.DecayTime;

        _more1 = src._more1;
        _more2 = src._more2;
        _moreB = src._moreB;
        _moreP = src._moreP;
        _link = src._link;
        _price = src._price;
        _quality = src._quality;
        _hitsCur = src._hitsCur;
        _hitsMax = src._hitsMax;
        _crafter = src._crafter;
        _usesRemaining = src._usesRemaining;
        _dispId = src._dispId;
        _tdata1 = src._tdata1;
        _tdata2 = src._tdata2;
        _tdata3 = src._tdata3;
        _tdata4 = src._tdata4;

        // Tags (custom + attribute flags stored as tags) and the runtime EVENTS
        // list must travel with the split so the remainder behaves identically.
        Tags.CopyFrom(src.Tags);
        _events.Clear();
        _events.AddRange(src._events);
    }

    public Item AddItemWithStack(Item item)
        => TryAddItemWithStack(item) ?? item;

    /// <summary>Add or merge an item and report a full-container failure with
    /// null. Callers that transfer ownership can then roll back or bounce the
    /// item instead of sending a packet for an item that was never contained.</summary>
    public Item? TryAddItemWithStack(Item item)
    {
        foreach (var existing in _contents)
        {
            if (existing.CanStackWith(item) && (existing.Amount + item.Amount) <= existing.MaxAmount)
            {
                existing.Amount += item.Amount;
                return existing;
            }
        }
        return TryAddItem(item) ? item : null;
    }

    public bool RemoveItem(Item item)
    {
        if (_contents.Remove(item))
        {
            item.ContainedIn = Serial.Invalid;
            NotifyTradeContentChanged();
            return true;
        }
        return false;
    }

    /// <summary>Wired by the host to clear both sides' accept flags when a secure
    /// trade window's contents change.</summary>
    public static Action<Item>? OnTradeWindowChanged;

    /// <summary>
    /// An offer that has been agreed to is only agreed to as it stood. Source-X makes
    /// this a container invariant rather than a handler courtesy: ContentAdd
    /// (CItemContainer.cpp:557) and OnRemoveObj (:798) both call Trade_Status(false)
    /// for IT_EQ_TRADE_WINDOW. Resetting only in the client drop handler left every
    /// other path - a script move, an engine deletion - able to change the goods out
    /// from under an acceptance that had already been given.
    /// </summary>
    private void NotifyTradeContentChanged()
    {
        if (_type == ItemType.EqTradeWindow)
            OnTradeWindowChanged?.Invoke(this);
    }

    /// <summary>Implements the <c>CONT=</c> script setter: move this item into
    /// the given container item, or into a character's backpack. Detaches it
    /// from its current container/ground first.</summary>
    private bool TryMoveToContainer(string value)
    {
        var world = ResolveWorld?.Invoke();
        if (world == null) return true;
        uint contUid = ParseHexOrDecUInt(value);
        if (contUid == 0) return true;

        var target = world.FindObject(new Core.Types.Serial(contUid));
        if (target == null) return true;

        if (target is Item contItem)
            return contItem.TryAddItem(this);
        else if (target is Character ch && ch.Backpack != null)
            return ch.Backpack.TryAddItem(this);
        return false;
    }

    public Item? FindContentItem(Serial uid, int maxDepth = 16)
    {
        if (maxDepth <= 0) return null;
        foreach (var item in _contents)
        {
            if (item.Uid == uid) return item;
            var found = item.FindContentItem(uid, maxDepth - 1);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Total item tree weight in whole stones for UI/status surfaces.</summary>
    public int TotalWeight => TotalWeightTenths / WeightUnits;

    /// <summary>Total item tree weight in tenths of a stone for capacity checks.</summary>
    public int TotalWeightTenths => CalcTotalWeightTenths();

    private int CalcTotalWeightTenths()
    {
        long weight = 0;
        var seen = new HashSet<Item>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Item>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
                continue;
            weight += (long)current.Weight * Math.Max(1, (int)current.Amount);
            if (weight >= int.MaxValue)
                return int.MaxValue;
            foreach (var child in current._contents)
                pending.Push(child);
        }
        return (int)weight;
    }

    // --- IScriptObj overrides ---

    public override bool TryGetProperty(string key, out string value)
    {
        // Champion altar read keys (Source-X ICHMPL_*).
        if (Champion != null && Champion.TryGetProperty(key, out value!))
            return true;

        value = "";
        var upper = key.ToUpperInvariant();

        // AOS on-hit combat properties (HITLEECHLIFE, HITFIREBALL, ...) are
        // tag-backed like the SLAYER pair; ITEMDEF-level values live in the
        // def-tags and are read by the combat engine's fallback.
        if (AosOnHitProperties.Contains(upper))
        {
            value = TryGetTag(upper, out var aosv) ? aosv ?? "0" : "0";
            return true;
        }
        if (SpellCastingProperties.Contains(upper))
        {
            value = TryGetTag(upper, out var castingValue) ? castingValue ?? "0" : "0";
            return true;
        }
        if (upper == CombatSpeedProperties.IncreaseSwingSpeed)
        {
            value = TryGetTag(upper, out var speedValue) ? speedValue ?? "0" : "0";
            return true;
        }

        switch (upper)
        {
            // Resolve the effective type, not the raw _type field: a legacy-loaded
            // item leaves _type at Normal and derives its type from the ITEMDEF
            // (which resolves even for synthetic-index defs). Reading the raw field
            // showed t_normal/0 for those in <TYPE> and the property editor while
            // MORE1 already resolved — the "typedef okunmuyor" report.
            case "TYPE":
                value = FormatItemType(_type != ItemType.Normal
                    ? _type
                    : ResolveDefinition()?.Type ?? ItemType.Normal);
                return true;
            case "AMOUNT": value = _amount.ToString(); return true;
            case "MAXAMOUNT": value = MaxAmount.ToString(); return true; // 0 for non-stackable
            case "BASEWEIGHT": value = Weight.ToString(); return true; // Source-X m_weight: per-unit tenths of a stone
            case "CONT": value = _containedIn.IsValid ? $"0{_containedIn.Value:X}" : ""; return true;
            case "HITS":
            case "HITPOINTS": value = HitsCur.ToString(); return true; // Source-X IC_HITPOINTS == IC_HITS
            case "MAXHITS":
            case "HITSMAX": value = HitsMax.ToString(); return true;
            case "LAYER": value = ((byte)EquipLayer).ToString(); return true;

            // Slayer-system item side (Source-X CCPropsItemEquippable) —
            // tag-backed; ITEMDEF-level values live in the def-tags and are
            // consulted by the combat engine's fallback.
            case "SLAYER_GROUP": value = TryGetTag("SLAYER_GROUP", out var slyG) ? slyG ?? "0" : "0"; return true;
            case "SLAYER_SPECIES": value = TryGetTag("SLAYER_SPECIES", out var slyS) ? slyS ?? "0" : "0"; return true;

            // Faz 1: Core fields
            case "MORE1": case "MORE": value = FormatMore1(); return true;
            case "MORE2": value = $"0{_more2:X}"; return true;
            case "MOREB": value = $"0{_moreB:X}"; return true;
            case "MORE1H": value = ((ushort)(_more1 >> 16)).ToString(); return true;
            case "MORE1L": value = ((ushort)(_more1 & 0xFFFF)).ToString(); return true;
            case "MORE2H": value = ((ushort)(_more2 >> 16)).ToString(); return true;
            case "MORE2L": value = ((ushort)(_more2 & 0xFFFF)).ToString(); return true;
            case "MOREP": value = _moreP.ToString(); return true;
            case "MOREX": value = _moreP.X.ToString(); return true;
            case "MOREY": value = _moreP.Y.ToString(); return true;
            case "MOREZ": value = _moreP.Z.ToString(); return true;
            case "RUNE_X": value = _moreP.X.ToString(); return true;
            case "RUNE_Y": value = _moreP.Y.ToString(); return true;
            case "RUNE_Z": value = _moreP.Z.ToString(); return true;
            case "RUNE_MAP": value = _moreP.Map.ToString(); return true;
            case "LINK": value = _link.IsValid ? $"0{_link.Value:X}" : ""; return true;
            case "MEMORYTYPES": value = ((ushort)GetMemoryTypes()).ToString(); return true;
            case "PRICE": value = _price.ToString(); return true;
            case "QUALITY": value = _quality.ToString(); return true;
            case "CRAFTER":
            case "CRAFTEDBY": // Source-X IC_CRAFTEDBY — the crafter's uid
                value = _crafter.IsValid ? $"0{_crafter.Value:X}" : ""; return true;
            case "USESREMAINING":
            case "USESCUR": value = _usesRemaining.ToString(); return true; // Source-X IC_USESCUR alias
            case "USESMAX": value = (TryGetTag("USESMAX", out string? um) ? um : "0") ?? "0"; return true;
            case "DECAY":
            {
                if (DecayTime <= 0) { value = "-1"; return true; }
                long remaining = (DecayTime - Environment.TickCount64) / 1000;
                value = remaining > 0 ? remaining.ToString() : "0";
                return true;
            }

            // TDATA instance
            case "TDATA1": value = _tdata1.ToString(); return true;
            case "TDATA2": value = _tdata2.ToString(); return true;
            case "TDATA3": value = _tdata3.ToString(); return true;
            case "TDATA4": value = _tdata4.ToString(); return true;

            // Identity
            case "ISITEM": value = "1"; return true;
            case "ISCHAR": value = "0"; return true;
            case "BASEID": value = FormatBaseId(); return true;
            // Display id read (Source-X <DISPID>/<ID>): mirrors BASEID's
            // defname-or-hex form but reflects the DISPID override when set,
            // so scripts like IF (<DISPID> == i_pie_safe) and weapon-type
            // dispatch on <dispid> work. Without this the value fell through
            // to the D-prefix decimal handler and resolved to 0.
            case "DISPID":
            case "ID": value = FormatDispId(); return true;
            case "DISPIDDEC": value = BaseId.ToString(); return true;
            case "DIR":
            case "DIRECTION": value = Direction.ToString(); return true;
            case "TOPOBJ":
            {
                var world = ResolveWorld?.Invoke();
                if (world != null)
                {
                    ObjBase cur = this;
                    for (int depth = 0; depth < 64; depth++)
                    {
                        if (cur is not Item ci || !ci.ContainedIn.IsValid) break;
                        var parent = world.FindObject(ci.ContainedIn);
                        if (parent == null) break;
                        cur = parent;
                    }
                    value = $"0{cur.Uid.Value:X}";
                }
                else
                {
                    value = $"0{Uid.Value:X}";
                }
                return true;
            }

            // Faz 2: Container properties
            case "COUNT": value = _contents.Count.ToString(); return true;
            case "FCOUNT": value = GetDeepContentCount().ToString(); return true;
            case "EMPTY": value = _contents.Count == 0 ? "1" : "0"; return true;

            // Faz 3: Spellbook
            case "SPELLCOUNT":
                if (_type is ItemType.Spellbook or ItemType.SpellbookNecro or ItemType.SpellbookPala
                    or ItemType.SpellbookExtra or ItemType.SpellbookBushido or ItemType.SpellbookNinjitsu
                    or ItemType.SpellbookArcanist or ItemType.SpellbookMystic or ItemType.SpellbookMastery)
                {
                    ulong mask = ((ulong)_more2 << 32) | _more1;
                    value = CountBits(mask).ToString();
                }
                else
                    value = "0";
                return true;
        }

        // Faz 2: Container dot-notation properties
        if (upper.StartsWith("FINDID.", StringComparison.Ordinal))
        {
            var arg = upper[7..];
            ushort id = ParseHexId(arg);
            var found = FindContentByBaseId(id);
            value = found != null ? $"0{found.Uid.Value:X}" : "";
            return true;
        }
        if (upper.StartsWith("FINDTYPE.", StringComparison.Ordinal))
        {
            var arg = upper[9..];
            ItemType ft = ParseItemType(arg);
            var found = FindContentByType(ft);
            value = found != null ? $"0{found.Uid.Value:X}" : "";
            return true;
        }
        if (upper.StartsWith("FINDCONT.", StringComparison.Ordinal))
        {
            if (int.TryParse(upper[9..], out int idx) && idx >= 0 && idx < _contents.Count)
                value = $"0{_contents[idx].Uid.Value:X}";
            return true;
        }
        if (upper == "RESCOUNT")
        {
            // Source-X CContainer RESCOUNT: number of items directly contained.
            value = _contents.Count.ToString();
            return true;
        }
        if (upper.StartsWith("RESCOUNT.", StringComparison.Ordinal))
        {
            var arg = upper[9..];
            ushort id = ParseHexId(arg);
            value = GetResCount(id).ToString();
            return true;
        }
        if (upper == "INSTANCES")
        {
            // Source-X CBaseBaseDef INSTANCES: count of world items sharing this
            // item's base (matched by display id, as the FORINSTANCES loop does).
            var world = ResolveWorld?.Invoke();
            int count = 0;
            if (world != null)
            {
                ushort myId = BaseId;
                foreach (var obj in world.GetAllObjects())
                    if (obj is Item it && !it.IsDeleted && it.BaseId == myId)
                        count++;
            }
            value = count.ToString();
            return true;
        }
        if (upper.StartsWith("RESTEST ", StringComparison.Ordinal) ||
            upper.StartsWith("RESTEST.", StringComparison.Ordinal))
        {
            // RESTEST amount id [amount id ...]
            var args = key[8..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            value = EvalResTest(args) ? "1" : "0";
            return true;
        }

        // Faz 3: Book/Message properties
        if (_type is ItemType.Book or ItemType.Message)
        {
            if (upper == "AUTHOR") { value = Tags.Get("BOOK_AUTHOR") ?? ""; return true; }
            if (upper == "TITLE") { value = Tags.Get("BOOK_TITLE") ?? Name; return true; }
            if (upper == "PAGES") { value = CountTagsWithPrefix("PAGE_").ToString(); return true; }
            if (upper.StartsWith("BODY.", StringComparison.Ordinal) || upper.StartsWith("PAGE.", StringComparison.Ordinal))
            {
                int dot = upper.IndexOf('.');
                value = Tags.Get($"PAGE_{upper[(dot + 1)..]}") ?? "";
                return true;
            }
        }

        // Faz 3: Map properties
        if (_type is ItemType.Map or ItemType.MapBlank)
        {
            if (upper == "PINS") { value = CountTagsWithPrefix("PIN_").ToString(); return true; }
            if (upper.StartsWith("PIN.", StringComparison.Ordinal))
            {
                value = Tags.Get($"PIN_{upper[4..]}") ?? "";
                return true;
            }
        }

        // Ship properties (resolved via static delegate)
        // Source: CItemShip sm_szLoadKeys + CCMultiMovable sm_szLoadKeys
        if (_type is ItemType.Ship or ItemType.ShipPlank or ItemType.ShipTiller
            or ItemType.ShipHold or ItemType.ShipHoldLock or ItemType.ShipSide
            or ItemType.ShipSideLocked or ItemType.ShipOther)
        {
            var ship = ResolveShip?.Invoke(Uid);
            switch (upper)
            {
                case "TILLER":
                    var tiller = ship?.GetTiller(ResolveWorld!());
                    value = tiller != null ? $"0{tiller.Uid.Value:X}" : "0";
                    return true;
                case "HATCH":
                    var hold = ship?.GetHold(ResolveWorld!());
                    value = hold != null ? $"0{hold.Uid.Value:X}" : "0";
                    return true;
                case "PLANKS":
                    value = (ship?.GetPlankCount() ?? 0).ToString();
                    return true;
                case "SHIPSPEED":
                    // Source-X: "period,tiles" format
                    value = ship != null ? $"{ship.SpeedPeriod},{ship.SpeedTiles}" : "0,0";
                    return true;
                case "PILOT":
                    value = ship?.Pilot.IsValid == true ? $"0{ship.Pilot.Value:X}" : "0";
                    return true;
                case "SHIPANCHOR":
                case "ANCHOR":
                    value = ship?.Anchored == true ? "1" : "0";
                    return true;
                case "DIRFACE":
                    value = ((byte)(ship?.DirFace ?? 0)).ToString();
                    return true;
                case "DIRMOVE":
                    value = ((byte)(ship?.DirMove ?? 0)).ToString();
                    return true;
                case "SPEEDMODE":
                    value = ((byte)(ship?.SpeedMode ?? 0)).ToString();
                    return true;
            }

            // SHIPSPEED.TILES / SHIPSPEED.PERIOD
            if (upper.StartsWith("SHIPSPEED.", StringComparison.Ordinal))
            {
                var sub = upper[10..];
                if (sub == "TILES")
                    value = (ship?.SpeedTiles ?? 0).ToString();
                else if (sub == "PERIOD")
                    value = (ship?.SpeedPeriod ?? 0).ToString();
                else
                    value = "0";
                return true;
            }

            if (upper.StartsWith("PLANK.", StringComparison.Ordinal))
            {
                if (int.TryParse(upper[6..], out int pi))
                {
                    var plank = ship?.GetPlank(pi, ResolveWorld!());
                    value = plank != null ? $"0{plank.Uid.Value:X}" : "0";
                }
                return true;
            }
        }

        // Spawn properties (IT_SPAWN_CHAR)
        if (SpawnChar != null)
        {
            switch (upper)
            {
                case "SPAWNCOUNT" or "SPAWNCUR" or "COUNT":
                    value = SpawnChar.CurrentCount.ToString();
                    return true;
                case "SPAWNMAX":
                    value = SpawnChar.MaxCount.ToString();
                    return true;
                case "SPAWNDEF":
                    value = SpawnChar.GetSpawnDefName();
                    return true;
                case "SPAWNRANGE":
                    value = SpawnChar.SpawnRange.ToString();
                    return true;
                case "PILE":
                    value = "0";
                    return true;
                case "TIMELO":
                    value = Tags.Get("TIMELO") ?? "15";
                    return true;
                case "TIMEHI":
                    value = Tags.Get("TIMEHI") ?? "30";
                    return true;
                case "MAXDIST":
                    value = SpawnChar.SpawnRange.ToString();
                    return true;
            }
            // spawn.AT(n) — access spawned NPC by index
            if (upper.StartsWith("AT(", StringComparison.Ordinal) && upper.EndsWith(')'))
            {
                if (int.TryParse(upper[3..^1], out int idx))
                {
                    var ch = SpawnChar.GetSpawnedAt(idx);
                    value = ch != null ? $"0{ch.Uid.Value:X}" : "0";
                }
                return true;
            }
        }
        if (SpawnItem != null)
        {
            switch (upper)
            {
                case "PILE":
                    value = SpawnItem.Pile.ToString();
                    return true;
            }
        }

        // Guild/town stone properties
        if (TryGetGuildStoneProperty(upper, out value))
            return true;

        // Guild/town stone references: MEMBER.n, MEMBERFROMUID.uid, GUILD.n, GUILDFROMUID.uid
        if (TryGetGuildStoneReference(upper, out value))
            return true;

        // Guild stone relation properties: WEWAR.uid, THEYWAR.uid, ISENEMY.uid, etc.
        if (TryGetGuildRelationProperty(upper, out value))
            return true;

        // Customizable multi: DESIGNER reference
        // Customizable multi references & properties
        if (_type == ItemType.MultiCustom)
        {
            if (upper == "DESIGNER")
            {
                value = Tags.Get("HOUSE_DESIGNER") ?? "0";
                return true;
            }
            if (upper == "EDITAREA")
            {
                value = Tags.Get("HOUSE_EDITAREA") ?? "0,0,0,0";
                return true;
            }
            if (upper == "FIXTURES")
            {
                // Count design components marked as fixtures (5th field = 1)
                int fc = 0;
                foreach (var (k, v) in Tags.GetAll())
                {
                    if (k.StartsWith("DESIGN_", StringComparison.OrdinalIgnoreCase))
                    {
                        var p = v.Split(',');
                        if (p.Length > 4 && p[4] == "1") fc++;
                    }
                }
                value = fc.ToString();
                return true;
            }
            if (upper == "REVISION")
            {
                value = Tags.Get("HOUSE_REVISION") ?? "0";
                return true;
            }
            if (upper == "COMPONENTS")
            {
                // Source-X: count of design components currently in
                // the house design (not the static ITEMDEF component
                // list — that is COMP).
                int cc = 0;
                foreach (var (k, _) in Tags.GetAll())
                    if (k.StartsWith("DESIGN_", StringComparison.OrdinalIgnoreCase))
                        cc++;
                value = cc.ToString();
                return true;
            }
            // DESIGN.n.KEY — get property from nth design component
            if (upper.StartsWith("DESIGN.", StringComparison.Ordinal))
            {
                // Format: DESIGN.n.ID / DESIGN.n.DX / DESIGN.n.DY / DESIGN.n.DZ / DESIGN.n.D / DESIGN.n.FIXTURE
                var rest = upper[7..]; // "n.KEY"
                int dot2 = rest.IndexOf('.');
                if (dot2 > 0 && int.TryParse(rest[..dot2], out int di))
                {
                    string subKey = rest[(dot2 + 1)..];
                    // Design components stored as TAG: DESIGN_n = "id,dx,dy,dz,fixture"
                    string? compData = Tags.Get($"DESIGN_{di}");
                    if (!string.IsNullOrEmpty(compData))
                    {
                        var parts = compData.Split(',');
                        value = subKey switch
                        {
                            "ID" => parts.Length > 0 ? parts[0] : "0",
                            "DX" => parts.Length > 1 ? parts[1] : "0",
                            "DY" => parts.Length > 2 ? parts[2] : "0",
                            "DZ" => parts.Length > 3 ? parts[3] : "0",
                            "D" => parts.Length > 3 ? $"{(parts.Length > 1 ? parts[1] : "0")},{(parts.Length > 2 ? parts[2] : "0")},{parts[3]}" : "0,0,0",
                            "FIXTURE" => parts.Length > 4 ? parts[4] : "0",
                            _ => "0"
                        };
                    }
                }
                return true;
            }
        }

        // Faz 4: Multi properties
        if (_type is ItemType.Multi or ItemType.MultiCustom or ItemType.MultiAddon)
        {
            if (upper == "COMPS")
            {
                var comps = Tags.Get("HOUSE_COMPONENTS");
                value = string.IsNullOrEmpty(comps) ? "0" : comps.Split(',', StringSplitOptions.RemoveEmptyEntries).Length.ToString();
                return true;
            }
            if (upper.StartsWith("COMP.", StringComparison.Ordinal))
            {
                if (int.TryParse(upper[5..], out int ci))
                {
                    var comps = Tags.Get("HOUSE_COMPONENTS")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (comps != null && ci >= 0 && ci < comps.Length)
                        value = comps[ci].Trim();
                }
                return true;
            }
        }

        // Def-fallback: read from ITEMDEF if available
        var def = ResolveDefinition();
        if (def != null)
        {
            // RESOURCES / SKILLMAKE craft lists (Source-X CBaseBaseDef OBC_RESOURCES /
            // CItemBase IBC_SKILLMAKE). Bare = the comma-separated list; .COUNT = the
            // entry count; .<n>[.KEY|.VAL] indexes an entry 1-based (WriteKeys starts
            // at index-1). Quantities live only in the raw string, so parse that.
            if (upper == "RESOURCES" || upper.StartsWith("RESOURCES.", StringComparison.Ordinal))
            {
                if (TryResolveResourceList(def.ResourcesRaw, upper, "RESOURCES", out value))
                    return true;
            }
            if (upper == "SKILLMAKE" || upper.StartsWith("SKILLMAKE.", StringComparison.Ordinal))
            {
                if (TryResolveResourceList(def.SkillMakeRaw, upper, "SKILLMAKE", out value))
                    return true;
            }

            switch (upper)
            {
                case "VALUE": value = def.ValueMin == def.ValueMax ? def.ValueMin.ToString() : $"{def.ValueMin},{def.ValueMax}"; return true;
                case "WEIGHT": value = (Weight / WeightUnits).ToString(); return true;
                case "HEIGHT": value = def.Height.ToString(); return true;
                case "ARMOR": value = def.DefenseMin == def.DefenseMax ? def.DefenseMin.ToString() : $"{def.DefenseMin},{def.DefenseMax}"; return true;
                case "ARMOR.LO": value = def.DefenseMin.ToString(); return true;
                case "ARMOR.HI": value = def.DefenseMax.ToString(); return true;
                case "DAM": value = def.AttackMin == def.AttackMax ? def.AttackMin.ToString() : $"{def.AttackMin},{def.AttackMax}"; return true;
                case "DAM.LO": value = def.AttackMin.ToString(); return true;
                case "DAM.HI": value = def.AttackMax.ToString(); return true;
                case "SPEED": value = def.Speed.ToString(); return true;
                case "SKILL": value = ((int)def.Skill).ToString(); return true;
                case "REQSTR": value = def.ReqStr.ToString(); return true;
                case "RANGE": value = def.RangeMin == def.RangeMax ? def.RangeMin.ToString() : $"{def.RangeMin},{def.RangeMax}"; return true;
                case "RANGEH": value = def.RangeMax.ToString(); return true;
                case "RANGEL": value = def.RangeMin.ToString(); return true;
                case "DYE": value = def.Dye ? "1" : "0"; return true;
                case "FLIP": value = def.Flip ? "1" : "0"; return true;
                case "REPAIR": value = def.Repair ? "1" : "0"; return true;
                case "TWOHANDS": value = def.TwoHands ? "1" : "0"; return true;
                case "ISARMOR": value = (def.DefenseMin > 0 || def.DefenseMax > 0) ? "1" : "0"; return true;
                case "ISWEAPON": value = (def.AttackMin > 0 || def.AttackMax > 0) ? "1" : "0"; return true;
            }
            var extended = def.TagDefs.Get(upper);
            if (extended != null)
            {
                value = extended;
                return true;
            }
        }

        if (upper.StartsWith("LINK.", StringComparison.Ordinal) && _link.IsValid)
        {
            string sub = key["LINK.".Length..];
            var world = ResolveWorld?.Invoke();
            if (world != null)
            {
                var linked = world.FindObject(_link);
                if (linked != null)
                    return linked.TryGetProperty(sub, out value);
            }
            value = "";
            return true;
        }

        // CONT.<prop> / TOPOBJ.<prop> chained reads — item scripts inspect
        // their container/wearer this way (the flash robe's
        // IF (<CONT.FINDLAYER(n)>) gear sweep).
        if (upper.StartsWith("CONT.", StringComparison.Ordinal) && _containedIn.IsValid)
        {
            string sub = key["CONT.".Length..];
            var contObj = ResolveWorld?.Invoke()?.FindObject(_containedIn);
            if (contObj != null)
                return contObj.TryGetProperty(sub, out value);
            value = "";
            return true;
        }
        if (upper.StartsWith("TOPOBJ.", StringComparison.Ordinal))
        {
            string sub = key["TOPOBJ.".Length..];
            var topObj = ResolveTopObject();
            if (topObj != null && !ReferenceEquals(topObj, this))
                return topObj.TryGetProperty(sub, out value);
            value = "";
            return true;
        }

        if (upper.StartsWith("HOUSE.", StringComparison.Ordinal) &&
            TryGetHouseProperty(upper["HOUSE.".Length..], out value))
        {
            return true;
        }

        return base.TryGetProperty(key, out value);
    }

    public override bool TrySetProperty(string key, string value)
    {
        // Champion altar writable keys (Source-X ICHMPL_* loaders).
        if (Champion != null && Champion.TrySetProperty(key, value))
            return true;

        var upper = key.ToUpperInvariant();

        // AOS on-hit combat properties are tag-backed (see TryGetProperty).
        if (AosOnHitProperties.Contains(upper))
        {
            SetTag(upper, value.Trim());
            return true;
        }
        if (SpellCastingProperties.Contains(upper))
        {
            SetTag(upper, value.Trim());
            return true;
        }
        if (upper == CombatSpeedProperties.IncreaseSwingSpeed)
        {
            SetTag(upper, value.Trim());
            return true;
        }

        switch (upper)
        {
            case "TYPE":
            {
                var parsed = ParseItemType(value);
                if (parsed != ItemType.Invalid)
                {
                    _type = parsed;
                    if (parsed is ItemType.SpawnChar or ItemType.SpawnItem or ItemType.SpawnChampion)
                        OnSpawnTypeChanged?.Invoke(this);
                }
                return true;
            }
            case "AMOUNT":
                if (ushort.TryParse(value, out ushort av)) Amount = av;
                return true;
            case "MAXAMOUNT":
                // Per-item stack cap (Source-X SetMaxAmount). The getter gates on
                // stackability, so a value on a non-stackable item is inert. A
                // negative/blank value clears the override back to the global default.
                if (int.TryParse(value, out int maxAmt) && maxAmt >= 0)
                    _maxAmountOverride = Math.Min(maxAmt, ushort.MaxValue);
                else
                    _maxAmountOverride = null;
                return true;
            case "BASEWEIGHT":
                // Source-X sets m_weight on the instance (per-unit tenths). A
                // negative/blank value clears the override back to the def weight.
                if (int.TryParse(value, out int bwv) && bwv >= 0) _weightOverride = bwv;
                else _weightOverride = null;
                return true;
            case "HITS":
            case "HITPOINTS":
                if (int.TryParse(value, out int hits)) HitsCur = hits;
                return true;
            case "MAXHITS":
            case "HITSMAX":
                if (int.TryParse(value, out int maxHits)) HitsMax = maxHits;
                return true;

            // Move this item into another container (or a char's pack). Very
            // common in crafting/loot/vendor scripts: <new>.cont <pack.uid>.
            case "CONT":
                return TryMoveToContainer(value);

            // Set Z (height) of a grounded/contained item.
            case "Z":
                if (int.TryParse(value, out int zVal))
                    Position = new Point3D(X, Y,
                        (sbyte)Math.Clamp(zVal, sbyte.MinValue, sbyte.MaxValue), MapIndex);
                return true;
            case "DIR":
            case "DIRECTION":
                if (byte.TryParse(value, out byte dirVal))
                {
                    Direction = (byte)(dirVal & 0x07);
                    MarkDirty(DirtyFlag.Direction);
                }
                return true;

            // Set the equip layer (used when scripts equip an item directly).
            case "LAYER":
                if (byte.TryParse(value, out byte layerVal))
                    EquipLayer = (Layer)layerVal;
                return true;

            case "SLAYER_GROUP": SetTag("SLAYER_GROUP", value.Trim()); return true;
            case "SLAYER_SPECIES": SetTag("SLAYER_SPECIES", value.Trim()); return true;

            // Faz 1: Core fields
            case "MORE1": case "MORE":
            {
                uint v = ParseHexOrDecUInt(value);
                if (v == 0 && value.Length > 0 && value.Any(char.IsLetter) && ResolveDefName != null)
                {
                    ushort resolved = ResolveDefName(value);
                    if (resolved != 0) v = resolved;
                    else SetTag("MORE1_DEFNAME", value);
                }
                _more1 = v;
                return true;
            }
            case "MORE2":
                // Sphere fixed-point decimals: "50.0" stores 500 (tenths) —
                // potion strengths (MORE2=50.0) and similar script values.
                _more2 = ParseSphereFixedOrUInt(value);
                return true;
            case "MOREB":
                _moreB = ParseHexOrDecUInt(value);
                return true;
            case "MORE1H":
                if (ushort.TryParse(value, out ushort m1h))
                    _more1 = (_more1 & 0x0000FFFF) | ((uint)m1h << 16);
                return true;
            case "MORE1L":
                if (ushort.TryParse(value, out ushort m1l))
                    _more1 = (_more1 & 0xFFFF0000) | m1l;
                return true;
            case "MORE2H":
                if (ushort.TryParse(value, out ushort m2h))
                    _more2 = (_more2 & 0x0000FFFF) | ((uint)m2h << 16);
                return true;
            case "MORE2L":
                if (ushort.TryParse(value, out ushort m2l))
                    _more2 = (_more2 & 0xFFFF0000) | m2l;
                return true;
            case "MOREP":
                if (Point3D.TryParse(value, out var mp)) _moreP = mp;
                return true;
            case "MOREX":
                if (short.TryParse(value, out short mx)) _moreP = new Point3D(mx, _moreP.Y, _moreP.Z, _moreP.Map);
                return true;
            case "MOREY":
                if (short.TryParse(value, out short my)) _moreP = new Point3D(_moreP.X, my, _moreP.Z, _moreP.Map);
                return true;
            case "MOREZ":
                if (sbyte.TryParse(value, out sbyte mz)) _moreP = new Point3D(_moreP.X, _moreP.Y, mz, _moreP.Map);
                return true;
            case "LINK":
                _link = new Serial(ParseHexOrDecUInt(value));
                return true;
            case "PRICE":
                if (int.TryParse(value, out int pv)) _price = pv;
                return true;
            case "QUALITY":
                if (ushort.TryParse(value, out ushort qv)) _quality = qv;
                return true;
            case "CRAFTER":
            case "CRAFTEDBY":
                _crafter = new Serial(ParseHexOrDecUInt(value));
                return true;
            case "USESREMAINING":
            case "USESCUR":
                if (ushort.TryParse(value, out ushort ur)) _usesRemaining = ur;
                return true;
            case "USESMAX":
                SetTag("USESMAX", value);
                return true;
            case "DECAY":
                if (long.TryParse(value, out long decaySec) && decaySec > 0)
                    DecayTime = Environment.TickCount64 + decaySec * 1000;
                return true;
            case "TIMER":
                // Sphere has ONE item timer: TIMER drives the @Timer script
                // AND decay. This used to arm ONLY DecayTime (the legacy-save
                // import reading), so every scripted `TIMER n` line — the
                // flash-robe color loop, trap rearms, door autoclose written
                // as a property — never armed the script timer at all.
                // Negative = disable (the scripts' off switch). The decay
                // meaning is mirrored for decayable items so imported loose
                // ground items still rot on their saved schedule.
                if (long.TryParse(value, out long timerSec))
                {
                    SetTimeout(timerSec < 0
                        ? 0
                        : Environment.TickCount64 + timerSec * 1000);
                    if (timerSec > 0 &&
                        (IsAttr(ObjAttributes.Decay) ||
                         (IsOnGround && !IsAttr(ObjAttributes.Move_Never) &&
                          !IsAttr(ObjAttributes.Static))))
                        DecayTime = Environment.TickCount64 + timerSec * 1000;
                }
                return true;

            case "AUTHOR": // books / bulletin messages read the AUTHOR tag
                SetTag("AUTHOR", value);
                return true;

            case "TIMERF": // restore a persisted TIMERF/TIMERFMS timer (world load)
                return TryLoadTimerFEntry(value);

            // TDATA instance
            case "TDATA1": _tdata1 = ParseHexOrDecUInt(value); return true;
            case "TDATA2": _tdata2 = ParseHexOrDecUInt(value); return true;
            case "TDATA3": _tdata3 = ParseHexOrDecUInt(value); return true;
            case "TDATA4": _tdata4 = ParseHexOrDecUInt(value); return true;

            case "DISPID":
            {
                uint parsed = ParseHexOrDecUInt(value);
                if (parsed != 0)
                    _dispId = (ushort)parsed;
                else if (ResolveDefName != null)
                {
                    ushort resolved = ResolveDefName(value);
                    if (resolved != 0) _dispId = resolved;
                }
                return true;
            }
            case "CONTGRID":
                if (byte.TryParse(value, out byte gv)) _containerGridIndex = gv;
                return true;

            // Sphere spawner properties — round-trip as TAGs and apply to the
            // active spawn component (char OR item) so the interval / scatter range
            // take effect. (This case is hit before the spawn-component blocks
            // below, so the application must happen here.)
            case "SPAWNID": case "TIMELO": case "TIMEHI": case "MAXDIST":
                SetTag(upper, value);
                if (upper == "MAXDIST" && int.TryParse(value, out int spawnMd))
                {
                    if (SpawnChar != null) SpawnChar.SpawnRange = spawnMd;
                    if (SpawnItem != null) SpawnItem.SpawnRange = spawnMd;
                }
                else if (upper is "TIMELO" or "TIMEHI")
                {
                    int spLo = int.TryParse(Tags.Get("TIMELO"), out int l) ? l : 15;
                    int spHi = int.TryParse(Tags.Get("TIMEHI"), out int h) ? h : 30;
                    SpawnChar?.SetDelay(spLo, spHi);
                    SpawnItem?.SetDelay(spLo, spHi);
                }
                return true;
            case "ADDOBJ":
            {
                string? existing = Tags.Get(upper);
                SetTag(upper, string.IsNullOrEmpty(existing) ? value : $"{existing},{value}");
                return true;
            }
            // Multi/housing properties — round-trip as TAGs
            case "REGION.FLAGS": case "REGION.EVENTS": case "OWNER": case "HOUSETYPE":
                SetTag(upper, value);
                return true;
            // Storage budget properties. The tag is still written so a template
            // applied before the house is registered keeps working, but when the
            // multi already IS a live house the value must reach it — writing
            // only the tag left the running house on its defaults.
            case "LOCKDOWNSPERCENT": case "BASEVENDORS": case "BASESTORAGE":
            case "INCREASEDSTORAGE":
            {
                SetTag(upper, value);
                var liveHouse = ResolveHouse?.Invoke(Uid);
                if (liveHouse != null && int.TryParse(value.Trim(), out int storageVal))
                {
                    switch (upper)
                    {
                        case "LOCKDOWNSPERCENT": liveHouse.LockdownsPercent = storageVal; break;
                        case "BASEVENDORS": liveHouse.BaseVendors = storageVal; break;
                        case "BASESTORAGE": liveHouse.BaseStorage = storageVal; break;
                        case "INCREASEDSTORAGE": liveHouse.IncreasedStorage = storageVal; break;
                    }
                }
                return true;
            }
            // Multi-valued housing properties — accumulate comma-separated
            case "ADDCOMP": case "SECURE": case "LOCKITEM":
            {
                string? existing = Tags.Get(upper);
                SetTag(upper, string.IsNullOrEmpty(existing) ? value : $"{existing},{value}");
                return true;
            }
        }

        // Faz 3: Book/Message set
        if (_type is ItemType.Book or ItemType.Message)
        {
            if (upper == "AUTHOR") { Tags.Set("BOOK_AUTHOR", value); return true; }
            if (upper == "TITLE") { Tags.Set("BOOK_TITLE", value); return true; }
            if (upper.StartsWith("BODY.", StringComparison.Ordinal))
            {
                // BODY.n — append a new line (value is the text)
                int pageCount = CountTagsWithPrefix("PAGE_");
                Tags.Set($"PAGE_{pageCount}", value);
                return true;
            }
            if (upper.StartsWith("PAGE.", StringComparison.Ordinal))
            {
                int dot = upper.IndexOf('.');
                Tags.Set($"PAGE_{upper[(dot + 1)..]}", value);
                return true;
            }
        }

        // Map: PIN.n set
        if ((_type is ItemType.Map or ItemType.MapBlank) &&
            upper.StartsWith("PIN.", StringComparison.Ordinal))
        {
            Tags.Set($"PIN_{upper[4..]}", value);
            return true;
        }

        // Ship property set — Source: CCMultiMovable::r_LoadVal
        if (_type == ItemType.Ship)
        {
            var ship = ResolveShip?.Invoke(Uid);
            if (ship != null)
            {
                switch (upper)
                {
                    case "ANCHOR":
                    case "SHIPANCHOR":
                        ship.Anchored = value != "0";
                        if (ship.Anchored)
                            ResolveShipEngine?.Invoke()?.Stop(ship);
                        return true;
                    case "SPEEDMODE":
                        if (byte.TryParse(value, out byte sm))
                            ship.SpeedMode = (Core.Enums.ShipSpeedMode)Math.Clamp(sm, (byte)1, (byte)4);
                        return true;
                    case "PILOT":
                    {
                        var engine = ResolveShipEngine?.Invoke();
                        var pilotUid = new Serial(ParseHexOrDecUInt(value));
                        if (engine != null)
                            engine.SetPilot(ship, pilotUid.IsValid ? ResolveWorld?.Invoke()?.FindChar(pilotUid) : null);
                        else if (!pilotUid.IsValid)
                            ship.Pilot = Serial.Invalid;
                        return true;
                    }
                }

                if (upper.StartsWith("SHIPSPEED.", StringComparison.Ordinal))
                {
                    var sub = upper[10..];
                    if (sub == "TILES")
                    {
                        if (byte.TryParse(value, out byte t)) ship.SpeedTiles = Math.Clamp(t, (byte)1, (byte)16);
                    }
                    else if (sub == "PERIOD")
                    {
                        if (ushort.TryParse(value, out ushort p)) ship.SpeedPeriod = Math.Max((ushort)1, p);
                    }
                    return true;
                }

                if (upper == "SHIPSPEED")
                {
                    // "period,tiles" format
                    var parts = value.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        if (ushort.TryParse(parts[0], out ushort p)) ship.SpeedPeriod = Math.Max((ushort)1, p);
                        if (byte.TryParse(parts[1], out byte t)) ship.SpeedTiles = Math.Clamp(t, (byte)1, (byte)16);
                    }
                    return true;
                }
            }
        }

        // Spawn property set (IT_SPAWN_CHAR)
        if (SpawnChar != null)
        {
            switch (upper)
            {
                case "SPAWNMAX" or "AMOUNT":
                    if (int.TryParse(value, out int sm)) SpawnChar.MaxCount = sm;
                    return true;
                case "SPAWNRANGE" or "MAXDIST":
                    if (int.TryParse(value, out int sr)) SpawnChar.SpawnRange = sr;
                    return true;
                case "TIMELO":
                {
                    SetTag("TIMELO", value);
                    if (int.TryParse(value, out int lo))
                    {
                        string? hiStr = Tags.Get("TIMEHI");
                        int hi = 30;
                        if (hiStr != null) int.TryParse(hiStr, out hi);
                        SpawnChar.SetDelay(lo, hi);
                    }
                    return true;
                }
                case "TIMEHI":
                {
                    SetTag("TIMEHI", value);
                    if (int.TryParse(value, out int hi))
                    {
                        string? loStr = Tags.Get("TIMELO");
                        int lo = 15;
                        if (loStr != null) int.TryParse(loStr, out lo);
                        SpawnChar.SetDelay(lo, hi);
                    }
                    return true;
                }
            }
        }
        if (SpawnItem != null)
        {
            switch (upper)
            {
                case "PILE":
                    if (int.TryParse(value, out int pv)) SpawnItem.Pile = pv;
                    return true;
                case "SPAWNRANGE": // MAXDIST alias handled in the spawner-property case above
                    if (int.TryParse(value, out int isr)) SpawnItem.SpawnRange = isr;
                    return true;
            }
        }

        // Guild stone properties: ABBREV, ALIGN, MASTERUID
        if (TrySetGuildStoneProperty(key.ToUpperInvariant(), value))
            return true;

        // Guild stone relation properties: WEWAR.uid, THEYWAR.uid, etc.
        if (TrySetGuildRelationProperty(key.ToUpperInvariant(), value))
            return true;

        return base.TrySetProperty(key, value);
    }

    public override bool TryExecuteCommand(string key, string args, ITextConsole source)
    {
        var upper = key.ToUpperInvariant();

        // Chained object dispatch (Source-X CScriptObj r_Verb ref heads):
        // TOPOBJ.<rest> / CONT.<rest> / LINK.<rest> route the remainder of
        // the line to the resolved object — item scripts drive their wearer
        // this way (the flash robe's TOPOBJ.FINDLAYER(5).COLOR <hue>).
        // Command first, property-set fallback; an invalid ref swallows the
        // line like Sphere does.
        if (upper.StartsWith("TOPOBJ.", StringComparison.Ordinal) ||
            upper.StartsWith("CONT.", StringComparison.Ordinal) ||
            upper.StartsWith("LINK.", StringComparison.Ordinal))
        {
            int headDot = key.IndexOf('.');
            string head = upper[..headDot];
            string chainTail = key[(headDot + 1)..];
            var chainWorld = ResolveWorld?.Invoke();
            ObjBase? refObj = head switch
            {
                "TOPOBJ" => ResolveTopObject(),
                "CONT" => _containedIn.IsValid ? chainWorld?.FindObject(_containedIn) : null,
                "LINK" => _link.IsValid ? chainWorld?.FindObject(_link) : null,
                _ => null,
            };
            if (refObj == null)
                return true;
            if (refObj.TryExecuteCommand(chainTail, args, source))
                return true;
            if (args.Length > 0 && refObj.TrySetProperty(chainTail, args))
                return true;
            return true;
        }

        // Champion altar verbs (Source-X ICHMPV_*: START/STOP/INIT/ADDSPAWN/
        // DELREDCANDLE/DELWHITECANDLE/ADDOBJ/DELOBJ) win when the component
        // is attached.
        if (Champion != null && Champion.TryExecuteVerb(upper, args, source as Characters.Character))
            return true;

        switch (upper)
        {
            // CONTP x,y — reposition a CONTAINED item at (x,y) inside its
            // container (Source-X CItem IC_CONTP). A ground/equipped item is
            // a no-op like the reference. GM item-creation scripts use it to
            // lay out contents (NEW.CONTP 44,121).
            case "CONTP":
            {
                if (!_containedIn.IsValid) return true;
                var coords = args.Split([',', ' ', '\t'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (coords.Length >= 2 &&
                    short.TryParse(coords[0], out short cpx) &&
                    short.TryParse(coords[1], out short cpy))
                {
                    Position = new Point3D(cpx, cpy, Position.Z, Position.Map);
                }
                return true;
            }
            // Clone this item (optionally <n> times) into the same location.
            case "DUPE":
            {
                var dupeWorld = ResolveWorld?.Invoke();
                if (dupeWorld == null) return true;
                int dupeCount = 1;
                if (!string.IsNullOrWhiteSpace(args) && int.TryParse(args.Trim(), out int dc) && dc > 0)
                    dupeCount = Math.Min(dc, 1000);
                var parent = _containedIn.IsValid ? dupeWorld.FindObject(_containedIn) : null;
                for (int n = 0; n < dupeCount; n++)
                {
                    var copy = dupeWorld.CreateItem();
                    copy.CopyStackInstanceStateFrom(this);
                    copy.Amount = Amount;
                    bool placed = parent switch
                    {
                        Item contItem => contItem.TryAddItem(copy),
                        Character wearer when wearer.Backpack != null &&
                            (wearer.PrivLevel >= PrivLevel.GM || wearer.CanCarry(copy))
                            => wearer.Backpack.TryAddItem(copy),
                        Character wearer => dupeWorld.PlaceItemWithDecay(copy, wearer.Position),
                        _ => dupeWorld.PlaceItem(copy, Position)
                    };
                    if (!placed)
                        dupeWorld.RemoveItem(copy);
                }
                return true;
            }

            // Faz 2: Container commands
            case "OPEN":
                OnScriptOpen?.Invoke(this, source);
                return true;
            case "DELETE":
                // DELETE nth — 1-based index
                if (int.TryParse(args.Trim(), out int delIdx) && delIdx >= 1 && delIdx <= _contents.Count)
                {
                    var target = _contents[delIdx - 1];
                    _contents.RemoveAt(delIdx - 1);
                    target.RemoveFromWorld();
                }
                return true;
            case "EMPTY":
                foreach (var child in _contents.ToArray())
                    child.RemoveFromWorld();
                _contents.Clear();
                return true;

            // Source-X CIV_CONSUME: consume N (default 1) from this stack;
            // the item is removed when the whole amount is used up.
            case "CONSUME":
            {
                int n = 1;
                if (!string.IsNullOrWhiteSpace(args) && int.TryParse(args.Trim(), out int reqN) && reqN > 0)
                    n = reqN;
                if (n >= _amount)
                    RemoveFromWorld();
                else
                    Amount = (ushort)(_amount - n);
                return true;
            }

            // Source-X CIV_CONTCONSUME: consume N of an item id from this
            // container's contents, recursing into sub-containers.
            case "CONTCONSUME":
            {
                var p = args.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (p.Length == 0) return true;

                ushort wantedId = ResolveDefName?.Invoke(p[0]) ?? 0;
                if (wantedId == 0)
                {
                    string idStr = p[0];
                    if (idStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idStr = idStr[2..];
                    else if (idStr.StartsWith('0') && idStr.Length > 1) idStr = idStr[1..];
                    ushort.TryParse(idStr, System.Globalization.NumberStyles.HexNumber, null, out wantedId);
                }
                if (wantedId == 0) return true;

                int need = p.Length > 1 && int.TryParse(p[1], out int n) && n > 0 ? n : 1;
                ConsumeFromContents(this, wantedId, ref need);
                return true;
            }

            // Source-X CIV_BOUNCE: put the item back into its top-level
            // owner's backpack (a loose ground item stays put).
            case "BOUNCE":
            {
                var world = ResolveWorld?.Invoke();
                if (world == null) return true;
                ObjBase cur = this;
                for (int depth = 0; depth < 64; depth++)
                {
                    if (cur is not Item ci || !ci.ContainedIn.IsValid) break;
                    var parent = world.FindObject(ci.ContainedIn);
                    if (parent == null) break;
                    cur = parent;
                }
                if (cur is Character owner && owner.Backpack != null && owner.Backpack != this)
                {
                    if (IsEquipped && ContainedIn == owner.Uid)
                        owner.Unequip(EquipLayer);
                    var oldParent = ContainedIn.IsValid ? world.FindObject(ContainedIn) as Item : null;
                    oldParent?.RemoveItem(this);
                    IsEquipped = false;
                    if (!owner.Backpack.TryAddItem(this))
                        world.PlaceItemWithDecay(this, owner.Position);
                }
                return true;
            }

            // Source-X CIV_DROP: detach the item from its current container or
            // equipment slot and place it at the top-level owner's position.
            case "DROP":
            {
                var world = ResolveWorld?.Invoke();
                if (world == null) return false;
                Point3D dropPoint = GetTopLevelPosition();
                return world.PlaceItem(this, dropPoint);
            }

            // Source-X CIV_UNEQUIP: bounce this item into the invoking
            // character's backpack, regardless of who currently wears it.
            case "UNEQUIP":
            {
                var sourceChar = (source as IClientContext)?.Character;
                var world = ResolveWorld?.Invoke();
                if (sourceChar == null || world == null)
                    return false;

                Characters.Character? unequippedFrom = null;
                if (ContainedIn.IsValid && world.FindObject(ContainedIn) is Character wearer &&
                    IsEquipped && wearer.GetEquippedItem(EquipLayer) == this)
                {
                    unequippedFrom = wearer;
                    wearer.Unequip(EquipLayer);
                }
                else if (ContainedIn.IsValid && world.FindObject(ContainedIn) is Item parent)
                    parent.RemoveItem(this);

                IsEquipped = false;
                // Source-X CIV_UNEQUIP runs ITRIG_UnEquip after the bounce.
                if (unequippedFrom != null)
                    OnItemUnequipped?.Invoke(this, unequippedFrom);
                if (sourceChar.Backpack != null && sourceChar.Backpack.TryAddItem(this))
                    return true;
                return world.PlaceItemWithDecay(this, sourceChar.Position);
            }

            // Source-X CIV_USEDOOR bypasses key/lock checks and directly
            // toggles the door state. It is intentionally distinct from USE.
            case "USEDOOR":
            {
                var world = ResolveWorld?.Invoke();
                if (!DoorHelper.IsDoorItem(this, world?.MapData))
                    return false;

                int doorDir = DoorHelper.GetDoorDir(DispIdFull);
                bool isOpen = doorDir >= 0
                    ? (doorDir & 1) != 0
                    : TryGetTag("DOOR_OPEN", out string? open) && open == "1";
                bool changed = isOpen ? CloseDoor() : DoorHelper.TryOpenDoorState(this, ignoreLock: true);
                if (changed)
                {
                    MarkDirty((DirtyFlag)0xFFFFFFFF);
                    OnVisualUpdate?.Invoke(this);
                }
                return changed;
            }

            // Source-X CIV_DECAY: arm (or re-arm) the decay timer — args are
            // seconds; empty falls back to the shard default decay window.
            case "DECAY":
            {
                long decayMs = World.GameWorld.DefaultDecayTimeMs;
                if (!string.IsNullOrWhiteSpace(args) && long.TryParse(args.Trim(), out long decSec) && decSec > 0)
                    decayMs = decSec * 1000L;
                DecayTime = Environment.TickCount64 + decayMs;
                return true;
            }
            case "FIXWEIGHT":
                // Weight is computed on demand (TotalWeight walks contents);
                // refresh the client view so tooltips pick the value up.
                MarkDirty((DirtyFlag)0xFFFFFFFF);
                return true;
            case "UPDATE":
            case "UPDATEX":
            case "REMOVEFROMVIEW":
                MarkDirty((DirtyFlag)0xFFFFFFFF);
                return true;

            // Source-X CV_MOVE: shift the item by a (dx,dy,dz) tuple.
            // Args: "<dx>,<dy>[,<dz>]" or "<dx> <dy> [<dz>]".
            case "MOVE":
            {
                var parts = args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2) return true;
                if (!short.TryParse(parts[0], out short dx) ||
                    !short.TryParse(parts[1], out short dy))
                    return true;
                sbyte dz = 0;
                if (parts.Length >= 3 && sbyte.TryParse(parts[2], out sbyte tz)) dz = tz;
                var p = Position;
                Position = new Point3D(
                    (short)(p.X + dx), (short)(p.Y + dy), (sbyte)(p.Z + dz), p.Map);
                return true;
            }
            // Source-X CV_FLIP: rotate the item's facing if it has a
            // matching flipped graphic (def->Flip flag). For items
            // without a flip pair this is a no-op.
            case "FLIP":
            {
                TryFlipDisplay();
                return true;
            }
            // Source-X CV_DCLICK: simulate a double-click on this item.
            // OnScriptDClick routes through the acting client's dclick path
            // (containers, doors, potions, ...); ack even when unwired so
            // the X-prefix chain doesn't fall through to script fallback.
            case "DCLICK":
            case "USE":
                OnScriptDClick?.Invoke(this, source);
                return true;

            // Custom multi design commands
            case "ADDITEM":
            {
                if (_type == ItemType.MultiCustom)
                {
                    // ADDITEM item_id, dx, dy, dz
                    var parts = args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 4)
                    {
                        int designCount = CountTagsWithPrefix("DESIGN_");
                        Tags.Set($"DESIGN_{designCount}", $"{parts[0]},{parts[1]},{parts[2]},{parts[3]},0");
                    }
                }
                return true;
            }
            case "ADDMULTI":
            {
                if (_type == ItemType.MultiCustom)
                {
                    // ADDMULTI multi_id, dx, dy, dz
                    var parts = args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 4)
                    {
                        int designCount = CountTagsWithPrefix("DESIGN_");
                        Tags.Set($"DESIGN_{designCount}", $"{parts[0]},{parts[1]},{parts[2]},{parts[3]},1");
                    }
                }
                return true;
            }
            case "CLEAR":
            {
                if (_type == ItemType.MultiCustom)
                {
                    foreach (var (k, _) in Tags.GetAll().ToArray())
                    {
                        if (k.StartsWith("DESIGN_", StringComparison.OrdinalIgnoreCase))
                            Tags.Remove(k);
                    }
                }
                return true;
            }
            case "COMMIT":
            {
                // Commit design changes — actual multi rebuild handled at engine level
                if (_type == ItemType.MultiCustom)
                {
                    Tags.Set("HOUSE_DESIGN_COMMITTED", "1");
                    int rev = int.TryParse(Tags.Get("HOUSE_REVISION") ?? "0", out int r) ? r : 0;
                    Tags.Set("HOUSE_REVISION", (rev + 1).ToString());
                }
                return true;
            }
            case "CUSTOMIZE":
            {
                if (_type == ItemType.MultiCustom)
                {
                    string uid = args.Trim();
                    if (uid.Length > 0)
                        Tags.Set("HOUSE_DESIGNER", uid);
                }
                return true;
            }
            case "ENDCUSTOMIZE":
            {
                if (_type == ItemType.MultiCustom)
                    Tags.Remove("HOUSE_DESIGNER");
                return true;
            }
            case "REMOVEITEM":
            {
                if (_type == ItemType.MultiCustom)
                {
                    // REMOVEITEM item_id, dx, dy, dz — find and remove matching design entry
                    var parts = args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 4)
                    {
                        string target = $"{parts[0]},{parts[1]},{parts[2]},{parts[3]}";
                        foreach (var (k, v) in Tags.GetAll().ToArray())
                        {
                            if (k.StartsWith("DESIGN_", StringComparison.OrdinalIgnoreCase) &&
                                v.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                            {
                                Tags.Remove(k);
                                break;
                            }
                        }
                    }
                }
                return true;
            }
            case "RESET":
            {
                // Reset design to foundation — clear all design entries
                if (_type == ItemType.MultiCustom)
                {
                    foreach (var (k, _) in Tags.GetAll().ToArray())
                    {
                        if (k.StartsWith("DESIGN_", StringComparison.OrdinalIgnoreCase))
                            Tags.Remove(k);
                    }
                    int rev = int.TryParse(Tags.Get("HOUSE_REVISION") ?? "0", out int r) ? r : 0;
                    Tags.Set("HOUSE_REVISION", (rev + 1).ToString());
                }
                return true;
            }
            case "REVERT":
            {
                // Undo changes since last commit — clear uncommitted flag
                if (_type == ItemType.MultiCustom)
                    Tags.Remove("HOUSE_DESIGN_COMMITTED");
                return true;
            }
            case "RESYNC":
            {
                MarkDirty(DirtyFlag.Position | DirtyFlag.Body | DirtyFlag.Hue | DirtyFlag.Name | DirtyFlag.Amount | DirtyFlag.Container);
                return true;
            }
            case "MULTICREATE":
            {
                // Source-X: MULTICREATE owner_uid — must be used immediately
                // after SERV.NEWITEM creates a multi, so the multi region
                // is initialised against that owner. We set HOUSE.OWNER
                // so HousingEngine.DeserializeFromWorld (and the runtime
                // register delegate, if wired) picks it up.
                if (_type is ItemType.Multi or ItemType.MultiCustom)
                {
                    string uidStr = args.Trim();
                    if (uidStr.Length > 0)
                    {
                        Tags.Set("HOUSE.OWNER", uidStr);
                        OnHouseRegister?.Invoke(this);
                    }
                }
                return true;
            }

            // Source-X CItemMulti verb surface (sm_szVerbKeys): the house
            // management verbs drive the LIVE House object at runtime —
            // previously they only landed in tags and had no effect until a
            // world reload. Resolved through the ResolveHouse hook (wired by
            // the housing engine); args are a serial for the list verbs.
            case "ADDCOOWNER":
            case "DELCOOWNER":
            case "ADDFRIEND":
            case "DELFRIEND":
            case "ADDBAN":
            case "DELBAN":
            case "ADDACCESS":
            case "DELACCESS":
            case "LOCKITEM":
            case "UNLOCKITEM":
            case "SECURE":
            case "RELEASE":
            case "REDEED":
            {
                // Ship multis carry ItemType.Ship — without it here the ship
                // REDEED route below was unreachable (caught by the verb-path
                // integration test).
                if (_type is not (ItemType.Multi or ItemType.MultiCustom or ItemType.Ship))
                    break; // not a multi — fall through to the generic paths
                var house = ResolveHouse?.Invoke(Uid);
                if (house == null)
                {
                    // Not a registered house: a SHIP multi dry-docks through
                    // the ship engine (cargo crate + teardown + deed).
                    if (key.Equals("REDEED", StringComparison.OrdinalIgnoreCase) && RedeedShip != null)
                        RedeedShip(Uid);
                    return true;
                }

                string keyUpper = key.ToUpperInvariant();
                if (keyUpper == "REDEED")
                {
                    var world = ResolveWorld?.Invoke();
                    if (world != null)
                    {
                        var deed = RedeedHouse != null ? RedeedHouse(Uid) : house.Redeed(world);
                        if (deed != null)
                        {
                            var owner = house.Owner.IsValid ? world.FindChar(house.Owner) : null;
                            bool delivered = owner?.Backpack != null &&
                                (owner.PrivLevel >= PrivLevel.GM || owner.CanCarry(deed)) &&
                                owner.Backpack.TryAddItem(deed);
                            if (!delivered)
                                world.PlaceItemWithDecay(deed, Position);
                        }
                    }
                    return true;
                }

                uint argUid = ParseHexOrDecUInt(args.Trim());
                if (argUid == 0)
                    return true;
                var subject = new Serial(argUid);
                // Script-driven management acts with owner authority.
                var actor = house.Owner;
                switch (keyUpper)
                {
                    case "ADDCOOWNER": house.AddCoOwner(subject); break;
                    case "DELCOOWNER": house.RemoveCoOwner(subject); break;
                    case "ADDFRIEND": house.AddFriend(subject); break;
                    case "DELFRIEND": house.RemoveFriend(subject); break;
                    case "ADDBAN": house.AddBan(subject); break;
                    case "DELBAN": house.RemoveBan(subject); break;
                    case "ADDACCESS": house.AddAccess(subject); break;
                    case "DELACCESS": house.RemoveAccess(subject); break;
                    case "LOCKITEM": house.Lockdown(subject, actor); break;
                    case "UNLOCKITEM": house.ReleaseLockdown(subject, actor); break;
                    case "SECURE": house.SecureContainer(subject, actor); break;
                    case "RELEASE":
                        if (!house.ReleaseSecure(subject, actor))
                            house.ReleaseLockdown(subject, actor);
                        break;
                }
                return true;
            }

            // Map: PIN x,y — add a new pin
            case "PIN":
            {
                if (_type is ItemType.Map or ItemType.MapBlank)
                {
                    var parts = args.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        int pinCount = CountTagsWithPrefix("PIN_");
                        Tags.Set($"PIN_{pinCount}", $"{parts[0]},{parts[1]}");
                    }
                }
                return true;
            }

            // Faz 3: Book ERASE [page_num]
            case "ERASE":
                if (_type is ItemType.Book or ItemType.Message)
                {
                    var eraseArg = args.Trim();
                    if (eraseArg.Length > 0 && int.TryParse(eraseArg, out int erasePage) && erasePage >= 1)
                    {
                        // Erase single page (1-based)
                        Tags.Remove($"PAGE_{erasePage - 1}");
                    }
                    else
                    {
                        // Erase all pages
                        foreach (var (k, _) in Tags.GetAll().ToArray())
                        {
                            if (k.StartsWith("PAGE_", StringComparison.OrdinalIgnoreCase))
                                Tags.Remove(k);
                        }
                    }
                }
                return true;

            // Faz 3: Spellbook commands
            case "ADDSPELL":
                if (int.TryParse(args.Trim(), out int addSpell) && addSpell >= 0 && addSpell < 64)
                {
                    if (addSpell < 32)
                        _more1 |= (1u << addSpell);
                    else
                        _more2 |= (1u << (addSpell - 32));
                }
                return true;
            case "REMOVESPELL":
                if (int.TryParse(args.Trim(), out int rmSpell) && rmSpell >= 0 && rmSpell < 64)
                {
                    if (rmSpell < 32)
                        _more1 &= ~(1u << rmSpell);
                    else
                        _more2 &= ~(1u << (rmSpell - 32));
                }
                return true;

            // Ship navigation commands
            case "SHIPFORE": case "SHIPBACK": case "SHIPLEFT": case "SHIPRIGHT":
            case "SHIPFORELEFT": case "SHIPFORERIGHT": case "SHIPBACKLEFT": case "SHIPBACKRIGHT":
            case "SHIPDRIFTLEFT": case "SHIPDRIFTRIGHT":
            case "SHIPTURNAROUND": case "SHIPTURN": case "SHIPTURNLEFT": case "SHIPTURNRIGHT":
            case "SHIPANCHORDROP": case "SHIPANCHORRAISE": case "SHIPANCHOR": case "SHIPSTOP":
            case "SHIPMOVE": case "SHIPFACE": case "SHIPGATE": case "SHIPUP": case "SHIPDOWN":
            case "SHIPLAND":
            {
                var engine = ResolveShipEngine?.Invoke();
                var ship = ResolveShip?.Invoke(Uid);
                if (engine != null && ship != null)
                    engine.ExecuteCommand(ship, upper, args);
                return true;
            }

            // Guild/town stone commands
            case "DECLAREWAR":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0 && ResolveGuildManager?.Invoke()?.DeclareWar(Uid, new Serial(uid)) != true)
                        guild.DeclareWar(new Serial(uid));
                }
                return true;
            }
            case "DECLAREPEACE":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0 && ResolveGuildManager?.Invoke()?.DeclarePeace(Uid, new Serial(uid)) != true)
                        guild.DeclarePeace(new Serial(uid));
                }
                return true;
            }
            case "INVITEWAR":
            {
                // INVITEWAR stone_uid, who_declared (0=they, 1=we)
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    var parts = args.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        uint uid = ParseHexOrDecUInt(parts[0]);
                        bool weDeclared = parts[1].Trim() == "1";
                        if (uid != 0)
                        {
                            var rel = guild.GetOrCreateRelation(new Serial(uid));
                            if (weDeclared) rel.WeDeclaredWar = true;
                            else rel.TheyDeclaredWar = true;
                        }
                    }
                }
                return true;
            }
            case "DECLAREALLY":
            {
                // Declare (our side of) an alliance with another stone. The alliance
                // only becomes active once the other guild reciprocates (IsAlly).
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0 && ResolveGuildManager?.Invoke()?.DeclareAlliance(Uid, new Serial(uid)) != true)
                        guild.AddAlly(new Serial(uid));
                }
                return true;
            }
            case "DECLAREUNALLY":
            {
                // Withdraw our alliance declaration (mirror of DECLAREPEACE for wars).
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0 && ResolveGuildManager?.Invoke()?.WithdrawAlliance(Uid, new Serial(uid)) != true)
                        guild.RemoveAlly(new Serial(uid));
                }
                return true;
            }
            case "INVITEALLY":
            {
                // INVITEALLY stone_uid, who_declared (0=they, 1=we) — the alliance
                // counterpart of INVITEWAR; records one side of the mutual accept.
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    var parts = args.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        uint uid = ParseHexOrDecUInt(parts[0]);
                        bool weDeclared = parts[1].Trim() == "1";
                        if (uid != 0)
                        {
                            var rel = guild.GetOrCreateRelation(new Serial(uid));
                            if (weDeclared) rel.WeDeclaredAlliance = true;
                            else rel.TheyDeclaredAlliance = true;
                        }
                    }
                }
                return true;
            }
            case "APPLYTOJOIN":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0)
                    {
                        guild.AddRecruit(new Serial(uid));
                        // Source-X marks the candidate with the stone memory
                        // too, so <argo.memoryfindtype.memory_town> dedup and
                        // affiliation checks see town candidates.
                        var recruit = ResolveWorld?.Invoke()?.FindChar(new Serial(uid));
                        recruit?.Memory_AddObjTypes(Uid,
                            ItemType == ItemType.StoneTown ? MemoryType.Town : MemoryType.Guild);
                    }
                }
                return true;
            }
            case "JOINASMEMBER":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0)
                    {
                        guild.JoinAsMember(new Serial(uid));
                        var member = ResolveWorld?.Invoke()?.FindChar(new Serial(uid));
                        if (member != null)
                        {
                            var memType = ItemType == ItemType.StoneTown
                                ? MemoryType.Town
                                : MemoryType.Guild;
                            member.Memory_AddObjTypes(Uid, memType);
                        }
                    }
                }
                return true;
            }
            case "ELECTMASTER":
            {
                ResolveGuild?.Invoke(Uid)?.ElectMaster();
                return true;
            }
            case "CHANGEALIGN":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null && byte.TryParse(args.Trim(), out byte av))
                    guild.Align = (Guild.GuildAlign)av;
                return true;
            }
            case "RESIGN":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0)
                    {
                        guild.RemoveMember(new Serial(uid));
                        // Source-X clears the stone memory on resign, else a
                        // stale town/guild memory blocks re-recruit elsewhere.
                        var resigned = ResolveWorld?.Invoke()?.FindChar(new Serial(uid));
                        var stoneMem = resigned?.Memory_FindObj(Uid);
                        if (stoneMem != null) resigned!.Memory_Delete(stoneMem);
                    }
                }
                return true;
            }
            case "TOGGLEABBREVIATION":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0)
                    {
                        var member = guild.FindMember(new Serial(uid));
                        if (member != null)
                            member.ShowAbbrev = !member.ShowAbbrev;
                    }
                }
                return true;
            }
            case "ALLMEMBERS":
            {
                // ALLMEMBERS priv, command — execute command on matching members
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return true;
                var parts = args.Split(',', 2, StringSplitOptions.TrimEntries);
                int minPriv = parts.Length > 0 && int.TryParse(parts[0], out int parsedPriv) ? parsedPriv : -1;
                string command = parts.Length > 1 ? parts[1] : "";
                if (string.IsNullOrWhiteSpace(command)) return true;

                foreach (var member in guild.Members)
                {
                    if ((byte)member.Priv >= 100) continue;
                    if (minPriv >= 0 && (byte)member.Priv < minPriv) continue;
                    ExecuteGuildMemberCommand?.Invoke(this, member.CharUid, command);
                }
                return true;
            }
            case "ALLGUILDS":
            {
                // ALLGUILDS flags, command — execute command on linked guilds
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return true;
                var parts = args.Split(',', 2, StringSplitOptions.TrimEntries);
                string flags = parts.Length > 0 ? parts[0] : "";
                string command = parts.Length > 1 ? parts[1] : "";
                if (string.IsNullOrWhiteSpace(command)) return true;

                bool includeWar = flags.Contains("war", StringComparison.OrdinalIgnoreCase) ||
                                  flags.Contains("enemy", StringComparison.OrdinalIgnoreCase) ||
                                  flags.Contains("all", StringComparison.OrdinalIgnoreCase);
                bool includeAlly = flags.Contains("ally", StringComparison.OrdinalIgnoreCase) ||
                                   flags.Contains("all", StringComparison.OrdinalIgnoreCase);
                foreach (var (otherStone, rel) in guild.Relations)
                {
                    if ((includeWar && rel.IsEnemy) || (includeAlly && rel.IsAlly))
                        ExecuteGuildRelationCommand?.Invoke(this, otherStone, command);
                }
                return true;
            }
            // Source-X ISV_DELHOUSE / ISV_DELSHIP: unregister a structure by uid
            // (DelMulti removes it regardless of house/ship kind).
            case "DELHOUSE":
            case "DELSHIP":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexOrDecUInt(args.Trim());
                    if (uid != 0) guild.DelMulti(new Serial(uid));
                }
                return true;
            }
        }

        // Spawn commands (IT_SPAWN_CHAR)
        if (SpawnChar != null)
        {
            switch (upper)
            {
                case "SPAWNRESET" or "RESET":
                    SpawnChar.Reset();
                    return true;
                case "SPAWNCLEAR":
                    SpawnChar.KillAll();
                    return true;
                case "START":
                    SpawnChar.Start();
                    OnSpawnStartStop?.Invoke(this, true);
                    return true;
                case "STOP":
                    SpawnChar.Stop();
                    OnSpawnStartStop?.Invoke(this, false);
                    return true;
                case "DELOBJ":
                    if (!string.IsNullOrEmpty(args) && uint.TryParse(args.TrimStart('0'),
                        System.Globalization.NumberStyles.HexNumber, null, out uint duid))
                        SpawnChar.DelObj(new Core.Types.Serial(duid));
                    return true;
            }
        }

        return base.TryExecuteCommand(key, args, source);
    }

    public bool TryFlipDisplay()
    {
        var def = ResolveDefinition();
        if (def == null)
            return false;

        ushort next = 0;
        if (def.FlipId != 0)
            next = def.FlipId;
        else if (def.Flip)
            next = (ushort)(BaseId ^ 1);

        if (next == 0 || next == BaseId)
            return false;

        BaseId = next;
        return true;
    }

    /// <summary>True when this is a loose ground item sitting in a region flagged
    /// REGION_FLAG_NODECAY. Contained items decay with their container, so only
    /// top-level ground items are protected (Source-X CItem decay region check).</summary>
    private bool IsInNoDecayRegion()
    {
        if (ContainedIn.IsValid) return false;
        var region = ResolveWorld?.Invoke()?.FindRegion(Position);
        return region != null && region.IsFlag(Core.Enums.RegionFlag.NoDecay);
    }

    /// <summary>Real milliseconds per light-source burn tick (Source-X
    /// IT_LIGHT_LIT _SetTimeoutS(60): one charge every 60 seconds).</summary>
    public const long LightBurnTickMs = 60_000;

    /// <summary>Source-X IT_LIGHT_LIT tick (CItem.cpp:6271): a lit light source
    /// consumes one charge per minute; at zero it marks itself burned out
    /// (LIGHT_BURNED, can never relight) and reverts to the doused type.
    /// ATTR_MOVE_NEVER/STATIC lights burn forever — no charge, no re-arm.
    /// The running countdown survives restarts via the persisted TIMER.</summary>
    private void OnLightBurnTick()
    {
        if (IsAttr(ObjAttributes.Move_Never) || IsAttr(ObjAttributes.Static))
            return;

        int charges = 20;
        if (TryGetTag("LIGHT_CHARGES", out string? raw) && int.TryParse(raw, out int parsed))
            charges = parsed;
        charges--;
        SetTag("LIGHT_CHARGES", Math.Max(0, charges).ToString());

        if (charges > 0)
        {
            SetTimeout(Environment.TickCount64 + LightBurnTickMs);
            return;
        }

        SetTag("LIGHT_BURNED", "1");
        ExtinguishLight(burnedOut: true);
    }

    /// <summary>Douse a lit light source (auto-burnout or scripted). Keeps
    /// SphereNet's type-flip convention (no graphic pair swap) and plays the
    /// Source-X douse sound (0x4B8 burned out / 0x3BE snuffed).</summary>
    internal void ExtinguishLight(bool burnedOut)
    {
        if (_type != ItemType.LightLit)
            return;
        _type = ItemType.LightOut;
        SetTimeout(0);
        EmitScriptSound(burnedOut ? "0x04B8" : "0x03BE");
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
    }

    public override bool OnTick()
    {
        if (_isDeleted) return false;

        // Item decay: if DecayTime is set and elapsed, mark deleted — unless the
        // item is a loose ground item in a NODECAY region (Source-X
        // REGION_FLAG_NODECAY: things on the ground don't decay here), in which
        // case re-arm the timer so it decays normally once it leaves the region.
        if (DecayTime > 0 && Environment.TickCount64 >= DecayTime)
        {
            if (IsInNoDecayRegion())
            {
                DecayTime = Environment.TickCount64 + World.GameWorld.DefaultDecayTimeMs;
            }
            else
            {
                if (_type == ItemType.Corpse)
                {
                    // The handler scatters contents, or stages a player corpse to
                    // bones (returns false → it re-armed DecayTime; keep the item).
                    bool consumed = OnCorpseDecay?.Invoke(this) ?? true;
                    if (!consumed)
                        return true;
                }
                else
                {
                    // Source-X CItem::_OnTick: decay IS the item timer, so @Timer
                    // has the final say over destruction. RETURN 1 keeps the item
                    // — re-arm a fresh decay window so a script can defer/cancel
                    // the rot (e.g. a quest item that must not vanish). Items with
                    // no @Timer handler return Default/null and decay as before.
                    if (OnTimerExpired?.Invoke(this) == TriggerResult.True)
                    {
                        DecayTime = Environment.TickCount64 + World.GameWorld.DefaultDecayTimeMs;
                        return true;
                    }
                }
                _isDeleted = true;
                SpawnChar?.KillAll();
                return false;
            }
        }

        // TIMER expiry — Source-X CItem::_OnTick parity:
        // Fire @Timer trigger, then fall through to type-specific behavior.
        // Source-X: RETURN 1 = script handled it; Default/0 = no handler,
        // engine continues with OnTickComponent for the item type.
        // Item deletion is driven by DecayTime above, not by @Timer return.
        long timeout = Timeout;
        bool timerFired = false;
        if (timeout > 0 && Environment.TickCount64 >= timeout)
        {
            timerFired = true;
            SetTimeout(0);
            OnTimerExpired?.Invoke(this);

            // Source-X CItem::_OnTick trap state machine: an armed trap relaxes
            // to inactive, an inactive one either re-arms (MOREZ periodic) or
            // returns to the idle IT_TRAP waiting for the next trigger.
            switch (_type)
            {
                case ItemType.TrapActive:
                    SetTrapState(ItemType.TrapInactive, (ushort)More1, MoreP.X);
                    break;
                case ItemType.TrapInactive:
                    if (MoreP.Z != 0)
                        SetTrapState(ItemType.TrapActive, (ushort)More1, MoreP.Y);
                    else
                        SetTrapState(ItemType.Trap, BaseId, -1);
                    break;
                case ItemType.ShipPlank:
                    // Source-X Ship_Plank autoclose: an open plank swings shut
                    // 5 seconds after opening.
                    ClosePlank();
                    break;
                case ItemType.Door:
                case ItemType.DoorOpen:
                case ItemType.DoorLocked:
                case ItemType.Portculis:
                case ItemType.PortLocked:
                    // Source-X Use_Door timer: an opened door swings shut 20
                    // seconds later (locked-while-open still closes).
                    CloseDoor();
                    break;
                case ItemType.Crops:
                case ItemType.Foliage:
                    // Source-X CItem::_OnTick → Plant_OnTick: a crop/foliage grows
                    // one stage each timer tick.
                    PlantOnTick();
                    break;
                case ItemType.LightLit:
                    OnLightBurnTick();
                    break;
                case ItemType.AnimActive:
                    EndAnim();
                    break;
                case ItemType.EqMemoryObj when EquipLayer == Layer.FlagWool:
                    RegrowWool();
                    break;
                case ItemType.BeeHive:
                    // Source-X CItem::_OnTick IT_BEE_HIVE (CItem.cpp:6380): the hive
                    // tops itself back up to five and sleeps another 15 minutes.
                    if (More1 < BeeHiveMaxHoney)
                        More1 += 1;
                    SetTimeout(Environment.TickCount64 + BeeHiveRefillMs);
                    break;
            }
        }

        // Champion altar: the timer is the red-candle DECAY tick, not a spawn
        // schedule — the champion replaces members from kills (Source-X
        // CCChampion::OnTickComponent).
        if (Champion != null)
        {
            if (timerFired)
                Champion.OnTick(Environment.TickCount64);
            return true;
        }

        // Source-X parity: the item's timer IS the spawn timer.
        // When it fires (including manual TIMER=1 via .info),
        // sync SpawnComponent so it spawns on this tick.
        if (timerFired && SpawnChar != null)
            SpawnChar.ForceSpawn();

        long now = Environment.TickCount64;
        SpawnChar?.OnTick(now);
        SpawnItem?.OnTick(now);

        return true;
    }

    /// <summary>Recursive stack consume by base id (Source-X ContentConsume):
    /// walks the container tree eating stacks until <paramref name="need"/>
    /// is satisfied.</summary>
    private static void ConsumeFromContents(Item container, ushort wantedId, ref int need)
    {
        foreach (var child in container.Contents.ToArray())
        {
            if (need <= 0) return;
            if (child.BaseId == wantedId)
            {
                int take = Math.Min(need, child.Amount);
                need -= take;
                if (take >= child.Amount)
                    child.RemoveFromWorld();
                else
                    child.Amount -= (ushort)take;
            }
            else if (child.Contents.Count > 0)
            {
                ConsumeFromContents(child, wantedId, ref need);
            }
        }
    }

    /// <summary>
    /// Source-X CItem::SetTrapState — hop the trap between IT_TRAP /
    /// IT_TRAP_ACTIVE / IT_TRAP_INACTIVE. MORE1 holds the counterpart graphic
    /// (0 = dispid+1); the current graphic is saved back into MORE1 on swap so
    /// the pair ping-pongs. <paramref name="timeSec"/> 0 = 3s default, negative
    /// = no timer.
    /// </summary>
    public void SetTrapState(ItemType state, ushort id, int timeSec)
    {
        if (id == 0)
        {
            id = (ushort)More1;
            if (id == 0) id = (ushort)(BaseId + 1);
        }
        if (timeSec == 0)
            timeSec = 3;

        if (id != BaseId)
        {
            More1 = BaseId; // save the old graphic (Source-X m_itTrap.m_AnimID)
            BaseId = id;
        }
        _type = state;
        SetTimeout(timeSec > 0 ? Environment.TickCount64 + timeSec * 1000L : 0);
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
    }

    /// <summary>
    /// Source-X CItem::Ship_Plank open: an IT_SHIP_SIDE(_LOCKED) becomes an
    /// open IT_SHIP_PLANK for 5 seconds (autoclose timer). The original side
    /// type is remembered in MORE2 (m_itShipPlank.m_wSideType); the graphic
    /// pair ping-pongs through MORE1 like a door.
    /// </summary>
    public bool OpenPlank()
    {
        if (_type == ItemType.ShipPlank)
            return true; // already open
        if (_type is not (ItemType.ShipSide or ItemType.ShipSideLocked))
            return false;

        More2 = (uint)_type; // remember the side type for the close
        // The open-plank art comes from MORE1 when a script staged it, else
        // from the side itemdef's TDATA1 (Source-X: [ITEMDEF 03eb1]
        // TYPE=t_ship_side_locked TDATA1=03ed5). Freshly-placed ships only
        // carry the def data, so without the fallback the type flipped but
        // the graphic never changed — the "ship door won't open" report.
        uint alt = More1;
        if (alt == 0)
            alt = _tdata1 != 0 ? _tdata1 : (ResolveDefinition()?.TData1 ?? 0);
        if (alt is > 0 and <= ushort.MaxValue && (ushort)alt != BaseId)
        {
            More1 = BaseId;
            BaseId = (ushort)alt;
        }
        _type = ItemType.ShipPlank;
        SetTimeout(Environment.TickCount64 + 5000); // autoclose in 5s
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
        return true;
    }

    /// <summary>Source-X Ship_Plank close: restore the stored side type and
    /// graphic, clear the autoclose timer.</summary>
    /// <summary>Source-X Use_Door closing half (the 20s auto-close timer):
    /// flip the open art back, un-shift the hinge, clear the state and push
    /// the visual to observers. Returns false when already closed.</summary>
    public bool CloseDoor()
    {
        // Source-X refuses the whole door path for an item that is not top-level
        // (Use_Door, CItem.cpp:4695). A door picked up while open used to reach this
        // through its own timer, have its container slot read as world coordinates,
        // and get dropped out of the pack onto the map.
        if (ContainedIn.IsValid)
            return false;

        int doorDir = World.DoorHelper.GetDoorDir(DispIdFull);
        bool isOpen = doorDir >= 0
            ? (doorDir & 1) != 0
            : TryGetTag("DOOR_OPEN", out string? s) && s == "1";
        if (!isOpen)
            return false;

        int offset = _type is ItemType.Portculis or ItemType.PortLocked ? 2 : 1;
        ushort newId = (ushort)(DispIdFull - offset);
        if (DispIdOverride != 0)
            TrySetProperty("DISPID", $"0{newId:X}");
        else
            BaseId = newId;
        RemoveTag("DOOR_OPEN");
        // Odd (open) slot → GetDoorShift returns the closing shift.
        World.DoorHelper.MoveDoorLeaf(this, doorDir);
        SetTimeout(0);
        EmitScriptSound("0x00F1");
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
        return true;
    }

    public bool ClosePlank()
    {
        if (_type != ItemType.ShipPlank)
            return false;

        var sideType = More2 is > 0 and <= ushort.MaxValue &&
                       Enum.IsDefined(typeof(ItemType), (ushort)More2)
            ? (ItemType)More2
            : ItemType.ShipSide;
        if (More1 != 0)
        {
            ushort alt = (ushort)More1;
            More1 = BaseId;
            BaseId = alt;
        }
        _type = sideType;
        SetTimeout(0);
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
        return true;
    }

    // ---- Plant growth (Source-X CItemPlant.cpp) ----

    /// <summary>Default plant growth interval. Source-X ties this to the lunar cycle
    /// (GetNextNewMoon); SphereNet uses a fixed interval, overridable per-item via the
    /// MORE1 respawn-seconds field (Source-X m_Respawn_Sec).</summary>
    public static long PlantGrowthDefaultMs = 10 * 60 * 1000; // 10 minutes

    // HUE_RED_DARK — Source-X marks a regrowing (invisible) plot with this hue so
    // staff can still see the plot while ordinary players cannot.
    private const ushort PlantRegrowHue = 0x0021;

    /// <summary>Re-arm the growth timer (Source-X Plant_SetTimer → GetDecayTime),
    /// honoring a per-item MORE1 respawn-seconds override.</summary>
    private void PlantArmTimer()
    {
        long ms = More1 > 0 ? More1 * 1000L : PlantGrowthDefaultMs;
        if (ms <= 0) ms = PlantGrowthDefaultMs;
        SetTimeout(Environment.TickCount64 + ms);
    }

    /// <summary>Arm a freshly placed crop/foliage so it begins growing (Source-X
    /// Use_Seed → Plant_CropReset arms the first timer). Marks it immovable.</summary>
    internal void PlantStartGrowth()
    {
        SetAttr(ObjAttributes.Move_Never);
        PlantArmTimer();
    }

    private static ushort PlantResolveChainId(uint tdata, string? tdataName)
        => ResolveTDataId(tdata, tdataName);

    /// <summary>Resolve a TDATA id/defname pair to a graphic id — shared by
    /// the plant stage chains and the drink empty-container return
    /// (Source-X m_ttDrink.m_ridEmpty, script TDATA1).</summary>
    internal static ushort ResolveTDataId(uint tdata, string? tdataName)
    {
        if (tdata != 0) return (ushort)tdata;
        if (!string.IsNullOrEmpty(tdataName) && ResolveDefName != null)
            return ResolveDefName(tdataName);
        return 0;
    }

    /// <summary>Sphere numeric parse that also accepts the script fixed-point
    /// decimal form: "50.0" → 500 (value in tenths). Plain integers parse raw.</summary>
    internal static uint ParseSphereFixedOrUInt(string value)
    {
        value = value.Trim();
        int dot = value.IndexOf('.');
        if (dot > 0 &&
            uint.TryParse(value[..dot], out uint whole) &&
            dot + 1 < value.Length && char.IsDigit(value[dot + 1]))
        {
            return whole * 10 + (uint)(value[dot + 1] - '0');
        }
        return ParseHexOrDecUInt(value);
    }

    private void PlantSetId(ushort id)
    {
        if (id != 0 && id != BaseId) BaseId = id;
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
    }

    /// <summary>Source-X CItem::Plant_OnTick (CItemPlant.cpp:104): a crop/foliage grows
    /// one stage each timer tick. A crop moved into a container dies. An invisible
    /// regrowing stage reappears. A mature stage (no TDATA2 grow target) drops a fruit
    /// and resets the chain; otherwise it morphs into its TDATA2 grow-id.</summary>
    internal bool PlantOnTick()
    {
        if (ContainedIn.IsValid)
        {
            _isDeleted = true; // not top-level → the plant dies
            return false;
        }
        SetAttr(ObjAttributes.Move_Never);
        PlantArmTimer();

        if (IsAttr(ObjAttributes.Invis))
        {
            // Regrowing hidden stage → reappear (Source-X clears ATTR_INVIS + hue).
            ClearAttr(ObjAttributes.Invis);
            Hue = new Core.Types.Color(0);
            PlantSetId(BaseId);
            return true;
        }

        var def = DefinitionLoader.GetItemDef(BaseId);
        ushort growId = PlantResolveChainId((uint)(def?.TData2 ?? 0), def?.TData2Name);
        if (growId == 0)
        {
            // Fully mature → drop a fruit, then reset to the first stage (hidden regrow).
            PlantDropFruit();
            PlantCropReset();
        }
        else
        {
            PlantSetId(growId);
        }
        return true;
    }

    /// <summary>Source-X Plant_CropReset (CItemPlant.cpp:176): reset to the TDATA1
    /// first-stage id, re-arm the timer, and hide as an invisible dark-red regrow
    /// marker (staff-visible) until the next tick reveals it.</summary>
    internal void PlantCropReset()
    {
        var def = DefinitionLoader.GetItemDef(BaseId);
        ushort resetId = PlantResolveChainId((uint)(def?.TData1 ?? 0), def?.TData1Name);
        if (resetId != 0 && resetId != BaseId) BaseId = resetId;
        Hue = new Core.Types.Color(PlantRegrowHue);
        SetAttr(ObjAttributes.Invis);
        PlantArmTimer();
        MarkDirty((DirtyFlag)0xFFFFFFFF);
        OnVisualUpdate?.Invoke(this);
    }

    private void PlantDropFruit()
    {
        var def = DefinitionLoader.GetItemDef(BaseId);
        // MORE2 fruit override (Source-X m_ridFruitOverride) else TDATA3.
        ushort fruitId = More2 != 0 ? (ushort)More2
            : PlantResolveChainId((uint)(def?.TData3 ?? 0), def?.TData3Name);
        if (fruitId == 0) return;
        var world = ResolveWorld?.Invoke();
        if (world == null) return;
        var fruit = world.CreateItem();
        fruit.BaseId = fruitId;
        // The produce keeps the type its own definition gives it. Source-X builds it
        // with CreateScript (CItemPlant.cpp:65) and does not turn everything a plant
        // yields into food - cotton and hay are not edible.
        world.PlaceItemWithDecay(fruit, Position);
    }

    /// <summary>
    /// Source-X CItem::Use_Trap — the trap was sprung (dclick or step). Arms an
    /// idle trap (graphic swap + MOREX-second active window) and returns the
    /// base damage (MORE2, default 2).
    /// </summary>
    public int UseTrap()
    {
        if (ItemType == ItemType.Trap)
            SetTrapState(ItemType.TrapActive, (ushort)More1, MoreP.X);

        if (More2 == 0) More2 = 2;
        return (int)More2;
    }

    /// <summary>
    /// Initialize the SpawnComponent for IT_SPAWN_CHAR / IT_SPAWN_ITEM items.
    /// Resolves MORE1 as either a spawn group defname or a single chardef/itemdef ID.
    /// Called from WorldLoader/LegacySphereImporter after item load, and at
    /// runtime when TYPE is set to a spawn type via script.
    /// </summary>
    public void InitializeSpawnComponent(World.GameWorld world, ResourceHolder resources, long preservedTimeoutMs = 0)
    {
        // Gate on the VIRTUALIZED type: Sphere saves omit TYPE when it equals
        // the base itemdef, so a worldgem's raw _type is Normal and only the
        // ItemType getter sees SpawnChar (def inheritance). Gating on _type
        // silently no-opped for every def-typed spawner.
        var effType = ItemType;
        if (effType is ItemType.SpawnChar or ItemType.SpawnChampion)
        {
            if (SpawnChar == null)
                SpawnChar = new SpawnComponent(this, world);

            // Source-X IT_SPAWN_CHAMPION rides the char-spawn machinery but
            // ignores the amount cap and never pauses (CCSpawn special case).
            SpawnChar.IsChampion = effType == ItemType.SpawnChampion;
            if (effType == ItemType.SpawnChampion)
            {
                // Champion altar: MORE1 links a [CHAMPION x] def, not a
                // chardef/spawn group — the champion component owns the wave
                // composition and drives all spawning through SpawnChar.
                Champion ??= new ChampionComponent(this, world);
                string champDef = TryGetTag("MORE1_DEFNAME", out string? champTag) &&
                    !string.IsNullOrWhiteSpace(champTag)
                    ? champTag.Trim()
                    : "";
                if (champDef.Length == 0 && _more1 != 0)
                {
                    var champLink = resources.GetResource(Core.Enums.ResType.Champion, (int)_more1);
                    champDef = champLink?.DefName ?? "";
                }
                if (champDef.Length > 0)
                    Champion.InitFromDef(resources, champDef);
            }
            else
            {
                SpawnChar.SetFromMore1(_more1, resources);
                // Legacy saves write MORE1 as a raw defname (MORE1=c_spider_giant).
                // The numeric setter can't always resolve it (chardef hashes are
                // 24-bit; the ItemDef-gated resolver returns 0) and parks it in
                // the MORE1_DEFNAME tag — consume that here, or every imported
                // NPC spawner comes up empty and never spawns.
                if (_more1 == 0 && TryGetTag("MORE1_DEFNAME", out string? spawnDef) &&
                    !string.IsNullOrWhiteSpace(spawnDef))
                    SpawnChar.SetFromDefName(spawnDef, resources);
            }
            SpawnChar.ApplyMoreP();

            if (_amount > 1)
                SpawnChar.MaxCount = _amount;
            else if (_more2 != 0)
            {
                ushort maxCount = (ushort)(_more2 & 0xFFFF);
                if (maxCount > 0)
                    SpawnChar.MaxCount = maxCount;
            }

            SpawnChar.ResetTimer(preservedTimeoutMs);
            RelinkSpawnedChildrenFromTag(world);
        }
        else if (effType == ItemType.SpawnItem)
        {
            if (SpawnItem == null)
                SpawnItem = new ItemSpawnComponent(this, world);

            if (_more1 != 0)
                SpawnItem.ItemDefId = (int)_more1; // full index — named itemdefs hash above 0xFFFF
            else if (TryGetTag("MORE1_DEFNAME", out string? itemDefName) &&
                     !string.IsNullOrWhiteSpace(itemDefName))
            {
                // Same legacy-save defname fallback as the char spawner above.
                var rid = resources.ResolveDefName(itemDefName.Trim());
                if (rid.IsValid && rid.Type == Core.Enums.ResType.ItemDef)
                    SpawnItem.ItemDefId = rid.Index; // full 24-bit index, not (ushort)
            }

            if (_amount > 1)
                SpawnItem.MaxCount = _amount;

            SpawnItem.ResetTimer(preservedTimeoutMs);
        }
    }

    /// <summary>Restore a char spawner's live-children list from its saved ADDOBJ
    /// entries. Source-X CItem/CCSpawn rebuilds GetCurrentSpawned() from ADDOBJ on
    /// load (CCSpawn::AddObj), independent of the SPAWNID tag. Without it a native
    /// MORE1/AMOUNT spawner forgets its NPCs on reload, sees CurrentCount 0 &lt;
    /// AMOUNT, and respawns its whole quota every restart — the population grows
    /// without bound (one worldgem yielding many NPCs). Idempotent (RegisterExisting
    /// de-dupes) and never alters MaxCount: the cap stays AMOUNT and any excess
    /// carried in from an over-accumulated save drains off by attrition, matching
    /// Source-X leaving MORE2/current-spawned unmodified on load.</summary>
    private void RelinkSpawnedChildrenFromTag(World.GameWorld world)
    {
        if (SpawnChar == null) return;
        string? addObj = Tags.Get("ADDOBJ");
        if (string.IsNullOrEmpty(addObj)) return;

        int spawnRange = SpawnChar.SpawnRange;
        foreach (string tok in addObj.Split(',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseSpawnUid(tok, out uint uid)) continue;
            var ch = world.FindChar(new Serial(uid));
            if (ch == null || ch.IsDead || ch.IsDeleted) continue;

            SpawnChar.RegisterExisting(new Serial(uid));
            if (ch.Home.X == 0 && ch.Home.Y == 0)
                ch.Home = Position;
            if (ch.HomeDist == Character.UnlimitedHomeDistance)
                ch.HomeDist = (short)spawnRange;
            if (!ch.TryGetTag("SPAWNITEM", out _))
                ch.SetTag("SPAWNITEM", $"0{Uid.Value:x8}");
            if (!ch.IsStatFlag(StatFlag.Spawned))
                ch.SetStatFlag(StatFlag.Spawned);
            if (ch.NpcBrain == NpcBrainType.None)
            {
                var cdef = DefinitionLoader.GetCharDef(ch.CharDefIndex);
                ch.NpcBrain = (cdef != null && cdef.NpcBrain != NpcBrainType.None)
                    ? cdef.NpcBrain
                    : NpcBrainType.Monster;
            }
        }
    }

    /// <summary>Parse an ADDOBJ serial token: "0x1234", leading-0 hex ("0400211c9",
    /// how the saver writes UIDs), or plain decimal.</summary>
    private static bool TryParseSpawnUid(string val, out uint result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (val.Length > 1 && val[0] == '0' && !val.Contains('.'))
            return uint.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);
        return uint.TryParse(val, out result);
    }

    // --- Helper methods ---

    private int GetDeepContentCount(int maxDepth = 16)
    {
        if (maxDepth <= 0) return 0;
        int count = _contents.Count;
        foreach (var child in _contents)
            count += child.GetDeepContentCount(maxDepth - 1);
        return count;
    }

    private static ushort ParseHexId(string arg)
    {
        var s = arg.Trim();
        if (s.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        else if (s.Length > 1 && s[0] == '0' && !s.All(char.IsDigit))
            s = s[1..]; // Sphere-style leading 0 hex
        if (ushort.TryParse(s, NumberStyles.HexNumber, null, out ushort h))
            return h;
        if (ushort.TryParse(arg.Trim(), out ushort d))
            return d;
        return 0;
    }

    internal static ItemType ParseItemType(string arg)
    {
        var s = arg.Trim();
        if (s.StartsWith("T_", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        s = s.Replace("_", "");
        if (Enum.TryParse<ItemType>(s, ignoreCase: true, out var it))
            return it;
        if (ushort.TryParse(arg.Trim(), out ushort n))
            return (ItemType)n;
        return ItemType.Invalid;
    }

    private static string FormatItemType(ItemType t)
    {
        if (t == ItemType.Invalid || t == ItemType.Normal)
            return ((ushort)t).ToString();
        var name = t.ToString();
        var sb = new System.Text.StringBuilder("t_", name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private string FormatMore1()
    {
        if (_more1 == 0) return "0";
        // Use the resolved type, not the raw _type field: a legacy-loaded item
        // leaves _type at Normal and gets its type from the ITEMDEF (which now
        // resolves even for synthetic-index defs), so a spawn item's MORE1 shows
        // its <defname> instead of a bare number.
        var effectiveType = _type != ItemType.Normal
            ? _type
            : ResolveDefinition()?.Type ?? ItemType.Normal;
        if (effectiveType is ItemType.SpawnChar or ItemType.SpawnChampion)
        {
            if (SpawnChar?.SpawnGroup != null && !string.IsNullOrEmpty(SpawnChar.SpawnGroup.DefName))
                return SpawnChar.SpawnGroup.DefName;
            // Full index — non-numeric-header chardefs (c_alchemist) hash above
            // 0xFFFF; masking would miss the def and save a bare number instead
            // of the defname, breaking the classic MORE1=<defname> round-trip.
            var cdef = Definitions.DefinitionLoader.GetCharDef((int)_more1);
            if (cdef != null && !string.IsNullOrEmpty(cdef.DefName))
                return cdef.DefName;
        }
        return $"0{_more1:X}";
    }

    private string FormatBaseId()
    {
        var idef = ResolveDefinition();
        if (idef != null && !string.IsNullOrEmpty(idef.DefName))
            return idef.DefName;
        return $"0{BaseId:X}";
    }

    private string FormatDispId()
    {
        ushort id = DispIdFull;
        var idef = Definitions.DefinitionLoader.GetItemDef(id);
        if (idef != null && !string.IsNullOrEmpty(idef.DefName))
            return idef.DefName;
        return $"0{id:X}";
    }

    private Item? FindContentByBaseId(ushort baseId)
    {
        foreach (var item in _contents)
            if (item.BaseId == baseId) return item;
        return null;
    }

    /// <summary>Resolve a RESOURCES/SKILLMAKE resource-qty list read from its raw
    /// "&lt;qty&gt; &lt;resource&gt;[, ...]" string (Source-X CBaseBaseDef OBC_RESOURCES).
    /// Forms: bare = whole list; .COUNT = entry count; .&lt;n&gt; = the n-th entry
    /// (1-based); .&lt;n&gt;.KEY = its resource token; .&lt;n&gt;.VAL = its quantity.</summary>
    private static bool TryResolveResourceList(string raw, string upper, string keyword, out string value)
    {
        value = "";
        string rest = upper.Length > keyword.Length ? upper[(keyword.Length + 1)..] : "";
        if (rest.Length == 0)
        {
            value = raw; // whole comma-separated list (ARGV counts the entries)
            return true;
        }

        var entries = string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (rest == "COUNT")
        {
            value = entries.Length.ToString();
            return true;
        }

        int subDot = rest.IndexOf('.');
        string idxPart = subDot < 0 ? rest : rest[..subDot];
        string field = subDot < 0 ? "" : rest[(subDot + 1)..];
        if (!int.TryParse(idxPart, out int idx) || idx < 1 || idx > entries.Length)
        {
            value = "0";
            return true;
        }

        string entry = entries[idx - 1]; // Source-X WriteKeys index is 1-based
        int sp = entry.IndexOfAny([' ', '\t']);
        bool hasQty = sp > 0 && ulong.TryParse(entry.AsSpan(0, sp), out _);
        string qty = hasQty ? entry[..sp].Trim() : "1";
        string resKey = hasQty ? entry[(sp + 1)..].Trim() : entry.Trim();

        value = field switch
        {
            "KEY" => resKey,
            "VAL" => qty,
            _ => entry
        };
        return true;
    }

    private Item? FindContentByType(ItemType type)
    {
        if (type == ItemType.Invalid) return null;
        foreach (var item in _contents)
            if (item._type == type) return item;
        return null;
    }

    private int GetResCount(ushort baseId)
    {
        int total = 0;
        foreach (var item in _contents)
        {
            if (item.BaseId == baseId) total += item._amount;
            total += item.GetResCount(baseId); // recurse into subcontainers
        }
        return total;
    }

    private bool EvalResTest(string[] parts)
    {
        // pairs: amount id amount id ...
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], out int need)) return false;
            ushort id = ParseHexId(parts[i + 1]);
            if (GetResCount(id) < need) return false;
        }
        return true;
    }

    private int CountTagsWithPrefix(string prefix)
    {
        int count = 0;
        foreach (var (k, _) in Tags.GetAll())
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    private static int CountBits(ulong mask)
    {
        int count = 0;
        while (mask != 0)
        {
            count += (int)(mask & 1);
            mask >>= 1;
        }
        return count;
    }

    private bool TryGetHouseProperty(string subKey, out string value)
    {
        value = "";
        var house = ResolveHouse?.Invoke(Uid);
        if (house == null)
        {
            value = "0";
            return true;
        }

        switch (subKey)
        {
            case "OWNER": value = FormatSerial(house.Owner); return true;
            case "TYPE": value = ((byte)house.Type).ToString(); return true;
            case "GUILDSTONE": value = FormatSerial(house.GuildStone); return true;
            case "STORAGE":
            case "BASESTORAGE": value = house.BaseStorage.ToString(); return true;
            case "MAXSTORAGE": value = house.MaxStorage.ToString(); return true;
            case "CURRENTSTORAGE": value = house.CurrentStorage.ToString(); return true;
            case "INCREASEDSTORAGE": value = house.IncreasedStorage.ToString(); return true;
            case "LOCKDOWNSPERCENT": value = house.LockdownsPercent.ToString(); return true;
            case "MAXLOCKDOWNS": value = house.MaxLockdowns.ToString(); return true;
            case "MAXSECURE": value = house.MaxSecure.ToString(); return true;
            case "BASEVENDORS": value = house.BaseVendors.ToString(); return true;
            case "MAXVENDORS": value = house.MaxVendors.ToString(); return true;
            case "DECAYSTAGE":
            case "DECAY_STAGE": value = ((byte)house.DecayStage).ToString(); return true;
        }

        if (TryGetHouseSerialCollection(subKey, "COOWNER", "COOWNERS", house.CoOwners, out value) ||
            TryGetHouseSerialCollection(subKey, "FRIEND", "FRIENDS", house.Friends, out value) ||
            TryGetHouseSerialCollection(subKey, "BAN", "BANS", house.Bans, out value) ||
            // The access list was maintained by ADDACCESS/DELACCESS but had no
            // readout at all, so a script could never count or enumerate it.
            TryGetHouseSerialCollection(subKey, "ACCESS", "ACCESSES", house.AccessList, out value) ||
            TryGetHouseSerialCollection(subKey, "LOCKDOWN", "LOCKDOWNS", house.Lockdowns, out value) ||
            TryGetHouseSerialCollection(subKey, "SECURE", "SECURE", house.SecureContainers, out value) ||
            TryGetHouseSerialCollection(subKey, "COMPONENT", "COMPONENTS", house.Components, out value) ||
            TryGetHouseSerialCollection(subKey, "VENDOR", "VENDORS", house.Vendors, out value))
        {
            return true;
        }

        if (TryReadHouseSerialPredicate(subKey, "PRIV.", uid => ((byte)house.GetPriv(uid)).ToString(), out value) ||
            TryReadHouseSerialPredicate(subKey, "ISOWNER.", uid => uid == house.Owner ? "1" : "0", out value) ||
            TryReadHouseSerialPredicate(subKey, "CANACCESS.", uid => house.CanAccess(uid) ? "1" : "0", out value) ||
            TryReadHouseSerialPredicate(subKey, "CANLOCKDOWN.", uid => house.CanLockdown(uid) ? "1" : "0", out value) ||
            TryReadHouseSerialPredicate(subKey, "ISLOCKEDDOWN.", uid => house.IsLockedDown(uid) ? "1" : "0", out value) ||
            TryReadHouseSerialPredicate(subKey, "ISSECURED.", uid => house.IsSecured(uid) ? "1" : "0", out value))
        {
            return true;
        }

        value = "0";
        return true;
    }

    private static bool TryGetHouseSerialCollection(string subKey, string singular, string plural,
        IReadOnlyCollection<Serial> serials, out string value)
    {
        value = "";
        if (subKey.Equals(singular, StringComparison.Ordinal) ||
            subKey.Equals(plural, StringComparison.Ordinal))
        {
            value = serials.Count.ToString();
            return true;
        }

        string singularPrefix = singular + ".";
        string pluralPrefix = plural + ".";
        string? idxStr = null;
        if (subKey.StartsWith(singularPrefix, StringComparison.Ordinal))
            idxStr = subKey[singularPrefix.Length..];
        else if (subKey.StartsWith(pluralPrefix, StringComparison.Ordinal))
            idxStr = subKey[pluralPrefix.Length..];

        if (idxStr == null)
            return false;

        if (!int.TryParse(idxStr, out int index) || index < 0)
        {
            value = "0";
            return true;
        }

        value = index < serials.Count ? FormatSerial(serials.ElementAt(index)) : "0";
        return true;
    }

    private static bool TryReadHouseSerialPredicate(string subKey, string prefix,
        Func<Serial, string> predicate, out string value)
    {
        value = "";
        if (!subKey.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        uint raw = ParseSphereSerial(subKey[prefix.Length..]);
        value = raw == 0 ? "0" : predicate(new Serial(raw));
        return true;
    }

    private static uint ParseSphereSerial(string value)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (s.Length > 1 && s[0] == '0')
            return uint.TryParse(s, NumberStyles.HexNumber, null, out uint hex) ? hex : 0;
        return uint.TryParse(s, out uint dec) ? dec : 0;
    }

    private static string FormatSerial(Serial uid) =>
        uid.IsValid && uid.Value != 0 ? $"0{uid.Value:X8}" : "0";

    // --- Guild stone relation properties ---

    private static bool TryParseRelationKey(string upper, out string propName, out Serial otherUid)
    {
        propName = "";
        otherUid = Serial.Invalid;
        // Format: PROPNAME.0uid or PROPNAME.uid
        int dot = upper.IndexOf('.');
        if (dot < 0) return false;
        propName = upper[..dot];
        string uidStr = upper[(dot + 1)..];
        uint val = ParseHexOrDecUInt(uidStr);
        if (val == 0) return false;
        otherUid = new Serial(val);
        return true;
    }

    private bool TryGetGuildRelationProperty(string upper, out string value)
    {
        value = "";
        if (!TryParseRelationKey(upper, out string prop, out Serial otherUid))
            return false;

        switch (prop)
        {
            case "WEWAR":
            case "THEYWAR":
            case "WEALLIANCE":
            case "THEYALLIANCE":
            case "ISENEMY":
            case "ISALLY":
                break;
            default:
                return false;
        }

        var guild = ResolveGuild?.Invoke(Uid);
        if (guild == null) { value = "0"; return true; }
        var rel = guild.GetRelation(otherUid);

        value = prop switch
        {
            "WEWAR" => (rel?.WeDeclaredWar ?? false) ? "1" : "0",
            "THEYWAR" => (rel?.TheyDeclaredWar ?? false) ? "1" : "0",
            "WEALLIANCE" => (rel?.WeDeclaredAlliance ?? false) ? "1" : "0",
            "THEYALLIANCE" => (rel?.TheyDeclaredAlliance ?? false) ? "1" : "0",
            "ISENEMY" => (rel?.IsEnemy ?? false) ? "1" : "0",
            "ISALLY" => (rel?.IsAlly ?? false) ? "1" : "0",
            _ => "0"
        };
        return true;
    }

    private bool TryGetGuildStoneProperty(string upper, out string value)
    {
        value = "";
        switch (upper)
        {
            case "ABBREV":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                value = guild?.Abbreviation ?? "";
                return guild != null;
            }
            case "ABBREVIATIONTOGGLE":
            {
                // Returns defname based on whether SRC has abbreviation showing
                // In practice returns "STONECONFIG_VARIOUSNAME_SHOW" or "_HIDE"
                value = "STONECONFIG_VARIOUSNAME_SHOW"; // default
                return ResolveGuild?.Invoke(Uid) != null;
            }
            case "ALIGN":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                value = guild != null ? ((byte)guild.Align).ToString() : "0";
                return guild != null;
            }
            case "ALIGNTYPE":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                value = guild.Align switch
                {
                    Guild.GuildAlign.Order => "Order",
                    Guild.GuildAlign.Chaos => "Chaos",
                    _ => "Standard"
                };
                return true;
            }
            case "GUILD.COUNT":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                value = guild?.Relations.Count.ToString() ?? "0";
                return guild != null;
            }
            case "MASTER":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                var master = guild.GetMaster();
                if (master == null) { value = ""; return true; }
                var world = ResolveWorld?.Invoke();
                var ch = world?.FindChar(master.CharUid);
                value = ch?.Name ?? "";
                return true;
            }
            case "MASTERUID":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                var master = guild.GetMaster();
                value = master != null ? $"0{master.CharUid.Value:X}" : "0";
                return true;
            }
            case "MASTERTITLE":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                var master = guild.GetMaster();
                value = master?.Title ?? "";
                return true;
            }
            case "MASTERGENDERTITLE":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                var master = guild.GetMaster();
                if (master == null) { value = ""; return true; }
                var world = ResolveWorld?.Invoke();
                var ch = world?.FindChar(master.CharUid);
                // Body 0x0190 = male, 0x0191 = female
                value = ch != null && ch.BodyId == 0x0191 ? "Lady" : "Lord";
                return true;
            }
            case "LOYALTO":
            {
                // Returns name of the member SRC is loyal to — needs SRC context
                // Fallback: return empty (actual SRC-dependent resolution in script engine)
                value = "";
                return ResolveGuild?.Invoke(Uid) != null;
            }
            case "WEBPAGE":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                value = guild?.WebUrl ?? "";
                return guild != null;
            }
            case "HOUSES":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                value = guild.HouseCount.ToString();
                return true;
            }
            case "SHIPS":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                value = guild.ShipCount.ToString();
                return true;
            }
            case "MAXHOUSES":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                value = guild.MaxHouses.ToString();
                return true;
            }
            case "MAXSHIPS":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                value = guild.MaxShips.ToString();
                return true;
            }
        }

        // CHARTER.n — nth line of guild charter (zero-based)
        if (upper.StartsWith("CHARTER.", StringComparison.Ordinal))
        {
            var guild = ResolveGuild?.Invoke(Uid);
            if (guild == null) { value = ""; return true; }
            if (int.TryParse(upper[8..], out int lineIdx))
            {
                var lines = guild.Charter.Split('\n');
                value = lineIdx >= 0 && lineIdx < lines.Length ? lines[lineIdx] : "";
            }
            return true;
        }

        // MEMBER.COUNT [priv] — number of members with at least given priv
        if (upper.StartsWith("MEMBER.COUNT", StringComparison.Ordinal))
        {
            var guild = ResolveGuild?.Invoke(Uid);
            if (guild == null) { value = "0"; return true; }
            int minPriv = -1; // default: all
            var remainder = upper[12..].TrimStart(' ', '.');
            if (remainder.Length > 0 && int.TryParse(remainder, out int mp))
                minPriv = mp;
            value = guild.GetMemberCount(minPriv).ToString();
            return true;
        }

        return false;
    }

    private bool TrySetGuildStoneProperty(string upper, string value)
    {
        switch (upper)
        {
            case "ABBREV":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                guild.Abbreviation = value;
                return true;
            }
            case "ALIGN":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                if (byte.TryParse(value, out byte av))
                    guild.Align = (Guild.GuildAlign)av;
                return true;
            }
            case "MASTERUID":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                uint uid = ParseHexOrDecUInt(value);
                if (uid != 0) guild.SetMaster(new Serial(uid));
                return true;
            }
            case "WEBPAGE":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                guild.WebUrl = value;
                return true;
            }
            // Source-X STC_ADDHOUSE / STC_ADDSHIP: register a multi to the guild.
            case "ADDHOUSE":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                uint uid = ParseHexOrDecUInt(value);
                if (uid != 0) guild.AddHouse(new Serial(uid));
                return true;
            }
            case "ADDSHIP":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                uint uid = ParseHexOrDecUInt(value);
                if (uid != 0) guild.AddShip(new Serial(uid));
                return true;
            }
            case "MAXHOUSES":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                if (int.TryParse(value.Trim(), out int mh) && mh >= 0) guild.MaxHouses = mh;
                return true;
            }
            case "MAXSHIPS":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild == null) return false;
                if (int.TryParse(value.Trim(), out int ms) && ms >= 0) guild.MaxShips = ms;
                return true;
            }
        }

        // CHARTER.n — set nth line of guild charter (zero-based)
        if (upper.StartsWith("CHARTER.", StringComparison.Ordinal))
        {
            var guild = ResolveGuild?.Invoke(Uid);
            if (guild == null) return false;
            if (int.TryParse(upper[8..], out int lineIdx) && lineIdx >= 0)
            {
                var lines = guild.Charter.Split('\n').ToList();
                while (lines.Count <= lineIdx) lines.Add("");
                lines[lineIdx] = value;
                guild.Charter = string.Join('\n', lines);
            }
            return true;
        }

        return false;
    }

    private bool TryGetGuildStoneReference(string upper, out string value)
    {
        value = "";

        // MEMBER.n — nth member character UID (zero-based)
        if (upper.StartsWith("MEMBER.", StringComparison.Ordinal) &&
            !upper.StartsWith("MEMBERFROMUID.", StringComparison.Ordinal))
        {
            var guild = ResolveGuild?.Invoke(Uid);
            if (guild == null) { value = "0"; return true; }
            if (int.TryParse(upper[7..], out int idx) && idx >= 0 && idx < guild.MemberCount)
                value = $"0{guild.Members[idx].CharUid.Value:X}";
            else
                value = "0";
            return true;
        }

        // MEMBERFROMUID.character_uid — member by character UID
        if (upper.StartsWith("MEMBERFROMUID.", StringComparison.Ordinal))
        {
            var guild = ResolveGuild?.Invoke(Uid);
            if (guild == null) { value = "0"; return true; }
            uint uid = ParseHexOrDecUInt(upper[14..]);
            if (uid != 0)
            {
                var member = guild.FindMember(new Serial(uid));
                value = member != null ? $"0{member.CharUid.Value:X}" : "0";
            }
            else
                value = "0";
            return true;
        }

        // GUILD.n — nth linked guild/town stone UID (zero-based, from relations)
        if (upper.StartsWith("GUILD.", StringComparison.Ordinal) &&
            !upper.StartsWith("GUILDFROMUID.", StringComparison.Ordinal))
        {
            var guild = ResolveGuild?.Invoke(Uid);
            if (guild == null) { value = "0"; return true; }
            var relKeys = guild.Relations.Keys.ToList();
            if (int.TryParse(upper[6..], out int idx) && idx >= 0 && idx < relKeys.Count)
                value = $"0{relKeys[idx].Value:X}";
            else
                value = "0";
            return true;
        }

        // GUILDFROMUID.stone_uid — linked guild by stone UID
        if (upper.StartsWith("GUILDFROMUID.", StringComparison.Ordinal))
        {
            var guild = ResolveGuild?.Invoke(Uid);
            if (guild == null) { value = "0"; return true; }
            uint uid = ParseHexOrDecUInt(upper[12..]);
            if (uid != 0)
            {
                var rel = guild.GetRelation(new Serial(uid));
                value = rel != null ? $"0{rel.OtherStoneUid.Value:X}" : "0";
            }
            else
                value = "0";
            return true;
        }

        return false;
    }

    private bool TrySetGuildRelationProperty(string upper, string value)
    {
        if (!TryParseRelationKey(upper, out string prop, out Serial otherUid))
            return false;

        switch (prop)
        {
            case "WEWAR":
            case "THEYWAR":
            case "WEALLIANCE":
            case "THEYALLIANCE":
                break;
            default:
                return false;
        }

        var guild = ResolveGuild?.Invoke(Uid);
        if (guild == null) return false;

        bool flag = value != "0" && !string.IsNullOrEmpty(value);
        var rel = guild.GetOrCreateRelation(otherUid);

        switch (prop)
        {
            case "WEWAR": rel.WeDeclaredWar = flag; break;
            case "THEYWAR": rel.TheyDeclaredWar = flag; break;
            case "WEALLIANCE": rel.WeDeclaredAlliance = flag; break;
            case "THEYALLIANCE": rel.TheyDeclaredAlliance = flag; break;
        }
        return true;
    }

    // --- Container position randomization (Source-X GetRandContainerLoc parity) ---

    [ThreadStatic]
    private static Random? _containerRng;

    private static void AssignRandomContainerPosition(Item container, Item child)
    {
        var (minX, minY, maxX, maxY) = GetContainerBounds(container);
        var rng = _containerRng ??= new();
        child.Position = new Core.Types.Point3D(
            (short)(minX + rng.Next(maxX - minX)),
            (short)(minY + rng.Next(maxY - minY)));
    }

    private static (int minX, int minY, int maxX, int maxY) GetContainerBounds(Item container)
    {
        var idef = container.ResolveDefinition();

        if (idef != null && idef.TData3 != 0 && idef.TData4 != 0)
        {
            int cMinX = (int)(idef.TData3 >> 16);
            int cMinY = (int)(idef.TData3 & 0xFFFF);
            int cMaxX = (int)(idef.TData4 >> 16);
            int cMaxY = (int)(idef.TData4 & 0xFFFF);
            if (cMaxX > cMinX && cMaxY > cMinY)
                return (cMinX, cMinY, cMaxX, cMaxY);
        }

        ushort gumpId = 0x003C;
        if (idef != null && idef.TData2 != 0)
            gumpId = (ushort)(idef.TData2 & 0xFFFF);
        else
            gumpId = FallbackContainerGump(container.BaseId);

        return GumpBounds(gumpId);
    }

    private static ushort FallbackContainerGump(ushort baseId) => baseId switch
    {
        0xE7C or 0x9AB => 0x4A,
        0xE76 or 0x2256 or 0x2257 => 0x3D,
        0xE77 or 0xE7F => 0x3E,
        0xE7A or 0x24D5 or 0x24D6 or 0x24D9 or 0x24DA => 0x3F,
        0xE40 or 0xE41 => 0x42,
        0xE7D or 0x9AA => 0x43,
        0xE7E or 0x9A9 or 0xE3C or 0xE3D or 0xE3E or 0xE3F => 0x44,
        0xE42 or 0xE43 => 0x49,
        0xE80 or 0x9A8 => 0x4B,
        _ => 0x3C,
    };

    private static (int, int, int, int) GumpBounds(ushort gumpId) => gumpId switch
    {
        0x07 => (30, 30, 270, 170),
        0x09 => (20, 85, 124, 196),
        0x3C => (44, 65, 186, 159),
        0x3D => (29, 34, 137, 128),
        0x3E => (33, 36, 142, 148),
        0x3F => (19, 47, 182, 123),
        0x40 => (16, 38, 152, 125),
        0x41 => (35, 38, 145, 116),
        0x42 => (18, 105, 162, 178),
        0x43 => (16, 51, 184, 124),
        0x44 => (20, 10, 170, 100),
        0x47 => (16, 10, 148, 138),
        0x48 => (16, 10, 154, 94),
        0x49 => (18, 105, 162, 178),
        0x4A => (18, 105, 162, 178),
        0x4B => (16, 51, 184, 124),
        0x4C => (46, 74, 196, 184),
        0x4D => (76, 12, 110, 68),
        0x4E => (24, 96, 96, 152),
        0x4F => (24, 96, 96, 152),
        0x51 => (16, 10, 154, 94),
        0x102 => (35, 10, 190, 95),
        0x103 => (41, 21, 186, 111),
        0x104 or 0x105 or 0x106 or 0x107 => (10, 10, 170, 115),
        0x108 => (10, 30, 170, 145),
        0x109 or 0x10A or 0x10B or 0x10C or 0x10D or 0x10E => (10, 10, 170, 115),
        0x116 => (44, 29, 128, 103),
        0x11A => (19, 61, 119, 155),
        0x11B => (23, 51, 163, 151),
        0x11C => (16, 51, 156, 166),
        0x11D => (25, 51, 165, 166),
        0x11E => (16, 51, 156, 151),
        0x11F => (21, 51, 161, 151),
        0x120 => (56, 30, 158, 104),
        0x121 => (77, 44, 161, 105),
        0x122 => (16, 51, 156, 166),
        0x123 => (35, 13, 112, 165),
        _ => (44, 65, 186, 159),
    };
}
