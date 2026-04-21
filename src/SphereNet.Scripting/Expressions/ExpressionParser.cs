namespace SphereNet.Scripting.Expressions;

/// <summary>
/// Expression evaluator. Maps to CExpression in Source-X.
/// Evaluates arithmetic, comparison, logical, and bitwise expressions.
/// Supports hex (0x), decimal, and variable references.
/// </summary>
public sealed class ExpressionParser
{
    /// <summary>
    /// Variable/property resolver callback.
    /// Given a variable name, returns its string value.
    /// </summary>
    public Func<string, string?>? VariableResolver { get; set; }

    /// <summary>
    /// Dialog-response accessor. Set while executing a [Dialog X Button] On=
    /// block so <c>&lt;ArgN&gt;</c>, <c>&lt;Argtxt[N]&gt;</c>, <c>&lt;Argchk[N]&gt;</c>
    /// and <c>&lt;ArgV&gt;</c>/<c>&lt;ArgV[N]&gt;</c> resolve against the real
    /// gump response. Null outside a button handler.
    /// </summary>
    public Func<string, string?>? DialogArgResolver { get; set; }

    /// <summary>When true, unresolved <c>&lt;X&gt;</c> expressions are reported
    /// via <see cref="DiagnosticLogger"/>. Default off so production logs stay
    /// quiet; flip on with the <c>.SCRIPTDEBUG</c> command while hunting
    /// missing properties in imported Sphere scripts.
    ///
    /// Performance note: leaving this on under load is OK for development but
    /// not recommended in production — every unresolved &lt;X&gt; pays a
    /// string-format + log write, and noisy script packs (region/NPC ticks)
    /// can produce thousands of warnings per second. Default stays
    /// <c>false</c> for that reason.</summary>
    public bool DebugUnresolved { get; set; }

    /// <summary>Callback for diagnostic messages (unknown variables, commands).
    /// Host wires this up to write to the server console / log.</summary>
    public Action<string>? DiagnosticLogger { get; set; }

    /// <summary>
    /// Per-thread "where am I" label used by <see cref="ReportUnresolved"/> when
    /// the caller doesn't pass an explicit context. ScriptInterpreter / dialog
    /// dispatcher / trigger runner push the current source location here before
    /// invoking expression evaluation, so unresolved warnings look like
    /// <c>[script] unresolved &lt;Eval&gt; (in d_admin.scp(412) @Click)</c>
    /// instead of the bare variable name. The field is thread-static so server
    /// tick threads and the network thread don't trample each other.
    /// </summary>
    [ThreadStatic]
    private static string? t_currentSourceLabel;

    /// <summary>Push a "where am I" label for the current thread. Returns a
    /// scope handle whose <c>Dispose</c> restores the previous label, so
    /// callers can use <c>using (parser.PushSourceLabel(...)) { ... }</c>
    /// without writing try/finally everywhere.</summary>
    public SourceLabelScope PushSourceLabel(string? label)
    {
        var prev = t_currentSourceLabel;
        t_currentSourceLabel = label;
        return new SourceLabelScope(prev);
    }

    /// <summary>Read-only view of the current thread's source label, or
    /// <c>null</c> if nothing has been pushed.</summary>
    public static string? CurrentSourceLabel => t_currentSourceLabel;

    public readonly struct SourceLabelScope : IDisposable
    {
        private readonly string? _previous;
        internal SourceLabelScope(string? previous) { _previous = previous; }
        public void Dispose() { t_currentSourceLabel = _previous; }
    }

    internal void ReportUnresolved(string varExpr, string context = "")
    {
        if (!DebugUnresolved || DiagnosticLogger == null) return;

        // Prefer the caller-supplied context, but fall back to whatever the
        // current ScriptInterpreter / dialog runner pushed for this thread.
        // This is what lets the warning name a concrete script + line even
        // for variables resolved deep inside nested <Eval>/<QVal> expansion.
        string ctx = !string.IsNullOrEmpty(context)
            ? context
            : (t_currentSourceLabel ?? "");

        string msg = string.IsNullOrEmpty(ctx)
            ? $"[script] unresolved <{varExpr}>"
            : $"[script] unresolved <{varExpr}> (in {ctx})";
        DiagnosticLogger(msg);
    }

    /// <summary>
    /// Evaluate a full expression string to a numeric value.
    /// Maps to CExpression::GetVal in Source-X.
    /// </summary>
    public long Evaluate(ReadOnlySpan<char> expr)
    {
        expr = expr.Trim();
        if (expr.IsEmpty) return 0;

        int pos = 0;
        string text = expr.ToString();
        return ParseExpression(text, ref pos);
    }

    /// <summary>
    /// Evaluate a full expression string to a string value.
    /// Resolves &lt;TAG&gt; substitutions first.
    /// </summary>
    public string EvaluateStr(string expr)
    {
        if (string.IsNullOrEmpty(expr)) return "";
        return ResolveAngleBrackets(expr);
    }

