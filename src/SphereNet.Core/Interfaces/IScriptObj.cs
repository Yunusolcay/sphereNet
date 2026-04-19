using SphereNet.Core.Enums;

namespace SphereNet.Core.Interfaces;

/// <summary>
/// Scriptable object interface. Maps to CScriptObj in Source-X.
/// All objects that can participate in script evaluation implement this.
/// </summary>
public interface IScriptObj
{
    string GetName();

    /// <summary>
    /// Read a script property value. Maps to r_WriteVal.
    /// </summary>
    bool TryGetProperty(string key, out string value);

    /// <summary>
    /// Execute a script verb/command. Maps to r_Verb.
    /// </summary>
    bool TryExecuteCommand(string key, string args, ITextConsole source);

    /// <summary>
    /// Load/set a script property value. Maps to r_LoadVal.
    /// </summary>
    bool TrySetProperty(string key, string value);

    /// <summary>
    /// Execute a trigger on this object.
    /// </summary>
    TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args);
}
