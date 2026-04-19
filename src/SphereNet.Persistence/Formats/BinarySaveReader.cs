using System.Buffers.Binary;
using System.Text;

namespace SphereNet.Persistence.Formats;

/// <summary>
/// Binary tag-stream reader — decodes files produced by <see cref="BinarySaveWriter"/>.
/// Validates the magic header and version. Streaming: property bytes are
/// read only when requested so record traversal stays O(1) memory.
/// </summary>
public sealed class BinarySaveReader : ISaveReader
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private uint _remainingProps;

    public BinarySaveReader(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;

        Span<byte> header = stackalloc byte[8];
        ReadExact(_stream, header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != BinarySaveWriter.Magic)
            throw new InvalidDataException($"Not a SphereNet binary save (magic=0x{magic:X8})");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != BinarySaveWriter.FormatVersion)
            throw new InvalidDataException($"Unsupported binary save version {version}, expected {BinarySaveWriter.FormatVersion}");
        // reserved[6..8] ignored
    }

    public bool NextRecord(out string section)
    {
        // Skip unread properties from the previous record.
        while (_remainingProps > 0)
        {
            NextProperty(out _, out _);
        }

        int sectionLen = _stream.ReadByte();
        if (sectionLen <= 0)
        {
            // Either EOF (<0) or terminator sentinel (0).
            section = string.Empty;
            return false;
        }

        Span<byte> nameBuf = stackalloc byte[256];
        ReadExact(_stream, nameBuf[..sectionLen]);
        section = Encoding.UTF8.GetString(nameBuf[..sectionLen]);

        Span<byte> countBuf = stackalloc byte[4];
        ReadExact(_stream, countBuf);
        _remainingProps = BinaryPrimitives.ReadUInt32LittleEndian(countBuf);
        return true;
    }

    public bool NextProperty(out string key, out string value)
    {
        if (_remainingProps == 0)
        {
            key = value = string.Empty;
            return false;
        }

        int keyLen = _stream.ReadByte();
        if (keyLen <= 0) throw new InvalidDataException("Unexpected end of property key");

        Span<byte> keyBuf = stackalloc byte[256];
        ReadExact(_stream, keyBuf[..keyLen]);
        key = Encoding.UTF8.GetString(keyBuf[..keyLen]);

        Span<byte> lenBuf = stackalloc byte[2];
        ReadExact(_stream, lenBuf);
        int valLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);

        // Values can be up to 64K — can't stackalloc safely for the large end.
        byte[] valBytes = valLen == 0 ? Array.Empty<byte>() : new byte[valLen];
        if (valLen > 0)
            ReadExact(_stream, valBytes);
        value = valLen == 0 ? string.Empty : Encoding.UTF8.GetString(valBytes);

        _remainingProps--;
        return true;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0) throw new EndOfStreamException("Unexpected end of binary save");
            read += n;
        }
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}
