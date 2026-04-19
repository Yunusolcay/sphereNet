using System.Globalization;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Skill class definition. Maps to [SKILLCLASS] in Source-X.
/// Holds per-skill caps and stat caps used by SkillEngine.
/// </summary>
public sealed class SkillClassDef : ResourceLink
{
    public string Name { get; set; } = "";
    public int SkillSumMax { get; set; } = 7000; // 700.0
    public int StatSumMax { get; set; } = 225;
    public int StrMax { get; set; } = 125;
    public int DexMax { get; set; } = 125;
    public int IntMax { get; set; } = 125;

    public Dictionary<SkillType, int> SkillCaps { get; } = [];

    public SkillClassDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string value)
    {
        string upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "NAME":
                Name = value.Trim();
                return;
            case "DEFNAME":
                DefName = value.Trim();
                return;
            case "SKILLSUM":
            case "SKILLSUMMAX":
            case "MAXSKILLS":
            case "MAXBASESKILL":
                SkillSumMax = ParseSkillSumValue(value, 7000);
                return;
            case "STATSUM":
            case "STATSUMMAX":
            case "MAXSTATS":
                StatSumMax = ParseIntValue(value, 225);
                return;
            case "STR":
            case "MAXSTR":
                StrMax = ParseIntValue(value, 125);
                return;
            case "DEX":
            case "MAXDEX":
                DexMax = ParseIntValue(value, 125);
                return;
            case "INT":
            case "MAXINT":
                IntMax = ParseIntValue(value, 125);
                return;
        }

        if (TryParseSkillKey(key, out SkillType skill))
        {
            SkillCaps[skill] = ParseSkillCapValue(value, 1000);
        }
    }

    private static bool TryParseSkillKey(string key, out SkillType skill)
    {
        if (Enum.TryParse(key, true, out skill))
            return true;

        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
            idx >= 0 && idx < (int)SkillType.Qty)
        {
            skill = (SkillType)idx;
            return true;
        }

        skill = SkillType.None;
        return false;
    }

    private static int ParseSkillCapValue(string value, int fallback)
    {
        string v = value.Trim();
        if (v.Contains('.') &&
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dec))
            return (int)Math.Round(dec * 10d);
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
            return iv <= 200 ? iv * 10 : iv;
        return fallback;
    }

    private static int ParseSkillSumValue(string value, int fallback)
    {
        string v = value.Trim();
        if (v.Contains('.') &&
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double dec))
            return (int)Math.Round(dec * 10d);
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
            return iv <= 1000 ? iv * 10 : iv;
        return fallback;
    }

    private static int ParseIntValue(string value, int fallback)
    {
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
            return iv;
        return fallback;
    }
}
