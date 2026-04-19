namespace SphereNet.MapData.Map;

/// <summary>
/// A single map terrain cell (from map0.mul).
/// </summary>
public readonly struct MapCell
{
    public ushort TileId { get; init; }
    public sbyte Z { get; init; }
}

/// <summary>
/// A static item placed on the map (from statics0.mul).
/// </summary>
public readonly struct StaticItem
{
    public ushort TileId { get; init; }
    public byte XOffset { get; init; }
    public byte YOffset { get; init; }
    public sbyte Z { get; init; }
    public ushort Hue { get; init; }
}

/// <summary>
/// A 8x8 block of map terrain cells.
/// </summary>
public sealed class MapBlock
{
    public const int BlockSize = 8;
    public const int CellCount = BlockSize * BlockSize;

    public uint Header { get; set; }
    public MapCell[] Cells { get; } = new MapCell[CellCount];

    public MapCell GetCell(int x, int y) => Cells[y * BlockSize + x];
}
