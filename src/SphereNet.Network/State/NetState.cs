using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;

namespace SphereNet.Network.State;

/// <summary>
/// Per-connection state machine. Maps to CNetState in Source-X.
/// Tracks socket, buffers, crypto state, and connection lifecycle.
/// </summary>
public sealed class NetState : IDisposable
{
    private Socket? _socket;
    private readonly byte[] _recvBuffer = new byte[65536];
    private int _recvLength;
    private readonly object _sendLock = new();
    private readonly Queue<PacketBuffer> _sendQueue = [];

    private readonly ILogger _logger;

    public int Id { get; set; }
    public IPEndPoint? RemoteEndPoint { get; private set; }
    public IPEndPoint? LocalEndPoint { get; private set; }
    public ConnectType ConnectionType { get; set; } = ConnectType.Unknown;
    public bool IsInUse { get; private set; }
    public bool IsClosing { get; private set; }
    public DateTime ConnectTime { get; private set; }
    public long LastActivityTick { get; set; }
    public uint Seed { get; set; }
    public bool IsSeeded { get; set; }

    // Walk buffer for movement sync (Source-X CNetworkInput walk buffer)
    public byte WalkSequence { get; set; }
    public int WalkBufferCount { get; set; }
    public long WalkBufferRegenTime { get; set; }
    public uint LastFastWalkKey { get; set; }

    // Packet flood detection
    public int PacketFloodCount { get; set; }
    public long PacketFloodWindowStart { get; set; }

    // Client info
    public string AccountName { get; set; } = "";
    public uint AuthId { get; set; }
    public int SelectedCharSlot { get; set; } = -1;

    // Encryption
    public CryptoState Crypto { get; } = new CryptoState();
    public uint ClientVersionNumber { get; set; }
    public int UndecryptedOffset { get; set; }

    // Client version breakpoints (UO protocol milestones).
    // When ClientVersionNumber == 0 (undetected), assume modern client — all current
    // clients (ClassicUO, official 7.0.x) require the newer packet formats.
    // Sending old-format packets to a modern client corrupts the TCP byte stream.
    public bool IsClientPost6017  => ClientVersionNumber == 0 || ClientVersionNumber >= 60_001_007; // 6.0.1.7+
    public bool IsClientPost60142 => ClientVersionNumber == 0 || ClientVersionNumber >= 60_014_002; // 6.0.14.2+
    public bool IsClientPost7090  => ClientVersionNumber == 0 || ClientVersionNumber >= 70_009_000; // 7.0.9.0+
    public bool IsClientPost70180 => ClientVersionNumber == 0 || ClientVersionNumber >= 70_018_000; // 7.0.18.0+

    /// <summary>Debug mode — log all outgoing packets.</summary>
    public bool DebugPackets { get; set; }

    public NetState(ILogger logger)
    {
        _logger = logger;
    }

    public void Init(Socket socket)
    {
        _socket = socket;
        _socket.NoDelay = true;
        _socket.LingerState = new LingerOption(false, 0);
        RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
        LocalEndPoint = socket.LocalEndPoint as IPEndPoint;
        ConnectionType = ConnectType.Unknown;
        IsInUse = true;
        IsClosing = false;
        ConnectTime = DateTime.UtcNow;
        LastActivityTick = Environment.TickCount64;
        WalkSequence = 0;
        WalkBufferCount = 0;
        WalkBufferRegenTime = 0;
        LastFastWalkKey = 0;
        _recvLength = 0;
        AccountName = "";
        AuthId = 0;
        SelectedCharSlot = -1;
        IsSeeded = false;
        Seed = 0;
        Crypto.Reset();
        ClientVersionNumber = 0;
        UndecryptedOffset = 0;
    }

