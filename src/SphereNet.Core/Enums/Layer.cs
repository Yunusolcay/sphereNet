namespace SphereNet.Core.Enums;

/// <summary>
/// Equipment layers. Maps to LAYER_TYPE in Source-X.
/// </summary>
public enum Layer : byte
{
    None = 0,
    OneHanded = 1,
    TwoHanded = 2,
    Shoes = 3,
    Pants = 4,
    Shirt = 5,
    Helm = 6,
    Gloves = 7,
    Ring = 8,
    Talisman = 9,
    Neck = 10,
    Hair = 11,
    Waist = 12,
    Chest = 13,
    Bracelet = 14,
    // UO LAYER_TYPE: LAYER_FACE=15 (enhanced-client face style / light halo),
    // LAYER_BEARD=16 (facial hair). These were previously swapped, which placed
    // beards on the face layer on the wire and mis-mapped legacy LAYER_BEARD=16
    // imports onto the Face slot.
    Face = 15,
    FacialHair = 16,
    Tunic = 17,
    Earrings = 18,
    Arms = 19,
    Cape = 20,
    Pack = 21,
    Robe = 22,
    Skirt = 23,
    Legs = 24,
    Horse = 25,
    VendorStock = 26,
    VendorExtra = 27,
    VendorBuy = 28,
    BankBox = 29,
    Special = 30,
    // UO LAYER_DRAGGING — the slot an item occupies while being dragged. SphereNet
    // tracks dragging via the DRAGGING tag rather than this layer, so it is defined
    // only for numeric parity with LAYER_TYPE; it is not a client-equippable slot.
    Dragging = 31,
    // Source-X keeps its spell and flag layers above LAYER_DRAGGING (uofiles_enums.h:592).
    // SphereNet models the ones it needs at the reference's own numbers, so a layer
    // byte means the same thing on both sides.
    FlagWool = 46,      // LAYER_FLAG_Wool - a shorn sheep's regrowing fleece
    Qty
}
