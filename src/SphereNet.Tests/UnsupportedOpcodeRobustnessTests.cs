using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Network.Manager;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// Verifies that modern/unsupported opcodes with no registered handler are
/// ignored safely: the server neither crashes nor desyncs the packet stream,
/// and logging is rate-limited to one entry per opcode per connection.
/// </summary>
public class UnsupportedOpcodeRobustnessTests
{
    // Opcodes that are framed (fixed or length-prefixed) but have no incoming
    // handler. These must be consumed using their declared framing, not crash.
    // (0xFA gained a real handler — Ultima Store button — so it is no longer
    // part of this set.)
    private static readonly byte[] FixedUnsupported = { 0xFB, 0xF1 }; // len 2, 9

    private static (NetworkManager Mgr, NetState State) CreatePlaintextConnection(int id = 1)
    {
        var mgr = new NetworkManager(2, NullLoggerFactory.Instance);
        var state = mgr.GetState(id - 1)!;
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        state.Id = id;
        state.IsSeeded = true;
        ForceNoCrypto(state);
        return (mgr, state);
    }

    private static void ForceNoCrypto(NetState state)
    {
        var crypto = state.Crypto;
        var t = crypto.GetType();
        t.GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, true);
        t.GetField("_encType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, EncryptionType.None);
    }

    private static void Process(NetworkManager mgr, NetState state)
    {
        typeof(NetworkManager)
            .GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mgr, [state]);
    }

    private static byte[] VarPacket(byte opcode, int payloadLen)
    {
        int total = 3 + payloadLen;
        var p = new byte[total];
        p[0] = opcode;
        p[1] = (byte)(total >> 8);
        p[2] = (byte)(total & 0xFF);
        return p;
    }

    [Fact]
    public void FixedUnsupportedOpcodes_AreConsumedInOrder_WithoutDesync()
    {
        var (mgr, state) = CreatePlaintextConnection();
        var seen = new List<(byte Op, int Len)>();
        mgr.OnUnknownPacket += (_, op, bytes) => seen.Add((op, bytes.Length));

        // 0x33 (2) + 0xF1 (9), back to back. Both are fixed-length opcodes with
        // no registered handler, so the framer must still consume exactly their
        // declared size and hand the next packet over intact.
        var buffer = new byte[2 + 9];
        buffer[0] = 0x33;
        // buffer[1] is 0x33 payload
        buffer[2] = 0xF1;
        state.InjectReceived(buffer);

        Process(mgr, state);

        Assert.False(state.IsClosing);
        Assert.Equal(0, state.ReceivedData.Length); // fully consumed, no leftover
        Assert.Equal(new[] { ((byte)0x33, 2), ((byte)0xF1, 9) }, seen);
    }

    [Fact]
    public void VariableUnsupportedOpcodes_RespectLengthField_WithoutDesync()
    {
        var (mgr, state) = CreatePlaintextConnection();
        var seen = new List<(byte Op, int Len)>();
        mgr.OnUnknownPacket += (_, op, bytes) => seen.Add((op, bytes.Length));

        // 0xF2 (len 5), 0xD0 (len 8), 0xDD (len 4), 0xE0 (len 12), 0xF7 (len 6), 0xF9 (len 3)
        var p1 = VarPacket(0xF2, 2);
        var p2 = VarPacket(0xD0, 5);
        var p3 = VarPacket(0xDD, 1);
        var p4 = VarPacket(0xE0, 9);
        var p5 = VarPacket(0xF7, 3);
        var p6 = VarPacket(0xF9, 0);
        var buffer = new[] { p1, p2, p3, p4, p5, p6 }.SelectMany(x => x).ToArray();
        state.InjectReceived(buffer);

        Process(mgr, state);

        Assert.False(state.IsClosing);
        Assert.Equal(0, state.ReceivedData.Length);
        Assert.Equal(new[]
        {
            ((byte)0xF2, 5), ((byte)0xD0, 8), ((byte)0xDD, 4),
            ((byte)0xE0, 12), ((byte)0xF7, 6), ((byte)0xF9, 3)
        }, seen);
    }

    [Fact]
    public void UnsupportedOpcode_FollowedByValidPacket_DoesNotDropConnection()
    {
        var (mgr, state) = CreatePlaintextConnection();
        mgr.OnUnknownPacket += (_, _, _) => { };

        // An unsupported variable packet, then a flood of the same fixed opcode.
        var prefix = VarPacket(0xF7, 4);
        var rest = new byte[1 + 2 + 9]; // 0xFA, 0xFB, 0xF1 again
        rest[0] = 0xFA;
        rest[1] = 0xFB;
        rest[3] = 0xF1;
        state.InjectReceived(prefix.Concat(rest).ToArray());

        Process(mgr, state);

        Assert.False(state.IsClosing);
        Assert.Equal(0, state.ReceivedData.Length);
    }

    [Fact]
    public void ShouldLogUnknownOpcode_IsTrueOnceThenSuppressedPerConnection()
    {
        var state = new NetState(NullLogger<NetState>.Instance);
        var m = typeof(NetState).GetMethod("ShouldLogUnknownOpcode",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool Call(byte op) => (bool)m.Invoke(state, [op])!;

        Assert.True(Call(0xF7));  // first time -> log
        Assert.False(Call(0xF7)); // repeat -> suppress
        Assert.True(Call(0xD0));  // distinct opcode -> log
        Assert.False(Call(0xD0)); // repeat -> suppress
    }
}
