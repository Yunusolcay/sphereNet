namespace SphereNet.Scripting.Parsing;

/// <summary>
/// Splitting a command line into its key and its argument the way Source-X does.
///
/// Upstream stores a delayed command as one raw string and only splits it when the
/// timer fires: <c>CTimedFunction::OnTick</c> builds a <c>CScript</c> from it
/// (CTimedFunction.cpp:88), <c>CScriptKeyAlloc::ParseKey</c> runs
/// <c>Str_Parse</c> over the buffer (CScript.cpp:336), and <c>Str_Parse</c>'s
/// default separator set is <c>"=, \t"</c> (CExpression.cpp:144).
///
/// So <c>f_capture 37</c>, <c>f_capture=37</c> and <c>f_capture,37</c> all name the
/// same function with the same argument. Splitting on whitespace alone kept the
/// separator inside the name, and <c>f_capture=37</c> silently resolved to nothing.
///
/// Only the FIRST separator is structural: <c>Str_Parse</c> peels one argument off
/// and hands the whole remainder over untouched, so <c>f_a 1,2,3</c> keeps
/// <c>1,2,3</c> as one argument string.
/// </summary>
public static class ScriptCommandLine
{
    /// <summary>Source-X <c>Str_Parse</c> default separators (CExpression.cpp:144).</summary>
    private static bool IsSeparator(char c) => c is '=' or ',' or ' ' or '\t';

    /// <summary>Split <paramref name="line"/> at its first unquoted, unbracketed
    /// separator. <paramref name="key"/> is the part before it, <paramref name="args"/>
    /// the remainder with leading whitespace removed. A line with no separator is all
    /// key and an empty argument.</summary>
    public static void Split(string? line, out string key, out string args)
    {
        key = "";
        args = "";
        if (string.IsNullOrEmpty(line))
            return;

        string s = line.TrimStart();
        if (s.Length == 0)
            return;

        // Upstream ignores separators inside quotes and inside bracket pairs, so a
        // quoted or bracketed argument survives intact (CExpression.cpp:150-210).
        bool inQuotes = false;
        int curly = 0, square = 0, round = 0, angle = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (inQuotes)
                continue;

            switch (c)
            {
                case '{': curly++; continue;
                case '}': if (curly > 0) curly--; continue;
                case '[': square++; continue;
                case ']': if (square > 0) square--; continue;
                case '(': round++; continue;
                case ')': if (round > 0) round--; continue;
                case '<': angle++; continue;
                case '>': if (angle > 0) angle--; continue;
            }

            if (curly > 0 || square > 0 || round > 0 || angle > 0)
                continue;

            if (IsSeparator(c))
            {
                key = s[..i].Trim();
                args = s[(i + 1)..].TrimStart();
                return;
            }
        }

        key = s.Trim();
    }
}
