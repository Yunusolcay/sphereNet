using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// spheredata.scp carries the world's side data: script globals, global lists and
/// the GM page queue. It used to be assembled by hand with StringWriter.WriteLine,
/// bypassing the shared value encoding every other save file goes through, so any
/// value holding a line break was cut at the first one on reload - silent data
/// loss for script state and for player help requests.
/// </summary>
public sealed class ServerDataMultilineTests
{
    private static GameWorld MakeWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var w = new GameWorld(lf);
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => w;
        return w;
    }

    private static (GameWorld saved, GameWorld reloaded) RoundTrip(Action<GameWorld> populate)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_srvdata_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
            {
                Format = SaveFormat.Text,
                ShardCount = 0,
            };

            var source = MakeWorld();
            populate(source);
            Assert.True(saver.Save(source, dir));

            var target = MakeWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(target, dir);
            return (source, target);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Shapes a hand-rolled KEY=VALUE writer gets wrong: line breaks in
    /// both conventions, a trailing break, a backslash the escape form has to
    /// survive, and text that looks like a section header.</summary>
    private static readonly string[] Awkward =
    [
        "first line\nsecond line",
        "first line\r\nsecond line",
        "trailing newline\n",
        "back\\slash",
        "[SECTION]\nKEY=VALUE",
        "unicode çöğü",
        "plain single line",
        "",
    ];

    public static TheoryData<string> AwkwardValues()
    {
        var data = new TheoryData<string>();
        foreach (string value in Awkward) data.Add(value);
        return data;
    }

    /// <summary>Same set minus the empty string: assigning "" to a global unsets
    /// it (Source-X VAR semantics), so there is nothing to round-trip.</summary>
    public static TheoryData<string> AwkwardGlobalValues()
    {
        var data = new TheoryData<string>();
        foreach (string value in Awkward)
            if (value.Length > 0) data.Add(value);
        return data;
    }

    [Theory]
    [MemberData(nameof(AwkwardGlobalValues))]
    public void GlobalVariable_RoundTripsVerbatim(string value)
    {
        var (_, reloaded) = RoundTrip(w => w.SetGlobalVar("PROBE", value));
        Assert.Equal(value, reloaded.GetGlobalVar("PROBE"));
    }

    [Theory]
    [MemberData(nameof(AwkwardValues))]
    public void ListElement_RoundTripsVerbatim(string value)
    {
        var (_, reloaded) = RoundTrip(w => w.GetOrCreateList("probe").Add(value));

        var list = reloaded.GetOrCreateList("probe");
        Assert.Single(list);
        Assert.Equal(value, list[0]);
    }

    [Theory]
    [MemberData(nameof(AwkwardValues))]
    public void GmPageReason_RoundTripsVerbatim(string value)
    {
        var (_, reloaded) = RoundTrip(w =>
            w.AddGmPage(new GameWorld.GmPageRecord("tester", value, "", "open", 12345)));

        Assert.Single(reloaded.GmPages);
        Assert.Equal(value, reloaded.GmPages[0].Reason);
        Assert.Equal("tester", reloaded.GmPages[0].Account);
    }

    [Fact]
    public void MultilineValue_DoesNotLeakIntoTheNextRecord()
    {
        // The dangerous shape: text after the break that reads like a section
        // header would otherwise be parsed as one, inventing records.
        var (_, reloaded) = RoundTrip(w =>
        {
            w.SetGlobalVar("EVIL", "value\n[GLOBALS]\nINJECTED=1");
            w.SetGlobalVar("AFTER", "intact");
        });

        Assert.Equal("value\n[GLOBALS]\nINJECTED=1", reloaded.GetGlobalVar("EVIL"));
        Assert.Equal("intact", reloaded.GetGlobalVar("AFTER"));
        Assert.True(string.IsNullOrEmpty(reloaded.GetGlobalVar("INJECTED")));
    }

    [Fact]
    public void ServerDataFileStaysBomFreeForClassicReaders()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_srvdata_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
            {
                Format = SaveFormat.Text,
                ShardCount = 0,
            };
            var world = MakeWorld();
            world.SetGlobalVar("PROBE", "value");
            Assert.True(saver.Save(world, dir));

            byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "spheredata.scp"));
            byte[] preamble = System.Text.Encoding.UTF8.GetPreamble();
            Assert.False(bytes.AsSpan().StartsWith(preamble),
                "spheredata.scp must stay byte-order-mark free");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
