using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;

namespace SphereNet.Network.Manager;

/// <summary>
/// Central network manager. Maps to CNetworkManager in Source-X.
/// Manages listen socket, connection pool, accept, input/output processing.
/// </summary>
public sealed class NetworkManager : IDisposable
{
    private Socket? _listenSocket;
    private readonly NetState[] _states;
    private readonly PacketManager _packetManager;
    private readonly ILogger<NetworkManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private int _maxClients;
    private bool _isRunning;

    public PacketManager Packets => _packetManager;
    public int ActiveConnections => _states.Count(s => s.IsInUse);

    public CryptConfig? CryptConfig { get; set; }
    public bool UseCrypt { get; set; } = true;
    public bool UseNoCrypt { get; set; }
    public bool DebugPackets { get; set; }
    public HashSet<byte>? DebugPacketOpcodeFilter { get; set; }
    public int MaxPacketsPerTick { get; set; } = 100;
    public Func<NetState, byte, byte[], bool>? PacketScriptHook { get; set; }

    /// <summary>Fired when a connection is about to be cleaned up (before Clear).</summary>
    public event Action<int>? OnConnectionClosed;
    public event Action<NetState>? OnConnectionAccepted;
    public event Action<NetState, byte, byte[]>? OnUnknownPacket;
    public event Action<NetState, int>? OnPacketQuotaExceeded;
    public event Action<NetState>? OnConnectionClosedState;

    public NetworkManager(int maxClients, ILoggerFactory loggerFactory)
    {
        _maxClients = maxClients;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NetworkManager>();
        _packetManager = new PacketManager();
        _states = new NetState[maxClients];

        for (int i = 0; i < maxClients; i++)
        {
            _states[i] = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = i };
        }

