using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The view delta keeps two halves per client: KnownItems (what the client has
/// been told about) and LastKnownItemState (what it was last told). A uid in the
/// first without an entry in the second used to be treated as "already up to
/// date": the updated-items pass recorded the current state and sent nothing, so
/// the object stayed invisible for the rest of the session and only came back
/// once a resync cleared KnownItems — walk to another sector and return.
/// </summary>
public sealed class ViewDeltaKnownWithoutStateTests
{
    private static GameClient MakeClient(ILoggerFactory lf, GameWorld world,
        out NetState state, Point3D pos)
    {
        state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(20_000, 30_000));
        state.ClientVersionNumber = 70_020_000;
        var client = new GameClient(state, world, new AccountManager(lf),
            lf.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, pos);
        TestHarness.AttachCharacter(client, player);
        return client;
    }

    /// <summary>Counts world-item draws for a uid. A Stygian Abyss capable
    /// client (anything 7.0+) receives 0xF3 rather than the classic 0x1A, so
    /// both layouts are matched: 0x1A puts the serial at offset 3 after the
    /// length word, 0xF3 at offset 4 after the fixed 0x0001 and datatype byte.
    /// </summary>
    private static int CountWorldItemPackets(NetState state, uint uid)
    {
        int n = 0;
        foreach (var p in TestHarness.GetQueuedPackets(state))
        {
            var span = p.Span;
            int off = span.Length > 0 && span[0] switch { 0x1A => true, _ => false } ? 3
                    : span.Length > 0 && span[0] == 0xF3 ? 4
                    : -1;
            if (off < 0 || span.Length < off + 4) continue;
            uint serial = ((uint)span[off] << 24) | ((uint)span[off + 1] << 16) |
                          ((uint)span[off + 2] << 8) | span[off + 3];
            if ((serial & 0x7FFFFFFF) == uid) n++;
        }
        return n;
    }

    [Fact]
    public void ItemKnownWithoutRecordedState_IsResentInsteadOfSwallowed()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = MakeClient(lf, world, out var state, new Point3D(100, 100, 0, 0));

        var door = world.CreateItem();
        door.BaseId = 0x0675; // a door
        world.PlaceItem(door, new Point3D(101, 100, 0, 0));
        uint uid = door.Uid.Value;

        // Reproduce the broken half-state: the client "knows" the item but the
        // server never recorded what it was shown.
        client.View.KnownItems.Add(uid);
        client.View.LastKnownItemState.Remove(uid);

        int before = CountWorldItemPackets(state, uid);
        client.ViewNeedsRefresh = true;
        client.UpdateClientView();

        Assert.Contains(uid, client.View.KnownItems);
        Assert.True(client.View.LastKnownItemState.ContainsKey(uid));
        Assert.True(CountWorldItemPackets(state, uid) > before,
            "the item must be re-sent when its recorded state is missing");
    }

    /// <summary>Once the state is recorded and nothing changes, the item must not
    /// be re-sent every tick — the fix must not turn into a per-tick resend.</summary>
    [Fact]
    public void UnchangedKnownItem_IsNotResentOnTheNextTick()
    {
        using var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = MakeClient(lf, world, out var state, new Point3D(100, 100, 0, 0));

        var door = world.CreateItem();
        door.BaseId = 0x0675;
        world.PlaceItem(door, new Point3D(101, 100, 0, 0));
        uint uid = door.Uid.Value;

        client.ViewNeedsRefresh = true;
        client.UpdateClientView();   // first pass sends it as a new item
        int afterFirst = CountWorldItemPackets(state, uid);
        Assert.True(afterFirst > 0);

        client.ViewNeedsRefresh = true;
        client.UpdateClientView();   // nothing changed

        Assert.Equal(afterFirst, CountWorldItemPackets(state, uid));
    }
}
