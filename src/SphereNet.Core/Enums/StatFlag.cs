namespace SphereNet.Core.Enums;

/// <summary>
/// Character status flags. Maps exactly to STATF_* defines in Source-X CChar.h.
/// </summary>
[Flags]
public enum StatFlag : uint
{
    None = 0,
    Invul = 0x00000001,
    Dead = 0x00000002,
    Freeze = 0x00000004,
    Invisible = 0x00000008,
    Sleeping = 0x00000010,
    War = 0x00000020,
    Reactive = 0x00000040,
    Poisoned = 0x00000080,
    NightSight = 0x00000100,
    Reflection = 0x00000200,
    Polymorph = 0x00000400,
    Incognito = 0x00000800,
    SpiritSpeak = 0x00001000,
    Insubstantial = 0x00002000,
    EmoteAction = 0x00004000,
    CommCrystal = 0x00008000,
    HasShield = 0x00010000,
    ArcherCanMove = 0x00020000,
    Stone = 0x00040000,
    Hovering = 0x00080000,
    Fly = 0x00100000,
    // 0x00200000 reserved
    Meditation = 0x00200000,
    Hallucinating = 0x00400000,
    Hidden = 0x00800000,
    InDoors = 0x01000000,
    Criminal = 0x02000000,
    Conjured = 0x04000000,
    Pet = 0x08000000,
    Spawned = 0x10000000,
    SaveParity = 0x20000000,
    Ridden = 0x40000000,
    OnHorse = 0x80000000,
}
