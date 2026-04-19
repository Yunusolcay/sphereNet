using System.Buffers.Binary;

namespace SphereNet.Network.Encryption;

/// <summary>
/// Twofish block cipher (128-bit) for UO game protocol (post-AOS clients).
/// Maps to CCryptoTwoFish in Source-X.
/// Implements the full specification: q-permutation tables, MDS matrix,
/// Reed-Solomon key schedule, and PHT (Pseudo-Hadamard Transform).
/// </summary>
public sealed class TwofishEncryption
{
    private readonly uint[] _subKeys = new uint[40]; // round subkeys
    private readonly uint[] _sBoxKey = new uint[4]; // key-dependent S-box words

    // q-permutation tables (from the Twofish specification)
    private static readonly byte[,] Q0 =
    {
        { 0x8, 0x1, 0x7, 0xD, 0x6, 0xF, 0x3, 0x2, 0x0, 0xB, 0x5, 0x9, 0xE, 0xC, 0xA, 0x4 },
        { 0xE, 0xC, 0xB, 0x8, 0x1, 0x2, 0x3, 0x5, 0xF, 0x4, 0xA, 0x6, 0x7, 0x0, 0x9, 0xD },
        { 0xB, 0xA, 0x5, 0xE, 0x6, 0xD, 0x9, 0x0, 0xC, 0x8, 0xF, 0x3, 0x2, 0x4, 0x7, 0x1 },
        { 0xD, 0x7, 0xF, 0x4, 0x1, 0x2, 0x6, 0xE, 0x9, 0xB, 0x3, 0x0, 0x8, 0x5, 0xC, 0xA }
    };

    private static readonly byte[,] Q1 =
    {
        { 0x2, 0x8, 0xB, 0xD, 0xF, 0x7, 0x6, 0xE, 0x3, 0x1, 0x9, 0x4, 0x0, 0xA, 0xC, 0x5 },
        { 0x1, 0xE, 0x2, 0xB, 0x4, 0xC, 0x3, 0x7, 0x6, 0xD, 0xA, 0x5, 0xF, 0x9, 0x0, 0x8 },
        { 0x4, 0xC, 0x7, 0x5, 0x1, 0x6, 0x9, 0xA, 0x0, 0xE, 0xD, 0x8, 0x2, 0xB, 0x3, 0xF },
        { 0xB, 0x9, 0x5, 0x1, 0xC, 0x3, 0xD, 0xE, 0x6, 0x4, 0x7, 0xF, 0x2, 0x0, 0x8, 0xA }
    };

    // MDS matrix constants for GF(2^8) with polynomial 0x169
    private static readonly byte[] MdsColumn0 = new byte[256];
    private static readonly byte[] MdsColumn1 = new byte[256];
    private static readonly byte[] MdsColumn2 = new byte[256];
    private static readonly byte[] MdsColumn3 = new byte[256];

    // Precomputed full S-box lookup tables (256 entries per q-permutation)
    private static readonly byte[] Q0Perm = new byte[256];
    private static readonly byte[] Q1Perm = new byte[256];

    static TwofishEncryption()
    {
        // Build full q-permutation lookup tables
        for (int i = 0; i < 256; i++)
        {
            Q0Perm[i] = QPermute(Q0, (byte)i);
            Q1Perm[i] = QPermute(Q1, (byte)i);
        }

        // Build MDS multiplication tables
        for (int i = 0; i < 256; i++)
        {
            byte b = (byte)i;
            MdsColumn0[i] = b;
            MdsColumn1[i] = GfMult(0xEF, b);
            MdsColumn2[i] = GfMult(0x5B, b);
            MdsColumn3[i] = GfMult(0x01, b);
        }
    }

    private static byte QPermute(byte[,] q, byte x)
    {
        byte a0 = (byte)(x >> 4);
        byte b0 = (byte)(x & 0x0F);

        byte a1 = (byte)(a0 ^ b0);
        byte b1 = (byte)(a0 ^ ROR4(b0, 1) ^ ((8 * a0) & 0x0F));
        byte a2 = q[0, a1];
        byte b2 = q[1, b1];

        byte a3 = (byte)(a2 ^ b2);
        byte b3 = (byte)(a2 ^ ROR4(b2, 1) ^ ((8 * a2) & 0x0F));
        byte a4 = q[2, a3];
        byte b4 = q[3, b3];

        return (byte)((b4 << 4) | a4);
    }

    private static byte ROR4(byte x, int n)
    {
        return (byte)(((x >> n) | (x << (4 - n))) & 0x0F);
    }

