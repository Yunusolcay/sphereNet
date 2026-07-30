using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;

namespace SphereNet.Network.State;

public readonly record struct MovementStep(byte Direction, byte Sequence, uint FastWalkKey, int Mode);

/// <summary>
/// Per-connection state machine. Maps to CNetState in Source-X.
/// Tracks socket, buffers, crypto state, and connection lifecycle.
/// </summary>
public sealed class NetState : IDisposable
{
    private const int InitialSendBatchBufferSize = 4096;
    private const int InitialCryptScratchSize = 256;
    private const int MaxSendQueueSize = 4096;
    // Above this queue depth a connection is treated as falling behind and
    // low-priority cosmetic broadcasts are shed for it (interest management).
    // Well below MaxSendQueueSize so chatter is dropped long before the hard
    // overflow disconnect would trigger.
    private const int DroppableSoftCap = 1024;
    // Same idea but on the async byte backlog: in non-blocking mode a slow
    // client's real backlog accumulates in the outbound byte buffer
    // (_outEnd-_outStart), not in the send queues (which drain into it each flush).
    // So shed cosmetic chatter once the byte backlog passes this soft cap too,
    // well before the hard 512 KB disconnect cap — restoring graceful
    // degradation for the async path.
    private const int DroppableByteSoftCap = 256 * 1024;

    /// <summary>Count of cosmetic broadcast packets shed by interest management
    /// (process-wide; for telemetry).</summary>
    public static long DroppedChatterPackets;

    /// <summary>Opcodes safe to drop to a backed-up connection: overhead speech
    /// and sound. These carry no state the client tracks, so skipping them only
    /// costs a message/sound, never desync.</summary>
    private static bool IsDroppableUnderPressure(byte opcode) => opcode switch
    {
        0xAE => true, // Unicode speech (overhead text)
        0x1C => true, // ASCII message / speech
        0x54 => true, // Play sound effect
        _ => false,
    };

    // Async (non-blocking) game send. The game socket is put in non-blocking
    // mode; FlushOutput accumulates compressed+encrypted bytes into a persistent
    // per-connection buffer and drains as much as the socket accepts without
    // ever blocking a server thread. A slow client's bytes back up here (bounded
    // by MaxPendingSendBytes) instead of stalling the flush, and it is
    // disconnected only if it falls hopelessly behind. Toggle for A/B + rollback.
    public static bool NonBlockingGameSend = true;
    // Backpressure cap: a connection buffering more than this many unsent bytes
    // is hopelessly behind and is disconnected. Generous for a transient stall,
    // but bounds worst-case memory to ~maxClients * this.
    private const int MaxPendingSendBytes = 512 * 1024;

    private Socket? _socket;
    private readonly byte[] _recvBuffer = new byte[65536];
    private int _recvLength;
    private readonly object _sendLock = new();
    // Outbound queues indexed by PacketPriority. FlushOutput drains them
    // Highest → Idle each flush, so latency-critical traffic (movement
    // ack/reject 0x21/0x22, auth) never waits behind bulk world/UI updates.
    // Source-X PacketSend priority parity.
    private readonly Queue<PacketBuffer>[] _queues =
    [
        new Queue<PacketBuffer>(), // Idle
        new Queue<PacketBuffer>(), // Low
        new Queue<PacketBuffer>(), // Normal
        new Queue<PacketBuffer>(), // High
        new Queue<PacketBuffer>(), // Highest
    ];

    private int TotalQueuedCount()
    {
        int n = 0;
        for (int i = 0; i < _queues.Length; i++) n += _queues[i].Count;
        return n;
    }

    private bool AnyQueued()
    {
        for (int i = 0; i < _queues.Length; i++)
            if (_queues[i].Count > 0) return true;
        return false;
    }

    /// <summary>Dequeue the next packet in priority order (Highest → Idle), or
    /// null if all queues are empty. Caller must hold <see cref="_sendLock"/>.</summary>
    private PacketBuffer? DequeueNextLocked()
    {
        for (int p = _queues.Length - 1; p >= 0; p--)
            if (_queues[p].Count > 0) return _queues[p].Dequeue();
        return null;
    }

    private void DrainAllQueuesToPoolLocked()
    {
        for (int i = 0; i < _queues.Length; i++)
            while (_queues[i].Count > 0)
                _queues[i].Dequeue().ReturnToPool();
    }
    // Persistent outbound byte buffer. [_outStart.._outEnd) are compressed+
    // encrypted bytes not yet accepted by the socket (may span ticks in
    // non-blocking mode). In blocking mode it is fully drained each flush.
    private byte[] _sendBatchBuffer = new byte[InitialSendBatchBufferSize];
    private int _outStart;
    private int _outEnd;
    // Per-connection scratch for encrypting a shared broadcast payload without
    // mutating the shared (cross-recipient) cache. Only used by crypted game
    // connections; nocrypt connections append the shared bytes directly.
    private byte[] _cryptScratch = new byte[InitialCryptScratchSize];

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
    public byte LastMovementOpcode { get; set; }
    public int LastMovementBatchSize { get; set; }

