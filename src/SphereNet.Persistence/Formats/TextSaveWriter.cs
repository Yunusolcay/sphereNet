namespace SphereNet.Persistence.Formats;

/// <summary>
/// Writes the classic Sphere <c>.scp</c> text format. Each record opens with
/// <c>[SECTION]</c>, properties as <c>KEY=VALUE</c>, closed by <c>[EOF]</c> +
/// blank line. Wire-compatible with Sphere / Source-X saves.
/// </summary>
public sealed class TextSaveWriter : ISaveWriter
{
    private readonly StreamWriter _writer;
    private readonly bool _ownsStream;
    private bool _recordOpen;
    private long _written;

    public TextSaveWriter(Stream stream, bool ownsStream = true)
    {
        _writer = new StreamWriter(stream, System.Text.Encoding.UTF8, bufferSize: 64 * 1024, leaveOpen: !ownsStream);
        _ownsStream = ownsStream;
    }

    public long WrittenBytes => _written;

    public void WriteHeaderComment(string line)
    {
        _writer.Write("// ");
        _writer.WriteLine(line);
        _written += 3 + line.Length + Environment.NewLine.Length;
    }

    public void BeginRecord(string section)
    {
        if (_recordOpen) EndRecord();
        _writer.Write('[');
        _writer.Write(section);
        _writer.WriteLine(']');
        _written += 2 + section.Length + Environment.NewLine.Length;
        _recordOpen = true;
    }

    public void WriteProperty(string key, string value)
    {
        _writer.Write(key);
        _writer.Write('=');
        _writer.WriteLine(value);
        _written += key.Length + 1 + value.Length + Environment.NewLine.Length;
    }

    public void EndRecord()
    {
        if (!_recordOpen) return;
        _writer.WriteLine("[EOF]");
        _writer.WriteLine();
        _written += 5 + 2 * Environment.NewLine.Length;
        _recordOpen = false;
    }

    public void Flush() => _writer.Flush();

    public void Dispose()
    {
        if (_recordOpen) EndRecord();
        _writer.Flush();
        _writer.Dispose();
    }
}
