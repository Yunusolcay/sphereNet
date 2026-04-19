using System.IO.Compression;
using System.IO.MemoryMappedFiles;

namespace SphereNet.MapData.Map;

/// <summary>
/// Reads map data from map0LegacyMUL.uop files.
/// UOP is a container format wrapping MUL data blocks.
/// Each entry contains one or more 196-byte map blocks (4-byte header + 64 cells * 3 bytes).
/// Uses MemoryMappedFile so the OS pages out unused map regions automatically.
/// </summary>
public sealed class UopMapReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _dataLength;
    private readonly string? _tempFilePath;
    private readonly int _width;
    private readonly int _height;
    private readonly int _blockWidth;
    private readonly int _blockHeight;

    private const int BlockDataSize = 196; // 4 + 64*3
    private const uint UopMagic = 0x50594D; // "MYP"

    public int Width => _width;
    public int Height => _height;

    public UopMapReader(string uopPath, int width, int height)
    {
        _width = width;
        _height = height;
        _blockWidth = width / MapBlock.BlockSize;
        _blockHeight = height / MapBlock.BlockSize;

        // Decompress UOP into a temp file, then memory-map it
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"spherenet_map_{Guid.NewGuid():N}.tmp");
        ExtractMulFromUop(uopPath, _tempFilePath);

        var fileInfo = new FileInfo(_tempFilePath);
        _dataLength = fileInfo.Length;

        _mmf = MemoryMappedFile.CreateFromFile(_tempFilePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, _dataLength, MemoryMappedFileAccess.Read);
    }

    public MapBlock ReadBlock(int blockX, int blockY)
    {
        if (blockX < 0 || blockX >= _blockWidth || blockY < 0 || blockY >= _blockHeight)
            return new MapBlock();

        long offset = ((long)blockX * _blockHeight + blockY) * BlockDataSize;
        if (offset + BlockDataSize > _dataLength)
            return new MapBlock();

        var block = new MapBlock
        {
            Header = _view.ReadUInt32(offset)
        };
        long pos = offset + 4;

        for (int i = 0; i < MapBlock.CellCount; i++)
        {
            block.Cells[i] = new MapCell
            {
                TileId = _view.ReadUInt16(pos),
                Z = (sbyte)_view.ReadByte(pos + 2)
            };
            pos += 3;
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

    /// <summary>
    /// Extract all MUL data from UOP container into a flat file.
    /// </summary>
    private static void ExtractMulFromUop(string uopPath, string outputPath)
    {
        using var fs = new FileStream(uopPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);

        // Header
        uint magic = br.ReadUInt32();
        if (magic != UopMagic)
            throw new InvalidDataException($"Not a UOP file: bad magic 0x{magic:X8}");

        uint version = br.ReadUInt32();
        uint timestamp = br.ReadUInt32();
        long nextBlock = br.ReadInt64();
        uint blockSize = br.ReadUInt32();
        int fileCount = br.ReadInt32();

        // Read all file entries from linked blocks
        var entries = new List<UopEntry>(fileCount);
        long blockOffset = nextBlock;

        while (blockOffset != 0)
        {
            fs.Seek(blockOffset, SeekOrigin.Begin);
            int filesInBlock = br.ReadInt32();
            long nextBlockOffset = br.ReadInt64();

            for (int i = 0; i < filesInBlock; i++)
            {
                long fileOffset = br.ReadInt64();
                int headerLength = br.ReadInt32();
                int compressedLength = br.ReadInt32();
                int decompressedLength = br.ReadInt32();
                ulong fileHash = br.ReadUInt64();
                uint dataHash = br.ReadUInt32();
                short compressionFlag = br.ReadInt16();

                if (fileOffset == 0)
                    continue;

                entries.Add(new UopEntry
                {
                    Offset = fileOffset + headerLength,
                    CompressedLength = compressedLength,
                    DecompressedLength = decompressedLength,
                    CompressionFlag = compressionFlag,
                    Hash = fileHash
                });
            }

            blockOffset = nextBlockOffset;
        }

        // Build hash → index mapping using the same hash as ClassicUO
        var hashToIndex = new Dictionary<ulong, int>();
        for (int mapN = 0; mapN < 6; mapN++)
        {
            for (int idx = 0; idx < entries.Count + 100; idx++)
            {
                string name = $"build/map{mapN}legacymul/{idx:D8}.dat";
                ulong hash = CreateHash(name);
                if (!hashToIndex.ContainsKey(hash))
                    hashToIndex[hash] = idx;

                string xName = $"build/map{mapN}xlegacymul/{idx:D8}.dat";
                ulong xHash = CreateHash(xName);
                if (!hashToIndex.ContainsKey(xHash))
                    hashToIndex[xHash] = idx;
            }
        }

        // Sort entries by their index in the MUL file
        var sortedEntries = new SortedDictionary<int, UopEntry>();
        foreach (var entry in entries)
        {
            if (hashToIndex.TryGetValue(entry.Hash, out int idx))
                sortedEntries[idx] = entry;
        }

        // Read and decompress all entries into the output file
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        foreach (var kvp in sortedEntries)
        {
            var entry = kvp.Value;
            fs.Seek(entry.Offset, SeekOrigin.Begin);
            byte[] compressed = br.ReadBytes(entry.CompressedLength);

            if (entry.CompressionFlag == 1) // Zlib
            {
                byte[] decompressed = new byte[entry.DecompressedLength];
                using var ms = new MemoryStream(compressed);
                using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                int totalRead = 0;
                while (totalRead < decompressed.Length)
                {
                    int read = zlib.Read(decompressed, totalRead, decompressed.Length - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
                output.Write(decompressed, 0, totalRead);
            }
            else // No compression
            {
                output.Write(compressed, 0, compressed.Length);
            }
        }
    }

    /// <summary>
    /// ClassicUO-compatible UOP hash function (HashLittle2).
    /// </summary>
    private static ulong CreateHash(string s)
    {
        uint eax, ecx, edx, ebx, esi, edi;
        eax = ecx = edx = ebx = esi = edi = 0;
        ebx = edi = esi = (uint)s.Length + 0xDEADBEEF;

        int i = 0;
        for (i = 0; i + 12 < s.Length; i += 12)
        {
            edi = (uint)((s[i + 7] << 24) | (s[i + 6] << 16) | (s[i + 5] << 8) | s[i + 4]) + edi;
            esi = (uint)((s[i + 11] << 24) | (s[i + 10] << 16) | (s[i + 9] << 8) | s[i + 8]) + esi;
            edx = (uint)((s[i + 3] << 24) | (s[i + 2] << 16) | (s[i + 1] << 8) | s[i]) - esi;

            edx = (edx + ebx) ^ (esi >> 28) ^ (esi << 4);
            esi += edi;
            edi = (edi - edx) ^ (edx >> 26) ^ (edx << 6);
            edx += esi;
            esi = (esi - edi) ^ (edi >> 24) ^ (edi << 8);
            edi += edx;
            ebx = (edx - esi) ^ (esi >> 16) ^ (esi << 16);
            esi += edi;
            edi = (edi - ebx) ^ (ebx >> 13) ^ (ebx << 19);
            ebx += esi;
            esi = (esi - edi) ^ (edi >> 28) ^ (edi << 4);
            edi += ebx;
        }

        int remaining = s.Length - i;

        if (remaining > 0)
        {
            switch (remaining)
            {
                case 12:
                    esi += (uint)s[i + 11] << 24;
                    goto case 11;
                case 11:
                    esi += (uint)s[i + 10] << 16;
                    goto case 10;
                case 10:
                    esi += (uint)s[i + 9] << 8;
                    goto case 9;
                case 9:
                    esi += s[i + 8];
                    goto case 8;
                case 8:
                    edi += (uint)s[i + 7] << 24;
                    goto case 7;
                case 7:
                    edi += (uint)s[i + 6] << 16;
                    goto case 6;
                case 6:
                    edi += (uint)s[i + 5] << 8;
                    goto case 5;
                case 5:
                    edi += s[i + 4];
                    goto case 4;
                case 4:
                    ebx += (uint)s[i + 3] << 24;
                    goto case 3;
                case 3:
                    ebx += (uint)s[i + 2] << 16;
                    goto case 2;
                case 2:
                    ebx += (uint)s[i + 1] << 8;
                    goto case 1;
                case 1:
                    ebx += s[i];
                    break;
            }

            esi = (esi ^ edi) - ((edi >> 18) ^ (edi << 14));
            ecx = (esi ^ ebx) - ((esi >> 21) ^ (esi << 11));
            edi = (edi ^ ecx) - ((ecx >> 7) ^ (ecx << 25));
            esi = (esi ^ edi) - ((edi >> 16) ^ (edi << 16));
            edx = (esi ^ ecx) - ((esi >> 28) ^ (esi << 4));
            edi = (edi ^ edx) - ((edx >> 18) ^ (edx << 14));
            eax = (esi ^ edi) - ((edi >> 8) ^ (edi << 24));
        }
        else
        {
            eax = esi;
        }

        return ((ulong)edi << 32) | eax;
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();

        // Temp dosyayı temizle
        if (_tempFilePath != null)
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    private struct UopEntry
    {
        public long Offset;
        public int CompressedLength;
        public int DecompressedLength;
        public short CompressionFlag;
        public ulong Hash;
    }
}
