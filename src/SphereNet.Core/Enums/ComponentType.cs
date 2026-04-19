namespace SphereNet.Core.Enums;

/// <summary>
/// ECS component types. Maps to COMP_TYPE in Source-X CComponent.h.
/// </summary>
public enum ComponentType : byte
{
    Champion = 0,
    Spawn,
    Multi,
    ItemDamageable,
    Qty
}
