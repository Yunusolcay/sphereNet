using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Which recipe a request names, and which tool pays for the work.
///
/// The recipe registry was keyed by the DISPLAY graphic, so two ITEMDEFs sharing an
/// art id were the same recipe: the second silently replaced the first, which then
/// vanished from its own skill's list and whose defname built the other item.
/// Source-X carries the ITEMDEF resource id through Skill_MakeItem and looks the
/// definition up with it directly (CCharSkill.cpp:870/679) - the identity is never
/// reduced to the graphic.
///
/// The tool had the twin of the ammunition defect: the check that PERMITTED the
/// craft refused to descend into a container it may not search, while the lookup
/// that picked the tool to WEAR descended anyway. A spare locked away therefore took
/// the damage owed by the tool in the crafter's hand. Source-X ContentFind skips an
/// unsearchable container (CContainer.cpp:236), and both halves now run one search.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CraftParity05ATests
{
    /// <summary>Two named definitions on ONE graphic, wanting different skills.</summary>
    private const string SharedGraphicScript = """
        [ITEMDEF i_review_first]
        ID=0f51
        NAME=First recipe
        SKILLMAKE=Tinkering 10.0

        [ITEMDEF i_review_second]
        ID=0f51
        NAME=Second recipe
        SKILLMAKE=Carpentry 20.0
        """;

    private const string ToolScript = """
        [TYPEDEFS]
        t_tinker_tools=171

        [ITEMDEF i_review_widget]
        ID=0f52
        NAME=Widget
        SKILLMAKE=Tinkering 0.0,t_tinker_tools
        """;

    private static (GameWorld World, CraftingEngine Engine, ResourceHolder Resources) Load(
        string script, int expectedRecipes)
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_05a_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);

        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(path) ?? ""
        };
        resources.LoadResourceFile(path);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var engine = new CraftingEngine(world);
        Assert.Equal(expectedRecipes, engine.LoadRecipesFromDefs(resources));
        return (world, engine, resources);
    }

    private static int DefId(ResourceHolder resources, string defname)
    {
        var rid = resources.ResolveDefName(defname);
        Assert.True(rid.IsValid, defname);
        return rid.Index;
    }

    // --- SX-05A-01: a recipe is its definition, not its picture --------------

    [Fact]
    public void TwoDefinitionsSharingAGraphicAreTwoRecipes()
    {
        var (_, engine, _) = Load(SharedGraphicScript, expectedRecipes: 2);

        Assert.Equal(2, engine.AllRecipes.Count);
    }

    [Fact]
    public void EachDefnameFindsItsOwnRecipe()
    {
        var (_, engine, resources) = Load(SharedGraphicScript, expectedRecipes: 2);

        var first = engine.TryGetRecipe(DefId(resources, "i_review_first"));
        var second = engine.TryGetRecipe(DefId(resources, "i_review_second"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(SkillType.Tinkering, first!.PrimarySkill);
        Assert.Equal(SkillType.Carpentry, second!.PrimarySkill);
    }

    [Fact]
    public void NeitherSkillLosesItsRecipeToTheOther()
    {
        // The overwrite took the first recipe off its own skill's craft list.
        var (_, engine, _) = Load(SharedGraphicScript, expectedRecipes: 2);

        Assert.Single(engine.GetRecipesBySkill(SkillType.Tinkering));
        Assert.Single(engine.GetRecipesBySkill(SkillType.Carpentry));
    }

    [Fact]
    public void TheLoadOrderDoesNotDecideTheWinner()
    {
        // Same two definitions, declared the other way round.
        var (_, engine, resources) = Load("""
            [ITEMDEF i_review_second]
            ID=0f51
            NAME=Second recipe
            SKILLMAKE=Carpentry 20.0

            [ITEMDEF i_review_first]
            ID=0f51
            NAME=First recipe
            SKILLMAKE=Tinkering 10.0
            """, expectedRecipes: 2);

        Assert.Equal(SkillType.Tinkering,
            engine.TryGetRecipe(DefId(resources, "i_review_first"))!.PrimarySkill);
        Assert.Equal(SkillType.Carpentry,
            engine.TryGetRecipe(DefId(resources, "i_review_second"))!.PrimarySkill);
    }

    [Fact]
    public void AnAmbiguousGraphicIsRefusedRatherThanGuessed()
    {
        // A caller holding only the art id has no answer coming when several
        // definitions wear it - the old code handed back whichever loaded last.
        var (_, engine, _) = Load(SharedGraphicScript, expectedRecipes: 2);

        Assert.Null(engine.TryGetRecipe(0x0F51));
    }

    [Fact]
    public void ALoneGraphicStillResolvesForOlderCallers()
    {
        var (_, engine, _) = Load(ToolScript, expectedRecipes: 1);

        var byGraphic = engine.TryGetRecipe(0x0F52);
        Assert.NotNull(byGraphic);
        Assert.Equal(SkillType.Tinkering, byGraphic!.PrimarySkill);
    }

    [Fact]
    public void ANumericDefinitionResolvesByItsOwnId()
    {
        var (_, engine, _) = Load("""
            [ITEMDEF 0f53]
            NAME=Numeric recipe
            SKILLMAKE=Tinkering 0.0
            """, expectedRecipes: 1);

        Assert.NotNull(engine.TryGetRecipe(0x0F53));
    }

    [Fact]
    public void AReloadLeavesNoStaleRecipeBehind()
    {
        var (_, engine, resources) = Load(SharedGraphicScript, expectedRecipes: 2);
        Assert.Equal(2, engine.LoadRecipesFromDefs(resources));
        Assert.Equal(2, engine.AllRecipes.Count);
        Assert.NotNull(engine.TryGetRecipe(DefId(resources, "i_review_first")));
    }

    // --- SX-05A-02: the tool that permits is the tool that wears -------------

    private sealed record Bench(GameWorld World, CraftingEngine Engine, Character Crafter,
        Item Pack, CraftRecipe Recipe);

    private static Bench ToolBench()
    {
        var (world, engine, _) = Load(ToolScript, expectedRecipes: 1);

        var crafter = world.CreateCharacter();
        crafter.IsPlayer = true;
        crafter.PrivLevel = PrivLevel.GM;        // removes the random craft failure only
        crafter.SetSkill(SkillType.Tinkering, 1000);
        world.PlaceCharacter(crafter, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        crafter.Equip(pack, Layer.Pack);

        var recipe = engine.TryGetRecipe(0x0F52)!;
        return new Bench(world, engine, crafter, pack, recipe);
    }

    private static Item Tool(GameWorld world, Item container)
    {
        var tool = world.CreateItem();
        tool.BaseId = 0x1EB8;
        tool.ItemType = ItemType.TinkerTools;
        tool.HitsCur = tool.HitsMax = 10;
        Assert.True(container.TryAddItem(tool));
        return tool;
    }

    private static Item Bag(GameWorld world, Item parent, bool locked)
    {
        var bag = world.CreateItem();
        bag.ItemType = locked ? ItemType.ContainerLocked : ItemType.Container;
        Assert.True(parent.TryAddItem(bag));
        return bag;
    }

    private static void WithCertainWear(Action body)
    {
        bool savedEnabled = CombatEngine.DurabilityEnabled;
        int savedChance = CombatEngine.DurabilityLossChance;
        int savedMin = CombatEngine.DurabilityLossMin;
        int savedMax = CombatEngine.DurabilityLossMax;
        try
        {
            CombatEngine.DurabilityEnabled = true;
            CombatEngine.DurabilityLossChance = 100;
            CombatEngine.DurabilityLossMin = 1;
            CombatEngine.DurabilityLossMax = 1;
            body();
        }
        finally
        {
            CombatEngine.DurabilityEnabled = savedEnabled;
            CombatEngine.DurabilityLossChance = savedChance;
            CombatEngine.DurabilityLossMin = savedMin;
            CombatEngine.DurabilityLossMax = savedMax;
        }
    }

    [Fact]
    public void ASpareLockedAwayIsNotTheToolThatWears()
    {
        WithCertainWear(() =>
        {
            var b = ToolBench();
            var locked = Tool(b.World, Bag(b.World, b.Pack, locked: true));
            var carried = Tool(b.World, b.Pack);

            Assert.NotNull(b.Engine.TryCraft(b.Crafter, b.Recipe));

            Assert.Equal(10, locked.HitsCur);
            Assert.Equal(9, carried.HitsCur);
        });
    }

    [Fact]
    public void AToolInAnOpenInnerBagIsStillUsable()
    {
        WithCertainWear(() =>
        {
            var b = ToolBench();
            var nested = Tool(b.World, Bag(b.World, b.Pack, locked: false));

            Assert.NotNull(b.Engine.TryCraft(b.Crafter, b.Recipe));
            Assert.Equal(9, nested.HitsCur);
        });
    }

    [Fact]
    public void ALockedToolAloneWillNotDoTheWork()
    {
        var b = ToolBench();
        var locked = Tool(b.World, Bag(b.World, b.Pack, locked: true));

        Assert.False(b.Engine.CanCraft(b.Crafter, b.Recipe));
        Assert.Null(b.Engine.TryCraft(b.Crafter, b.Recipe));
        Assert.Equal(10, locked.HitsCur);
    }

    [Fact]
    public void AWieldedToolKeepsItsPriority()
    {
        WithCertainWear(() =>
        {
            var b = ToolBench();
            var packed = Tool(b.World, b.Pack);
            var held = b.World.CreateItem();
            held.BaseId = 0x1EB8;
            held.ItemType = ItemType.TinkerTools;
            held.HitsCur = held.HitsMax = 10;
            b.Crafter.Equip(held, Layer.OneHanded);

            Assert.NotNull(b.Engine.TryCraft(b.Crafter, b.Recipe));

            Assert.Equal(9, held.HitsCur);
            Assert.Equal(10, packed.HitsCur);
        });
    }

    [Fact]
    public void NoToolWearsWhileDurabilityIsSwitchedOff()
    {
        bool saved = CombatEngine.DurabilityEnabled;
        try
        {
            CombatEngine.DurabilityEnabled = false;
            var b = ToolBench();
            var carried = Tool(b.World, b.Pack);

            Assert.NotNull(b.Engine.TryCraft(b.Crafter, b.Recipe));
            Assert.Equal(10, carried.HitsCur);
        }
        finally { CombatEngine.DurabilityEnabled = saved; }
    }
}
