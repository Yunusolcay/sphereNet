using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.Skills;

/// <summary>
/// Skill trigger types. Maps exactly to SKTRIG_TYPE in Source-X CSkillDef.h.
/// </summary>
public enum SkillTrigger
{
    Abort = 1,
    Fail = 2,
    Gain = 3,
    PreStart = 4,
    Select = 5,
    Start = 6,
    Stroke = 7,
    Success = 8,
    TargetCancel = 9,
    UseQuick = 10,
    Wait = 11,

    Qty = 12,
}

/// <summary>
/// Skill engine. Maps to CChar::Skill_* functions in Source-X CCharSkill.cpp.
/// Handles skill checks, gain, experience, caps.
/// </summary>
public static class SkillEngine
{
    private static readonly Random _rand = new();

    /// <summary>Callback fired when a skill gain occurs. Args: (Character, SkillType, newValue).</summary>
    public static Action<Character, SkillType, int>? OnSkillGain { get; set; }

    /// <summary>Callback fired when a stat gain occurs. Args: (Character, statIndex: 0=STR 1=DEX 2=INT, newValue).</summary>
    public static Action<Character, int, int>? OnStatGain { get; set; }

    /// <summary>Skill variance for S-curve calculation (Source-X: SKILL_VARIANCE = 100).</summary>
    private const int SkillVariance = 100;

    /// <summary>
    /// Check if a skill use succeeds. Maps to Skill_CheckSuccess.
    /// Does NOT award experience.
    /// </summary>
    public static bool CheckSuccess(Character ch, SkillType skill, int difficulty, bool useBellCurve = true)
    {
        if (ch.PrivLevel >= PrivLevel.GM && skill != SkillType.Parrying)
            return true;

        if (difficulty < 0)
            return false;

        int iDiff = difficulty * 10; // scale 0-100 to 0-1000
        int skillVal = ch.GetSkill(skill);

        int successChance;
        if (useBellCurve)
        {
            successChance = CalcSCurve(skillVal - iDiff, SkillVariance);
        }
        else
        {
            successChance = iDiff;
        }

        return successChance >= _rand.Next(1000);
    }

    /// <summary>
    /// Quick skill use: check + experience in one call.
    /// Maps to Skill_UseQuick in Source-X.
    /// </summary>
    public static bool UseQuick(Character ch, SkillType skill, int difficulty, bool allowGain = true, bool useBellCurve = true)
    {
        bool success = CheckSuccess(ch, skill, difficulty, useBellCurve);

        if (allowGain)
        {
            int expDiff = success ? difficulty : -difficulty;
            GainExperience(ch, skill, expDiff);
        }

        return success;
    }

    /// <summary>
    /// Award skill experience / potential gain.
    /// Maps to Skill_Experience in Source-X.
    /// </summary>
    public static void GainExperience(Character ch, SkillType skill, int difficulty)
    {
        if (ch.IsDead) return;
        if ((int)skill < 0 || (int)skill >= (int)SkillType.Qty)
            return;

        // Scale and clamp difficulty
        int iDiff = Math.Clamp(difficulty * 10, 1, 1000);

        int currentSkill = ch.GetSkill(skill);
        int skillMax = GetSkillMax(ch, skill);

        // Lock state: 0=Up (gain), 1=Down (decay only), 2=Locked (no change)
        byte lockState = ch.GetSkillLock(skill);
        if (lockState == 2)
            return;

        // Check total skill cap
        int totalSkill = GetSkillSum(ch);
        int totalMax = GetSkillSumMax(ch);
        if (totalSkill >= totalMax)
            iDiff = 0; // at skill cap

        // Gain radius check (task too easy) — use SkillDef value if available
        var def = DefinitionLoader.GetSkillDef((int)skill);
        int gainRadius = def?.GainRadius > 0 ? def.GainRadius : 300;
        if (gainRadius > 0 && (iDiff + gainRadius) < Math.Max(50, currentSkill))
            return; // task too easy for gain

        // Advance rate — use SkillDef AdvRate if available, else default curve
        int chance = def?.AdvRate > 0 ? CalcAdvanceRateFromDef(currentSkill, def.AdvRate) : CalcAdvanceRate(currentSkill);

        if (chance <= 0)
            return;

        int roll = _rand.Next(1000);

        if (currentSkill < skillMax && iDiff > 0)
        {
            // Try skill decay before gain
            if (roll * 3 <= chance * 4)
            {
                TrySkillDecay(ch, skill);
            }

            // Skill gain — only when lock state is Up (0)
            if (lockState == 0 && roll <= chance)
            {
                ch.SetSkill(skill, (ushort)(currentSkill + 1));
                OnSkillGain?.Invoke(ch, skill, currentSkill + 1);
            }
        }

        // Stat gains
        TryStatGain(ch, skill);
    }

