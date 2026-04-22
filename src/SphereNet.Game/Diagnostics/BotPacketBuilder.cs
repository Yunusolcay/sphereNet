using System.Text;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Builds UO protocol packets for bot clients.
/// These packets mimic what a real UO client would send.
/// </summary>
public static class BotPacketBuilder
{
    /// <summary>Build 0xEF Login Seed packet (21 bytes).</summary>
    public static byte[] BuildLoginSeed(uint seed, uint clientMajor = 7, uint clientMinor = 0, uint clientRevision = 0, uint clientPatch = 0)
    {
        var packet = new byte[21];
        packet[0] = 0xEF;
        WriteUInt32BE(packet, 1, seed);
        WriteUInt32BE(packet, 5, clientMajor);
        WriteUInt32BE(packet, 9, clientMinor);
        WriteUInt32BE(packet, 13, clientRevision);
        WriteUInt32BE(packet, 17, clientPatch);
        return packet;
    }

    /// <summary>Build 0x80 Account Login packet (62 bytes).</summary>
    public static byte[] BuildAccountLogin(string account, string password)
    {
        var packet = new byte[62];
        packet[0] = 0x80;
        WriteAsciiFixed(packet, 1, account, 30);
        WriteAsciiFixed(packet, 31, password, 30);
        packet[61] = 0x00; // next login key
        return packet;
    }

    /// <summary>Build 0xA0 Server Select packet (3 bytes).</summary>
    public static byte[] BuildServerSelect(ushort serverIndex = 0)
    {
        var packet = new byte[3];
        packet[0] = 0xA0;
        WriteUInt16BE(packet, 1, serverIndex);
        return packet;
    }

    /// <summary>Build 0x91 Game Server Login packet (65 bytes).</summary>
    public static byte[] BuildGameLogin(string account, string password, uint authId)
    {
        var packet = new byte[65];
        packet[0] = 0x91;
        WriteUInt32BE(packet, 1, authId);
        WriteAsciiFixed(packet, 5, account, 30);
        WriteAsciiFixed(packet, 35, password, 30);
        return packet;
    }

    /// <summary>Build 0x5D Character Select packet (73 bytes).</summary>
    public static byte[] BuildCharSelect(int slotIndex, string charName)
    {
        var packet = new byte[73];
        packet[0] = 0x5D;
        WriteUInt32BE(packet, 1, 0xEDEDEDED); // pattern1
        WriteAsciiFixed(packet, 5, charName, 30);
        WriteUInt16BE(packet, 35, 0); // unknown
        WriteUInt32BE(packet, 37, 0x1F); // client flags (T2A+)
        WriteUInt32BE(packet, 41, 0xEDEDEDED); // pattern2
        WriteUInt32BE(packet, 45, 1); // login count
        // 16 bytes padding at 49
        WriteInt32BE(packet, 65, slotIndex);
        // 4 bytes clientIP at 69
        return packet;
    }

    /// <summary>Build 0x02 Move Request packet (7 bytes).</summary>
    public static byte[] BuildMoveRequest(byte direction, byte sequence, uint fastWalkKey = 0)
    {
        var packet = new byte[7];
        packet[0] = 0x02;
        packet[1] = direction;
        packet[2] = sequence;
        WriteUInt32BE(packet, 3, fastWalkKey);
        return packet;
    }

    /// <summary>Build 0x05 Attack Request packet (5 bytes).</summary>
    public static byte[] BuildAttackRequest(uint targetUid)
    {
        var packet = new byte[5];
        packet[0] = 0x05;
        WriteUInt32BE(packet, 1, targetUid);
        return packet;
    }

    /// <summary>Build 0x06 Double Click packet (5 bytes).</summary>
    public static byte[] BuildDoubleClick(uint targetUid)
    {
        var packet = new byte[5];
        packet[0] = 0x06;
        WriteUInt32BE(packet, 1, targetUid);
        return packet;
    }

    /// <summary>Build 0x72 War Mode packet (5 bytes).</summary>
    public static byte[] BuildWarMode(bool warMode)
    {
        var packet = new byte[5];
        packet[0] = 0x72;
        packet[1] = warMode ? (byte)1 : (byte)0;
        // 3 bytes unknown
        return packet;
    }

    /// <summary>Build 0x73 Ping packet (2 bytes).</summary>
    public static byte[] BuildPing(byte sequence)
    {
        return [0x73, sequence];
    }

    /// <summary>Build 0xBF subcommand 0x05 - Screen size (sent after game login).</summary>
    public static byte[] BuildScreenSize(ushort width = 800, ushort height = 600)
    {
        var packet = new byte[13];
        packet[0] = 0xBF;
        WriteUInt16BE(packet, 1, 13); // length
        WriteUInt16BE(packet, 3, 0x05); // subcommand: screen size
        WriteUInt32BE(packet, 5, 0); // unknown
        WriteUInt16BE(packet, 9, width);
        WriteUInt16BE(packet, 11, height);
        return packet;
    }

    /// <summary>Build 0xBF subcommand 0x0B - Client language (sent after game login).</summary>
    public static byte[] BuildClientLanguage(string lang = "ENU")
    {
        int len = 6 + lang.Length + 1;
        var packet = new byte[len];
        packet[0] = 0xBF;
        WriteUInt16BE(packet, 1, (ushort)len);
        WriteUInt16BE(packet, 3, 0x0B); // subcommand: language
        Encoding.ASCII.GetBytes(lang, 0, lang.Length, packet, 5);
        packet[len - 1] = 0;
        return packet;
    }

    /// <summary>Build 0x12 Skill Use packet (variable length).</summary>
    public static byte[] BuildSkillUse(ushort skillId)
    {
        string cmd = $" {skillId} 0";
        int len = 4 + cmd.Length + 1;
        var packet = new byte[len];
        packet[0] = 0x12;
        WriteUInt16BE(packet, 1, (ushort)len);
        packet[3] = 0x24; // skill use command type
        Encoding.ASCII.GetBytes(cmd, 0, cmd.Length, packet, 4);
        packet[len - 1] = 0; // null terminator
        return packet;
    }

    /// <summary>Build 0x03 Speech packet (variable length).</summary>
    public static byte[] BuildSpeech(string text, byte type = 0, ushort hue = 0x03B2, ushort font = 3)
    {
        int len = 8 + text.Length + 1;
        var packet = new byte[len];
        packet[0] = 0x03;
        WriteUInt16BE(packet, 1, (ushort)len);
        packet[3] = type;
        WriteUInt16BE(packet, 4, hue);
        WriteUInt16BE(packet, 6, font);
        Encoding.ASCII.GetBytes(text, 0, text.Length, packet, 8);
        packet[len - 1] = 0;
        return packet;
    }

    private static void WriteUInt32BE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteInt32BE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteUInt16BE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteAsciiFixed(byte[] buf, int offset, string text, int length)
    {
        int copyLen = Math.Min(text.Length, length);
        Encoding.ASCII.GetBytes(text, 0, copyLen, buf, offset);
    }
}
