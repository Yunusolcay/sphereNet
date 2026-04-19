namespace SphereNet.Network.Packets;

/// <summary>
/// Packet router. Maps opcode → handler. Maps to PacketManager in Source-X.
/// Supports standard (single byte opcode), extended (0xBF sub-opcode), and encoded (0xD7 sub-opcode).
/// </summary>
public sealed class PacketManager
{
    private readonly PacketHandler?[] _handlers = new PacketHandler?[256];
    private readonly Dictionary<ushort, PacketHandler> _extendedHandlers = [];
    private readonly Dictionary<ushort, PacketHandler> _encodedHandlers = [];

    public void Register(PacketHandler handler)
    {
        _handlers[handler.PacketId] = handler;
    }

    public void RegisterExtended(ushort subCmd, PacketHandler handler)
    {
        _extendedHandlers[subCmd] = handler;
    }

    public void RegisterEncoded(ushort subCmd, PacketHandler handler)
    {
        _encodedHandlers[subCmd] = handler;
    }

    public PacketHandler? GetHandler(byte opcode) => _handlers[opcode];

    public PacketHandler? GetExtendedHandler(ushort subCmd) =>
        _extendedHandlers.GetValueOrDefault(subCmd);

    public PacketHandler? GetEncodedHandler(ushort subCmd) =>
        _encodedHandlers.GetValueOrDefault(subCmd);

    public void Unregister(byte opcode) => _handlers[opcode] = null;
}
