namespace SphereNet.Game.Macro;

public enum MacroStepType : byte
{
    UseObject,
    UseSkill,
    TargetLocation,
    TargetObject,
    TargetSelf,
}

public sealed class MacroStep
{
    public MacroStepType Type { get; init; }
    public int DelayMs { get; init; }
    public ushort ItemDispId { get; init; }
    public int SkillId { get; init; }
    public short X { get; init; }
    public short Y { get; init; }
    public sbyte Z { get; init; }
    public ushort Graphic { get; init; }
    public uint Serial { get; init; }
}

public sealed class MacroSession
{
    public List<MacroStep> Steps { get; } = [];

    public string Describe()
    {
        int use = 0, tgt = 0, skill = 0;
        foreach (var s in Steps)
        {
            switch (s.Type)
            {
                case MacroStepType.UseObject: use++; break;
                case MacroStepType.UseSkill: skill++; break;
                default: tgt++; break;
            }
        }
        return $"{Steps.Count} steps ({use} use, {tgt} target, {skill} skill)";
    }
}
