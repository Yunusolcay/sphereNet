namespace SphereNet.Core.Enums;

/// <summary>
/// Direction values. Maps to DIR_TYPE in Source-X.
/// </summary>
public enum Direction : byte
{
    North = 0,
    NorthEast = 1,
    East = 2,
    SouthEast = 3,
    South = 4,
    SouthWest = 5,
    West = 6,
    NorthWest = 7,
    Qty = 8,
    Running = 0x80
}
