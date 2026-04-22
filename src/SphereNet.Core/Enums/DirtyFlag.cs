namespace SphereNet.Core.Enums;

/// <summary>
/// Bit flags indicating which properties of an object have changed since last consumed.
/// Used to trigger <see cref="SphereNet.Game.World.GameWorld.NotifyDirty"/> when an object
/// transitions from clean to dirty. Note: the view delta system currently does not filter
/// by individual flags — it uses the dirty set as a "something changed" gate and performs
/// a full range scan. These flags are retained for future per-field optimizations.
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
    /// <summary>Reserved for future use. Currently no code sets this flag.</summary>
    Deleted    = 1024,
}
