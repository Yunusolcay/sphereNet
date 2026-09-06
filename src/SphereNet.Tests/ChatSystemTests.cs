using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.Chat;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using System.Text;

namespace SphereNet.Tests;

/// <summary>
/// UO chat (conference) system: 0xB2 outgoing byte layout (parsed back the
/// way the ClassicUO handler does), 0xB3 incoming command parse, and the
/// ChatEngine channel/membership state machine.
/// </summary>
public class ChatSystemTests
{
    // --- 0xB2 layout: parse exactly like ClassicUO PacketHandlers.ChatMessage ---

    private static (ushort Cmd, ushort? Code, string? S1, string? S2, ushort? Trailer)
        ParseChatPacket(PacketBuffer built, bool hasCode, int strings, bool hasTrailer)
    {
        byte[] data = built.Span.ToArray();
        Assert.Equal(0xB2, data[0]);
        int len = (data[1] << 8) | data[2];
        Assert.Equal(data.Length, len);

        int o = 3;
        ushort cmd = (ushort)((data[o] << 8) | data[o + 1]); o += 2;
        o += 4; // client: p.Skip(4)

        ushort? code = null;
        if (hasCode) { code = (ushort)((data[o] << 8) | data[o + 1]); o += 2; }

        string? ReadUnicodeBE()
        {
            var sb = new StringBuilder();
            while (o + 1 < data.Length)
            {
                ushort ch = (ushort)((data[o] << 8) | data[o + 1]); o += 2;
                if (ch == 0) break;
                sb.Append((char)ch);
            }
            return sb.ToString();
        }

        string? s1 = strings >= 1 ? ReadUnicodeBE() : null;
        string? s2 = strings >= 2 ? ReadUnicodeBE() : null;
        ushort? trailer = null;
        if (hasTrailer) { trailer = (ushort)((data[o] << 8) | data[o + 1]); o += 2; }
        Assert.Equal(data.Length, o);
        return (cmd, code, s1, s2, trailer);
    }

    [Fact]
    public void ChatPacket_CreateChannel_MatchesClientParse()
    {
        var built = PacketChatSystem.MakeCreateChannel("General").Build();
        var (cmd, _, s1, _, trailer) = ParseChatPacket(built, hasCode: false, strings: 1, hasTrailer: true);
        Assert.Equal(PacketChatSystem.CreateChannel, cmd);
        Assert.Equal("General", s1);
        Assert.Equal((ushort)'0', trailer); // '1' would mean password-protected
    }

    [Fact]
    public void ChatPacket_ChannelMessage_MatchesClientParse()
    {
        var built = PacketChatSystem.MakeChannelMessage("Yunus", "selam dunya").Build();
        var (cmd, code, s1, s2, _) = ParseChatPacket(built, hasCode: true, strings: 2, hasTrailer: false);
        Assert.Equal(PacketChatSystem.ChannelMessage, cmd);
        Assert.Equal((ushort)0, code);
        Assert.Equal("Yunus", s1);
        Assert.Equal("selam dunya", s2);
    }

    [Fact]
    public void ChatPacket_JoinedChannel_MatchesClientParse()
    {
        var built = PacketChatSystem.MakeJoinedChannel("General").Build();
        var (cmd, _, s1, _, _) = ParseChatPacket(built, hasCode: false, strings: 1, hasTrailer: false);
        Assert.Equal(PacketChatSystem.JoinedChannel, cmd);
        Assert.Equal("General", s1);
    }

    // --- 0xB3 incoming parse ---

    [Fact]
    public void ChatAction_ParsesLangCommandAndText()
    {
        // [lang:4]["TRK\0"][cmd 0x61][unicode "hi" + null]
        var payload = new byte[4 + 2 + 6];
        payload[0] = (byte)'T'; payload[1] = (byte)'R'; payload[2] = (byte)'K'; payload[3] = 0;
        payload[4] = 0x00; payload[5] = 0x61;
        payload[6] = 0x00; payload[7] = (byte)'h';
        payload[8] = 0x00; payload[9] = (byte)'i';
        payload[10] = 0x00; payload[11] = 0x00;

        var state = new NetState(NullLogger<NetState>.Instance);
        ushort seenCmd = 0; string seenText = "";
        state.ChatActionHandler = (_, cmd, text) => { seenCmd = cmd; seenText = text; };

        new PacketChatAction().OnReceive(new PacketBuffer(payload), state);

        Assert.Equal((ushort)0x61, seenCmd);
        Assert.Equal("hi", seenText);
    }

