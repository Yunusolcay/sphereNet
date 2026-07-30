using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.World;
using SphereNet.Network.Encryption;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

// Loopback login integration. Drives the REAL receive pipeline of the production
// NetworkManager — seed parse -> crypto auto-detect (no-crypt) -> packet framing
// -> handler dispatch -> GameClient -> outgoing packets — over an in-process
// NetState, without a TCP socket. Raw client bytes are fed through
// NetState.InjectReceived and the (private) NetworkManager.ProcessInput pump, the
// same code path a socket read would reach via ProcessAllInput. The login->relay
// ->game-login->char-list->enter-world sequence is exercised across two
// connections (login server then game server) exactly as a real client does,
// including the 0xA0 -> authId/relay -> 0x91 handoff.
//
// Determinism over socket fidelity: the only thing skipped versus a real loopback
// socket is the TCP Receive() itself, which InjectReceived reproduces byte for
// byte. No ports, no accept timing, no async reads -> no CI flakiness. This
// mirrors the assembly's existing reflection-based in-process test style.
public class ClientLoginIntegrationTests
{
    private static readonly MethodInfo s_processInput =
        typeof(NetworkManager).GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("NetworkManager.ProcessInput not found");

    private const uint Seed = 0x12345678;
    private const uint RelayAuthId = 0x0A0B0C0D;

    // Feed raw client bytes into a state's receive buffer and run one pump of the
    // production input pipeline (seed/crypto/framing/dispatch).
    private static void Pump(NetworkManager nm, NetState state, byte[] wire)
    {
        state.InjectReceived(wire);
        s_processInput.Invoke(nm, [state]);
    }

    private static List<byte> Outgoing(NetState state) =>
        TestHarness.GetQueuedPackets(state)
            .Where(p => p.Span.Length > 0)
            .Select(p => p.Span[0])
            .ToList();

    private static NetworkManager NewManager(ILoggerFactory lf) =>
        new(maxClients: 8, lf) { UseNoCrypt = true, UseCrypt = false };

    private static (GameClient client, NetState state) NewConnection(
        ILoggerFactory lf, GameWorld world, AccountManager accounts)
    {
        var state = TestHarness.CreateActiveNetState(lf, 1);
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());

