using System;
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
using SphereNet.Game.World.Regions;
using SphereNet.Game.World.Sectors;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Content that reaches the client, and the environment it reaches them in.
///
/// A board message and a book share one page list upstream (CItemMessage.cpp:53) and
/// the full-message packet reads it with GetPageText (send.cpp:2222). A client that
/// understands facets gets 0xF5, which carries the map and reads the item's
/// OVERRIDE.MAPWIDTH/MAPHEIGHT, with height before width (send.cpp:5358); older
/// clients keep 0x90 (:2841). A page declaring more lines than upstream tolerates ends
/// the request (receive.cpp:1032). The book window announces the object's own
/// writability (send.cpp:2917).
///
/// Weather codes are WEATHER_TYPE: 255 dry, 0 rain, 1 storm, 2 snow
/// (sphereproto.h:514). Setting a sector's weather or season publishes it to everyone
/// standing there and fires @EnvironChange (CSector.cpp:879/904). A pinned light wins
/// over the clock (:684/818). Precipitation is rolled against the sector's own
/// RAINCHANCE and then COLDCHANCE (:857). Weather belongs to a region's identity, not
/// to its visible name.
/// </summary>
public sealed class ContentEnvironmentParity11C12ATests
{
    // ================================================================ helpers

    private sealed record Bench(GameWorld World, NetState State, GameClient Client, Character Me);

    private static Bench Setup(uint clientVersion = 0)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var state = TestHarness.CreateActiveNetState(lf, 1);
        if (clientVersion != 0) state.ClientVersionNumber = clientVersion;
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var me = world.CreateCharacter();
        me.IsPlayer = true;
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

    private static List<PacketBuffer> Sent(Bench b, byte opcode) =>
        TestHarness.GetQueuedPackets(b.State).Where(p => p.Span[0] == opcode).ToList();

    private static ushort U16(System.ReadOnlySpan<byte> s, int off) => (ushort)((s[off] << 8) | s[off + 1]);

    private static Item MapAt(Bench b, short x, short y)
    {
        var map = Place(b, ItemType.Map, x, y);
        map.More1 = (90u << 16) | 90u;   // left, top
        map.More2 = (110u << 16) | 110u; // right, bottom
        return map;
    }

    // ================================================================ 11C-1

    [Fact]
    public void ABoardMessageOutOfAClassicSaveShowsItsBody()
    {
        var b = Setup();
        var board = Place(b, ItemType.BBoard, 101, 100);
        // What the item loader leaves behind for a classic "BODY.0=..." line: the
        // shared page storage, not the board's own tag names.
        var msg = b.World.CreateItem();
        msg.ItemType = ItemType.Message;
        msg.Name = "an old notice";
        msg.SetTag("AUTHOR", "Scribe");
        msg.SetTag("PAGE_1", "first line");
        board.AddItem(msg);

        b.Client.HandleBulletinBoardRequestMessage(board.Uid.Value, msg.Uid.Value);

        var full = Assert.Single(Sent(b, 0x71), p => p.Span[3] == 2);
        Assert.Contains("first line", System.Text.Encoding.ASCII.GetString(full.Span.ToArray()));
    }

    [Fact]
    public void ANoticePostedInGameReadsBackThroughTheScriptSurface()
    {
        var b = Setup();
        var board = Place(b, ItemType.BBoard, 101, 100);

        b.Client.HandleBulletinBoardPost(board.Uid.Value, 0, "a notice", ["line one", "line two"]);

        var msg = Assert.Single(board.Contents);
        Assert.True(msg.TryGetProperty("BODY.0", out string? first));
        Assert.Equal("line one", first);
        Assert.True(msg.TryGetProperty("BODY.1", out string? second));
        Assert.Equal("line two", second);
    }

    [Fact]
    public void ABoardWrittenByAnOlderSphereNetSaveStillReads()
    {
        var b = Setup();
        var board = Place(b, ItemType.BBoard, 101, 100);
        var msg = b.World.CreateItem();
        msg.Name = "older notice";
        msg.SetTag("BODY_1", "legacy line");
        board.AddItem(msg);

        b.Client.HandleBulletinBoardRequestMessage(board.Uid.Value, msg.Uid.Value);

        var full = Assert.Single(Sent(b, 0x71), p => p.Span[3] == 2);
        Assert.Contains("legacy line", System.Text.Encoding.ASCII.GetString(full.Span.ToArray()));
    }

