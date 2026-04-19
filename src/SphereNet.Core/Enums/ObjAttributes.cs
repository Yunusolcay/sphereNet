namespace SphereNet.Core.Enums;

/// <summary>
/// Object attribute flags. Maps to ATTR_* in Source-X.
/// </summary>
[Flags]
public enum ObjAttributes : uint
{
    None = 0,
    Identified = 0x0001,
    Decay = 0x0002,
    Newbie = 0x0004,
    Move_Always = 0x0008,
    Move_Never = 0x0010,
    Magic = 0x0020,
    Owned = 0x0040,
    Invis = 0x0080,
    Cursed = 0x0100,
    Cursed2 = 0x0200,
    Blessed = 0x0400,
    Blessed2 = 0x0800,
    ForSale = 0x1000,
    Stolen = 0x2000,
    CanDecay = 0x4000,
    Static = 0x8000,
    Exceptional = 0x10000,
    Enchanted = 0x20000,
    Secure = 0x40000,
    DamageD = 0x80000,
    LockedDown = 0x100000,
    Nodropt = 0x200000,
    NotRading = 0x400000
}
