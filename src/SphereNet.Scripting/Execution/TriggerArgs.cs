using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Trigger arguments container. Maps to CScriptTriggerArgs in Source-X.
/// </summary>
public sealed class TriggerArgs : ITriggerArgs
{
    public IScriptObj? Source { get; set; }
    public IScriptObj? Object1 { get; set; }
    public IScriptObj? Object2 { get; set; }
    public int Number1 { get; set; }
    public int Number2 { get; set; }
    public int Number3 { get; set; }
    public string ArgString { get; set; } = "";

    public TriggerArgs() { }

    public TriggerArgs(IScriptObj? source, int n1 = 0, int n2 = 0, string argStr = "")
    {
        Source = source;
        Number1 = n1;
        Number2 = n2;
        ArgString = argStr;
    }
}
