using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-H3 (wiki/hedef.txt long tail):
//   * IT_WEB struggle: dclick damages the web with STR; a destroyed web
//     leaves spider silk and unfreezes the stuck char (Source-X Use_Item_Web)
//   * char verb WAKE (counterpart of SLEEP), object verb DESTROY (hard
//     removal), SMSG* sysmessage aliases
public class ParityWaveH3Tests
{
    private sealed class CaptureConsole : SphereNet.Core.Interfaces.ITextConsole
    {
        public readonly List<string> Lines = [];
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) => Lines.Add(text);
        public string GetName() => "capture";
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    // A torn web leaves NOTHING behind: the reference's IT_WEB damage branch calls
    // Delete() and creates no resource (CItem.cpp:5886). The "silk" this expectation
    // used to demand came from a stale comment in Use_Item_Web, and the graphic it
    // named (0x0DF8) is wool.
    [Fact]
    public void Web_Struggle_DestroysWeb_AndUnfreezes()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1601);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Str = 100;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var web = world.CreateItem();
        web.BaseId = 0x0EE3;
        web.ItemType = ItemType.Web;
        web.HitsCur = 50; // one 100-STR struggle tears it
        world.PlaceItem(web, new Point3D(100, 100, 0, 0));

        // Simulate being stuck (the step path freezes on web contact).
        player.SetStatFlag(StatFlag.Freeze);

        client.HandleDoubleClick(web.Uid.Value);

        Assert.True(web.IsDeleted);
        Assert.False(player.IsStatFlag(StatFlag.Freeze));
        Assert.DoesNotContain(world.GetItemsInRange(new Point3D(100, 100, 0, 0), 0),
            i => i.BaseId == 0x0DF8);
    }

    [Fact]
    public void Web_Struggle_StrongWebSurvives()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1602);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Str = 10;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var web = world.CreateItem();
        web.BaseId = 0x0EE3;
        web.ItemType = ItemType.Web;
        web.HitsCur = 300;
        world.PlaceItem(web, new Point3D(100, 100, 0, 0));
        player.SetStatFlag(StatFlag.Freeze);

        client.HandleDoubleClick(web.Uid.Value);

        Assert.False(web.IsDeleted);
        Assert.Equal(290, web.HitsCur);                       // STR chipped it
        Assert.True(player.IsStatFlag(StatFlag.Freeze));      // still stuck
    }

    [Fact]
    public void WakeVerb_ClearsSleeping()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        var console = new CaptureConsole();

        Assert.True(ch.TryExecuteCommand("SLEEP", "", console));
        Assert.True(ch.IsStatFlag(StatFlag.Sleeping));

        Assert.True(ch.TryExecuteCommand("WAKE", "", console));
        Assert.False(ch.IsStatFlag(StatFlag.Sleeping));
    }

    [Fact]
    public void DestroyVerb_RemovesObject()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));

        Assert.True(item.TryExecuteCommand("DESTROY", "", new CaptureConsole()));
        Assert.True(item.IsDeleted);
    }

    [Fact]
    public void SmsgAliases_RouteToSysMessage()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        var console = new CaptureConsole();

        Assert.True(ch.TryExecuteCommand("SMSG", "hello there", console));
        Assert.True(ch.TryExecuteCommand("SMSGU", "unicode line", console));

        Assert.Contains("hello there", console.Lines);
        Assert.Contains("unicode line", console.Lines);
    }
}
