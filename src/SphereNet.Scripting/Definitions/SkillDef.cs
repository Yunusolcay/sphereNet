using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Skill definition. Maps to CSkillDef in Source-X.
/// Loaded from [SKILL] sections.
/// </summary>
public sealed class SkillDef : ResourceLink
{
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public int AdvRate { get; set; }
    public int Delay { get; set; }
    public int StatStr { get; set; }
    public int StatDex { get; set; }
    public int StatInt { get; set; }
    public int GainRadius { get; set; }
    public int Flags { get; set; }
    public int BonusStr { get; set; }
    public int BonusDex { get; set; }
    public int BonusInt { get; set; }
    public int BonusStats { get; set; }
    public int Group { get; set; }
    public int Effect { get; set; }
    public string PromptMsg { get; set; } = "";
    public int Values { get; set; }

    public SkillDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "NAME": Name = value; break;
            case "TITLE": Title = value; break;
            case "DEFNAME":
            case "KEY": DefName = value; break;
            case "ADV_RATE": int.TryParse(value, out int ar); AdvRate = ar; break;
            case "DELAY": int.TryParse(value, out int d); Delay = d; break;
            case "STAT_STR": int.TryParse(value, out int ss); StatStr = ss; break;
            case "STAT_DEX": int.TryParse(value, out int sd); StatDex = sd; break;
            case "STAT_INT": int.TryParse(value, out int si); StatInt = si; break;
            case "GAINRADIUS": int.TryParse(value, out int gr); GainRadius = gr; break;
            case "FLAGS": int.TryParse(value, out int f); Flags = f; break;
            case "BONUS_STR": int.TryParse(value, out int bs); BonusStr = bs; break;
            case "BONUS_DEX": int.TryParse(value, out int bd); BonusDex = bd; break;
            case "BONUS_INT": int.TryParse(value, out int bi); BonusInt = bi; break;
            case "BONUS_STATS": int.TryParse(value, out int bst); BonusStats = bst; break;
            case "GROUP": int.TryParse(value, out int g); Group = g; break;
            case "EFFECT": int.TryParse(value, out int e); Effect = e; break;
            case "PROMPT_MSG": PromptMsg = value; break;
            case "VALUES": int.TryParse(value, out int v); Values = v; break;
        }
    }
}
