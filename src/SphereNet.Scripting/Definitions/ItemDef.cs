using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Item definition template. Maps to CItemBase in Source-X.
/// Loaded lazily from [ITEMDEF] sections.
/// </summary>
public sealed class ItemDef : BaseDef
{
    public ItemType Type { get; set; } = ItemType.Normal;
    public ushort FlipId { get; set; }
    public int Weight { get; set; }
    public Layer Layer { get; set; }
    public int ValueMin { get; set; }
    public int ValueMax { get; set; }
    public ulong QwFlags { get; set; }
    public CanFlags CanUse { get; set; }

    public int Speed { get; set; }
    public SkillType Skill { get; set; }
    public int ReqStr { get; set; }
    public bool Dye { get; set; }
    public bool Flip { get; set; }
    public bool Repair { get; set; }
    public bool Replicate { get; set; }
    public bool TwoHands { get; set; }
    public uint TData1 { get; set; }
    public uint TData2 { get; set; }
    public uint TData3 { get; set; }
    public uint TData4 { get; set; }
    public ulong TFlags { get; set; }
    public ushort AmmoAnim { get; set; }
    public ushort AmmoAnimHue { get; set; }
    public byte AmmoAnimRender { get; set; }
    public ushort AmmoCont { get; set; }
    public string AmmoType { get; set; } = "";
    public string ResMake { get; set; } = "";
    public string DupeList { get; set; } = "";
    public int WeightReduction { get; set; }

    public ushort DupItemId { get; set; }
    public List<ResourceId> SkillMake { get; } = [];
    public string SkillMakeRaw { get; set; } = "";
    public string ResourcesRaw { get; set; } = "";

    public ItemDef(ResourceId id) : base(id) { }

