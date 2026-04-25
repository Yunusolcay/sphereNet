using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SphereNet.Network.Encryption;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// A simulated UO client that connects via TCP and performs automated actions.
/// Used for stress testing the server with realistic network load.
/// </summary>
public sealed class BotClient : IDisposable
{
    private readonly ILogger _logger;
    private readonly Random _rng;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _behaviorTask;
    
    private readonly string _accountName;
    private readonly string _charName;
    private readonly int _botId;
    
    private byte _moveSequence;
    private uint _authId;
    private uint _charUid;
    private short _x, _y;
    private sbyte _z;
    private bool _disposed;

    private readonly BotWorldModel _world = new();
    private BotAI? _ai;
    
    // Buffers for game server packets (Huffman compressed)
    private readonly List<byte> _decompressedBuffer = new();
    private readonly List<byte> _compressedBuffer = new(); // Raw compressed data
    private bool _isGamePhase;

    public BotState State { get; private set; } = BotState.Disconnected;
    public int PacketsSent { get; private set; }
    public int PacketsReceived { get; private set; }
    public int BytesSent { get; private set; }
    public int BytesReceived { get; private set; }
    public long ConnectTimeMs { get; private set; }
    public long LastActivityMs { get; private set; }
    public int BotId => _botId;

    public const string AccountPrefix = "spherenetBot";
    public const string CharPrefix = "SphereBot";

    public BotClient(int botId, ILogger logger)
    {
        _botId = botId;
        _logger = logger;
        _rng = new Random(botId * 31337);
        _accountName = $"{AccountPrefix}{botId:D4}";
        _charName = $"{CharPrefix}{botId}";
    }

