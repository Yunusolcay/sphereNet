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
                break;
            case "REAPAMOUNT":
                ParseRange(arg, out int rmin, out int rmax);
                ReapAmountMin = rmin;
                ReapAmountMax = rmax;
                break;
            case "REGEN":
                if (int.TryParse(arg, out int regen))
                    Regen = regen;
                break;
            case "SKILL":
                ParseRange(arg, out int smin, out int smax);
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
