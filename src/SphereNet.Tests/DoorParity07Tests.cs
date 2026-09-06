using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Doors, gates and the levers wired to them.
///
/// Source-X keeps three separate things apart that SphereNet had collapsed into one
/// hinged-door routine. A vertical gate MOVES between the two heights in MORE1 and
/// MORE2 and never changes its art (Use_Portculis, CItem.cpp:4583). A door with a
/// custom open graphic swaps to it and shifts by MOREP, remembering the graphic it
/// replaced so the next use swaps straight back (Use_DoorNew, :4633). Only a door
/// with neither falls through to the classic hinge table (Use_Door, :4691).
///
/// Both door routines refuse an item that is not top-level before touching anything
/// (:4637, :4695) - without that, a door carried in a pack had its container slot read
/// as world coordinates and was dropped onto the map. A locked gate refuses a bare
/// hand entirely unless the use arrived through a LINK (CCharUse.cpp:1771). And a
/// lever is followed: Use_Item walks m_uidLink after the item's own use, up to 64
/// hops, signalling each target (:1962) - a link only ever opens a door.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class DoorParity07Tests
{
    private const ushort ClosedDoor = 0x0675;
    private const ushort GateGfx = 0x06F5;

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient Client, Character Player) Bench(GameWorld world, int id = 7001)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), id);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);
        return (client, player);
    }

    private static Item Gate(GameWorld world, ItemType type, sbyte lowered = 0, sbyte raised = 20)
    {
        var gate = world.CreateItem();
        gate.BaseId = GateGfx;
        gate.ItemType = type;
        gate.More1 = unchecked((uint)(byte)lowered);
        gate.More2 = unchecked((uint)(byte)raised);
        world.PlaceItem(gate, new Point3D(101, 100, lowered, 0));
        return gate;
    }

    private static Item Door(GameWorld world, ItemType type = ItemType.Door)
    {
        var door = world.CreateItem();
        door.BaseId = ClosedDoor;
        door.ItemType = type;
        world.PlaceItem(door, new Point3D(101, 100, 0, 0));
        return door;
    }

    // --- SX-07A-01: a gate moves, it does not change its picture ------------

    [Fact]
    public void APortcullisRisesToItsUpperHeight()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var gate = Gate(world, ItemType.Portculis);

        client.HandleDoubleClick(gate.Uid.Value);

        Assert.Equal(20, gate.Z);
        Assert.Equal(GateGfx, gate.DispIdFull);   // the art is untouched
    }

    [Fact]
    public void APortcullisComesBackDownOnTheNextUse()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var gate = Gate(world, ItemType.Portculis);

        client.HandleDoubleClick(gate.Uid.Value);
        client.HandleDoubleClick(gate.Uid.Value);

        Assert.Equal(0, gate.Z);
        Assert.Equal(GateGfx, gate.DispIdFull);
    }

    [Fact]
    public void AGateWithOneHeightDoesNothing()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var gate = Gate(world, ItemType.Portculis, lowered: 5, raised: 5);

        client.HandleDoubleClick(gate.Uid.Value);

        Assert.Equal(5, gate.Z);
    }

    // --- SX-07A-02: a locked gate refuses a bare hand -----------------------

    [Fact]
    public void ALockedGateIgnoresAnOrdinaryPlayer()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var gate = Gate(world, ItemType.PortLocked);

        client.HandleDoubleClick(gate.Uid.Value);

        Assert.Equal(0, gate.Z);
        Assert.Equal(GateGfx, gate.DispIdFull);
    }

    [Fact]
    public void ALockedGateOpensForStaff()
    {
        var world = CreateWorld();
        var (client, player) = Bench(world);
        player.PrivLevel = PrivLevel.GM;
        var gate = Gate(world, ItemType.PortLocked);

        client.HandleDoubleClick(gate.Uid.Value);

        Assert.Equal(20, gate.Z);
    }

    // --- SX-07B-01: a custom open graphic ----------------------------------

    [Fact]
    public void ADoorWithACustomOpenGraphicUsesItAndItsOwnOffset()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var door = Door(world);
        door.DoorOpenId = 0x06A5;
        door.MoreP = new Point3D(2, 1, 0, 0);

        client.HandleDoubleClick(door.Uid.Value);

        Assert.Equal((ushort)0x06A5, door.DispIdFull);
        Assert.Equal(103, door.X);
        Assert.Equal(101, door.Y);
    }

    [Fact]
    public void TheCustomDoorSwapsStraightBack()
    {
        var world = CreateWorld();
        var (client, player) = Bench(world);
        var door = Door(world);
        door.DoorOpenId = 0x06A5;
        door.MoreP = new Point3D(2, 1, 0, 0);

        client.HandleDoubleClick(door.Uid.Value);
        world.MoveCharacter(player, door.Position);     // stay in reach of the leaf
        client.HandleDoubleClick(door.Uid.Value);

        Assert.Equal(ClosedDoor, door.DispIdFull);
        Assert.Equal(101, door.X);
        Assert.Equal(100, door.Y);
    }

    [Fact]
    public void AnOrdinaryDoorStillUsesTheClassicHinge()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var door = Door(world);

        client.HandleDoubleClick(door.Uid.Value);

        Assert.Equal((ushort)0x0676, door.DispIdFull);
        Assert.True(door.TryGetTag("DOOR_OPEN", out _));
    }

    // --- SX-07C-01: a door in a pack is not a door in the world -------------

    [Fact]
    public void ADoorInsideAPackIsNotUsed()
    {
        var world = CreateWorld();
        var (client, player) = Bench(world);
        var door = Door(world);
        Assert.True(player.Backpack!.TryAddItem(door));

        client.HandleDoubleClick(door.Uid.Value);

        Assert.Equal(ClosedDoor, door.DispIdFull);
        Assert.True(door.ContainedIn.IsValid);
    }

    [Fact]
    public void ADoorCarriedAwayWhileOpenStaysInThePack()
    {
        // The timer close path reached the same code and dropped the door on the map.
        var world = CreateWorld();
        var (client, player) = Bench(world);
        var door = Door(world);
        client.HandleDoubleClick(door.Uid.Value);
        Assert.True(door.TryGetTag("DOOR_OPEN", out _));

        Assert.True(player.Backpack!.TryAddItem(door));
        Assert.True(door.ContainedIn.IsValid);

        Assert.False(door.CloseDoor());
        Assert.True(door.ContainedIn.IsValid);
    }

    // --- SX-07D-01: a lever works what it is wired to -----------------------

    private static Item Lever(GameWorld world)
    {
        var lever = world.CreateItem();
        lever.BaseId = 0x108C;
        lever.ItemType = ItemType.Switch;
        lever.More1 = 0x108D;
        world.PlaceItem(lever, new Point3D(101, 100, 0, 0));
        return lever;
    }

    [Fact]
    public void ALeverOpensTheDoorItIsLinkedTo()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var lever = Lever(world);
        var door = Door(world);
        world.PlaceItem(door, new Point3D(120, 101, 0, 0));   // well out of reach
        lever.Link = door.Uid;

        client.HandleDoubleClick(lever.Uid.Value);

        Assert.Equal((ushort)0x0676, door.DispIdFull);
        Assert.True(door.TryGetTag("DOOR_OPEN", out _));
    }

    [Fact]
    public void PullingTheLeverAgainDoesNotCloseTheDoor()
    {
        // A link only ever opens: Use_DoorNew returns early on a just-open use that
        // finds the door already open.
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var lever = Lever(world);
        var door = Door(world);
        world.PlaceItem(door, new Point3D(120, 101, 0, 0));
        lever.Link = door.Uid;

        client.HandleDoubleClick(lever.Uid.Value);
        client.HandleDoubleClick(lever.Uid.Value);

        Assert.True(door.TryGetTag("DOOR_OPEN", out _));
    }

    [Fact]
    public void ALeverLinkedToALockedGateWorksIt()
    {
        // The link carries the authority a bare hand does not.
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var lever = Lever(world);
        var gate = Gate(world, ItemType.PortLocked);
        world.PlaceItem(gate, new Point3D(120, 101, 0, 0));
        lever.Link = gate.Uid;

        client.HandleDoubleClick(lever.Uid.Value);

        Assert.Equal(20, gate.Z);
    }

    [Fact]
    public void AnUnlinkedLeverJustFlips()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var lever = Lever(world);
        var door = Door(world);

        client.HandleDoubleClick(lever.Uid.Value);

        Assert.Equal((ushort)0x108D, lever.DispIdFull);
        Assert.Equal(ClosedDoor, door.DispIdFull);
    }

    [Fact]
    public void ALinkChainThatLoopsBackTerminates()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var lever = Lever(world);
        var door = Door(world);
        world.PlaceItem(door, new Point3D(120, 101, 0, 0));
        lever.Link = door.Uid;
        door.Link = lever.Uid;      // back to the start

        client.HandleDoubleClick(lever.Uid.Value);

        Assert.True(door.TryGetTag("DOOR_OPEN", out _));
    }

    [Fact]
    public void ALinkToSomethingGoneIsHarmless()
    {
        var world = CreateWorld();
        var (client, _) = Bench(world);
        var lever = Lever(world);
        var door = Door(world);
        lever.Link = door.Uid;
        world.RemoveItem(door);
        door.Delete();

        client.HandleDoubleClick(lever.Uid.Value);

        Assert.Equal((ushort)0x108D, lever.DispIdFull);
    }
}
