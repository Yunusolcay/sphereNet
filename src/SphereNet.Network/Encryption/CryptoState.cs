using System.Collections.Concurrent;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;

namespace SphereNet.Network.Encryption;

/// <summary>
/// Per-connection crypto state. Handles auto-detection and decryption
/// of incoming client data. Maps to CCrypto in Source-X.
/// </summary>
public sealed class CryptoState
{
    private LoginEncryption? _loginCrypt;
    private TwofishGameEncryption? _twofishCrypt;
    private Md5GameEncryption? _md5Encrypt; // MD5-based outgoing encryption (Source-X EncryptMD5)
    private EncryptionType _encType = EncryptionType.None;
    private uint _key1;
    private uint _key2;
    private uint _seed;
    private bool _initialized;

    public bool IsInitialized => _initialized;
    public EncryptionType EncType => _encType;
    public uint Key1 => _key1;
    public uint Key2 => _key2;

    /// <summary>Client version number recovered from relay keys during game login detection.</summary>
    public uint RelayClientVersion { get; private set; }

    /// <summary>
    /// Pending relay keys: authId → (MasterHi=Key1, MasterLo=Key2) from the login detection.
    /// Source-X RelayGameCryptStart uses these to derive the game Twofish seed.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, (uint Key1, uint Key2, uint ClientVersion)> _pendingRelays = new();

    public static void StoreRelayKeys(uint authId, uint key1, uint key2, uint clientVersion = 0)
    {
        _pendingRelays[authId] = (key1, key2, clientVersion);
    }

    public static bool TryGetRelayKeys(uint authId, out uint key1, out uint key2, out uint clientVersion)
    {
        if (_pendingRelays.TryRemove(authId, out var keys))
        {
            key1 = keys.Key1;
            key2 = keys.Key2;
            clientVersion = keys.ClientVersion;
            return true;
        }
        key1 = key2 = 0;
        clientVersion = 0;
        return false;
    }

    public byte[]? DetectAndDecryptLogin(uint seed, ReadOnlySpan<byte> rawData, CryptConfig cryptConfig, bool useCrypt, bool useNoCrypt)
    {
        _seed = seed;

        if (useNoCrypt)
        {
            if (rawData[0] == 0x80 && rawData.Length >= 62 && rawData[30] == 0x00 && rawData[60] == 0x00)
            {
                _encType = EncryptionType.None;
                _initialized = true;
                return rawData.ToArray();
            }
        }

        if (!useCrypt)
            return null;

        foreach (var clientKey in cryptConfig.Keys)
        {
            var testCrypt = new LoginEncryption(seed, clientKey.Key1, clientKey.Key2);
            byte[] testBuf = rawData.ToArray();
            testCrypt.Decrypt(testBuf, 0, testBuf.Length);

            if (testBuf[0] == 0x80 && testBuf.Length >= 62 && testBuf[30] == 0x00 && testBuf[60] == 0x00)
            {
                bool valid = true;
                for (int i = 21; i <= 30 && valid; i++)
                    valid = testBuf[i] == 0x00 && testBuf[i + 30] == 0x00;

                if (valid)
                {
                    _key1 = clientKey.Key1;
                    _key2 = clientKey.Key2;
                    _encType = clientKey.EncType;
                    _loginCrypt = testCrypt;
                    _initialized = true;
                    return testBuf;
                }
            }
        }

        if (rawData[0] == 0x80 && rawData.Length >= 62)
        {
            _encType = EncryptionType.None;
            _initialized = true;
            return rawData.ToArray();
        }

        return null;
    }

    /// <summary>
    /// Decrypt incoming data on an already-initialized connection.
    /// </summary>
    public void Decrypt(byte[] data, int offset, int length)
    {
        if (_encType == EncryptionType.None)
            return;

        if (_twofishCrypt != null)
        {
            _twofishCrypt.Decrypt(data, offset, length);
            return;
        }

        _loginCrypt?.Decrypt(data, offset, length);
    }

    /// <summary>
    /// Encrypt outgoing data (after Huffman compression) for game connections.
    /// Source-X CNetworkOutput: compress → EncryptMD5 → send.
    /// Uses MD5 digest of the initial Twofish cipher table (not Twofish itself).
    /// </summary>
    public void Encrypt(byte[] data, int offset, int length)
    {
        if (_encType == EncryptionType.None)
            return;

        _md5Encrypt?.Encrypt(data, offset, length);
    }