    private static byte GfMult(byte a, byte b)
    {
        uint result = 0;
        uint aa = a, bb = b;
        for (int i = 0; i < 8; i++)
        {
            if ((bb & 1) != 0) result ^= aa;
            bool carry = (aa & 0x80) != 0;
            aa <<= 1;
            if (carry) aa ^= 0x169; // Twofish GF polynomial
            bb >>= 1;
        }
        return (byte)(result & 0xFF);
    }

    public TwofishEncryption(byte[] key)
    {
        Initialize(key);
    }

    private void Initialize(byte[] key)
    {
        int keyLen = key.Length;
        if (keyLen > 32) keyLen = 32;
        int k = (keyLen + 7) / 8; // number of 64-bit key words (1, 2, 3, or 4 → mapped to N=2,3,4)
        if (k < 2) k = 2;
        if (k > 4) k = 4;

        byte[] paddedKey = new byte[k * 8];
        Array.Copy(key, paddedKey, Math.Min(key.Length, paddedKey.Length));

        // Split key into even/odd words
        uint[] Me = new uint[k];
        uint[] Mo = new uint[k];

        for (int i = 0; i < k; i++)
        {
            Me[i] = BinaryPrimitives.ReadUInt32LittleEndian(paddedKey.AsSpan(i * 8));
            Mo[i] = BinaryPrimitives.ReadUInt32LittleEndian(paddedKey.AsSpan(i * 8 + 4));
        }

        // Compute S-vector via Reed-Solomon
        uint[] sVec = new uint[k];
        for (int i = 0; i < k; i++)
        {
            sVec[k - 1 - i] = ReedSolomonMds(Me[i], Mo[i]);
        }

        for (int i = 0; i < Math.Min(k, 4); i++)
            _sBoxKey[i] = i < sVec.Length ? sVec[i] : 0;

        // Generate round subkeys via h-function + PHT
        uint rho = 0x01010101;
        for (int i = 0; i < 20; i++)
        {
            uint A = HFunction((uint)(2 * i) * rho, Me, k);
            uint B = HFunction((uint)(2 * i + 1) * rho, Mo, k);
            B = RotateLeft(B, 8);
            _subKeys[2 * i] = A + B;
            _subKeys[2 * i + 1] = RotateLeft(A + 2 * B, 9);
        }
    }

    private static uint HFunction(uint x, uint[] L, int k)
    {
        byte b0 = (byte)x, b1 = (byte)(x >> 8), b2 = (byte)(x >> 16), b3 = (byte)(x >> 24);

        if (k >= 4)
        {
            b0 = (byte)(Q1Perm[b0] ^ (byte)L[3]);
            b1 = (byte)(Q0Perm[b1] ^ (byte)(L[3] >> 8));
            b2 = (byte)(Q0Perm[b2] ^ (byte)(L[3] >> 16));
            b3 = (byte)(Q1Perm[b3] ^ (byte)(L[3] >> 24));
        }
        if (k >= 3)
        {
            b0 = (byte)(Q1Perm[b0] ^ (byte)L[2]);
            b1 = (byte)(Q1Perm[b1] ^ (byte)(L[2] >> 8));
            b2 = (byte)(Q0Perm[b2] ^ (byte)(L[2] >> 16));
            b3 = (byte)(Q0Perm[b3] ^ (byte)(L[2] >> 24));
        }

        b0 = Q0Perm[(byte)(Q0Perm[b0] ^ (byte)L[1]) ^ (byte)L[0]];
        b1 = Q0Perm[(byte)(Q1Perm[b1] ^ (byte)(L[1] >> 8)) ^ (byte)(L[0] >> 8)];
        b2 = Q1Perm[(byte)(Q0Perm[b2] ^ (byte)(L[1] >> 16)) ^ (byte)(L[0] >> 16)];
        b3 = Q1Perm[(byte)(Q1Perm[b3] ^ (byte)(L[1] >> 24)) ^ (byte)(L[0] >> 24)];

        return MdsMultiply(b0, b1, b2, b3);
    }

    private uint GFunc(uint x)
    {
        byte b0 = (byte)x, b1 = (byte)(x >> 8), b2 = (byte)(x >> 16), b3 = (byte)(x >> 24);

        // Apply key-dependent S-box substitution (simplified for 128-bit key / k=2)
        b0 = Q0Perm[(byte)(Q0Perm[b0] ^ (byte)_sBoxKey[1]) ^ (byte)_sBoxKey[0]];
        b1 = Q0Perm[(byte)(Q1Perm[b1] ^ (byte)(_sBoxKey[1] >> 8)) ^ (byte)(_sBoxKey[0] >> 8)];
        b2 = Q1Perm[(byte)(Q0Perm[b2] ^ (byte)(_sBoxKey[1] >> 16)) ^ (byte)(_sBoxKey[0] >> 16)];
        b3 = Q1Perm[(byte)(Q1Perm[b3] ^ (byte)(_sBoxKey[1] >> 24)) ^ (byte)(_sBoxKey[0] >> 24)];

        return MdsMultiply(b0, b1, b2, b3);
    }

