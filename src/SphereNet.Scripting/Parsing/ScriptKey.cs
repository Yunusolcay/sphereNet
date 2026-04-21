namespace SphereNet.Scripting.Parsing;

/// <summary>
/// A parsed script line as KEY=VALUE pair.
/// Maps to CScriptKey in Source-X.
/// </summary>
public sealed class ScriptKey
{
    public string Key { get; private set; } = "";
    public string Arg { get; private set; } = "";
    public bool HasArg => Arg.Length > 0;

    /// <summary>Source file path the line was parsed from (set by
    /// <see cref="ScriptFile"/>). Used for diagnostic messages so
    /// scriptdebug warnings can pinpoint the offending line; never
    /// affects execution. Empty for keys built in code.</summary>
    public string SourceFile { get; set; } = "";

    /// <summary>1-based source line number, set together with
    /// <see cref="SourceFile"/>. Zero when unknown.</summary>
    public int SourceLine { get; set; }

    public ScriptKey() { }

    /// <summary>Construct a pre-parsed key/arg pair — used when expanding
    /// or rewriting script lines without re-running the parser (e.g. the
    /// dialog FOR/IF pre-expander in GameClient).</summary>
    public ScriptKey(string key, string arg)
    {
        Key = key ?? "";
        Arg = arg ?? "";
    }

    /// <summary>
    /// Parse a raw line into key and argument.
    /// Handles KEY=VALUE, KEY VALUE, and plain KEY forms.
    /// Also handles =, +=, -=, .= operators by rewriting the argument.
    /// </summary>
    public void Parse(ReadOnlySpan<char> line)
    {
        line = line.Trim();
        if (line.IsEmpty)
        {
            Key = "";
            Arg = "";
            return;
        }

        // Increment / decrement forms: "FOO ++" / "FOO--" / "BAR++"
        // Rewrite into += 1 / -= 1 so the rest of the pipeline handles them
        // uniformly. Sphere scripts lean on this heavily ("Src.CTag0.X ++"
        // is the canonical counter bump in d_admin_main).
        if (line.Length >= 2)
        {
            var trimmed = line;
            if (trimmed[^1] == '+' && trimmed[^2] == '+' &&
                (trimmed.Length == 2 || trimmed[^3] != '+'))
            {
                string body = trimmed[..^2].TrimEnd().ToString();
                if (body.Length > 0)
                {
                    Key = body;
                    Arg = $"<EVAL <{body}>+1>";
                    return;
                }
            }
            if (trimmed[^1] == '-' && trimmed[^2] == '-' &&
                (trimmed.Length == 2 || trimmed[^3] != '-'))
            {
                string body = trimmed[..^2].TrimEnd().ToString();
                if (body.Length > 0)
                {
                    Key = body;
                    Arg = $"<EVAL <{body}>-1>";
                    return;
                }
            }
        }

        int eqPos = line.IndexOf('=');
        int spacePos = line.IndexOf(' ');
        int tabPos = line.IndexOf('\t');
        int sepPos = -1;

        if (eqPos >= 0 && (spacePos < 0 || eqPos <= spacePos) && (tabPos < 0 || eqPos <= tabPos))
        {
            sepPos = eqPos;
        }
        else if (spacePos >= 0 && (tabPos < 0 || spacePos <= tabPos))
        {
            sepPos = spacePos;
        }
        else if (tabPos >= 0)
        {
            sepPos = tabPos;
        }

        if (sepPos < 0)
        {
            Key = line.ToString();
            Arg = "";
            return;
        }

        Key = line[..sepPos].TrimEnd().ToString();
        Arg = line[(sepPos + 1)..].TrimStart().ToString();

        // Handle compound assignment operators that collapse onto the
        // key side: "foo+=3", "foo.=x", "foo-=2". The "=" is the sep
        // here so the preceding char is the operator.
        if (Key.Length > 0 && line[sepPos] == '=')
        {
            char lastChar = Key[^1];
            if (lastChar is '+' or '-' or '.' or '|' or '&' or '^')
            {
                string realKey = Key[..^1].TrimEnd();
                string op = lastChar.ToString();

                if (op == ".")
                {
                    // .= is string concatenation: KEY = <KEY> + ARG
                    Arg = $"<{realKey}>{Arg}";
                }
                else
                {
                    // += / -= / |= / &= / ^=  →  KEY = <EVAL <KEY> OP ARG>
                    Arg = $"<EVAL <{realKey}>{op}{Arg}>";
                }
                Key = realKey;
            }
        }

        // Handle the space-separated form where the operator is in the
        // Arg part: "foo += 3", "bar |= flag", "baz .= suffix". sepPos
        // fell on whitespace so the operator is the first token of Arg.
        if (Arg.Length >= 2)
        {
            char a0 = Arg[0];
            if (a0 is '+' or '-' or '.' or '|' or '&' or '^' && Arg[1] == '=')
            {
                string realKey = Key;
                string op = a0.ToString();
                string rest = Arg[2..].TrimStart();

                if (op == ".")
                    Arg = $"<{realKey}>{rest}";
                else
                    Arg = $"<EVAL <{realKey}>{op}{rest}>";
            }
        }
    }

    /// <summary>
    /// Try to parse the argument as an integer.
    /// Supports hex (0x prefix) and decimal.
    /// </summary>
    public bool TryGetArgInt(out long value)
    {
        return TryParseNumber(Arg.AsSpan(), out value);
    }

    public long GetArgInt(long defaultValue = 0)
    {
        return TryGetArgInt(out long val) ? val : defaultValue;
    }

    internal static bool TryParseNumber(ReadOnlySpan<char> text, out long value)
    {
        text = text.Trim();
        value = 0;
        if (text.IsEmpty) return false;

        // Explicit 0x/0X prefix → hex
        if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
        {
            return long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        // Leading '0' with length > 1 → hex (Source-X convention)
        // Source-X treats all numbers starting with '0' as hex: 09ae1, 06F7, 00000020, etc.
        if (text.Length > 1 && text[0] == '0')
        {
            return long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        // Plain decimal
        return long.TryParse(text, out value);
    }

    public override string ToString() => HasArg ? $"{Key}={Arg}" : Key;
}
