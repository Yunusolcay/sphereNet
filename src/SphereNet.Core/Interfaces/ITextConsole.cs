using SphereNet.Core.Enums;

namespace SphereNet.Core.Interfaces;

/// <summary>
/// Text console interface. Maps to CTextConsole in Source-X.
/// Represents anything that can receive system messages (client, server console, etc.).
/// </summary>
public interface ITextConsole
{
    PrivLevel GetPrivLevel();
    void SysMessage(string text);
    void SysMessage(string text, ushort hue) => SysMessage(text);
    string GetName();

    /// <summary>
    /// Optional script bridge for Source-X style runtime verbs.
    /// Default implementation is no-op for non-game consoles.
    /// </summary>
    bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs) => false;

    /// <summary>
    /// Optional script variable resolver extension (e.g. ARGO.*, TARGP, SERV.*).
    /// Default implementation does nothing.
    /// </summary>
    bool TryResolveScriptVariable(string varName, IScriptObj target, ITriggerArgs? triggerArgs, out string value)
    {
        value = "";
        return false;
    }

    /// <summary>
    /// Optional object query for script loop verbs (FORPLAYERS / FORINSTANCES).
    /// </summary>
    IReadOnlyList<IScriptObj> QueryScriptObjects(string query, IScriptObj target, string args, ITriggerArgs? triggerArgs) =>
        Array.Empty<IScriptObj>();
}
