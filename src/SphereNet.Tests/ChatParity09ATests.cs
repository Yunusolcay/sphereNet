using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Chat;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Chat channels: switching, passwords, voice and the channel list.
///
/// Source-X checks the destination before leaving the old channel (JoinChannel,
/// CChat.cpp:70), keeps create and join apart (:12/:70), parses the join command as
/// <c>"Name" password</c> (:169), reads a member's own voice record rather than the
/// channel default (HasVoice, CChatChannel.cpp:272), lets a founder's moderation be
/// revoked like anyone else's (RevokeModerator, :517), and announces channel-list
/// changes to everyone in chat (CChat.cpp:48; RenameChannel, :138).
/// </summary>
public sealed class ChatParity09ATests
{
    private sealed record Bench(GameWorld World, ChatEngine Chat,
        GameClient Alice, Character AliceChar, GameClient Bob, Character BobChar,
        List<(Serial To, SphereNet.Network.Packets.PacketWriter Packet)> Sent);

    private static Bench Setup(params string[] staticChannels)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var chat = new ChatEngine(staticChannels);
        var sent = new List<(Serial, SphereNet.Network.Packets.PacketWriter)>();
        var lf = LoggerFactory.Create(_ => { });
        var accounts = new AccountManager(lf);

        GameClient Player(int port, string name, out Character ch)
        {
            var client = TestHarness.CreateClient(lf, world, accounts, port);
            client.SetEngines(chatEngine: chat);
            ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Name = name;
            world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, ch);
            client.SendToChar = (to, packet) => sent.Add((to, packet));
            client.HandleChatOpen();
            return client;
        }

        var alice = Player(8501, "Alice", out var aliceChar);
        var bob = Player(8502, "Bob", out var bobChar);
        return new Bench(world, chat, alice, aliceChar, bob, bobChar, sent);
    }

    private const ushort Join = 0x62;
    private const ushort Create = 0x63;

    // --- 09A-1: a refused switch keeps the old channel -------------------

    [Fact]
    public void AWrongPasswordDoesNotCostTheChannelYouAreIn()
    {
        var bench = Setup();
        bench.Alice.HandleChatAction(Create, "Old");
        bench.Bob.HandleChatAction(Create, "Locked{secret}");

        bench.Alice.HandleChatAction(Join, "\"Locked\" wrong");

        Assert.Equal("Old", bench.Chat.GetMemberChannel(bench.AliceChar.Uid)?.Name);
        Assert.NotNull(bench.Chat.GetChannel("Old"));
    }

    [Fact]
    public void ARightPasswordStillSwitches()
    {
        var bench = Setup();
        bench.Alice.HandleChatAction(Create, "Old");
        bench.Bob.HandleChatAction(Create, "Locked{secret}");

        bench.Alice.HandleChatAction(Join, "\"Locked\" secret");

        Assert.Equal("Locked", bench.Chat.GetMemberChannel(bench.AliceChar.Uid)?.Name);
    }

    // --- 09A-2: the join command carries its password --------------------

    [Fact]
    public void TheJoinCommandReadsThePasswordAfterTheQuotes()
    {
        var bench = Setup();
        bench.Bob.HandleChatAction(Create, "Locked{secret}");

        bench.Alice.HandleChatAction(Join, "\"Locked\" secret");

        Assert.Equal("Locked", bench.Chat.GetMemberChannel(bench.AliceChar.Uid)?.Name);
    }

    // --- 09A-5: create and join are different commands -------------------

    [Fact]
    public void JoiningAChannelThatIsGoneCreatesNothing()
    {
        var bench = Setup();

        bench.Alice.HandleChatAction(Join, "\"Room\"");

        Assert.Null(bench.Chat.GetChannel("Room"));
        Assert.Null(bench.Chat.GetMemberChannel(bench.AliceChar.Uid));
    }

    [Fact]
    public void CreatingAChannelSomebodyElseOwnsIsRefused()
    {
        var bench = Setup();
        bench.Bob.HandleChatAction(Create, "Room");

        bench.Alice.HandleChatAction(Create, "Room");

        Assert.Null(bench.Chat.GetMemberChannel(bench.AliceChar.Uid));
        Assert.Single(bench.Chat.GetChannel("Room")!.Members);
    }

    [Fact]
    public void CreatingAFreeNameStillWorks()
    {
        var bench = Setup();

        bench.Alice.HandleChatAction(Create, "Room");

        Assert.Equal("Room", bench.Chat.GetMemberChannel(bench.AliceChar.Uid)?.Name);
    }

    // --- 09A-3: the default only decides what a member starts with -------

    [Fact]
    public void TurningTheDefaultOffDoesNotSilenceWhoIsAlreadyThere()
    {
        var bench = Setup();
        bench.Alice.HandleChatAction(Create, "Room");
        bench.Bob.HandleChatAction(Join, "\"Room\"");

        Assert.True(bench.Chat.SetDefaultVoice(bench.AliceChar.Uid, false));

        Assert.True(bench.Chat.GetChannel("Room")!.CanSpeak(bench.BobChar.Uid));
    }

    // --- 09A-4: a founder's moderation can be revoked --------------------

    [Fact]
    public void AFoundersModerationCanBeTakenAway()
    {
        var bench = Setup();
        bench.Alice.HandleChatAction(Create, "Room");
        bench.Bob.HandleChatAction(Join, "\"Room\"");
        var room = bench.Chat.GetChannel("Room")!;

        Assert.True(bench.Chat.SetModerator(bench.AliceChar.Uid, bench.BobChar.Uid, true));
        Assert.True(bench.Chat.SetModerator(bench.BobChar.Uid, bench.AliceChar.Uid, false));

        Assert.False(room.IsModerator(bench.AliceChar.Uid));
    }

    [Fact]
    public void AFounderStillStartsAsModerator()
    {
        var bench = Setup();
        bench.Alice.HandleChatAction(Create, "Room");

        Assert.True(bench.Chat.GetChannel("Room")!.IsModerator(bench.AliceChar.Uid));
    }

    // --- 09A-6: the channel list is announced to everyone ----------------

    private int ChannelPacketsTo(Bench bench, Serial who) =>
        bench.Sent.Count(s => s.To == who && s.Packet is PacketChatSystem);

    [Fact]
    public void ANewChannelIsAnnouncedToEveryoneInChat()
    {
        var bench = Setup("General");
        bench.Bob.HandleChatAction(Join, "\"General\"");
        bench.Sent.Clear();

        bench.Alice.HandleChatAction(Create, "Room");

        Assert.True(ChannelPacketsTo(bench, bench.BobChar.Uid) > 0);
    }

    [Fact]
    public void ARenameIsAnnouncedOutsideTheChannelToo()
    {
        var bench = Setup("General");
        bench.Bob.HandleChatAction(Join, "\"General\"");
        bench.Alice.HandleChatAction(Create, "Room");
        bench.Sent.Clear();

        bench.Alice.HandleChatAction(0x64, "NewRoom");   // rename

        Assert.True(ChannelPacketsTo(bench, bench.BobChar.Uid) > 0);
    }
}
