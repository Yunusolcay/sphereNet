namespace SphereNet.MapData.Tiles;

/// <summary>
/// Tile flag bits from tiledata.mul. Determines tile behavior (blocking, surface, etc.).
/// </summary>
[Flags]
public enum TileFlag : ulong
{
    None = 0,
    Background = 1 << 0,
    Weapon = 1 << 1,
    Transparent = 1 << 2,
    Translucent = 1 << 3,
    Wall = 1 << 4,
    Damaging = 1 << 5,
    Impassable = 1 << 6,
    Wet = 1 << 7,
    Surface = 1 << 9,
    Bridge = 1 << 10,
    Generic = 1 << 11,
    Window = 1 << 12,
    NoShoot = 1 << 13,
    ArticleA = 1 << 14,
    ArticleAn = 1 << 15,
    Internal = 1 << 16,
    Foliage = 1 << 17,
    PartialHue = 1 << 18,
    NoHouse = 1 << 19,
    Map = 1 << 20,
    Container = 1 << 21,
    Wearable = 1 << 22,
    LightSource = 1 << 23,
    Animation = 1 << 24,
    NoDiagonal = 1 << 25,
    Armor = 1 << 27,
    Roof = 1 << 28,
    Door = 1 << 29,
    StairBack = 1 << 30,
    StairRight = (ulong)1 << 31,
}

/// <summary>
/// Land tile data from tiledata.mul (first half).
/// </summary>
public readonly struct LandTileData
{
    public TileFlag Flags { get; init; }
    public ushort TextureId { get; init; }
    public string Name { get; init; }

    public bool IsWet => (Flags & TileFlag.Wet) != 0;
    public bool IsImpassable => (Flags & TileFlag.Impassable) != 0;
}

/// <summary>
/// Static/item tile data from tiledata.mul (second half).
/// </summary>
public readonly struct ItemTileData
{
    public TileFlag Flags { get; init; }
    public byte Weight { get; init; }
    public byte Quality { get; init; }
    public ushort Animation { get; init; }
    public byte Quantity { get; init; }
    public byte Value { get; init; }
    public byte Height { get; init; }
    public string Name { get; init; }

    public bool IsImpassable => (Flags & TileFlag.Impassable) != 0;
    public bool IsSurface => (Flags & TileFlag.Surface) != 0;
    public bool IsBridge => (Flags & TileFlag.Bridge) != 0;
    public bool IsWall => (Flags & TileFlag.Wall) != 0;
    public bool IsWet => (Flags & TileFlag.Wet) != 0;
    public bool IsRoof => (Flags & TileFlag.Roof) != 0;
    public int CalcHeight => IsBridge ? Height / 2 : Height;
}
