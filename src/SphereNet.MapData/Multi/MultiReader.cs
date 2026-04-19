namespace SphereNet.MapData.Multi;

/// <summary>
/// Reads multi.mul + multi.idx — house/ship structure definitions.
/// multi.idx: index file — per-multi lookup (offset + length).
/// multi.mul: data file — multi components (12 bytes each for pre-HS).
/// </summary>
public sealed class MultiReader : IDisposable
{
    private readonly BinaryReader _idxReader;
    private readonly BinaryReader _dataReader;
    private readonly Dictionary<int, MultiDef> _cache = [];

    private const int ComponentSize = 12; // tileId:2 + x:2 + y:2 + z:2 + flags:4
    private const int IdxEntrySize = 12;  // offset:4 + length:4 + extra:4

    public MultiReader(string idxPath, string dataPath)
    {
        var idxStream = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _idxReader = new BinaryReader(idxStream);

        var dataStream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _dataReader = new BinaryReader(dataStream);
    }

    public MultiDef? GetMulti(int multiId)
    {
        if (_cache.TryGetValue(multiId, out var cached))
            return cached;

        var multi = ReadMulti(multiId);
        if (multi != null)
            _cache[multiId] = multi;

        return multi;
    }

    private MultiDef? ReadMulti(int multiId)
    {
        long idxOffset = (long)multiId * IdxEntrySize;
        if (idxOffset + IdxEntrySize > _idxReader.BaseStream.Length)
            return null;

        _idxReader.BaseStream.Seek(idxOffset, SeekOrigin.Begin);
        int dataOffset = _idxReader.ReadInt32();
        int dataLength = _idxReader.ReadInt32();
        _idxReader.ReadInt32(); // extra

        if (dataOffset < 0 || dataLength <= 0)
            return null;

        int count = dataLength / ComponentSize;
        var components = new MultiComponent[count];

        _dataReader.BaseStream.Seek(dataOffset, SeekOrigin.Begin);
        for (int i = 0; i < count; i++)
        {
            components[i] = new MultiComponent
            {
                TileId = _dataReader.ReadUInt16(),
                XOffset = _dataReader.ReadInt16(),
                YOffset = _dataReader.ReadInt16(),
                ZOffset = _dataReader.ReadInt16(),
                Flags = _dataReader.ReadUInt32()
            };
        }

        return new MultiDef(multiId, components);
    }

    public void Dispose()
    {
        _idxReader.Dispose();
        _dataReader.Dispose();
    }
}
