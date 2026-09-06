namespace SphereNet.Core.Interfaces;

/// <summary>
/// Trigger arguments interface. Maps to CScriptTriggerArgs in Source-X.
/// </summary>
public interface ITriggerArgs
{
    IScriptObj? Source { get; }
    IScriptObj? Object1 { get; }
    IScriptObj? Object2 { get; }
    // Source-X CScriptTriggerArgs keeps ARGN1/2/3 as int64 (CScriptTriggerArgs.h:21).
    // These were int, and InitFromRaw saturated a larger script value at
    // int.MinValue/MaxValue, so a big counter or timestamp reached the script as a
    // different number.
    long Number1 { get; set; }
    long Number2 { get; set; }
    long Number3 { get; set; }
    string ArgString { get; set; }
}
