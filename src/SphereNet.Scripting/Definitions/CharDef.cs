using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Scripting.Definitions;

/// <summary>Entry for an ITEMNEWBIE/ITEM line in a CHARDEF. The stock
/// script form is <c>ITEM=defname[,amount[,dice]]</c> where <c>dice</c>
/// is a Sphere dice roll (R5 = 1..5, 2d6 = 2 six-sided). When Dice is
/// non-null it overrides Amount at spawn time.</summary>
public sealed class NewbieItemEntry
{
    public string DefName { get; set; } = "";
    public string? Color { get; set; }
    public int Amount { get; set; }
    public string? Dice { get; set; }
    /// <summary>True when loaded from ITEMNEWBIE (no-loot on death).
    /// Source-X flags these with ATTR_NEWBIE; we just remember the
    /// origin so a future loot table can skip them.</summary>
    public bool Newbie { get; set; }
}

/// <summary>
/// Character definition template. Maps to CCharBase in Source-X.
/// Loaded lazily from [CHARDEF] sections.
/// </summary>
public sealed class CharDef : BaseDef
{
    /// <summary>
    /// Optional display/body reference from ID=/DISPID= when defined as a DEFNAME
    /// (example: ID=c_horse_brown_dk).
    /// </summary>
    public string DisplayIdRef { get; set; } = "";

    public ushort TrackId { get; set; }
    public ushort SoundIdle { get; set; }
    public ushort SoundNotice { get; set; }
    public ushort SoundHit { get; set; }
    public ushort SoundGetHit { get; set; }
    public ushort SoundDie { get; set; }

    public int StrMin { get; set; }
    public int StrMax { get; set; }
    public int DexMin { get; set; }
    public int DexMax { get; set; }
    public int IntMin { get; set; }
    public int IntMax { get; set; }

    public int HitsMin { get; set; }
    public int HitsMax { get; set; }

    public ItemType FoodType { get; set; }
    public string FoodTypeRaw { get; set; } = "";
    public ushort BaseColor { get; set; }
    public int EraLimitGear { get; set; }
    public int EraLimitLoot { get; set; }
    public int EraLimitProps { get; set; }
    public short ResPhysical { get; set; }
    public short ResFire { get; set; }
    public short ResCold { get; set; }
    public short ResPoison { get; set; }
    public short ResEnergy { get; set; }
    public short ResPhysicalMax { get; set; }
    public short ResFireMax { get; set; }
    public short ResColdMax { get; set; }
    public short ResPoisonMax { get; set; }
    public short ResEnergyMax { get; set; }
    public short ReflectPhysicalDam { get; set; }
    public List<string> Aliases { get; } = [];

    public uint Anim { get; set; }
    public short BloodColor { get; set; }
    public int MoveRate { get; set; } = 100;
    public uint HireDayWage { get; set; }
    public ushort MaxFood { get; set; }

    /// <summary>True when the definition wrote MAXFOOD itself, as opposed to the value
    /// having been derived from FOODTYPE or left at the default. Source-X keeps no such
    /// marker - its zero simply means zero - but SphereNet has to tell "this creature
    /// eats nothing" apart from "this definition says nothing about food", because a
    /// definition that says nothing still gets the classic ceiling.</summary>
    public bool MaxFoodExplicit { get; private set; }
    public string Icon { get; set; } = "";
    public string Job { get; set; } = "";
    public int ThrowDam { get; set; }
    public int ThrowDamType { get; set; }
    public ushort ThrowObj { get; set; }
    public int ThrowRange { get; set; }

    public List<ResourceId> Desires { get; } = [];

    /// <summary>Want score per Desires entry, index-aligned. Source-X
    /// NPC_WantThisItem returns the DESIRES entry's resource qty (default 1
    /// when the pack writes a bare defname) — not a flat maximum.</summary>
    public List<int> DesireQtys { get; } = [];
    public List<ResourceId> Aversions { get; } = [];
    public ResourceId? SpeechResource { get; set; }
    public List<ResourceId> SpeechResources { get; } = [];
    public NpcBrainType NpcBrain { get; set; }

    /// <summary>Items to equip on NPC spawn (ITEMNEWBIE lines + optional COLOR).</summary>
    public List<NewbieItemEntry> NewbieItems { get; } = [];

