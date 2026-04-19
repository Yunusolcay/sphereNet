using System.Buffers.Binary;
using System.Text;

namespace SphereNet.MapData.Tiles;

/// <summary>
/// Reads tiledata.mul — land tiles and static/item tiles.
/// Supports both pre-HS (32-bit flags) and HS+ (64-bit flags) formats.
/// Maps to CUOTiledata in Source-X.
/// </summary>
public sealed class TileDataReader : IDisposable
{
    private readonly BinaryReader _reader;
    private LandTileData[]? _landTiles;
    private ItemTileData[]? _itemTiles;
    private bool _useHighSeas;

    public const int LandTileCount = 0x4000;
    public const int LandBlockSize = 32;
    public const int ItemBlockSize = 32;

    public TileDataReader(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(stream);
    }

    public LandTileData[] LandTiles => _landTiles ?? throw new InvalidOperationException("Call Load() first");
    public ItemTileData[] ItemTiles => _itemTiles ?? throw new InvalidOperationException("Call Load() first");
    public bool IsHighSeasFormat => _useHighSeas;

    public void Load()
    {
        DetectFormat();
        _reader.BaseStream.Seek(0, SeekOrigin.Begin);
        _landTiles = ReadLandTiles();
        _itemTiles = ReadItemTiles();
    }

    private void DetectFormat()
    {
        // HS format: land entry = 4 header + 32*(8 flags + 2 texId + 20 name) = 4 + 32*30 = 964
        // Pre-HS:    land entry = 4 header + 32*(4 flags + 2 texId + 20 name) = 4 + 32*26 = 836
        // Total land: 512 groups. HS: 512*964 = 493568, Pre-HS: 512*836 = 428032
        long fileSize = _reader.BaseStream.Length;
        // If file is large enough for HS format, use it
        long hsLandSize = (LandTileCount / LandBlockSize) * (4 + LandBlockSize * 30);
        _useHighSeas = fileSize > hsLandSize + 100000; // rough heuristic
    }

    private LandTileData[] ReadLandTiles()
    {
        var tiles = new LandTileData[LandTileCount];

        for (int block = 0; block < LandTileCount / LandBlockSize; block++)
        {
            _reader.ReadInt32(); // block header

            for (int i = 0; i < LandBlockSize; i++)
            {
                int idx = block * LandBlockSize + i;
                TileFlag flags;
                if (_useHighSeas)
                    flags = (TileFlag)_reader.ReadUInt64();
                else
                    flags = (TileFlag)_reader.ReadUInt32();

                ushort texId = _reader.ReadUInt16();
                string name = ReadFixedString(20);

                tiles[idx] = new LandTileData
                {
                    Flags = flags,
                    TextureId = texId,
                    Name = name
                };
            }
        }

        return tiles;
    }

    private ItemTileData[] ReadItemTiles()
    {
        long remaining = _reader.BaseStream.Length - _reader.BaseStream.Position;
        int flagSize = _useHighSeas ? 8 : 4;
        // ServUO entry layout (after flags):
        //   1 weight + 1 quality + 2 miscdata + 1 unk + 1 quantity +
        //   4 animation + 1 hue + 1 value + 1 height + 20 name = 33 bytes
        // Prior version read 2+1=3 bytes where ServUO reads 4 (ReadInt32) —
        // that 1-byte slip accumulates and scrambles tile flags for every
        // entry past the first. ServUO's pre-HS item entry is 37 bytes,
        // HS is 41; we must match exactly.
        int entrySize = flagSize + 33;
        int groupSize = 4 + (ItemBlockSize * entrySize);
        int numGroups = (int)(remaining / groupSize);
        int totalItems = numGroups * ItemBlockSize;

        var tiles = new ItemTileData[totalItems];

        for (int block = 0; block < numGroups; block++)
        {
            _reader.ReadInt32(); // block header

            for (int i = 0; i < ItemBlockSize; i++)
            {
                int idx = block * ItemBlockSize + i;
                TileFlag flags;
                if (_useHighSeas)
                    flags = (TileFlag)_reader.ReadUInt64();
                else
                    flags = (TileFlag)_reader.ReadUInt32();

                byte weight = _reader.ReadByte();
                byte quality = _reader.ReadByte();
                _reader.ReadUInt16();                  // miscdata
                _reader.ReadByte();                    // unk1
                byte quantity = _reader.ReadByte();
                uint animBlock = _reader.ReadUInt32(); // anim + unk (matches ServUO's ReadInt32)
                ushort anim = (ushort)(animBlock & 0xFFFF);
                _reader.ReadByte();                    // hue
                byte value = _reader.ReadByte();
                byte height = _reader.ReadByte();
                string name = ReadFixedString(20);

                tiles[idx] = new ItemTileData
                {
                    Flags = flags,
                    Weight = weight,
                    Quality = quality,
                    Animation = anim,
                    Quantity = quantity,
                    Value = value,
                    Height = height,
                    Name = name
                };
            }
        }

        return tiles;
    }

    private string ReadFixedString(int length)
    {
        var bytes = _reader.ReadBytes(length);
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = length;
        return Encoding.ASCII.GetString(bytes, 0, end);
    }

    public LandTileData GetLandTile(int tileId)
    {
        if (_landTiles == null || tileId < 0 || tileId >= _landTiles.Length)
            return default;
        return _landTiles[tileId];
    }

    public ItemTileData GetItemTile(int tileId)
    {
        if (_itemTiles == null || tileId < 0 || tileId >= _itemTiles.Length)
            return default;
        return _itemTiles[tileId];
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}
