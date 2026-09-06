namespace SphereNet.Core.Types;

/// <summary>
/// The boundary between the script world's numbers and the engine's own fields.
///
/// Source-X carries ARGN1/2/3 as int64 (CScriptTriggerArgs.h:21) while most game
/// fields it feeds — damage, a skill id, a delay in tenths — are narrower. The
/// transport stays 64-bit; the narrowing happens HERE, at the field that needs it,
/// and saturates rather than wrapping, so an out-of-range script value becomes an
/// extreme instead of flipping sign.
/// </summary>
public static class ScriptNumber
{
    /// <summary>Narrow a script number to an engine <see cref="int"/> field,
    /// saturating at the bounds instead of wrapping.</summary>
    public static int ToEngineInt(long value) =>
        value >= int.MaxValue ? int.MaxValue :
        value <= int.MinValue ? int.MinValue :
        (int)value;
}