    /// <summary>Loot category name for CATEGORY= from scripts.</summary>
    public string Category { get; set; } = "";
    public string Subsection { get; set; } = "";
    public string Description { get; set; } = "";
    public int FollowerSlots { get; set; } = 1;
    public short DamPhysical { get; set; }
    public short DamFire { get; set; }
    public short DamCold { get; set; }
    public short DamPoison { get; set; }
    public short DamEnergy { get; set; }

    public List<int> NpcSpells { get; } = [];
    public Dictionary<SkillType, (int Min, int Max)> SkillRanges { get; } = [];

    public CharDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "NAME": Name = value; break;
            case "ID":
            case "DISPID":
                if (TryParseHexOrDec(value, out ushort dispId))
                {
                    DispIndex = dispId;
                    DisplayIdRef = "";
                }
                else
                {
                    // Source-style alias body reference (e.g. ID=c_horse_brown_dk)
                    DisplayIdRef = value.Trim();
                }
                break;
            case "TRACKID": TrackId = ParseHexOrDec(value); break;
            case "SOUND": ParseCharSounds(value); break;
            case "SOUNDIDLE": SoundIdle = ParseUShort(value); break;
            case "SOUNDNOTICE": SoundNotice = ParseUShort(value); break;
            case "SOUNDHIT": SoundHit = ParseUShort(value); break;
            case "SOUNDGETHIT": SoundGetHit = ParseUShort(value); break;
            case "SOUNDDIE": SoundDie = ParseUShort(value); break;
            case "STR": (StrMin, StrMax) = ParseRange(value); break;
            case "DEX": (DexMin, DexMax) = ParseRange(value); break;
            case "INT": (IntMin, IntMax) = ParseRange(value); break;
            case "HITS": (HitsMin, HitsMax) = ParseRange(value); break;
            case "DAM": (AttackMin, AttackMax) = ParseRange(value); break;
            case "ARMOR": (DefenseMin, DefenseMax) = ParseRange(value); break;
            case "CAN": Can = ParseCanFlags(value); break;
            case "HEIGHT": byte.TryParse(value, out byte h); Height = h; break;
            case "DEFNAME": DefName = value; break;
            case "DEFNAME2":
                foreach (string alias in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (!Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) Aliases.Add(alias);
                break;
            case "FOODTYPE":
                FoodTypeRaw = value.Trim();
                FoodType = ParseFoodType(value);
                // Source-X CCharBase::SetFoodType derives m_MaxFood from FOODTYPE:
                // the largest resource quantity (bare resource = qty 1).
                MaxFood = DeriveMaxFood(FoodTypeRaw);
                break;
            case "ERALIMITGEAR": int.TryParse(value, out int eraGear); EraLimitGear = eraGear; break;
            case "ERALIMITLOOT": int.TryParse(value, out int eraLoot); EraLimitLoot = eraLoot; break;
            case "ERALIMITPROPS": int.TryParse(value, out int eraProps); EraLimitProps = eraProps; break;
            case "NPC":
            case "NPCBRAIN":
                // Source-X CHARDEF accepts both "NPC=brain_vendor" (DEFNAME
                // form) and "NPCBRAIN=Vendor" (numeric/legacy
                // saves). Strip the brain_ / npc_ prefix so the matching
                // enum value parses; without this c_alchemist & friends
                // dropped to NpcBrain=None and the spawn pipeline fell back
                // to Animal — no vendor speech / buy / sell dispatch.
                {
                    string raw = (value ?? string.Empty).Trim();
                    if (raw.StartsWith("brain_", StringComparison.OrdinalIgnoreCase))
                        raw = raw[6..];
                    else if (raw.StartsWith("npc_", StringComparison.OrdinalIgnoreCase))
                        raw = raw[4..];
                    if (Enum.TryParse(raw, true, out NpcBrainType nb))
                        NpcBrain = nb;
                    else if (int.TryParse(raw, out int nbi))
                        NpcBrain = (NpcBrainType)nbi;
                }
                break;
            case "CATEGORY": Category = value.Trim(); break;
            case "ANIM": Anim = ParseHexOrDecUInt(value); break;
            case "BLOODCOLOR": short.TryParse(value, out short bc); BloodColor = bc; break;
            case "RANGE": (RangeMin, RangeMax) = ParseRange(value); break;
            case "RANGEH": int.TryParse(value, out int rh); RangeMax = rh; break;
            case "RANGEL": int.TryParse(value, out int rl); RangeMin = rl; break;
            case "MOVERATE": int.TryParse(value, out int mr); MoveRate = mr; break;
            case "HIREDAYWAGE": uint.TryParse(value, out uint hw); HireDayWage = hw; break;
            case "MAXFOOD":
                ushort.TryParse(value, out ushort mf);
                MaxFood = mf;
                MaxFoodExplicit = true;
                break;
            case "ICON": Icon = value.Trim(); break;
            case "JOB": Job = value.Trim(); break;
            case "THROWDAM": int.TryParse(value, out int td); ThrowDam = td; TagDefs.Set("THROWDAM", value.Trim()); break;
            case "THROWDAMTYPE": int.TryParse(value, out int tdt); ThrowDamType = tdt; TagDefs.Set("THROWDAMTYPE", value.Trim()); break;
            case "THROWOBJ": ThrowObj = ParseHexOrDec(value); TagDefs.Set("THROWOBJ", value.Trim()); break;
            case "THROWRANGE": int.TryParse(value, out int tr); ThrowRange = tr; TagDefs.Set("THROWRANGE", value.Trim()); break;
            case "NPCSPELL":
                if (TryParseNpcSpell(value, out int spId) && spId > 0 && !NpcSpells.Contains(spId))
                    NpcSpells.Add(spId);
                break;
            case "RESLEVEL": byte.TryParse(value, out byte rsl); ResLevel = rsl; break;
            case "RESDISPDNHUE": ResDispDnHue = ParseHexOrDec(value); break;
            case "RESDISPDNID": ResDispDnId = ParseHexOrDec(value); break;
            case "RESOURCES": ParseCarveResources(value); break;
            case "EVENTS":
            case "TEVENTS":
                ParseEventsList(value);
                break;
            case "DESIRES": ParseDesireList(value); break;
            case "AVERSIONS": ParseResourceList(value, Aversions); break;
            case "SUBSECTION": Subsection = value.Trim(); break;
            case "DESCRIPTION": Description = value.Trim(); break;
            case "FOLLOWERSLOTS": int.TryParse(value, out int fs); FollowerSlots = fs; break;
            // Source-X CCPropsChar FACTION_GROUP/FACTION_SPECIES (the Slayer
            // system's NPC side) — stored as def-tags so ApplyNpcDefinitionTags
            // lands them on every spawned instance.
            case "FACTION_GROUP":
            case "FACTION_SPECIES":
                TagDefs.Set(key, value.Trim());
                break;
            // AOS on-hit combat properties (HITLEECHLIFE, HITFIREBALL, ...):
            // same def-tag flow as the faction pair.
            case var _ when AosOnHitProperties.Contains(key):
                TagDefs.Set(key.ToUpperInvariant(), value.Trim());
                break;
            case var _ when SpellCastingProperties.Contains(key):
                TagDefs.Set(key.ToUpperInvariant(), value.Trim());
                break;
            case CombatSpeedProperties.IncreaseSwingSpeed:
                TagDefs.Set(CombatSpeedProperties.IncreaseSwingSpeed, value.Trim());
                break;
            case "DAMPHYSICAL": short.TryParse(value, out short dpv); DamPhysical = dpv; break;
            case "DAMFIRE": short.TryParse(value, out short dfv); DamFire = dfv; break;
            case "DAMCOLD": short.TryParse(value, out short dcv); DamCold = dcv; break;
            case "DAMPOISON": short.TryParse(value, out short dpov); DamPoison = dpov; break;
            case "DAMENERGY": short.TryParse(value, out short dev); DamEnergy = dev; break;
            case "RESPHYSICAL": short.TryParse(value, out short rpv); ResPhysical = rpv; break;
            case "RESFIRE": short.TryParse(value, out short rfv); ResFire = rfv; break;
            case "RESCOLD": short.TryParse(value, out short rcv); ResCold = rcv; break;
            case "RESPOISON": short.TryParse(value, out short rpov); ResPoison = rpov; break;
            case "RESENERGY": short.TryParse(value, out short rev); ResEnergy = rev; break;
            case "RESPHYSICALMAX": short.TryParse(value, out short rpm); ResPhysicalMax = rpm; break;
            case "RESFIREMAX": short.TryParse(value, out short rfm); ResFireMax = rfm; break;
            case "RESCOLDMAX": short.TryParse(value, out short rcm); ResColdMax = rcm; break;
            case "RESPOISONMAX": short.TryParse(value, out short rpom); ResPoisonMax = rpom; break;
            case "RESENERGYMAX": short.TryParse(value, out short rem); ResEnergyMax = rem; break;
            case "REFLECTPHYSICALDAM": short.TryParse(value, out short reflect); ReflectPhysicalDam = reflect; break;
            case "SPEECH":
                SpeechResource = ResourceId.FromString(value);
                var speechRid = ResourceId.FromString(value, ResType.Speech);
                if (speechRid.IsValid && !SpeechResources.Contains(speechRid))
                    SpeechResources.Add(speechRid);
                break;
            case "TSPEECH":
                var tspeechRid = ResourceId.FromString(value, ResType.Speech);
                if (tspeechRid.IsValid && !SpeechResources.Contains(tspeechRid))
                    SpeechResources.Add(tspeechRid);
                break;
            case "ITEMNEWBIE":
            case "ITEM":
            {
                // ITEM=defname[,amount[,dice]]
                //   ITEM=i_dagger                 → just the item
                //   ITEM=i_gold,50                → 50 gold
                //   ITEM=random_facial_hair,1,R5  → one pick, 1..5 rand
                var parts = value.Split(',', StringSplitOptions.TrimEntries);
                var entry = new NewbieItemEntry
                {
                    DefName = parts.Length > 0 ? parts[0] : "",
                    Newbie = key.Equals("ITEMNEWBIE", StringComparison.OrdinalIgnoreCase),
                };
                if (parts.Length >= 2 && int.TryParse(parts[1], out int amt) && amt > 0)
                    entry.Amount = amt;
                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                    entry.Dice = parts[2];
                NewbieItems.Add(entry);
                break;
            }
            case "COLOR":
                // COLOR after ITEMNEWBIE sets that item's hue
                if (NewbieItems.Count > 0)
                    NewbieItems[^1].Color = value.Trim();
                else
                    BaseColor = ParseUShort(value);
                break;
            default:
                if (key.StartsWith("BREATH.", StringComparison.OrdinalIgnoreCase))
                {
                    TagDefs.Set(key, value.Trim());
                    break;
                }
                if (TryParseSkillName(key, out var skill))
                {
                    SkillRanges[skill] = ParseSkillRange(value);
                    break;
                }
                if (key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase))
                {
                    TagDefs.Set(key[4..], value);
                    break;
                }
                // Previously dropped with zero visibility — count it so a
                // real pack load can report what it lost.
                UnknownKeyDiagnostics.Record("CHARDEF", key);
                break;
        }
    }

    private static bool TryParseSkillName(string key, out SkillType skill)
    {
        string normalized = key.Trim().Replace("_", "", StringComparison.OrdinalIgnoreCase);
        if (normalized.StartsWith("SKILL", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[5..];

        skill = normalized.ToUpperInvariant() switch
        {
            "EVALUATINGINTEL" or "EVALUATINGINTELLIGENCE" or "EVALINT" => SkillType.EvalInt,
            "ITEMID" or "ITEMIDENTIFICATION" => SkillType.ItemId,
            "TASTEID" or "TASTEIDENTIFICATION" => SkillType.TasteId,
            "MAGICRESISTANCE" or "RESISTINGSPELLS" or "RESIST" => SkillType.MagicResistance,
            "ANIMALLORE" => SkillType.AnimalLore,
            "ARMSLORE" => SkillType.ArmsLore,
            "DETECTINGHIDDEN" => SkillType.DetectingHidden,
            "SPIRITSPEAK" => SkillType.SpiritSpeak,
            "SWORDSMANSHIP" => SkillType.Swordsmanship,
            "MACEFIGHTING" or "MACEFIGHT" => SkillType.MaceFighting,
            "FENCING" => SkillType.Fencing,
            "WRESTLING" => SkillType.Wrestling,
            "TACTICS" => SkillType.Tactics,
            "ARCHERY" => SkillType.Archery,
            "MAGERY" => SkillType.Magery,
            "PARRYING" or "PARRY" => SkillType.Parrying,
            _ => SkillType.None,
        };
        if (skill != SkillType.None)
            return true;

        return Enum.TryParse(key, true, out skill) && skill >= 0 && skill < SkillType.Qty;
    }

    private void ParseEventsList(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in parts)
        {
            var rid = ResourceId.FromEventName(name);
            if (rid.IsValid && !Events.Contains(rid))
                Events.Add(rid);
        }
    }

    /// <summary>RESOURCES entries with their amounts and raw defnames
    /// ("19 i_ribs_raw" → 19×, "i_flesh_head_2" → 1×). Corpse carving reads
    /// this list (reference Use_CarveCorpse over m_BaseResources).</summary>
    public List<(ResourceId Rid, int Amount, string DefName)> CarveResources { get; } = [];

    private void ParseCarveResources(string value)
    {
        CarveResources.Clear();
        BaseResources.Clear();
        foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string name = raw;
            int amount = 1;
            int sp = raw.IndexOf(' ');
            if (sp > 0 && int.TryParse(raw[..sp], out int amt))
            {
                amount = Math.Max(1, amt);
                name = raw[(sp + 1)..].Trim();
            }
            var rid = ResourceId.FromString(name);
            if (rid.IsValid)
                BaseResources.Add(rid);
            CarveResources.Add((rid, amount, name));
        }
    }

    /// <summary>DESIRES is a Source-X resource-qty list: each entry is a
    /// bare defname, "qty defname" or "defname qty". The qty is the want
    /// score (NPC_WantThisItem returns GetResQty; bare entries default 1).
    /// A qty-carrying entry used to fail ResourceId.FromString entirely.</summary>
    private void ParseDesireList(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in parts)
        {
            var tokens = entry.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) continue;
            string name = tokens[0];
            int qty = 1;
            if (tokens.Length >= 2 && int.TryParse(tokens[0], out int leadQty))
            {
                qty = leadQty;
                name = tokens[1];
            }
            else if (tokens.Length >= 2 && int.TryParse(tokens[1], out int tailQty))
            {
                qty = tailQty;
            }
            // r_* entries are REGION preferences (Source-X keeps region
            // resources in m_Desires for homing) — type them as such so the
            // item want-matching never hashes them as fake itemdefs.
            ResType type = name.StartsWith("t_", StringComparison.OrdinalIgnoreCase)
                ? ResType.TypeDef
                : name.StartsWith("r_", StringComparison.OrdinalIgnoreCase)
                    ? ResType.RegionType
                    : ResType.ItemDef;
            var rid = ResourceId.FromString(name, type);
            if (!rid.IsValid) continue;
            Desires.Add(rid);
            // Source-X preserves the raw qty (DESIRES=999 t_corpse stays 999
            // for @NPCLookAtItem ARGN2); anything >= 100 is a certain want.
            DesireQtys.Add(Math.Max(0, qty));
        }
    }

    private static void ParseResourceList(string value, List<ResourceId> list)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in parts)
        {
            string token = name.Trim();
            ResType type = token.StartsWith("t_", StringComparison.OrdinalIgnoreCase)
                ? ResType.TypeDef
                : ResType.ItemDef;
            var rid = ResourceId.FromString(token, type);
            if (rid.IsValid)
                list.Add(rid);
        }
    }

    private void ParseCharSounds(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length >= 1) SoundIdle = ParseUShort(parts[0]);
        if (parts.Length >= 2) SoundNotice = ParseUShort(parts[1]);
        if (parts.Length >= 3) SoundHit = ParseUShort(parts[2]);
        if (parts.Length >= 4) SoundGetHit = ParseUShort(parts[3]);
        if (parts.Length >= 5) SoundDie = ParseUShort(parts[4]);
    }

    private static ushort ParseUShort(string value)
    {
        string token = value.Trim();
        int op = token.LastIndexOfAny(['+', '-']);
        int delta = 0;
        if (op > 0 && int.TryParse(token[(op + 1)..], out int parsedDelta))
        {
            delta = token[op] == '-' ? -parsedDelta : parsedDelta;
            token = token[..op].Trim();
        }

        uint numeric;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uint.TryParse(token.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out numeric);
        else if (token.Length > 1 && token[0] == '0')
            uint.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out numeric);
        else if (!uint.TryParse(token, out numeric) && DefNameResolver?.Invoke(token) is long resolved)
            numeric = unchecked((uint)resolved);

        return (ushort)Math.Clamp((long)numeric + delta, 0, ushort.MaxValue);
    }

    private static ItemType ParseFoodType(string value)
    {
        string[] choices = value.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (choices.Length == 0)
            return ItemType.Normal;
        string first = choices[0];
        string[] parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return ItemType.Normal;
        string token = parts.Length > 1 && int.TryParse(parts[0], out _) ? parts[1] : parts[0];
        if (token.StartsWith("t_", StringComparison.OrdinalIgnoreCase)) token = token[2..];
        token = token.Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(token, true, out ItemType type) ? type : ItemType.Normal;
    }

    /// <summary>Source-X CCharBase::SetFoodType: m_MaxFood = the largest resource
    /// quantity across the comma-separated FOODTYPE entries. Each entry is
    /// "&lt;qty&gt; &lt;resource&gt;"; a bare resource (no leading number) counts as 1.</summary>
    private static ushort DeriveMaxFood(string foodTypeRaw)
    {
        if (string.IsNullOrWhiteSpace(foodTypeRaw))
            return 0;
        ushort max = 0;
        foreach (var part in foodTypeRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int sp = part.IndexOfAny([' ', '\t']);
            string lead = sp < 0 ? part : part[..sp];
            ushort qty = ushort.TryParse(lead, out ushort q) ? q : (ushort)1;
            if (qty > max) max = qty;
        }
        return max;
    }

    private static (int Min, int Max) ParseRange(string value)
    {
        value = value.Trim();
        // Sphere brace range: {min max} (space-separated). chardefs use this for
        // STR/DEX/INT/HITS, e.g. STR={100 130} — previously this failed to parse
        // (no comma) and the stat collapsed to 0.
        if (value.Length > 0 && value[0] == '{')
        {
            string inner = value.Trim('{', '}').Trim();
            var parts = inner.Split(new[] { ' ', '\t', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out int bmin) && int.TryParse(parts[1], out int bmax))
                return (bmin, bmax);
            if (parts.Length == 1 && int.TryParse(parts[0], out int bsingle))
                return (bsingle, bsingle);
            return (0, 0);
        }
        int comma = value.IndexOf(',');
        if (comma >= 0)
        {
            int.TryParse(value.AsSpan(0, comma).Trim(), out int min);
            int.TryParse(value.AsSpan(comma + 1).Trim(), out int max);
            return (min, max);
        }
        int.TryParse(value, out int single);
        return (single, single);
    }

    private static (int Min, int Max) ParseSkillRange(string value)
    {
        string normalized = value.Trim().Trim('{', '}').Trim();
        string[] parts = normalized.Split([' ', '\t', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return (0, 0);

        if (!decimal.TryParse(parts[0], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out decimal min))
            return (0, 0);
        decimal max = min;
        if (parts.Length > 1 && !decimal.TryParse(parts[1], System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out max))
            max = min;
        if (max < min)
            (min, max) = (max, min);

        // Source scripts express skills in displayed points (15.0), while the
        // runtime stores tenths (150). Plain integer script values retain the
        // legacy raw-scale behavior used by generated definitions.
        decimal scale = normalized.Contains('.') ? 10m : 1m;
        return ((int)Math.Round(min * scale), (int)Math.Round(max * scale));
    }

    private static ushort ParseHexOrDec(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            ushort.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort result);
            return result;
        }
        if (value.StartsWith("0", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
        {
            ushort.TryParse(value.AsSpan(), System.Globalization.NumberStyles.HexNumber, null, out ushort result);
            return result;
        }
        ushort.TryParse(value, out ushort r);
        return r;
    }

    private static bool TryParseHexOrDec(string value, out ushort result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (value.StartsWith("0", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
            return ushort.TryParse(value.AsSpan(), System.Globalization.NumberStyles.HexNumber, null, out result);

        return ushort.TryParse(value, out result);
    }

    /// <summary>Parse a CHARDEF <c>CAN=</c> value. Accepts a numeric mask
    /// (<c>0x2004</c> / <c>8196</c>) OR the symbolic, <c>|</c>-separated Sphere
    /// movement-type constants (<c>MT_RUN|MT_WALK</c>) — the standard
    /// [DEFNAME can_flags] names. Each token maps to the matching CanFlags bit
    /// (values identical to the can_flags defname). Without this the symbolic
    /// form failed to parse and every such creature lost its CAN flags
    /// (run/fly/swim/passwalls), defaulting to a basic walker.</summary>
    /// <summary>Optional hook to resolve a script DEFNAME constant (notably the
    /// can_flags MT_* names) to its numeric value at parse time. Wired to the
    /// ResourceHolder during startup so symbolic CAN= flags follow the script's
    /// own definitions; left null in isolated unit tests, where ParseCanFlags
    /// uses its built-in MT_* fallback map.</summary>
    public static Func<string, long?>? DefNameResolver { get; set; }
    public static Func<string, int?>? SpellNameResolver { get; set; }

    private static bool TryParseNpcSpell(string value, out int spellId)
    {
        spellId = 0;
        string normalized = value.Trim();
        if (int.TryParse(normalized, out int numeric))
        {
            spellId = numeric;
            return true;
        }

        if (normalized.StartsWith("spell_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[6..];
        else if (normalized.StartsWith("s_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        if (Enum.TryParse<SpellType>(normalized, true, out var named))
        {
            spellId = (int)named;
            return true;
        }

        int? resolved = SpellNameResolver?.Invoke(value.Trim());
        if (resolved.HasValue)
        {
            spellId = resolved.Value;
            return true;
        }

        return false;
    }

    private static CanFlags ParseCanFlags(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CanFlags.None;
        uint flags = 0;
        foreach (var raw in value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string tok = raw;
            if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(tok.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hx))
            {
                flags |= hx;
                continue;
            }
            // Sphere numeric convention: a leading zero means HEX — a legacy
            // chardef's CAN=0300 is mt_equip|mt_usehands (0x300), not 300
            // (the ItemDef parsers already follow this; this one read the
            // token as decimal and mis-flagged every imported chardef).
            if (tok.Length > 1 && tok[0] == '0' &&
                uint.TryParse(tok.AsSpan(), System.Globalization.NumberStyles.HexNumber, null, out uint lzHex))
            {
                flags |= lzHex;
                continue;
            }
            if (uint.TryParse(tok, out uint dec))
            {
                flags |= dec;
                continue;
            }
            // Prefer the script's own [DEFNAME can_flags] values when wired
            // (production). The hardcoded map below is the fallback for isolated
            // parsing (e.g. unit tests) where no resolver is attached.
            if (DefNameResolver?.Invoke(tok) is long resolved)
            {
                flags |= (uint)resolved;
                continue;
            }
            flags |= tok.ToUpperInvariant() switch
            {
                "MT_GHOST" => 0x0001u,
                "MT_SWIM" => 0x0002u,
                "MT_WALK" => 0x0004u,
                "MT_PASSWALLS" => 0x0008u,
                "MT_FLY" => 0x0010u,
                "MT_FIRE_IMMUNE" => 0x0020u,
                "MT_INDOORS" => 0x0040u,
                "MT_HOVER" => 0x0080u,
                "MT_EQUIP" => 0x0100u,
                "MT_USEHANDS" => 0x0200u,
                "MT_MOUNT" => 0x0400u,
                "MT_FEMALE" => 0x0800u,
                "MT_NONHUM" => 0x1000u,
                "MT_RUN" => 0x2000u,
                "MT_NODCLICKLOS" => 0x4000u,
                "MT_NODCLICKDIST" => 0x8000u,
                _ => 0u,
            };
        }
        return (CanFlags)flags;
    }

    private static uint ParseHexOrDecUInt(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint result);
            return result;
        }
        if (value.StartsWith("0", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
        {
            uint.TryParse(value.AsSpan(), System.Globalization.NumberStyles.HexNumber, null, out uint result);
            return result;
        }
        uint.TryParse(value, out uint r);
        return r;
    }
}
