namespace SphereNet.Core.Enums;

/// <summary>
/// ECS component props types. Maps to COMPPROPS_TYPE in Source-X CComponentProps.h.
/// </summary>
public enum ComponentPropsType : byte
{
    ItemChar = 0,
    ItemWeapon,
    ItemEquippable,
    Char,
    Qty
}
