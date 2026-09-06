using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Party invitations and the membership they create.
///
/// Source-X answers the invitation the client NAMES (receive.cpp:2708 -> AcceptEvent,
/// CParty.cpp:443), re-checks that the inviter can still see the one accepting (:457),
/// runs @PartyAdd before any membership changes (:481), honours PARTY_AUTODECLINEINVITE
/// before an invitation is sent at all (CClientTarg.cpp:2455), and lets both @PartyRemove
/// and @PartyLeave refuse a removal (:315). The standard client remove command disbands
/// when the leader is the one leaving - fDisband defaults to true (CParty.h:92).
/// </summary>
public sealed class PartyParity09BTests
{
    private const ushort PartySub = 0x0006;

    private sealed record Bench(GameWorld World, PartyManager Parties,
        GameClient AliceClient, Character Alice,
        GameClient BobClient, Character Bob,
        GameClient CarolClient, Character Carol);

    private static Bench Setup(TriggerDispatcher? triggers = null)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var parties = new PartyManager();
        var lf = LoggerFactory.Create(_ => { });
        var accounts = new AccountManager(lf);

        GameClient Player(int port, string name, short x, out Character ch)
        {
            var client = TestHarness.CreateClient(lf, world, accounts, port);
            client.SetEngines(partyManager: parties, triggerDispatcher: triggers);
            ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Name = name;
            world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
            TestHarness.AttachCharacter(client, ch);
            client.SendToChar = (_, _) => { };
            return client;
        }

