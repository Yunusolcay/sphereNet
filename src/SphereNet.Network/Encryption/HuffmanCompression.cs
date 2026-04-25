using System.Buffers;

namespace SphereNet.Network.Encryption;

/// <summary>
/// Huffman compression/decompression for UO client packets.
/// Used for decompressing data received from client (game server connection)
/// and compressing data sent to the client.
/// Maps to CHuffman in Source-X.
/// </summary>
public static class HuffmanCompression
{
    // Huffman tree nodes as per UO protocol specification (for decompression)
    private static readonly int[,] DecompTree = {
        {2,1},{4,3},{0,5},{7,6},{9,8},{11,10},{13,12},{14,-256},
        {16,15},{18,17},{20,19},{22,21},{23,-1},{25,24},{27,26},{29,28},
        {31,30},{33,32},{35,34},{37,36},{39,38},{41,40},{42,-2},{44,43},
        {46,45},{48,47},{50,49},{52,51},{54,53},{56,55},{58,57},{60,59},
        {62,61},{63,-3},{65,64},{67,66},{69,68},{71,70},{73,72},{75,74},
        {77,76},{79,78},{81,80},{83,82},{85,84},{87,86},{89,88},{91,90},
        {93,92},{-4,94},{96,95},{98,97},{100,99},{102,101},{104,103},{106,105},
        {108,107},{110,109},{112,111},{114,113},{116,115},{118,117},{-5,119},
        {121,120},{123,122},{125,124},{127,126},{129,128},{131,130},{133,132},
        {-6,134},{136,135},{138,137},{140,139},{142,141},{144,143},{-7,145},
        {147,146},{149,148},{151,150},{153,152},{-8,154},{156,155},{158,157},
        {160,159},{162,161},{-9,163},{165,164},{167,166},{169,168},{-10,170},
        {172,171},{174,173},{176,175},{-11,177},{179,178},{181,180},{-12,182},
        {184,183},{186,185},{-13,187},{189,188},{-14,190},{192,191},{194,193},
        {-15,195},{197,196},{-16,198},{200,199},{-17,201},{203,202},{-18,204},
        {206,205},{-19,207},{209,208},{-20,210},{-21,211},{213,212},{-22,214},
        {216,215},{-23,217},{219,218},{-24,220},{222,221},{-25,223},{225,224},
        {-26,226},{228,227},{-27,229},{231,230},{-28,232},{-29,233},{235,234},
        {-30,236},{-31,237},{239,238},{-32,240},{-33,241},{243,242},{-34,244},
        {-35,245},{-36,246},{-37,247},{-38,248},{-39,249},{-40,250},{-41,251},
        {-42,252},{-43,253},{-44,254}
    };

