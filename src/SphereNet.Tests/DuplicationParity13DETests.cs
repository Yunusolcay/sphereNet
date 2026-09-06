using System;
using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using Microsoft.Extensions.Logging;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Duplication: what a copy carries and where it lands (reviews 13D and 13E).
///
/// Source-X CItem::DupeCopy (CItem.cpp:4099) copies the object's own state; the
/// container override recreates every child as a copy of its own
/// (CItemContainer.cpp:830); CCSpawn::Copy carries the spawner's configuration but
/// never the children it has already produced (CCSpawn.cpp:1272); and CChar::DupeFrom
/// carries the status flags, stat block and EVENTS, then duplicates the equipped
/// layers (CChar.cpp:1092/1194). CIV_DUPE finishes with MoveNearObj(this,1), which
/// walks up to the TOP-LEVEL object and uses its world position (CObjBase.cpp:498) -
/// a copy is never pushed into the source's container. The count and the uid are
/// Sphere arguments read through the expression parser (CScript.cpp:154/161).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DuplicationParity13DETests
{
    private sealed class AdminConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
    }

    private static Item GroundItem(GameWorld world, int x = 100, ushort id = 0x0EED)
    {
        var item = world.CreateItem();
        item.BaseId = id;
        world.PlaceItem(item, new Point3D((short)x, 100, 0, 0));
        return item;
    }

    private static Item Container(GameWorld world)
    {
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = ItemType.Container;
        return box;
    }

    // ============================================================ 13D-1

    [Fact]
    public void AContainerCopyCarriesItsContents()
    {
        var world = TestHarness.CreateWorld();
        var box = Container(world);
        world.PlaceItem(box, new Point3D(100, 100, 0, 0));

        var loose = world.CreateItem();
        loose.BaseId = 0x0EED;
        loose.Name = "loose";
        box.AddItem(loose);

        var inner = Container(world);
        inner.Name = "inner";
        var deep = world.CreateItem();
        deep.BaseId = 0x0EED;
        deep.Name = "deep";
        inner.AddItem(deep);
        box.AddItem(inner);

        var copy = box.CreateDupe(world);

        Assert.Equal(2, copy.ContentCount);
        var copiedInner = Assert.Single(copy.Contents, c => c.Name == "inner");
        Assert.Equal("deep", Assert.Single(copiedInner.Contents).Name);

        // Independent objects, and the source keeps everything it had.
        Assert.Equal(2, box.ContentCount);
        Assert.DoesNotContain(copy.Contents, c => c.Uid == loose.Uid || c.Uid == inner.Uid);
        Assert.NotEqual(deep.Uid, copiedInner.Contents[0].Uid);
    }

    // ============================================================ 13D-2

    [Theory]
    [InlineData(ItemType.SpawnChar)]
    [InlineData(ItemType.SpawnItem)]
    public void ASpawnerCopyGetsAWorkingComponent(ItemType type)
    {
        var world = TestHarness.CreateWorld();
        var spawner = GroundItem(world);
        spawner.ItemType = type;
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { })
                .CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        spawner.InitializeSpawnComponent(world, resources);
        Assert.True(spawner.SpawnChar != null || spawner.SpawnItem != null);

        var copy = spawner.CreateDupe(world);

        Assert.Equal(type, copy.ItemType);
        if (type == ItemType.SpawnChar)
        {
            Assert.NotNull(copy.SpawnChar);
            // A component of its own, not the source's.
            Assert.NotSame(spawner.SpawnChar, copy.SpawnChar);
        }
        else
        {
            Assert.NotNull(copy.SpawnItem);
            Assert.NotSame(spawner.SpawnItem, copy.SpawnItem);
        }
    }

    // ============================================================ 13D-3 / 13D-4

    [Fact]
    public void ACharacterCopyCarriesItsEquipmentAndPack()
    {
        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var pack = Container(world);
        npc.Backpack = pack;
        npc.Equip(pack, Layer.Pack);
        var carried = world.CreateItem();
        carried.BaseId = 0x0EED;
        carried.Name = "carried";
        pack.AddItem(carried);

        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        npc.Equip(shirt, Layer.Shirt);

        var copy = npc.CreateDupe(world);

        var copiedShirt = copy.GetEquippedItem(Layer.Shirt);
        Assert.NotNull(copiedShirt);
        Assert.NotEqual(shirt.Uid, copiedShirt!.Uid);

        var copiedPack = copy.GetEquippedItem(Layer.Pack);
        Assert.NotNull(copiedPack);
        Assert.Same(copiedPack, copy.Backpack);
        Assert.Equal("carried", Assert.Single(copiedPack!.Contents).Name);

        // The source keeps its own.
        Assert.NotNull(npc.GetEquippedItem(Layer.Shirt));
        Assert.Single(pack.Contents);
    }

    [Fact]
    public void ACharacterCopyCarriesItsGameState()
    {
        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        npc.Name = "Special";
        npc.Fame = 1000;
        npc.Karma = -1000;
        npc.ResFire = 25;
        npc.SetStatFlag(StatFlag.Hidden);
        npc.Str = 80;
        npc.MaxHits = 75;
        npc.Hits = 75;
        npc.SetTag("KEPT", "37");
        npc.Events.Add(new ResourceId(ResType.Events, 4242));

        var copy = npc.CreateDupe(world);

        Assert.Equal("Special", copy.Name);
        Assert.Equal(1000, copy.Fame);
        Assert.Equal(-1000, copy.Karma);
        Assert.Equal(25, copy.ResFire);
        Assert.True(copy.IsStatFlag(StatFlag.Hidden));
        Assert.Equal(75, copy.Hits);
        Assert.Equal("37", copy.Tags.Get("KEPT"));
        Assert.Single(copy.Events);
    }

    // ============================================================ 13E-1

    [Fact]
    public void ACopyLandsBesideTheSourcesTopLevelObjectNotInsideItsContainer()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(120, 130, 0, 0));
        var pack = Container(world);
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);

        var source = world.CreateItem();
        source.BaseId = 0x0EED;
        pack.AddItem(source);
        source.Position = new Point3D(21, 35, 0, 0);   // container-local coordinates

        int packBefore = pack.ContentCount;
        source.TryExecuteCommand("DUPE", "1", new AdminConsole());

        // Not added to the pack...
        Assert.Equal(packBefore, pack.ContentCount);
        // ...and standing where the OWNER is, not at the container-local coordinate.
        var copy = world.FindItem(world.LastNewItem);
        Assert.NotNull(copy);
        Assert.Equal(owner.Position.X, copy!.Position.X);
        Assert.Equal(owner.Position.Y, copy.Position.Y);
    }

    [Fact]
    public void AFullContainerDoesNotSwallowOrLoseTheCopy()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(120, 130, 0, 0));
        var pack = Container(world);
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);

        var source = world.CreateItem();
        source.BaseId = 0x0EED;
        pack.AddItem(source);

        int itemsBefore = world.TotalItems;
        source.TryExecuteCommand("DUPE", "1", new AdminConsole());

        Assert.Equal(itemsBefore + 1, world.TotalItems);
        var copy = world.FindItem(world.LastNewItem);
        Assert.NotNull(copy);
        Assert.False(copy!.IsDeleted);
    }

    // ============================================================ 13E-3

    [Theory]
    [InlineData("2", 2)]
    [InlineData("1+1", 2)]
    [InlineData("010", 16)]   // leading zero is hex
    [InlineData("0A", 10)]
    public void TheDupeCountIsReadAsASphereArgument(string arg, int expected)
    {
        var world = TestHarness.CreateWorld();
        var source = GroundItem(world);

        int before = world.TotalItems;
        source.TryExecuteCommand("DUPE", arg, new AdminConsole());

        Assert.Equal(expected, world.TotalItems - before);
    }
}
