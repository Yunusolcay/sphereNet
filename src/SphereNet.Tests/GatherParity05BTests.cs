using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

/// <summary>
/// What @ResourceGather is handed, and what the ore that comes out of the ground
/// has been through.
///
/// Source-X sets the arguments as Init(wAmount, 0, 0, pResBit) and seeds
/// LOCAL.ResourceID with the reap item (CCharSkill.cpp:1029): ARGN1 is the AMOUNT,
/// the object argument is the resource marker, and the item id travels in the
/// local, read back afterwards (:1044). SphereNet passed the ITEM ID as ARGN1 and
/// the amount as ARGN2, so a script halving a yield with ARGN1=2 produced four
/// copies of item id 2, and ARGN1=0 - which the reference consumes as "take
/// nothing" - was discarded as a zero and the full reap handed over anyway.
///
/// The reaped item is then built with CItem::CreateScript (:1050), which runs
/// GenerateScript and with it the ITEMDEF's @Create (CItem.cpp:404/415), the
/// amount being set only afterwards. SphereNet built it raw, so a resource whose
/// definition scripts its hue or tags in @Create came out of the ground bare.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class GatherParity05BTests
{
    private const ushort OreId = 0x19B9;
    private const ushort SubstituteOreId = 0x1BF2;
    private const int Pool = 10;
    private const int ReapAmount = 4;

    private sealed class Rig
    {
        public required GameWorld World { get; init; }
        public required GatheringEngine Engine { get; init; }
        public required Character Miner { get; init; }
        public int CreateCalls;
    }

    /// <param name="createBody">Lines for the ore's ITEMDEF @Create block.</param>
    /// <param name="gatherBody">Lines for the resource's @ResourceGather block.</param>
    /// <param name="onCreate">Native hook run alongside the scripted @Create.</param>
    private static Rig Setup(string createBody = "", string gatherBody = "",
        Action<Item>? onCreate = null)
    {
        var lf = LoggerFactory.Create(_ => { });

        string createBlock = string.IsNullOrWhiteSpace(createBody) ? "" : "ON=@Create\n" + createBody;
        string gatherBlock = string.IsNullOrWhiteSpace(gatherBody)
            ? "" : "ON=@ResourceGather\n" + gatherBody;

        string script = $"""
            [ITEMDEF 019b9]
            NAME=iron ore
            {createBlock}

            [ITEMDEF 01bf2]
            NAME=substitute ore

            [REGIONRESOURCE r_05b_ore]
            DEFNAME=r_05b_ore
            AMOUNT={Pool}
            REAP=0x19B9
            REAPAMOUNT={ReapAmount}
            SKILL=0.0
            {gatherBlock}

            [REGIONTYPE r_05b_rock t_rock]
            DEFNAME=r_05b_rock
            RESOURCES=100.0 r_05b_ore
            """;

        string path = Path.Combine(Path.GetTempPath(), $"sphnet_05b_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, script);

        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(path) ?? ""
        };
        resources.LoadResourceFile(path);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var interpreter = new ScriptInterpreter(new ExpressionParser(),
            lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
            runner.TryRunFunction(name, target, source, args, scope, out var r)
                ? r : TriggerResult.Default;
        interpreter.CallFunction = (name, target, source, args) =>
            runner.TryRunFunction(name, target, source, args, out var r)
                ? r : TriggerResult.Default;
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

        var world = TestHarness.CreateWorld();
        var miner = world.CreateCharacter();
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;

        var rig = new Rig
        {
            World = world,
            Engine = new GatheringEngine(world, dispatcher),
            Miner = miner,
        };

        // The reaped item now goes through the ordinary scripted-creation contract -
        // the same hook loot, spawns and crafting already use.
        Item.CreateTriggerHook = item =>
        {
            rig.CreateCalls++;
            dispatcher.FireItemTrigger(item, ItemTrigger.Create,
                new TriggerArgs { ItemSrc = item });
            onCreate?.Invoke(item);
        };

        return rig;
    }

    private static GatherResult Gather(Rig rig) =>
        rig.Engine.TryGatherForSink(rig.Miner, SkillType.Mining, new Point3D(100, 100, 0, 0));

    // --- SX-05B-01: ARGN1 is the amount -------------------------------------

    [Fact]
    public void AScriptHalvingTheYieldGetsTwoOfTheSameOre()
    {
        // ARGN1=2 means "take two", not "take four of item id 2".
        var result = Gather(Setup(gatherBody: "ARGN1=2"));

        Assert.True(result.Success);
        Assert.NotNull(result.Item);
        Assert.Equal(OreId, result.Item!.BaseId);
        Assert.Equal(2, result.Item.Amount);
    }

    [Fact]
    public void AScriptZeroingTheAmountGetsNothing()
    {
        // The reference consumes ARGN1 from the pool and yields no item when that
        // comes back zero or less; the old code read 0 as "unset" and paid in full.
        var result = Gather(Setup(gatherBody: "ARGN1=0"));

        Assert.False(result.Success);
        Assert.Null(result.Item);
    }

    [Fact]
    public void AScriptReturningOneCancelsTheReap()
    {
        var result = Gather(Setup(gatherBody: "RETURN 1"));

        Assert.False(result.Success);
        Assert.Null(result.Item);
    }

    [Fact]
    public void AScriptLeavingTheArgumentsAloneGetsTheDefinitionsReap()
    {
        var result = Gather(Setup(gatherBody: "LOCAL.UNTOUCHED=1"));

        Assert.True(result.Success);
        Assert.Equal(OreId, result.Item!.BaseId);
        Assert.Equal(ReapAmount, result.Item.Amount);
    }

    [Fact]
    public void TheItemIdTravelsInLocalResourceId()
    {
        // The id is read BACK from the local, so a script may substitute the ore
        // without touching the amount.
        var result = Gather(Setup(gatherBody: "LOCAL.RESOURCEID=0x1BF2"));

        Assert.True(result.Success);
        Assert.Equal(SubstituteOreId, result.Item!.BaseId);
        Assert.Equal(ReapAmount, result.Item.Amount);
    }

    [Fact]
    public void LocalResourceIdArrivesSeededWithTheDefinitionsReap()
    {
        // The script can read what it is about to get, which is what makes a
        // conditional substitution possible at all.
        var result = Gather(Setup(gatherBody:
            $"IF (<LOCAL.RESOURCEID> == {OreId})\nARGN1=1\nENDIF"));

        Assert.True(result.Success);
        Assert.Equal(1, result.Item!.Amount);
    }

    [Fact]
    public void AnAmountLargerThanThePoolIsClampedToIt()
    {
        var result = Gather(Setup(gatherBody: "ARGN1=999"));

        Assert.True(result.Success);
        Assert.Equal(Pool, result.Item!.Amount);
    }

    [Fact]
    public void ANegativeAmountYieldsNothing()
    {
        Assert.False(Gather(Setup(gatherBody: "ARGN1=-1")).Success);
    }

    // --- SX-05B-02: the ore has been through its own @Create ----------------

    [Fact]
    public void TheReapedOreHasRunItsDefinitionsCreateBlock()
    {
        var result = Gather(Setup(createBody: "COLOR=0455\nTAG.REVIEW_CREATED=1"));

        Assert.True(result.Success);
        Assert.Equal((ushort)0x0455, result.Item!.Hue);
        Assert.True(result.Item.TryGetTag("REVIEW_CREATED", out string? tag));
        Assert.Equal("1", tag);
    }

    [Fact]
    public void TheCreateBlockRunsExactlyOnce()
    {
        // FireCreateTrigger is instance-guarded, so the delivery path downstream
        // cannot re-run a body that would, e.g., re-roll a hue.
        var rig = Setup(createBody: "COLOR=0455");

        var result = Gather(rig);
        Assert.True(result.Success);
        result.Item!.FireCreateTrigger();

        Assert.Equal(1, rig.CreateCalls);
    }

    [Fact]
    public void TheAmountIsSetAfterTheCreateBlock()
    {
        // The reference sets the amount after CreateScript, so a @Create body has no
        // say in the yield.
        var result = Gather(Setup(createBody: "COLOR=0455", onCreate: item => item.Amount = 1));

        Assert.True(result.Success);
        Assert.Equal(ReapAmount, result.Item!.Amount);
    }

    [Fact]
    public void AResourceWithNoCreateBlockIsUnchanged()
    {
        var result = Gather(Setup());

        Assert.True(result.Success);
        Assert.Equal(OreId, result.Item!.BaseId);
        Assert.Equal(ReapAmount, result.Item.Amount);
        Assert.Equal((ushort)0, result.Item.Hue);
    }

    [Fact]
    public void AnOreItsOwnCreateBlockDeletesIsNotDelivered()
    {
        // A @Create body may destroy the item or swap it for another; a dead object
        // must not be reported as a successful gather.
        var rig = Setup(onCreate: item =>
        {
            item.Delete();
        });

        var result = Gather(rig);

        Assert.False(result.Success);
        Assert.Null(result.Item);
    }

    [Fact]
    public void TheSubstitutedOreAlsoRunsItsOwnCreateBlock()
    {
        // The Create that runs is the one belonging to the id the script chose.
        var rig = Setup(gatherBody: "LOCAL.RESOURCEID=0x1BF2");

        var result = Gather(rig);

        Assert.True(result.Success);
        Assert.Equal(SubstituteOreId, result.Item!.BaseId);
        Assert.Equal(1, rig.CreateCalls);
    }
}
