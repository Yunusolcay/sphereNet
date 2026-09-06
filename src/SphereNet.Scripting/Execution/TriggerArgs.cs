using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Trigger arguments container. Maps to CScriptTriggerArgs in Source-X.
/// </summary>
public sealed class TriggerArgs : ITriggerArgs
{
    private string _argString = "";
    private string[]? _argvCache;

    public IScriptObj? Source { get; set; }
    public IScriptObj? Object1 { get; set; }
    public IScriptObj? Object2 { get; set; }
    public int Number1 { get; set; }
    public int Number2 { get; set; }
    public int Number3 { get; set; }
    public string ArgString
    {
        get => _argString;
        set
        {
            _argString = value ?? "";
            _argvCache = null;
        }
    }

    /// <summary>Shared LOCAL.* pool for the whole trigger chain (Source-X
    /// CScriptTriggerArgs.m_VarsLocal). When set, every trigger block run with
    /// these args uses this map as its scope locals instead of a fresh one —
    /// the engine can seed values (e.g. @SpellEffectTick LOCAL.EFFECT) and
    /// read script writes back after the fire. Functions still get their own
    /// locals (Source-X function-call semantics).</summary>
    public Variables.VarMap? SharedLocals { get; set; }

    public TriggerArgs() { }

    public TriggerArgs(IScriptObj? source, int n1 = 0, int n2 = 0, string argStr = "")
    {
        Source = source;
        Number1 = n1;
        Number2 = n2;
        ArgString = argStr;
    }

    /// <summary>Set the argument string the way a script call does, filling in the
    /// numeric ARGN1/2/3 fields from its leading numbers (Source-X
    /// CScriptTriggerArgs::Init, CScriptTriggerArgs.cpp:112). A call that only
    /// assigned <see cref="ArgString"/> left ARGN1 at zero, so a delayed
    /// <c>TIMERF 5, f_give 30</c> handed the script an amount of nothing.
    ///
    /// Upstream only looks for numbers when the string STARTS with one (a digit, or a
    /// minus followed by a digit); "hello 5" leaves all three at zero. Each further
    /// number must follow an argument separator (comma or whitespace).</summary>
    public void InitFromRaw(string? raw)
    {
        ArgString = raw ?? "";
        Number1 = 0;
        Number2 = 0;
        Number3 = 0;

        string s = _argString;
        int i = 0;
        if (!StartsWithNumber(s, i))
            return;
        for (int slot = 1; slot <= 3; slot++)
        {
            if (!StartsWithNumber(s, i) || !TryReadNumber(s, ref i, out long value))
                return;
            int truncated = value > int.MaxValue ? int.MaxValue
                          : value < int.MinValue ? int.MinValue
                          : (int)value;
            if (slot == 1) Number1 = truncated;
            else if (slot == 2) Number2 = truncated;
            else Number3 = truncated;
            // Skip one argument separator, the way SKIP_ARGSEP does.
            while (i < s.Length && (s[i] == ',' || char.IsWhiteSpace(s[i]))) i++;
        }
    }

    private static bool StartsWithNumber(string s, int i) =>
        i < s.Length &&
        (char.IsAsciiDigit(s[i]) || (s[i] == '-' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])));

    private static bool TryReadNumber(string s, ref int i, out long value)
    {
        value = 0;
        int start = i;
        if (i < s.Length && s[i] == '-') i++;
        int digitStart = i;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        if (i == digitStart)
        {
            i = start;
            return false;
        }
        // A leading zero means hex in Sphere script (0A = 10), matching the number
        // reading the rest of the engine does.
        var span = s.AsSpan(digitStart, i - digitStart);
        bool ok = span.Length > 1 && span[0] == '0'
            ? long.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out value)
            : long.TryParse(span, out value);
        if (!ok)
        {
            i = start;
            return false;
        }
        if (s[start] == '-') value = -value;
        return true;
    }

    public IReadOnlyList<string> GetArgv() => _argvCache ??= SplitArgString(_argString);

    public int GetArgc() => GetArgv().Count;

    // Source-X CScriptTriggerArgs parses ARGV on COMMAS only (leading whitespace
    // per field is skipped, but spaces inside a field are preserved) — so a
    // multi-word field such as "0,0,960,400,Admin Panel" keeps ARGV[4] intact.
    // Splitting on spaces too would fragment every multi-word argument. Empty
    // fields are preserved (not dropped): "  ,,230,90,Pin" must keep ARGV[0..1]
    // as empty so the remaining indices stay aligned (Source-X keeps empty args).
    private static string[] SplitArgString(string argString) =>
        string.IsNullOrWhiteSpace(argString)
            ? []
            : argString.Split(',', StringSplitOptions.TrimEntries);
}