    // Packet flood detection
    public int PacketFloodCount { get; set; }
    public long PacketFloodWindowStart { get; set; }

    // RTT measurement
    private byte _rttPingSeq;
    private long _rttPingSentTick;
    private int _rttMs = -1;
    private long _rttLastPingSentTick;
    public int RttMs => _rttMs;
    public bool HasRtt => _rttMs >= 0;
    // 0 = disabled. Server-initiated 0x73 pings corrupt the stock ClassicUO ping meter
    // (it treats every incoming 0x73 as a reply to its own ping), so leave this off.
    public static int RttPingIntervalMs { get; set; } = 0;

    // Client info
    public string AccountName { get; set; } = "";
    public uint AuthId { get; set; }
    public int SelectedCharSlot { get; set; } = -1;

    // Encryption
    public CryptoState Crypto { get; } = new CryptoState();
    public ClientEra ClientEra { get; set; } = ClientEra.Sphere56x;

    private uint _clientVersionNumber;
    private ProtocolChanges _protocolChanges;

    // Distinct unhandled opcodes already logged for this connection. Used to
    // rate-limit unknown-packet logging to one entry per opcode per connection,
    // so a client that repeatedly sends an unsupported opcode cannot spam logs.
    private HashSet<byte>? _loggedUnknownOpcodes;

    public uint ClientVersionNumber
    {
        get => _clientVersionNumber;
        set
        {
            _clientVersionNumber = value;
            _protocolChanges = ProtocolChangesHelper.DetermineProtocolChanges(value);
        }
    }

    public ProtocolChanges ProtocolChanges => _protocolChanges;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasProtocolChanges(ProtocolChanges flags) =>
        (_protocolChanges & flags) == flags;

    public Expansion ClientExpansion { get; set; } = Expansion.None;

    public uint ClientTypeFlag { get; set; }

    /// <summary>Parsed client type from 0xE1 flag.
    /// 0=Classic 2D, 1=Classic 3D (UO:3D), 2=Kingdom Reborn, 3=Enhanced Client.</summary>
    public byte ParsedClientType => (byte)Math.Min(ClientTypeFlag, 3);
    public int UndecryptedOffset { get; set; }
    public byte PendingPacketOpcode { get; set; }
    public int PendingPacketLength { get; set; }
    public long PendingPacketStartTick { get; set; }

    // Damage popup packet selection (Source-X CClient::addShowDamage):
    //   >= 4.0.7a  -> 0x0B new damage (7 bytes)
    //   4.0.0-4.0.6 -> 0xBF.0x22 old damage (11 bytes)
    //   < 4.0.0     -> no damage packet at all
    // An unreported version (0) is treated as a modern client so existing clients
    // keep getting 0x0B — only a client that explicitly announces an old version
    // is downgraded to the legacy packet.
    public bool SendsNewDamagePacket => _clientVersionNumber == 0 || _clientVersionNumber >= 40_007_000;
    public bool SendsOldDamagePacket => _clientVersionNumber >= 40_000_000 && _clientVersionNumber < 40_007_000;

    public bool IsClientPost6017  => HasProtocolChanges(ProtocolChanges.Version6017) || FallbackVersionAtLeast(60_001_007);
    public bool IsClientPost60142 => HasProtocolChanges(ProtocolChanges.Version60142) || FallbackVersionAtLeast(60_014_002);
    public bool IsClientPost7090  => HasProtocolChanges(ProtocolChanges.Version7090) || FallbackVersionAtLeast(70_009_000);
    public bool SupportsAosTooltip => HasProtocolChanges(ProtocolChanges.Version500a) || ClientEra == ClientEra.Modern || _clientVersionNumber >= 40_000_000;
    public bool SupportsBuffIcon => HasProtocolChanges(ProtocolChanges.BuffIcon) || ClientEra == ClientEra.Modern || _clientVersionNumber >= 50_002_002;
    public bool SupportsMapWaypoints => ClientEra == ClientEra.Modern || IsKingdomRebornClient ||
        IsEnhancedClient || _clientVersionNumber >= 70_084_000;
    public bool SupportsStygianAbyss => HasProtocolChanges(ProtocolChanges.StygianAbyss);
    public bool SupportsHighSeas => HasProtocolChanges(ProtocolChanges.HighSeas);
    public bool IsKingdomRebornClient => ParsedClientType == 2;
    public bool IsEnhancedClient => ParsedClientType == 3;
    public bool SupportsNewMobileIncoming => HasProtocolChanges(ProtocolChanges.NewMobileIncoming);
    public bool SupportsNewSecureTrading => HasProtocolChanges(ProtocolChanges.NewSecureTrading);
    public bool SupportsNewCharacterList => HasProtocolChanges(ProtocolChanges.NewCharacterList);
    public bool SupportsNewCharacterCreation => HasProtocolChanges(ProtocolChanges.NewCharacterCreation);
    public bool SupportsExtendedStatus => HasProtocolChanges(ProtocolChanges.ExtendedStatus);

