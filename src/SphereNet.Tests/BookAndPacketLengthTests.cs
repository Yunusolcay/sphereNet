using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 05 — outbound packet-length overflow + poisoned-book chain (T1) and the
/// BOOK_PAGES=65535 open-loop hang (E1).
///
/// The variable-length wire size is a ushort; a body over 65535 bytes wrapped the
/// length field (65536 -> 0) and desynced/froze the client. A writable book was
/// the player-reachable trigger: an unbounded 0x66 line was stored and later
/// re-emitted in one oversized 0x66. These verify: WriteLengthAt refuses to wrap
/// and flags oversize; the send path drops oversize packets (and returns the
/// buffer to the pool); incoming book lines are byte-capped; OpenBook clamps the
/// page count and cannot loop forever; and a large book is split into multiple
/// in-spec 0x66 packets instead of one overflowing one.
/// </summary>
public sealed class BookAndPacketLengthTests
{
    // ---- PacketBuffer wire-length guard ----

    private static PacketBuffer BuildPacket(byte opcode, int totalLen)
    {
        var buf = new PacketBuffer(totalLen + 16);
        buf.WriteByte(opcode);
        buf.WriteUInt16(0); // length placeholder
        buf.WriteBytes(new byte[totalLen - 3]);
        buf.WriteLengthAt(1);
        return buf;
    }

    [Fact]
    public void WriteLengthAt_ExactMax_WritesLength_NotOversize()
    {
        var buf = BuildPacket(0x66, PacketBuffer.MaxWireLength); // 65535
        Assert.False(buf.IsOversize);
        Assert.Equal(65535, buf.Length);
        Assert.Equal(0xFF, buf.Data[1]);
        Assert.Equal(0xFF, buf.Data[2]); // length field == 65535, not wrapped
        buf.ReturnToPool();
    }

    [Fact]
    public void WriteLengthAt_OverMax_FlagsOversize_NoWrappedLength()
    {
        var buf = BuildPacket(0x66, PacketBuffer.MaxWireLength + 1); // 65536
        Assert.True(buf.IsOversize);
        Assert.Equal(65536, buf.Length);
        buf.ReturnToPool();
    }

    [Fact]
    public void Send_OversizePacket_IsDropped_AndReturnedToPool()
    {
        var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 1);

        var oversize = BuildPacket(0x66, PacketBuffer.MaxWireLength + 1);
        Assert.True(oversize.IsOversize);
        state.Send(oversize);

        Assert.Empty(TestHarness.GetQueuedPackets(state)); // dropped, never queued
        Assert.Empty(oversize.Data);                       // ReturnToPool ran (no leak)

