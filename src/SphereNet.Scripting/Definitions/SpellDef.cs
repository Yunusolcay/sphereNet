using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Spell definition. Maps to CSpellDef in Source-X.
/// Loaded from [SPELL] sections.
/// </summary>
public sealed class SpellDef : ResourceLink
{
    public string Name { get; set; } = "";
    public string Runes { get; set; } = "";
    public int ManaCost { get; set; }
    public int CastTime { get; set; }
    public int Effect { get; set; }
    public int Sound { get; set; }
    public int Flags { get; set; }
    public int SkillRequired { get; set; }
    public string Reagents { get; set; } = "";

    public SpellDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "NAME": Name = value; break;
            case "RUNES":
                Runes = value.StartsWith('.') ? value[1..] : value;
                break;
            case "MANAUSE": int.TryParse(value, out int m); ManaCost = m; break;
            case "CASTTIME": int.TryParse(value, out int ct); CastTime = ct; break;
            case "EFFECT_ID": int.TryParse(value, out int e); Effect = e; break;
            case "SOUND": int.TryParse(value, out int s); Sound = s; break;
            case "FLAGS": int.TryParse(value, out int f); Flags = f; break;
            case "SKILLREQ": int.TryParse(value, out int sr); SkillRequired = sr; break;
            case "REAGENTS": Reagents = value; break;
            case "DEFNAME": DefName = value; break;
        }
    }
}