    public ushort ScreenWidth { get; set; }
    public ushort ScreenHeight { get; set; }
    public string ClientLanguage { get; set; } = "ENU";

    /// <summary>Debug mode — log all outgoing packets.</summary>
    public bool DebugPackets { get; set; }
    public Func<ReadOnlySpan<byte>, string>? PacketDebugClassifier { get; set; }

    public NetState(ILogger logger)
    {
        _logger = logger;
    }

    public void Init(Socket socket)
    {
        _socket = socket;
        _socket.NoDelay = true;
        _socket.SendTimeout = 5000;
        _socket.LingerState = new LingerOption(false, 0);
        // E6: non-blocking from the first byte — login/unknown connections used
        // to keep the socket blocking until game login, so a zero-window client
        // could stall the flush pass with the server-list/relay packets.
        if (NonBlockingGameSend)
        {
            try { _socket.Blocking = false; }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not set non-blocking on #{Id}", Id); }
        }
        _outStart = 0;
        _outEnd = 0;
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
        LastMovementOpcode = 0;
        LastMovementBatchSize = 0;
        _recvLength = 0;
        AccountName = "";
        AuthId = 0;
        SelectedCharSlot = -1;
        IsSeeded = false;
        Seed = 0;
        Crypto.Reset();
        ClientVersionNumber = 0;
        UndecryptedOffset = 0;
        _loggedUnknownOpcodes?.Clear();
        ClearPendingPacket();
    }

    /// <summary>
    /// Returns true the first time an unhandled <paramref name="opcode"/> is seen
    /// on this connection, false on subsequent occurrences. Lets the packet loop
    /// log each unsupported opcode once instead of on every received packet.
    /// </summary>
    internal bool ShouldLogUnknownOpcode(byte opcode) =>
        (_loggedUnknownOpcodes ??= new HashSet<byte>()).Add(opcode);