        var alice = Player(8601, "Alice", 100, out var aliceChar);
        var bob = Player(8602, "Bob", 101, out var bobChar);
        var carol = Player(8603, "Carol", 102, out var carolChar);
        return new Bench(world, parties, alice, aliceChar, bob, bobChar, carol, carolChar);
    }

    private static byte[] WithUid(byte cmd, Serial uid) =>
    [
        cmd,
        (byte)(uid.Value >> 24), (byte)(uid.Value >> 16),
        (byte)(uid.Value >> 8), (byte)uid.Value,
    ];

    private static void Invite(GameClient inviter, Character target) =>
        inviter.HandlePartyInvite(target.Uid.Value);

    private static void Accept(GameClient who, Serial inviter) =>
        who.HandleExtendedCommand(PartySub, WithUid(8, inviter));

    private static void Decline(GameClient who, Serial inviter) =>
        who.HandleExtendedCommand(PartySub, WithUid(9, inviter));

    private static void Remove(GameClient who, Serial member) =>
        who.HandleExtendedCommand(PartySub, WithUid(2, member));

    // --- 09B-1: the answer names its invitation --------------------------

    [Fact]
    public void AnswerToAStaleInvitationJoinsNobody()
    {
        var bench = Setup();
        Invite(bench.AliceClient, bench.Bob);
        Invite(bench.CarolClient, bench.Bob);   // the pending one is Carol's now

        Accept(bench.BobClient, bench.Alice.Uid);

        Assert.Null(bench.Parties.FindParty(bench.Bob.Uid));
    }

    [Fact]
    public void AnswerToTheInvitationInHandStillJoins()
    {
        var bench = Setup();
        Invite(bench.AliceClient, bench.Bob);
        Invite(bench.CarolClient, bench.Bob);

        Accept(bench.BobClient, bench.Carol.Uid);

        Assert.NotNull(bench.Parties.FindParty(bench.Bob.Uid));
        Assert.Equal(bench.Carol.Uid, bench.Parties.FindParty(bench.Bob.Uid)!.Master);
    }

    [Fact]
    public void DecliningAStaleInvitationLeavesThePendingOneAlone()
    {
        var bench = Setup();
        Invite(bench.AliceClient, bench.Bob);
        Invite(bench.CarolClient, bench.Bob);

        Decline(bench.BobClient, bench.Alice.Uid);
        Accept(bench.BobClient, bench.Carol.Uid);

        Assert.NotNull(bench.Parties.FindParty(bench.Bob.Uid));
    }

    // --- 09B-2: the inviter must still see the guest ---------------------

    [Fact]
    public void AnInviterWhoLostSightOfYouCannotStillRecruitYou()
    {
        var bench = Setup();
        Invite(bench.AliceClient, bench.Bob);
        bench.World.MoveCharacter(bench.Bob, new Point3D(300, 300, 0, 0));

        Accept(bench.BobClient, bench.Alice.Uid);

        Assert.Null(bench.Parties.FindParty(bench.Bob.Uid));
    }

    // --- 09B-3: the auto-decline preference ------------------------------

    [Fact]
    public void APlayerWhoDeclinesInvitationsIsNotInvited()
    {
        var bench = Setup();
        bench.Bob.SetTag("PARTY_AUTODECLINEINVITE", "1");

        Invite(bench.AliceClient, bench.Bob);

        Assert.False(bench.Bob.TryGetTag("PARTY_INVITE_FROM", out _));
    }

    [Fact]
    public void TheProtocolInviteHonoursItToo()
    {
        var bench = Setup();
        bench.Bob.SetTag("PARTY_AUTODECLINEINVITE", "1");

        bench.AliceClient.HandleExtendedCommand(PartySub, WithUid(1, bench.Bob.Uid));

        Assert.False(bench.Bob.TryGetTag("PARTY_INVITE_FROM", out _));
    }

    // --- 09B-4: @PartyAdd runs on the join -------------------------------

    [Fact]
    public void AVetoedPartyAddKeepsTheGuestOut()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "PartyAdd", (_, _) => TriggerResult.True);
        var bench = Setup(triggers);
        Invite(bench.AliceClient, bench.Bob);

        Accept(bench.BobClient, bench.Alice.Uid);

        Assert.Null(bench.Parties.FindParty(bench.Bob.Uid));
    }

    [Fact]
    public void APartyAddThatDoesNotObjectStillJoins()
    {
        int calls = 0;
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "PartyAdd", (_, _) => { calls++; return TriggerResult.Default; });
        var bench = Setup(triggers);
        Invite(bench.AliceClient, bench.Bob);

        Accept(bench.BobClient, bench.Alice.Uid);

        Assert.Equal(1, calls);
        Assert.NotNull(bench.Parties.FindParty(bench.Bob.Uid));
    }

    // --- 09B-5: both stages may refuse a removal -------------------------

    private static Bench ThreeMemberParty(TriggerDispatcher? triggers = null)
    {
        var bench = Setup(triggers);
        Invite(bench.AliceClient, bench.Bob);
        Accept(bench.BobClient, bench.Alice.Uid);
        Invite(bench.AliceClient, bench.Carol);
        Accept(bench.CarolClient, bench.Alice.Uid);
        Assert.Equal(3, bench.Parties.FindParty(bench.Alice.Uid)!.MemberCount);
        return bench;
    }

    [Fact]
    public void AVetoedPartyRemoveKeepsTheMember()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "PartyRemove", (_, _) => TriggerResult.True);
        var bench = ThreeMemberParty(triggers);

        Remove(bench.AliceClient, bench.Bob.Uid);

        Assert.True(bench.Parties.FindParty(bench.Alice.Uid)!.IsMember(bench.Bob.Uid));
    }

    [Fact]
    public void AVetoedPartyLeaveKeepsTheMemberToo()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "PartyLeave", (_, _) => TriggerResult.True);
        var bench = ThreeMemberParty(triggers);

        Remove(bench.AliceClient, bench.Bob.Uid);

        Assert.True(bench.Parties.FindParty(bench.Alice.Uid)!.IsMember(bench.Bob.Uid));
    }

    [Fact]
    public void AnUnobjectedRemovalStillHappens()
    {
        var bench = ThreeMemberParty();

        Remove(bench.AliceClient, bench.Bob.Uid);

        Assert.Null(bench.Parties.FindParty(bench.Bob.Uid));
        Assert.Equal(2, bench.Parties.FindParty(bench.Alice.Uid)!.MemberCount);
    }

    // --- 09B-6: the leader leaving disbands ------------------------------

    [Fact]
    public void TheLeaderLeavingByTheStandardCommandDisbandsTheParty()
    {
        var bench = ThreeMemberParty();

        Remove(bench.AliceClient, bench.Alice.Uid);

        Assert.Null(bench.Parties.FindParty(bench.Alice.Uid));
        Assert.Null(bench.Parties.FindParty(bench.Bob.Uid));
        Assert.Null(bench.Parties.FindParty(bench.Carol.Uid));
    }

    [Fact]
    public void AnOrdinaryMemberLeavingDoesNot()
    {
        var bench = ThreeMemberParty();

        Remove(bench.BobClient, bench.Bob.Uid);

        var party = bench.Parties.FindParty(bench.Alice.Uid);
        Assert.NotNull(party);
        Assert.Equal(2, party!.MemberCount);
        Assert.Equal(bench.Alice.Uid, party.Master);
    }
}