    /// <summary>
    /// Hardcoded Huffman encode table from Source-X (kxCompress_Base).
    /// 257 entries: 0-255 for byte values, 256 for end-of-data terminator.
    /// Each entry: lower 4 bits = number of bits in code, upper bits (>>4) = the Huffman code.
    /// </summary>
    private static readonly ushort[] CompressBase =
    {
        0x0002, 0x01f5, 0x0226, 0x0347, 0x0757, 0x0286, 0x03b6, 0x0327,
        0x0e08, 0x0628, 0x0567, 0x0798, 0x19d9, 0x0978, 0x02a6, 0x0577,
        0x0718, 0x05b8, 0x1cc9, 0x0a78, 0x0257, 0x04f7, 0x0668, 0x07d8,
        0x1919, 0x1ce9, 0x03f7, 0x0909, 0x0598, 0x07b8, 0x0918, 0x0c68,
        0x02d6, 0x1869, 0x06f8, 0x0939, 0x1cca, 0x05a8, 0x1aea, 0x1c0a,
        0x1489, 0x14a9, 0x0829, 0x19fa, 0x1719, 0x1209, 0x0e79, 0x1f3a,
        0x14b9, 0x1009, 0x1909, 0x0136, 0x1619, 0x1259, 0x1339, 0x1959,
        0x1739, 0x1ca9, 0x0869, 0x1e99, 0x0db9, 0x1ec9, 0x08b9, 0x0859,
        0x00a5, 0x0968, 0x09c8, 0x1c39, 0x19c9, 0x08f9, 0x18f9, 0x0919,
        0x0879, 0x0c69, 0x1779, 0x0899, 0x0d69, 0x08c9, 0x1ee9, 0x1eb9,
        0x0849, 0x1649, 0x1759, 0x1cd9, 0x05e8, 0x0889, 0x12b9, 0x1729,
        0x10a9, 0x08d9, 0x13a9, 0x11c9, 0x1e1a, 0x1e0a, 0x1879, 0x1dca,
        0x1dfa, 0x0747, 0x19f9, 0x08d8, 0x0e48, 0x0797, 0x0ea9, 0x0e19,
        0x0408, 0x0417, 0x10b9, 0x0b09, 0x06a8, 0x0c18, 0x0717, 0x0787,
        0x0b18, 0x14c9, 0x0437, 0x0768, 0x0667, 0x04d7, 0x08a9, 0x02f6,
        0x0c98, 0x0ce9, 0x1499, 0x1609, 0x1baa, 0x19ea, 0x39fa, 0x0e59,
        0x1949, 0x1849, 0x1269, 0x0307, 0x06c8, 0x1219, 0x1e89, 0x1c1a,
        0x11da, 0x163a, 0x385a, 0x3dba, 0x17da, 0x106a, 0x397a, 0x24ea,
        0x02e7, 0x0988, 0x33ca, 0x32ea, 0x1e9a, 0x0bf9, 0x3dfa, 0x1dda,
        0x32da, 0x2eda, 0x30ba, 0x107a, 0x2e8a, 0x3dea, 0x125a, 0x1e8a,
        0x0e99, 0x1cda, 0x1b5a, 0x1659, 0x232a, 0x2e1a, 0x3aeb, 0x3c6b,
        0x3e2b, 0x205a, 0x29aa, 0x248a, 0x2cda, 0x23ba, 0x3c5b, 0x251a,
        0x2e9a, 0x252a, 0x1ea9, 0x3a0b, 0x391b, 0x23ca, 0x392b, 0x3d5b,
        0x233a, 0x2cca, 0x390b, 0x1bba, 0x3a1b, 0x3c4b, 0x211a, 0x203a,
        0x12a9, 0x231a, 0x3e0b, 0x29ba, 0x3d7b, 0x202a, 0x3adb, 0x213a,
        0x253a, 0x32ca, 0x23da, 0x23fa, 0x32fa, 0x11ca, 0x384a, 0x31ca,
        0x17ca, 0x30aa, 0x2e0a, 0x276a, 0x250a, 0x3e3b, 0x396a, 0x18fa,
        0x204a, 0x206a, 0x230a, 0x265a, 0x212a, 0x23ea, 0x3acb, 0x393b,
        0x3e1b, 0x1dea, 0x3d6b, 0x31da, 0x3e5b, 0x3e4b, 0x207a, 0x3c7b,
        0x277a, 0x3d4b, 0x0c08, 0x162a, 0x3daa, 0x124a, 0x1b4a, 0x264a,
        0x33da, 0x1d1a, 0x1afa, 0x39ea, 0x24fa, 0x373b, 0x249a, 0x372b,
        0x1679, 0x210a, 0x23aa, 0x1b8a, 0x3afb, 0x18ea, 0x2eca, 0x0627,
        0x00d4  // terminator (entry 256)
    };

    /// <summary>
    /// Decompress Huffman-encoded data from the client (uses DecompTree).
    /// Returns decompressed byte array.
    /// </summary>
    public static byte[] Decompress(byte[] input, int offset, int length)
    {
        const int MaxDecompressedSize = 262_144; // 256 KB safety cap
        var output = new List<byte>(length * 2);
        int bitPos = 0;
        int totalBits = length * 8;

        while (bitPos < totalBits)
        {
            int node = 0;

            while (node >= 0)
            {
                if (bitPos >= totalBits) return output.ToArray();

                int byteIdx = offset + (bitPos >> 3);
                int bitIdx = 7 - (bitPos & 7);
                int bit = (input[byteIdx] >> bitIdx) & 1;
                bitPos++;

                node = DecompTree[node, bit];
            }

            int value = ~node;
            if (value == 256) break;
            output.Add((byte)value);
            if (output.Count >= MaxDecompressedSize) break;
        }

        return output.ToArray();
    }

    // Decompression tree built from CompressBase (lazy initialized)
    private static int[,]? _serverDecompTree;
    private static readonly object _treeLock = new();

    /// <summary>
    /// Build decompression tree from CompressBase table.
    /// This tree decodes data that was compressed using Compress().
    /// </summary>
    private static int[,] BuildServerDecompTree()
    {
        // Max tree size - each code path needs nodes
        // Use int.MinValue as "uninitialized" marker (not -1 since that could be a valid leaf)
        const int Uninitialized = int.MinValue;
        var tree = new int[1024, 2];
        int nextNode = 1; // Node 0 is root
        
        // Initialize all nodes to uninitialized
        for (int i = 0; i < 1024; i++)
        {
            tree[i, 0] = Uninitialized;
            tree[i, 1] = Uninitialized;
        }

        // Build tree from CompressBase entries
        for (int byteVal = 0; byteVal <= 256; byteVal++)
        {
            ushort entry = CompressBase[byteVal];
            int nBits = entry & 0xF;
            int code = entry >> 4;

            if (nBits == 0) continue;

            int node = 0;
            for (int bitIdx = nBits - 1; bitIdx >= 0; bitIdx--)
            {
                int bit = (code >> bitIdx) & 1;
                
                if (bitIdx == 0)
                {
                    // Leaf node - store negative value (-(byteVal + 1))
                    // -1 = byte 0, -2 = byte 1, ..., -257 = byte 256 (EOF)
                    tree[node, bit] = -(byteVal + 1);
                }
                else
                {
                    // Internal node - create child if needed
                    if (tree[node, bit] == Uninitialized)
                    {
                        tree[node, bit] = nextNode++;
                    }
                    // Move to child node (must be positive)
                    node = tree[node, bit];
                }
            }
        }

        return tree;
    }

