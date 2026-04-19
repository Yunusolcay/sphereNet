namespace SphereNet.Core.Enums;

/// <summary>
/// Region flags. Maps to REGION_FLAG_* in Source-X.
/// </summary>
[Flags]
public enum RegionFlag : uint
{
    None = 0,
    GuardedOff = 0x0001,
    Guarded = 0x0002,
    NoMagic = 0x0004,
    Gate = 0x0008,
    Recall = 0x0010,
    Mark = 0x0020,
    Arena = 0x0040,
    SafeZone = 0x0080,
    NoPvP = 0x0100,
    NoPeraCrime = 0x0200,
    Announce = 0x0400,
    Underground = 0x0800,
    Jail = 0x1000,
    NoBuild = 0x2000,
    Safe = 0x4000
}
