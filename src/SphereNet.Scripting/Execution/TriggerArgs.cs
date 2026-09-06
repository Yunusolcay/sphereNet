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
    public long Number1 { get; set; }
    public long Number2 { get; set; }
    public long Number3 { get; set; }
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

    public TriggerArgs(IScriptObj? source, long n1 = 0, long n2 = 0, string argStr = "")
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
            if (slot == 1) Number1 = value;
            else if (slot == 2) Number2 = value;
            else Number3 = value;
            // Skip one argument separator, the way SKIP_ARGSEP does.
            while (i < s.Length && (s[i] == ',' || char.IsWhiteSpace(s[i]))) i++;
        }
    }

    private static bool StartsWithNumber(string s, int i) =>
        i < s.Length &&
        (char.IsAsciiDigit(s[i]) || (s[i] == '-' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])));

    /// <summary>Read one Sphere number, consuming exactly the characters it owns —
    /// the port of <c>CExpression::GetSingle</c> (CExpression.cpp:646).
    ///
    /// A leading '0' is the HEX MARKER, not a digit: after it the scan consumes
    /// <c>[0-9 A-F a-f]</c>. Scanning ASCII digits only stopped dead on the first
    /// letter, so <c>0A,2,3</c> read as 0 and then left the cursor parked on 'A',
    /// which killed ARGN2 and ARGN3 as well. Otherwise the number is decimal, and
    /// '.' inside it is a grouping separator upstream skips.</summary>
    private static bool TryReadNumber(string s, ref int i, out long value)
    {
        value = 0;
        int start = i;
        bool negative = i < s.Length && s[i] == '-';
        if (negative) i++;
        if (i >= s.Length || !char.IsAsciiDigit(s[i]))
        {
            i = start;
            return false;
        }

        if (s[i] == '0')
        {
            // HEX PATH. Consume the '0' marker, then every hex digit after it.
            i++;
            ulong acc = 0;
            int significant = 0;          // significant nibbles, upstream's uiSig
            bool seenNonZero = false;
            while (i < s.Length)
            {
                int nibble = HexValue(s[i]);
                if (nibble < 0) break;
                i++;
                uint digit = (uint)nibble;
                if (!seenNonZero)
                {
                    if (digit == 0) continue;   // leading zeros carry no width
                    seenNonZero = true;
                    significant = 1;
                    acc = digit;
                }
                else if (significant < 16)
                {
                    acc = (acc << 4) | digit;
                    significant++;
                }
                // Past 16 nibbles upstream flags overflow but keeps consuming the
                // token so the caller lands after it; the value is then unusable.
            }
            // "0", "0000" -> zero (:713).
            // Width decides the sign reinterpretation: up to 8 significant nibbles
            // is a signed 32-bit value widened to 64, beyond that a signed 64-bit
            // one (:724-740). That is what makes 0FFFFFFFF read as -1.
            value = !seenNonZero ? 0
                  : significant <= 8 ? unchecked((int)(uint)acc)
                  : unchecked((long)acc);
        }
        else
        {
            // DECIMAL PATH. '.' is a grouping separator and is skipped (:741).
            long acc = 0;
            bool any = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '.') { i++; continue; }
                if (!char.IsAsciiDigit(c)) break;
                i++;
                any = true;
                // Guard the accumulation but keep consuming, the way upstream does.
                if (acc <= (long.MaxValue - (c - '0')) / 10)
                    acc = acc * 10 + (c - '0');
            }
            if (!any)
            {
                i = start;
                return false;
            }
            value = acc;
        }

        if (negative) value = -value;
        return true;
    }

    private static int HexValue(char c) =>
        c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;

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
