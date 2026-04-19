namespace SphereNet.Scripting.Variables;

/// <summary>
/// Global expression variables. Maps to CExprGlobals / g_ExprGlobals in Source-X.
/// Holds VarDefs, VarGlobals, ListGlobals, and default messages.
/// </summary>
public sealed class ExpressionGlobals
{
    /// <summary>Resource DEFNAMEs resolved at load time (numeric constants).</summary>
    public VarMap VarResDefs { get; } = new();

    /// <summary>Runtime global variables (VARs).</summary>
    public VarMap VarDefs { get; } = new();

    /// <summary>Script-accessible global variables.</summary>
    public VarMap VarGlobals { get; } = new();

    /// <summary>Global named lists.</summary>
    public ListMap ListGlobals { get; } = new();

    /// <summary>Internal engine lists.</summary>
    public ListMap ListInternals { get; } = new();

    /// <summary>Default messages (from defmessages.tbl).</summary>
    public Dictionary<string, string> DefMessages { get; } = new(StringComparer.OrdinalIgnoreCase);
}
