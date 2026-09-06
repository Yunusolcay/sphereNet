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

    /// <summary>Read one Sphere numeric TOKEN. A leading '0' means hexadecimal
    /// (<c>010</c> is 16, <c>0A</c> is 10) - it is a base marker, not a digit - and an
    /// explicit <c>0x</c> prefix means the same. Everything else is decimal, so
    /// <c>10</c> is ten. Reading a bare decimal as hex silently addressed a different
    /// object; reading Sphere hex as decimal silently changed a count.</summary>
    public static bool TryParseToken(string? token, out long value)
    {
        value = 0;
        string t = (token ?? "").Trim();
        if (t.Length == 0) return false;

        bool negative = t[0] == '-';
        if (negative) t = t[1..].Trim();
        if (t.Length == 0) return false;

        bool ok;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            ok = long.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        else if (t.Length > 1 && t[0] == '0')
            ok = long.TryParse(t.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out value);
        else
            ok = long.TryParse(t, out value);

        if (!ok) return false;
        if (negative) value = -value;
        return true;
    }

    /// <summary>Read a Sphere numeric ARGUMENT: a token, or a simple sum of them.
    /// Source-X arguments go through the expression parser (GetArgVal -&gt;
    /// Exp_GetVal, CScript.cpp:154), so <c>1+1</c> is two rather than a parse failure
    /// that quietly became a default. This covers the token-and-sum grammar the
    /// engine's own verb arguments use; a full expression belongs to the script
    /// interpreter, which resolves it before the verb ever sees it.</summary>
    public static bool TryParseArgument(string? text, out long value)
    {
        value = 0;
        string s = (text ?? "").Trim();
        if (s.Length == 0) return false;

        long total = 0;
        int i = 0, sign = 1;
        bool any = false;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && s[i] != '+' && !(i > start && s[i] == '-')) i++;
            string term = s[start..i].Trim();
            if (term.Length == 0) return false;
            if (!TryParseToken(term, out long termValue)) return false;
            total += sign * termValue;
            any = true;
            if (i < s.Length)
            {
                sign = s[i] == '-' ? -1 : 1;
                i++;
            }
        }
        if (!any) return false;
        value = total;
        return true;
    }
}
