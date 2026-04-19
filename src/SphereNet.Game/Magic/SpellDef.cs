using SphereNet.Core.Enums;

namespace SphereNet.Game.Magic;

/// <summary>
/// Spell definition. Maps to CSpellDef in Source-X CSpellDef.h.
/// Loaded from [SPELL ...] sections in scripts.
/// </summary>
public sealed class SpellDef
{
    public SpellType Id { get; init; }
    public string Name { get; set; } = "";
    public SpellFlag Flags { get; set; }
    public ushort ManaCost { get; set; }
    public ushort TithingCost { get; set; }
    public int Sound { get; set; }
    public string Runes { get; set; } = "";
    public string TargetPrompt { get; set; } = "";
    public ushort EffectId { get; set; }
    public ushort RuneItemId { get; set; }
    public ushort ScrollItemId { get; set; }
    public Layer Layer { get; set; }

    // Curves (linear interpolation between two endpoints based on skill 0-1000).
    // Sphere / Source-X convention for "A,B": A is the value at 0 skill, B is
    // the value at maximum skill (1000 = 100.0). The "Base" / "Scale" field
    // names here are historical — Scale is the TOP value (endpoint at max
    // skill), NOT a delta added to Base. GetEffect / GetDuration interpolate
    // linearly between Base and Scale.
    public int CastTimeBase { get; set; } = 15; // tenths of a second
    public int EffectBase { get; set; }
    public int EffectScale { get; set; }         // value at max skill (1000)
    public int DurationBase { get; set; }         // tenths of a second at 0 skill
    public int DurationScale { get; set; }        // tenths of a second at max skill
    public int InterruptChance { get; set; } = 50;
    public ulong Group { get; set; }

    // Reagents (resource ID → amount)
    public Dictionary<ushort, int> Reagents { get; } = [];

    // Skill requirements (SkillType → minimum value)
    public Dictionary<SkillType, int> SkillReq { get; } = [];

    /// <summary>Get the primary casting skill for this spell.</summary>
    public SkillType GetPrimarySkill()
    {
        foreach (var kv in SkillReq)
            return kv.Key;
        return SkillType.Magery;
    }

    /// <summary>Get primary skill difficulty (0-1000).</summary>
    public int GetDifficulty()
    {
        foreach (var kv in SkillReq)
            return kv.Value;
        return 0;
    }

    /// <summary>Effect strength at given skill level (0-1000). Linear
    /// interpolation between EffectBase (at 0) and EffectScale (at 1000).
    /// Matches Source-X CValueCurveDef::GetLinear for a 2-endpoint curve:
    /// <c>base + (top - base) * skill / 1000</c>.</summary>
    public int GetEffect(int skillLevel) =>
        EffectBase + ((EffectScale - EffectBase) * skillLevel / 1000);

    /// <summary>Duration in tenths of a second at given skill level
    /// (0-1000). Linear interpolation between DurationBase (at 0 skill)
    /// and DurationScale (at max skill), matching Source-X convention.</summary>
    public int GetDuration(int skillLevel) =>
        DurationBase + ((DurationScale - DurationBase) * skillLevel / 1000);

    /// <summary>Get cast time at given skill level, in tenths of a second.</summary>
    public int GetCastTime(int skillLevel) =>
        Math.Max(1, CastTimeBase);

    public bool IsFlag(SpellFlag flag) => (Flags & flag) != 0;

    /// <summary>Read a property by key (for script access).</summary>
    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "NAME": value = Name; return true;
            case "FLAGS": value = ((ulong)Flags).ToString(); return true;
            case "GROUP": value = Group.ToString(); return true;
            case "MANAUSE": value = ManaCost.ToString(); return true;
            case "SOUND": value = Sound.ToString(); return true;
            case "RUNES": value = Runes; return true;
            case "PROMPT_MSG": value = TargetPrompt; return true;
            case "EFFECT_ID": value = $"0{EffectId:X}"; return true;
            case "RUNE_ITEM": value = $"0{RuneItemId:X}"; return true;
            case "SCROLL_ITEM": value = $"0{ScrollItemId:X}"; return true;
            case "CAST_TIME": value = CastTimeBase.ToString(); return true;
            case "EFFECT": value = EffectScale != 0 ? $"{EffectBase},{EffectScale}" : EffectBase.ToString(); return true;
            case "DURATION": value = DurationScale != 0 ? $"{DurationBase},{DurationScale}" : DurationBase.ToString(); return true;
            case "INTERRUPT": value = InterruptChance.ToString(); return true;
        }

        // RESOURCES.n.KEY / RESOURCES.n.VAL
        if (upper.StartsWith("RESOURCES.", StringComparison.Ordinal))
        {
            var rest = upper[10..]; // "n.KEY" or "n.VAL"
            int dot = rest.IndexOf('.');
            if (dot > 0 && int.TryParse(rest[..dot], out int idx))
            {
                string sub = rest[(dot + 1)..];
                int i = 0;
                foreach (var kv in Reagents)
                {
                    if (i == idx)
                    {
                        value = sub == "KEY" ? $"0{kv.Key:X}" : kv.Value.ToString();
                        return true;
                    }
                    i++;
                }
            }
            return true;
        }

        return false;
    }

    /// <summary>Get the circle (1-8) for magery spells.</summary>
    public int GetCircle() => Id switch
    {
        >= SpellType.Clumsy and <= SpellType.Weaken => 1,
        >= SpellType.Agility and <= SpellType.Strength => 2,
        >= SpellType.Bless and <= SpellType.WallOfStone => 3,
        >= SpellType.ArchCure and <= SpellType.Recall => 4,
        >= SpellType.BladeSpirit and <= SpellType.SummonCreature => 5,
        >= SpellType.Dispel and <= SpellType.Reveal => 6,
        >= SpellType.ChainLightning and <= SpellType.Polymorph => 7,
        >= SpellType.Earthquake and <= SpellType.WaterElemental => 8,
        _ => 0,
    };
}
