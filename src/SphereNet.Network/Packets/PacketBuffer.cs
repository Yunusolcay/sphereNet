using System.Buffers.Binary;

namespace SphereNet.Network.Packets;

/// <summary>
/// Big-endian packet buffer for UO protocol. Maps to Packet in Source-X.
/// UO protocol uses network byte order (big-endian) for all multi-byte values.
/// </summary>
public sealed class PacketBuffer
{
    private byte[] _data;
    private int _position;
    private int _length;

    public PacketBuffer(int capacity = 64)
    {
        _data = new byte[capacity];
    }

    public PacketBuffer(byte[] data)
    {
        _data = data;
        _length = data.Length;
    }

    public int Position { get => _position; set => _position = value; }
    public int Length => _length;
    public byte[] Data => _data;
    public ReadOnlySpan<byte> Span => _data.AsSpan(0, _length);

    private void EnsureCapacity(int needed)
    {
        int required = _position + needed;
        if (required <= _data.Length) return;

        int newSize = Math.Max(_data.Length * 2, required);
        Array.Resize(ref _data, newSize);
    }

    // --- Write methods (big-endian) ---

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _data[_position++] = value;
        if (_position > _length) _length = _position;
    }

    public void WriteSByte(sbyte value) => WriteByte((byte)value);

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16BigEndian(_data.AsSpan(_position), value);
        _position += 2;
        if (_position > _length) _length = _position;
    }

    public void WriteInt16(short value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteInt16BigEndian(_data.AsSpan(_position), value);
        _position += 2;
        if (_position > _length) _length = _position;
    }

    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(_position), value);
        _position += 4;
        if (_position > _length) _length = _position;
    }

    public void WriteInt32(int value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteInt32BigEndian(_data.AsSpan(_position), value);
        _position += 4;
        if (_position > _length) _length = _position;
    }

    public void WriteAsciiFixed(string text, int length)
    {
        EnsureCapacity(length);
        int i = 0;
        for (; i < text.Length && i < length; i++)
            _data[_position + i] = (byte)text[i];
        for (; i < length; i++)
            _data[_position + i] = 0;
        _position += length;
        if (_position > _length) _length = _position;
    }

    public void WriteAsciiNull(string text)
    {
        int len = text.Length + 1;
        EnsureCapacity(len);
        for (int i = 0; i < text.Length; i++)
            _data[_position + i] = (byte)text[i];
        _data[_position + text.Length] = 0;
        _position += len;
        if (_position > _length) _length = _position;
    }

    public void WriteUnicodeNullBE(string text)
    {
        int len = (text.Length + 1) * 2;
        EnsureCapacity(len);
        for (int i = 0; i < text.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_data.AsSpan(_position), text[i]);
            _position += 2;
        }
        WriteUInt16(0);
        if (_position > _length) _length = _position;
    }

    /// <summary>Write a Unicode string in Little Endian without null terminator (for 0xD6 tooltip args).</summary>
    public void WriteUnicodeLE(string text)
    {
        int len = text.Length * 2;
        EnsureCapacity(len);
        for (int i = 0; i < text.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(_position), text[i]);
            _position += 2;
        }
        if (_position > _length) _length = _position;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(bytes.Length);
        bytes.CopyTo(_data.AsSpan(_position));
        _position += bytes.Length;
        if (_position > _length) _length = _position;
    }

    /// <summary>
    /// Write packet length at a specific position (for variable-length packets).
    /// </summary>
    public void WriteLengthAt(int position)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_data.AsSpan(position), (ushort)_length);
    }

    // --- Read methods (big-endian) ---

    public byte ReadByte()
    {
        if (_position >= _length) return 0;
        return _data[_position++];
    }

    public sbyte ReadSByte() => (sbyte)ReadByte();
    public bool ReadBool() => ReadByte() != 0;

    public ushort ReadUInt16()
    {
        if (_position + 2 > _length) return 0;
        var val = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_position));
        _position += 2;
        return val;
    }

    public short ReadInt16()
    {
        if (_position + 2 > _length) return 0;
        var val = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(_position));
        _position += 2;
        return val;
    }

    public uint ReadUInt32()
    {
        if (_position + 4 > _length) return 0;
        var val = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_position));
        _position += 4;
        return val;
    }

    public int ReadInt32()
    {
        if (_position + 4 > _length) return 0;
        var val = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_position));
        _position += 4;
        return val;
    }

    public string ReadAsciiFixed(int length)
    {
        if (_position + length > _length) return "";
        int end = _position + length;
        int nullIdx = Array.IndexOf(_data, (byte)0, _position, length);
        int strLen = nullIdx >= 0 ? nullIdx - _position : length;
        var result = System.Text.Encoding.ASCII.GetString(_data, _position, strLen);
        _position = end;
        return result;
    }

    public string ReadAsciiNull()
    {
        int start = _position;
        while (_position < _length && _data[_position] != 0)
            _position++;
        string result = System.Text.Encoding.ASCII.GetString(_data, start, _position - start);
        if (_position < _length) _position++; // skip null
        return result;
    }

    public string ReadUnicodeNullBE()
    {
        var sb = new System.Text.StringBuilder();
        while (_position + 2 <= _length)
        {
            ushort ch = ReadUInt16();
            if (ch == 0) break;
            sb.Append((char)ch);
        }
        return sb.ToString();
    }

    public string ReadUnicodeFixed(int charCount)
    {
        var sb = new System.Text.StringBuilder(charCount);
        for (int i = 0; i < charCount && _position + 2 <= _length; i++)
        {
            ushort ch = ReadUInt16();
            if (ch != 0) sb.Append((char)ch);
        }
        return sb.ToString();
    }

    public byte[] ReadBytes(int count)
    {
        if (_position + count > _length) count = _length - _position;
        var result = new byte[count];
        Array.Copy(_data, _position, result, 0, count);
        _position += count;
        return result;
    }

    public int Remaining => _length - _position;

    public void Reset()
    {
        _position = 0;
        _length = 0;
    }
}
