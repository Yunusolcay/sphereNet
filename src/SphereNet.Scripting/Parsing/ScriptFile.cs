namespace SphereNet.Scripting.Parsing;

/// <summary>
/// .scp file reader with optional in-memory line cache.
/// Maps to CScript + CCacheableScriptFile in Source-X.
/// Reads section-based script files with [SECTION] blocks and KEY=VALUE lines.
/// </summary>
public sealed class ScriptFile : IDisposable
{
    private static readonly Dictionary<string, string[]> s_fileContentCache = new(StringComparer.OrdinalIgnoreCase);

    private StreamReader? _reader;
    private string[]? _cachedLines;
    private int _cacheLineIndex;
    private string? _pushedBackLine; // For non-cached mode pushback

    public string FilePath { get; private set; } = "";
    public ScriptContext Context { get; } = new();
    public bool IsOpen => _reader != null || _cachedLines != null;
    public bool UseCache { get; set; }

    /// <summary>
    /// Clear the static file content cache to free memory after bulk loading.
    /// </summary>
    public static void ClearFileCache() => s_fileContentCache.Clear();

    private const string ScriptExtension = ".scp";

    public bool Open(string path)
    {
        Close();

        if (!Path.HasExtension(path))
            path += ScriptExtension;

        if (!File.Exists(path))
            return false;

        FilePath = Path.GetFullPath(path);
        Context.FilePath = FilePath;
        Context.LineNumber = 0;
        Context.FileOffset = 0;

        if (UseCache)
        {
            if (!s_fileContentCache.TryGetValue(FilePath, out _cachedLines))
            {
                _cachedLines = File.ReadAllLines(FilePath);
                s_fileContentCache[FilePath] = _cachedLines;
            }
            _cacheLineIndex = 0;
        }
        else
        {
            _reader = new StreamReader(FilePath, System.Text.Encoding.UTF8);
        }

        return true;
    }

    public void Close()
    {
        _reader?.Dispose();
        _reader = null;
        _cachedLines = null;
        _cacheLineIndex = 0;
    }

    /// <summary>
    /// Read the next non-empty, non-comment line.
    /// Returns null at EOF.
    /// </summary>
    public string? ReadLine()
    {
        while (true)
        {
            string? raw = ReadRawLine();
            if (raw == null) return null;

            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            if (line.IsEmpty) continue;

            // Skip full-line comments
            if (line.Length >= 2 && line[0] == '/' && line[1] == '/')
                continue;

            // Strip inline comments
            int commentIdx = line.IndexOf("//");
            if (commentIdx > 0)
                return line[..commentIdx].TrimEnd().ToString();

            return line.ToString();
        }
    }

    private string? ReadRawLine()
    {
        string? line;

        // Check for pushed-back line first (non-cached mode)
        if (_pushedBackLine != null)
        {
            line = _pushedBackLine;
            _pushedBackLine = null;
            Context.LineNumber++;
            return line;
        }

        if (_cachedLines != null)
        {
            if (_cacheLineIndex >= _cachedLines.Length) return null;
            line = _cachedLines[_cacheLineIndex++];
        }
        else
        {
            line = _reader?.ReadLine();
            if (line == null) return null;
        }

        Context.LineNumber++;
        return line;
    }

    /// <summary>
    /// Read the next key=value pair from the current section.
    /// Returns null if a new section header is encountered or EOF.
    /// </summary>
    public ScriptKey? ReadKeyParse()
    {
        string? line = ReadLine();
        if (line == null) return null;

        // If we hit a new section, don't consume it
        if (line.StartsWith('['))
            return null;

        // Check for [EOF]
        if (line.Equals("[EOF]", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = new ScriptKey
        {
            SourceFile = Context.FilePath,
            SourceLine = Context.LineNumber
        };
        key.Parse(line.AsSpan());
        return key;
    }

    /// <summary>
    /// Scan for the next [SECTION] header.
    /// Returns the full header string (without brackets) or null if EOF.
    /// </summary>
    public string? FindNextSection()
    {
        while (true)
        {
            string? line = ReadLine();
            if (line == null) return null;

            if (line.StartsWith('['))
            {
                if (line.Equals("[EOF]", StringComparison.OrdinalIgnoreCase))
                    return null;

                int end = line.IndexOf(']');
                if (end > 1)
                    return line[1..end].Trim();
            }
        }
    }

    /// <summary>
    /// Parse the entire file into sections.
    /// </summary>
    public List<ScriptSection> ReadAllSections()
    {
        var sections = new List<ScriptSection>();

        while (true)
        {
            string? header = FindNextSection();
            if (header == null) break;

            var (name, arg) = ScriptSection.ParseHeader(header.AsSpan());
            var section = new ScriptSection(name, arg, Context.Snapshot());

            while (true)
            {
                string? line = ReadLine();
                if (line == null) break;

                if (line.StartsWith('['))
                {
                    // Push back — we'll re-find this section header next iteration
                    if (_cachedLines != null)
                    {
                        _cacheLineIndex--;
                        Context.LineNumber--;
                    }
                    else
                    {
                        // Non-cached mode: store the line for re-reading
                        _pushedBackLine = line;
                        Context.LineNumber--;
                    }
                    break;
                }

                var key = new ScriptKey
                {
                    SourceFile = Context.FilePath,
                    SourceLine = Context.LineNumber
                };
                key.Parse(line.AsSpan());
                if (!string.IsNullOrEmpty(key.Key))
                    section.Keys.Add(key);
            }

            sections.Add(section);
        }

        return sections;
    }

    /// <summary>
    /// Seek to a specific line (cache mode only).
    /// </summary>
    public bool SeekToLine(int lineNumber)
    {
        if (_cachedLines == null) return false;
        if (lineNumber < 1 || lineNumber > _cachedLines.Length) return false;
        _cacheLineIndex = lineNumber - 1;
        Context.LineNumber = lineNumber - 1;
        return true;
    }

    public void Dispose() => Close();
}
