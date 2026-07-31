using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// sphere.ini DEADCANNOTSEELIVING (Source-X CChar::CanSeeAsDead). A ghost keeps
/// seeing other ghosts, living players, its own pets and healers, but loses
/// sight of ordinary living NPCs. Mode 2 additionally stops an NPC from seeing
/// living players that are not its owner.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DeadCannotSeeLivingTests
{
    private static GameWorld MakeWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar(GameWorld world, bool isPlayer, int x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = isPlayer;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void Disabled_ByDefault_EverythingStaysVisible()
    {
        var world = MakeWorld();
        var ghost = MakeChar(world, isPlayer: true);
        var npc = MakeChar(world, isPlayer: false, x: 101);
        ghost.SetStatFlag(StatFlag.Dead);

        Assert.Equal(0, Character.DeadCannotSeeLiving);
        Assert.True(ghost.CanSeeAsDead(npc));
    }

    [Fact]
    public void Enabled_GhostLosesSightOfOrdinaryLivingNpcs()
    {
        var world = MakeWorld();
        Character.DeadCannotSeeLiving = 1;
        var ghost = MakeChar(world, isPlayer: true);
        ghost.SetStatFlag(StatFlag.Dead);
        var npc = MakeChar(world, isPlayer: false, x: 101);

        Assert.False(ghost.CanSeeAsDead(npc));
    }

    [Fact]
    public void Enabled_GhostStillSeesLivingPlayersAndOtherGhosts()
    {
        var world = MakeWorld();
        Character.DeadCannotSeeLiving = 1;
        var ghost = MakeChar(world, isPlayer: true);
        ghost.SetStatFlag(StatFlag.Dead);

        var livingPlayer = MakeChar(world, isPlayer: true, x: 101);
        var otherGhost = MakeChar(world, isPlayer: true, x: 102);
        otherGhost.SetStatFlag(StatFlag.Dead);
        var deadNpc = MakeChar(world, isPlayer: false, x: 103);
        deadNpc.SetStatFlag(StatFlag.Dead);

        Assert.True(ghost.CanSeeAsDead(livingPlayer));
        Assert.True(ghost.CanSeeAsDead(otherGhost));
        Assert.True(ghost.CanSeeAsDead(deadNpc));
    }

    [Fact]
    public void Enabled_GhostStillSeesItsOwnPetAndHealers()
    {
        var world = MakeWorld();
        Character.DeadCannotSeeLiving = 1;
        var ghost = MakeChar(world, isPlayer: true);
        ghost.SetStatFlag(StatFlag.Dead);

        var pet = MakeChar(world, isPlayer: false, x: 101);
        pet.SetTag("OWNER_UID", $"0{ghost.Uid.Value:X}");

        var healer = MakeChar(world, isPlayer: false, x: 102);
        healer.NpcBrain = NpcBrainType.Healer;

        Assert.True(ghost.CanSeeAsDead(pet));
        Assert.True(ghost.CanSeeAsDead(healer));
    }

    [Fact]
    public void Enabled_GmGhostIsExempt()
    {
        var world = MakeWorld();
        Character.DeadCannotSeeLiving = 1;
        var ghost = MakeChar(world, isPlayer: true);
        ghost.PrivLevel = PrivLevel.GM;
        ghost.SetStatFlag(StatFlag.Dead);
        var npc = MakeChar(world, isPlayer: false, x: 101);

        Assert.True(ghost.CanSeeAsDead(npc));
    }

    /// <summary>Mode 1 leaves NPC observers alone; only mode 2 blinds an NPC to
    /// living players that are not its owner (Source-X CCharStatus.cpp:1012).</summary>
    [Fact]
    public void ModeTwo_AlsoBlindsAnNpcToLivingPlayersThatAreNotItsOwner()
    {
        var world = MakeWorld();
        var npcObserver = MakeChar(world, isPlayer: false);
        npcObserver.SetStatFlag(StatFlag.Dead);
        var stranger = MakeChar(world, isPlayer: true, x: 101);
        var owner = MakeChar(world, isPlayer: true, x: 102);
        npcObserver.SetTag("OWNER_UID", $"0{owner.Uid.Value:X}");

        Character.DeadCannotSeeLiving = 1;
        Assert.True(npcObserver.CanSeeAsDead(stranger));

        Character.DeadCannotSeeLiving = 2;
        Assert.False(npcObserver.CanSeeAsDead(stranger));
        Assert.True(npcObserver.CanSeeAsDead(owner)); // its own owner stays visible
    }

    /// <summary>Dying and resurrecting change what the character may see, so its
    /// own client has to rebuild the view instead of waiting for the next step.
    /// </summary>
    [Fact]
    public void DeathAndResurrection_RequestAnOwnViewRefresh()
    {
        var world = MakeWorld();
        var ch = MakeChar(world, isPlayer: true);

        int refreshes = 0;
        Character.OnOwnViewRefreshNeeded = c => { if (c == ch) refreshes++; };
        try
        {
            ch.SetStatFlag(StatFlag.Dead);
            Assert.Equal(1, refreshes);

            ch.SetStatFlag(StatFlag.Dead); // already dead — no second request
            Assert.Equal(1, refreshes);

            ch.ClearStatFlag(StatFlag.Dead);
            Assert.Equal(2, refreshes);
        }
        finally { Character.OnOwnViewRefreshNeeded = null; }
    }
}