    // ================================================================ 11C-2

    [Fact]
    public void AModernClientIsToldWhichWorldTheMapShows()
    {
        var b = Setup(clientVersion: 70_013_000);
        var map = MapAt(b, 100, 100);
        map.MoreP = new Point3D(0, 0, 0, 2); // MOREM = facet 2

        b.Client.ItemUse.OpenMapGump(map);

        var pkt = Assert.Single(Sent(b, 0xF5));
        Assert.Equal(21, pkt.Span.Length);
        Assert.Empty(Sent(b, 0x90));
        Assert.Equal(2, U16(pkt.Span, 19)); // the facet, last field
    }

    [Fact]
    public void AnOlderClientKeepsTheOldMapPacket()
    {
        var b = Setup(clientVersion: 60_000_000);
        var map = MapAt(b, 100, 100);

        b.Client.ItemUse.OpenMapGump(map);

        var pkt = Assert.Single(Sent(b, 0x90));
        Assert.Equal(19, pkt.Span.Length);
        Assert.Empty(Sent(b, 0xF5));
    }

    // ================================================================ 11C-3

    [Fact]
    public void ACustomMapWindowKeepsItsSize()
    {
        var b = Setup(clientVersion: 70_013_000);
        var map = MapAt(b, 100, 100);
        map.SetTag("OVERRIDE.MAPWIDTH", "300");
        map.SetTag("OVERRIDE.MAPHEIGHT", "240");

        b.Client.ItemUse.OpenMapGump(map);

        var pkt = Assert.Single(Sent(b, 0xF5));
        // Height comes BEFORE width in this packet.
        Assert.Equal(240, U16(pkt.Span, 15));
        Assert.Equal(300, U16(pkt.Span, 17));
    }

    [Fact]
    public void AMapWithNoOverrideKeepsTheDefaultWindow()
    {
        var b = Setup(clientVersion: 70_013_000);
        var map = MapAt(b, 100, 100);

        b.Client.ItemUse.OpenMapGump(map);

        var pkt = Assert.Single(Sent(b, 0xF5));
        Assert.Equal(200, U16(pkt.Span, 15));
        Assert.Equal(200, U16(pkt.Span, 17));
    }

    [Fact]
    public void TheMapGumpLeavesBeforeItsOwnPins()
    {
        var b = Setup(clientVersion: 70_013_000);
        var map = MapAt(b, 100, 100);
        map.SetTag("PIN_1", "95,95");

        b.Client.ItemUse.OpenMapGump(map);

        // The queue drains by priority, so the window has to sit in the same one as
        // the pin packets that follow it - otherwise the client is told to plot on a
        // window it has not been given.
        var order = TestHarness.GetQueuedPackets(b.State).Select(p => p.Span[0]).ToList();
        Assert.Equal(0xF5, order[0]);
        Assert.Equal(0x56, order[1]);
    }

    // ================================================================ 11C-4

    private static PacketBuffer BookPageWire(uint serial, int firstPageLines)
    {
        var w = new List<byte>();
        void U32(uint v) { w.Add((byte)(v >> 24)); w.Add((byte)(v >> 16)); w.Add((byte)(v >> 8)); w.Add((byte)v); }
        void U16w(ushort v) { w.Add((byte)(v >> 8)); w.Add((byte)v); }

        U32(serial);
        U16w(2);                       // two pages in this request
        U16w(1);                       // page 1
        U16w((ushort)firstPageLines);
        for (int i = 0; i < firstPageLines; i++) { w.Add((byte)'x'); w.Add(0); }
        U16w(2);                       // page 2
        U16w(1);
        w.AddRange(System.Text.Encoding.ASCII.GetBytes("new second"));
        w.Add(0);
        return new PacketBuffer(w.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(100)]
    public void ALongFirstPageDoesNotSwallowTheSecond(int lines)
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.SetTag("PAGE_2", "old second");
        b.State.BookPageHandler = (_, serial, pages) => b.Client.HandleBookPage(serial, pages);

        new PacketBookPage().OnReceive(BookPageWire(book.Uid.Value, lines), b.State);

        Assert.True(book.TryGetTag("PAGE_2", out string? second));
        Assert.Equal("new second", second);
    }

    [Fact]
    public void APageDeclaringMoreLinesThanUpstreamToleratesEndsTheRequest()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.SetTag("PAGE_2", "old second");
        b.State.BookPageHandler = (_, serial, pages) => b.Client.HandleBookPage(serial, pages);

