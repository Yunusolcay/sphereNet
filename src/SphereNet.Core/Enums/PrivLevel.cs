namespace SphereNet.Core.Enums;

/// <summary>
/// Privilege levels. Maps exactly to PLEVEL_TYPE in Source-X CTextConsole.h.
/// </summary>
public enum PrivLevel : byte
{
    Guest = 0,
    Player = 1,
    Counsel = 2,
    Seer = 3,
    GM = 4,
    Dev = 5,
    Admin = 6,
    Owner = 7,

    Qty = 8,
}
