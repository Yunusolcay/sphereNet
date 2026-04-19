namespace SphereNet.Game.Scripting;

/// <summary>
/// Per-client script FILE object. Provides text file read/write access from scripts.
/// Maps to Source-X FILE.* commands and properties.
/// Each GameClient holds at most one active file handle.
/// </summary>
public sealed class ScriptFileHandle : IDisposable
{
    private readonly string _basePath;
    private FileStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string _filePath = "";

    // MODE flags (Sphere semantics)
    private bool _modeAppend;
    private bool _modeCreate;
    private bool _modeRead = true;
    private bool _modeWrite;

    public ScriptFileHandle(string basePath)
    {
        _basePath = Path.GetFullPath(basePath);
        if (!Directory.Exists(_basePath))
            Directory.CreateDirectory(_basePath);
    }

    public bool IsOpen => _stream != null;
    public bool IsEof => _reader?.EndOfStream ?? true;
    public string FilePath => _filePath;
    public long Position => _stream?.Position ?? 0;
    public long Length => _stream?.Length ?? 0;

    public bool ModeAppend { get => _modeAppend; set => _modeAppend = value; }
    public bool ModeCreate { get => _modeCreate; set => _modeCreate = value; }
    public bool ModeRead { get => _modeRead; set => _modeRead = value; }
    public bool ModeWrite { get => _modeWrite; set => _modeWrite = value; }

    public void SetModeDefault()
    {
        _modeAppend = false;
        _modeCreate = false;
        _modeRead = true;
        _modeWrite = false;
    }

    public bool Open(string path)
    {
        Close();

        string? resolved = ResolveSafePath(_basePath, path);
        if (resolved == null)
            return false;

        try
        {
            FileMode mode;
            FileAccess access;

            if (_modeCreate)
            {
                mode = FileMode.Create;
                access = _modeRead ? FileAccess.ReadWrite : FileAccess.Write;
            }
            else if (_modeAppend)
            {
                mode = FileMode.Append;
                access = FileAccess.Write;
            }
            else if (_modeWrite)
            {
                mode = _modeRead ? FileMode.OpenOrCreate : FileMode.OpenOrCreate;
                access = _modeRead ? FileAccess.ReadWrite : FileAccess.Write;
            }
            else
            {
                // Read-only (default)
                if (!File.Exists(resolved))
                    return false;
                mode = FileMode.Open;
                access = FileAccess.Read;
            }

            // Ensure parent directory exists for write modes
            if (access != FileAccess.Read)
            {
                string? dir = Path.GetDirectoryName(resolved);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }

            _stream = new FileStream(resolved, mode, access, FileShare.ReadWrite);
            _filePath = path;

            if (access == FileAccess.Read || access == FileAccess.ReadWrite)
                _reader = new StreamReader(_stream, leaveOpen: true);
            if (access == FileAccess.Write || access == FileAccess.ReadWrite)
                _writer = new StreamWriter(_stream, leaveOpen: true) { AutoFlush = false };

            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    public void Close()
    {
        _writer?.Dispose();
        _writer = null;
        _reader?.Dispose();
        _reader = null;
        _stream?.Dispose();
        _stream = null;
        _filePath = "";
    }

    public void WriteLine(string text)
    {
        _writer?.WriteLine(text);
    }

    public void Write(string text)
    {
        _writer?.Write(text);
    }

    public void WriteChr(int asciiVal)
    {
        _writer?.Write((char)asciiVal);
    }

    public void Flush()
    {
        _writer?.Flush();
        _stream?.Flush();
    }

    /// <summary>
    /// Read a specific line (1-based). 0 = last line.
    /// Sphere FILE.READLINE re-reads from beginning each call.
    /// </summary>
    public string ReadLine(int lineNum)
    {
        if (_stream == null || _reader == null)
            return "";

        _stream.Seek(0, SeekOrigin.Begin);
        _reader.DiscardBufferedData();

        if (lineNum == 0)
        {
            // Read all lines, return last
            string last = "";
            while (!_reader.EndOfStream)
                last = _reader.ReadLine() ?? "";
            return last;
        }

        int current = 0;
        while (!_reader.EndOfStream)
        {
            string? line = _reader.ReadLine();
            current++;
            if (current == lineNum)
                return line ?? "";
        }
        return "";
    }

    public string ReadChar()
    {
        if (_reader == null) return "";
        int ch = _reader.Read();
        return ch < 0 ? "" : ((char)ch).ToString();
    }

    public string ReadByte()
    {
        if (_stream == null) return "";
        int b = _stream.ReadByte();
        return b < 0 ? "" : b.ToString();
    }

    public void Seek(string pos)
    {
        if (_stream == null) return;

        if (pos.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            _stream.Seek(0, SeekOrigin.Begin);
            _reader?.DiscardBufferedData();
        }
        else if (pos.Equals("END", StringComparison.OrdinalIgnoreCase))
        {
            _stream.Seek(0, SeekOrigin.End);
            _reader?.DiscardBufferedData();
        }
        else if (long.TryParse(pos, out long offset))
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _reader?.DiscardBufferedData();
        }
    }

    // --- Static helpers ---

    public static bool FileExists(string basePath, string path)
    {
        string? resolved = ResolveSafePath(basePath, path);
        return resolved != null && File.Exists(resolved);
    }

    public static int GetFileLines(string basePath, string path)
    {
        string? resolved = ResolveSafePath(basePath, path);
        if (resolved == null || !File.Exists(resolved))
            return 0;
        try { return File.ReadAllLines(resolved).Length; }
        catch { return 0; }
    }

    public static bool DeleteFile(string basePath, string path)
    {
        string? resolved = ResolveSafePath(basePath, path);
        if (resolved == null || !File.Exists(resolved))
            return false;
        try { File.Delete(resolved); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Resolve path safely under basePath, blocking path traversal.
    /// </summary>
    private static string? ResolveSafePath(string basePath, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Block obvious traversal patterns
        string normalized = path.Replace('\\', '/');
        if (normalized.Contains("..") || normalized.StartsWith('/') || normalized.StartsWith(':'))
            return null;

        string fullBase = Path.GetFullPath(basePath);
        string full = Path.GetFullPath(Path.Combine(fullBase, normalized));

        // Ensure resolved path is under basePath
        if (!full.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            return null;

        return full;
    }

    public void Dispose()
    {
        Close();
    }
}