    /// <summary>
    /// Detect encryption on a game login (0x91) connection after relay.
    /// Implements Source-X RelayGameCryptStart: derives Twofish seed from
    /// master keys and authId, then applies both Twofish and login XOR decryption.
    /// </summary>
    public byte[]? DetectAndDecryptGameLogin(uint newSeed, ReadOnlySpan<byte> rawData,
        CryptConfig cryptConfig, bool useCrypt, bool useNoCrypt)
    {
        _seed = newSeed;

        // 1) ENC_NONE — check unencrypted
        if (useNoCrypt)
        {
            if (rawData[0] == 0x91 && rawData.Length >= 65 && rawData[34] == 0x00 && rawData[64] == 0x00)
            {
                _encType = EncryptionType.None;
                _initialized = true;
                return rawData.ToArray();
            }
        }

        if (!useCrypt)
            return null;

        // 2) RelayGameCryptStart — exact port of Source-X CCrypto::RelayGameCryptStart.
        if (TryGetRelayKeys(newSeed, out uint relayKey1, out uint relayKey2, out uint relayVer))
        {
            RelayClientVersion = relayVer;
            // Derive new seed (same as Source-X RelayGameCryptStart)
            uint xored = relayKey1 ^ relayKey2;
            uint swapped = ((xored >> 24) & 0xFF) |
                           ((xored >> 8) & 0xFF00) |
                           ((xored << 8) & 0xFF0000) |
                           ((xored << 24) & 0xFF000000);
            uint derivedSeed = swapped ^ newSeed;

            for (int encTry = 0; encTry <= 3; encTry++)
            {
                byte[] testBuf = rawData.ToArray();
                TwofishGameEncryption? thisTf = null;

                if (encTry == 3) // ENC_TFISH
                {
                    thisTf = new TwofishGameEncryption(derivedSeed);
                    thisTf.Decrypt(testBuf, 0, testBuf.Length);
                }
                else if (encTry == 1 || encTry == 2)
                {
                    continue; // Blowfish not implemented
                }

                if (testBuf[0] == 0x91)
                {
                    var loginDecrypt = new LoginEncryption(0, relayKey1, relayKey2, maskLo: 0, maskHi: 0);
                    loginDecrypt.Decrypt(testBuf, 0, testBuf.Length);

                    if (testBuf[0] == 0x91 && testBuf.Length >= 65 && testBuf[34] == 0x00 && testBuf[64] == 0x00)
                    {
                        _key1 = relayKey1;
                        _key2 = relayKey2;
                        _encType = encTry == 3 ? EncryptionType.Twofish : EncryptionType.None;
                        _twofishCrypt = thisTf;
                        _md5Encrypt = thisTf != null ? new Md5GameEncryption(thisTf.Md5Digest) : null;
                        _loginCrypt = null;
                        _initialized = true;
                        return testBuf;
                    }
                }
            }
        }

        // 3) Fallback: GameCryptStart — seed-only Twofish (no relay keys available)
        {
            var testTf = new TwofishGameEncryption(newSeed);
            byte[] testBuf = rawData.ToArray();
            testTf.Decrypt(testBuf, 0, testBuf.Length);

            if (testBuf[0] == 0x91 && testBuf.Length >= 65 && testBuf[34] == 0x00 && testBuf[64] == 0x00)
            {
                _encType = EncryptionType.Twofish;
                _loginCrypt = null;
                _twofishCrypt = testTf;
                _md5Encrypt = new Md5GameEncryption(testTf.Md5Digest);
                _initialized = true;
                return testBuf;
            }
        }

        // 4) LoginXOR fallback — for older clients
        foreach (var clientKey in cryptConfig.Keys)
        {
            if (clientKey.EncType != EncryptionType.Login)
                continue;

            var testCrypt = new LoginEncryption(newSeed, clientKey.Key1, clientKey.Key2);
            byte[] testBuf = rawData.ToArray();
            testCrypt.Decrypt(testBuf, 0, testBuf.Length);

            if (testBuf[0] == 0x91 && testBuf.Length >= 65 && testBuf[34] == 0x00 && testBuf[64] == 0x00)
            {
                _key1 = clientKey.Key1;
                _key2 = clientKey.Key2;
                _encType = clientKey.EncType;
                _loginCrypt = testCrypt;
                _twofishCrypt = null;
                _initialized = true;
                return testBuf;
            }
        }

        if (rawData[0] == 0x91 && rawData.Length >= 65)
        {
            _encType = EncryptionType.None;
            _initialized = true;
            return rawData.ToArray();
        }

        return null;
    }

    public void Reset()
    {
        _loginCrypt = null;
        _twofishCrypt = null;
        _md5Encrypt = null;
        _encType = EncryptionType.None;
        _key1 = 0;
        _key2 = 0;
        _seed = 0;
        _initialized = false;
    }
}
