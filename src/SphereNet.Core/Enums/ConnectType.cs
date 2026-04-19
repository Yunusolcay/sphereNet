namespace SphereNet.Core.Enums;

/// <summary>
/// Connection type. Maps exactly to CONNECT_TYPE in Source-X crypto_common.h.
/// </summary>
public enum ConnectType : byte
{
    None = 0,
    Unknown = 1,
    Crypt = 2,
    Login = 3,
    Game = 4,
    Http = 5,
    Telnet = 6,
    UOG = 7,
    Axis = 8,

    Qty = 9,
}

/// <summary>
/// Encryption type. Maps exactly to ENCRYPTION_TYPE in Source-X crypto_common.h.
/// </summary>
public enum EncryptionType : byte
{
    None = 0,
    Blowfish = 1,
    BlowfishTwofish = 2,
    Twofish = 3,
    Login = 4,

    Qty = 5,
}
