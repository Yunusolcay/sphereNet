using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// REGIONRESOURCE definition. Maps to CRegionResourceDef in Source-X.
/// Defines a harvestable resource type (ore, logs, fish, etc.) with skill requirements and yield.
/// </summary>
public sealed class RegionResourceDef : ResourceLink
{
    /// <summary>Spawn amount range (how many resource units exist in a region).</summary>
    public int AmountMin { get; set; }
    public int AmountMax { get; set; }

    /// <summary>The BASEID of the item produced when gathered.</summary>
    public ushort Reap { get; set; }

    /// <summary>Raw REAP value from script (defname like "i_ore_iron"). Resolved post-load.</summary>
    public string? ReapRaw { get; set; }

    /// <summary>Amount of items yielded per successful gather.</summary>
    public int ReapAmountMin { get; set; } = 1;
    public int ReapAmountMax { get; set; } = 1;

    /// <summary>Regeneration time in seconds.</summary>
    public int Regen { get; set; }

    /// <summary>Skill difficulty range (in tenths: 0-1000).</summary>
    public int SkillMin { get; set; }
    public int SkillMax { get; set; }

    public RegionResourceDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string arg)
    {
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "AMOUNT":
                ParseRange(arg, out int amin, out int amax);
                AmountMin = amin;
                AmountMax = amax;
                break;
            case "REAP":
                Reap = ParseHexOrDec(arg);
                if (Reap == 0)
                    ReapRaw = arg.Trim();
                break;
            case "REAPAMOUNT":
                ParseRange(arg, out int rmin, out int rmax);
                ReapAmountMin = rmin;
                ReapAmountMax = rmax;
                break;
            case "REGEN":
                Regen = EvalSimpleExpression(arg);
                break;
            case "SKILL":
                ParseFloatRange(arg, out int smin, out int smax);
                SkillMin = smin;
                SkillMax = smax;
                break;
            case "DEFNAME":
                base.DefName = arg.Trim();
                break;
        }
    }

    private static void ParseRange(string val, out int min, out int max)
    {
        min = 0; max = 0;
        var parts = val.Split(',');
        if (parts.Length >= 1) int.TryParse(parts[0].Trim(), out min);
        if (parts.Length >= 2) int.TryParse(parts[1].Trim(), out max);
        else max = min;
    }

    /// <summary>Parse float range and convert to tenths (e.g. "1.0,100.0" → 10,1000).</summary>
    private static void ParseFloatRange(string val, out int min, out int max)
    {
        min = 0; max = 0;
        var parts = val.Split(',');
        if (parts.Length >= 1)
        {
            if (double.TryParse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture, out double d))
                min = (int)Math.Round(d * 10);
            else if (int.TryParse(parts[0].Trim(), out int i))
                min = i;
        }
        if (parts.Length >= 2)
        {
            if (double.TryParse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out double d))
                max = (int)Math.Round(d * 10);
            else if (int.TryParse(parts[1].Trim(), out int i))
                max = i;
        }
        else max = min;
    }

    /// <summary>Evaluate simple arithmetic expressions like "60*60*10" → 36000.</summary>
    private static int EvalSimpleExpression(string val)
    {
        val = val.Trim();
        if (val.Contains('*'))
        {
            long result = 1;
            foreach (var part in val.Split('*'))
            {
                if (long.TryParse(part.Trim(), out long v))
                    result *= v;
                else
                    return 0;
            }
            return (int)Math.Min(result, int.MaxValue);
        }
        return int.TryParse(val, out int simple) ? simple : 0;
    }

    private static ushort ParseHexOrDec(string val)
    {
        val = val.Trim();
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ushort.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hex))
                return hex;
        }
        else if (val.StartsWith('0') && val.Length > 1)
        {
            if (ushort.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out ushort hex))
                return hex;
        }
        if (ushort.TryParse(val, out ushort dec))
            return dec;
        return 0;
    }
}