        new PacketBookPage().OnReceive(BookPageWire(book.Uid.Value, 101), b.State);

        // Nothing past the offending page is parsed, and no stray page number is
        // invented out of its leftover bytes.
        Assert.True(book.TryGetTag("PAGE_2", out string? second));
        Assert.Equal("old second", second);
    }

    // ================================================================ 11C-5

    [Fact]
    public void AReadOnlyBookIsNotAnnouncedAsWritable()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);
        book.SetTag("BOOK_WRITABLE", "0");

        b.Client.HandleDoubleClick(book.Uid.Value);

        var header = Assert.Single(Sent(b, 0x93));
        Assert.Equal(0, header.Span[5]);
        // and the server agrees: the page edit is refused.
        b.Client.HandleBookPage(book.Uid.Value, [(1, ["nope"])]);
        Assert.False(book.TryGetTag("PAGE_1", out _));
    }

    [Fact]
    public void AnOrdinaryBookIsStillAnnouncedAsWritable()
    {
        var b = Setup();
        var book = Place(b, ItemType.Book, 100, 100);

        b.Client.HandleDoubleClick(book.Uid.Value);

        var header = Assert.Single(Sent(b, 0x93));
        Assert.Equal(1, header.Span[5]);
    }

    // ================================================================ 12A-1

    [Fact]
    public void TheWeatherVerbsSpeakSourceXCodes()
    {
        var sector = new Sector(0, 0, 0, 4);
        var console = new TestConsole();

        Assert.True(sector.TryExecuteCommand("DRY", "", console));
        Assert.Equal(Sector.WeatherDry, sector.Weather);

        Assert.True(sector.TryExecuteCommand("RAIN", "", console));
        Assert.Equal((byte)WeatherType.Rain, sector.Weather);

        Assert.True(sector.TryExecuteCommand("SNOW", "", console));
        Assert.Equal((byte)WeatherType.Snow, sector.Weather);
    }

    [Fact]
    public void AFreshSectorIsDry()
    {
        Assert.Equal(Sector.WeatherDry, new Sector(0, 0, 0, 4).Weather);
    }

    // ================================================================ 12A-2

    [Fact]
    public void SettingASectorsWeatherReachesThePlayersStandingInIt()
    {
        var b = Setup();
        var notified = new List<(Sector S, Character C)>();
        b.World.OnSectorEnvironment = (s, c) => notified.Add((s, c));
        var sector = b.World.GetSector(b.Me.Position)!;

        sector.Weather = (byte)WeatherType.Snow;

        var one = Assert.Single(notified);
        Assert.Equal(b.Me, one.C);
        Assert.Equal((byte)WeatherType.Snow, one.S.Weather);
    }

    [Fact]
    public void SettingASectorsSeasonReachesThemToo()
    {
        var b = Setup();
        int notified = 0;
        b.World.OnSectorEnvironment = (_, _) => notified++;
        var sector = b.World.GetSector(b.Me.Position)!;

        sector.Season = (byte)SeasonType.Winter;

        Assert.Equal(1, notified);
    }

    [Fact]
    public void SettingTheSameWeatherAgainSaysNothing()
    {
        var b = Setup();
        var sector = b.World.GetSector(b.Me.Position)!;
        sector.Weather = (byte)WeatherType.Rain;
        int notified = 0;
        b.World.OnSectorEnvironment = (_, _) => notified++;

        sector.Weather = (byte)WeatherType.Rain;

        Assert.Equal(0, notified);
    }

    // ================================================================ 12A-3

    [Fact]
    public void APinnedSectorLightIsWhatEveryoneSees()
    {
        var sector = LightBench();

        sector.Light = 12;

        Assert.True(sector.IsLightOverridden);
        Assert.Equal(12, sector.Light);
        Assert.Equal((byte)12, sector.GetLightCalc());
    }

    [Fact]
    public void APinnedLightDoesNotDriftBackToTheClock()
    {
        var sector = LightBench();
        sector.Light = 12;

        Assert.False(sector.RefreshLight());
        Assert.Equal((byte)12, sector.GetLightCalc());
    }

    [Fact]
    public void ClearingThePinGoesBackToTheTimeOfDay()
    {
        var sector = LightBench();
        sector.Light = 12;

        sector.ClearLightOverride();

        Assert.False(sector.IsLightOverridden);
        Assert.Equal((byte)0, sector.GetLightCalc());
    }

    [Fact]
    public void APinnedLightIsIgnoredWhenTheShardForbidsIt()
    {
        var sector = LightBench();
        sector.AllowLightOverride = () => false;

        sector.Light = 12;

        Assert.Equal((byte)0, sector.GetLightCalc());
    }

    /// <summary>Noon, so the calculated light is full daylight (0).</summary>
    private static Sector LightBench() => new Sector(0, 0, 0, 4)
    {
        GetWorldMinutes = () => 720,
        GetWorldTime = () => (12, 0),
        GetLightSettings = () => (0, 25, 27),
        IsDungeon = () => false,
    };

    // ================================================================ 12A-4

    [Fact]
    public void ARainlessRegionStaysDry()
    {
        var (engine, _) = WeatherBench(rain: 0, cold: 0);
        var seen = new List<WeatherType>();
        engine.OnWeatherChanged = (_, t, _, _) => seen.Add(t);

        for (int i = 0; i < 20; i++) engine.OnTick();

        Assert.Empty(seen);
    }

    [Fact]
    public void AColdRegionGetsSnowWheneverItPrecipitates()
    {
        var (engine, _) = WeatherBench(rain: 100, cold: 100);
        var seen = new List<WeatherType>();
        engine.OnWeatherChanged = (_, t, _, _) => seen.Add(t);

        for (int i = 0; i < 20; i++) engine.OnTick();

        Assert.NotEmpty(seen);
        Assert.All(seen.Where(t => t != WeatherType.None), t => Assert.Equal(WeatherType.Snow, t));
    }

    private static (WeatherEngine Engine, Region Region) WeatherBench(int rain, int cold)
    {
        var b = Setup();
        var region = new Region { Name = "climate test", MapIndex = 0 };
        region.AddRect(0, 0, 4000, 4000);
        region.P = new Point3D(100, 100, 0, 0);
        b.World.AddRegion(region);

        // Weather is only rolled for a region that has an online player in it.
        b.Me.IsOnline = true;
        b.World.AddOnlinePlayer(b.Me);

        var engine = new WeatherEngine(b.World)
        {
            GetClimate = _ => (rain, cold),
        };
        // The generation roll itself is pinned: whether a REGION precipitates is a
        // 0.5%-per-tick draw, so leaving it to chance made this assertion a coin flip
        // over any bounded number of ticks. With the low value forced, the climate
        // numbers alone decide - which is what these two tests are about.
        typeof(WeatherEngine)
            .GetField("_rand", System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.NonPublic)!
            .SetValue(engine, new LowestRoll());
        return (engine, region);
    }

    /// <summary>A Random that always rolls the bottom of its range.</summary>
    private sealed class LowestRoll : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    // ================================================================ 12A-5

    [Fact]
    public void TwoRegionsOfTheSameNameDoNotShareOneSky()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 1024, 1024);
        world.InitMap(1, 1024, 1024);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;

        var britain0 = new Region { Name = "Britain", MapIndex = 0 };
        britain0.AddRect(0, 0, 500, 500);
        var britain1 = new Region { Name = "Britain", MapIndex = 1 };
        britain1.AddRect(0, 0, 500, 500);
        world.AddRegion(britain0);
        world.AddRegion(britain1);

        var engine = new WeatherEngine(world);
        engine.SetRegionWeather(britain0, WeatherType.Rain, 20, 15);

        Assert.Equal(WeatherType.Rain, engine.GetWeatherForRegion(britain0).Type);
        Assert.Equal(WeatherType.None, engine.GetWeatherForRegion(britain1).Type);
    }

    [Fact]
    public void RenamingARegionDoesNotOrphanItsWeather()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 1024, 1024);
        var region = new Region { Name = "Britain", MapIndex = 0 };
        region.AddRect(0, 0, 500, 500);
        world.AddRegion(region);

        var engine = new WeatherEngine(world);
        engine.SetRegionWeather(region, WeatherType.Storm, 60, 15);
        region.Name = "New Britain";

        Assert.Equal(WeatherType.Storm, engine.GetWeatherForRegion(region).Type);
    }

    private sealed class TestConsole : SphereNet.Core.Interfaces.ITextConsole
    {
        public void SysMessage(string message) { }
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "test";
    }
}
