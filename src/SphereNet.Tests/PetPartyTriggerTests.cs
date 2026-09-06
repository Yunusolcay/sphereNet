using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the wired pet/party P1 triggers @Follow and @PartyDisband. @Follow
// fires when a pet is commanded to follow its master ("follow me"/"come"); the
// pet is a non-player so it routes through the EVENTSPET global set.
// @PartyDisband fires on each former member when a party drops to zero.
public class PetPartyTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient client, Character actor) NewClient(GameWorld world, int id)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        var actor = world.CreateCharacter();
        actor.IsPlayer = true;
        world.PlaceCharacter(actor, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, actor);
        return (client, actor);
    }

    [Fact]
    public void Follow_PetCommandedToFollow_FiresFollowTrigger()
    {
        var world = CreateWorld();
        var (client, player) = NewClient(world, 1701);

        var pet = world.CreateCharacter();
        pet.Name = "rex";
        pet.NpcMaster = player.Uid;                       // owned → CanAcceptPetCommandFrom
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        var d = new TriggerDispatcher();
        int fires = 0;
        d.RegisterCharEvent("EVENTSPET", "Follow", (_, _) => { fires++; return TriggerResult.Default; });
        client.SetEngines(triggerDispatcher: d);

        client.HandleSpeech(0, 0, 0, "rex follow me");

        Assert.Equal(1, fires);
        Assert.Equal(SphereNet.Core.Enums.PetAIMode.Follow, pet.PetAIMode);
    }

    [Fact]
    // @PartyDisband is asked BEFORE the party comes apart, on the member left holding
    // it, and RETURN 1 stops that stage (Source-X Disband, CParty.cpp:375). The old
    // expectation had it fired afterwards on every former member, with the party
    // already empty - too late to refuse and with nothing left to describe.
    public void PartyDisband_RemovingLastMember_AsksTheMemberLeftHoldingIt()
    {
        var world = CreateWorld();
        var (client, master) = NewClient(world, 1702);
        var member = world.CreateCharacter();
        member.IsPlayer = true;
        world.PlaceCharacter(member, new Point3D(101, 100, 0, 0));

        var pm = new PartyManager();
        pm.AcceptInvite(master.Uid, member.Uid); // master + member = 2-member party

        var d = new TriggerDispatcher();
        var disbanded = new List<uint>();
        d.RegisterCharEvent("EVENTSPLAYER", "PartyDisband",
            (obj, _) => { disbanded.Add(((Character)obj).Uid.Value); return TriggerResult.Default; });
        client.SetEngines(triggerDispatcher: d, partyManager: pm);

        // 0xBF sub 0x06, command 2 (remove member) + member UID — drops party to 0.
        uint u = member.Uid.Value;
        byte[] data = [2, (byte)(u >> 24), (byte)(u >> 16), (byte)(u >> 8), (byte)u];
        client.HandleExtendedCommand(0x0006, data);

        Assert.Equal([master.Uid.Value], disbanded);
        Assert.Null(pm.FindParty(master.Uid));
    }

    [Fact]
    public void PartyDisband_Veto_KeepsThePartyTogether()
    {
        var world = CreateWorld();
        var (client, master) = NewClient(world, 1703);
        var member = world.CreateCharacter();
        member.IsPlayer = true;
        world.PlaceCharacter(member, new Point3D(101, 100, 0, 0));

        var pm = new PartyManager();
        pm.AcceptInvite(master.Uid, member.Uid);

        var d = new TriggerDispatcher();
        d.RegisterCharEvent("EVENTSPLAYER", "PartyDisband", (_, _) => TriggerResult.True);
        client.SetEngines(triggerDispatcher: d, partyManager: pm);

        uint u = member.Uid.Value;
        byte[] data = [2, (byte)(u >> 24), (byte)(u >> 16), (byte)(u >> 8), (byte)u];
        client.HandleExtendedCommand(0x0006, data);

        Assert.NotNull(pm.FindParty(master.Uid));
        Assert.True(pm.FindParty(master.Uid)!.IsMember(member.Uid));
    }
}
