using System.Globalization;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Definitions;
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
    public static Func<Ships.ShipEngine?>? ResolveShipEngine;
    public new static Func<World.GameWorld>? ResolveWorld;
    public static Func<Serial, Guild.GuildDef?>? ResolveGuild;
    /// <summary>Invoked by MULTICREATE when a script registers a multi
    /// as a house at runtime. Program.cs wires this to
    /// HousingEngine.RegisterExistingMulti so the region tracker knows
    /// about the new house without waiting for the next save cycle.</summary>
    public static Action<Item>? OnHouseRegister;

    /// <summary>Invoked when a corpse is about to be destroyed by the
    /// per-item decay timer. Program.cs wires this to a handler that
    /// drops the corpse's remaining contents to the ground at its
    /// position. Done via a callback instead of a direct GameWorld
    /// reference so Item.cs stays free of world / placement plumbing.</summary>
    public static Action<Item>? OnCorpseDecay;

    private ItemType _type;
    private ushort _amount = 1;
    private Serial _containedIn = Serial.Invalid;
    private byte _containerGridIndex;
    private readonly List<Item> _contents = [];
    private bool _isDeleted;

    // Faz 1: Core item fields
    private uint _more1;
    private uint _more2;
    private Point3D _moreP;
    private Serial _link = Serial.Invalid;
    private int _price;
    private ushort _quality = 50;

    // TDATA instance overrides (default 0)
    private uint _tdata1;
    private uint _tdata2;
    private uint _tdata3;
    private uint _tdata4;

    public uint TData1 { get => _tdata1; set => _tdata1 = value; }
    public uint TData2 { get => _tdata2; set => _tdata2 = value; }
    public uint TData3 { get => _tdata3; set => _tdata3 = value; }
    public uint TData4 { get => _tdata4; set => _tdata4 = value; }

    // Runtime EVENTS list (from ITEMDEF + dynamically added)
    private readonly List<ResourceId> _events = [];

    /// <summary>Runtime EVENTS list. Populated from ITEMDEF Events + dynamically added at runtime.</summary>
    public List<ResourceId> Events => _events;

    /// <summary>Attached spawn component (for IT_SPAWN_CHAR / IT_SPAWN_ITEM).</summary>
    public SpawnComponent? SpawnChar { get; set; }
    public ItemSpawnComponent? SpawnItem { get; set; }

    public ItemType ItemType
    {
        get => _type;
        set => _type = value;
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

    /// <summary>Full display id (BaseId, used in UO packet as item graphic).</summary>
    public ushort DispIdFull => BaseId;

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
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('%') < 0)
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
    public Point3D MoreP { get => _moreP; set => _moreP = value; }
    public Serial Link { get => _link; set => _link = value; }
    public int Price { get => _price; set => _price = value; }
    public ushort Quality { get => _quality; set => _quality = value; }

    /// <summary>Weapon swing speed read from ITEMDEF.SPEED. 0 = unspecified
    /// (combat code falls back to a sensible default). Used by
    /// <c>GetSwingDelayMs</c> to compute swing recoil per Source-X
    /// <c>Calc_CombatAttackSpeed</c>.</summary>
    public int Speed
    {
        get
        {
            var def = DefinitionLoader.GetItemDef(BaseId);
            return def?.Speed ?? 0;
        }
    }

    /// <summary>Item weight in stones (sphere units). Reads from ITEMDEF.</summary>
    public int Weight
    {
        get
        {
            var def = DefinitionLoader.GetItemDef(BaseId);
            return def?.Weight ?? 0;
        }
    }

    /// <summary>True if this is a 2-handed weapon. Used for swing speed
    /// weight bonus and to block shield equip.</summary>
    public bool IsTwoHanded
    {
        get
        {
            if (EquipLayer == Layer.TwoHanded) return true;
            var def = DefinitionLoader.GetItemDef(BaseId);
            return def?.TwoHands ?? false;
        }
    }

    public void Delete()
    {
        _isDeleted = true;
        _contents.Clear();
    }

    // --- Container functionality ---

    public IReadOnlyList<Item> Contents => _contents;
    public int ContentCount => _contents.Count;

    public void AddItem(Item item)
    {
        _contents.Add(item);
        item.ContainedIn = Uid;
    }

    public bool RemoveItem(Item item)
    {
        if (_contents.Remove(item))
        {
            item.ContainedIn = Serial.Invalid;
            return true;
        }
        return false;
    }

    public Item? FindContentItem(Serial uid)
    {
        foreach (var item in _contents)
        {
            if (item.Uid == uid) return item;
            var found = item.FindContentItem(uid);
            if (found != null) return found;
        }
        return null;
    }

    public int TotalWeight
    {
        get
        {
            int w = Amount;
            foreach (var child in _contents)
                w += child.TotalWeight;
            return w;
        }
    }

    // --- IScriptObj overrides ---

    public override bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "TYPE": value = ((ushort)_type).ToString(); return true;
            case "AMOUNT": value = _amount.ToString(); return true;
            case "CONT": value = _containedIn.IsValid ? $"0{_containedIn.Value:X}" : ""; return true;
            case "LAYER": value = ((byte)EquipLayer).ToString(); return true;

            // Faz 1: Core fields
            case "MORE1": case "MORE": value = $"0{_more1:X}"; return true;
            case "MORE2": value = $"0{_more2:X}"; return true;
            case "MORE1H": value = ((ushort)(_more1 >> 16)).ToString(); return true;
            case "MORE1L": value = ((ushort)(_more1 & 0xFFFF)).ToString(); return true;
            case "MORE2H": value = ((ushort)(_more2 >> 16)).ToString(); return true;
            case "MORE2L": value = ((ushort)(_more2 & 0xFFFF)).ToString(); return true;
            case "MOREP": value = _moreP.ToString(); return true;
            case "MOREX": value = _moreP.X.ToString(); return true;
            case "MOREY": value = _moreP.Y.ToString(); return true;
            case "MOREZ": value = _moreP.Z.ToString(); return true;
            case "LINK": value = _link.IsValid ? $"0{_link.Value:X}" : ""; return true;
            case "PRICE": value = _price.ToString(); return true;
            case "QUALITY": value = _quality.ToString(); return true;

            // TDATA instance
            case "TDATA1": value = _tdata1.ToString(); return true;
            case "TDATA2": value = _tdata2.ToString(); return true;
            case "TDATA3": value = _tdata3.ToString(); return true;
            case "TDATA4": value = _tdata4.ToString(); return true;

            // Identity
            case "ISITEM": value = "1"; return true;
            case "ISCHAR": value = "0"; return true;
            case "BASEID": value = $"0{BaseId:X}"; return true;
            case "DISPIDDEC": value = BaseId.ToString(); return true;

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
        if (upper.StartsWith("RESCOUNT.", StringComparison.Ordinal))
        {
            var arg = upper[9..];
            ushort id = ParseHexId(arg);
            value = GetResCount(id).ToString();
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
                case "SPAWNCOUNT" or "SPAWNCUR":
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
        var def = DefinitionLoader.GetItemDef(BaseId);
        if (def != null)
        {
            switch (upper)
            {
                case "VALUE": value = def.ValueMin == def.ValueMax ? def.ValueMin.ToString() : $"{def.ValueMin},{def.ValueMax}"; return true;
                case "WEIGHT": value = def.Weight.ToString(); return true;
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
        }

        return base.TryGetProperty(key, out value);
    }

    public override bool TrySetProperty(string key, string value)
    {
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "TYPE":
                if (ushort.TryParse(value, out ushort tv)) _type = (ItemType)tv;
                return true;
            case "AMOUNT":
                if (ushort.TryParse(value, out ushort av)) Amount = av;
                return true;

            // Faz 1: Core fields
            case "MORE1": case "MORE":
                _more1 = ParseHexUInt(value);
                return true;
            case "MORE2":
                _more2 = ParseHexUInt(value);
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
                _link = new Serial(ParseHexUInt(value));
                return true;
            case "PRICE":
                if (int.TryParse(value, out int pv)) _price = pv;
                return true;
            case "QUALITY":
                if (ushort.TryParse(value, out ushort qv)) _quality = qv;
                return true;

            // TDATA instance
            case "TDATA1": _tdata1 = ParseHexUInt(value); return true;
            case "TDATA2": _tdata2 = ParseHexUInt(value); return true;
            case "TDATA3": _tdata3 = ParseHexUInt(value); return true;
            case "TDATA4": _tdata4 = ParseHexUInt(value); return true;
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
                        return true;
                    case "SPEEDMODE":
                        if (byte.TryParse(value, out byte sm))
                            ship.SpeedMode = (Core.Enums.ShipSpeedMode)Math.Clamp(sm, (byte)1, (byte)4);
                        return true;
                    case "PILOT":
                        ship.Pilot = new Serial(ParseHexUInt(value));
                        return true;
                }

                if (upper.StartsWith("SHIPSPEED.", StringComparison.Ordinal))
                {
                    var sub = upper[10..];
                    if (sub == "TILES")
                    {
                        if (byte.TryParse(value, out byte t)) ship.SpeedTiles = t;
                    }
                    else if (sub == "PERIOD")
                    {
                        if (ushort.TryParse(value, out ushort p)) ship.SpeedPeriod = p;
                    }
                    return true;
                }

                if (upper == "SHIPSPEED")
                {
                    // "period,tiles" format
                    var parts = value.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        if (ushort.TryParse(parts[0], out ushort p)) ship.SpeedPeriod = p;
                        if (byte.TryParse(parts[1], out byte t)) ship.SpeedTiles = t;
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
                case "SPAWNMAX":
                    if (int.TryParse(value, out int sm)) SpawnChar.MaxCount = sm;
                    return true;
                case "SPAWNRANGE":
                    if (int.TryParse(value, out int sr)) SpawnChar.SpawnRange = sr;
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
        switch (upper)
        {
            // Faz 2: Container commands
            case "OPEN":
                // SendOpenContainer is on GameClient; source must be a GameClient
                // The actual open is handled via TryExecuteScriptCommand at GameClient level
                return true;
            case "DELETE":
                // DELETE nth — 1-based index
                if (int.TryParse(args.Trim(), out int delIdx) && delIdx >= 1 && delIdx <= _contents.Count)
                {
                    _contents[delIdx - 1].Delete();
                    _contents.RemoveAt(delIdx - 1);
                }
                return true;
            case "EMPTY":
                foreach (var child in _contents.ToArray())
                    child.Delete();
                _contents.Clear();
                return true;
            case "FIXWEIGHT":
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
                var def = DefinitionLoader.GetItemDef(BaseId);
                if (def != null && def.Flip)
                {
                    BaseId = (ushort)(BaseId ^ 1);
                }
                return true;
            }
            // Source-X CV_DCLICK: simulate a double-click on this item.
            // The actual handler runs in GameClient (containers, doors,
            // potions, etc.); here we just acknowledge so the X-prefix
            // chain doesn't fall through to the script fallback.
            case "DCLICK":
            case "USE":
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
                // Resend building state to character — actual packet send at engine level
                // Stub: mark for resync
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
                    uint uid = ParseHexUInt(args.Trim());
                    if (uid != 0) guild.DeclareWar(new Serial(uid));
                }
                return true;
            }
            case "DECLAREPEACE":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexUInt(args.Trim());
                    if (uid != 0) guild.DeclarePeace(new Serial(uid));
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
                        uint uid = ParseHexUInt(parts[0]);
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
            case "APPLYTOJOIN":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexUInt(args.Trim());
                    if (uid != 0) guild.AddRecruit(new Serial(uid));
                }
                return true;
            }
            case "JOINASMEMBER":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexUInt(args.Trim());
                    if (uid != 0) guild.JoinAsMember(new Serial(uid));
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
                    uint uid = ParseHexUInt(args.Trim());
                    if (uid != 0) guild.RemoveMember(new Serial(uid));
                }
                return true;
            }
            case "TOGGLEABBREVIATION":
            {
                var guild = ResolveGuild?.Invoke(Uid);
                if (guild != null)
                {
                    uint uid = ParseHexUInt(args.Trim());
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
                // Stub: actual execution requires script engine context
                return true;
            }
            case "ALLGUILDS":
            {
                // ALLGUILDS flags, command — execute command on linked guilds
                // Stub: actual execution requires script engine context
                return true;
            }
        }

        // Spawn commands (IT_SPAWN_CHAR)
        if (SpawnChar != null)
        {
            switch (upper)
            {
                case "SPAWNRESET":
                    SpawnChar.KillAll();
                    SpawnChar.ForceSpawn();
                    return true;
                case "SPAWNCLEAR":
                    SpawnChar.KillAll();
                    return true;
            }
        }

        return base.TryExecuteCommand(key, args, source);
    }

    public override bool OnTick()
    {
        if (_isDeleted) return false;

        // Item decay: if DecayTime is set and elapsed, mark deleted
        if (DecayTime > 0 && Environment.TickCount64 >= DecayTime)
        {
            // Corpses drop their remaining contents to the ground as
            // the body rots. ServUO/Source-X both do this at decay
            // time rather than destroying the items with the corpse.
            if (_type == ItemType.Corpse && _contents.Count > 0)
            {
                OnCorpseDecay?.Invoke(this);
            }
            _isDeleted = true;
            SpawnChar?.KillAll();
            return false;
        }

        // Spawn component ticks
        long now = Environment.TickCount64;
        SpawnChar?.OnTick(now);
        SpawnItem?.OnTick(now);

        return true;
    }

    /// <summary>
    /// Initialize the SpawnComponent for IT_SPAWN_CHAR items.
    /// Resolves MORE1 as either a spawn group defname or a single chardef ID.
    /// Called from WorldLoader/LegacySphereImporter after item load.
    /// </summary>
    public void InitializeSpawnComponent(World.GameWorld world, ResourceHolder resources)
    {
        if (_type != ItemType.SpawnChar)
            return;

        if (SpawnChar == null)
            SpawnChar = new SpawnComponent(this, world);

        SpawnChar.SetFromMore1(_more1, resources);

        // MORE2 low word = max count, high word = min delay in minutes
        if (_more2 != 0)
        {
            ushort maxCount = (ushort)(_more2 & 0xFFFF);
            if (maxCount > 0)
                SpawnChar.MaxCount = maxCount;
        }
    }

    // --- Helper methods ---

    private int GetDeepContentCount()
    {
        int count = _contents.Count;
        foreach (var child in _contents)
            count += child.GetDeepContentCount();
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

    private static uint ParseHexUInt(string val)
    {
        var s = val.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, null, out uint h)) return h;
        }
        else if (s.Length > 1 && s[0] == '0' && !s.All(char.IsDigit))
        {
            if (uint.TryParse(s, NumberStyles.HexNumber, null, out uint h)) return h;
        }
        if (uint.TryParse(s, out uint d)) return d;
        return 0;
    }

    private static ItemType ParseItemType(string arg)
    {
        // Try "t_container" style name (strip t_ prefix, enum parse)
        var s = arg.Trim();
        if (s.StartsWith("T_", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (Enum.TryParse<ItemType>(s, ignoreCase: true, out var it))
            return it;
        // Try numeric
        if (ushort.TryParse(arg.Trim(), out ushort n))
            return (ItemType)n;
        return ItemType.Invalid;
    }

    private Item? FindContentByBaseId(ushort baseId)
    {
        foreach (var item in _contents)
            if (item.BaseId == baseId) return item;
        return null;
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
        uint val = ParseHexUInt(uidStr);
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
                uint uid = ParseHexUInt(value);
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
            uint uid = ParseHexUInt(upper[14..]);
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
            uint uid = ParseHexUInt(upper[12..]);
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
}
