using System.Diagnostics.CodeAnalysis;

namespace SphereNet.Core.Types;

/// <summary>
/// 32-bit unique object identifier. Maps to CUID in Source-X.
/// Bit 30 distinguishes items (set) from characters (clear).
/// </summary>
public readonly struct Serial : IEquatable<Serial>, IComparable<Serial>
{
    public const uint ItemFlag = 0x40000000;
    public const uint IndexMask = 0x0FFFFFFF;
    public const uint ClearValue = 0xFFFFFFFF;

    public static readonly Serial Invalid = new(ClearValue);
    public static readonly Serial Zero = new(0);

    private readonly uint _value;

    public Serial(uint value) => _value = value;

    public uint Value => _value;
    public bool IsItem => (_value & ItemFlag) != 0;
    public bool IsChar => (_value & ItemFlag) == 0 && _value != ClearValue;
    public bool IsValid => _value != ClearValue;
    public int Index => (int)(_value & IndexMask);

    public static Serial NewItem(int index) => new((uint)index | ItemFlag);
    public static Serial NewChar(int index) => new((uint)index);

    public bool Equals(Serial other) => _value == other._value;
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Serial s && Equals(s);
    public override int GetHashCode() => (int)_value;
    public int CompareTo(Serial other) => _value.CompareTo(other._value);

    public static bool operator ==(Serial left, Serial right) => left._value == right._value;
    public static bool operator !=(Serial left, Serial right) => left._value != right._value;
    public static implicit operator uint(Serial s) => s._value;
    public static explicit operator Serial(uint v) => new(v);

    public override string ToString() => $"0{_value:X8}";
}