    public void Clear()
    {
        try { _socket?.Shutdown(SocketShutdown.Both); }
        catch (Exception ex) { _logger.LogDebug(ex, "Socket shutdown error (client #{Id})", Id); }
        try { _socket?.Close(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Socket close error (client #{Id})", Id); }
        _socket = null;
        IsInUse = false;
        IsClosing = false;
        _recvLength = 0;
        lock (_sendLock) { _sendQueue.Clear(); }
    }

    public bool CanReceive => IsInUse && !IsClosing && _socket is { Connected: true };

    /// <summary>
    /// Try to receive data from the socket into the internal buffer.
    /// Returns number of bytes received, or -1 on error/disconnect.
    /// </summary>
    public int Receive()
    {
        if (_socket == null) return -1;

        try
        {
            if (!_socket.Connected) return -1;
            if (_socket.Available <= 0) return 0;

            int space = _recvBuffer.Length - _recvLength;
            if (space <= 0) return 0;

            int read = _socket.Receive(_recvBuffer, _recvLength, space, SocketFlags.None);
            if (read <= 0) return -1;

            _recvLength += read;
            LastActivityTick = Environment.TickCount64;
            return read;
        }
        catch (SocketException)
        {
            return -1;
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    public ReadOnlySpan<byte> ReceivedData => _recvBuffer.AsSpan(0, _recvLength);

    public void ConsumeReceived(int count)
    {
        if (count >= _recvLength)
        {
            _recvLength = 0;
            UndecryptedOffset = 0;
        }
        else
        {
            Buffer.BlockCopy(_recvBuffer, count, _recvBuffer, 0, _recvLength - count);
            _recvLength -= count;
            UndecryptedOffset = Math.Max(0, UndecryptedOffset - count);
        }
    }

    public void InjectReceived(byte[] data)
    {
        if (data.Length + _recvLength > _recvBuffer.Length)
        {
            MarkClosing();
            return;
        }
        if (_recvLength > 0)
        {
            Buffer.BlockCopy(_recvBuffer, 0, _recvBuffer, data.Length, _recvLength);
        }
        Buffer.BlockCopy(data, 0, _recvBuffer, 0, data.Length);
        _recvLength += data.Length;
    }

    public void ReplaceReceivedRange(int offset, byte[] data)
    {
        Buffer.BlockCopy(data, 0, _recvBuffer, offset, data.Length);
    }

    /// <summary>Enqueue a packet for sending.</summary>
    public void Send(PacketBuffer packet)
    {
        if (!IsInUse || IsClosing) return;
        LastActivityTick = Environment.TickCount64;

        if (DebugPackets)
        {
            var raw = packet.Span;
            byte opcode = raw.Length > 0 ? raw[0] : (byte)0;
            if (opcode != 0x73) // skip Ping spam
                _logger.LogDebug("SEND #{Id} 0x{Op:X2} len={Len} data=[{Data}]",
                    Id, opcode, raw.Length, FormatHex(raw, 32));
        }

        lock (_sendLock) { _sendQueue.Enqueue(packet); }
    }

    /// <summary>Send a pre-built PacketWriter.</summary>
    public void Send(PacketWriter writer)
    {
        Send(writer.Build());
    }

    /// <summary>Flush all queued packets to the socket.</summary>
    public void FlushOutput()
    {
        if (_socket == null || !_socket.Connected) return;

        lock (_sendLock)
        {
            while (_sendQueue.Count > 0)
            {
                var packet = _sendQueue.Dequeue();
                try
                {
                    if (ConnectionType == ConnectType.Game)
                    {
                        // Game connection: all outgoing data must be Huffman compressed.
                        // Source-X compresses each packet individually before sending.
                        var compressed = HuffmanCompression.Compress(packet.Data, 0, packet.Length);
                        Crypto.Encrypt(compressed, 0, compressed.Length);
                        _socket.Send(compressed, 0, compressed.Length, SocketFlags.None);
                    }
                    else
                    {
                        // Login connection: send raw (no compression)
                        _socket.Send(packet.Data, 0, packet.Length, SocketFlags.None);
                    }
                }
                catch (SocketException ex)
                {
                    if (IsExpectedDisconnect(ex.SocketErrorCode))
                    {
                        _logger.LogInformation("Client {Remote} disconnected ({Reason}).", RemoteEndPoint, ex.SocketErrorCode);
                    }
                    else
                    {
                        _logger.LogWarning("Send error to {Remote}: {Msg}", RemoteEndPoint, ex.Message);
                    }
                    MarkClosing();
                    break;
                }
            }
        }
    }

    public void MarkClosing() => IsClosing = true;

    // --- Packet handler delegates (connected to game logic) ---

    public Action<NetState, string, string>? LoginRequestHandler { get; set; }
    public Action<NetState, string, string, uint>? GameLoginHandler { get; set; }
    public Action<NetState, int, string>? CharSelectHandler { get; set; }
    public Action<NetState, byte, byte, uint>? MoveRequestHandler { get; set; }
    public Action<NetState, byte, ushort, ushort, string>? SpeechHandler { get; set; }
    public Action<NetState, uint>? AttackRequestHandler { get; set; }
    public Action<NetState, bool>? WarModeHandler { get; set; }
    public Action<NetState, uint>? DoubleClickHandler { get; set; }
    public Action<NetState, uint>? SingleClickHandler { get; set; }
    public Action<NetState, uint, ushort>? ItemPickupHandler { get; set; }
    public Action<NetState, uint, short, short, sbyte, uint>? ItemDropHandler { get; set; }
    public Action<NetState, uint, byte, uint>? ItemEquipHandler { get; set; }
    public Action<NetState, byte, uint>? StatusRequestHandler { get; set; }
    public Action<NetState, byte, uint, string>? ProfileRequestHandler { get; set; }
    public Action<NetState, byte, uint, uint, short, short, sbyte, ushort>? TargetResponseHandler { get; set; }
    public Action<NetState, uint, uint, uint, uint[], (ushort Id, string Text)[]>? GumpResponseHandler { get; set; }
    public Action<NetState, string>? ClientVersionHandler { get; set; }
    public Action<NetState, byte>? ViewRangeHandler { get; set; }
    public Action<NetState, ushort, PacketBuffer>? ExtendedCommandHandler { get; set; }
    public Action<NetState, uint>? AOSTooltipHandler { get; set; }
    public Action<NetState, uint, byte, List<VendorBuyEntry>>? VendorBuyHandler { get; set; }
    public Action<NetState, uint, List<VendorSellEntry>>? VendorSellHandler { get; set; }
    public Action<NetState, byte, string>? TextCommandHandler { get; set; }
    public Action<NetState>? ResyncRequestHandler { get; set; }
    public Action<NetState>? LogoutRequestHandler { get; set; }
    public Action<NetState>? HelpRequestHandler { get; set; }
    public Action<NetState, ushort>? ServerSelectHandler { get; set; }
    public Action<NetState, string>? CharCreateHandler { get; set; }
    public Action<NetState, byte, uint, uint>? SecureTradeHandler { get; set; }
    public Action<NetState, uint, string>? RenameHandler { get; set; }

    // Phase 1: Critical Stability
    public Action<NetState, byte>? DeathMenuHandler { get; set; }
    public Action<NetState, int, string>? CharDeleteHandler { get; set; }
    public Action<NetState, uint, ushort>? DyeResponseHandler { get; set; }
    public Action<NetState, uint, uint, uint, string>? PromptResponseHandler { get; set; }
    public Action<NetState, uint, ushort, ushort, ushort>? MenuChoiceHandler { get; set; }

    // Phase 2: Content Features
    public Action<NetState, uint, List<(ushort PageNum, string[] Lines)>>? BookPageHandler { get; set; }
    public Action<NetState, uint, bool, string, string>? BookHeaderHandler { get; set; }
    public Action<NetState, uint>? BulletinBoardRequestListHandler { get; set; }
    public Action<NetState, uint, uint>? BulletinBoardRequestMessageHandler { get; set; }
    public Action<NetState, uint, uint, string, string[]>? BulletinBoardPostHandler { get; set; }
    public Action<NetState, uint, uint>? BulletinBoardDeleteHandler { get; set; }
    public Action<NetState, uint>? MapDetailHandler { get; set; }
    public Action<NetState, uint, byte, byte, ushort, ushort>? MapPinEditHandler { get; set; }

    // Phase 3: Client Compatibility
    /// <summary>Reply to a 0xAB Gump Value Input dialog.
    /// Args: <c>(state, targetSerial, context, action, text)</c> where
    /// <c>action</c> is 1 for OK and 0 for cancel.</summary>
    public Action<NetState, uint, ushort, byte, string>? GumpTextEntryHandler { get; set; }
    public Action<NetState, uint>? AllNamesRequestHandler { get; set; }

    public string ClientVersion { get; set; } = "";
    public byte ViewRange { get; set; } = 18;
    public uint AssistVersion { get; set; }

    internal void OnLoginRequest(string account, string password)
    {
        AccountName = account;
        ConnectionType = ConnectType.Login;
        LoginRequestHandler?.Invoke(this, account, password);
    }

    internal void OnGameLogin(string account, string password, uint authId)
    {
        AccountName = account;
        AuthId = authId;
        ConnectionType = ConnectType.Game;
        GameLoginHandler?.Invoke(this, account, password, authId);
    }

    internal void OnCharSelect(int slot, string name)
    {
        SelectedCharSlot = slot;
        CharSelectHandler?.Invoke(this, slot, name);
    }

    internal void OnMoveRequest(byte dir, byte seq, uint fastWalkKey)
        => MoveRequestHandler?.Invoke(this, dir, seq, fastWalkKey);

    internal void OnSpeech(byte type, ushort hue, ushort font, string text)
        => SpeechHandler?.Invoke(this, type, hue, font, text);

    internal void OnAttackRequest(uint targetUid)
        => AttackRequestHandler?.Invoke(this, targetUid);

    internal void OnWarMode(bool warMode)
        => WarModeHandler?.Invoke(this, warMode);

    internal void OnDoubleClick(uint serial)
        => DoubleClickHandler?.Invoke(this, serial);

    internal void OnSingleClick(uint serial)
        => SingleClickHandler?.Invoke(this, serial);

    internal void OnItemPickup(uint serial, ushort amount)
        => ItemPickupHandler?.Invoke(this, serial, amount);

    internal void OnItemDrop(uint serial, short x, short y, sbyte z, uint container)
        => ItemDropHandler?.Invoke(this, serial, x, y, z, container);

    internal void OnItemEquip(uint serial, byte layer, uint container)
        => ItemEquipHandler?.Invoke(this, serial, layer, container);

    internal void OnStatusRequest(byte type, uint serial)
        => StatusRequestHandler?.Invoke(this, type, serial);

    internal void OnProfileRequest(byte mode, uint serial, string bioText)
        => ProfileRequestHandler?.Invoke(this, mode, serial, bioText);

    internal void OnTargetResponse(byte type, uint targetId, uint serial, short x, short y, sbyte z, ushort graphic)
        => TargetResponseHandler?.Invoke(this, type, targetId, serial, x, y, z, graphic);

    internal void OnGumpResponse(uint serial, uint gumpId, uint buttonId, uint[] switches, (ushort Id, string Text)[] textEntries)
        => GumpResponseHandler?.Invoke(this, serial, gumpId, buttonId, switches, textEntries);

    internal void OnClientVersion(string version)
    {
        ClientVersion = version;
        ClientVersionHandler?.Invoke(this, version);
    }

    internal void OnViewRange(byte range)
    {
        ViewRange = Math.Clamp(range, (byte)5, (byte)24);
        ViewRangeHandler?.Invoke(this, ViewRange);
    }

    internal void OnExtendedCommand(ushort subCmd, PacketBuffer buffer)
        => ExtendedCommandHandler?.Invoke(this, subCmd, buffer);

    internal void OnAOSTooltip(uint serial)
        => AOSTooltipHandler?.Invoke(this, serial);

    internal void OnVendorBuy(uint vendorSerial, byte flag, List<VendorBuyEntry> items)
        => VendorBuyHandler?.Invoke(this, vendorSerial, flag, items);

    internal void OnVendorSell(uint vendorSerial, List<VendorSellEntry> items)
        => VendorSellHandler?.Invoke(this, vendorSerial, items);

    internal void OnSecureTrade(byte action, uint sessionId, uint param)
        => SecureTradeHandler?.Invoke(this, action, sessionId, param);

    internal void OnRename(uint serial, string name)
        => RenameHandler?.Invoke(this, serial, name);

    internal void OnTextCommand(byte type, string command)
        => TextCommandHandler?.Invoke(this, type, command);

    internal void OnResyncRequest()
    {
        LastActivityTick = Environment.TickCount64;
        ResyncRequestHandler?.Invoke(this);
    }

    internal void OnLogoutRequest()
    {
        LastActivityTick = Environment.TickCount64;
        LogoutRequestHandler?.Invoke(this);
    }

    internal void OnHelpRequest()
    {
        LastActivityTick = Environment.TickCount64;
        HelpRequestHandler?.Invoke(this);
    }

    internal void OnServerSelect(ushort serverIndex)
    {
        ServerSelectHandler?.Invoke(this, serverIndex);
    }

    internal void OnCharCreate(string name)
    {
        CharCreateHandler?.Invoke(this, name);
    }

    // Phase 1: Critical Stability
    internal void OnDeathMenu(byte action)
        => DeathMenuHandler?.Invoke(this, action);

    internal void OnCharDelete(int charIndex, string password)
        => CharDeleteHandler?.Invoke(this, charIndex, password);

    internal void OnDyeResponse(uint itemSerial, ushort hue)
        => DyeResponseHandler?.Invoke(this, itemSerial, hue);

    internal void OnPromptResponse(uint serial, uint promptId, uint type, string text)
        => PromptResponseHandler?.Invoke(this, serial, promptId, type, text);

    internal void OnMenuChoice(uint serial, ushort menuId, ushort index, ushort modelId)
        => MenuChoiceHandler?.Invoke(this, serial, menuId, index, modelId);

    // Phase 2: Content Features
    internal void OnBookPage(uint serial, List<(ushort PageNum, string[] Lines)> pages)
        => BookPageHandler?.Invoke(this, serial, pages);

    internal void OnBookHeader(uint serial, bool writable, string title, string author)
        => BookHeaderHandler?.Invoke(this, serial, writable, title, author);

    internal void OnBulletinBoardRequestList(uint boardSerial)
        => BulletinBoardRequestListHandler?.Invoke(this, boardSerial);

    internal void OnBulletinBoardRequestMessage(uint boardSerial, uint msgSerial)
        => BulletinBoardRequestMessageHandler?.Invoke(this, boardSerial, msgSerial);

    internal void OnBulletinBoardPost(uint boardSerial, uint replyTo, string subject, string[] bodyLines)
        => BulletinBoardPostHandler?.Invoke(this, boardSerial, replyTo, subject, bodyLines);

    internal void OnBulletinBoardDelete(uint boardSerial, uint msgSerial)
        => BulletinBoardDeleteHandler?.Invoke(this, boardSerial, msgSerial);

    internal void OnMapDetail(uint serial)
        => MapDetailHandler?.Invoke(this, serial);

    internal void OnMapPinEdit(uint serial, byte action, byte pinId, ushort x, ushort y)
        => MapPinEditHandler?.Invoke(this, serial, action, pinId, x, y);

    // Phase 3: Client Compatibility
    internal void OnHardwareInfo()
    {
        // Log only — no handler dispatch needed
    }

    internal void OnSystemInfo()
    {
        // Log only — no handler dispatch needed
    }

    internal void OnAssistVersion(uint version)
    {
        AssistVersion = version;
    }

    internal void OnGumpTextEntry(uint serial, ushort context, byte action, string text)
        => GumpTextEntryHandler?.Invoke(this, serial, context, action, text);

    internal void OnAllNamesRequest(uint serial)
        => AllNamesRequestHandler?.Invoke(this, serial);

    internal void SendPing(byte seq)
    {
        var buf = new PacketBuffer(2);
        buf.WriteByte(0x73);
        buf.WriteByte(seq);
        Send(buf);
    }

    public void Dispose() => Clear();

    private static bool IsExpectedDisconnect(SocketError error) => error switch
    {
        SocketError.ConnectionAborted => true,
        SocketError.ConnectionReset => true,
        SocketError.OperationAborted => true,
        SocketError.Shutdown => true,
        SocketError.Interrupted => true,
        SocketError.NotConnected => true,
        _ => false
    };

    private static string FormatHex(ReadOnlySpan<byte> data, int maxBytes)
    {
        int len = Math.Min(data.Length, maxBytes);
        var sb = new System.Text.StringBuilder(len * 3);
        for (int i = 0; i < len; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        if (data.Length > maxBytes) sb.Append(" ...");
        return sb.ToString();
    }
}
