namespace SphereNet.Scripting.Parsing;

/// <summary>
/// Represents a [SECTION] block in a .scp file.
/// Stores the section name, file offset, and allows iteration through keys.
/// </summary>
public sealed class ScriptSection
{
    public string Name { get; }
    public string Argument { get; }
    public ScriptContext Context { get; }
    public List<ScriptKey> Keys { get; } = [];

    public ScriptSection(string name, string argument, ScriptContext context)
    {
        Name = name;
        Argument = argument;
        Context = context;
    }

    /// <summary>
    /// Parse section header like "ITEMDEF 0100" into name="ITEMDEF" and argument="0100".
    /// </summary>
    public static (string Name, string Argument) ParseHeader(ReadOnlySpan<char> header)
    {
        header = header.Trim();

        int spaceIdx = header.IndexOf(' ');
        if (spaceIdx < 0)
        {
            int tabIdx = header.IndexOf('\t');
            if (tabIdx < 0)
                return (string.Intern(header.ToString().ToUpperInvariant()), "");
            spaceIdx = tabIdx;
        }

        string name = string.Intern(header[..spaceIdx].Trim().ToString().ToUpperInvariant());
        string arg = header[(spaceIdx + 1)..].Trim().ToString();
        return (name, arg);
    }

    /// <summary>
    /// Find a key by name (case-insensitive).
    /// </summary>
    public ScriptKey? FindKey(string keyName)
    {
        foreach (var key in Keys)
        {
            if (key.Key.Equals(keyName, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }

    /// <summary>
    /// Get all keys that start with "ON=" (trigger definitions).
    /// </summary>
    public IEnumerable<ScriptKey> GetTriggers()
    {
        foreach (var key in Keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
                key.Key.StartsWith("ON=@", StringComparison.OrdinalIgnoreCase))
                yield return key;
        }
    }

    public override string ToString() =>
        string.IsNullOrEmpty(Argument) ? $"[{Name}]" : $"[{Name} {Argument}]";
}