    // --- ChatEngine state machine ---

    [Fact]
    public void ChatEngine_JoinLeave_TracksMembershipAndAdhocChannels()
    {
        var engine = new ChatEngine("General");
        var a = new Serial(1);
        var b = new Serial(2);

        Assert.NotNull(engine.Join(a, "General"));
        Assert.NotNull(engine.Join(b, "General"));
        Assert.Equal(2, engine.GetChannel("General")!.Members.Count);

        // Joining another channel leaves the previous one. Making a new one is the
        // create command: Source-X refuses a join to a channel that does not exist
        // (CChat.cpp:70), so the two are no longer the same call.
        var adhoc = engine.Join(a, "Trade", create: true);
        Assert.NotNull(adhoc);
        Assert.Single(engine.GetChannel("General")!.Members);
        Assert.Equal("Trade", engine.GetMemberChannel(a)!.Name);

        // Ad-hoc channels disappear when emptied; static ones survive.
        engine.Leave(a);
        Assert.Null(engine.GetChannel("Trade"));
        engine.Leave(b);
        Assert.NotNull(engine.GetChannel("General"));
    }

    [Fact]
    public void ChatEngine_ChatNames_RoundTrip()
    {
        var engine = new ChatEngine();
        var uid = new Serial(42);
        Assert.Equal("", engine.GetChatName(uid));
        engine.SetChatName(uid, "Yunus");
        Assert.Equal("Yunus", engine.GetChatName(uid));
    }

    // --- Conference moderation: owner, password, kick, voice/moderator roles ---

    [Fact]
    public void ChatEngine_AdHocCreator_BecomesOwnerAndModerator()
    {
        var engine = new ChatEngine();
        var owner = new Serial(1);
        var channel = engine.Join(owner, "Trade", create: true);
        Assert.NotNull(channel);
        Assert.Equal(owner, channel!.Owner);
        Assert.True(channel.IsModerator(owner));
        Assert.Equal((ushort)1, channel.UserType(owner)); // owner shows as moderator
    }

    [Fact]
    public void ChatEngine_PasswordProtectedChannel_RejectsWrongOrMissingPassword()
    {
        var engine = new ChatEngine();
        var owner = new Serial(1);
        var guest = new Serial(2);

        // Owner creates a protected channel.
        Assert.NotNull(engine.Join(owner, "Secret", "letmein", create: true));
        Assert.True(engine.GetChannel("Secret")!.HasPassword);

        // Missing and wrong passwords are rejected; the guest stays out.
        Assert.Null(engine.Join(guest, "Secret"));
        Assert.Null(engine.Join(guest, "Secret", "nope"));
        Assert.Null(engine.GetMemberChannel(guest));

        // Correct password joins.
        Assert.NotNull(engine.Join(guest, "Secret", "letmein"));
        Assert.Equal("Secret", engine.GetMemberChannel(guest)!.Name);
    }

    [Fact]
    public void ChatEngine_Kick_OnlyModeratorRemovesOtherMember()
    {
        var engine = new ChatEngine();
        var owner = new Serial(1);
        var member = new Serial(2);
        engine.Join(owner, "Trade", create: true);
        engine.Join(member, "Trade");

        // A non-moderator cannot kick.
        Assert.Null(engine.Kick(member, owner));
        // The owner cannot kick themselves.
        Assert.Null(engine.Kick(owner, owner));

        // The owner kicks the member out.
        var channel = engine.Kick(owner, member);
        Assert.NotNull(channel);
        Assert.Null(engine.GetMemberChannel(member));
        Assert.DoesNotContain(member, engine.GetChannel("Trade")!.Members);
    }