        // A valid packet on the same connection still queues normally.
        var ok = BuildPacket(0x66, 128);
        Assert.False(ok.IsOversize);
        state.Send(ok);
        Assert.Single(TestHarness.GetQueuedPackets(state));
    }

    // ---- Incoming 0x66 per-line byte cap ----

    [Fact]
    public void IncomingBookLine_WithoutNull_IsCappedBeforeStorage()
    {
        var (world, _, accounts, lf) = Env();
        var state = TestHarness.CreateActiveNetState(lf, 1);
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var ch = world.CreateCharacter();
        TestHarness.AttachCharacter(client, ch);
        state.BookPageHandler = (_, serial, pages) => client.HandleBookPage(serial, pages);

        // The write path now needs a real book the reader can see and reach
        // (receive.cpp:1002/1017), so the fixture places both.
        var book = world.CreateItem();
        book.ItemType = SphereNet.Core.Enums.ItemType.Book;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        world.PlaceItem(book, new Point3D(100, 100, 0, 0));

        // 0x66 write: 1 page, 1 line of 1000 'A' with a single trailing NUL.
        var line = new byte[1000];
        for (int i = 0; i < line.Length; i++) line[i] = (byte)'A';
        var wire = new List<byte>();
        wire.AddRange(U32(book.Uid.Value));
        wire.AddRange(U16(1));               // page count
        wire.AddRange(U16(1));               // page num
        wire.AddRange(U16(1));               // line count
        wire.AddRange(line);
        wire.Add(0);                          // NUL

        new PacketBookPage().OnReceive(new PacketBuffer(wire.ToArray()), state);

        Assert.True(book.TryGetTag("PAGE_1", out string? stored));
        Assert.Equal(256, stored!.Length); // capped at MaxLineChars, not 1000
    }

    // ---- OpenBook: BOOK_PAGES clamp (E1) + chunked send (T1) ----

    [Fact]
    public void OpenBook_BookPages65535_DoesNotHang_AndClampsPageCount()
    {
        var (world, state, accounts, lf) = Env();
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var ch = world.CreateCharacter();
        TestHarness.AttachCharacter(client, ch);

        var book = world.CreateItem();
        book.SetTag("BOOK_PAGES", "65535"); // the old ushort loop wrapped to 0 and spun forever

        var ex = Record.Exception(() => client.OpenBook(book, false));
        Assert.Null(ex);

        var header = TestHarness.GetQueuedPackets(state).First(p => p.Span[0] == 0x93);
        // 0x93 is fixed 99 bytes with no length field: uid(1..4), writable, writable,
        // pages(7..8), title(60), author(30) — send.cpp:2925.
        Assert.Equal(99, header.Span.Length);
        int pageCount = (header.Span[7] << 8) | header.Span[8];
        Assert.Equal(256, pageCount); // clamped by the anti-hang cap
    }

    [Fact]
    public void OpenBook_LargeBook_SplitsIntoMultipleInSpecPackets()
    {
        var (world, state, accounts, lf) = Env();
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var ch = world.CreateCharacter();
        TestHarness.AttachCharacter(client, ch);

        var book = world.CreateItem();
        book.SetTag("BOOK_PAGES", "200");
        string longLine = new('A', 300);
        for (int i = 1; i <= 200; i++)
            book.SetTag($"PAGE_{i}", longLine); // 200 * ~305 bytes > one packet's budget

        client.OpenBook(book, false);

        var bookPages = TestHarness.GetQueuedPackets(state).Where(p => p.Span[0] == 0x66).ToList();
        Assert.True(bookPages.Count >= 2, "large book should split across multiple 0x66 packets");
        Assert.All(bookPages, p => Assert.True(p.Span.Length <= PacketBuffer.MaxWireLength));
    }

    [Fact]
    public void OpenBook_HugeBook_AllPacketsInSpec_AndEveryPageDelivered()
    {
        var (world, state, accounts, lf) = Env();
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var ch = world.CreateCharacter();
        TestHarness.AttachCharacter(client, ch);

        // A book whose total content (~1 MB) far exceeds the 65535 wire ceiling.
        // Every emitted 0x66 must stay in spec (never oversize/dropped) and no
        // page may be lost — the reader gets the whole book across many packets.
        var book = world.CreateItem();
        book.SetTag("BOOK_PAGES", "256");
        string line = new('A', 4000);
        for (int i = 1; i <= 256; i++)
            book.SetTag($"PAGE_{i}", line);

        client.OpenBook(book, false);

        var pages = TestHarness.GetQueuedPackets(state).Where(p => p.Span[0] == 0x66).ToList();
        Assert.True(pages.Count >= 2, "a 1 MB book must span multiple packets");
        Assert.All(pages, p => Assert.False(p.IsOversize));
        Assert.All(pages, p => Assert.True(p.Span.Length <= PacketBuffer.MaxWireLength));
        int totalPages = pages.Sum(p => (p.Span[7] << 8) | p.Span[8]);
        Assert.Equal(256, totalPages); // every page delivered, none dropped
    }

    // ---- helpers ----

    private static (GameWorld world, NetState state, AccountManager accounts, ILoggerFactory lf) Env()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var accounts = new AccountManager(lf) { AutoCreateAccounts = true };
        var state = TestHarness.CreateActiveNetState(lf, 1);
        return (world, state, accounts, lf);
    }

    private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] U16(ushort v) => [(byte)(v >> 8), (byte)v];
}
