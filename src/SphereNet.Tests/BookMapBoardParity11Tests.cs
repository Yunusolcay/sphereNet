using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Books, maps and bulletin boards: the wire shape of the book window, and who is
/// allowed to edit what from how far away.
///
/// Source-X sends 0x93 as a fixed 99 bytes with 60- and 30-byte string fields
/// (PacketDisplayBook, send.cpp:2871) and offers a writable book MAX_BOOK_PAGES
/// (:2919, sphereproto.h:767). Reading a page needs CanSee (receive.cpp:1002),
/// writing one additionally needs a real CItemMessage that IsBookWritable (:1017),
/// and retitling needs CanTouch (CClientEvent.cpp:165). A book's TITLE is its name
/// (CItemMessage.cpp:73/112) and its BODY is a zero-based page list (:53,
/// send.cpp:1709). A map edit needs a CItemMap within reach and refuses a non-GM
/// when the pins are glued (receive.cpp:867/875), and opening a map sets the
/// server's plot mode from what it sends (CClientMsg.cpp:2542). Every bulletin
/// board sub-command is gated on seeing the board (receive.cpp:1193) and a posted
/// message is ATTR_MOVE_NEVER (:1252). A classic save writes one bare PIN= line per
/// pin (CItemMap.cpp:47/100).
/// </summary>
public sealed class BookMapBoardParity11Tests
{
    private sealed record Bench(GameWorld World, NetState State, GameClient Client, Character Me);

