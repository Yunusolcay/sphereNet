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
    private readonly List<int> _amountCurve = [];
    public IReadOnlyList<int> AmountCurve => _amountCurve;

    /// <summary>The BASEID of the item produced when gathered.</summary>
    public ushort Reap { get; set; }

    /// <summary>Raw REAP value from script (defname like "i_ore_iron"). Resolved post-load.</summary>
    public string? ReapRaw { get; set; }

    /// <summary>Amount of items yielded per successful gather.</summary>
    public int ReapAmountMin { get; set; } = 1;
    public int ReapAmountMax { get; set; } = 1;
    private readonly List<int> _reapAmountCurve = [];
    public IReadOnlyList<int> ReapAmountCurve => _reapAmountCurve;

    private readonly List<int> _regenCurve = [];
    public IReadOnlyList<int> RegenCurve => _regenCurve;

    /// <summary>Regeneration time in TENTHS of a second - the first point of the
    /// REGEN curve. Source-X loads REGEN as a value curve whose own comment reads
    /// "Tenths of second once found how long to regen this type"
    /// (CRegionResourceDef.cpp:73), and turns a sample into a marker timeout with
    /// GetRandom() * MSECS_PER_TENTH (CWorldMap.cpp:148). Reading it as whole
    /// seconds made every vein last ten times as long as its script asked for.
    /// The reference pack's own REGEN=60*60*10 reads as an hour in tenths.</summary>
    public int Regen { get; set; }

    /// <summary>Last point of the REGEN curve; equals <see cref="Regen"/> for the
    /// single-valued form.</summary>
    public int RegenMax { get; set; }

    /// <summary>Skill difficulty range (in tenths: 0-1000).</summary>
    public int SkillMin { get; set; }
    public int SkillMax { get; set; }
    private readonly List<int> _skillCurve = [];
    /// <summary>Source-X CValueCurveDef points, preserved in script order.</summary>
    public IReadOnlyList<int> SkillCurve => _skillCurve;

    public RegionResourceDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string arg)
    {
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "AMOUNT":
                ParseIntegerCurve(arg, _amountCurve);
                AmountMin = _amountCurve.Count > 0 ? _amountCurve[0] : 0;
                AmountMax = _amountCurve.Count > 0 ? _amountCurve[^1] : AmountMin;
                break;
            case "REAP":
                Reap = ParseHexOrDec(arg);
                if (Reap == 0)
                    ReapRaw = arg.Trim();
                break;
            case "REAPAMOUNT":
                ParseIntegerCurve(arg, _reapAmountCurve);
                ReapAmountMin = _reapAmountCurve.Count > 0 ? _reapAmountCurve[0] : 1;
                ReapAmountMax = _reapAmountCurve.Count > 0 ? _reapAmountCurve[^1] : ReapAmountMin;
                break;
            case "REGEN":
                ParseExpressionCurve(arg, _regenCurve);
                Regen = _regenCurve.Count > 0 ? _regenCurve[0] : 0;
                RegenMax = _regenCurve.Count > 0 ? _regenCurve[^1] : Regen;
                break;
            case "SKILL":
                ParseFloatCurve(arg, _skillCurve);
                SkillMin = _skillCurve.Count > 0 ? _skillCurve[0] : 0;
                SkillMax = _skillCurve.Count > 0 ? _skillCurve[^1] : SkillMin;
                break;
            case "DEFNAME":
                base.DefName = arg.Trim();
                break;
        }
    }

    /// <summary>Source-X m_vcSkill.GetRandom()/10. A random 0..999 sample is
    /// evaluated across the resource's full value curve.</summary>
    public int GetRandomSkillDifficulty(Random random) =>
        GetSkillDifficultyAt(random.Next(1000));

    internal int GetSkillDifficultyAt(int samplePermille)
    {
        int sample = Math.Clamp(samplePermille, 0, 999);
        IReadOnlyList<int> points = _skillCurve.Count > 0
            ? _skillCurve
            : SkillMin == SkillMax ? [SkillMin] : [SkillMin, SkillMax];
        return EvaluateCurve(points, sample) / 10;
    }

    /// <summary>Source-X m_vcAmount.GetRandom for a newly-created resource node.</summary>
    public int GetRandomAmount(Random random)
    {
        IReadOnlyList<int> points = _amountCurve.Count > 0
            ? _amountCurve
            : AmountMin == AmountMax ? [AmountMin] : [AmountMin, AmountMax];
        return EvaluateCurve(points, random.Next(1000));
    }

    /// <summary>Source-X natural-resource yield: REAPAMOUNT.GetRandomLinear(skill),
    /// falling back to AMOUNT.GetRandomLinear(skill)/2 when it yields zero.</summary>
    public int GetRandomReapAmount(int skillValue, Random random)
    {
        int amount = 0;
        if (_reapAmountCurve.Count > 0)
            amount = GetRandomLinear(_reapAmountCurve, skillValue, random);
        if (amount <= 0)
        {
            IReadOnlyList<int> points = _amountCurve.Count > 0
                ? _amountCurve
                : AmountMin == AmountMax ? [AmountMin] : [AmountMin, AmountMax];
            amount = GetRandomLinear(points, skillValue, random) / 2;
        }
        return Math.Max(1, amount);
    }

    private static int GetRandomLinear(IReadOnlyList<int> points, int skillValue, Random random) =>
        (EvaluateCurve(points, Math.Max(0, skillValue)) +
         EvaluateCurve(points, random.Next(1000))) / 2;

    private static int EvaluateCurve(IReadOnlyList<int> points, int samplePermille)
    {
        if (points.Count == 0) return 0;
        if (points.Count == 1) return Math.Max(0, points[0]);

        int lowIndex;
        int segmentSize;
        int segmentSample = Math.Max(0, samplePermille);
        if (points.Count == 2)
        {
            lowIndex = 0;
            segmentSize = 1000;
        }
        else if (points.Count == 3)
        {
            lowIndex = segmentSample >= 500 ? 1 : 0;
            if (lowIndex == 1) segmentSample -= 500;
            segmentSize = 500;
        }
        else
        {
            lowIndex = segmentSample * points.Count / 1000;
            int lastSegment = points.Count - 2;
            if (lowIndex > lastSegment) lowIndex = lastSegment;
            segmentSize = 1000 / (points.Count - 1);
            segmentSample -= lowIndex * segmentSize;
        }

        long low = points[lowIndex];
        long high = points[lowIndex + 1];
        long value = low + (high - low) * segmentSample / segmentSize;
        return (int)Math.Clamp(value, 0L, int.MaxValue);
    }

    /// <summary>Source-X m_vcRegenerateTime.GetRandom() - a random sample across
    /// the whole REGEN curve, in tenths of a second. A comma-separated REGEN used to
    /// parse as a single expression and collapse to zero, which then fell through to
    /// an invented default.</summary>
    public int GetRandomRegen(Random random)
    {
        IReadOnlyList<int> points = _regenCurve.Count > 0
            ? _regenCurve
            : Regen == RegenMax ? [Regen] : [Regen, RegenMax];
        return EvaluateCurve(points, random.Next(1000));
    }

    /// <summary>A value curve whose points may each be a simple expression - REGEN
    /// is written as 60*60*10 in the reference pack, which the plain integer parser
    /// cannot read.</summary>
    private static void ParseExpressionCurve(string val, List<int> destination)
    {
        destination.Clear();
        foreach (string raw in val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            destination.Add(EvalSimpleExpression(raw));
    }

    private static void ParseIntegerCurve(string val, List<int> destination)
    {
        destination.Clear();
        foreach (string raw in val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(raw, out int parsed))
                destination.Add(parsed);
        }
    }

    private static void ParseFloatCurve(string val, List<int> destination)
    {
        destination.Clear();
        foreach (string raw in val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                destination.Add((int)Math.Round(parsed * 10));
            else if (int.TryParse(raw, out int integer))
                destination.Add(integer);
        }
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
