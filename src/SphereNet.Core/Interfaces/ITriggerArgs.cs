namespace SphereNet.Core.Interfaces;

/// <summary>
/// Trigger arguments interface. Maps to CScriptTriggerArgs in Source-X.
/// </summary>
public interface ITriggerArgs
{
    IScriptObj? Source { get; }
    IScriptObj? Object1 { get; }
    IScriptObj? Object2 { get; }
    int Number1 { get; set; }
    int Number2 { get; set; }
    int Number3 { get; set; }
    string ArgString { get; set; }
}