    /// <summary>
    /// Load properties from script key-value pairs.
    /// </summary>
    public void LoadFromKey(string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "NAME": Name = value; break;
            case "TYPE": Type = ParseItemType(value); break;
            case "WEIGHT": int.TryParse(value, out int w); Weight = w; break;
            case "LAYER": Enum.TryParse(value, true, out Layer l); Layer = l; break;
            case "FLIPID": ParseHexOrDec(value, out ushort f); FlipId = f; break;
            case "VALUE": (ValueMin, ValueMax) = ParseRange(value); break;
            case "DAM": (AttackMin, AttackMax) = ParseRange(value); break;
            case "ARMOR": (DefenseMin, DefenseMax) = ParseRange(value); break;
            case "CAN": ParseHexOrDecUInt(value, out uint can); Can = (CanFlags)can; break;
            case "CANUSE": ParseHexOrDecUInt(value, out uint canUse); CanUse = (CanFlags)canUse; break;
            case "HEIGHT": byte.TryParse(value, out byte h); Height = h; break;
            case "DUPEITEM": ParseHexOrDec(value, out ushort dup); DupItemId = dup; break;
            case "ID": ParseHexOrDec(value, out ushort id); DispIndex = id; break;
            case "DISPID": ParseHexOrDec(value, out ushort did); DispIndex = did; break;
            case "DEFNAME": DefName = value; break;
            case "EVENTS":
            case "TEVENTS":
                ParseEventsList(value);
                break;
            case "SKILLMAKE": SkillMakeRaw = value.Trim(); ParseResourceList(value, SkillMake); break;
            case "RESOURCES": ResourcesRaw = value.Trim(); ParseResourceList(value, BaseResources); break;
            case "RANGE": (RangeMin, RangeMax) = ParseRange(value); break;
            case "RANGEH": int.TryParse(value, out int rh); RangeMax = rh; break;
            case "RANGEL": int.TryParse(value, out int rl); RangeMin = rl; break;
            case "SPEED": int.TryParse(value, out int spd); Speed = spd; break;
            case "SKILL": Enum.TryParse(value, true, out SkillType sk); Skill = sk; break;
            case "REQSTR": int.TryParse(value, out int rs); ReqStr = rs; break;
            case "DYE": Dye = value != "0"; break;
            case "FLIP": Flip = value != "0"; break;
            case "REPAIR": Repair = value != "0"; break;
            case "REPLICATE": Replicate = value != "0"; break;
            case "TWOHANDS": TwoHands = value != "0"; break;
            case "TDATA1": ParseHexOrDecUInt(value, out uint td1); TData1 = td1; break;
            case "TDATA2": ParseHexOrDecUInt(value, out uint td2); TData2 = td2; break;
            case "TDATA3": ParseHexOrDecUInt(value, out uint td3); TData3 = td3; break;
            case "TDATA4": ParseHexOrDecUInt(value, out uint td4); TData4 = td4; break;
            case "TFLAGS": ParseHexOrDecULong(value, out ulong tf); TFlags = tf; break;
            case "AMMOANIM": ParseHexOrDec(value, out ushort aa); AmmoAnim = aa; break;
            case "AMMOANIMHUE": ParseHexOrDec(value, out ushort aah); AmmoAnimHue = aah; break;
            case "AMMOANIMRENDER": byte.TryParse(value, out byte aar); AmmoAnimRender = aar; break;
            case "AMMOCONT": ParseHexOrDec(value, out ushort ac); AmmoCont = ac; break;
            case "AMMOTYPE": AmmoType = value.Trim(); break;
            case "RESMAKE": ResMake = value.Trim(); break;
            case "DUPELIST": DupeList = value.Trim(); break;
            case "WEIGHTREDUCTION": int.TryParse(value, out int wr); WeightReduction = wr; break;
            case "RESLEVEL": byte.TryParse(value, out byte rsl); ResLevel = rsl; break;
            case "RESDISPDNHUE": ParseHexOrDec(value, out ushort rdh); ResDispDnHue = rdh; break;
            case "RESDISPDNID": ParseHexOrDec(value, out ushort rdi); ResDispDnId = rdi; break;
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

    private static void ParseHexOrDec(string value, out ushort result)
    {
        result = 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("0", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
        {
            var span = value.AsSpan();
            if (span.Length > 2 && (span[1] == 'x' || span[1] == 'X'))
                ushort.TryParse(span[2..], System.Globalization.NumberStyles.HexNumber, null, out result);
            else
                ushort.TryParse(span, out result);
        }
        else
        {
            ushort.TryParse(value, out result);
        }
    }

    private static void ParseHexOrDecUInt(string value, out uint result)
    {
        result = 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            uint.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        else
            uint.TryParse(value, out result);
    }

    private static void ParseHexOrDecULong(string value, out ulong result)
    {
        result = 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            ulong.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        else
            ulong.TryParse(value, out result);
    }

    /// <summary>
    /// Parse Source-X type strings (t_weapon_sword, t_armor, etc.)
    /// to the ItemType enum. Strips the t_ prefix and underscores.
    /// Also handles numeric values.
    /// </summary>
    private static ItemType ParseItemType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ItemType.Normal;

        // Numeric value
        if (ushort.TryParse(value, out ushort numType))
            return (ItemType)numType;

        // Strip t_ prefix used by Source-X scripts
        string normalized = value.Trim();
        if (normalized.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        // Remove underscores for enum matching (t_weapon_sword → weaponsword → WeaponSword)
        normalized = normalized.Replace("_", "");

        if (Enum.TryParse(normalized, true, out ItemType result))
            return result;

        return ItemType.Normal;
    }

    /// <summary>
    /// Apply Source-X's <c>%plural/singular%</c> name template rules to
    /// produce the runtime display name for the given amount. Mirrors
    /// <c>CItemBase::GetNamePluralize</c> in <c>CItemBase.cpp</c>:
    ///   • <c>%</c> toggles "inside" mode and resets to plural section.
    ///   • <c>/</c> inside switches to the singular section.
    ///   • Inside characters are kept only when they belong to the
    ///     side selected by <paramref name="pluralize"/>.
    /// Examples:
    ///   • <c>"Black Pearl%s%"</c> → "Black Pearl" / "Black Pearls"
    ///   • <c>"%shoes/shoe%"</c> → "shoes" / "shoe"
    ///   • <c>"loa%ves/f%"</c> → "loaves" / "loaf"
    /// </summary>
    public static string Pluralize(string? nameTemplate, bool pluralize)
    {
        if (string.IsNullOrEmpty(nameTemplate))
            return string.Empty;
        if (nameTemplate.IndexOf('%') < 0)
            return nameTemplate; // no template, fast path

        var sb = new System.Text.StringBuilder(nameTemplate.Length);
        bool inside = false;
        bool plural = false;
        foreach (char c in nameTemplate)
        {
            if (c == '%')
            {
                inside = !inside;
                plural = true;
                continue;
            }
            if (inside)
            {
                if (c == '/')
                {
                    plural = false;
                    continue;
                }
                if (pluralize ? !plural : plural)
                    continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Convenience overload — picks plural form when amount &gt; 1.</summary>
    public static string Pluralize(string? nameTemplate, int amount)
        => Pluralize(nameTemplate, amount != 1);
}
