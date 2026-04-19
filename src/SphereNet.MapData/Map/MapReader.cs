namespace SphereNet.MapData.Map;

/// <summary>
/// Reads map0.mul — terrain data organized in 8x8 blocks.
/// Each block = 4 byte header + 64 cells * 3 bytes (tileId:2 + z:1) = 196 bytes.
/// </summary>
public sealed class MapReader : IDisposable
{
    private readonly BinaryReader _reader;
    private readonly int _width;
    private readonly int _height;
    private readonly int _blockWidth;
    private readonly int _blockHeight;

    private const int BlockDataSize = 196; // 4 + 64*3

    public int Width => _width;
    public int Height => _height;

    public MapReader(string filePath, int width, int height)
    {
        _width = width;
        _height = height;
        _blockWidth = width / MapBlock.BlockSize;
        _blockHeight = height / MapBlock.BlockSize;

        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(stream);
    }

    public MapBlock ReadBlock(int blockX, int blockY)
    {
        if (blockX < 0 || blockX >= _blockWidth || blockY < 0 || blockY >= _blockHeight)
            return new MapBlock();

        long offset = ((long)blockX * _blockHeight + blockY) * BlockDataSize;
        if (offset + BlockDataSize > _reader.BaseStream.Length)
            return new MapBlock();

        _reader.BaseStream.Seek(offset, SeekOrigin.Begin);

        var block = new MapBlock { Header = _reader.ReadUInt32() };

        for (int i = 0; i < MapBlock.CellCount; i++)
        {
            block.Cells[i] = new MapCell
            {
                TileId = _reader.ReadUInt16(),
                Z = _reader.ReadSByte()
            };
        }

        return block;
    }

    public MapCell GetCell(int x, int y)
    {
        int bx = x / MapBlock.BlockSize;
        int by = y / MapBlock.BlockSize;
        var block = ReadBlock(bx, by);
        return block.GetCell(x % MapBlock.BlockSize, y % MapBlock.BlockSize);
    }

    public void Dispose() => _reader.Dispose();
}
