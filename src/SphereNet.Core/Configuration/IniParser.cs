namespace SphereNet.Core.Configuration;

/// <summary>
/// INI file parser. Reads sphere.ini style configuration files.
/// Supports [SECTION] blocks and KEY=VALUE lines.
/// </summary>
public sealed class IniParser
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, string>> Sections => _sections;

    public void Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"INI file not found: {filePath}");

        string currentSection = "";
        _sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in File.ReadLines(filePath))
        {
            ReadOnlySpan<char> line = rawLine.AsSpan().Trim();

            if (line.IsEmpty || line[0] == '/' && line.Length > 1 && line[1] == '/')
                continue;

            int commentIdx = line.IndexOf("//");
            if (commentIdx > 0)
                line = line[..commentIdx].TrimEnd();

            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end > 1)
                {
                    currentSection = line[1..end].ToString().Trim();
                    if (!_sections.ContainsKey(currentSection))
                        _sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            int eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                string key = line[..eqIdx].TrimEnd().ToString();
                string value = line[(eqIdx + 1)..].TrimStart().ToString();
                _sections[currentSection][key] = value;
            }
        }
    }

    public string? GetValue(string section, string key)
    {
        if (_sections.TryGetValue(section, out var dict) && dict.TryGetValue(key, out var value))
            return value;
        return null;
    }

    public int GetInt(string section, string key, int defaultValue = 0)
    {
        string? val = GetValue(section, key);
        if (val == null) return defaultValue;

        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || val.StartsWith("0X"))
        {
            if (int.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int hexResult))
                return hexResult;
        }

        return int.TryParse(val, out int result) ? result : defaultValue;
    }

    public bool GetBool(string section, string key, bool defaultValue = false)
    {
        string? val = GetValue(section, key);
        if (val == null) return defaultValue;
        return val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               val.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasSection(string section) => _sections.ContainsKey(section);

    public IEnumerable<KeyValuePair<string, string>> GetSectionValues(string section)
    {
        if (_sections.TryGetValue(section, out var dict))
            return dict;
        return Enumerable.Empty<KeyValuePair<string, string>>();
    }
}