    public static bool IsBotAccountName(string name) => 
        name.StartsWith(AccountPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsBotCharName(string name) => 
        name.StartsWith(CharPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> ConnectAndLoginAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            State = BotState.Connecting;
            long startMs = Environment.TickCount64;

            // === PHASE 1: Login Server ===
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.ReceiveTimeout = 10000;
            _tcp.SendTimeout = 5000;

            await _tcp.ConnectAsync(host, port, ct);
            _stream = _tcp.GetStream();
            
            ConnectTimeMs = Environment.TickCount64 - startMs;
            State = BotState.LoggingIn;

            // Step 1: Login Seed + Account Login
            uint seed = (uint)(_rng.Next() & 0x7FFFFFFF);
            await SendPacketAsync(BotPacketBuilder.BuildLoginSeed(seed), ct);
            await SendPacketAsync(BotPacketBuilder.BuildAccountLogin(_accountName, "botpass"), ct);
            
            // Step 2: Wait for server list (0xA8)
            var response = await ReadPacketAsync(ct);
            if (response == null)
            {
                _logger.LogWarning("[Bot{Id}] Step 2 FAIL: No response after account login", _botId);
                return false;
            }
            if (response[0] != 0xA8)
            {
                _logger.LogWarning("[Bot{Id}] Step 2 FAIL: Expected 0xA8 (ServerList), got 0x{Op:X2}", _botId, response[0]);
                return false;
            }
            PacketsReceived++;

            // Step 3: Select server
            await SendPacketAsync(BotPacketBuilder.BuildServerSelect(0), ct);

            // Step 4: Wait for relay (0x8C)
            response = await ReadPacketAsync(ct);
            if (response == null)
            {
                _logger.LogWarning("[Bot{Id}] Step 4 FAIL: No response after server select", _botId);
                return false;
            }
            if (response[0] != 0x8C)
            {
                _logger.LogWarning("[Bot{Id}] Step 4 FAIL: Expected 0x8C (Relay), got 0x{Op:X2}", _botId, response[0]);
                return false;
            }
            PacketsReceived++;
            
            // Parse relay info: IP (4 bytes), port (2 bytes), authId (4 bytes)
            // Packet format: [0x8C] [IP 4 bytes] [Port 2 bytes] [AuthId 4 bytes]
            string gameHost = $"{response[1]}.{response[2]}.{response[3]}.{response[4]}";
            int gamePort = (response[5] << 8) | response[6];
            _authId = ReadUInt32BE(response, 7);

            // Close login server connection
            _stream?.Close();
            _tcp?.Close();
            
            // Small delay before reconnecting
            await Task.Delay(50, ct);

            // === PHASE 2: Game Server (reconnect) ===
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.ReceiveTimeout = 10000;
            _tcp.SendTimeout = 5000;

            // Connect to game server (use original host if relay says 127.0.0.1)
            string connectHost = (gameHost == "127.0.0.1") ? host : gameHost;
            await _tcp.ConnectAsync(connectHost, gamePort, ct);
            _stream = _tcp.GetStream();

            // Step 5: Game server login (need to send seed again for game server)
            seed = (uint)(_rng.Next() & 0x7FFFFFFF);
            await SendPacketAsync(BotPacketBuilder.BuildLoginSeed(seed), ct);
            await SendPacketAsync(BotPacketBuilder.BuildGameLogin(_accountName, "botpass", _authId), ct);
            await _stream.FlushAsync(ct);

            // Small delay to let server process and respond
            await Task.Delay(100, ct);

            // Mark that we are now in game phase (Huffman compressed packets)
            _isGamePhase = true;
            _decompressedBuffer.Clear();
            _compressedBuffer.Clear();

            // Step 6: Wait for char list (0xA9) - may receive other packets first (0xB9 features, etc)
            bool gotCharList = false;
            var receivedPackets = new List<byte>();
            
            // Check if data is available
            if (!_tcp.Connected)
            {
                _logger.LogWarning("[Bot{Id}] Step 6: TCP disconnected after game login", _botId);
                return false;
            }

            for (int i = 0; i < 20 && !gotCharList; i++)
            {
                response = await ReadGamePacketAsync(ct, 10000); // 10 second timeout, decompressed
                if (response == null) 
                {
                    _logger.LogWarning("[Bot{Id}] Step 6: Timeout waiting for char list (attempt {I}, connected={Connected}, dataAvail={Avail}, bufLen={BufLen})", 
                        _botId, i, _tcp?.Connected ?? false, _stream?.DataAvailable ?? false, _decompressedBuffer.Count);
                    break;
                }
                PacketsReceived++;
                receivedPackets.Add(response[0]);
                
                if (response[0] == 0xA9) // Char list
                    gotCharList = true;
            }

            if (!gotCharList)
            {
                _logger.LogWarning("[Bot{Id}] Step 6 FAIL: Did not receive char list. Got packets: [{Packets}]", 
                    _botId, string.Join(", ", receivedPackets.Select(p => $"0x{p:X2}")));
                return false;
            }

            // Step 7: Select character (slot 0)
            await SendPacketAsync(BotPacketBuilder.BuildCharSelect(0, _charName), ct);

            // Step 8: Wait for login confirm (0x1B)
            bool loggedIn = false;
            receivedPackets.Clear();
            for (int i = 0; i < 30 && !loggedIn; i++)
            {
                response = await ReadGamePacketAsync(ct); // Use game packet reader
                if (response == null) 
                {
                    _logger.LogWarning("[Bot{Id}] Step 8: Timeout waiting for login confirm (attempt {I})", _botId, i);
                    break;
                }
                PacketsReceived++;
                receivedPackets.Add(response[0]);
                
                if (response[0] == 0x1B) // Login confirm
                {
                    _charUid = ReadUInt32BE(response, 1);
                    _x = (short)ReadUInt16BE(response, 11);
                    _y = (short)ReadUInt16BE(response, 13);
                    _z = (sbyte)response[16];
                    loggedIn = true;
                }
            }

            if (loggedIn)
            {
                State = BotState.Playing;
                LastActivityMs = Environment.TickCount64;
                _world.CharUid = _charUid;
                _world.X = _x;
                _world.Y = _y;
                _world.Z = _z;
                _world.MaxHits = 100;
                _world.Hits = 100;
                return true;
            }

            _logger.LogWarning("[Bot{Id}] Step 8 FAIL: Did not receive login confirm (0x1B). Got packets: [{Packets}]",
                _botId, string.Join(", ", receivedPackets.Select(p => $"0x{p:X2}")));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Bot{Id}] Login exception: {Error}", _botId, ex.Message);
            State = BotState.Disconnected;
            return false;
        }
    }

    public void StartBehavior(BotBehavior behavior, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _behaviorTask = Task.Run(() => RunBehaviorLoopAsync(behavior, _cts.Token));
    }

    private async Task RunBehaviorLoopAsync(BotBehavior behavior, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && State == BotState.Playing)
            {
                // Drain incoming packets (non-blocking)
                await DrainIncomingPacketsAsync(ct);

                // Perform behavior
                switch (behavior)
                {
                    case BotBehavior.Idle:
                        await Task.Delay(1000, ct);
                        break;
                    case BotBehavior.RandomWalk:
                        await DoRandomWalkAsync(ct);
                        break;
                    case BotBehavior.Combat:
                        await DoCombatAsync(ct);
                        break;
                    case BotBehavior.FullSimulation:
                        await DoFullSimulationAsync(ct);
                        break;
                    case BotBehavior.SmartAI:
                        await DoSmartAIAsync(ct);
                        break;
                }

                LastActivityMs = Environment.TickCount64;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] Behavior error: {Error}", _botId, ex.Message);
        }
        finally
        {
            State = BotState.Disconnected;
        }
    }

    private async Task DrainIncomingPacketsAsync(CancellationToken ct)
    {
        try
        {
            if (_stream == null || !_tcp!.Connected) return;
            
            // Keep reading while there's data in the stream or decompressed buffer
            while ((_stream.DataAvailable || _decompressedBuffer.Count > 0) && !ct.IsCancellationRequested)
            {
                var packet = _isGamePhase 
                    ? await ReadGamePacketAsync(ct, timeout: 50) 
                    : await ReadPacketAsync(ct, timeout: 50);
                    
                if (packet == null) break;
                
                PacketsReceived++;
                ProcessIncomingPacket(packet);
            }
        }
        catch { }
    }

    private void ProcessIncomingPacket(byte[] packet)
    {
        if (packet.Length == 0) return;
        long now = Environment.TickCount64;

        switch (packet[0])
        {
            case 0x20: // Draw Game Player (19 bytes)
                if (packet.Length >= 19)
                {
                    _world.Body = ReadUInt16BE(packet, 5);
                    _world.X = _x = (short)ReadUInt16BE(packet, 11);
                    _world.Y = _y = (short)ReadUInt16BE(packet, 13);
                    _world.Direction = packet[17];
                    _world.Z = _z = (sbyte)packet[18];
                }
                break;

            case 0x78: // Draw Object (mobile appears)
                if (packet.Length >= 19)
                {
                    uint serial78 = ReadUInt32BE(packet, 3);
                    if (serial78 == _charUid || (serial78 & 0x40000000) != 0) break;
                    _world.Mobiles[serial78] = new KnownMobile
                    {
                        Serial = serial78,
                        Body = ReadUInt16BE(packet, 7),
                        X = (short)ReadUInt16BE(packet, 9),
                        Y = (short)ReadUInt16BE(packet, 11),
                        Z = (sbyte)packet[13],
                        Notoriety = packet[18],
                        LastSeenMs = now
                    };
                }
                break;

            case 0x77: // Mobile Moving (17 bytes)
                if (packet.Length >= 17)
                {
                    uint serial77 = ReadUInt32BE(packet, 1);
                    if (serial77 == _charUid) break;
                    if (_world.Mobiles.TryGetValue(serial77, out var mob77))
                    {
                        mob77.Body = ReadUInt16BE(packet, 5);
                        mob77.X = (short)ReadUInt16BE(packet, 7);
                        mob77.Y = (short)ReadUInt16BE(packet, 9);
                        mob77.Z = (sbyte)packet[11];
                        mob77.Notoriety = packet[16];
                        mob77.LastSeenMs = now;
                    }
                    else
                    {
                        _world.Mobiles[serial77] = new KnownMobile
                        {
                            Serial = serial77,
                            Body = ReadUInt16BE(packet, 5),
                            X = (short)ReadUInt16BE(packet, 7),
                            Y = (short)ReadUInt16BE(packet, 9),
                            Z = (sbyte)packet[11],
                            Notoriety = packet[16],
                            LastSeenMs = now
                        };
                    }
                }
                break;

            case 0x1D: // Delete Object (5 bytes)
                if (packet.Length >= 5)
                {
                    uint serialDel = ReadUInt32BE(packet, 1);
                    _world.Mobiles.Remove(serialDel);
                }
                break;

            case 0xA1: // Health Update (9 bytes)
                if (packet.Length >= 9)
                {
                    uint serialHp = ReadUInt32BE(packet, 1);
                    if (serialHp == _charUid)
                    {
                        _world.MaxHits = (short)ReadUInt16BE(packet, 5);
                        _world.Hits = (short)ReadUInt16BE(packet, 7);
                        _world.IsDead = _world.Hits <= 0 && _world.MaxHits > 0;
                    }
                }
                break;

            case 0xA2: // Mana Update (9 bytes)
                if (packet.Length >= 9)
                {
                    uint serialMana = ReadUInt32BE(packet, 1);
                    if (serialMana == _charUid)
                    {
                        _world.MaxMana = (short)ReadUInt16BE(packet, 5);
                        _world.Mana = (short)ReadUInt16BE(packet, 7);
                    }
                }
                break;

            case 0xA3: // Stam Update (9 bytes)
                if (packet.Length >= 9)
                {
                    uint serialStam = ReadUInt32BE(packet, 1);
                    if (serialStam == _charUid)
                    {
                        _world.MaxStam = (short)ReadUInt16BE(packet, 5);
                        _world.Stam = (short)ReadUInt16BE(packet, 7);
                    }
                }
                break;

            case 0x22: // Move Ack (3 bytes)
                _world.MoveRejectCount = 0;
                break;

            case 0x21: // Move Rejected (8 bytes)
                if (packet.Length >= 8)
                {
                    _world.MoveRejectCount++;
                    _world.X = _x = (short)ReadUInt16BE(packet, 2);
                    _world.Y = _y = (short)ReadUInt16BE(packet, 4);
                    _world.Direction = packet[6];
                    _world.Z = _z = (sbyte)packet[7];
                }
                break;

            case 0x6C: // Target Cursor (19 bytes)
                if (packet.Length >= 7)
                {
                    _world.HasPendingTarget = true;
                    _world.TargetCursorId = ReadUInt32BE(packet, 2);
                }
                break;
        }
    }

    private async Task DoRandomWalkAsync(CancellationToken ct)
    {
        byte dir = (byte)_rng.Next(0, 8);
        await SendPacketAsync(BotPacketBuilder.BuildMoveRequest(dir, _moveSequence++), ct);
        await Task.Delay(_rng.Next(300, 700), ct);
    }

    private async Task DoCombatAsync(CancellationToken ct)
    {
        // Enable war mode
        await SendPacketAsync(BotPacketBuilder.BuildWarMode(true), ct);
        await Task.Delay(200, ct);
        
        // Attack a random UID (NPC nearby - simulated)
        uint targetUid = (uint)(0x00000001 + _rng.Next(0, 100000));
        await SendPacketAsync(BotPacketBuilder.BuildAttackRequest(targetUid), ct);
        await Task.Delay(_rng.Next(500, 1000), ct);
    }

    private async Task DoFullSimulationAsync(CancellationToken ct)
    {
        int action = _rng.Next(0, 100);
        
        if (action < 40) // 40% walk
        {
            await DoRandomWalkAsync(ct);
        }
        else if (action < 70) // 30% combat
        {
            await DoCombatAsync(ct);
        }
        else if (action < 90) // 20% double click (loot attempt)
        {
            uint targetUid = (uint)(0x40000001 + _rng.Next(0, 10000));
            await SendPacketAsync(BotPacketBuilder.BuildDoubleClick(targetUid), ct);
            await Task.Delay(_rng.Next(200, 500), ct);
        }
        else // 10% skill use
        {
            ushort skillId = (ushort)_rng.Next(0, 50);
            await SendPacketAsync(BotPacketBuilder.BuildSkillUse(skillId), ct);
            await Task.Delay(_rng.Next(500, 1000), ct);
        }
    }

    private async Task DoSmartAIAsync(CancellationToken ct)
    {
        _ai ??= new BotAI(_world, _rng);
        var action = _ai.Tick();
        await ExecuteActionAsync(action, ct);
    }

    private async Task ExecuteActionAsync(BotAction action, CancellationToken ct)
    {
        switch (action.Type)
        {
            case BotAIActionType.Move:
                await SendPacketAsync(BotPacketBuilder.BuildMoveRequest(action.Direction, _moveSequence++), ct);
                bool isRunning = (action.Direction & 0x80) != 0;
                int moveDelay = isRunning ? _rng.Next(100, 250) : _rng.Next(200, 450);
                await Task.Delay(moveDelay, ct);
                break;
            case BotAIActionType.Attack:
                await SendPacketAsync(BotPacketBuilder.BuildAttackRequest(action.TargetSerial), ct);
                await Task.Delay(_rng.Next(500, 1200), ct);
                break;
            case BotAIActionType.DoubleClick:
                await SendPacketAsync(BotPacketBuilder.BuildDoubleClick(action.TargetSerial), ct);
                await Task.Delay(_rng.Next(300, 600), ct);
                break;
            case BotAIActionType.EnableWarMode:
                _world.IsWarMode = true;
                await SendPacketAsync(BotPacketBuilder.BuildWarMode(true), ct);
                await Task.Delay(200, ct);
                break;
            case BotAIActionType.DisableWarMode:
                _world.IsWarMode = false;
                await SendPacketAsync(BotPacketBuilder.BuildWarMode(false), ct);
                await Task.Delay(200, ct);
                break;
            case BotAIActionType.Speech:
                if (action.Text != null)
                    await SendPacketAsync(BotPacketBuilder.BuildSpeech(action.Text), ct);
                await Task.Delay(_rng.Next(300, 600), ct);
                break;
            case BotAIActionType.Wait:
                await Task.Delay(Math.Max(100, action.DelayMs), ct);
                break;
            case BotAIActionType.None:
                await Task.Delay(200, ct);
                break;
        }
    }

    private async Task SendPacketAsync(byte[] packet, CancellationToken ct)
    {
        if (_stream == null || !_tcp!.Connected) return;
        
        try
        {
            await _stream.WriteAsync(packet, ct);
            PacketsSent++;
            BytesSent += packet.Length;
        }
        catch { State = BotState.Disconnected; }
    }

    private async Task<byte[]?> ReadPacketAsync(CancellationToken ct, int timeout = 5000)
    {
        if (_stream == null) 
        {
            _logger.LogDebug("[Bot{Id}] ReadPacket: stream is null", _botId);
            return null;
        }
        if (_tcp == null || !_tcp.Connected)
        {
            _logger.LogDebug("[Bot{Id}] ReadPacket: tcp is null or not connected", _botId);
            return null;
        }
        
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            var header = new byte[3];
            int read = await _stream.ReadAsync(header.AsMemory(0, 1), linkedCts.Token);
            if (read == 0) 
            {
                _logger.LogDebug("[Bot{Id}] ReadPacket: read returned 0 bytes (connection closed)", _botId);
                return null;
            }
            
            byte opcode = header[0];
            int length = GetPacketLength(opcode);
            bool isVariable = (length == -1);
            
            if (isVariable)
            {
                read = await _stream.ReadAsync(header.AsMemory(1, 2), linkedCts.Token);
                if (read < 2) return null;
                length = (header[1] << 8) | header[2];
            }
            
            if (length <= 0 || length > 65535) return null;
            
            var packet = new byte[length];
            packet[0] = opcode;
            
            // For variable packets, we already read opcode + 2 length bytes = 3 bytes
            // For fixed packets, we only read opcode = 1 byte
            int alreadyRead = isVariable ? 3 : 1;
            if (isVariable)
            {
                packet[1] = header[1];
                packet[2] = header[2];
            }
            
            int remaining = length - alreadyRead;
            int offset = alreadyRead;
            while (remaining > 0)
            {
                read = await _stream.ReadAsync(packet.AsMemory(offset, remaining), linkedCts.Token);
                if (read == 0) return null;
                offset += read;
                remaining -= read;
            }
            
            BytesReceived += length;
            return packet;
        }
        catch (OperationCanceledException)
        {
            // Timeout - this is expected
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] ReadPacket exception: {Error}", _botId, ex.Message);
            return null;
        }
    }

    private static int GetPacketLength(byte opcode)
    {
        // Packet lengths based on server's PacketDefinitions.cs
        // -1 = variable length (has 2-byte length field after opcode)
        return opcode switch
        {
            0x11 => 91,  // Status bar info
            0x1B => 37,  // Login confirm
            0x1C => -1,  // ASCII speech (variable)
            0x1D => 5,   // Delete object
            0x20 => 19,  // Update mobile
            0x21 => 8,   // Move reject
            0x22 => 3,   // Move ack
            0x24 => 9,   // Container display
            0x25 => 21,  // Add item to container
            0x27 => 2,   // Reject move item
            0x2E => 15,  // Equip item
            0x3A => -1,  // Skills list (variable)
            0x4E => 6,   // Personal light level
            0x4F => 2,   // Global light level
            0x55 => 1,   // Login complete
            0x5D => 73,  // Char select
            0x6C => 19,  // Target cursor
            0x6D => 3,   // Play music
            0x6E => 14,  // Char animation
            0x73 => 2,   // Ping
            0x77 => 17,  // Mobile moving
            0x78 => -1,  // Draw object (variable)
            0x82 => 2,   // Login denied
            0x88 => 66,  // Open paperdoll
            0x8C => 11,  // Relay
            0xA1 => 9,   // Health bar update
            0xA2 => 9,   // Mana bar update
            0xA3 => 9,   // Stamina bar update
            0xA8 => -1,  // Server list (variable)
            0xA9 => -1,  // Char list (variable)
            0xAE => -1,  // Unicode speech (variable)
            0xB9 => 5,   // Enable features (extended, 5 bytes)
            0xBC => 3,   // Season
            0xBF => -1,  // Extended command (variable)
            0xC1 => -1,  // Localized message (variable)
            0xCC => -1,  // Localized message affix (variable)
            0xD6 => -1,  // Mega cliloc (variable)
            0xDC => 9,   // Object cache
            0xDD => -1,  // Compressed gump (variable)
            0xF3 => 26,  // Object info (SA+)
            _ => -1      // Assume variable for unknown
        };
    }

    private static uint ReadUInt32BE(byte[] buf, int offset)
    {
        return ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) |
               ((uint)buf[offset + 2] << 8) | buf[offset + 3];
    }

    private static ushort ReadUInt16BE(byte[] buf, int offset)
    {
        return (ushort)((buf[offset] << 8) | buf[offset + 1]);
    }

    /// <summary>
    /// Read a packet from the game server (Huffman compressed).
    /// Each packet is compressed separately with its own EOF marker.
    /// </summary>
    private async Task<byte[]?> ReadGamePacketAsync(CancellationToken ct, int timeout = 5000)
    {
        if (_stream == null || _tcp == null || !_tcp.Connected) return null;

        var timeoutTime = DateTime.UtcNow.AddMilliseconds(timeout);

        while (DateTime.UtcNow < timeoutTime)
        {
            // Try to extract a packet from the decompressed buffer
            var packet = ExtractPacketFromBuffer();
            if (packet != null)
            {
                BytesReceived += packet.Length;
                return packet;
            }

            // Try to decompress more from the compressed buffer
            if (_compressedBuffer.Count > 0)
            {
                var compressedArray = _compressedBuffer.ToArray();
                var decompressed = HuffmanCompression.DecompressFromServer(
                    compressedArray, 0, compressedArray.Length, out int bytesConsumed);
                
                if (decompressed.Length > 0)
                {
                    _decompressedBuffer.AddRange(decompressed);
                }
                
                // Remove consumed bytes from compressed buffer
                if (bytesConsumed > 0 && bytesConsumed <= _compressedBuffer.Count)
                {
                    _compressedBuffer.RemoveRange(0, bytesConsumed);
                }
                else if (decompressed.Length == 0)
                {
                    // No progress and no output - need more data
                    // Don't clear buffer, we need more bytes
                }
                
                continue; // Try to extract packet again
            }

            // Read more compressed data from network
            if (_stream.DataAvailable || (_compressedBuffer.Count == 0 && _decompressedBuffer.Count == 0))
            {
                var rawBuffer = new byte[4096];
                using var readCts = new CancellationTokenSource(500);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, readCts.Token);

                try
                {
                    int read = await _stream.ReadAsync(rawBuffer.AsMemory(), linked.Token);
                    if (read == 0) 
                    {
                        // Connection closed
                        return null;
                    }
                    
                    // Add to compressed buffer for decompression
                    for (int i = 0; i < read; i++)
                        _compressedBuffer.Add(rawBuffer[i]);
                }
                catch (OperationCanceledException)
                {
                    // Short read timeout, try to decompress what we have
                }
            }
            else
            {
                // No data available and nothing to process, wait a bit
                await Task.Delay(10, ct);
            }
        }

        return null;
    }

    /// <summary>
    /// Extract a complete packet from the decompressed buffer, or null if incomplete.
    /// </summary>
    private byte[]? ExtractPacketFromBuffer()
    {
        if (_decompressedBuffer.Count == 0) return null;

        byte opcode = _decompressedBuffer[0];
        int length = GetPacketLength(opcode);
        bool isVariable = (length == -1);

        if (isVariable)
        {
            if (_decompressedBuffer.Count < 3) return null; // Need at least opcode + 2 length bytes
            length = (_decompressedBuffer[1] << 8) | _decompressedBuffer[2];
        }

        if (length <= 0 || length > 65535) 
        {
            // Invalid packet, skip this byte
            _decompressedBuffer.RemoveAt(0);
            return null;
        }

        if (_decompressedBuffer.Count < length) return null; // Incomplete packet

        // Extract the packet
        var packet = _decompressedBuffer.GetRange(0, length).ToArray();
        _decompressedBuffer.RemoveRange(0, length);
        return packet;
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        try { _behaviorTask?.Wait(1000); } catch { }
        _stream?.Close();
        _tcp?.Close();
        State = BotState.Disconnected;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _cts?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}

public enum BotState
{
    Disconnected,
    Connecting,
    LoggingIn,
    Playing
}

public enum BotBehavior
{
    Idle,
    RandomWalk,
    Combat,
    FullSimulation,
    SmartAI
}
