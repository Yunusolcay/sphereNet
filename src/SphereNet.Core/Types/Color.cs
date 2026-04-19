namespace SphereNet.Core.Types;

/// <summary>
/// UO color/hue value. Maps to HUE_TYPE in Source-X.
/// </summary>
public readonly struct Color : IEquatable<Color>
{
    public static readonly Color Default = new(0);
    public static readonly Color DyeDefault = new(0x0001);

    private readonly ushort _value;

    public Color(ushort value) => _value = value;

    public ushort Value => _value;
    public bool IsDefault => _value == 0;

    public bool Equals(Color other) => _value == other._value;
    public override bool Equals(object? obj) => obj is Color c && Equals(c);
    public override int GetHashCode() => _value;

    public static bool operator ==(Color left, Color right) => left._value == right._value;
    public static bool operator !=(Color left, Color right) => left._value != right._value;
    public static implicit operator ushort(Color c) => c._value;
    public static explicit operator Color(ushort v) => new(v);

    public override string ToString() => $"0x{_value:X4}";
}
