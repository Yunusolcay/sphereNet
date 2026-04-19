namespace SphereNet.MapData.Multi;

/// <summary>
/// A single component of a multi-structure (house/ship).
/// </summary>
public readonly struct MultiComponent
{
    public ushort TileId { get; init; }
    public short XOffset { get; init; }
    public short YOffset { get; init; }
    public short ZOffset { get; init; }
    public uint Flags { get; init; }

    public bool IsVisible => (Flags & 0x01) == 0;
}

/// <summary>
/// Complete multi-structure definition loaded from multi.mul.
/// </summary>
public sealed class MultiDef
{
    public int MultiId { get; }
    public MultiComponent[] Components { get; }

    public MultiDef(int multiId, MultiComponent[] components)
    {
        MultiId = multiId;
        Components = components;
    }

    public (short MinX, short MinY, short MaxX, short MaxY) GetBounds()
    {
        if (Components.Length == 0)
            return (0, 0, 0, 0);

        short minX = short.MaxValue, minY = short.MaxValue;
        short maxX = short.MinValue, maxY = short.MinValue;

        foreach (var c in Components)
        {
            if (c.XOffset < minX) minX = c.XOffset;
            if (c.YOffset < minY) minY = c.YOffset;
            if (c.XOffset > maxX) maxX = c.XOffset;
            if (c.YOffset > maxY) maxY = c.YOffset;
        }

        return (minX, minY, maxX, maxY);
    }
}
