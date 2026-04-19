namespace SphereNet.Network.Packets;

/// <summary>
/// Base class for incoming packets (client → server).
/// Maps to Packet class in Source-X receive.h.
/// </summary>
public abstract class PacketHandler
{
    public byte PacketId { get; }
    public int ExpectedLength { get; }

    protected PacketHandler(byte packetId, int expectedLength)
    {
        PacketId = packetId;
        ExpectedLength = expectedLength;
    }

    /// <summary>
    /// Process the received packet. Called from NetworkInput after decryption.
    /// </summary>
    public abstract void OnReceive(PacketBuffer buffer, State.NetState state);
}

/// <summary>
/// Base class for outgoing packets (server → client).
/// Maps to PacketSend class in Source-X send.h.
/// </summary>
public abstract class PacketWriter
{
    public byte PacketId { get; }

    protected PacketWriter(byte packetId)
    {
        PacketId = packetId;
    }

    /// <summary>
    /// Build the packet into the buffer.
    /// </summary>
    public abstract PacketBuffer Build();

    /// <summary>
    /// Create a fixed-length packet buffer with the opcode written.
    /// </summary>
    protected PacketBuffer CreateFixed(int totalLength)
    {
        var buf = new PacketBuffer(totalLength);
        buf.WriteByte(PacketId);
        return buf;
    }

    /// <summary>
    /// Create a variable-length packet buffer with opcode and placeholder length.
    /// Call buf.WriteLengthAt(1) when done writing.
    /// </summary>
    protected PacketBuffer CreateVariable(int estimatedSize = 128)
    {
        var buf = new PacketBuffer(estimatedSize);
        buf.WriteByte(PacketId);
        buf.WriteUInt16(0); // placeholder for length
        return buf;
    }
}