        RegisterStandardPackets();
    }

    private void RegisterStandardPackets()
    {
        _packetManager.Register(new PacketLoginRequest());
        _packetManager.Register(new PacketGameLogin());
        _packetManager.Register(new PacketCharSelect());
        _packetManager.Register(new PacketCreateCharacter());
        _packetManager.Register(new PacketCreateCharacterHS());
        _packetManager.Register(new PacketPing());
        _packetManager.Register(new PacketMoveRequest());
        _packetManager.Register(new PacketSpeechRequest());
        _packetManager.Register(new PacketAttackRequest());
        _packetManager.Register(new PacketWarMode());
        _packetManager.Register(new PacketTextCommand());
        _packetManager.Register(new PacketSkillLock());
        _packetManager.Register(new PacketResyncRequest());
        _packetManager.Register(new PacketLogoutRequest());
        _packetManager.Register(new PacketHelpRequest());
        _packetManager.Register(new PacketServerSelect());
        _packetManager.Register(new PacketDoubleClick());
        _packetManager.Register(new PacketSingleClick());
        _packetManager.Register(new PacketItemPickup());
        _packetManager.Register(new PacketItemDrop());
        _packetManager.Register(new PacketItemEquip());
        _packetManager.Register(new PacketStatusRequest());
        _packetManager.Register(new PacketTargetResponse());
        _packetManager.Register(new PacketSpeechUnicode());
        _packetManager.Register(new PacketGumpResponse());
        _packetManager.Register(new PacketClientVersion());
        _packetManager.Register(new PacketExtendedCommand());
        _packetManager.Register(new PacketEncodedCommand());
        _packetManager.Register(new PacketAOSTooltipReq());
        _packetManager.Register(new PacketVendorBuy());
        _packetManager.Register(new PacketVendorSell());
        _packetManager.Register(new PacketSecureTrade());
        _packetManager.Register(new PacketRename());
        _packetManager.Register(new PacketProfileRequest());
        _packetManager.Register(new PacketViewRange());

        // Phase 1: Critical Stability
        _packetManager.Register(new PacketDeathMenu());
        _packetManager.Register(new PacketCharDelete());
        _packetManager.Register(new PacketDyeResponse());
        _packetManager.Register(new PacketPromptResponse());
        _packetManager.Register(new PacketMenuChoice());

        // Phase 2: Content Features
        _packetManager.Register(new PacketBookPage());
        _packetManager.Register(new PacketBookHeader());
        _packetManager.Register(new PacketBulletinBoard());
        _packetManager.Register(new PacketMapDetail());
        _packetManager.Register(new PacketMapPinEdit());

        // Phase 3: Client Compatibility
        _packetManager.Register(new PacketHardwareInfo());
        _packetManager.Register(new PacketSystemInfo());
        _packetManager.Register(new PacketAssistVersion());
        _packetManager.Register(new PacketGumpTextEntry());
        _packetManager.Register(new PacketAllNamesReq());
        _packetManager.Register(new PacketChatText());
    }

    /// <summary>Initialize the listen socket.</summary>
    public bool Start(string ip, int port)
    {
        try
        {
            var addr = ip == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(ip);
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listenSocket.Bind(new IPEndPoint(addr, port));
            _listenSocket.Listen(32);
            _listenSocket.Blocking = false;
            _isRunning = true;
            _logger.LogInformation("Listening on {IP}:{Port}", ip, port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start listener on {IP}:{Port}", ip, port);
            return false;
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _listenSocket?.Close();
        _listenSocket = null;

        foreach (var state in _states)
        {
            if (state.IsInUse)
                state.Clear();
        }
    }

    /// <summary>
    /// Accept new connections. Called from main tick.
    /// </summary>
    public void CheckNewConnections()
    {
        if (_listenSocket == null || !_isRunning) return;

        try
        {
            while (_listenSocket.Poll(0, SelectMode.SelectRead))
            {
                Socket clientSocket = _listenSocket.Accept();
                var slot = FindFreeSlot();
                if (slot == null)
                {
                    _logger.LogWarning("No free slots, rejecting connection from {EP}", clientSocket.RemoteEndPoint);
                    clientSocket.Close();
                    continue;
                }

                slot.Init(clientSocket);
                slot.DebugPackets = DebugPackets;
                _logger.LogInformation("Connection #{Id} from {EP}", slot.Id, slot.RemoteEndPoint);
                OnConnectionAccepted?.Invoke(slot);
            }
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Accept poll interrupted");
        }
    }

    /// <summary>
    /// Process all incoming data. Called from main loop BEFORE World.OnTick/RunServerTick
    /// to ensure packets are handled promptly for low latency.
    /// </summary>
    public void ProcessAllInput()
    {
        foreach (var state in _states)
        {
            if (!state.CanReceive) continue;

            int read = state.Receive();
            if (read < 0)
            {
                _logger.LogInformation("Connection #{Id} lost", state.Id);
                state.MarkClosing();
                continue;
            }

            if (read == 0) continue;

            ProcessInput(state);
        }
    }

    private void ProcessInput(NetState state)
    {
        var data = state.ReceivedData;
        if (data.Length == 0) return;

        // First bytes of a new connection is the seed.
        // Classic clients send 4 raw bytes. 7.0+ clients send packet 0xEF (21 bytes).
        if (!state.IsSeeded)
        {
            if (data[0] == 0xEF && data.Length >= 21)
            {
                state.Seed = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                uint major = (uint)((data[5] << 24) | (data[6] << 16) | (data[7] << 8) | data[8]);
                uint minor = (uint)((data[9] << 24) | (data[10] << 16) | (data[11] << 8) | data[12]);
                uint rev = (uint)((data[13] << 24) | (data[14] << 16) | (data[15] << 8) | data[16]);
                uint patch = (uint)((data[17] << 24) | (data[18] << 16) | (data[19] << 8) | data[20]);
                state.ClientVersionNumber = major * 10_000_000 + minor * 1_000_000 + rev * 1_000 + patch;
                state.IsSeeded = true;
                _logger.LogTrace("Seed 0xEF #{Id}: seed=0x{Seed:X8}, ver={Major}.{Minor}.{Rev}.{Patch}",
                    state.Id, state.Seed, major, minor, rev, patch);
                state.ConsumeReceived(21);
            }
            else if (data.Length >= 4 && data[0] != 0xEF)
            {
                state.Seed = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                state.IsSeeded = true;
                _logger.LogTrace("Seed classic #{Id}: seed=0x{Seed:X8}", state.Id, state.Seed);
                state.ConsumeReceived(4);
            }
            else
            {
                return;
            }

            data = state.ReceivedData;
            if (data.Length == 0) return;
        }

        // Encryption auto-detection on first real packet after seed
        if (!state.Crypto.IsInitialized && data.Length > 0)
        {
            if (!TryInitCrypto(state, data))
                return;
            data = state.ReceivedData;
            if (data.Length == 0) return;
        }

        // Decrypt any new data with the established cipher
        if (state.Crypto.IsInitialized && state.Crypto.EncType != EncryptionType.None)
        {
            int undecrypted = state.UndecryptedOffset;
            if (undecrypted < data.Length)
            {
                byte[] toDecrypt = data[undecrypted..].ToArray();
                state.Crypto.Decrypt(toDecrypt, 0, toDecrypt.Length);
                state.ReplaceReceivedRange(undecrypted, toDecrypt);
                state.UndecryptedOffset = data.Length;
                data = state.ReceivedData;
            }
        }

        int consumed = 0;
        int packetsProcessed = 0;
        while (consumed < data.Length)
        {
            if (MaxPacketsPerTick > 0 && packetsProcessed >= MaxPacketsPerTick)
            {
                long now = Environment.TickCount64;
                if (now - state.PacketFloodWindowStart > 10_000)
                {
                    state.PacketFloodCount = 0;
                    state.PacketFloodWindowStart = now;
                }
                state.PacketFloodCount++;
                if (state.PacketFloodCount >= 5)
                {
                    _logger.LogWarning("Packet flood detected for #{Id}, dropping connection", state.Id);
                    state.MarkClosing();
                    break;
                }
                OnPacketQuotaExceeded?.Invoke(state, packetsProcessed);
                break;
            }

            byte opcode = data[consumed];
            int definedLen = PacketDefinitions.GetPacketLength(opcode, state);
            bool hasLengthField = definedLen == 0;
            int packetLen = definedLen;

            if (hasLengthField)
            {
                if (data.Length - consumed < 3) break;
                packetLen = (data[consumed + 1] << 8) | data[consumed + 2];
            }

            const int MaxPacketSize = 65535;
            if (packetLen <= 0 || packetLen > MaxPacketSize)
            {
                _logger.LogWarning("Invalid packet length {Len} from #{Id}, dropping connection", packetLen, state.Id);
                state.MarkClosing();
                break;
            }
            if (data.Length - consumed < packetLen) break;

            var handler = _packetManager.GetHandler(opcode);
            if (handler != null)
            {
                var rawPacket = data.Slice(consumed, packetLen).ToArray();
                if (InvokePacketScriptHook(state, opcode, rawPacket))
                {
                    packetsProcessed++;
                    consumed += packetLen;
                    continue;
                }

                if (ShouldLogPacketDebug(opcode))
                {
                    _logger.LogDebug("RECV #{Id} 0x{Op:X2} ({Name}) len={Len} data=[{Data}]",
                        state.Id, opcode, handler.GetType().Name,
                        packetLen, FormatHex(rawPacket, 32));
                }

                int payloadOffset = hasLengthField ? 3 : 1;
                int payloadLength = packetLen - payloadOffset;
                if (payloadLength < 0)
                {
                    _logger.LogWarning("Invalid packet length #{Id} 0x{Op:X2}: total={Total} payloadOffset={Offset}",
                        state.Id, opcode, packetLen, payloadOffset);
                    break;
                }

                var buffer = new PacketBuffer(data.Slice(consumed + payloadOffset, payloadLength).ToArray());
                handler.OnReceive(buffer, state);
            }
            else
            {
                var rawBytes = data.Slice(consumed, packetLen).ToArray();
                OnUnknownPacket?.Invoke(state, opcode, rawBytes);
                if (ShouldLogPacketDebug(opcode))
                {
                    _logger.LogDebug("RECV #{Id} 0x{Op:X2} (UNHANDLED) len={Len} data=[{Data}]",
                        state.Id, opcode, packetLen, FormatHex(rawBytes.AsSpan(0, Math.Min(rawBytes.Length, 32)), 32));
                }
                else
                {
                    _logger.LogTrace("Unhandled packet 0x{Op:X2} from #{Id}", opcode, state.Id);
                }
            }

            consumed += packetLen;
            packetsProcessed++;
        }

        if (consumed > 0)
            state.ConsumeReceived(consumed);
    }

    /// <summary>
    /// Attempt to detect encryption and decrypt the first packet.
    /// Returns true if data was processed (or should be retried), false to wait for more data.
    /// </summary>
    private bool TryInitCrypto(NetState state, ReadOnlySpan<byte> data)
    {
        var config = CryptConfig;

        if (config == null || config.Keys.Count == 0)
        {
            // No crypt config loaded — assume plaintext
            state.Crypto.Reset();
            // Mark as initialized with no encryption via a direct approach
            byte[]? result = state.Crypto.DetectAndDecryptLogin(
                state.Seed, data, new CryptConfig(), false, true);
            if (result != null)
            {
                ReplaceReceivedData(state, result);
                return true;
            }
            // Not a login packet? Check game login
            result = state.Crypto.DetectAndDecryptGameLogin(
                state.Seed, data, new CryptConfig(), false, true);
            if (result != null)
            {
                ReplaceReceivedData(state, result);
                if (state.ClientVersionNumber == 0 && state.Crypto.RelayClientVersion > 0)
                    state.ClientVersionNumber = state.Crypto.RelayClientVersion;
                return true;
            }
            return false;
        }

        // Try game login first (65 bytes) — if we try login (62 bytes) first on 65-byte data,
        // it might falsely match and corrupt the crypto state.
        bool isGameLogin = data.Length >= 65;
        bool isLoginPacket = data.Length >= 62 && !isGameLogin;

        if (isGameLogin)
        {
            _logger.LogDebug("Game login detection for #{Id}: seed=0x{Seed:X8}, bytes: {B0:X2} {B1:X2} {B2:X2} {B3:X2}, len={Len}",
                state.Id, state.Seed, data[0], data[1], data[2], data[3], data.Length);

            var result = state.Crypto.DetectAndDecryptGameLogin(
                state.Seed, data[..65], config, UseCrypt, UseNoCrypt || !UseCrypt);
            if (result != null)
            {
                byte[] newData = new byte[result.Length + (data.Length - 65)];
                result.CopyTo(newData, 0);
                if (data.Length > 65)
                    data[65..].CopyTo(newData.AsSpan(65));
                ReplaceReceivedData(state, newData);
                state.ConnectionType = ConnectType.Game;
                // Restore client version from login connection (carried via relay keys)
                if (state.ClientVersionNumber == 0 && state.Crypto.RelayClientVersion > 0)
                    state.ClientVersionNumber = state.Crypto.RelayClientVersion;
                _logger.LogDebug("Game login encryption detected for #{Id}: {Enc}", state.Id, state.Crypto.EncType);
                return true;
            }
            state.Crypto.Reset();

            // Didn't match as game login; fall through to try as login
            isLoginPacket = true;
        }

        if (isLoginPacket)
        {
            var result = state.Crypto.DetectAndDecryptLogin(
                state.Seed, data[..62], config, UseCrypt, UseNoCrypt || !UseCrypt);
            if (result != null)
            {
                byte[] newData = new byte[result.Length + (data.Length - 62)];
                result.CopyTo(newData, 0);
                if (data.Length > 62)
                    data[62..].CopyTo(newData.AsSpan(62));
                ReplaceReceivedData(state, newData);
                state.ConnectionType = ConnectType.Login;
                _logger.LogInformation("Login encryption detected for #{Id}: {Enc}", state.Id, state.Crypto.EncType);
                return true;
            }
        }

        _logger.LogWarning("Failed to detect encryption for #{Id}, first byte: 0x{B:X2}, len: {Len}",
            state.Id, data[0], data.Length);
        state.MarkClosing();
        return false;
    }

    private static void ReplaceReceivedData(NetState state, byte[] newData)
    {
        state.ConsumeReceived(state.ReceivedData.Length);
        state.InjectReceived(newData);
        state.UndecryptedOffset = newData.Length;
    }

    /// <summary>
    /// Flush all outgoing data. Called from main tick.
    /// </summary>
    public void ProcessAllOutput()
    {
        foreach (var state in _states)
        {
            if (state.IsInUse)
                state.FlushOutput();
        }
    }

    /// <summary>Idle timeout: drop connections with no activity for this duration.</summary>
    private const long IdleTimeoutMs = 120_000; // 2 dakika
    private const long UnauthIdleTimeoutMs = 15_000; // 15 saniye — seed/login olmayan bağlantılar

    /// <summary>
    /// Cleanup closed connections and drop idle ones. Called from main tick.
    /// </summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        foreach (var state in _states)
        {
            if (!state.IsInUse) continue;

            // Idle timeout — unauthenticated bağlantılar 15s, normal 2 dakika
            long timeout = state.IsSeeded ? IdleTimeoutMs : UnauthIdleTimeoutMs;
            if (!state.IsClosing && state.LastActivityTick > 0 &&
                now - state.LastActivityTick > timeout)
            {
                _logger.LogInformation("Connection #{Id} idle timeout ({Ms}ms)", state.Id,
                    now - state.LastActivityTick);
                state.MarkClosing();
            }

            if (state.IsClosing)
            {
                _logger.LogInformation("Closing connection #{Id}", state.Id);
                OnConnectionClosedState?.Invoke(state);
                OnConnectionClosed?.Invoke(state.Id);
                state.Clear();
            }
        }
    }

    /// <summary>Get all active connections.</summary>
    public IEnumerable<NetState> GetActiveStates()
    {
        foreach (var state in _states)
        {
            if (state.IsInUse && !state.IsClosing)
                yield return state;
        }
    }

    public bool InvokePacketScriptHook(NetState state, byte opcode, byte[] packet) =>
        PacketScriptHook?.Invoke(state, opcode, packet) == true;

    /// <summary>Register event handlers on all states (for connecting game logic).</summary>
    public void SetHandlers(
        Action<NetState, string, string>? loginRequest = null,
        Action<NetState, string, string, uint>? gameLogin = null,
        Action<NetState, int, string>? charSelect = null,
        Action<NetState, byte, byte, uint>? moveRequest = null,
        Action<NetState, byte, ushort, ushort, string>? speech = null,
        Action<NetState, uint>? attackRequest = null,
        Action<NetState, bool>? warMode = null,
        Action<NetState, uint>? doubleClick = null,
        Action<NetState, uint>? singleClick = null,
        Action<NetState, uint, ushort>? itemPickup = null,
        Action<NetState, uint, short, short, sbyte, uint>? itemDrop = null,
        Action<NetState, uint, byte, uint>? itemEquip = null,
        Action<NetState, byte, uint>? statusRequest = null,
        Action<NetState, byte, uint, uint, short, short, sbyte, ushort>? targetResponse = null,
        Action<NetState, uint, uint, uint, uint[], (ushort Id, string Text)[]>? gumpResponse = null,
        Action<NetState, string>? clientVersion = null,
        Action<NetState, byte>? viewRange = null,
        Action<NetState, uint>? aosTooltip = null,
        Action<NetState, byte, string>? textCommand = null,
        Action<NetState, ushort, PacketBuffer>? extendedCommand = null,
        Action<NetState>? resyncRequest = null,
        Action<NetState>? logoutRequest = null,
        Action<NetState>? helpRequest = null,
        Action<NetState, ushort>? serverSelect = null,
        Action<NetState, string>? charCreate = null,
        Action<NetState, uint, byte, List<Packets.Incoming.VendorBuyEntry>>? vendorBuy = null,
        Action<NetState, uint, List<Packets.Incoming.VendorSellEntry>>? vendorSell = null,
        Action<NetState, byte, uint, uint>? secureTrade = null,
        Action<NetState, uint, string>? rename = null,
        Action<NetState, byte, uint, string>? profileRequest = null,
        // Phase 1
        Action<NetState, byte>? deathMenu = null,
        Action<NetState, int, string>? charDelete = null,
        Action<NetState, uint, ushort>? dyeResponse = null,
        Action<NetState, uint, uint, uint, string>? promptResponse = null,
        Action<NetState, uint, ushort, ushort, ushort>? menuChoice = null,
        // Phase 2
        Action<NetState, uint, List<(ushort PageNum, string[] Lines)>>? bookPage = null,
        Action<NetState, uint, bool, string, string>? bookHeader = null,
        Action<NetState, uint>? bulletinBoardRequestList = null,
        Action<NetState, uint, uint>? bulletinBoardRequestMessage = null,
        Action<NetState, uint, uint, string, string[]>? bulletinBoardPost = null,
        Action<NetState, uint, uint>? bulletinBoardDelete = null,
        Action<NetState, uint>? mapDetail = null,
        Action<NetState, uint, byte, byte, ushort, ushort>? mapPinEdit = null,
        // Phase 3
        Action<NetState, uint, ushort, byte, string>? gumpTextEntry = null,
        Action<NetState, uint>? allNamesRequest = null)
    {
        foreach (var state in _states)
        {
            state.LoginRequestHandler = loginRequest;
            state.GameLoginHandler = gameLogin;
            state.CharSelectHandler = charSelect;
            state.CharCreateHandler = charCreate;
            state.MoveRequestHandler = moveRequest;
            state.SpeechHandler = speech;
            state.AttackRequestHandler = attackRequest;
            state.WarModeHandler = warMode;
            state.DoubleClickHandler = doubleClick;
            state.SingleClickHandler = singleClick;
            state.ItemPickupHandler = itemPickup;
            state.ItemDropHandler = itemDrop;
            state.ItemEquipHandler = itemEquip;
            state.StatusRequestHandler = statusRequest;
            state.TargetResponseHandler = targetResponse;
            state.GumpResponseHandler = gumpResponse;
            state.ClientVersionHandler = clientVersion;
            state.ViewRangeHandler = viewRange;
            state.AOSTooltipHandler = aosTooltip;
            state.TextCommandHandler = textCommand;
            state.ExtendedCommandHandler = extendedCommand;
            state.ResyncRequestHandler = resyncRequest;
            state.LogoutRequestHandler = logoutRequest;
            state.HelpRequestHandler = helpRequest;
            state.ServerSelectHandler = serverSelect;
            state.VendorBuyHandler = vendorBuy;
            state.VendorSellHandler = vendorSell;
            state.SecureTradeHandler = secureTrade;
            state.RenameHandler = rename;
            state.ProfileRequestHandler = profileRequest;

            // Phase 1
            state.DeathMenuHandler = deathMenu;
            state.CharDeleteHandler = charDelete;
            state.DyeResponseHandler = dyeResponse;
            state.PromptResponseHandler = promptResponse;
            state.MenuChoiceHandler = menuChoice;

            // Phase 2
            state.BookPageHandler = bookPage;
            state.BookHeaderHandler = bookHeader;
            state.BulletinBoardRequestListHandler = bulletinBoardRequestList;
            state.BulletinBoardRequestMessageHandler = bulletinBoardRequestMessage;
            state.BulletinBoardPostHandler = bulletinBoardPost;
            state.BulletinBoardDeleteHandler = bulletinBoardDelete;
            state.MapDetailHandler = mapDetail;
            state.MapPinEditHandler = mapPinEdit;

            // Phase 3
            state.GumpTextEntryHandler = gumpTextEntry;
            state.AllNamesRequestHandler = allNamesRequest;
        }
    }

    private NetState? FindFreeSlot()
    {
        foreach (var state in _states)
        {
            if (!state.IsInUse) return state;
        }
        return null;
    }

    public void Dispose() => Stop();

    private bool ShouldLogPacketDebug(byte opcode)
    {
        if (!DebugPackets)
            return false;

        // Skip ping spam by default.
        if (opcode == 0x73)
            return false;

        // Optional opcode whitelist. If empty/null => log all packets.
        var filter = DebugPacketOpcodeFilter;
        if (filter == null || filter.Count == 0)
            return true;

        return filter.Contains(opcode);
    }

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
