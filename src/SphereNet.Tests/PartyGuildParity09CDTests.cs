using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Guild;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Party and guild state that has to stay consistent.
///
/// Source-X moves a new party leader to the front of the member list (SetMaster,
/// CParty.cpp:62), resolves a script add through CharFind and refuses someone already
/// in a party (:767/:443), and tells the remaining members when a disconnect changes
/// the party (SetDisconnected, CChar.cpp:528 -> :296). A guild counts members by their
/// EXACT role (CItemStone.cpp:461), holds an election when a member leaves
/// (CStoneMember.cpp:400 -> ElectMaster, CItemStone.cpp:1135), forces peace on a stone
/// with no members left (:1226) and ties the membership records to the stone's own
/// lifetime (:30).
/// </summary>
public sealed class PartyGuildParity09CDTests
{
    private static GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, short x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    // --- 09C-3: a script add keeps membership single ---------------------

    [Fact]
    public void AScriptAddDoesNotStealSomebodyElsesMember()
    {
        var world = World();
        var parties = new PartyManager();
        Character.ResolvePartyManager = () => parties;

        var alice = Player(world, 100);
        var bob = Player(world, 101);
        var carol = Player(world, 102);
        parties.AcceptInvite(carol.Uid, bob.Uid);       // Bob is Carol's already

        Assert.True(alice.TryExecuteCommand("PARTY.ADDMEMBER", $"0{bob.Uid.Value:X}", null!));

        Assert.Equal(carol.Uid, parties.FindParty(bob.Uid)?.Master);
        Assert.Equal(1, parties.Parties.Count(p => p.IsMember(bob.Uid)));
    }

    [Fact]
    public void AScriptAddOfNobodyAddsNobody()
    {
        var world = World();
        var parties = new PartyManager();
        Character.ResolvePartyManager = () => parties;
        var alice = Player(world, 100);

        Assert.True(alice.TryExecuteCommand("PARTY.ADDMEMBERFORCED", "0123456", null!));

        Assert.False(parties.FindParty(alice.Uid)?.IsMember(new Serial(0x123456)) ?? false);
    }

    [Fact]
    public void AScriptAddOfSomebodyFreeStillWorks()
    {
        var world = World();
        var parties = new PartyManager();
        Character.ResolvePartyManager = () => parties;
        var alice = Player(world, 100);
        var bob = Player(world, 101);

        Assert.True(alice.TryExecuteCommand("PARTY.ADDMEMBER", $"0{bob.Uid.Value:X}", null!));

        Assert.True(parties.FindParty(alice.Uid)?.IsMember(bob.Uid));
    }

    // --- 09C-4: the leader is member 0 -----------------------------------

    [Fact]
    public void ANewLeaderMovesToTheFrontOfTheList()
    {
        var world = World();
        var parties = new PartyManager();
        var alice = Player(world, 100);
        var bob = Player(world, 101);
        parties.AcceptInvite(alice.Uid, bob.Uid);

        var party = parties.FindParty(alice.Uid)!;
        party.SetMaster(bob.Uid);

        Assert.Equal(bob.Uid, party.Master);
        Assert.Equal(bob.Uid, party.Members[0]);
    }

    // --- 09C-2: a disconnect tells the party -----------------------------

    [Fact]
    public void ADisconnectTellsTheRestOfTheParty()
    {
        var world = World();
        var parties = new PartyManager();
        var lf = LoggerFactory.Create(_ => { });
        var accounts = new AccountManager(lf);

        var client = TestHarness.CreateClient(lf, world, accounts, 8701);
        var leaver = Player(world, 100);
        TestHarness.AttachCharacter(client, leaver);
        var sent = new List<Serial>();
        client.SendToChar = (to, _) => sent.Add(to);
        client.SetEngines(partyManager: parties);

        var stayer = Player(world, 101);
        parties.AcceptInvite(stayer.Uid, leaver.Uid);

        client.OnDisconnect();

        Assert.Contains(stayer.Uid, sent);
    }

    // --- 09C-5: a guild counts the role asked for ------------------------

    private static GuildDef GuildOfEveryRole(GameWorld world, out Serial stoneUid)
    {
        var manager = new GuildManager();
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stoneUid = stone.Uid;

        var master = Player(world, 100);
        var guild = manager.CreateGuild(stone.Uid, "Reviewers", master.Uid);
        guild.AddRecruit(Player(world, 101).Uid);                       // Candidate
        guild.JoinAsMember(Player(world, 102).Uid);                     // Member
        var accepted = guild.AddRecruit(Player(world, 103).Uid);
        accepted.Priv = GuildPriv.Accepted;
        return guild;
    }

    [Fact]
    public void MemberCountAsksForOneRoleAtATime()
    {
        var world = World();
        var guild = GuildOfEveryRole(world, out _);

        Assert.Equal(4, guild.GetMemberCount(-1));
        Assert.Equal(1, guild.GetMemberCount((int)GuildPriv.Candidate));
        Assert.Equal(1, guild.GetMemberCount((int)GuildPriv.Member));
        Assert.Equal(1, guild.GetMemberCount((int)GuildPriv.Master));
        Assert.Equal(1, guild.GetMemberCount((int)GuildPriv.Accepted));
    }

    // --- 09C-6: a departure holds an election ----------------------------