    private static Bench Setup(PrivLevel priv = PrivLevel.Player)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var state = TestHarness.CreateActiveNetState(lf, 1);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.PrivLevel = priv;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return new Bench(world, state, client, me);
    }

    private static Item Place(Bench b, ItemType type, short x, short y)
    {
        var item = b.World.CreateItem();
        item.ItemType = type;
        b.World.PlaceItem(item, new Point3D(x, y, 0, 0));
        return item;
    }

    /// <summary>GetQueuedPackets hands back a SNAPSHOT, so clear the real priority
    /// queues behind it.</summary>
    private static void Drain(Bench b)
    {
        var queues = (Queue<PacketBuffer>[])typeof(NetState)
            .GetField("_queues", System.Reflection.BindingFlags.Instance |
                                 System.Reflection.BindingFlags.NonPublic)!
            .GetValue(b.State)!;
        foreach (var q in queues) q.Clear();
    }

    private static List<PacketBuffer> Sent(Bench b, byte opcode) =>
        TestHarness.GetQueuedPackets(b.State).Where(p => p.Span[0] == opcode).ToList();

    private static List<(ushort, string[])> Write(ushort page, string text) =>
        [(page, [text])];

    private static List<(ushort, string[])> Read(ushort page) => [(page, [])];

    // ================================================================ 11A-1

    [Fact]
    public void TheBookWindowIsAFixedNinetyNineBytes()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.SetTag("BOOK_TITLE", "A Title");
        book.SetTag("BOOK_AUTHOR", "A Scribe");

        b.Client.OpenBook(book, writable: true);

        var header = Assert.Single(Sent(b, 0x93));
        Assert.Equal(99, header.Span.Length);
        // uid at 1..4, unshifted by any length field.
        var span = header.Span;
        uint uid = (uint)((span[1] << 24) | (span[2] << 16) | (span[3] << 8) | span[4]);
        Assert.Equal(book.Uid.Value, uid);
        Assert.Equal(1, span[5]);   // writable
        Assert.Equal(1, span[6]);   // written twice upstream
        // Fixed-width fields: title at 9, author at 69.
        Assert.Equal("A Title", ReadFixed(span, 9, 60));
        Assert.Equal("A Scribe", ReadFixed(span, 69, 30));
    }

    private static string ReadFixed(System.ReadOnlySpan<byte> span, int offset, int length)
    {
        var chars = new List<char>();
        for (int i = 0; i < length; i++)
        {
            byte c = span[offset + i];
            if (c == 0) break;
            chars.Add((char)c);
        }
        return new string(chars.ToArray());
    }

    [Fact]
    public void AnOverlongTitleStaysInsideItsField()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.SetTag("BOOK_TITLE", new string('T', 200));
        book.SetTag("BOOK_AUTHOR", new string('A', 200));

        b.Client.OpenBook(book, writable: true);

        var header = Assert.Single(Sent(b, 0x93));
        Assert.Equal(99, header.Span.Length);
        Assert.Equal(59, ReadFixed(header.Span, 9, 60).Length);   // NUL-terminated inside 60
        Assert.Equal(29, ReadFixed(header.Span, 69, 30).Length);
    }

    // ================================================================ 11B-5

    [Fact]
    public void AWritableBookOffersTheWholeWritingCapacity()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);

        b.Client.OpenBook(book, writable: true);

        var header = Assert.Single(Sent(b, 0x93));
        Assert.Equal(64, (header.Span[7] << 8) | header.Span[8]);
    }

    [Fact]
    public void APageWrittenBeyondSixteenIsOfferedWhenTheBookIsOpened()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        b.Client.HandleBookPage(book.Uid.Value, Write(17, "page seventeen"));
        Assert.True(book.TryGetTag("PAGE_17", out _));
        Drain(b);

        b.Client.OpenBook(book, writable: true);

        int delivered = Sent(b, 0x66).Sum(p => (p.Span[7] << 8) | p.Span[8]);
        Assert.Equal(64, delivered);
    }

    [Fact]
    public void AFinishedBookOffersExactlyThePagesItHas()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.SetTag("PAGE_1", "one");
        book.SetTag("PAGE_2", "two");

        b.Client.OpenBook(book, writable: false);

        var header = Assert.Single(Sent(b, 0x93));
        Assert.Equal(2, (header.Span[7] << 8) | header.Span[8]);
    }

    [Fact]
    public void AWriteBeyondTheHardCeilingIsRefused()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);

        b.Client.HandleBookPage(book.Uid.Value, Write(65, "past the end"));

        Assert.False(book.TryGetTag("PAGE_65", out _));
    }

    // ================================================================ 11A-2

    [Fact]
    public void ABookCarriedOutOfSightCanNoLongerBeWritten()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 500, 500);

        b.Client.HandleBookPage(book.Uid.Value, Write(1, "from afar"));

        Assert.False(book.TryGetTag("PAGE_1", out _));
    }

    [Fact]
    public void APileOfGoldIsNotABook()
    {
        var b = Setup();
        var gold = Place(b, ItemType.Gold, 100, 100);

        b.Client.HandleBookPage(gold.Uid.Value, Write(1, "not a page"));
        b.Client.HandleBookHeader(gold.Uid.Value, true, "not a title", "nobody");

        Assert.False(gold.TryGetTag("PAGE_1", out _));
        Assert.False(gold.TryGetTag("BOOK_TITLE", out _));
    }

    [Fact]
    public void ANearbyBookIsStillWritable()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 101, 100);

        b.Client.HandleBookPage(book.Uid.Value, Write(1, "close enough"));

        Assert.True(book.TryGetTag("PAGE_1", out string? text));
        Assert.Equal("close enough", text);
    }

    [Fact]
    public void RetitlingABookOutOfReachIsRefused()
    {
        var b = Setup();
        // Visible but not touchable: sight reaches much further than the arm does.
        var book = Place(b, ItemType.Book, 110, 100);

        b.Client.HandleBookHeader(book.Uid.Value, true, "remote title", "ghost");

        Assert.False(book.TryGetTag("BOOK_TITLE", out _));
    }

    // ================================================================ 11A-6

    [Fact]
    public void ABooksTitleIsItsName()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.Name = "old name";

        Assert.True(book.TrySetProperty("TITLE", "new title"));

        Assert.Equal("new title", book.Name);
        Assert.True(book.TryGetProperty("TITLE", out string? title));
        Assert.Equal("new title", title);
    }

    [Fact]
    public void TheClientsTitleChangeRenamesTheBookToo()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.Name = "old name";

        b.Client.HandleBookHeader(book.Uid.Value, true, "client title", "a scribe");

        Assert.Equal("client title", book.Name);
    }

    // ================================================================ 11A-4

    [Fact]
    public void BodyStartsAtTheFirstPageTheReaderSees()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);

        book.TrySetProperty("BODY.0", "first");
        book.TrySetProperty("BODY.1", "second");

        Assert.True(book.TryGetTag("PAGE_1", out string? p1));
        Assert.Equal("first", p1);
        Assert.True(book.TryGetTag("PAGE_2", out string? p2));
        Assert.Equal("second", p2);
        Assert.False(book.TryGetTag("PAGE_0", out _));
    }

    [Fact]
    public void ReadingBodyBackGivesTheSamePage()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.TrySetProperty("BODY.0", "first");

        Assert.True(book.TryGetProperty("BODY.0", out string? body));
        Assert.Equal("first", body);
        Assert.True(book.TryGetProperty("PAGES", out string? pages));
        Assert.Equal("1", pages);
    }

    [Fact]
    public void APageWrittenByTheOldZeroBasedAppendIsLiftedIntoPlace()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        // What a save written before the indexes agreed looks like.
        book.SetTag("PAGE_0", "orphaned first page");
        book.SetTag("PAGE_1", "second page");

        Assert.True(book.TryGetProperty("BODY.0", out string? body));

        Assert.Equal("orphaned first page", body);
        Assert.True(book.TryGetTag("PAGE_2", out string? moved));
        Assert.Equal("second page", moved);
        Assert.False(book.TryGetTag("PAGE_0", out _));
    }

    // ================================================================ 11A-5

    [Fact]
    public void AClassicSavesRepeatedPinLinesLand()
    {
        var b = Setup();
        var map = Place(b, ItemType.Map, 100, 100);

        Assert.True(map.TrySetProperty("PIN", "10,20"));
        Assert.True(map.TrySetProperty("PIN", "30,40"));

        Assert.True(map.TryGetProperty("PINS", out string? count));
        Assert.Equal("2", count);
        Assert.True(map.TryGetProperty("PIN.1", out string? first));
        Assert.Equal("10,20", first);
        Assert.True(map.TryGetProperty("PIN.2", out string? second));
        Assert.Equal("30,40", second);
    }

    // ================================================================ 11A-3

    [Fact]
    public void ADistantMapCannotBePinned()
    {
        var b = Setup();
        var map = Place(b, ItemType.Map, 400, 400);

        b.Client.HandleMapPinEdit(map.Uid.Value, 1, 0, 55, 66);

        Assert.Equal("", map.Tags.Get("PIN_1") ?? "");
    }

    [Fact]
    public void PinsAreNotWrittenOntoWhateverElseTheSerialNames()
    {
        var b = Setup();
        var gold = Place(b, ItemType.Gold, 100, 100);

        b.Client.HandleMapPinEdit(gold.Uid.Value, 1, 0, 55, 66);

        Assert.Null(gold.Tags.Get("PIN_1"));
    }

    [Fact]
    public void ANearbyMapStillTakesPins()
    {
        var b = Setup();
        var map = Place(b, ItemType.Map, 101, 100);

        b.Client.HandleMapPinEdit(map.Uid.Value, 1, 0, 55, 66);

        Assert.Equal("55,66", map.Tags.Get("PIN_1"));
    }

    // ================================================================ 11B-3

    [Fact]
    public void GluedPinsRefuseAPlayer()
    {
        var b = Setup();
        var map = Place(b, ItemType.Map, 100, 100);
        map.SetTag("PIN_1", "1,2");
        map.MoreP = new Point3D(0, 0, 1, 0); // MOREZ = pins glued

        b.Client.HandleMapPinEdit(map.Uid.Value, 1, 0, 55, 66);   // add
        b.Client.HandleMapPinEdit(map.Uid.Value, 5, 0, 0, 0);     // clear all

        Assert.Null(map.Tags.Get("PIN_2"));
        Assert.Equal("1,2", map.Tags.Get("PIN_1"));
    }

    [Fact]
    public void GluedPinsStillYieldToStaff()
    {
        var b = Setup(PrivLevel.GM);
        var map = Place(b, ItemType.Map, 100, 100);
        map.MoreP = new Point3D(0, 0, 1, 0);

        b.Client.HandleMapPinEdit(map.Uid.Value, 1, 0, 55, 66);

        Assert.Equal("55,66", map.Tags.Get("PIN_1"));
    }

    // ================================================================ 11B-4

    [Fact]
    public void OpeningAMapPutsTheServerBackInReadingMode()
    {
        var b = Setup();
        var map = Place(b, ItemType.Map, 100, 100);
        map.More1 = (90u << 16) | 90u;   // left, top
        map.More2 = (110u << 16) | 110u; // right, bottom
        map.SetTag("PLOTMODE", "1");

        b.Client.ItemUse.OpenMapGump(map);

        Assert.Null(map.Tags.Get("PLOTMODE"));

        // and the first toggle after that turns editing ON, not off again.
        Drain(b);
        b.Client.HandleMapPinEdit(map.Uid.Value, 6, 0, 0, 0);
        var reply = Assert.Single(Sent(b, 0x56));
        Assert.Equal(7, reply.Span[5]);          // MAP_SENT
        Assert.Equal(1, reply.Span[6]);          // editing on
    }

    // ================================================================ 11B-1

    private static (Item Board, Item Msg) BoardWithMessage(Bench b, short x, short y)
    {
        var board = Place(b, ItemType.BBoard, x, y);
        b.Client.HandleBulletinBoardPost(board.Uid.Value, 0, "a notice", ["a line"]);
        return (board, board.Contents[0]);
    }

    [Fact]
    public void ABoardOutOfSightAnswersNothing()
    {
        var b = Setup();
        var (board, msg) = BoardWithMessage(b, 101, 100);
        b.World.MoveCharacter(b.Me, new Point3D(500, 500, 0, 0));
        Drain(b);

        b.Client.HandleBulletinBoardRequestHead(board.Uid.Value, msg.Uid.Value);
        b.Client.HandleBulletinBoardRequestMessage(board.Uid.Value, msg.Uid.Value);
        b.Client.HandleBulletinBoardDelete(board.Uid.Value, msg.Uid.Value);

        Assert.Empty(Sent(b, 0x71));
        Assert.False(msg.IsDeleted);
    }

    [Fact]
    public void ABoardInFrontOfYouStillAnswers()
    {
        var b = Setup();
        var (board, msg) = BoardWithMessage(b, 101, 100);
        Drain(b);

        b.Client.HandleBulletinBoardRequestHead(board.Uid.Value, msg.Uid.Value);
        b.Client.HandleBulletinBoardRequestMessage(board.Uid.Value, msg.Uid.Value);

        Assert.Equal(2, Sent(b, 0x71).Count);
    }

    // ================================================================ 11B-2

    [Fact]
    public void APostedNoticeIsFixedInPlace()
    {
        var b = Setup();
        var (_, msg) = BoardWithMessage(b, 101, 100);

        Assert.True(msg.IsAttr(ObjAttributes.Move_Never));
    }
}