    public void Clear()
    {
        // Best-effort: flush queued + pending outbound bytes to the kernel before
        // shutting down, so a graceful disconnect still delivers what was queued.
        // Reuses the normal send path (same compression/encryption/order); the
        // non-blocking send never stalls teardown — whatever the kernel won't take
        // is dropped, as it was before this buffer existed.
        try { FlushOutput(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Final flush error (client #{Id})", Id); }

        try { _socket?.Shutdown(SocketShutdown.Both); }
        catch (Exception ex) { _logger.LogDebug(ex, "Socket shutdown error (client #{Id})", Id); }
        try { _socket?.Close(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Socket close error (client #{Id})", Id); }
        _socket = null;
        IsInUse = false;
        IsClosing = false;
        _recvLength = 0;
        lock (_sendLock)
        {
            // Return any unsent pooled buffers to the pool instead of dropping them.
            DrainAllQueuesToPoolLocked();
            _outStart = 0;
            _outEnd = 0;
            ShrinkTransientBuffersLocked();
        }
    }

    private void ShrinkTransientBuffersLocked()
    {
        if (_sendBatchBuffer.Length > InitialSendBatchBufferSize)
            _sendBatchBuffer = new byte[InitialSendBatchBufferSize];
        if (_cryptScratch.Length > InitialCryptScratchSize)
            _cryptScratch = new byte[InitialCryptScratchSize];
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
            if (space <= 0)
            {
                _logger.LogWarning("Receive buffer full for #{Id} ({EP}), disconnecting", Id, RemoteEndPoint);
                MarkClosing();
                return -1;
            }

            int read = _socket.Receive(_recvBuffer, _recvLength, space, SocketFlags.None);
            if (read <= 0) return -1;

            _recvLength += read;
            LastActivityTick = Environment.TickCount64;
            return read;
        }
        catch (SocketException ex)
        {
            // On a non-blocking socket a transient WouldBlock/Interrupted just
            // means "no data right now" — not a lost connection.
            if (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.Interrupted)
                return 0;
            return -1;
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Reserved for optional client-side Huffman receive. UO clients send plaintext
    /// packets to the game server; only server→client traffic is Huffman compressed.
    /// </summary>
    public bool HuffmanReceiveEnabled { get; set; } = false;

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

    public void ReplaceReceivedRange(int offset, byte[] data, int length)
    {
        Buffer.BlockCopy(data, 0, _recvBuffer, offset, length);
    }

    /// <summary>Replace the entire receive buffer (e.g. after Huffman decompress).</summary>
    public void ReplaceAllReceived(byte[] data, int length)
    {
        if (length > _recvBuffer.Length)
        {
            MarkClosing();
            return;
        }

        if (length > 0)
            Buffer.BlockCopy(data, 0, _recvBuffer, 0, length);
        _recvLength = length;
        UndecryptedOffset = 0;
        ClearPendingPacket();
    }

    /// <summary>Enqueue a packet for sending.</summary>
    public void Send(PacketBuffer packet)
    {
        if (!IsInUse || IsClosing) { packet.ReturnToPool(); return; }
        LastActivityTick = Environment.TickCount64;

        if (DebugPackets)
        {
            var raw = packet.Span;
            byte opcode = raw.Length > 0 ? raw[0] : (byte)0;
            if (opcode != 0x73) // skip Ping spam
            {
                string cat = ClassifyPacket(raw);
                _logger.LogDebug("SEND #{Id} cat={Cat} 0x{Op:X2} len={Len} data=[{Data}]",
                    Id, cat, opcode, raw.Length, FormatHex(raw, 32));
            }
        }

        var priority = packet.Length > 0
            ? PacketPriorityClassifier.Classify(packet.Data[0])
            : PacketPriority.Normal;
        EnqueueAt(packet, priority);
    }

    /// <summary>Send a pre-built PacketWriter (priority classified by opcode).</summary>
    public void Send(PacketWriter writer)
    {
        Send(writer.Build());
    }

    /// <summary>Send a PacketWriter at an explicit priority, overriding the
    /// opcode-based classification (rarely needed).</summary>
    public void Send(PacketWriter writer, PacketPriority priority)
    {
        EnqueueAt(writer.Build(), priority);
    }

    /// <summary>Enqueue a latency-critical packet (movement ack/reject) at
    /// Highest priority — Source-X PRI_HIGHEST parity. Kept as a convenience
    /// wrapper; equivalent to <c>Send(writer, PacketPriority.Highest)</c>.</summary>
    public void SendPriority(PacketWriter writer)
    {
        EnqueueAt(writer.Build(), PacketPriority.Highest);
    }

    /// <summary>Drop a packet whose finalized size overflowed the ushort length
    /// field (its wire length can't be represented, so sending it would desync
    /// the client). Logs the opcode and true size, returns the buffer to the
    /// pool, and reports the drop so callers stop. Never throws.</summary>
    private bool DropOversize(PacketBuffer packet)
    {
        // Drop a packet flagged oversize during building OR any buffer whose length
        // exceeds the wire ceiling — including a raw PacketBuffer(byte[]) that never
        // went through WriteLengthAt (e.g. a recorded broadcast replayed verbatim),
        // which carries IsOversize == false. Without the length check such a buffer
        // bypasses this central guard and desyncs the client's packet stream.
        if (!packet.IsOversize && packet.Length <= PacketBuffer.MaxWireLength) return false;
        byte opcode = packet.Length > 0 ? packet.Data[0] : (byte)0;
        if (packet.OversizeReason != null)
            _logger.LogError(
                "Dropping over-budget packet 0x{Op:X2} for #{Id} ({EP}); not sent: {Reason}",
                opcode, Id, RemoteEndPoint, packet.OversizeReason);
        else
            _logger.LogError(
                "Dropping oversize packet 0x{Op:X2} ({Len} bytes > {Max}) for #{Id} ({EP}); not sent",
                opcode, packet.Length, PacketBuffer.MaxWireLength, Id, RemoteEndPoint);
        packet.ReturnToPool();
        return true;
    }

    private void EnqueueAt(PacketBuffer packet, PacketPriority priority)
    {
        if (DropOversize(packet)) return;
        if (!IsInUse || IsClosing) { packet.ReturnToPool(); return; }
        LastActivityTick = Environment.TickCount64;

        lock (_sendLock)
        {
            if (TotalQueuedCount() >= MaxSendQueueSize)
            {
                _logger.LogWarning("Send queue overflow for #{Id} ({EP}), disconnecting",
                    Id, RemoteEndPoint);
                MarkClosing();
                packet.ReturnToPool();
                return;
            }
            _queues[(int)priority].Enqueue(packet);
        }
    }

    /// <summary>Enqueue a buffer shared across several recipients (built once by
    /// the broadcaster and marked via <see cref="PacketBuffer.MarkShared"/>).
    /// Does not rebuild the packet. If this connection can't take it, only this
    /// recipient's refcount claim is released — the buffer survives for the rest.</summary>
    public void EnqueueShared(PacketBuffer packet)
    {
        if (DropOversize(packet)) return;
        if (!IsInUse || IsClosing) { packet.ReturnToPool(); return; }
        LastActivityTick = Environment.TickCount64;

        lock (_sendLock)
        {
            // Interest management: when this connection is already falling behind
            // (queue backed up past the soft cap), shed low-priority cosmetic
            // chatter — overhead speech and sound — for it rather than piling on
            // toward a hard overflow disconnect. State-bearing broadcasts
            // (movement, status, combat results, corpses, ...) are never dropped.
            // A slow consumer in a 1,000-strong crowd loses some chat it could
            // never read anyway, but keeps its connection and its gameplay state.
            if (packet.Length > 0 && IsDroppableUnderPressure(packet.Data[0])
                && (TotalQueuedCount() > DroppableSoftCap
                    || (_outEnd - _outStart) > DroppableByteSoftCap))
            {
                DroppedChatterPackets++;
                packet.ReturnToPool();
                return;
            }

            if (TotalQueuedCount() >= MaxSendQueueSize)
            {
                _logger.LogWarning("Send queue overflow for #{Id} ({EP}), disconnecting",
                    Id, RemoteEndPoint);
                MarkClosing();
                packet.ReturnToPool();
                return;
            }

            var priority = packet.Length > 0
                ? PacketPriorityClassifier.Classify(packet.Data[0])
                : PacketPriority.Normal;
            _queues[(int)priority].Enqueue(packet);
        }
    }

    public void SendRaw(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        var packet = new PacketBuffer(data.Length);
        packet.WriteBytes(data);
        Send(packet);
    }

    /// <summary>Flush all queued packets to the socket.</summary>
    public void FlushOutput()
    {
        if (_socket == null || !_socket.Connected) return;

        lock (_sendLock)
        {
            // Nothing queued and nothing still pending from a previous
            // (non-blocking) flush.
            if (!AnyQueued() && _outStart == _outEnd) return;

            try
            {
                if (ConnectionType == ConnectType.Game)
                {
                    // Accumulate compressed+encrypted bytes into the persistent
                    // outbound buffer, then drain (non-blocking) what the socket
                    // accepts. Bytes are kept in stream order, so a partial send
                    // is fine — the remainder goes out next flush.
                    var buf = _sendBatchBuffer;

                    // Reclaim the already-sent prefix.
                    if (_outStart > 0)
                    {
                        if (_outEnd > _outStart)
                            Buffer.BlockCopy(buf, _outStart, buf, 0, _outEnd - _outStart);
                        _outEnd -= _outStart;
                        _outStart = 0;
                    }

                    bool overCap = false;
                    // Drain in priority order (Highest → Idle) so step
                    // confirmations and auth lead the batch ahead of bulk
                    // world/UI traffic.
                    while (AnyQueued())
                    {
                        // Enforce the cap DURING append, before growing the buffer:
                        // a huge queue/gump/burst must not balloon the buffer past
                        // the cap in a single flush. If already over, stop appending,
                        // shed the rest and disconnect.
                        if (_outEnd - _outStart > MaxPendingSendBytes)
                        {
                            DrainAllQueuesToPoolLocked();
                            overCap = true;
                            break;
                        }

                        var packet = DequeueNextLocked()!;

                        // Resolve the bytes to append to the batch (compressed,
                        // and encrypted when this connection uses a cipher).
                        byte[]? payload;
                        int payloadLen;

                        if (packet.IsShared)
                        {
                            // Broadcast: the pre-encryption bytes were compressed
                            // once by MarkShared; reuse them read-only (so parallel
                            // flushes never race on the cache). The defensive branch
                            // compresses locally WITHOUT writing the shared cache.
                            byte[]? shared = packet.SharedCompressed;
                            int sharedLen;
                            if (shared != null)
                            {
                                sharedLen = packet.SharedCompressedLen;
                            }
                            else
                            {
                                shared = HuffmanCompression.Compress(packet.Data, 0, packet.Length);
                                sharedLen = shared.Length;
                            }

                            if (Crypto.EncType == EncryptionType.None)
                            {
                                // Cache IS the final wire bytes — append directly,
                                // no per-recipient work, no mutation of the cache.
                                payload = shared;
                                payloadLen = sharedLen;
                            }
                            else
                            {
                                // Encrypt a private copy so the shared cache stays
                                // valid for the remaining recipients.
                                if (_cryptScratch.Length < sharedLen)
                                    _cryptScratch = new byte[Math.Max(_cryptScratch.Length * 2, sharedLen)];
                                Buffer.BlockCopy(shared, 0, _cryptScratch, 0, sharedLen);
                                Crypto.Encrypt(_cryptScratch, 0, sharedLen);
                                payload = _cryptScratch;
                                payloadLen = sharedLen;
                            }
                        }
                        else
                        {
                            // Unicast — unchanged: private compress + in-place encrypt.
                            var compressed = HuffmanCompression.Compress(packet.Data, 0, packet.Length);
                            Crypto.Encrypt(compressed, 0, compressed.Length);
                            payload = compressed;
                            payloadLen = compressed.Length;
                        }

                        int needed = _outEnd + payloadLen;
                        if (needed > buf.Length)
                        {
                            int newSize = Math.Max(buf.Length * 2, needed);
                            var bigger = new byte[newSize];
                            Buffer.BlockCopy(buf, 0, bigger, 0, _outEnd);
                            _sendBatchBuffer = bigger;
                            buf = bigger;
                        }

                        Buffer.BlockCopy(payload, 0, buf, _outEnd, payloadLen);
                        _outEnd += payloadLen;

                        // Releases this recipient's claim; the pooled backing array
                        // returns only when the last recipient has flushed.
                        packet.ReturnToPool();
                    }

                    if (overCap)
                    {
                        _logger.LogWarning("Send backpressure cap ({Pending} bytes, append) for #{Id} ({EP}), disconnecting",
                            _outEnd - _outStart, Id, RemoteEndPoint);
                        MarkClosing();
                    }

                    if (NonBlockingGameSend)
                    {
                        // Drain as much as the socket accepts without blocking.
                        while (_outStart < _outEnd)
                        {
                            int sent = _socket.Send(buf, _outStart, _outEnd - _outStart,
                                SocketFlags.None, out SocketError serr);
                            if (sent > 0) _outStart += sent;
                            if (serr == SocketError.WouldBlock) break;       // kernel buffer full — retry next flush
                            if (serr != SocketError.Success) throw new SocketException((int)serr);
                            if (sent == 0) break;
                        }

                        if (_outStart >= _outEnd)
                        {
                            _outStart = 0;
                            _outEnd = 0;
                        }
                        else if (!overCap && _outEnd - _outStart > MaxPendingSendBytes)
                        {
                            // Client hopelessly behind — shed it rather than buffer unbounded.
                            _logger.LogWarning("Send backpressure cap ({Pending} bytes) for #{Id} ({EP}), disconnecting",
                                _outEnd - _outStart, Id, RemoteEndPoint);
                            MarkClosing();
                        }
                    }
                    else
                    {
                        // Blocking fallback (toggle off): send the whole buffer.
                        if (_outEnd > _outStart)
                            _socket.Send(buf, _outStart, _outEnd - _outStart, SocketFlags.None);
                        _outStart = 0;
                        _outEnd = 0;
                    }
                }
                else if (NonBlockingGameSend)
                {
                    // E6: login/unknown connections (no compression/crypt) used a
                    // BLOCKING per-packet Send — one zero-window client could
                    // stall the whole flush pass mid-login-burst. Reuse the batch
                    // buffer: append raw bytes, drain what the socket accepts,
                    // carry the remainder to the next flush.
                    var buf = _sendBatchBuffer;
                    if (_outStart > 0)
                    {
                        if (_outEnd > _outStart)
                            Buffer.BlockCopy(buf, _outStart, buf, 0, _outEnd - _outStart);
                        _outEnd -= _outStart;
                        _outStart = 0;
                    }

                    PacketBuffer? packet;
                    while ((packet = DequeueNextLocked()) != null)
                    {
                        int needed = _outEnd + packet.Length;
                        if (needed > buf.Length)
                        {
                            var bigger = new byte[Math.Max(buf.Length * 2, needed)];
                            Buffer.BlockCopy(buf, 0, bigger, 0, _outEnd);
                            _sendBatchBuffer = bigger;
                            buf = bigger;
                        }
                        Buffer.BlockCopy(packet.Data, 0, buf, _outEnd, packet.Length);
                        _outEnd += packet.Length;
                        packet.ReturnToPool();
                    }

                    while (_outStart < _outEnd)
                    {
                        int sent = _socket.Send(buf, _outStart, _outEnd - _outStart,
                            SocketFlags.None, out SocketError serr);
                        if (sent > 0) _outStart += sent;
                        if (serr == SocketError.WouldBlock) break;
                        if (serr != SocketError.Success) throw new SocketException((int)serr);
                        if (sent == 0) break;
                    }

                    if (_outStart >= _outEnd)
                    {
                        _outStart = 0;
                        _outEnd = 0;
                    }
                    else if (_outEnd - _outStart > MaxPendingSendBytes)
                    {
                        _logger.LogWarning("Send backpressure cap ({Pending} bytes, login) for #{Id} ({EP}), disconnecting",
                            _outEnd - _outStart, Id, RemoteEndPoint);
                        MarkClosing();
                    }
                }
                else
                {
                    PacketBuffer? packet;
                    while ((packet = DequeueNextLocked()) != null)
                    {
                        _socket.Send(packet.Data, 0, packet.Length, SocketFlags.None);
                        packet.ReturnToPool();
                    }
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
            }
        }
    }

    public void MarkClosing() => IsClosing = true;

    public void MarkPendingPacket(byte opcode, int length, long now)
    {
        if (PendingPacketStartTick > 0 && PendingPacketOpcode == opcode && PendingPacketLength == length)
            return;

        PendingPacketOpcode = opcode;
        PendingPacketLength = length;
        PendingPacketStartTick = now;
    }

    public void ClearPendingPacket()
    {
        PendingPacketOpcode = 0;
        PendingPacketLength = 0;
        PendingPacketStartTick = 0;
    }

    // --- Packet handler delegates (connected to game logic) ---

    public Action<NetState, string, string>? LoginRequestHandler { get; set; }
    public Action<NetState, string, string, uint>? GameLoginHandler { get; set; }
    public Action<NetState, int, string>? CharSelectHandler { get; set; }
    public Action<NetState, byte, byte, uint>? MoveRequestHandler { get; set; }
    public Action<NetState, IReadOnlyList<MovementStep>>? MovementBatchHandler { get; set; }
    public Action<NetState, byte, ushort, ushort, string>? SpeechHandler { get; set; }
    public Action<NetState, uint>? AttackRequestHandler { get; set; }
    public Action<NetState, bool>? WarModeHandler { get; set; }
    public Action<NetState, uint>? DoubleClickHandler { get; set; }
    public Action<NetState, uint>? SingleClickHandler { get; set; }
    public Action<NetState, uint, uint>? MailMessageHandler { get; set; }
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
    /// <summary>0xD7 encoded command (custom-house design editor). Distinct from
    /// <see cref="ExtendedCommandHandler"/> so the overlapping 0xD7/0xBF
    /// subcommand IDs never cross-dispatch. The buffer is positioned at the
    /// subcommand payload (after serial + subCmd). Unwired = safe ignore.</summary>
    public Action<NetState, ushort, uint, PacketBuffer>? EncodedCommandHandler { get; set; }
    public Action<NetState, uint>? AOSTooltipHandler { get; set; }
    public Action<NetState, uint, byte, List<VendorBuyEntry>>? VendorBuyHandler { get; set; }
    public Action<NetState, uint, List<VendorSellEntry>>? VendorSellHandler { get; set; }
    public Action<NetState, byte, string>? TextCommandHandler { get; set; }
    public Action<NetState>? ResyncRequestHandler { get; set; }
    public Action<NetState>? LogoutRequestHandler { get; set; }
    public Action<NetState>? HelpRequestHandler { get; set; }
    public Action<NetState, ushort>? ServerSelectHandler { get; set; }
    public Action<NetState, Core.Types.CharCreateInfo>? CharCreateHandler { get; set; }
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
    public Action<NetState, uint, uint>? BulletinBoardRequestHeadHandler { get; set; }
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

    /// <summary>0xEC equip-item macro: the serials the client wants worn, in
    /// order (Source-X PacketEquipItemMacro).</summary>
    public Action<NetState, IReadOnlyList<uint>>? EquipMacroHandler { get; set; }
    /// <summary>0xED unequip-item macro: the layers the client wants stripped
    /// (Source-X PacketUnEquipItemMacro).</summary>
    public Action<NetState, IReadOnlyList<ushort>>? UnequipMacroHandler { get; set; }
    /// <summary>0xFB public-house-content toggle (Source-X
    /// PacketPublicHouseContent, CClient::_fShowPublicHouseContent).</summary>
    public Action<NetState, bool>? PublicHouseContentHandler { get; set; }

    internal void OnEquipMacro(IReadOnlyList<uint> serials) => EquipMacroHandler?.Invoke(this, serials);
    internal void OnUnequipMacro(IReadOnlyList<ushort> layers) => UnequipMacroHandler?.Invoke(this, layers);
    internal void OnPublicHouseContent(bool show) => PublicHouseContentHandler?.Invoke(this, show);

    public string ClientVersion { get; set; } = "";

    /// <summary>Default map view range (tiles) for a new connection, from config
    /// (sphere.ini MapViewSize). A client may still request its own via 0xC8.</summary>
    public static byte DefaultViewRange { get; set; } = 18;
    public byte ViewRange { get; set; } = DefaultViewRange;
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
        // Switch the game stream to non-blocking sends so a slow client never
        // blocks a flush thread (login stays blocking — tiny, short-lived).
        if (NonBlockingGameSend && _socket != null)
        {
            try { _socket.Blocking = false; }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not set non-blocking on #{Id}", Id); }
        }
        GameLoginHandler?.Invoke(this, account, password, authId);
    }

    internal void OnCharSelect(int slot, string name)
    {
        if (slot < 0 || slot > 7) return;
        SelectedCharSlot = slot;
        CharSelectHandler?.Invoke(this, slot, name);
    }

    internal void OnMoveRequest(byte dir, byte seq, uint fastWalkKey)
        => MoveRequestHandler?.Invoke(this, dir, seq, fastWalkKey);

    internal void OnMovementBatch(IReadOnlyList<MovementStep> steps)
    {
        if (MovementBatchHandler != null)
        {
            MovementBatchHandler.Invoke(this, steps);
            return;
        }

        foreach (var step in steps)
            OnMoveRequest(step.Direction, step.Sequence, step.FastWalkKey);
    }

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

    internal void OnMailMessage(uint targetSerial, uint attachmentSerial)
        => MailMessageHandler?.Invoke(this, targetSerial, attachmentSerial);

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

    internal void OnEncodedCommand(ushort subCmd, uint serial, PacketBuffer payload)
        => EncodedCommandHandler?.Invoke(this, subCmd, serial, payload);

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

    internal void OnCharCreate(Core.Types.CharCreateInfo info)
    {
        CharCreateHandler?.Invoke(this, info);
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

    internal void OnBulletinBoardRequestHead(uint boardSerial, uint msgSerial)
        => BulletinBoardRequestHeadHandler?.Invoke(this, boardSerial, msgSerial);

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

    internal void OnClientType(uint clientFlag)
    {
        ClientTypeFlag = clientFlag;
    }

    internal void OnKREncryption()
    {
        // KR encryption negotiation — accept silently, no action needed.
        // KR, Enhanced, and third-party clients send this during login handshake.
    }

    internal void OnCrashReport()
    {
        _logger.LogWarning("Client #{Id} ({EP}) sent crash report", Id, RemoteEndPoint);
        CrashReportHandler?.Invoke(this);
    }

    /// <summary>0xF4 crash report — lets the game layer fire @UserBugReport.</summary>
    public Action<NetState>? CrashReportHandler { get; set; }

    /// <summary>Stateless client UI button packets (0xFA Ultima Store,
    /// 0xB5 chat window). Arg: the packet opcode.</summary>
    public Action<NetState, byte>? ClientUiButtonHandler { get; set; }

    internal void OnClientUiButton(byte opcode) => ClientUiButtonHandler?.Invoke(this, opcode);

    /// <summary>0xB3 chat action: (cmd, payload text). 0x61 talk, 0x62 join,
    /// 0x63 create, 0x43 leave.</summary>
    public Action<NetState, ushort, string>? ChatActionHandler { get; set; }

    internal void OnChatAction(ushort cmd, string text) => ChatActionHandler?.Invoke(this, cmd, text);

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

    public bool SendRttPing(long nowMs)
    {
        if (RttPingIntervalMs <= 0) return false;
        if (_rttLastPingSentTick > 0 && (nowMs - _rttLastPingSentTick) < RttPingIntervalMs)
            return false;

        _rttPingSeq = (byte)((_rttPingSeq + 1) | 0x80);
        _rttPingSentTick = nowMs;
        _rttLastPingSentTick = nowMs;
        SendPing(_rttPingSeq);
        return true;
    }

    public void OnPingReceived(byte seq)
    {
        if ((seq & 0x80) != 0 && seq == _rttPingSeq && _rttPingSentTick > 0)
        {
            _rttMs = (int)(Environment.TickCount64 - _rttPingSentTick);
            _rttPingSentTick = 0;
        }
        else
        {
            SendPing(seq);
        }
    }

    public void Dispose() => Clear();

    private bool FallbackVersionAtLeast(uint version)
    {
        if (_clientVersionNumber != 0)
            return false; // already handled by HasProtocolChanges

        // When version is unknown (0xBD not yet received or client doesn't send it),
        // use the server-configured era as the baseline. Additionally, if the client
        // has announced itself via 0xE1 (KR/EC), it's at least a 6.0+ client.
        if (ClientEra == ClientEra.Modern)
            return true;
        if (ClientTypeFlag >= 2 && version <= 60_000_000)
            return true; // KR/EC clients are at least 6.0
        return false;
    }

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

    public string ClassifyPacket(ReadOnlySpan<byte> data)
    {
        string? external = PacketDebugClassifier?.Invoke(data);
        if (!string.IsNullOrWhiteSpace(external))
            return external;
        if (!TryReadPacketSerial(data, out uint serial))
            return "packet";
        return (serial & 0x40000000) != 0 ? "item" : "mobile";
    }

    private static bool TryReadPacketSerial(ReadOnlySpan<byte> data, out uint serial)
    {
        serial = 0;
        if (data.Length == 0)
            return false;

        int offset = data[0] switch
        {
            0x1A => data.Length >= 7 ? 3 : -1,
            0x1D => data.Length >= 5 ? 1 : -1,
            0x2E => data.Length >= 5 ? 1 : -1,
            0x78 => data.Length >= 7 ? 3 : -1,
            0x77 or 0x20 or 0x11 or 0x88 or 0xAE => data.Length >= 5 ? 1 : -1,
            _ => -1
        };

        if (offset < 0 || data.Length < offset + 4)
            return false;
        serial = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        return true;
    }
}