        // Mirror Program.NetworkHandlers wiring: route the parsed callbacks into
        // this connection's GameClient. The 0xA0 server-select handler reproduces
        // Program.OnServerSelect (store relay keys, emit 0x8C, close login conn).
        state.LoginRequestHandler = (_, acct, pwd) => client.HandleLoginRequest(acct, pwd);
        state.GameLoginHandler = (_, acct, pwd, authId) => client.HandleGameLogin(acct, pwd, authId);
        state.CharSelectHandler = (_, slot, name) => client.HandleCharSelect(slot, name);
        state.ServerSelectHandler = (s, _) =>
        {
            s.AuthId = RelayAuthId;
            CryptoState.StoreRelayKeys(RelayAuthId, s.Crypto.Key1, s.Crypto.Key2, s.ClientVersionNumber);
            s.Send(new PacketRelay(0x7F000001, 0, RelayAuthId));
            s.MarkClosing();
        };
        return (client, state);
    }

    // ---- raw client packet builders (post-opcode framing handled by the pump) --

    private static void PutU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    private static void PutAscii(byte[] b, int off, int len, string s)
    {
        for (int i = 0; i < len && i < s.Length; i++)
            b[off + i] = (byte)s[i];
    }

    private static byte[] SeedBytes()
    {
        var b = new byte[4];
        PutU32(b, 0, Seed);
        return b;
    }

    // 0x80 login request: opcode + account(30) + password(30) + nextKey(1) = 62.
    private static byte[] LoginPacket(string account, string password)
    {
        var b = new byte[62];
        b[0] = 0x80;
        PutAscii(b, 1, 30, account);
        PutAscii(b, 31, 30, password);
        return b;
    }

    // 0xA0 server select: opcode + serverIndex(2) = 3.
    private static byte[] ServerSelectPacket(ushort index)
    {
        var b = new byte[3];
        b[0] = 0xA0;
        b[1] = (byte)(index >> 8); b[2] = (byte)index;
        return b;
    }

    // 0x91 game login: opcode + authId(4) + account(30) + password(30) = 65.
    private static byte[] GameLoginPacket(uint authId, string account, string password)
    {
        var b = new byte[65];
        b[0] = 0x91;
        PutU32(b, 1, authId);
        PutAscii(b, 5, 30, account);
        PutAscii(b, 35, 30, password);
        return b;
    }

    // 0x5D char select: opcode + pattern1(4) + name(30) + unk(2) + clientFlag(4)
    // + pattern2(4) + loginCount(4) + padding(16) + slotIndex(4) + clientIp(4) = 73.
    private static byte[] CharSelectPacket(int slot, string name)
    {
        var b = new byte[73];
        b[0] = 0x5D;
        PutAscii(b, 5, 30, name);
        PutU32(b, 65, (uint)slot);
        return b;
    }

    private static byte[] Concat(byte[] a, byte[] c)
    {
        var r = new byte[a.Length + c.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(c, 0, r, a.Length, c.Length);
        return r;
    }

    private static (GameWorld world, AccountManager accounts, ILoggerFactory lf) CreateEnv()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        var accounts = new AccountManager(lf) { AutoCreateAccounts = true };
        return (world, accounts, lf);
    }

    [Fact]
    public void LoginServer_RawSeedAndLogin_SendsServerList()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);
        var (_, login) = NewConnection(lf, world, accounts);

        // Classic client coalesces the 4-byte seed with the 0x80 login packet.
        Pump(nm, login, Concat(SeedBytes(), LoginPacket("hero", "secret")));

        Assert.True(login.IsSeeded);
        Assert.Equal(ConnectType.Login, login.ConnectionType);
        Assert.Contains((byte)0xA8, Outgoing(login)); // server list
    }

    [Fact]
    public void ServerSelect_SendsRelayWithAuthId()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);
        var (_, login) = NewConnection(lf, world, accounts);

        Pump(nm, login, Concat(SeedBytes(), LoginPacket("hero", "secret")));
        Pump(nm, login, ServerSelectPacket(0));

        Assert.Equal(RelayAuthId, login.AuthId);
        Assert.Contains((byte)0x8C, Outgoing(login)); // relay packet
    }

    [Fact]
    public void GameServer_RawGameLogin_SendsFeatureAndCharList()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);

        // Login connection first so the account exists for the game re-auth.
        var (_, login) = NewConnection(lf, world, accounts);
        Pump(nm, login, Concat(SeedBytes(), LoginPacket("hero", "secret")));
        Pump(nm, login, ServerSelectPacket(0));

        // Second connection = game server. 0x91 carries the relay authId.
        var (_, game) = NewConnection(lf, world, accounts);
        Pump(nm, game, Concat(SeedBytes(), GameLoginPacket(RelayAuthId, "hero", "secret")));

        Assert.Equal(ConnectType.Game, game.ConnectionType);
        var outgoing = Outgoing(game);
        Assert.Contains((byte)0xB9, outgoing); // feature enable
        Assert.Contains((byte)0xA9, outgoing); // character list
    }

    // 0xEF seed: opcode + seed(4) + major(4) + minor(4) + revision(4) + patch(4).
    // The classic client sends this on the LOGIN connection only; the game
    // connection gets the bare 4-byte seed with no version in it.
    private static byte[] SeedPacketWithVersion(uint major, uint minor, uint rev, uint patch)
    {
        var b = new byte[21];
        b[0] = 0xEF;
        PutU32(b, 1, Seed);
        PutU32(b, 5, major);
        PutU32(b, 9, minor);
        PutU32(b, 13, rev);
        PutU32(b, 17, patch);
        return b;
    }

    /// <summary>Source-X carries the login connection's detected client version
    /// to the game connection through account tags (CClientLog.cpp:916, "pass
    /// detected client version to the game server"). Without that handoff the
    /// game socket decides every version-dependent format with no version:
    /// 0xB9 would go out with a 2-byte flag field while a 6.0.14.2+ client's
    /// packet table expects 4, and the client would read two bytes of the next
    /// packet as part of it.</summary>
    [Fact]
    public void GameLogin_AdoptsLoginConnectionVersion_AndSendsWideFeaturePacket()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);

        var (_, login) = NewConnection(lf, world, accounts);
        Pump(nm, login, SeedPacketWithVersion(7, 0, 20, 0));
        Pump(nm, login, LoginPacket("hero", "secret"));
        Assert.Equal(70_020_000u, login.ClientVersionNumber);
        Pump(nm, login, ServerSelectPacket(0));

        // Game connection: bare seed, no version of its own.
        var (_, game) = NewConnection(lf, world, accounts);
        Pump(nm, game, Concat(SeedBytes(), GameLoginPacket(RelayAuthId, "hero", "secret")));

        Assert.Equal(70_020_000u, game.ClientVersionNumber);
        Assert.True(game.SupportsBuffIcon);

        var feature = Assert.Single(TestHarness.GetQueuedPackets(game),
            p => p.Span.Length > 0 && p.Span[0] == 0xB9);
        Assert.Equal(5, feature.Span.Length); // 4-byte flag field, not 2
    }

    [Fact]
    public void CharSelect_AfterGameLogin_EntersWorld()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);

        var (_, login) = NewConnection(lf, world, accounts);
        Pump(nm, login, Concat(SeedBytes(), LoginPacket("hero", "secret")));
        Pump(nm, login, ServerSelectPacket(0));

        var (gameClient, game) = NewConnection(lf, world, accounts);
        Pump(nm, game, Concat(SeedBytes(), GameLoginPacket(RelayAuthId, "hero", "secret")));

        // Select character slot 0; account has none yet, so the engine creates one
        // and drives EnterWorld (login confirm 0x1B + start packets).
        Pump(nm, game, CharSelectPacket(0, "Hero"));

        Assert.NotNull(gameClient.Character);
        Assert.True(gameClient.Character!.IsOnline);
        Assert.Contains((byte)0x1B, Outgoing(game)); // login confirm / enter world
    }
}
