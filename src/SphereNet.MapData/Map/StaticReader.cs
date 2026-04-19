using System.IO.MemoryMappedFiles;

namespace SphereNet.MapData.Map;

/// <summary>
/// Reads statics0.mul + staidx0.mul — static items placed on the map.
/// staidx0.mul: index file — per-block lookup (offset + length).
/// statics0.mul: data file — static items (7 bytes each).
/// Uses MemoryMappedFile so the OS pages out unused regions automatically,
/// same pattern as UopMapReader (saves significant RAM vs. BinaryReader).
/// </summary>
public sealed class StaticReader : IDisposable
{
    private readonly MemoryMappedFile _idxMmf;
    private readonly MemoryMappedViewAccessor _idxView;
    private readonly long _idxLength;
    private readonly MemoryMappedFile _dataMmf;
    private readonly MemoryMappedViewAccessor _dataView;
    private readonly long _dataLength;

    private readonly int _blockWidth;
    private readonly int _blockHeight;

    private const int StaticEntrySize = 7; // tileId:2 + xOff:1 + yOff:1 + z:1 + hue:2
    private const int IdxEntrySize = 12;   // offset:4 + length:4 + extra:4

    public StaticReader(string idxPath, string dataPath, int mapWidth, int mapHeight)
    {
        _blockWidth = mapWidth / MapBlock.BlockSize;
        _blockHeight = mapHeight / MapBlock.BlockSize;

        _idxLength = new FileInfo(idxPath).Length;
        _idxMmf = MemoryMappedFile.CreateFromFile(idxPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _idxView = _idxMmf.CreateViewAccessor(0, _idxLength, MemoryMappedFileAccess.Read);

        _dataLength = new FileInfo(dataPath).Length;
        _dataMmf = MemoryMappedFile.CreateFromFile(dataPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _dataView = _dataMmf.CreateViewAccessor(0, _dataLength, MemoryMappedFileAccess.Read);
    }

    /// <summary>
    /// Read all static items in an 8x8 block.
    /// </summary>
    public StaticItem[] ReadBlock(int blockX, int blockY)
    {
        if (blockX < 0 || blockX >= _blockWidth || blockY < 0 || blockY >= _blockHeight)
            return [];

        long idxOffset = ((long)blockX * _blockHeight + blockY) * IdxEntrySize;
        if (idxOffset + IdxEntrySize > _idxLength)
            return [];

        int dataOffset = _idxView.ReadInt32(idxOffset);
        int dataLength = _idxView.ReadInt32(idxOffset + 4);
        // idxOffset + 8 is "extra" — skipped.

        if (dataOffset < 0 || dataLength <= 0)
            return [];
        if ((long)dataOffset + dataLength > _dataLength)
            return [];

        int count = dataLength / StaticEntrySize;
        var items = new StaticItem[count];

        long pos = dataOffset;
        for (int i = 0; i < count; i++)
        {
            items[i] = new StaticItem
            {
                TileId = _dataView.ReadUInt16(pos),
                XOffset = _dataView.ReadByte(pos + 2),
                YOffset = _dataView.ReadByte(pos + 3),
                Z = (sbyte)_dataView.ReadByte(pos + 4),
                Hue = _dataView.ReadUInt16(pos + 5)
            };
            pos += StaticEntrySize;
        }

        return items;
    }

    /// <summary>
    /// Get static items at a specific world coordinate.
    /// </summary>
    public StaticItem[] GetStatics(int x, int y)
    {
        int bx = x / MapBlock.BlockSize;
        int by = y / MapBlock.BlockSize;
        int offX = x % MapBlock.BlockSize;
        int offY = y % MapBlock.BlockSize;

        var allItems = ReadBlock(bx, by);
        var filtered = new List<StaticItem>();
        foreach (var item in allItems)
        {
            if (item.XOffset == offX && item.YOffset == offY)
                filtered.Add(item);
        }
        return filtered.ToArray();
    }

    public void Dispose()
    {
        _idxView.Dispose();
        _idxMmf.Dispose();
        _dataView.Dispose();
        _dataMmf.Dispose();
    }
}
