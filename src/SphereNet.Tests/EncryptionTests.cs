using SphereNet.Network.Encryption;

namespace SphereNet.Tests;

public class EncryptionTests
{
    [Fact]
    public void Blowfish_EncryptDecrypt_RoundTrips()
    {
        var key = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
        var bf = new BlowfishEncryption(key);

        byte[] original = { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE };
        byte[] data = (byte[])original.Clone();

        bf.Encrypt(data, 0, 8);
        Assert.NotEqual(original, data);

        bf.Decrypt(data, 0, 8);
        Assert.Equal(original, data);
    }

    [Fact]
    public void Blowfish_DifferentKeys_ProduceDifferentCiphertext()
    {
        var key1 = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var key2 = new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 };

        byte[] data1 = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        byte[] data2 = (byte[])data1.Clone();

        new BlowfishEncryption(key1).Encrypt(data1, 0, 8);
        new BlowfishEncryption(key2).Encrypt(data2, 0, 8);

        Assert.NotEqual(data1, data2);
    }

    [Fact]
    public void Blowfish_MultiBlock_EncryptDecrypt()
    {
        var key = new byte[] { 0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89 };
        var bf = new BlowfishEncryption(key);

        byte[] original = new byte[24];
        Random.Shared.NextBytes(original);
        byte[] data = (byte[])original.Clone();

        bf.Encrypt(data, 0, 24);
        bf.Decrypt(data, 0, 24);

        Assert.Equal(original, data);
    }

    [Fact]
    public void Twofish_EncryptDecrypt_RoundTrips()
    {
        var key = new byte[16];
        for (int i = 0; i < 16; i++) key[i] = (byte)i;

        var tf = new TwofishEncryption(key);

        byte[] original = new byte[16];
        Random.Shared.NextBytes(original);
        byte[] data = (byte[])original.Clone();

        tf.Encrypt(data, 0, 16);
        Assert.NotEqual(original, data);

        tf.Decrypt(data, 0, 16);
        Assert.Equal(original, data);
    }

    [Fact]
    public void Twofish_DifferentKeys_ProduceDifferentCiphertext()
    {
        var key1 = new byte[16]; key1[0] = 1;
        var key2 = new byte[16]; key2[0] = 2;

        byte[] data1 = new byte[16];
        byte[] data2 = new byte[16]; // same plaintext (zeros)

        new TwofishEncryption(key1).Encrypt(data1, 0, 16);
        new TwofishEncryption(key2).Encrypt(data2, 0, 16);

        Assert.NotEqual(data1, data2);
    }

    [Fact]
    public void Twofish_MultiBlock_RoundTrips()
    {
        var key = new byte[16];
        for (int i = 0; i < 16; i++) key[i] = (byte)(i * 3);
        var tf = new TwofishEncryption(key);

        byte[] original = new byte[48];
        Random.Shared.NextBytes(original);
        byte[] data = (byte[])original.Clone();

        tf.Encrypt(data, 0, 48);
        tf.Decrypt(data, 0, 48);

        Assert.Equal(original, data);
    }

    [Fact]
    public void LoginEncryption_Decrypt_ChangesData()
    {
        var enc = new LoginEncryption(0x12345678, 0, 0);
        byte[] data = { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        byte[] original = (byte[])data.Clone();

        enc.Decrypt(data, 0, 8);
        Assert.NotEqual(original, data);
    }
}