    /// <summary>Configurable total skill cap, defaults to 7000 (700.0). Set from sphere.ini MAXBASESKILL.</summary>
    public static int SkillSumMaxOverride { get; set; } = 7000;

    /// <summary>Per-skill max override table (from SkillClassDef or config).</summary>
    public static Dictionary<SkillType, int> SkillMaxOverrides { get; } = [];

    /// <summary>
    /// Get max skill value for a character.
    /// Default 1000 (100.0) for players, adjustable.
    /// </summary>
    public static int GetSkillMax(Character ch, SkillType skill)
    {
        byte lockState = ch.GetSkillLock(skill);
        int current = ch.GetSkill(skill);
        int classMax = ResolveSkillCap(ch, skill);

        if (lockState == 1) // down
            return Math.Min(current, classMax);
        if (lockState == 2) // locked
            return current < classMax ? current : classMax;

        return classMax;
    }

    /// <summary>Get total of all skill values.</summary>
    public static int GetSkillSum(Character ch)
    {
        int sum = 0;
        for (int i = 0; i < (int)SkillType.Qty; i++)
            sum += ch.GetSkill((SkillType)i);
        return sum;
    }

    /// <summary>Get maximum total skill cap.</summary>
    public static int GetSkillSumMax(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.SkillSumMax > 0 ? cls.SkillSumMax : SkillSumMaxOverride;
    }

    /// <summary>
    /// Calculate advance rate. Higher skill = lower chance.
    /// Maps to ADV_RATE curve in Source-X SkillDef.
    /// </summary>
    private static int CalcAdvanceRate(int skillLevel)
    {
        // Simple inverse: easier to gain at low skill, harder at high
        if (skillLevel >= 1000) return 0;
        return Math.Max(1, (1000 - skillLevel) / 5);
    }

    /// <summary>
    /// S-curve calculation for bell-curve success checks.
    /// Maps to Calc_GetSCurve in Source-X.
    /// </summary>
    private static int CalcSCurve(int value, int variance)
    {
        if (variance <= 0) variance = 1;
        double x = (double)value / variance;
        double sCurve = 500.0 + 500.0 * Math.Tanh(x);
        return (int)sCurve;
    }

    /// <summary>Try to decay a random skill (for stat gain room).</summary>
    private static void TrySkillDecay(Character ch, SkillType excludeSkill)
    {
        int total = GetSkillSum(ch);
        int max = GetSkillSumMax(ch);
        if (total < max) return;

        // Find a skill set to "down" and reduce it
        for (int i = 0; i < (int)SkillType.Qty; i++)
        {
            var sk = (SkillType)i;
            if (sk == excludeSkill) continue;
            if (ch.GetSkillLock(sk) != 1) continue; // only decay "down" skills
            int val = ch.GetSkill(sk);
            if (val > 0)
            {
                ch.SetSkill(sk, (ushort)(val - 1));
                return;
            }
        }
    }

    /// <summary>
    /// Try to gain stats from skill use.
    /// Stats gain based on SkillDef stat mapping or hardcoded fallback.
    /// </summary>
    private static int GetStatLock(Character ch, int statIdx)
    {
        if (ch.TryGetTag($"STATLOCK_{statIdx}", out string? val) && int.TryParse(val, out int lock_))
            return lock_;
        return 0;
    }

