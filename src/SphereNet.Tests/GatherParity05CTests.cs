using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// How long a resource node lives, and what happens to it while it does.
///
/// REGEN is a value curve in TENTHS of a second - the reference says so in the
/// loader itself ("Tenths of second once found how long to regen this type",
/// CRegionResourceDef.cpp:73) and turns a sample into the node's timeout with
/// GetRandom() * MSECS_PER_TENTH (CWorldMap.cpp:148). SphereNet read it as a
/// single whole-second integer, so every vein lasted ten times as long as its
/// script asked, and a comma-separated curve collapsed to zero and fell through
/// to an invented ten-hour default.
///
/// The node's life is also a single window. Source-X returns an existing resource
/// bit exactly as it found it (CWorldMap.cpp:71) and calls MoveToDecay once, at
/// creation (:148); consuming from it decrements the amount (CCharSkill.cpp:1046)
/// and leaves the timer alone, and a node at zero is spent (:1456). SphereNet
/// topped the pool up by elapsed time and pushed the deadline out on every touch,
/// which let a player keep one vein alive indefinitely. That behaviour was landed
/// as a parity claim - "Source-X gradual vein regrow" - which the reference does
/// not support, so it is removed rather than kept as a deliberate deviation.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GatherParity05CTests
{
    // --- SX-05C-01: REGEN is a curve, in tenths ------------------------------

    private static RegionResourceDef Def(string regen)
    {
        var def = new RegionResourceDef(default);
        def.LoadFromKey("REGEN", regen);
        return def;
    }

    [Fact]
    public void ASingleValueRegenIsReadAsTenths()
    {
        // 100 tenths = 10 seconds, not 100.
        var def = Def("100");

        Assert.Equal(100, def.Regen);
        Assert.Equal(100, def.GetRandomRegen(new FixedRandom(0)));
    }

    [Fact]
    public void TheReferencePacksOwnExpressionMeansAnHour()
    {
        // sphere_region.scp writes REGEN=60*60*10, which is an hour in tenths.
        var def = Def("60*60*10");

        Assert.Equal(36_000, def.Regen);
        Assert.Equal(3_600_000L, def.GetRandomRegen(new FixedRandom(0)) * 100L);
    }

    [Theory]
    [InlineData(0, 100)]        // bottom of the curve
    [InlineData(999, 199)]      // top of it: the 0..999 sample spans the range the
                                // same way AMOUNT=10,30 answers 29, not 30
    public void ACommaSeparatedRegenIsACurve(int sample, int expected)
    {
        // This used to parse as one expression, collapse to zero, and fall through
        // to the invented ten-hour default.
        var def = Def("100,200");

        Assert.Equal(100, def.Regen);
        Assert.Equal(200, def.RegenMax);
        Assert.Equal(expected, def.GetRandomRegen(new FixedRandom(sample)));
    }

    [Fact]
    public void ACurveOfExpressionsIsParsedPointByPoint()
    {
        var def = Def("60*10,60*20");

        Assert.Equal(600, def.Regen);
        Assert.Equal(1200, def.RegenMax);
    }

    [Fact]
    public void AnUnsetRegenSamplesZero()
    {
        // The reference samples an empty curve as zero and decays the node almost
        // at once; no arbitrary default is invented in its place.
        Assert.Equal(0, new RegionResourceDef(default).GetRandomRegen(new FixedRandom(500)));
    }

    // --- SX-05C-02: one node, one window ------------------------------------

    private const int Pool = 10;

    private sealed record Rig(GameWorld World, GatheringEngine Engine, Character Miner);

    private static Rig Setup(string regen)
    {
        var lf = LoggerFactory.Create(_ => { });
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_05c_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, $"""
            [ITEMDEF 019b9]
            NAME=iron ore

            [REGIONRESOURCE r_05c_ore]
            DEFNAME=r_05c_ore
            AMOUNT={Pool}
            REAP=0x19B9
            REAPAMOUNT=1
            SKILL=0.0
            REGEN={regen}

            [REGIONTYPE r_05c_rock t_rock]
            DEFNAME=r_05c_rock
            RESOURCES=100.0 r_05c_ore
            """);

        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(path) ?? ""
        };
        resources.LoadResourceFile(path);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        var miner = world.CreateCharacter();
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        return new Rig(world, new GatheringEngine(world), miner);
    }

    private static readonly Point3D Tile = new(100, 100, 0, 0);

    private static GatherResult Gather(Rig rig) =>
        rig.Engine.TryGatherForSink(rig.Miner, SkillType.Mining, Tile);

    private static Item Marker(Rig rig) =>
        rig.World.GetItemsInRange(Tile, 0)
            .Single(i => i.BaseId == GatheringEngine.MarkerGraphic);

    [Fact]
    public void TheNodesLifetimeComesFromRegenInTenths()
    {
        // REGEN=100 -> 10 seconds, where the whole-second reading gave 100.
        var rig = Setup("100");
        Assert.True(Gather(rig).Success);

        long remaining = Marker(rig).DecayTime - Environment.TickCount64;
        Assert.InRange(remaining, 8_000, 10_000);
    }

    [Fact]
    public void WorkingAVeinDoesNotExtendItsLife()
    {
        // Every gather used to push the deadline a full regen period out, so a
        // player could keep one vein alive for as long as they kept mining it.
        var rig = Setup("100");
        Assert.True(Gather(rig).Success);

        // A sentinel rather than the real deadline: two gathers land in the same
        // millisecond, so re-arming to "now + lifetime" twice would read as
        // unchanged and hide the defect.
        const long Sentinel = 12_345;
        Marker(rig).DecayTime = Sentinel;

        Assert.True(Gather(rig).Success);

        Assert.Equal(Sentinel, Marker(rig).DecayTime);
    }

    [Fact]
    public void EachGatherStillTakesFromThePool()
    {
        var rig = Setup("100");
        Assert.True(Gather(rig).Success);
        Assert.Equal(Pool - 1, GatheringEngine.GetPool(Marker(rig)));

        Assert.True(Gather(rig).Success);
        Assert.Equal(Pool - 2, GatheringEngine.GetPool(Marker(rig)));
    }

    [Fact]
    public void ASpentNodeStaysSpentUntilItsWindowRunsOut()
    {
        // No top-up by elapsed time: an emptied vein yields nothing more, and the
        // retry does not move its deadline either.
        var rig = Setup("100");
        for (int i = 0; i < Pool; i++)
            Assert.True(Gather(rig).Success);

        const long Sentinel = 23_456;
        Marker(rig).DecayTime = Sentinel;

        var spent = Gather(rig);
        Assert.False(spent.Success);
        Assert.True(spent.Depleted);
        Assert.Equal(0, GatheringEngine.GetPool(Marker(rig)));
        Assert.Equal(Sentinel, Marker(rig).DecayTime);
    }

    [Fact]
    public void OnceTheNodeIsGoneTheNextSearchRollsAFreshOne()
    {
        var rig = Setup("100");
        for (int i = 0; i < Pool; i++)
            Assert.True(Gather(rig).Success);
        Assert.True(Gather(rig).Depleted);

        // What the decay tick does when the window closes.
        var spentMarker = Marker(rig);
        rig.World.RemoveItem(spentMarker);

        var result = Gather(rig);
        Assert.True(result.Success);
        Assert.Equal(Pool - 1, GatheringEngine.GetPool(Marker(rig)));
    }

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => Math.Clamp(value, 0, maxValue - 1);
    }
}
