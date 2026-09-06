using System;
using System.IO;
using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 270 — plant growth (Source-X CItemPlant.cpp). A crop/foliage advances one
/// stage per growth-timer tick along its TDATA2 grow chain; at maturity (no TDATA2)
/// it drops a TDATA3 fruit and resets to the TDATA1 first stage, hidden as an
/// invisible regrow plot (ATTR_INVIS + dark-red staff marker) until the next tick
/// reveals it. A crop moved into a container dies. This is Source-X's crop-regrow
/// model, NOT the OSI plant-bowl minigame (which Source-X does not implement).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXWave270Tests
{
    private static string WriteScript(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_plant_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, contents);
        return path;
    }

    // Numeric TDATA chain (avoids needing the defname resolver):
    //   0c85 (stage 1) --TDATA2--> 3186 (stage 2, mature) --TDATA3--> 09d0 (fruit)
    //   3186 --TDATA1--> 0c85 (reset target)
    private static GameWorld LoadCropDefsAndWorld()
    {
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = WriteScript("""
            [ITEMDEF 0c85]
            NAME=test crop stage 1
            TYPE=t_crops
            TDATA2=03186

            [ITEMDEF 03186]
            NAME=test crop stage 2
            TYPE=t_crops
            TDATA1=0c85
            TDATA3=09d0

            [ITEMDEF 09d0]
            NAME=test fruit
            TYPE=t_fruit
            """);
        stack.Resources.LoadResourceFile(path);
        ScriptTestBootstrap.LoadDefinitions(stack.Resources);

        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item PlaceCrop(GameWorld world, ushort baseId)
    {
        var crop = world.CreateItem();
        crop.BaseId = baseId;
        crop.ItemType = ItemType.Crops;
        world.PlaceItem(crop, new Point3D(100, 100, 0, 0));
        crop.PlantStartGrowth();
        return crop;
    }

    [Fact]
    public void PlantOnTick_AdvancesAlongGrowthChain()
    {
        var world = LoadCropDefsAndWorld();
        var crop = PlaceCrop(world, 0x0C85);

        crop.PlantOnTick(); // stage 1 → stage 2 via TDATA2

        Assert.Equal((ushort)0x3186, crop.BaseId);
        Assert.False(crop.IsAttr(ObjAttributes.Invis));
    }

    [Fact]
    public void PlantOnTick_MatureStage_DropsFruitAndResetsAsHiddenRegrow()
    {
        var world = LoadCropDefsAndWorld();
        var crop = PlaceCrop(world, 0x3186); // start at the mature stage

        // The produce is counted by its own graphic, not by a forced Food type: a
        // plant yields what its definition says (Source-X CreateScript,
        // CItemPlant.cpp:65), and this fixture's fruit is t_fruit.
        int fruitBefore = world.GetItemsInRange(crop.Position, 2)
            .Count(i => i.BaseId == 0x09D0);

        crop.PlantOnTick();

        int fruitAfter = world.GetItemsInRange(new Point3D(100, 100, 0, 0), 2)
            .Count(i => i.BaseId == 0x09D0);
        Assert.True(fruitAfter > fruitBefore, "a fruit should drop on the ground");
        Assert.Equal((ushort)0x0C85, crop.BaseId);           // reset to stage 1
        Assert.True(crop.IsAttr(ObjAttributes.Invis));       // hidden regrow plot
    }

    [Fact]
    public void PlantOnTick_HiddenRegrowStage_Reappears()
    {
        var world = LoadCropDefsAndWorld();
        var crop = PlaceCrop(world, 0x0C85);
        crop.SetAttr(ObjAttributes.Invis);

        crop.PlantOnTick();

        Assert.False(crop.IsAttr(ObjAttributes.Invis)); // becomes visible again
    }

    [Fact]
    public void PlantOnTick_InsideContainer_Dies()
    {
        var world = LoadCropDefsAndWorld();
        var crop = PlaceCrop(world, 0x0C85);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.AddItem(crop); // not top-level anymore

        crop.PlantOnTick();

        Assert.True(crop.IsDeleted);
    }
}
