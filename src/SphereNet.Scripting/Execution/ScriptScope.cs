namespace SphereNet.Scripting.Execution;

/// <summary>
/// Script execution scope. Tracks LOCAL variables and control flow state.
/// Each trigger/function invocation gets its own scope.
/// </summary>
public sealed class ScriptScope
{
    public Variables.VarMap LocalVars { get; } = new();
    public string? ReturnValue { get; set; }
    public bool IsReturning { get; set; }
    public int LoopDepth { get; set; }

    /// <summary>Optional human-readable label for the current trigger or
    /// function (e.g. "@Click", "@Create", "f_doSomething"). Surfaced by
    /// scriptdebug warnings so the unresolved-variable message points at
    /// the right block, not just the file/line. Set by the trigger runner
    /// or dialog dispatcher; safe to leave null.</summary>
    public string? TriggerName { get; set; }

    /// <summary>
    /// Maximum nested loop depth to prevent infinite loops.
    /// Maps to MaxLoopTimes in sphere.ini.
    /// </summary>
    public int MaxLoopIterations { get; set; } = 512;

    /// <summary>
    /// REF object references (REF1..REFn). Stores UID strings.
    /// Maps to local REF variables in Source-X — local scope, doesn't
    /// interfere with other scripts (unlike global OBJ).
    /// </summary>
    private Dictionary<int, string>? _refs;

    public string GetRef(int index) =>
        _refs != null && _refs.TryGetValue(index, out string? v) ? v : "0";

    public void SetRef(int index, string value)
    {
        _refs ??= [];
        if (string.IsNullOrEmpty(value) || value == "0")
            _refs.Remove(index);
        else
            _refs[index] = value;
    }

    /// <summary>
    /// FLOAT variables (FLOAT.name). Stored as strings with decimal point.
    /// </summary>
    private Dictionary<string, string>? _floats;

    public string GetFloat(string name) =>
        _floats != null && _floats.TryGetValue(name, out string? v) ? v : "0.0";

    public void SetFloat(string name, string value)
    {
        _floats ??= new(StringComparer.OrdinalIgnoreCase);
        _floats[name] = value;
    }
}
