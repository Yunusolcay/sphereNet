namespace SphereNet.Core.Enums;

/// <summary>
/// Bit flags indicating which properties of an object have changed since last consumed.
/// Used by the delta-based view update system.
/// </summary>
[Flags]
public enum DirtyFlag : uint
{
    None       = 0,
    Position   = 1,
    Body       = 2,
    Hue        = 4,
    Name       = 8,
    Stats      = 16,
    Direction  = 32,
    Equip      = 64,
    StatFlags  = 128,
    Amount     = 256,
    Container  = 512,
    Deleted    = 1024,
}
