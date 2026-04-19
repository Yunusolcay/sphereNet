namespace SphereNet.Persistence.Formats;

/// <summary>
/// Streaming reader for classic Sphere <c>.scp</c> text saves. Line-by-line
/// tokenizer — same behaviour as the pre-refactor WorldLoader parse loop,
/// but extracted so the loader can work over an abstract source.
/// </summary>
public sealed class TextSaveReader : ISaveReader
{
    private readonly StreamReader _reader;
    private readonly bool _ownsStream;
    private bool _inRecord;

    public TextSaveReader(Stream stream, bool ownsStream = true)
    {
        _reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: !ownsStream);
        _ownsStream = ownsStream;
    }

    public bool NextRecord(out string section)
    {
        // Consume any stashed header from a prior NextProperty lookahead.
        if (_pendingSection != null)
        {
            string pending = _pendingSection;
            _pendingSection = null;
            if (!pending.Equals("EOF", StringComparison.OrdinalIgnoreCase))
            {
                section = pending;
                _inRecord = true;
                return true;
            }
        }

        // Flush remaining properties of the previous record.
        while (_inRecord && NextProperty(out _, out _)) { /* discard */ }

        string? line;
        while ((line = _reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("//")) continue;

            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                string name = trimmed[1..^1];
                if (name.Equals("EOF", StringComparison.OrdinalIgnoreCase))
                {
                    _inRecord = false;
                    continue;
                }
                section = name;
                _inRecord = true;
                return true;
            }
        }

        section = string.Empty;
        return false;
    }

    public bool NextProperty(out string key, out string value)
    {
        if (!_inRecord)
        {
            key = value = string.Empty;
            return false;
        }

        string? line;
        while ((line = _reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("//")) continue;

            // Another section header or [EOF] — back out, caller re-enters via NextRecord.
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                _inRecord = false;
                _pendingSection = trimmed[1..^1];
                key = value = string.Empty;
                return false;
            }

            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            key = trimmed[..eq].Trim();
            value = trimmed[(eq + 1)..].Trim();
            return true;
        }

        _inRecord = false;
        key = value = string.Empty;
        return false;
    }

    // Recovery for the "read ahead" case where NextProperty finds a new
    // [SECTION] before reporting end-of-record. Stashed here so the next
    // NextRecord returns it without re-reading the stream.
    private string? _pendingSection;

    public void Dispose() => _reader.Dispose();
}