    [Fact]
    public void ChatEngine_ModeratedChannel_GatesSpeechByVoiceAndModerator()
    {
        var engine = new ChatEngine();
        var owner = new Serial(1);
        var member = new Serial(2);
        var channel = engine.Join(owner, "Trade", create: true)!;
        engine.Join(member, "Trade");

        // Turning the default off does NOT reach back and silence a member who is
        // already talking: Source-X asks the member's own no-voice record (HasVoice,
        // CChatChannel.cpp:272) and the default only decides what a member starts with.
        // The old expectation had the setting mute everyone retroactively.
        Assert.True(engine.SetDefaultVoice(owner, false));
        Assert.True(channel.CanSpeak(owner));   // moderator
        Assert.True(channel.CanSpeak(member));  // was already speaking

        // Someone arriving after the change starts without a voice.
        var latecomer = new Serial(4);
        engine.Join(latecomer, "Trade");
        Assert.False(channel.CanSpeak(latecomer));
        Assert.Equal((ushort)0, channel.UserType(latecomer));

        // Taking the member's voice away individually does silence them.
        Assert.True(engine.SetVoice(owner, member, false));
        Assert.False(channel.CanSpeak(member));

        // Granting it back shows the voiced marker.
        Assert.True(engine.SetVoice(owner, member, true));
        Assert.True(channel.CanSpeak(member));
        Assert.Equal((ushort)2, channel.UserType(member)); // voiced marker

        // A non-moderator cannot grant voice or moderator status.
        var other = new Serial(3);
        engine.Join(other, "Trade");
        Assert.False(engine.SetVoice(member, other, true));
        Assert.False(engine.SetModerator(member, other, true));

        // Promote the member to moderator: UserType flips to 1 and revoking voice
        // no longer silences them.
        Assert.True(engine.SetModerator(owner, member, true));
        Assert.Equal((ushort)1, channel.UserType(member));
        Assert.True(channel.CanSpeak(member));
    }

    [Fact]
    public void ChatEngine_Rename_ReKeysAndRejectsTakenOrStatic()
    {
        var engine = new ChatEngine("General");
        var owner = new Serial(1);
        var staticMember = new Serial(2);
        engine.Join(owner, "Trade", create: true);
        engine.Join(staticMember, "General");

        // Static channels cannot be renamed.
        Assert.Null(engine.Rename(staticMember, "Lobby"));

        // A taken name is rejected.
        Assert.Null(engine.Rename(owner, "General"));
        // An empty name is rejected.
        Assert.Null(engine.Rename(owner, "   "));

        // A free name re-keys the channel and preserves membership.
        var renamed = engine.Rename(owner, "Bazaar");
        Assert.NotNull(renamed);
        Assert.Null(engine.GetChannel("Trade"));
        Assert.Equal("Bazaar", engine.GetMemberChannel(owner)!.Name);
        Assert.Contains(owner, engine.GetChannel("Bazaar")!.Members);
    }

    [Fact]
    public void ChatEngine_SetPassword_TogglesProtectionForLaterJoins()
    {
        var engine = new ChatEngine();
        var owner = new Serial(1);
        var guest = new Serial(2);
        engine.Join(owner, "Trade", create: true);

        // A non-member cannot set the password.
        Assert.False(engine.SetPassword(guest, "secret"));

        // The owner sets a password; a later join needs it.
        Assert.True(engine.SetPassword(owner, "secret"));
        Assert.True(engine.GetChannel("Trade")!.HasPassword);
        Assert.Null(engine.Join(guest, "Trade"));
        Assert.NotNull(engine.Join(guest, "Trade", "secret"));

        // Clearing the password re-opens the channel.
        Assert.True(engine.SetPassword(owner, null));
        Assert.False(engine.GetChannel("Trade")!.HasPassword);
    }

    // --- Ignore list (0x66/0x67/0x68) ---

    [Fact]
    public void ChatEngine_Ignore_AddRemoveAndSelfGuard()
    {
        var engine = new ChatEngine();
        var a = new Serial(1);
        var b = new Serial(2);

        Assert.False(engine.IsIgnoring(a, b));

        // Adding registers the ignore; a second add is a no-op (no change).
        Assert.True(engine.SetIgnored(a, b, true));
        Assert.True(engine.IsIgnoring(a, b));
        Assert.False(engine.SetIgnored(a, b, true));

        // Ignore is directional: b is not ignoring a.
        Assert.False(engine.IsIgnoring(b, a));

        // A character cannot ignore itself or an invalid target.
        Assert.False(engine.SetIgnored(a, a, true));
        Assert.False(engine.SetIgnored(a, Serial.Invalid, true));

        // Removing clears it; removing again reports no change.
        Assert.True(engine.SetIgnored(a, b, false));
        Assert.False(engine.IsIgnoring(a, b));
        Assert.False(engine.SetIgnored(a, b, false));
    }

    [Fact]
    public void ChatEngine_ToggleIgnore_FlipsAndReturnsNewState()
    {
        var engine = new ChatEngine();
        var a = new Serial(1);
        var b = new Serial(2);

        Assert.True(engine.ToggleIgnored(a, b));   // now ignored
        Assert.True(engine.IsIgnoring(a, b));
        Assert.False(engine.ToggleIgnored(a, b));  // back to allowed
        Assert.False(engine.IsIgnoring(a, b));
    }
}
