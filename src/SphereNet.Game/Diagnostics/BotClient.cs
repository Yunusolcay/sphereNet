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
    private Task? _receiveTask;
    
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
    private BotAnomalyScanner? _anomalyScanner;
    private System.Collections.Concurrent.ConcurrentQueue<BotAnomaly>? _anomalySink;
    
    // Buffers for game server packets (Huffman compressed)
    private readonly List<byte> _decompressedBuffer = new();
    private readonly List<byte> _compressedBuffer = new(); // Raw compressed data
    private bool _isGamePhase;

    public BotState State { get; internal set; } = BotState.Disconnected;
    public int PacketsSent { get; private set; }
    public int PacketsReceived { get; private set; }
    public int BytesSent { get; private set; }
    public int BytesReceived { get; private set; }
    public long ConnectTimeMs { get; private set; }
    public long LastActivityMs { get; private set; }
    public int BotId => _botId;
    public BotWorldModel World => _world;

    private BotActionApi? _actionApi;
    public BotActionApi Actions => _actionApi ??= new BotActionApi(this);

    public void SetAnomalySink(System.Collections.Concurrent.ConcurrentQueue<BotAnomaly> sink)
    {
        _anomalySink = sink;
        _anomalyScanner = new BotAnomalyScanner(_botId, _world);
    }

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

    /// <summary>Name-shape test only. NEVER use this to grant a privilege: the
    /// character name is chosen by the player, so an ordinary "SphereBotanist"
    /// matches. Ask <see cref="BotEngine.IsLiveBotCharacter"/> instead, which knows
    /// which characters the server itself is driving.</summary>
    public static bool IsBotCharName(string name) => 
        name.StartsWith(CharPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The character name this bot session logs in as.</summary>
    public string CharName => _charName;

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
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _behaviorTask = Task.Run(() => RunBehaviorLoopAsync(behavior, _cts.Token));
    }

    public void StartBehavior(Behaviors.IBotBehavior roleBehavior, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _behaviorTask = Task.Run(() => RunRoleBehaviorAsync(roleBehavior, _cts.Token));
    }

    /// <summary>Sole socket reader once the bot is playing: continuously reads
    /// packets and updates the world model + completes pending actions, so the
    /// behavior thread sees a current world and its WaitResult calls return
    /// promptly. Behaviors must NOT read the socket themselves (no draining) —
    /// two readers on one stream would corrupt it.</summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && State == BotState.Playing)
            {
                byte[]? packet = _isGamePhase
                    ? await ReadGamePacketAsync(ct, timeout: 200)
                    : await ReadPacketAsync(ct, timeout: 200);
                if (packet != null)
                {
                    PacketsReceived++;
                    ProcessIncomingPacket(packet);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] Receive loop error: {Error}", _botId, ex.Message);
        }
    }

    private async Task RunRoleBehaviorAsync(Behaviors.IBotBehavior roleBehavior, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && State == BotState.Playing)
            {
                // Incoming packets are drained by ReceiveLoopAsync (sole reader).
                await roleBehavior.RunAsync(this, Actions, ct);
                LastActivityMs = Environment.TickCount64;
                if (_anomalyScanner != null && _anomalySink != null)
                    _anomalyScanner.Tick(LastActivityMs, _anomalySink);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] Role behavior error: {Error}", _botId, ex.Message);
        }
        finally
        {
            if (_anomalySink != null)
                _anomalySink.Enqueue(new BotAnomaly
                {
                    Type = BotAnomalyType.UnexpectedDisconnect,
                    BotId = _botId,
                    TimestampMs = Environment.TickCount64,
                    Detail = "Bot disconnected during role behavior loop",
                    Severity = BotAnomalySeverity.Error,
                });
            State = BotState.Disconnected;
        }
    }

    private async Task RunBehaviorLoopAsync(BotBehavior behavior, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && State == BotState.Playing)
            {
                // Incoming packets are drained by ReceiveLoopAsync (sole reader).
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
                if (_anomalyScanner != null && _anomalySink != null)
                    _anomalyScanner.Tick(LastActivityMs, _anomalySink);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] Behavior error: {Error}", _botId, ex.Message);
        }
        finally
        {
            if (_anomalySink != null)
                _anomalySink.Enqueue(new BotAnomaly
                {
                    Type = BotAnomalyType.UnexpectedDisconnect,
                    BotId = _botId,
                    TimestampMs = Environment.TickCount64,
                    Detail = "Bot disconnected during behavior loop",
                    Severity = BotAnomalySeverity.Error,
                });
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

        // The receive loop runs on its own thread; guard world-model mutations
        // against the behavior thread's reads (FindNearest etc.).
        lock (_world.SyncRoot)
        {
        switch (packet[0])
        {
            case 0x20: // Draw Game Player (19 bytes)
                if (packet.Length >= 19)
                {
                    _world.PrevX = _world.X;
                    _world.PrevY = _world.Y;
                    _world.PrevZ = _world.Z;
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
                    _world.KnownItems.Remove(serialDel);
                    if (_world.OpenContainers.Remove(serialDel, out var cont))
                        foreach (var ci in cont.Items)
                            _world.KnownItems.Remove(ci.Serial);
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
                _world.ConsecutivePickupRejects = 0;
                _world.LastActionResult = BotActionResult.Success;
                _world.LastActionTimeMs = now;
                _actionApi?.CompleteMove(true);
                break;

            case 0x21: // Move Rejected (8 bytes)
                if (packet.Length >= 8)
                {
                    _world.MoveRejectCount++;
                    _world.TotalMoveRejects++;
                    _world.PrevX = _world.X;
                    _world.PrevY = _world.Y;
                    _world.PrevZ = _world.Z;
                    _world.X = _x = (short)ReadUInt16BE(packet, 2);
                    _world.Y = _y = (short)ReadUInt16BE(packet, 4);
                    _world.Direction = packet[6];
                    _world.Z = _z = (sbyte)packet[7];
                    _world.LastActionResult = BotActionResult.Rejected;
                    _world.LastActionTimeMs = now;
                    _actionApi?.CompleteMove(false);
                }
                break;

            case 0x6C: // Target Cursor (19 bytes)
                if (packet.Length >= 7)
                {
                    _world.HasPendingTarget = true;
                    _world.TargetCursorId = ReadUInt32BE(packet, 2);
                    _actionApi?.CompleteTarget();
                }
                break;

            case 0x1A: // World Item (variable)
                if (packet.Length >= 7)
                    HandleWorldItem(packet, now);
                break;

            case 0x3C: // Container Content (variable)
                if (packet.Length >= 5)
                {
                    HandleContainerContent(packet, now);
                    _actionApi?.CompletePickUp(true);
                }
                break;

            case 0x25: // Container Item Add (21 bytes, 6.0.1.7+)
                if (packet.Length >= 21)
                    HandleContainerItemAdd(packet, now);
                break;

            case 0x24: // Open Container (9 bytes)
                if (packet.Length >= 9)
                {
                    uint contSerial24 = ReadUInt32BE(packet, 1);
                    ushort gumpId24 = ReadUInt16BE(packet, 5);
                    if (!_world.OpenContainers.ContainsKey(contSerial24))
                        _world.OpenContainers[contSerial24] = new BotContainerState
                            { Serial = contSerial24, GumpId = gumpId24 };
                    _actionApi?.CompleteContainerOpen();
                }
                break;

            case 0xDD: // Compressed Gump (variable)
                if (packet.Length >= 23)
                {
                    uint gumpSerial = ReadUInt32BE(packet, 3);
                    uint gumpId = ReadUInt32BE(packet, 7);
                    int gumpX = (int)ReadUInt32BE(packet, 11);
                    int gumpY = (int)ReadUInt32BE(packet, 15);
                    _world.ActiveGump = new BotGumpState
                    {
                        Serial = gumpSerial,
                        GumpId = gumpId,
                        X = gumpX,
                        Y = gumpY,
                        IsOpen = true,
                        ReceivedMs = now,
                    };
                    _world.ActiveGumpReceivedMs = now;
                    _actionApi?.CompleteGump();
                }
                break;

            case 0x74: // Buy List (variable)
                if (packet.Length >= 8)
                    HandleBuyList(packet);
                break;

            case 0x9E: // Sell List (variable)
                if (packet.Length >= 9)
                    HandleSellList(packet);
                break;

            case 0x1C: // ASCII Message (variable)
                if (packet.Length >= 44)
                    HandleAsciiMessage(packet, now);
                break;

            case 0xAE: // Unicode Message (variable)
                if (packet.Length >= 48)
                    HandleUnicodeMessage(packet, now);
                break;

            case 0x2E: // Equip Item (15 bytes)
                if (packet.Length >= 15)
                {
                    uint eqSerial = ReadUInt32BE(packet, 1);
                    ushort eqItemId = ReadUInt16BE(packet, 5);
                    byte eqLayer = packet[8];
                    uint eqMobile = ReadUInt32BE(packet, 9);
                    ushort eqHue = ReadUInt16BE(packet, 13);
                    if (eqMobile == _charUid)
                    {
                        var eqItem = new BotKnownItem
                        {
                            Serial = eqSerial, ItemId = eqItemId,
                            Layer = eqLayer, Hue = eqHue, LastSeenMs = now,
                        };
                        _world.Equipment[eqLayer] = eqItem;
                        _world.KnownItems[eqSerial] = eqItem;
                    }
                }
                break;

            case 0x27: // Reject Move Item (2 bytes)
                _world.ConsecutivePickupRejects++;
                _world.LastActionResult = BotActionResult.Rejected;
                _world.LastActionTimeMs = now;
                _actionApi?.CompletePickUp(false);
                break;
        }
        }
    }

    private void HandleWorldItem(byte[] packet, long now)
    {
        // 0x1A: [opcode][len:2][serial:4][itemId:2][amount:2][x:2][y:2][z:1][dir:1][hue:2][flags:1]
        // Serial high bit indicates amount field present
        uint serial = ReadUInt32BE(packet, 3);
        bool hasAmount = (serial & 0x80000000) != 0;
        serial &= 0x7FFFFFFF;

        if ((serial & 0x40000000) != 0) return; // skip mobiles

        int offset = 7;
        ushort itemId = ReadUInt16BE(packet, offset); offset += 2;
        bool hasStackId = (itemId & 0x8000) != 0;
        itemId &= 0x3FFF;
        if (hasStackId) offset += 2; // skip stack id

        ushort amount = 0;
        if (hasAmount && offset + 2 <= packet.Length)
        {
            amount = ReadUInt16BE(packet, offset);
            offset += 2;
        }

        if (offset + 5 > packet.Length) return;
        short x = (short)(ReadUInt16BE(packet, offset) & 0x7FFF);
        bool hasDir = (ReadUInt16BE(packet, offset) & 0x8000) != 0;
        offset += 2;
        short y = (short)(ReadUInt16BE(packet, offset) & 0x3FFF);
        bool hasHue = (ReadUInt16BE(packet, offset) & 0x8000) != 0;
        bool hasFlags = (ReadUInt16BE(packet, offset) & 0x4000) != 0;
        offset += 2;

        if (hasDir) offset++;
        sbyte z = offset < packet.Length ? (sbyte)packet[offset++] : (sbyte)0;
        ushort hue = 0;
        if (hasHue && offset + 2 <= packet.Length)
        {
            hue = ReadUInt16BE(packet, offset);
            offset += 2;
        }

        _world.KnownItems[serial] = new BotKnownItem
        {
            Serial = serial, ItemId = itemId, X = x, Y = y, Z = z,
            Amount = amount, Hue = hue, LastSeenMs = now,
        };

        if (itemId == 0x0EED) // gold pile
            RecalcGold();
    }

    private void HandleContainerContent(byte[] packet, long now)
    {
        // 0x3C: [opcode][len:2][count:2] then per item (6.0.1.7+ = 20 bytes each)
        if (packet.Length < 5) return;
        ushort count = ReadUInt16BE(packet, 3);
        int offset = 5;

        uint parentSerial = 0;
        for (int i = 0; i < count && offset + 20 <= packet.Length; i++)
        {
            uint itemSerial = ReadUInt32BE(packet, offset);
            ushort itemId = ReadUInt16BE(packet, offset + 4);
            offset += 7; // serial(4) + itemId(2) + unknown(1)
            ushort amount = ReadUInt16BE(packet, offset); offset += 2;
            short ix = (short)ReadUInt16BE(packet, offset); offset += 2;
            short iy = (short)ReadUInt16BE(packet, offset); offset += 2;
            offset++; // grid index (6.0.1.7+)
            uint contSerial = ReadUInt32BE(packet, offset); offset += 4;
            ushort hue = ReadUInt16BE(packet, offset); offset += 2;

            if (parentSerial == 0) parentSerial = contSerial;

            var item = new BotKnownItem
            {
                Serial = itemSerial, ItemId = itemId, Amount = amount,
                X = ix, Y = iy, Hue = hue,
                ContainerSerial = contSerial, LastSeenMs = now,
            };
            _world.KnownItems[itemSerial] = item;

            if (_world.OpenContainers.TryGetValue(contSerial, out var cs))
                cs.Items.Add(item);
        }

        if (parentSerial != 0 && _world.OpenContainers.TryGetValue(parentSerial, out var container))
        {
            // items were already added above
        }

        RecalcGold();
    }

    private void HandleContainerItemAdd(byte[] packet, long now)
    {
        // 0x25 (21 bytes, 6.0.1.7+): serial(4) itemId(2) unk(1) amount(2) x(2) y(2) grid(1) container(4) hue(2)
        uint serial = ReadUInt32BE(packet, 1);
        ushort itemId = ReadUInt16BE(packet, 5);
        ushort amount = ReadUInt16BE(packet, 8);
        short x = (short)ReadUInt16BE(packet, 10);
        short y = (short)ReadUInt16BE(packet, 12);
        uint contSerial = ReadUInt32BE(packet, 15);
        ushort hue = ReadUInt16BE(packet, 19);

        var item = new BotKnownItem
        {
            Serial = serial, ItemId = itemId, Amount = amount,
            X = x, Y = y, Hue = hue,
            ContainerSerial = contSerial, LastSeenMs = now,
        };
        _world.KnownItems[serial] = item;

        if (_world.OpenContainers.TryGetValue(contSerial, out var cs))
            cs.Items.Add(item);

        _world.ConsecutivePickupRejects = 0;
        _world.LastActionResult = BotActionResult.Success;
        _world.LastActionTimeMs = now;

        if (itemId == 0x0EED) RecalcGold();
    }

    private void HandleBuyList(byte[] packet)
    {
        // 0x74: [opcode][len:2][vendorSerial:4][count:1] then items
        uint vendorSerial = ReadUInt32BE(packet, 3);
        byte count = packet[7];
        var vendor = new BotVendorState { VendorSerial = vendorSerial, IsBuyList = true };

        int offset = 8;
        for (int i = 0; i < count && offset + 5 <= packet.Length; i++)
        {
            int price = (int)ReadUInt32BE(packet, offset); offset += 4;
            byte nameLen = packet[offset++];
            string name = nameLen > 0 && offset + nameLen <= packet.Length
                ? System.Text.Encoding.ASCII.GetString(packet, offset, nameLen) : "";
            offset += nameLen;
            vendor.Items.Add(new BotVendorItem { Price = price, Name = name });
        }

        _world.ActiveVendor = vendor;
    }

    private void HandleSellList(byte[] packet)
    {
        // 0x9E: [opcode][len:2][vendorSerial:4][count:2] then items
        uint vendorSerial = ReadUInt32BE(packet, 3);
        ushort count = ReadUInt16BE(packet, 7);
        var vendor = new BotVendorState { VendorSerial = vendorSerial, IsBuyList = false };

        int offset = 9;
        for (int i = 0; i < count && offset + 14 <= packet.Length; i++)
        {
            uint itemSerial = ReadUInt32BE(packet, offset); offset += 4;
            ushort itemId = ReadUInt16BE(packet, offset); offset += 2;
            ushort hue = ReadUInt16BE(packet, offset); offset += 2;
            ushort amount = ReadUInt16BE(packet, offset); offset += 2;
            int price = (int)ReadUInt16BE(packet, offset); offset += 2;
            ushort nameLen = ReadUInt16BE(packet, offset); offset += 2;
            string name = nameLen > 0 && offset + nameLen <= packet.Length
                ? System.Text.Encoding.ASCII.GetString(packet, offset, nameLen) : "";
            offset += nameLen;
            vendor.Items.Add(new BotVendorItem
            {
                Serial = itemSerial, ItemId = itemId, Amount = amount,
                Price = price, Name = name,
            });
        }

        _world.ActiveVendor = vendor;
    }

    private void HandleAsciiMessage(byte[] packet, long now)
    {
        // 0x1C: [opcode][len:2][serial:4][body:2][type:1][hue:2][font:2][name:30][text:variable]
        if (packet.Length < 45) return;
        uint serial = ReadUInt32BE(packet, 3);
        int nameEnd = 14;
        string name = System.Text.Encoding.ASCII.GetString(packet, nameEnd, 30).TrimEnd('\0');
        int textStart = 44;
        int textLen = packet.Length - textStart - 1;
        if (textLen <= 0) return;
        string text = System.Text.Encoding.ASCII.GetString(packet, textStart, textLen).TrimEnd('\0');

        _world.Journal.Push(new BotJournalEntry
        {
            TimestampMs = now, SpeakerSerial = serial,
            Speaker = name, Text = text,
        });
    }

    private void HandleUnicodeMessage(byte[] packet, long now)
    {
        // 0xAE: [opcode][len:2][serial:4][body:2][type:1][hue:2][font:2][lang:4][name:30][text:unicode]
        if (packet.Length < 50) return;
        uint serial = ReadUInt32BE(packet, 3);
        string name = System.Text.Encoding.ASCII.GetString(packet, 18, 30).TrimEnd('\0');
        int textStart = 48;
        int textBytes = packet.Length - textStart;
        if (textBytes <= 0) return;
        string text = System.Text.Encoding.BigEndianUnicode
            .GetString(packet, textStart, textBytes).TrimEnd('\0');

        _world.Journal.Push(new BotJournalEntry
        {
            TimestampMs = now, SpeakerSerial = serial,
            Speaker = name, Text = text,
        });
    }

    private void RecalcGold()
    {
        int total = 0;
        foreach (var item in _world.KnownItems.Values)
            if (item.ItemId == 0x0EED)
                total += item.Amount == 0 ? 1 : item.Amount;
        _world.Gold = total;
    }

    private async Task DoRandomWalkAsync(CancellationToken ct)
    {
        byte dir = (byte)_rng.Next(0, 8);
        _world.TotalMoveRequests++;
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
                _world.TotalMoveRequests++;
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

    internal void SendRawPacket(byte[] packet)
    {
        if (_stream == null || !_tcp!.Connected) return;
        try
        {
            _stream.Write(packet);
            PacketsSent++;
            BytesSent += packet.Length;
        }
        catch { State = BotState.Disconnected; }
    }

    internal void SendMovePacket(byte dir)
    {
        SendRawPacket(BotPacketBuilder.BuildMoveRequest(dir, _moveSequence++));
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
            0x1A => -1,  // World item (variable)
            0x1C => -1,  // ASCII speech (variable)
            0x1D => 5,   // Delete object
            0x20 => 19,  // Update mobile
            0x21 => 8,   // Move reject
            0x22 => 3,   // Move ack
            0x24 => 9,   // Container display
            0x25 => 21,  // Add item to container
            0x27 => 2,   // Reject move item
            0x2E => 15,  // Equip item
            0x3C => -1,  // Container content (variable)
            0x3A => -1,  // Skills list (variable)
            0x4E => 6,   // Personal light level
            0x4F => 2,   // Global light level
            0x55 => 1,   // Login complete
            0x5D => 73,  // Char select
            0x6C => 19,  // Target cursor
            0x6D => 3,   // Play music
            0x6E => 14,  // Char animation
            0x6F => -1,  // Secure trade (variable)
            0x73 => 2,   // Ping
            0x74 => -1,  // Buy list (variable)
            0x77 => 17,  // Mobile moving
            0x78 => -1,  // Draw object (variable)
            0x82 => 2,   // Login denied
            0x88 => 66,  // Open paperdoll
            0x8C => 11,  // Relay
            0x9E => -1,  // Sell list (variable)
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
        _actionApi?.CompleteDisconnect();
        try { _behaviorTask?.Wait(1000); } catch { }
        try { _receiveTask?.Wait(1000); } catch { }
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
    SmartAI,
    Walker,
    CombatRole,
    Vendor,
    Loot,
    Skill,
    Social,
    Chaos,
    Cluster,
    Mage,
}
