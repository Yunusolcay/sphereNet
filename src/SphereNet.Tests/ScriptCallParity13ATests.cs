using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The script call chain: CALL, TRY and ARGN assignment (review 13A).
///
/// Source-X Execute_Call (CScriptObj.cpp:1505) resolves an object reference on the
/// argument first, then either passes the caller's args object through untouched (no
/// argument) or re-Inits it from the new argument and restores it afterwards. Either
/// way it is the SAME CScriptTriggerArgs, and the LOCAL pool lives on that object, so
/// CALL shares locals while an ordinary function verb line - which builds a fresh args
/// object (CObjBase.cpp:2138) - does not. TRY hands the rest of the line to the
/// target's full r_Verb (CObjBase.cpp:2899), and an ARGN assignment goes through the
/// expression parser (CScriptTriggerArgs.cpp:313).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptCallParity13ATests
{
    private sealed class AdminConsole(Character? sourceChar = null) : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
        public IScriptObj? GetSourceChar() => sourceChar;
    }

    private sealed class Harness : IDisposable
    {
        public ScriptInterpreter Interpreter { get; }
        public TriggerRunner Runner { get; }
        private readonly string _path;

        public Harness(string scriptText)
        {
            var lf = LoggerFactory.Create(_ => { });
            var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
            _path = Path.Combine(Path.GetTempPath(), $"sphnet_13a_{Guid.NewGuid():N}.scp");
            File.WriteAllText(_path, scriptText);
            resources.LoadResourceFile(_path);

            Interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
            Runner = new TriggerRunner(Interpreter, resources, lf.CreateLogger<TriggerRunner>());
            Interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
                Runner.TryRunFunction(name, target, source, args, scope, out var r) ? r : TriggerResult.Default;
            Interpreter.CallFunction = (name, target, source, args) =>
                Runner.TryRunFunction(name, target, source, args, out var r) ? r : TriggerResult.Default;
            Interpreter.FunctionLookup = Runner.HasFunction;
            Interpreter.ResolveObjectRef = (obj, head) => obj is ObjBase o ? o.ResolveRefHead(head) : null;
        }

        public void Dispose() => File.Delete(_path);
    }

    private static Item GroundItem(SphereNet.Game.World.GameWorld world, int x = 100)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D((short)x, 100, 0, 0));
        return item;
    }

    // ============================================================ 13A-1
    // CALL prepares ARGN/ARGS from its own argument, and an argument-less CALL leaves
    // the caller's args alone rather than blanking them.

    [Fact]
    public void CallWithAnArgumentPreparesBothArgnAndArgs()
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nTAG.N1=<ARGN1>\nTAG.ARGS=<ARGS>\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nCALL f_child 37\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var args = new TriggerArgs { Number1 = 17 };
        args.ArgString = "old";

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));

        Assert.True(item.TryGetProperty("TAG.N1", out string n1));
        Assert.Equal("37", n1);                     // not the caller's 17
        Assert.True(item.TryGetProperty("TAG.ARGS", out string a));
        Assert.Equal("37", a);
    }

    [Fact]
    public void CallWithoutAnArgumentPassesTheCallersArgsThrough()
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nTAG.N1=<ARGN1>\nTAG.ARGS=<ARGS>\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nCALL f_child\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var args = new TriggerArgs { Number1 = 17 };
        args.ArgString = "old";

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));

        Assert.True(item.TryGetProperty("TAG.N1", out string n1));
        Assert.Equal("17", n1);
        Assert.True(item.TryGetProperty("TAG.ARGS", out string a));
        Assert.Equal("old", a);                     // ARGS survives, it is not cleared
    }

    [Fact]
    public void TheCallersArgsAreRestoredAfterAnArgumentedCall()
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nCALL f_child 37\nTAG.AFTER=<ARGN1>\nTAG.AFTERARGS=<ARGS>\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var args = new TriggerArgs { Number1 = 17 };
        args.ArgString = "old";

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));

        Assert.True(item.TryGetProperty("TAG.AFTER", out string after));
        Assert.Equal("17", after);
        Assert.True(item.TryGetProperty("TAG.AFTERARGS", out string afterArgs));
        Assert.Equal("old", afterArgs);
        Assert.Equal(17L, args.Number1);            // ...on the args object itself too
        Assert.Equal("old", args.ArgString);
    }

    [Fact]
    public void APlainFunctionLinePreparesArgnFromItsOwnArgument()
    {
        using var h = new Harness(
            "[FUNCTION f_child]\nTAG.N1=<ARGN1>\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nf_child 37\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        var args = new TriggerArgs { Number1 = 17 };

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, args, out _));

        Assert.True(item.TryGetProperty("TAG.N1", out string n1));
        Assert.Equal("37", n1);
    }

    // ============================================================ 13A-3
    // A reference head redirects the call before the name is looked up.

    [Theory]
    [InlineData("TOPOBJ")]
    [InlineData("CONT")]
    [InlineData("LINK")]
    public void CallResolvesAnObjectReferenceHeadBeforeTheFunctionName(string head)
    {
        using var h = new Harness(
            "[FUNCTION f_mark]\nTAG.MARK=<ARGN1>\nRETURN 1\n\n" +
            $"[FUNCTION f_parent]\nCALL {head}.f_mark 37\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        pack.AddItem(item);
        item.Link = owner.Uid;

        ObjBase expected = head == "CONT" ? pack : owner;

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null, new TriggerArgs(), out _));

        Assert.True(expected.TryGetProperty("TAG.MARK", out string mark));
        Assert.Equal("37", mark);
        Assert.False(item.TryGetProperty("TAG.MARK", out string own) && own == "37");
    }

    [Fact]
    public void CallResolvesSrcBeforeTheFunctionName()
    {
        using var h = new Harness(
            "[FUNCTION f_mark]\nTAG.MARK=1\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nCALL SRC.f_mark\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var player = world.CreateCharacter();
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null,
            new TriggerArgs { Source = player }, out _));

        Assert.True(player.TryGetProperty("TAG.MARK", out string mark));
        Assert.Equal("1", mark);
    }

    // ============================================================ 13A-4
    // TRY runs the rest of the line through the target's ordinary verb path.

    [Fact]
    public void TryReachesAPropertyWrittenWithoutAnEquals()
    {
        using var h = new Harness("[FUNCTION f_parent]\nTRY TAG.FLAG 1\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, new AdminConsole(), new TriggerArgs(), out _));

        Assert.True(item.TryGetProperty("TAG.FLAG", out string flag));
        Assert.Equal("1", flag);
    }

    [Fact]
    public void TryReachesAScriptFunction()
    {
        using var h = new Harness(
            "[FUNCTION f_mark]\nTAG.MARK=1\nRETURN 1\n\n" +
            "[FUNCTION f_parent]\nTRY f_mark\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, new AdminConsole(), new TriggerArgs(), out _));

        Assert.True(item.TryGetProperty("TAG.MARK", out string mark));
        Assert.Equal("1", mark);
    }

    [Fact]
    public void TryStillReachesAnEngineVerb()
    {
        using var h = new Harness("[FUNCTION f_parent]\nTRY REMOVE\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, new AdminConsole(), new TriggerArgs(), out _));

        Assert.True(item.IsDeleted);
    }

    // ============================================================ 13A-5
    // ARGN assignment is an expression, not a narrow int parse.

    [Theory]
    [InlineData("16", "16")]
    [InlineData("010", "16")]     // leading zero is hex
    [InlineData("1+1", "2")]      // arithmetic, not a parse failure
    [InlineData("<EVAL 1+1>", "2")]
    public void ArgnAssignmentGoesThroughTheExpressionParser(string assigned, string expected)
    {
        using var h = new Harness(
            $"[FUNCTION f_parent]\nARGN1={assigned}\nTAG.RESULT=<ARGN1>\nRETURN 1\n");

        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        Assert.True(h.Runner.TryRunFunction("f_parent", item, null,
            new TriggerArgs { Number1 = 17 }, out _));

        Assert.True(item.TryGetProperty("TAG.RESULT", out string result));
        Assert.Equal(expected, result);
    }
}
