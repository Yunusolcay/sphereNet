using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Locks the drop-onto-stack merge cap and the scissors fixes: a merge must cap at
/// the target's effective MaxAmount (not the raw ushort ceiling) and never dupe a
/// remainder; scissors must clean a bloody-bandage stack instead of deleting it and
/// must not type-convert a Move_Never fixture.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DropMergeScissorsTests
{
    // A minimal pile itemdef so the two ground stacks resolve as stackable
    // (IsStackable needs the I_Pile CAN flag or tiledata Generic; CAN=0x0100 is
    // I_Pile). Without it CanStackWith returns false and the merge never fires.
    private const string PileScript = """
        [ITEMDEF 0eed]
        DEFNAME=i_test_gold
        NAME=gold coin
        CAN=0x0100
        """;

    private static void LoadPileDef()
    {
        string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spherenet_dropmerge_{Guid.NewGuid():N}.scp");
        System.IO.File.WriteAllText(tempFile, PileScript);
        var resources = new ResourceHolder(NullLoggerFactory.Instance.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = System.IO.Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    [Fact]
    public void StackMerge_CapsAtMaxAmount_RemainderSurvives()
    {
        LoadPileDef();
        var (client, world, player) = Setup(PrivLevel.Player);

        var target = world.CreateItem();
        target.BaseId = 0x0EED; target.ItemType = ItemType.Gold; target.Amount = 59_000;
        world.PlaceItem(target, new Point3D(100, 100, 0, 0));

        var dropped = world.CreateItem();
        dropped.BaseId = 0x0EED; dropped.ItemType = ItemType.Gold; dropped.Amount = 5_000;
        world.PlaceItem(dropped, new Point3D(101, 100, 0, 0));
        player.SetTag("DRAGGING", dropped.Uid.Value.ToString());

        client.HandleItemDrop(dropped.Uid.Value, target.X, target.Y, target.Z, target.Uid.Value);

        // Default MaxAmount is 60000, so only 1000 merges; before the fix the ushort
        // ceiling let it grow to 64000.
        Assert.Equal(60_000, target.Amount);
        Assert.False(dropped.IsDeleted);
        Assert.Equal(4_000, dropped.Amount); // remainder preserved, not lost or duped
    }

    // Bloody bandages are washed in water, not cut. Source-X handles them under
    // IT_BANDAGE_BLOOD (CClientTarg.cpp:2244) and its scissors branch does not
    // mention them at all - the message this expectation used to report success
    // with, itemuse_bandage_clean, is the reference's "clean these in water"
    // REFUSAL. The old test asserted the invented scissors behaviour.
    [Fact]
    public void BloodyBandageStack_IsCleanedInWater_WholeStackKept()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var pack = EquipBackpack(world, player);
        var bloody = world.CreateItem();
        bloody.BaseId = 0x0E20;
        bloody.ItemType = ItemType.BandageBlood;
        bloody.Amount = 5;
        pack.AddItem(bloody);

        var trough = world.CreateItem();
        trough.ItemType = ItemType.Water;
        world.PlaceItem(trough, player.Position);

        client.HandleDoubleClick(bloody.Uid.Value);
        Assert.True(client.HasPendingTarget);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, trough.Uid.Value, 0, 0, 0, 0);

        Assert.False(bloody.IsDeleted);
        Assert.Equal(ItemType.Bandage, bloody.ItemType);
        Assert.Equal(0x0E21, bloody.BaseId);            // the clean graphic, not just a retype
        Assert.Equal(5, bloody.Amount);
    }

    [Fact]
    public void BloodyBandageStack_IsNotCleanedOnDryLand()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var pack = EquipBackpack(world, player);
        var bloody = world.CreateItem();
        bloody.BaseId = 0x0E20;
        bloody.ItemType = ItemType.BandageBlood;
        bloody.Amount = 5;
        pack.AddItem(bloody);

        var rock = world.CreateItem();
        rock.ItemType = ItemType.Normal;
        world.PlaceItem(rock, player.Position);

        client.HandleDoubleClick(bloody.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, rock.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(ItemType.BandageBlood, bloody.ItemType);
    }

    [Fact]
    public void Scissors_DoNotCleanBloodyBandages()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var pack = EquipBackpack(world, player);
        var scissors = world.CreateItem();
        scissors.ItemType = ItemType.Scissors;
        pack.AddItem(scissors);
        var bloody = world.CreateItem();
        bloody.ItemType = ItemType.BandageBlood;
        bloody.Amount = 5;
        pack.AddItem(bloody);

        client.HandleDoubleClick(scissors.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, bloody.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(ItemType.BandageBlood, bloody.ItemType);
        Assert.Equal(5, bloody.Amount);
    }

    [Fact]
    public void Scissors_MoveNeverCloth_IsNotConverted()
    {
        var (client, world, player) = Setup(PrivLevel.Player);
        var pack = EquipBackpack(world, player);
        var scissors = world.CreateItem();
        scissors.ItemType = ItemType.Scissors;
        pack.AddItem(scissors);

        var cloth = world.CreateItem();
        cloth.ItemType = ItemType.Cloth;
        cloth.SetAttr(ObjAttributes.Move_Never); // a placed cloth fixture
        world.PlaceItem(cloth, player.Position);

        client.HandleDoubleClick(scissors.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, cloth.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(ItemType.Cloth, cloth.ItemType); // fixture untouched
    }

    private static (GameClient client, GameWorld world, Character player) Setup(PrivLevel priv)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(lf);
        var state = TestHarness.CreateActiveNetState(lf, Random.Shared.Next(10_000, 20_000));
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Tester";
        player.PrivLevel = priv;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return (client, world, player);
    }

    private static Item EquipBackpack(GameWorld world, Character player)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        return pack;
    }
}
