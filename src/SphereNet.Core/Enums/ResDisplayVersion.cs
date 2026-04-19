namespace SphereNet.Core.Enums;

/// <summary>
/// Resource display / client era version. Maps to RESDISPLAY_VERSION in Source-X game_enums.h.
/// Controls which features/items are available based on client expansion.
/// </summary>
public enum ResDisplayVersion : byte
{
    PreT2A = 0,
    T2A = 1,
    LBR = 2,
    AOS = 3,
    SE = 4,
    ML = 5,
    KR = 6,
    SA = 7,
    HS = 8,
    TOL = 9,

    Qty = 10,
}