    [Fact]
    public void AMemberLeavingHoldsAFreshElection()
    {
        var world = World();
        var manager = new GuildManager();
        var stone = world.CreateItem();
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));

        var alice = Player(world, 100);
        var bob = Player(world, 101);
        var carol = Player(world, 102);
        var dave = Player(world, 103);

        var guild = manager.CreateGuild(stone.Uid, "Reviewers", alice.Uid);
        foreach (var ch in new[] { bob, carol, dave })
            guild.JoinAsMember(ch.Uid);

        // Alice and Bob back Alice; Carol and Dave back Carol - a stable two-two.
        guild.FindMember(alice.Uid)!.LoyalTo = alice.Uid;
        guild.FindMember(bob.Uid)!.LoyalTo = alice.Uid;
        guild.FindMember(carol.Uid)!.LoyalTo = carol.Uid;
        guild.FindMember(dave.Uid)!.LoyalTo = carol.Uid;
        guild.ElectMaster();
        Assert.Equal(alice.Uid, guild.GetMaster()!.CharUid);

        // Alice leaves: Bob's vote for her falls back to himself, so Carol's two win.
        manager.MemberLeft(guild, alice.Uid);

        Assert.Equal(carol.Uid, guild.GetMaster()!.CharUid);
    }

    // --- 09D-3: a guild with nobody in it is at war with nobody ----------

    [Fact]
    public void TheLastMemberLeavingEndsTheGuildsWars()
    {
        var world = World();
        var manager = new GuildManager();
        var stoneA = world.CreateItem();
        var stoneB = world.CreateItem();
        world.PlaceItem(stoneA, new Point3D(100, 100, 0, 0));
        world.PlaceItem(stoneB, new Point3D(105, 100, 0, 0));

        var alice = Player(world, 100);
        var bob = Player(world, 105);
        var guildA = manager.CreateGuild(stoneA.Uid, "A", alice.Uid);
        var guildB = manager.CreateGuild(stoneB.Uid, "B", bob.Uid);
        manager.DeclareWar(stoneA.Uid, stoneB.Uid);
        manager.DeclareWar(stoneB.Uid, stoneA.Uid);
        Assert.True(guildA.IsAtWarWith(stoneB.Uid));
        Assert.True(guildB.IsAtWarWith(stoneA.Uid));

        manager.MemberLeft(guildA, alice.Uid);

        Assert.False(guildA.IsAtWarWith(stoneB.Uid));
        Assert.False(guildB.IsAtWarWith(stoneA.Uid));
    }

    // --- 09D-2: deleting the stone takes the guild with it ---------------

    [Fact]
    public void DeletingTheStoneEndsTheGuild()
    {
        var world = World();
        var manager = new GuildManager();
        var stone = world.CreateItem();
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        var alice = Player(world, 100);
        manager.CreateGuild(stone.Uid, "Reviewers", alice.Uid);

        manager.OnStoneDeleted(stone.Uid);

        Assert.Equal(0, manager.GuildCount);
        Assert.Null(manager.FindGuildFor(alice.Uid));
    }

    // --- 09D-1: a disbanded guild does not come back ---------------------

    [Fact]
    public void ADisbandedGuildDoesNotComeBackOnTheNextLoad()
    {
        var world = World();
        var manager = new GuildManager();
        var stone = world.CreateItem();
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        var alice = Player(world, 100);
        manager.CreateGuild(stone.Uid, "Reviewers", alice.Uid);
        manager.SerializeAllToTags(world);

        manager.RemoveGuild(stone.Uid, world);

        var reloaded = new GuildManager();
        reloaded.DeserializeFromWorld(world);
        Assert.Equal(0, reloaded.GuildCount);
    }

    [Fact]
    public void AGuildThatWasNotDisbandedStillLoads()
    {
        var world = World();
        var manager = new GuildManager();
        var stone = world.CreateItem();
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        var alice = Player(world, 100);
        manager.CreateGuild(stone.Uid, "Reviewers", alice.Uid);
        manager.SerializeAllToTags(world);

        var reloaded = new GuildManager();
        reloaded.DeserializeFromWorld(world);
        Assert.Equal(1, reloaded.GuildCount);
    }

    // --- 09D-4 / 09D-5: text survives the round trip ---------------------

    private static GuildDef RoundTrip(GameWorld world, Action<GuildDef> shape)
    {
        var manager = new GuildManager();
        var stone = world.CreateItem();
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        var alice = Player(world, 100);
        var guild = manager.CreateGuild(stone.Uid, "Reviewers", alice.Uid);
        shape(guild);
        manager.SerializeAllToTags(world);

        var reloaded = new GuildManager();
        reloaded.DeserializeFromWorld(world);
        return reloaded.GetGuild(stone.Uid)!;
    }

    [Theory]
    [InlineData("Ranger\\camp")]
    [InlineData("Guard\\master")]
    [InlineData("Guard: East, Watch")]
    [InlineData("Back\\\\slash")]
    public void AMemberTitleComesBackExactlyAsItWentIn(string title)
    {
        var world = World();
        Serial who = Serial.Invalid;

        var guild = RoundTrip(world, g =>
        {
            who = g.Members[0].CharUid;
            g.Members[0].Title = title;
        });

        Assert.Equal(title, guild.FindMember(who)!.Title);
    }

    [Fact]
    public void ALongCharterIsNotTrimmedOnTheWayBackIn()
    {
        var world = World();
        string charter = new('x', 240);

        var guild = RoundTrip(world, g => g.Charter = charter);

        Assert.Equal(charter, guild.Charter);
    }

    [Fact]
    public void ALongAbbreviationIsNotTrimmedEither()
    {
        var world = World();

        var guild = RoundTrip(world, g => g.Abbreviation = "FALCONS");

        Assert.Equal("FALCONS", guild.Abbreviation);
    }
}
