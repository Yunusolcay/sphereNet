namespace SphereNet.Core.Enums;

/// <summary>
/// Capability/behavior flags. Maps exactly to CAN_* defines in Source-X CBase.h.
/// Shared flag namespace for characters and items.
/// </summary>
[Flags]
public enum CanFlags : uint
{
    None = 0,

    // Character movement (low byte, overlaps with item tile flags)
    C_Ghost = 0x0001,
    C_Swim = 0x0002,
    C_Walk = 0x0004,
    C_PassWalls = 0x0008,
    C_Fly = 0x0010,
    C_FireImmune = 0x0020,
    C_NoIndoors = 0x0040,
    C_Hover = 0x0080,

    // Item tile flags (same low byte, different context)
    I_Door = 0x0001,
    I_Water = 0x0002,
    I_Platform = 0x0004,
    I_Block = 0x0008,
    I_Climb = 0x0010,
    I_Fire = 0x0020,
    I_Roof = 0x0040,
    I_Hover = 0x0080,

    // Item higher bits
    I_Pile = 0x0100,
    I_Dye = 0x0200,
    I_Flip = 0x0400,
    I_Light = 0x0800,
    I_Repair = 0x1000,
    I_Replicate = 0x2000,
    I_DcIgnoreLOS = 0x4000,
    I_DcIgnoreDist = 0x8000,
    I_BlockLOS = 0x10000,
    I_Exceptional = 0x20000,
    I_MakersMark = 0x40000,
    I_RetainColor = 0x80000,
    I_Enchant = 0x100000,
    I_Imbue = 0x200000,
    I_Recycle = 0x400000,
    I_Reforge = 0x800000,
    I_ForceDC = 0x1000000,
    I_Damageable = 0x2000000,
    I_BlockLOSHeight = 0x4000000,
    I_EquipOnCast = 0x8000000,
    I_ScriptedMore = 0x20000000,
    I_TimerContained = 0x40000000,

    // Character higher bits
    C_Equip = 0x00100,
    C_UseHands = 0x00200,
    C_Mount = 0x00400,
    C_Female = 0x00800,
    C_NonHumanoid = 0x01000,
    C_Run = 0x02000,
    C_DcIgnoreLOS = 0x04000,
    C_DcIgnoreDist = 0x08000,
    C_NonMover = 0x10000,
    C_NoBlockHeight = 0x20000,
    C_Statue = 0x40000,
    C_NonSelectable = 0x80000,

    // Object shared
    O_NoSleep = 0x10000000,
}

/// <summary>
/// Equipment/usage restriction flags. Maps to CAN_U_* in Source-X.
/// </summary>
[Flags]
public enum CanEquipFlags : ushort
{
    All = 0x000,
    Male = 0x001,
    Female = 0x002,
    Human = 0x004,
    Elf = 0x008,
    Gargoyle = 0x010,
    None = 0x020,
}