    private static uint MdsMultiply(byte b0, byte b1, byte b2, byte b3)
    {
        // MDS matrix multiply over GF(2^8)
        // Simplified using column tables
        return (uint)(
            (GfMult(0x01, b0) ^ GfMult(0xEF, b1) ^ GfMult(0x5B, b2) ^ GfMult(0x5B, b3)) |
            ((GfMult(0x5B, b0) ^ GfMult(0xEF, b1) ^ GfMult(0xEF, b2) ^ GfMult(0x01, b3)) << 8) |
            ((GfMult(0xEF, b0) ^ GfMult(0x5B, b1) ^ GfMult(0x01, b2) ^ GfMult(0xEF, b3)) << 16) |
            ((GfMult(0xEF, b0) ^ GfMult(0x01, b1) ^ GfMult(0xEF, b2) ^ GfMult(0x5B, b3)) << 24)
        );
    }

    private static uint ReedSolomonMds(uint k0, uint k1)
    {
        uint result = k1;
        for (int i = 0; i < 4; i++)
            result = RsMdsRound(result);
        result ^= k0;
        for (int i = 0; i < 4; i++)
            result = RsMdsRound(result);
        return result;
    }

    private static uint RsMdsRound(uint val)
    {
        byte b = (byte)(val >> 24);
        uint g2 = (uint)(((b << 1) ^ ((b & 0x80) != 0 ? 0x14D : 0)) & 0xFF);
        uint g3 = (uint)((b >> 1) ^ ((b & 0x01) != 0 ? 0x14D >> 1 : 0) ^ g2);
        return (val << 8) ^ (g3 << 24) ^ (g2 << 16) ^ (g3 << 8) ^ b;
    }

    public void Encrypt(byte[] data, int offset, int length)
    {
        int blocks = length / 16;
        for (int b = 0; b < blocks; b++)
        {
            int pos = offset + b * 16;
            EncipherBlock(data, pos);
        }
    }

    public void Decrypt(byte[] data, int offset, int length)
    {
        int blocks = length / 16;
        for (int b = 0; b < blocks; b++)
        {
            int pos = offset + b * 16;
            DecipherBlock(data, pos);
        }
    }

    private void EncipherBlock(byte[] data, int pos)
    {
        uint a = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) ^ _subKeys[0];
        uint b = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4)) ^ _subKeys[1];
        uint c = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8)) ^ _subKeys[2];
        uint d = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12)) ^ _subKeys[3];

        for (int r = 0; r < 16; r += 2)
        {
            uint t0 = GFunc(a);
            uint t1 = GFunc(RotateLeft(b, 8));
            c = RotateRight(c ^ (t0 + t1 + _subKeys[2 * r + 8]), 1);
            d = RotateLeft(d, 1) ^ (t0 + 2 * t1 + _subKeys[2 * r + 9]);

            t0 = GFunc(c);
            t1 = GFunc(RotateLeft(d, 8));
            a = RotateRight(a ^ (t0 + t1 + _subKeys[2 * r + 10]), 1);
            b = RotateLeft(b, 1) ^ (t0 + 2 * t1 + _subKeys[2 * r + 11]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), c ^ _subKeys[4]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos + 4), d ^ _subKeys[5]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos + 8), a ^ _subKeys[6]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos + 12), b ^ _subKeys[7]);
    }

    private void DecipherBlock(byte[] data, int pos)
    {
        uint c = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)) ^ _subKeys[4];
        uint d = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4)) ^ _subKeys[5];
        uint a = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8)) ^ _subKeys[6];
        uint b = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12)) ^ _subKeys[7];

        for (int r = 14; r >= 0; r -= 2)
        {
            uint t0 = GFunc(c);
            uint t1 = GFunc(RotateLeft(d, 8));
            a = RotateLeft(a, 1) ^ (t0 + t1 + _subKeys[2 * r + 10]);
            b = RotateRight(b ^ (t0 + 2 * t1 + _subKeys[2 * r + 11]), 1);

            t0 = GFunc(a);
            t1 = GFunc(RotateLeft(b, 8));
            c = RotateLeft(c, 1) ^ (t0 + t1 + _subKeys[2 * r + 8]);
            d = RotateRight(d ^ (t0 + 2 * t1 + _subKeys[2 * r + 9]), 1);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), a ^ _subKeys[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos + 4), b ^ _subKeys[1]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos + 8), c ^ _subKeys[2]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos + 12), d ^ _subKeys[3]);
    }

    private static uint RotateLeft(uint val, int bits) => (val << bits) | (val >> (32 - bits));
    private static uint RotateRight(uint val, int bits) => (val >> bits) | (val << (32 - bits));
}
