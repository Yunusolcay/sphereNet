namespace SphereNet.Core.Enums;

/// <summary>
/// NPC brain AI type. Maps exactly to NPCBRAIN_TYPE in Source-X CChar.h.
/// </summary>
public enum NpcBrainType : byte
{
    None = 0,
    Animal = 1,
    Human = 2,
    Healer = 3,
    Guard = 4,
    Banker = 5,
    Vendor = 6,
    Stable = 7,
    Monster = 8,
    Berserk = 9,
    Dragon = 10,

    Qty = 11,
}