    private long ParseExpression(string text, ref int pos)
    {
        return ParseLogicalOr(text, ref pos);
    }

    private long ParseLogicalOr(string text, ref int pos)
    {
        long left = ParseLogicalAnd(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length - 1 && text[pos] == '|' && text[pos + 1] == '|')
        {
            pos += 2;
            long right = ParseLogicalAnd(text, ref pos);
            left = (left != 0 || right != 0) ? 1 : 0;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private long ParseLogicalAnd(string text, ref int pos)
    {
        long left = ParseBitwiseOr(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length - 1 && text[pos] == '&' && text[pos + 1] == '&')
        {
            pos += 2;
            long right = ParseBitwiseOr(text, ref pos);
            left = (left != 0 && right != 0) ? 1 : 0;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private long ParseBitwiseOr(string text, ref int pos)
    {
        long left = ParseBitwiseXor(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length && text[pos] == '|' &&
               (pos + 1 >= text.Length || text[pos + 1] != '|'))
        {
            pos++;
            long right = ParseBitwiseXor(text, ref pos);
            left |= right;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private long ParseBitwiseXor(string text, ref int pos)
    {
        long left = ParseBitwiseAnd(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length && text[pos] == '^')
        {
            pos++;
            long right = ParseBitwiseAnd(text, ref pos);
            left ^= right;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private long ParseBitwiseAnd(string text, ref int pos)
    {
        long left = ParseComparison(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length && text[pos] == '&' &&
               (pos + 1 >= text.Length || text[pos + 1] != '&'))
        {
            pos++;
            long right = ParseComparison(text, ref pos);
            left &= right;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private long ParseComparison(string text, ref int pos)
    {
        long left = ParseShift(text, ref pos);
        SkipWhitespace(text, ref pos);

        if (pos >= text.Length) return left;

        char c = text[pos];
        char c2 = (pos + 1 < text.Length) ? text[pos + 1] : '\0';

        if (c == '=' && c2 == '=') { pos += 2; long r = ParseShift(text, ref pos); return left == r ? 1 : 0; }
        if (c == '!' && c2 == '=') { pos += 2; long r = ParseShift(text, ref pos); return left != r ? 1 : 0; }
        if (c == '>' && c2 == '=') { pos += 2; long r = ParseShift(text, ref pos); return left >= r ? 1 : 0; }
        if (c == '<' && c2 == '=') { pos += 2; long r = ParseShift(text, ref pos); return left <= r ? 1 : 0; }
        if (c == '>' && c2 != '>') { pos++; long r = ParseShift(text, ref pos); return left > r ? 1 : 0; }
        if (c == '<' && c2 != '<') { pos++; long r = ParseShift(text, ref pos); return left < r ? 1 : 0; }

        return left;
    }

    private long ParseShift(string text, ref int pos)
    {
        long left = ParseAddSub(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length - 1)
        {
            if (text[pos] == '<' && text[pos + 1] == '<')
            {
                pos += 2;
                long right = ParseAddSub(text, ref pos);
                left <<= (int)right;
            }
            else if (text[pos] == '>' && text[pos + 1] == '>')
            {
                pos += 2;
                long right = ParseAddSub(text, ref pos);
                left >>= (int)right;
            }
            else break;

            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private long ParseAddSub(string text, ref int pos)
    {
        long left = ParseMulDiv(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '+') { pos++; left += ParseMulDiv(text, ref pos); }
            else if (c == '-') { pos++; left -= ParseMulDiv(text, ref pos); }
            else break;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private long ParseMulDiv(string text, ref int pos)
    {
        long left = ParseUnary(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '*') { pos++; left *= ParseUnary(text, ref pos); }
            else if (c == '/')
            {
                pos++;
                long r = ParseUnary(text, ref pos);
                left = r != 0 ? left / r : 0;
            }
            else if (c == '%')
            {
                pos++;
                long r = ParseUnary(text, ref pos);
                left = r != 0 ? left % r : 0;
            }
            else break;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private long ParseUnary(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length) return 0;

        char c = text[pos];
        if (c == '-') { pos++; return -ParseUnary(text, ref pos); }
        if (c == '!') { pos++; return ParseUnary(text, ref pos) == 0 ? 1 : 0; }
        if (c == '~') { pos++; return ~ParseUnary(text, ref pos); }
        if (c == '+') { pos++; return ParseUnary(text, ref pos); }

        return ParsePrimary(text, ref pos);
    }

    private long ParsePrimary(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length) return 0;

        // Parenthesized expression
        if (text[pos] == '(')
        {
            pos++;
            long val = ParseExpression(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == ')') pos++;
            return val;
        }

        // Angle bracket variable <...>
        if (text[pos] == '<')
        {
            string resolved = ReadAngleBracket(text, ref pos);
            string expanded = ResolveAngleBrackets(resolved);
            if (long.TryParse(expanded, out long v)) return v;

            // Try hex
            if (expanded.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(expanded.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out v))
                return v;

            return 0;
        }

        // Number literal
        return ReadNumber(text, ref pos);
    }

    private long ReadNumber(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length) return 0;

        int start = pos;
        bool isHex = false;

        if (pos + 1 < text.Length && text[pos] == '0' && (text[pos + 1] == 'x' || text[pos + 1] == 'X'))
        {
            isHex = true;
            pos += 2;
            while (pos < text.Length && IsHexDigit(text[pos])) pos++;
        }
        else
        {
            while (pos < text.Length && char.IsDigit(text[pos])) pos++;
        }

        if (pos == start) return 0;

        ReadOnlySpan<char> numText = text.AsSpan(start, pos - start);
        if (isHex && numText.Length > 2)
        {
            long.TryParse(numText[2..], System.Globalization.NumberStyles.HexNumber, null, out long val);
            return val;
        }

        long.TryParse(numText, out long result);
        return result;
    }

    private string ReadAngleBracket(string text, ref int pos)
    {
        if (pos >= text.Length || text[pos] != '<') return "";
        pos++; // skip '<'

        int depth = 1;
        int parenDepth = 0;
        int start = pos;
        while (pos < text.Length && depth > 0)
        {
            char c = text[pos];
            if (c == '(') parenDepth++;
            else if (c == ')' && parenDepth > 0) parenDepth--;
            else if (c == '<')
            {
                // Disambiguate '<' — same rule as the top-level
                // ResolveAngleBrackets walker: a bracket open needs an
                // identifier start (letter / '_') right after. "a < b"
                // keeps '<' literal, "a<foo>b" opens a nested bracket.
                char next = pos + 1 < text.Length ? text[pos + 1] : '\0';
                if (next == '_' || char.IsLetter(next))
                    depth++;
                // else: literal LT — pass through as content
            }
            else if (c == '>')
            {
                // Paren-depth GT-operator disambiguation only applies at
                // the outermost bracket (depth == 1). Inside a nested
                // <...>, a '>' always closes the nested bracket even if
                // that nested bracket is sitting inside parentheses —
                // otherwise <eval 62+(<local._for>*25)> swallows the
                // inner '>' and the outer read runs past the end.
                if (depth == 1 && parenDepth > 0)
                {
                    pos++;
                    continue;
                }
                depth--;
            }
            if (depth > 0) pos++;
        }

        string content = text[start..pos];
        if (pos < text.Length && text[pos] == '>') pos++;
        return content;
    }

    /// <summary>
    /// Resolve all &lt;...&gt; substitutions in a string.
    /// Maps to CExpression::ParseScriptText in Source-X.
    /// </summary>
    public string ResolveAngleBrackets(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('<'))
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        int pos = 0;

        while (pos < text.Length)
        {
            if (text[pos] == '<')
            {
                // Sphere scripts use '<' both as the opening of a
                // variable expansion (<Account.Plevel>) AND as the
                // less-than operator (a < b). Disambiguate by the
                // following character: an identifier start (letter
                // or '_') begins an expansion, anything else is LT
                // and passes through literally. This prevents
                // "<Src.X> < <Y>" from being read as a single
                // runaway bracket.
                char next = pos + 1 < text.Length ? text[pos + 1] : '\0';
                bool isBracketOpen = next == '_' || char.IsLetter(next);
                if (!isBracketOpen)
                {
                    sb.Append('<');
                    pos++;
                    continue;
                }

                string inner = ReadAngleBracket(text, ref pos);
                string resolved = ResolveVariable(inner);
                sb.Append(resolved);
            }
            else
            {
                sb.Append(text[pos]);
                pos++;
            }
        }

        return sb.ToString();
    }

    private string ResolveVariable(string varExpr)
    {
        if (string.IsNullOrEmpty(varExpr)) return "";

        // Dialog response accessors — only active inside a [Dialog X Button]
        // handler. Checked first so a plain ARGN doesn't collide with a generic
        // variable of the same name elsewhere.
        if (DialogArgResolver != null)
        {
            string upper = varExpr.ToUpperInvariant();
            if (upper == "ARGN" || upper == "ARGV" ||
                upper == "ARGCHK" || upper == "ARGCHKID" ||
                upper.StartsWith("ARGV[", StringComparison.Ordinal) ||
                upper.StartsWith("ARGV.", StringComparison.Ordinal) ||
                upper.StartsWith("ARGTXT[", StringComparison.Ordinal) ||
                upper.StartsWith("ARGCHK[", StringComparison.Ordinal))
            {
                string? v = DialogArgResolver(varExpr);
                if (v != null) return v;
            }
        }

        // EVAL keyword — numeric evaluation
        if (varExpr.StartsWith("EVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("EVAL\t", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return Evaluate(expanded.AsSpan()).ToString();
        }

        // HVAL — hex evaluation
        if (varExpr.StartsWith("HVAL ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return "0" + Evaluate(expanded.AsSpan()).ToString("X");
        }

        // QVAL — conditional: <QVAL condition?true_val:false_val>
        if (varExpr.StartsWith("QVAL ", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateQval(varExpr[5..]);
        }

        // STRARG — extract first whitespace-delimited token from ARGS
        if (varExpr.StartsWith("STRARG ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[7..].Trim());
            int sp = inner.IndexOf(' ');
            return sp > 0 ? inner[..sp] : inner;
        }

        // STRSUB — substring: <STRSUB start,length,string>
        if (varExpr.StartsWith("STRSUB ", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateStrSub(varExpr[7..]);
        }

        // STRLEN — string length: <STRLEN string>
        if (varExpr.StartsWith("STRLEN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[7..].Trim());
            return inner.Length.ToString();
        }

        // STREAT — consume first token from ARGS string and return the
        // remainder. Sphere treats both space and comma as token
        // separators here (moongate-style "X,Y,Z,label" strings feed
        // STREAT chains to peel off numeric prefixes and reach the
        // trailing name).
        if (varExpr.StartsWith("STREAT ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[7..].Trim());
            int sep = inner.IndexOfAny([' ', ',']);
            return sep >= 0 ? inner[(sep + 1)..].TrimStart(' ', ',') : "";
        }

        // STRMATCH — wildcard pattern match: <STRMATCH pattern,string>
        if (varExpr.StartsWith("STRMATCH ", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateStrMatch(varExpr[9..]);
        }

        // SEX <male>/<female> — pick value based on character sex.
        // Used heavily in dialogs: <Sex <Def.male>/<Def.female>> shows the
        // male label when the target is male, female label otherwise.
        if (varExpr.StartsWith("SEX ", StringComparison.OrdinalIgnoreCase))
        {
            string body = varExpr[4..].Trim();
            int slash = body.IndexOf('/');
            if (slash < 0) return ResolveAngleBrackets(body);
            string male = ResolveAngleBrackets(body[..slash]);
            string female = ResolveAngleBrackets(body[(slash + 1)..]);
            string? sexVal = VariableResolver?.Invoke("SEX");
            return sexVal == "1" ? female : male;
        }

        // FORMATMINUTES N — format a minute count as "HH:MM" for admin panels.
        if (varExpr.StartsWith("FORMATMINUTES ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[14..].Trim());
            if (long.TryParse(inner, out long mins))
            {
                long hours = mins / 60;
                long rem = mins % 60;
                return $"{hours}:{rem:D2}";
            }
            return inner;
        }

        // STRCMP — case-sensitive string compare: <STRCMP str1,str2>
        if (varExpr.StartsWith("STRCMP ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = varExpr[6..].Split(',', 2);
            if (parts.Length == 2)
                return string.Compare(ResolveAngleBrackets(parts[0].Trim()),
                    ResolveAngleBrackets(parts[1].Trim()), StringComparison.Ordinal).ToString();
            return "0";
        }

        // STRLOWER / STRTOLOWER / STRUPPER / STRTOUPPER
        if (varExpr.StartsWith("STRTOLOWER ", StringComparison.OrdinalIgnoreCase))
            return ResolveAngleBrackets(varExpr[11..].Trim()).ToLowerInvariant();
        if (varExpr.StartsWith("STRLOWER ", StringComparison.OrdinalIgnoreCase))
            return ResolveAngleBrackets(varExpr[9..].Trim()).ToLowerInvariant();
        if (varExpr.StartsWith("STRTOUPPER ", StringComparison.OrdinalIgnoreCase))
            return ResolveAngleBrackets(varExpr[11..].Trim()).ToUpperInvariant();
        if (varExpr.StartsWith("STRUPPER ", StringComparison.OrdinalIgnoreCase))
            return ResolveAngleBrackets(varExpr[9..].Trim()).ToUpperInvariant();

        // ISNUMBER / ISNUM — returns 1 if arg is numeric
        if (varExpr.StartsWith("ISNUMBER ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ISNUM ", StringComparison.OrdinalIgnoreCase))
        {
            int prefixLen = varExpr.StartsWith("ISNUM ", StringComparison.OrdinalIgnoreCase) ? 6 : 9;
            string inner = ResolveAngleBrackets(varExpr[prefixLen..].Trim());
            if (inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(inner.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out _) ? "1" : "0";
            return long.TryParse(inner, out _) ? "1" : "0";
        }

        // ASC — convert string to hex ASCII codes (space-separated)
        if (varExpr.StartsWith("ASC ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ASC(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("ASC(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 3) : ResolveAngleBrackets(varExpr[4..].Trim());
            return string.Join(" ", inner.Select(c => ((int)c).ToString("X2")));
        }

        // ASCPAD — convert string to hex ASCII codes, padded to fixed length
        if (varExpr.StartsWith("ASCPAD ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ASCPAD(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("ASCPAD(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 6) : ResolveAngleBrackets(varExpr[7..].Trim());
            var parts = inner.Split(',', 2);
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int padCount))
            {
                string str = parts[1].Trim().Trim('"');
                var sb = new System.Text.StringBuilder();
                for (int idx = 0; idx < padCount; idx++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(idx < str.Length ? ((int)str[idx]).ToString("X2") : "00");
                }
                return sb.ToString();
            }
            return "";
        }

        // BETWEEN — proportional mapping: (iCurrent-iMin)*iAbsMax/(iMax-iMin)
        if (varExpr.StartsWith("BETWEEN ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("BETWEEN(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("BETWEEN(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 7) : ResolveAngleBrackets(varExpr[8..].Trim());
            var parts = inner.Split(',');
            if (parts.Length >= 4)
            {
                long iMin = Evaluate(parts[0].Trim().AsSpan());
                long iMax = Evaluate(parts[1].Trim().AsSpan());
                long iCur = Evaluate(parts[2].Trim().AsSpan());
                long iAbsMax = Evaluate(parts[3].Trim().AsSpan());
                long range = iMax - iMin;
                return range != 0 ? ((iCur - iMin) * iAbsMax / range).ToString() : "0";
            }
            return "0";
        }

        // BETWEEN2 — inverse proportional mapping: (iMax-iCurrent)*iAbsMax/(iMax-iMin)
        if (varExpr.StartsWith("BETWEEN2 ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("BETWEEN2(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("BETWEEN2(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 8) : ResolveAngleBrackets(varExpr[9..].Trim());
            var parts = inner.Split(',');
            if (parts.Length >= 4)
            {
                long iMin = Evaluate(parts[0].Trim().AsSpan());
                long iMax = Evaluate(parts[1].Trim().AsSpan());
                long iCur = Evaluate(parts[2].Trim().AsSpan());
                long iAbsMax = Evaluate(parts[3].Trim().AsSpan());
                long range = iMax - iMin;
                return range != 0 ? ((iMax - iCur) * iAbsMax / range).ToString() : "0";
            }
            return "0";
        }

        // CHR — ASCII code to character
        if (varExpr.StartsWith("CHR ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("CHR(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("CHR(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 3) : ResolveAngleBrackets(varExpr[4..].Trim());
            long code = Evaluate(inner.AsSpan());
            return code > 0 && code < 0x10000 ? ((char)code).ToString() : "";
        }

        // CLRBIT — clear a specific bit: value & ~(1 << bit)
        if (varExpr.StartsWith("CLRBIT ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("CLRBIT(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("CLRBIT(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 6) : ResolveAngleBrackets(varExpr[7..].Trim());
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                long val = Evaluate(parts[0].Trim().AsSpan());
                int bit = (int)Evaluate(parts[1].Trim().AsSpan());
                if (bit is >= 0 and < 64)
                    return (val & ~(1L << bit)).ToString();
            }
            return "0";
        }

        // SETBIT — set a specific bit: value | (1 << bit)
        if (varExpr.StartsWith("SETBIT ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("SETBIT(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("SETBIT(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 6) : ResolveAngleBrackets(varExpr[7..].Trim());
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                long val = Evaluate(parts[0].Trim().AsSpan());
                int bit = (int)Evaluate(parts[1].Trim().AsSpan());
                if (bit is >= 0 and < 64)
                    return (val | (1L << bit)).ToString();
            }
            return "0";
        }

        // ISBIT — test if a specific bit is set
        if (varExpr.StartsWith("ISBIT ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ISBIT(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("ISBIT(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 5) : ResolveAngleBrackets(varExpr[6..].Trim());
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                long val = Evaluate(parts[0].Trim().AsSpan());
                int bit = (int)Evaluate(parts[1].Trim().AsSpan());
                if (bit is >= 0 and < 64)
                    return (val & (1L << bit)) != 0 ? "1" : "0";
            }
            return "0";
        }

        // D — force decimal evaluation
        if (varExpr.Length > 1 && (varExpr[0] == 'D' || varExpr[0] == 'd') &&
            !varExpr.StartsWith("DEFMSG", StringComparison.OrdinalIgnoreCase) &&
            !varExpr.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) &&
            !varExpr.StartsWith("DEF0.", StringComparison.OrdinalIgnoreCase) &&
            (char.IsUpper(varExpr[1]) || char.IsDigit(varExpr[1]) || varExpr[1] == '<' || varExpr[1] == '0'))
        {
            // <Dproperty> or <D expression> — evaluate and return as decimal
            string inner = varExpr[1..];
            string resolved = ResolveAngleBrackets(inner);
            // Try resolve as variable first
            string? varVal = VariableResolver?.Invoke(resolved);
            if (varVal != null)
            {
                if (varVal.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                    varVal.StartsWith("0", StringComparison.Ordinal))
                    return Evaluate(varVal.AsSpan()).ToString();
                return varVal;
            }
            return Evaluate(resolved.AsSpan()).ToString();
        }

        // EXPLODE — split string by separator chars into comma-delimited list
        if (varExpr.StartsWith("EXPLODE ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("EXPLODE(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("EXPLODE(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 7) : ResolveAngleBrackets(varExpr[8..].Trim());
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                string separators = parts[0].Trim();
                string str = parts[1].Trim().Trim('"');
                var tokens = str.Split(separators.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                return string.Join(",", tokens);
            }
            return inner;
        }

        // FEVAL — float evaluation (returns integer, same as EVAL for our purposes)
        if (varExpr.StartsWith("FEVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FEVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FEVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 5) : varExpr[6..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return Evaluate(expanded.AsSpan()).ToString();
        }

        // FHVAL — float hex value (returns hex, same as HVAL for our purposes)
        if (varExpr.StartsWith("FHVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FHVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FHVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 5) : varExpr[6..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return "0" + Evaluate(expanded.AsSpan()).ToString("X");
        }

        // FLOATVAL — floating point math (basic support)
        if (varExpr.StartsWith("FLOATVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FLOATVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FLOATVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 8) : varExpr[9..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return Evaluate(expanded.AsSpan()).ToString();
        }

        // FVAL — format as x.x (divides by 10): <FVAL 125> = "12.5"
        if (varExpr.StartsWith("FVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 4) : varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            long val = Evaluate(expanded.AsSpan());
            return $"{val / 10}.{Math.Abs(val % 10)}";
        }

        // MULDIV — safe (num*mul)/div with 64-bit math
        if (varExpr.StartsWith("MULDIV ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("MULDIV(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("MULDIV(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 6) : ResolveAngleBrackets(varExpr[7..].Trim());
            var parts = inner.Split(',', 3);
            if (parts.Length == 3)
            {
                long num = Evaluate(parts[0].Trim().AsSpan());
                long mul = Evaluate(parts[1].Trim().AsSpan());
                long div = Evaluate(parts[2].Trim().AsSpan());
                return div != 0 ? (num * mul / div).ToString() : "0";
            }
            return "0";
        }

        // MD5HASH — compute MD5 hash of string
        if (varExpr.StartsWith("MD5HASH ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("MD5HASH(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("MD5HASH(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 7) : ResolveAngleBrackets(varExpr[8..].Trim());
            byte[] hash = System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(inner));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        // STRPOS — find character position in string
        if (varExpr.StartsWith("STRPOS ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRPOS(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("STRPOS(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 6) : ResolveAngleBrackets(varExpr[7..].Trim());
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                string charCode = parts[0].Trim();
                string str = parts[1].Trim();
                char searchChar = int.TryParse(charCode, out int code) ? (char)code : (charCode.Length > 0 ? charCode[0] : '\0');
                return str.IndexOf(searchChar).ToString();
            }
            return "-1";
        }

        // STRREGEXNEW — regex match with pattern length: <STRREGEXNEW len, string, pattern>
        if (varExpr.StartsWith("STRREGEXNEW ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRREGEXNEW(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("STRREGEXNEW(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 11) : ResolveAngleBrackets(varExpr[12..].Trim());
            var parts = inner.Split(',', 3);
            if (parts.Length == 3)
            {
                string str = ResolveAngleBrackets(parts[1].Trim());
                string pattern = ResolveAngleBrackets(parts[2].Trim());
                try
                {
                    return System.Text.RegularExpressions.Regex.IsMatch(str, pattern,
                        System.Text.RegularExpressions.RegexOptions.None,
                        TimeSpan.FromMilliseconds(100)) ? "1" : "0";
                }
                catch { return "-1"; }
            }
            return "0";
        }

        // STRREVERSE — reverse string characters
        if (varExpr.StartsWith("STRREVERSE ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRREVERSE(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("STRREVERSE(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 10) : ResolveAngleBrackets(varExpr[11..].Trim());
            char[] chars = inner.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        // STRTRIM — trim leading/trailing whitespace
        if (varExpr.StartsWith("STRTRIM ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRTRIM(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("STRTRIM(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 7) : ResolveAngleBrackets(varExpr[8..]);
            return inner.Trim();
        }

        // UVAL — unsigned value evaluation
        if (varExpr.StartsWith("UVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("UVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("UVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 4) : varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            long val = Evaluate(expanded.AsSpan());
            return ((ulong)val).ToString();
        }

        // ABS — absolute value
        if (varExpr.StartsWith("ABS(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ABS ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 3);
            return Math.Abs(Evaluate(inner.AsSpan())).ToString();
        }

        // SQRT — square root
        if (varExpr.StartsWith("SQRT(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("SQRT ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 4);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Sqrt(val)).ToString();
        }

        // SIN — sine (value in fixed-point: result * 1000)
        if (varExpr.StartsWith("SIN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("SIN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 3);
            long val = Evaluate(inner.AsSpan());
            return ((long)(Math.Sin(val * Math.PI / 180.0) * 1000)).ToString();
        }

        // COS — cosine (value in fixed-point: result * 1000)
        if (varExpr.StartsWith("COS(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("COS ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 3);
            long val = Evaluate(inner.AsSpan());
            return ((long)(Math.Cos(val * Math.PI / 180.0) * 1000)).ToString();
        }

        // TAN — tangent (value in fixed-point: result * 1000)
        if (varExpr.StartsWith("TAN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("TAN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 3);
            long val = Evaluate(inner.AsSpan());
            return ((long)(Math.Tan(val * Math.PI / 180.0) * 1000)).ToString();
        }

        // LOGARITHM — log base-10, or log with custom base
        if (varExpr.StartsWith("LOGARITHM(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("LOGARITHM ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 9);
            var parts = inner.Split(',', 2);
            string valStr = ResolveAngleBrackets(parts[0].Trim());
            long val = Evaluate(valStr.AsSpan());
            if (val <= 0) return "0";
            if (parts.Length == 2)
            {
                string baseStr = parts[1].Trim().ToLowerInvariant();
                double logBase = baseStr switch
                {
                    "e" => Math.E,
                    "pi" => Math.PI,
                    _ => double.TryParse(baseStr, out double b) ? b : 10.0
                };
                return ((long)Math.Log(val, logBase)).ToString();
            }
            return ((long)Math.Log10(val)).ToString();
        }

        // NAPIERPOW — e^value
        if (varExpr.StartsWith("NAPIERPOW(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("NAPIERPOW ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 9);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Exp(val)).ToString();
        }

        // RAND — random: RAND(max) or RAND(min,max)
        if (varExpr.StartsWith("RAND(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("RAND ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 4);
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                string minStr = ResolveAngleBrackets(parts[0].Trim());
                string maxStr = ResolveAngleBrackets(parts[1].Trim());
                if (int.TryParse(minStr, out int rMin) && int.TryParse(maxStr, out int rMax2) && rMax2 >= rMin)
                    return Random.Shared.Next(rMin, rMax2 + 1).ToString();
                return "0";
            }
            string singleStr = ResolveAngleBrackets(parts[0].Trim());
            if (int.TryParse(singleStr, out int rMax1) && rMax1 > 0)
                return Random.Shared.Next(rMax1).ToString();
            return "0";
        }

        // RANDBELL — bell curve random: RANDBELL(center, variance)
        if (varExpr.StartsWith("RANDBELL(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("RANDBELL ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 8);
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                string cenStr = ResolveAngleBrackets(parts[0].Trim());
                string varStr = ResolveAngleBrackets(parts[1].Trim());
                if (int.TryParse(cenStr, out int center) && int.TryParse(varStr, out int variance) && variance > 0)
                {
                    // Simple bell curve: sum of 3 uniform randoms (central limit theorem)
                    int sum = 0;
                    for (int i = 0; i < 3; i++)
                        sum += Random.Shared.Next(-variance, variance + 1);
                    return (center + sum / 3).ToString();
                }
            }
            return "0";
        }

        // STRCMPI — case-insensitive string compare
        if (varExpr.StartsWith("STRCMPI(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRCMPI ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 6);
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
                return string.Compare(ResolveAngleBrackets(parts[0].Trim()),
                    ResolveAngleBrackets(parts[1].Trim()), StringComparison.OrdinalIgnoreCase).ToString();
            return "0";
        }

        // STRINDEXOF — find substring: STRINDEXOF(text, search, start)
        if (varExpr.StartsWith("STRINDEXOF(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRINDEXOF ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 10);
            var parts = inner.Split(',', 3);
            if (parts.Length >= 2)
            {
                string text = ResolveAngleBrackets(parts[0].Trim());
                string search = ResolveAngleBrackets(parts[1].Trim());
                int start = parts.Length > 2 && int.TryParse(parts[2].Trim(), out int s) ? s : 0;
                start = Math.Clamp(start, 0, text.Length);
                return text.IndexOf(search, start, StringComparison.OrdinalIgnoreCase).ToString();
            }
            return "-1";
        }

        // STRREGEX — regex match: STRREGEX(pattern, text)
        if (varExpr.StartsWith("STRREGEX(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRREGEX ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 8);
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                string pattern = ResolveAngleBrackets(parts[0].Trim());
                string text = ResolveAngleBrackets(parts[1].Trim());
                try
                {
                    return System.Text.RegularExpressions.Regex.IsMatch(text, pattern,
                        System.Text.RegularExpressions.RegexOptions.None,
                        TimeSpan.FromMilliseconds(100)) ? "1" : "0";
                }
                catch { return "0"; }
            }
            return "0";
        }

        // ISOBSCENE — basic profanity check
        if (varExpr.StartsWith("ISOBSCENE(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ISOBSCENE ", StringComparison.OrdinalIgnoreCase))
        {
            // Simple stub — always returns 0 (no profanity filter loaded)
            return "0";
        }

        // ID — resolve defname to numeric value
        if (varExpr.StartsWith("ID(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ID ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 2);
            string resolved = ResolveAngleBrackets(inner);
            // Try as hex first
            if (resolved.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(resolved[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexVal))
                return hexVal.ToString();
            // Try as defname via variable resolver
            string? defVal = VariableResolver?.Invoke(resolved);
            if (defVal != null && long.TryParse(defVal, out long numVal))
                return numVal.ToString();
            // Try direct number
            if (long.TryParse(resolved, out long directVal))
                return directVal.ToString();
            return "0";
        }

        // ISEMPTY — returns 1 if arg is empty/0
        if (varExpr.StartsWith("ISEMPTY ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[8..].Trim());
            return (string.IsNullOrEmpty(inner) || inner == "0") ? "1" : "0";
        }

        // ISPLAYER — check if UID references a player character
        if (varExpr.Equals("ISPLAYER", StringComparison.OrdinalIgnoreCase))
        {
            string? val = VariableResolver?.Invoke("ISPLAYER");
            return val ?? "0";
        }

        // ISNPC — check if UID references an NPC character
        if (varExpr.Equals("ISNPC", StringComparison.OrdinalIgnoreCase))
        {
            string? val = VariableResolver?.Invoke("ISNPC");
            return val ?? "0";
        }

        // R — random: <R max> returns random 0..max-1
        if (varExpr.StartsWith("R ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("R\t", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[2..].Trim());
            if (int.TryParse(inner, out int rMax) && rMax > 0)
                return Random.Shared.Next(rMax).ToString();
            return "0";
        }

        // Try variable resolver
        string expanded2 = ResolveAngleBrackets(varExpr);
        string? result = VariableResolver?.Invoke(expanded2);
        if (result == null)
            ReportUnresolved(expanded2);
        return result ?? "";
    }

    private string EvaluateQval(string expr)
    {
        int questionIdx = expr.IndexOf('?');
        if (questionIdx < 0) return "";

        string condition = expr[..questionIdx].Trim();
        string rest = expr[(questionIdx + 1)..];

        int colonIdx = rest.IndexOf(':');
        string trueVal, falseVal;
        if (colonIdx >= 0)
        {
            trueVal = rest[..colonIdx].Trim();
            falseVal = rest[(colonIdx + 1)..].Trim();
        }
        else
        {
            trueVal = rest.Trim();
            falseVal = "";
        }

        long condResult = Evaluate(ResolveAngleBrackets(condition).AsSpan());
        return condResult != 0
            ? ResolveAngleBrackets(trueVal)
            : ResolveAngleBrackets(falseVal);
    }

    private string EvaluateStrSub(string expr)
    {
        // Format: start,length,string
        var parts = expr.Split(',', 3);
        if (parts.Length < 3) return "";
        string resolved = ResolveAngleBrackets(parts[2].Trim());
        if (!int.TryParse(ResolveAngleBrackets(parts[0].Trim()), out int start)) return "";
        if (!int.TryParse(ResolveAngleBrackets(parts[1].Trim()), out int length)) return "";
        if (start < 0) start = 0;
        if (start >= resolved.Length) return "";
        length = Math.Min(length, resolved.Length - start);
        if (length <= 0) return "";
        return resolved.Substring(start, length);
    }

    private string EvaluateStrMatch(string expr)
    {
        // Format: pattern,string — wildcard match (* = any chars, ? = single char)
        var parts = expr.Split(',', 2);
        if (parts.Length < 2) return "0";
        string pattern = ResolveAngleBrackets(parts[0].Trim());
        string input = ResolveAngleBrackets(parts[1].Trim());
        return WildcardMatch(pattern, input) ? "1" : "0";
    }

    private static bool WildcardMatch(string pattern, string input)
    {
        int p = 0, i = 0, starP = -1, starI = -1;
        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(input[i])))
            { p++; i++; }
            else if (p < pattern.Length && pattern[p] == '*')
            { starP = p++; starI = i; }
            else if (starP >= 0)
            { p = starP + 1; i = ++starI; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>
    /// Extract the argument portion from a function call like "FUNC(args)" or "FUNC args".
    /// prefixLen is the length of the function name (e.g. 3 for "ABS", 4 for "SQRT").
    /// Strips parentheses if present.
    /// </summary>
    private string ExtractFuncArg(string varExpr, int prefixLen)
    {
        string rest = varExpr[prefixLen..];
        if (rest.StartsWith('('))
        {
            // Strip matching parentheses
            rest = rest[1..];
            if (rest.EndsWith(')'))
                rest = rest[..^1];
        }
        else
        {
            rest = rest.TrimStart();
        }
        return ResolveAngleBrackets(rest);
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
    }

    private static bool IsHexDigit(char c) =>
        char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
