using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

public class DefinitionAndSpellRegressionTests
{
    private static ResourceHolder LoadScript(string contents)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_regression_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);

        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void DefinitionLoader_LoadsRegionDefinitionsAndHeaderFilter()
    {
        var resources = LoadScript("""
            [REGIONRESOURCE r_ore_iron]
            DEFNAME=r_ore_iron
            AMOUNT=3,5
            REAP=0x19B9
            REAPAMOUNT=1,2
            SKILL=1.0,100.0

            [REGIONTYPE r_default_rock t_rock]
            DEFNAME=r_default_rock
            RESOURCES=50.0 r_ore_iron
            """);

        var registry = new SpellRegistry();
        new DefinitionLoader(resources, registry).LoadAll();

        var regionTypeId = resources.ResolveDefName("r_default_rock");
        var regionResourceId = resources.ResolveDefName("r_ore_iron");
        var regionType = DefinitionLoader.GetRegionTypeDef(regionTypeId.Index);
        var regionResource = DefinitionLoader.GetRegionResourceDef(regionResourceId.Index);

        Assert.NotNull(regionType);
        Assert.Equal("t_rock", regionType.ItemTypeFilter);
        Assert.Single(regionType.Resources);
        Assert.NotNull(regionResource);
        Assert.Equal(3, regionResource.AmountMin);
        Assert.Equal(5, regionResource.AmountMax);
    }

    [Fact]
    public void DefinitionLoader_LoadAll_RebuildsSpellRegistry()
    {
        var registry = new SpellRegistry();
        var withSpell = LoadScript("""
            [SPELL 20]
            NAME=Teleport
            """);
        new DefinitionLoader(withSpell, registry).LoadAll();
        Assert.Equal(1, registry.Count);

        var withoutSpell = LoadScript("""
            [ITEMDEF 0f6c]
            NAME=Moongate
            """);
        new DefinitionLoader(withoutSpell, registry).LoadAll();

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void DefinitionLoader_PreservesRunesWithOrWithoutLeadingDot()
    {
        var resources = LoadScript("""
            [SPELL 5]
            NAME=Magic Arrow
            RUNES=In Por Ylem

            [SPELL 4]
            NAME=Heal
            RUNES=.In Mani
            """);

        var registry = new SpellRegistry();
        new DefinitionLoader(resources, registry).LoadAll();

        Assert.Equal("In Por Ylem", registry.Get(SpellType.MagicArrow)?.Runes);
        Assert.Equal("In Mani", registry.Get(SpellType.Heal)?.Runes);
    }

    [Fact]
    public void SpellEngine_Recall_UsesRuneItemTarget()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Recall,
            Flags = SpellFlag.TargObj,
            ManaCost = 0,
            CastTimeBase = 1
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var rune = world.CreateItem();
        rune.SetTag("RUNE_X", "200");
        rune.SetTag("RUNE_Y", "210");
        rune.SetTag("RUNE_Z", "5");
        rune.SetTag("RUNE_MAP", "0");
        world.PlaceItem(rune, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.Recall, rune.Uid, rune.Position) > 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(200, caster.X);
        Assert.Equal(210, caster.Y);
        Assert.Equal(5, caster.Z);
    }

    [Fact]
    public void SpellEngine_CastStart_EmitsPowerWords()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.MagicArrow,
            Flags = SpellFlag.TargChar,
            ManaCost = 0,
            CastTimeBase = 1
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        string? words = null;
        var engine = new SpellEngine(world, registry);
        engine.OnSpellWords = (_, text) => words = text;

        Assert.True(engine.CastStart(caster, SpellType.MagicArrow, target.Uid, target.Position) > 0);
        Assert.Equal("In Por Ylem", words);
    }

    [Fact]
    public void SpellEngine_CastDone_ClearsStateWhenManaWasSpentBeforeCompletion()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.MagicArrow,
            Flags = SpellFlag.TargChar,
            ManaCost = 10,
            CastTimeBase = 1
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 10;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.MagicArrow, target.Uid, target.Position) > 0);
        caster.Mana = 0;

        Assert.False(engine.CastDone(caster));
        Assert.False(caster.TryGetTag("SPELL_CASTING", out _));
    }
}
