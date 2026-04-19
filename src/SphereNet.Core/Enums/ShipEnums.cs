namespace SphereNet.Core.Enums;

/// <summary>
/// Ship movement type. Maps to SHIP_MOVEMENT_TYPE in Source-X CCMultiMovable.h.
/// </summary>
public enum ShipMovementType : byte
{
    Stop = 0,
    OneTile = 1,
    Normal = 2,
}

/// <summary>
/// Ship speed mode. Maps to SHIP_SPEED in Source-X CCMultiMovable.h.
/// </summary>
public enum ShipSpeedMode : byte
{
    OneTile = 1,
    Rowboat = 2,
    Slow = 3,
    Fast = 4,
}
