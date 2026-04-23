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

    public uint Anim { get; set; }
    public short BloodColor { get; set; }
    public int MoveRate { get; set; } = 100;
    public uint HireDayWage { get; set; }
    public ushort MaxFood { get; set; }
    public string Icon { get; set; } = "";
    public string Job { get; set; } = "";
    public int ThrowDam { get; set; }
    public int ThrowDamType { get; set; }
    public ushort ThrowObj { get; set; }
    public int ThrowRange { get; set; }

    public List<ResourceId> Desires { get; } = [];
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
            case "CAN": Can = (CanFlags)ParseHexOrDecUInt(value); break;
            case "HEIGHT": byte.TryParse(value, out byte h); Height = h; break;
            case "DEFNAME": DefName = value; break;
            case "FOODTYPE": Enum.TryParse(value, true, out ItemType ft); FoodType = ft; break;
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
            case "MAXFOOD": ushort.TryParse(value, out ushort mf); MaxFood = mf; break;
            case "ICON": Icon = value.Trim(); break;
            case "JOB": Job = value.Trim(); break;
            case "THROWDAM": int.TryParse(value, out int td); ThrowDam = td; break;
            case "THROWDAMTYPE": int.TryParse(value, out int tdt); ThrowDamType = tdt; break;
            case "THROWOBJ": ThrowObj = ParseHexOrDec(value); break;
            case "THROWRANGE": int.TryParse(value, out int tr); ThrowRange = tr; break;
            case "NPCSPELL":
                if (int.TryParse(value.Trim(), out int spId) && spId > 0 && !NpcSpells.Contains(spId))
                    NpcSpells.Add(spId);
                break;
            case "RESLEVEL": byte.TryParse(value, out byte rsl); ResLevel = rsl; break;
            case "RESDISPDNHUE": ResDispDnHue = ParseHexOrDec(value); break;
            case "RESDISPDNID": ResDispDnId = ParseHexOrDec(value); break;
            case "RESOURCES": ParseResourceList(value, BaseResources); break;
            case "EVENTS":
            case "TEVENTS":
                ParseEventsList(value);
                break;
            case "DESIRES": ParseResourceList(value, Desires); break;
            case "AVERSIONS": ParseResourceList(value, Aversions); break;
            case "SUBSECTION": Subsection = value.Trim(); break;
            case "DESCRIPTION": Description = value.Trim(); break;
            case "FOLLOWERSLOTS": int.TryParse(value, out int fs); FollowerSlots = fs; break;
            case "DAMPHYSICAL": short.TryParse(value, out short dpv); DamPhysical = dpv; break;
            case "DAMFIRE": short.TryParse(value, out short dfv); DamFire = dfv; break;
            case "DAMCOLD": short.TryParse(value, out short dcv); DamCold = dcv; break;
            case "DAMPOISON": short.TryParse(value, out short dpov); DamPoison = dpov; break;
            case "DAMENERGY": short.TryParse(value, out short dev); DamEnergy = dev; break;
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
                break;
            default:
                if (key.StartsWith("TAG.", StringComparison.OrdinalIgnoreCase))
                    TagDefs.Set(key[4..], value);
                break;
        }
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

    private static void ParseResourceList(string value, List<ResourceId> list)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in parts)
        {
            var rid = ResourceId.FromString(name);
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
        ushort.TryParse(value, out ushort result);
        return result;
    }

    private static (int Min, int Max) ParseRange(string value)
    {
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

    private static ushort ParseHexOrDec(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            ushort.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort result);
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

        return ushort.TryParse(value, out result);
    }

    private static uint ParseHexOrDecUInt(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint result);
            return result;
        }
        uint.TryParse(value, out uint r);
        return r;
    }
}