    private static void TryStatGain(Character ch, SkillType skill)
    {
        var def = DefinitionLoader.GetSkillDef((int)skill);

        int statSum = ch.Str + ch.Dex + ch.Int;
        int statSumMax = ResolveStatSumCap(ch);
        if (statSum >= statSumMax) return;

        // Chance: SkillDef BonusStats or default 5%
        int bonusChance = def?.BonusStats > 0 ? def.BonusStats : 5;
        if (_rand.Next(100) >= bonusChance) return;

        // Use SkillDef bonus weights if available, else fallback to primary stat mapping
        if (def != null && (def.BonusStr > 0 || def.BonusDex > 0 || def.BonusInt > 0))
        {
            int total = def.BonusStr + def.BonusDex + def.BonusInt;
            if (total > 0)
            {
                int roll = _rand.Next(total);
                if (roll < def.BonusStr)
                {
                    if (GetStatLock(ch, 0) == 0 && ch.Str < ResolveStrCap(ch)) { ch.Str++; ch.MaxHits++; OnStatGain?.Invoke(ch, 0, ch.Str); }
                }
                else if (roll < def.BonusStr + def.BonusDex)
                {
                    if (GetStatLock(ch, 1) == 0 && ch.Dex < ResolveDexCap(ch)) { ch.Dex++; ch.MaxStam++; OnStatGain?.Invoke(ch, 1, ch.Dex); }
                }
                else
                {
                    if (GetStatLock(ch, 2) == 0 && ch.Int < ResolveIntCap(ch)) { ch.Int++; ch.MaxMana++; OnStatGain?.Invoke(ch, 2, ch.Int); }
                }
            }
        }
        else
        {
            // Fallback: use hardcoded stat target
            int statIdx = def != null ? GetSkillStatTargetFromDef(def) : GetSkillStatTarget(skill);
            if (statIdx < 0) return;
            if (GetStatLock(ch, statIdx) != 0) return;
            switch (statIdx)
            {
                case 0: if (ch.Str < ResolveStrCap(ch)) { ch.Str++; ch.MaxHits++; OnStatGain?.Invoke(ch, 0, ch.Str); } break;
                case 1: if (ch.Dex < ResolveDexCap(ch)) { ch.Dex++; ch.MaxStam++; OnStatGain?.Invoke(ch, 1, ch.Dex); } break;
                case 2: if (ch.Int < ResolveIntCap(ch)) { ch.Int++; ch.MaxMana++; OnStatGain?.Invoke(ch, 2, ch.Int); } break;
            }
        }
    }

    private static int ResolveSkillCap(Character ch, SkillType skill)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        if (cls != null && cls.SkillCaps.TryGetValue(skill, out int classCap) && classCap > 0)
            return classCap;
        if (SkillMaxOverrides.TryGetValue(skill, out int overrideMax))
            return overrideMax;
        return 1000;
    }

    private static int ResolveStatSumCap(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.StatSumMax > 0 ? cls.StatSumMax : 225;
    }

    private static int ResolveStrCap(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.StrMax > 0 ? cls.StrMax : 125;
    }

    private static int ResolveDexCap(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.DexMax > 0 ? cls.DexMax : 125;
    }

    private static int ResolveIntCap(Character ch)
    {
        var cls = DefinitionLoader.GetSkillClassDef(ch.SkillClass);
        return cls?.IntMax > 0 ? cls.IntMax : 125;
    }

    /// <summary>
    /// Calculate advance rate using SkillDef's AdvRate value as a multiplier.
    /// </summary>
    private static int CalcAdvanceRateFromDef(int skillLevel, int advRate)
    {
        if (skillLevel >= 1000) return 0;
        int baseChance = Math.Max(1, (1000 - skillLevel) / 5);
        return Math.Max(1, baseChance * advRate / 100);
    }

    /// <summary>
    /// Get stat index from SkillDef STAT_STR/STAT_DEX/STAT_INT values.
    /// Returns the stat with the highest weight, or -1 if none set.
    /// </summary>
    private static int GetSkillStatTargetFromDef(SkillDef def)
    {
        if (def.StatStr <= 0 && def.StatDex <= 0 && def.StatInt <= 0)
            return -1;
        if (def.StatStr >= def.StatDex && def.StatStr >= def.StatInt) return 0;
        if (def.StatDex >= def.StatStr && def.StatDex >= def.StatInt) return 1;
        return 2;
    }

    /// <summary>
    /// Get which stat index (0=STR, 1=DEX, 2=INT) a skill primarily trains.
    /// Hardcoded fallback mapping for standard skills.
    /// </summary>
    private static int GetSkillStatTarget(SkillType skill) => skill switch
    {
        SkillType.Swordsmanship or SkillType.MaceFighting or SkillType.Wrestling or
        SkillType.Mining or SkillType.Lumberjacking or SkillType.Blacksmithing => 0, // STR

        SkillType.Fencing or SkillType.Archery or SkillType.Throwing or
        SkillType.Hiding or SkillType.Stealth or SkillType.Lockpicking or
        SkillType.Snooping or SkillType.Stealing or SkillType.Musicianship => 1, // DEX

        SkillType.Magery or SkillType.EvalInt or SkillType.MagicResistance or
        SkillType.Meditation or SkillType.SpiritSpeak or SkillType.Inscription or
        SkillType.Necromancy or SkillType.Mysticism or SkillType.Spellweaving => 2, // INT

        _ => -1,
    };
}
