using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H10 (wiki/hedef.txt long tail):
//   * bulletin boards end to end (Source-X CClient::addBulletinBoard /
//     CItemMessage): dclick opens the board (0x71 sub 0 + contents), post
//     creates a message item, header/body requests answer with 0x71
//     sub 1/2, delete is author-or-GM gated
public class ParityWaveH10Tests
{
    private static (GameWorld world, SphereNet.Game.Clients.GameClient client, Character player, Item board)
        CreateBoardEnv(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var board = world.CreateItem();
        board.BaseId = 0x1E5E;
        board.ItemType = ItemType.BBoard;
        board.Name = "town board";
        world.PlaceItem(board, new Point3D(101, 100, 0, 0));
        return (world, client, player, board);
    }

    [Fact]
    public void PostAndRead_RoundTripsThroughBoardPackets()
    {
        var (world, client, player, board) = CreateBoardEnv(2101);
        player.Name = "Scribe";

        client.HandleBulletinBoardPost(board.Uid.Value, 0, "Hello town",
            new[] { "first line", "second line" });

        // The post became a message item inside the board.
        var msg = Assert.Single(board.Contents);
        Assert.Equal("Hello town", msg.Name);
        Assert.Equal("Scribe", msg.Tags.Get("AUTHOR"));
        Assert.Equal(player.Uid, msg.Link);
        // A board message and a book share ONE page list upstream, so the body lives
        // in the page storage and reads back through the script BODY surface
        // (CItemMessage.cpp:53, send.cpp:2222).
        Assert.Equal("first line", msg.Tags.Get("PAGE_1"));
        Assert.True(msg.TryGetProperty("BODY.0", out string? firstLine));
        Assert.Equal("first line", firstLine);

        // Header request answers with 0x71 sub 1, body request with sub 2.
        client.HandleBulletinBoardRequestHead(board.Uid.Value, msg.Uid.Value);
        client.HandleBulletinBoardRequestMessage(board.Uid.Value, msg.Uid.Value);
        var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
        Assert.Contains(packets, p => p.Span.Length > 3 && p.Span[0] == 0x71 && p.Span[3] == 1);
        Assert.Contains(packets, p => p.Span.Length > 3 && p.Span[0] == 0x71 && p.Span[3] == 2);
    }

    [Fact]
    public void BoardDclick_SendsBoardNameAndContents()
    {
        var (world, client, player, board) = CreateBoardEnv(2102);
        client.HandleBulletinBoardPost(board.Uid.Value, 0, "Notice", new[] { "body" });

        client.HandleDoubleClick(board.Uid.Value);

        var packets = TestHarness.GetQueuedPackets(client.NetState).ToList();
        // sub 0 = board display with its serial
        uint uid = board.Uid.Value;
        Assert.Contains(packets, p => p.Span.Length > 7 && p.Span[0] == 0x71 && p.Span[3] == 0 &&
            ((uint)p.Span[4] << 24 | (uint)p.Span[5] << 16 | (uint)p.Span[6] << 8 | p.Span[7]) == uid);
        // the message item travels as container content (0x3C or 0x25)
        Assert.Contains(packets, p => p.Span.Length > 0 && (p.Span[0] == 0x3C || p.Span[0] == 0x25));
    }

    [Fact]
    public void Delete_IsAuthorOrGmGated()
    {
        var (world, client, player, board) = CreateBoardEnv(2103);
        client.HandleBulletinBoardPost(board.Uid.Value, 0, "Mine", new[] { "body" });
        var msg = Assert.Single(board.Contents);

        // A stranger's client can't delete it.
        var lf = LoggerFactory.Create(_ => { });
        var strangerClient = TestHarness.CreateClient(lf, world, new AccountManager(lf), 2104);
        var stranger = world.CreateCharacter();
        stranger.IsPlayer = true;
        world.PlaceCharacter(stranger, new Point3D(102, 100, 0, 0));
        TestHarness.AttachCharacter(strangerClient, stranger);
        strangerClient.HandleBulletinBoardDelete(board.Uid.Value, msg.Uid.Value);
        Assert.Single(board.Contents);

        // The author can.
        client.HandleBulletinBoardDelete(board.Uid.Value, msg.Uid.Value);
        Assert.Empty(board.Contents);
        Assert.True(msg.IsDeleted);
    }

    [Fact]
    public void Post_RollsOffOldestPastTheCap()
    {
        var (world, client, player, board) = CreateBoardEnv(2105);

        for (int i = 0; i < 34; i++)
            client.HandleBulletinBoardPost(board.Uid.Value, 0, $"msg {i}", new[] { "b" });

        Assert.Equal(32, board.Contents.Count);
        Assert.Equal("msg 33", board.Contents[^1].Name);   // newest kept
        Assert.DoesNotContain(board.Contents, m => m.Name == "msg 0"); // oldest rolled off
    }
}
