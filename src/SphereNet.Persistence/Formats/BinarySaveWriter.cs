using System.Buffers.Binary;
using System.Text;

namespace SphereNet.Persistence.Formats;

/// <summary>
/// Binary tag-stream writer. Wire format:
/// <code>
/// Header   : "SNBW" (4) + version:u16 + reserved:u16
/// Record   : section_len:u8 + section_utf8 + prop_count:u32 + property*
/// Property : key_len:u8 + key_utf8 + value_len:u16 + value_utf8
/// Terminator: section_len = 0
/// </code>
/// Same (key, value) semantics as <see cref="TextSaveWriter"/> so the loader
/// reuses the exact parsers — no per-field binary schema to maintain. Values
/// stay UTF-8 strings; no type packing.
/// </summary>
public sealed class BinarySaveWriter : ISaveWriter
{
    internal const uint Magic = 0x57424E53; // "SNBW" little-endian
    internal const ushort FormatVersion = 1;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly List<(string Key, string Value)> _pending = new(32);
    private bool _recordOpen;
    private string _currentSection = string.Empty;
    private long _written;

    public long WrittenBytes => _written;

    public BinarySaveWriter(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0); // reserved
        _stream.Write(header);
        _written += 8;
    }

    public void WriteHeaderComment(string line)
    {
        // Comments are dropped in binary — they belong in the text variant only.
        _ = line;
    }

    public void BeginRecord(string section)
    {
        if (_recordOpen) EndRecord();
        _currentSection = section;
        _pending.Clear();
        _recordOpen = true;
    }

    public void WriteProperty(string key, string value)
    {
        if (!_recordOpen)
            throw new InvalidOperationException("WriteProperty called before BeginRecord");
        _pending.Add((key, value));
    }

    public void EndRecord()
    {
        if (!_recordOpen) return;

        byte[] sectionBytes = Encoding.UTF8.GetBytes(_currentSection);
        if (sectionBytes.Length == 0 || sectionBytes.Length > 255)
            throw new InvalidDataException($"Section name '{_currentSection}' length out of range");

        _stream.WriteByte((byte)sectionBytes.Length);
        _stream.Write(sectionBytes);
        _written += 1 + sectionBytes.Length;

        Span<byte> countBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(countBuf, (uint)_pending.Count);
        _stream.Write(countBuf);
        _written += 4;

        Span<byte> lenU16 = stackalloc byte[2];
        foreach (var (k, v) in _pending)
        {
            byte[] kBytes = Encoding.UTF8.GetBytes(k);
            byte[] vBytes = Encoding.UTF8.GetBytes(v);

            if (kBytes.Length == 0 || kBytes.Length > 255)
                throw new InvalidDataException($"Property key '{k}' length out of range");
            if (vBytes.Length > 65535)
                throw new InvalidDataException($"Property value for '{k}' exceeds 65535 bytes");

            _stream.WriteByte((byte)kBytes.Length);
            _stream.Write(kBytes);

            BinaryPrimitives.WriteUInt16LittleEndian(lenU16, (ushort)vBytes.Length);
            _stream.Write(lenU16);
            _stream.Write(vBytes);

            _written += 1 + kBytes.Length + 2 + vBytes.Length;
        }

        _pending.Clear();
        _recordOpen = false;
    }

    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        if (_recordOpen) EndRecord();
        _stream.WriteByte(0); // terminator: section_len=0
        _stream.Flush();
        if (_ownsStream) _stream.Dispose();
    }
}