    /// <summary>
    /// Decompress data that was compressed using Compress() (CompressBase table).
    /// Use this for decoding server→client data on the client side.
    /// </summary>
    public static byte[] DecompressFromServer(byte[] input, int offset, int length)
    {
        return DecompressFromServer(input, offset, length, out _);
    }

    /// <summary>
    /// Decompress data that was compressed using Compress() (CompressBase table).
    /// Returns number of input bytes consumed (for handling multiple compressed packets).
    /// </summary>
    public static byte[] DecompressFromServer(byte[] input, int offset, int length, out int bytesConsumed)
    {
        bytesConsumed = length;

        if (_serverDecompTree == null)
        {
            lock (_treeLock)
            {
                _serverDecompTree ??= BuildServerDecompTree();
            }
        }

        const int MaxDecompressedSize = 262_144;
        var pool = ArrayPool<byte>.Shared;
        int capacity = Math.Min(length * 2, MaxDecompressedSize);
        var output = pool.Rent(capacity);
        int outLen = 0;

        try
        {
            int bitPos = 0;
            int totalBits = length * 8;

            while (bitPos < totalBits)
            {
                int node = 0;

                while (node >= 0)
                {
                    if (bitPos >= totalBits)
                    {
                        bytesConsumed = length;
                        return output.AsSpan(0, outLen).ToArray();
                    }

                    int byteIdx = offset + (bitPos >> 3);
                    if (byteIdx >= offset + length)
                    {
                        bytesConsumed = length;
                        return output.AsSpan(0, outLen).ToArray();
                    }

                    int bitIdx = 7 - (bitPos & 7);
                    int bit = (input[byteIdx] >> bitIdx) & 1;
                    bitPos++;

                    int next = _serverDecompTree[node, bit];
                    if (next == int.MinValue)
                    {
                        bytesConsumed = (bitPos + 7) >> 3;
                        return output.AsSpan(0, outLen).ToArray();
                    }
                    node = next;
                }

                int value = -(node + 1);
                if (value == 256)
                {
                    bytesConsumed = (bitPos + 7) >> 3;
                    return output.AsSpan(0, outLen).ToArray();
                }
                if (value < 0 || value > 255) break;

                if (outLen >= output.Length)
                {
                    var bigger = pool.Rent(output.Length * 2);
                    Array.Copy(output, bigger, outLen);
                    pool.Return(output);
                    output = bigger;
                }
                output[outLen++] = (byte)value;
                if (outLen >= MaxDecompressedSize) break;
            }

            bytesConsumed = length;
            return output.AsSpan(0, outLen).ToArray();
        }
        finally
        {
            pool.Return(output);
        }
    }

    /// <summary>
    /// Compress data for sending to the client (outgoing packets).
    /// Direct port of Source-X CHuffman::Compress using kxCompress_Base table.
    /// </summary>
    public static byte[] Compress(byte[] input, int offset, int length)
    {
        int initialSize = length * 2 + 4;
        var pool = ArrayPool<byte>.Shared;
        var output = pool.Rent(initialSize);
        try
        {
            uint iLen = 0;
            byte bitIdx = 0;
            byte xOutVal = 0;

            for (int i = 0; i <= length; i++)
            {
                ushort value = CompressBase[i == length ? 256 : input[offset + i]];
                byte nBits = (byte)(value & 0xF);
                value >>= 4;

                while (nBits > 0)
                {
                    if (iLen >= (uint)output.Length)
                    {
                        var bigger = pool.Rent(output.Length * 2);
                        Array.Copy(output, bigger, output.Length);
                        pool.Return(output);
                        output = bigger;
                    }

                    nBits--;
                    xOutVal = (byte)((uint)xOutVal << 1);
                    xOutVal |= (byte)(((uint)value >> nBits) & 0x1u);

                    if (++bitIdx == 8)
                    {
                        bitIdx = 0;
                        output[iLen++] = xOutVal;
                    }
                }
            }

            if (bitIdx > 0)
            {
                if (iLen < (uint)output.Length)
                    output[iLen++] = (byte)((uint)xOutVal << (8 - bitIdx));
            }

            var result = new byte[iLen];
            Array.Copy(output, result, (int)iLen);
            return result;
        }
        finally
        {
            pool.Return(output);
        }
    }
}
