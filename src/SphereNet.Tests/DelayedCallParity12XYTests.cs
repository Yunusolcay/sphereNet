using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The delayed-call boundaries reported in review 12X and 12Y.
///
/// Source-X keeps a delayed command as ONE raw string and only splits it when the
/// timer fires: CTimedFunction::OnTick builds a CScript (CTimedFunction.cpp:88),
/// CScriptKeyAlloc::ParseKey runs Str_Parse over it (CScript.cpp:336), and
/// Str_Parse's default separators are "=, \t" (CExpression.cpp:144). The line then
/// goes through CObjBase::r_Verb: the verb table owns its names outright, an unknown
/// name reaches the script [FUNCTION] (CObjBase.cpp:2138) and, failing that, becomes
/// a property assignment through CScriptObj::r_Verb's default branch
/// (CScriptObj.cpp:1481). SRC is the top-level CHARACTER, with no connected-client
/// requirement (CTimedFunction.cpp:43). Due order is global across every timed object
/// (CWorldTicker.cpp:1051), and ARGN1/2/3 are int64 (CScriptTriggerArgs.h:21) read by
/// CExpression::GetSingle, whose hex path treats a leading '0' as a marker and then
/// consumes [0-9 A-F a-f] (CExpression.cpp:666).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DelayedCallParity12XYTests
{
    private sealed class AdminConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
    }

    private static DelayedCallDispatcher NewDispatcher(TriggerRunner? runner = null) =>
        new(() => runner, null, new AdminConsole());

    private static TriggerRunner NewRunner(string scriptText, out string scriptPath)
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_12xy_{Guid.NewGuid():N}.scp");
        File.WriteAllText(scriptPath, scriptText);
        resources.LoadResourceFile(scriptPath);
        return new TriggerRunner(
            new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>()),
            resources, lf.CreateLogger<TriggerRunner>());
    }

    private static Item GroundItem(SphereNet.Game.World.GameWorld world, int x = 100)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D((short)x, 100, 0, 0));
        return item;
    }

    // ============================================================ 12X-1
    // Due order is GLOBAL, not per-object: upstream ticks one time-sorted buffer that
    // every timed object shares (CWorldTicker.cpp:1051 selection, :1129 execution).

    [Theory]
    [InlineData(true)]   // the early object joins the active set first
    [InlineData(false)]  // ...and second: the running order must not change
    public void JobsOnDifferentObjectsRunInDueOrder(bool earlyRegistersFirst)
    {
        var world = TestHarness.CreateWorld();
        var early = GroundItem(world, 100);
        var late = GroundItem(world, 102);

        if (earlyRegistersFirst)
        {
            early.AddTimerF(100, "f_early", "");
            late.AddTimerF(10_000, "f_late", "");
        }
        else
        {
            late.AddTimerF(10_000, "f_late", "");
            early.AddTimerF(100, "f_early", "");
        }

        var seen = new List<string>();
        world.TimerFExpired = (_, entry) => seen.Add(entry.FunctionName);

        // Past both due times, so the only thing that can order them is the due time.
        TestHarness.PumpTimerF(world, Environment.TickCount64 + 20_000);

        Assert.Equal(["f_early", "f_late"], seen);
    }

    [Fact]
    public void JobsSharingADueTimeKeepTheOrderTheyWereQueuedIn()
    {
        var world = TestHarness.CreateWorld();
        var a = GroundItem(world, 100);
        var b = GroundItem(world, 102);
        a.AddTimerF(0, "f_first", "");
        b.AddTimerF(0, "f_second", "");

        var seen = new List<string>();
        world.TimerFExpired = (_, entry) => seen.Add(entry.FunctionName);
        TestHarness.PumpTimerF(world, Environment.TickCount64 + 1000);

        Assert.Equal(["f_first", "f_second"], seen);
    }

    // ============================================================ 12X-2
    // Neither a verb nor a function: the line is a property assignment
    // (CScriptObj.cpp:1481 default -> r_LoadVal).

    [Fact]
    public void ADelayedLineNoVerbOrFunctionOwnsAssignsTheProperty()
    {
        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);
        item.Name = "before";

        NewDispatcher().Run(item, "NAME", "after");

        Assert.Equal("after", item.Name);
    }

    [Fact]
    public void ADelayedTagAssignmentReachesTheTag()
    {
        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        NewDispatcher().Run(item, "TAG.FLAG", "after");

        Assert.True(item.TryGetProperty("TAG.FLAG", out string flag));
        Assert.Equal("after", flag);
    }

    // ============================================================ 12X-3 / 12Y-5
    // "=" is an argument separator, so f_capture=37 is the function f_capture with
    // argument 37 - live (ScheduleTimerF) and on the classic load path alike.

    [Theory]
    [InlineData("0, f_capture 37")]
    [InlineData("0, f_capture=37")]
    [InlineData("0, f_capture,37")]
    public void EveryArgumentSeparatorSplitsTheDelayedPayload(string timerFArgs)
    {
        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        item.TryExecuteCommand("TIMERFMS", timerFArgs, new AdminConsole());

        var job = Assert.Single(item.TimerFEntries);
        Assert.Equal("f_capture", job.FunctionName);
        Assert.Equal("37", job.Args);
    }

    [Fact]
    public void AQuotedDelayedArgumentIsNotSplitInsideItsQuotes()
    {
        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        item.TryExecuteCommand("TIMERFMS", "0, f_capture \"a b=c\"", new AdminConsole());

        var job = Assert.Single(item.TimerFEntries);
        Assert.Equal("f_capture", job.FunctionName);
        Assert.Equal("\"a b=c\"", job.Args);
    }

    [Theory]
    [InlineData("f_mark 37")]
    [InlineData("f_mark=37")]
    public void AClassicSavedCallSplitsOnTheSameSeparators(string savedCall)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_12xy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using (var w = SaveIO.OpenWriter(Path.Combine(dir, "sphereworld.scp"), SaveFormat.Text))
            {
                w.BeginRecord("WORLDITEM");
                w.WriteProperty("SERIAL", "040000001");
                w.WriteProperty("ID", "0EED");
                w.WriteProperty("P", "1000,1000,0,0");
                w.EndRecord();
            }
            using (var w = SaveIO.OpenWriter(Path.Combine(dir, "spheredata.scp"), SaveFormat.Text))
            {
                w.BeginRecord("TIMERF");
                w.WriteProperty("TimerFCall", savedCall);
                w.WriteProperty("TimerFNumbers", $"99,{0x40000001},60000");
                w.EndRecord();
            }

            var world = TestHarness.CreateWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(world, dir);

            var job = Assert.Single(world.FindItem(new Serial(0x40000001))!.TimerFEntries);
            Assert.Equal("f_mark", job.FunctionName);
            Assert.Equal("37", job.Args);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ============================================================ 12X-4
    // The hex path consumes the leading '0' as a MARKER and then every hex digit, so a
    // letter neither zeroes the value nor strands the cursor on the rest of the line.

    [Theory]
    [InlineData("37,2,3", 37L, 2L, 3L)]
    [InlineData("010,2,3", 16L, 2L, 3L)]
    [InlineData("0A,2,3", 10L, 2L, 3L)]
    [InlineData("01F,2,3", 31L, 2L, 3L)]
    [InlineData("0ff,2,3", 255L, 2L, 3L)]
    [InlineData("0,2,3", 0L, 2L, 3L)]
    public void HexArgumentsReadWholeAndDoNotStrandTheFollowingNumbers(
        string raw, long n1, long n2, long n3)
    {
        var args = new SphereNet.Scripting.Execution.TriggerArgs();
        args.InitFromRaw(raw);

        Assert.Equal(n1, args.Number1);
        Assert.Equal(n2, args.Number2);
        Assert.Equal(n3, args.Number3);
    }

    [Fact]
    public void AHexArgumentReachesTheDelayedFunction()
    {
        var runner = NewRunner("[FUNCTION f_capture]\nTAG.N1=<ARGN1>\nRETURN 1\n", out string scp);
        try
        {
            var world = TestHarness.CreateWorld();
            var item = GroundItem(world);

            NewDispatcher(runner).Run(item, "f_capture", "0A,2,3");

            Assert.True(item.TryGetProperty("TAG.N1", out string n1));
            Assert.Equal("10", n1);
        }
        finally { File.Delete(scp); }
    }

    // ============================================================ 12X-5
    // ARGN1/2/3 travel as int64 (CScriptTriggerArgs.h:21); they used to saturate at
    // the 32-bit bounds on the way in.

    [Theory]
    [InlineData("2147483647,2,3", 2147483647L)]
    [InlineData("2147483648,2,3", 2147483648L)]
    [InlineData("-2147483649,2,3", -2147483649L)]
    [InlineData("9007199254740993,2,3", 9007199254740993L)]
    public void LargeNumericArgumentsSurviveAsSixtyFourBit(string raw, long expected)
    {
        var args = new SphereNet.Scripting.Execution.TriggerArgs();
        args.InitFromRaw(raw);

        Assert.Equal(expected, args.Number1);
        Assert.Equal(2L, args.Number2);   // the wider first value must not eat the rest
        Assert.Equal(3L, args.Number3);
    }

    // ============================================================ 12Y-1
    // SRC is the top-level CHARACTER whether or not a client is attached
    // (CTimedFunction.cpp:43), and CItem::r_Verb reaches it through pSrc->GetChar()
    // (CItem.cpp:3574).

    [Fact]
    public void ADelayedUnequipWorksForACharacterWithNoClient()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);

        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        owner.Equip(shirt, Layer.Shirt);
        Assert.True(shirt.IsEquipped);

        // No client resolver: the console stands in for the character itself.
        NewDispatcher().Run(shirt, "UNEQUIP", "");

        Assert.False(shirt.IsEquipped);
        Assert.Equal(pack.Uid, shirt.ContainedIn);
    }

    [Fact]
    public void ACharacterConsoleReportsTheCharacterItSpeaksFor()
    {
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

        var (src, console) = NewDispatcher().ResolveCaller(item);

        Assert.Same(owner, src);
        Assert.Same(owner, console.GetSourceChar());
    }

    // ============================================================ 12Y-2
    // A verb that OWNS the name and declines is the final answer; a like-named script
    // function is not tried (CObjBase.cpp:2134 - the function branch is reached only
    // when the table lookup misses).

    [Fact]
    public void ARefusedEngineVerbDoesNotFallThroughToALikeNamedFunction()
    {
        var runner = NewRunner("[FUNCTION UNEQUIP]\nTAG.SHADOW=1\nRETURN 1\n", out string scp);
        try
        {
            var world = TestHarness.CreateWorld();
            var item = GroundItem(world);   // on the ground: UNEQUIP has no character

            // No client resolver and no owner, so the console is the server: the verb
            // owns the name, refuses, and that is the end of the line.
            NewDispatcher(runner).Run(item, "UNEQUIP", "");

            Assert.False(item.TryGetProperty("TAG.SHADOW", out string s) && s == "1");
        }
        finally { File.Delete(scp); }
    }

    [Fact]
    public void AnUnknownNameStillReachesTheFunctionAndThenTheProperty()
    {
        var runner = NewRunner("[FUNCTION f_only]\nTAG.RAN=1\nRETURN 1\n", out string scp);
        try
        {
            var world = TestHarness.CreateWorld();
            var item = GroundItem(world);

            NewDispatcher(runner).Run(item, "f_only", "");
            Assert.True(item.TryGetProperty("TAG.RAN", out string ran) && ran == "1");

            // ...and a name neither owns still lands on the property.
            NewDispatcher(runner).Run(item, "NAME", "renamed");
            Assert.Equal("renamed", item.Name);
        }
        finally { File.Delete(scp); }
    }

    // ============================================================ 12Y-3
    // A TOPOBJ./CONT./LINK. head sends the REST of the line to the resolved object's
    // own r_Verb, function step included (CScriptObj.cpp:1217).

    [Theory]
    [InlineData("TOPOBJ")]
    [InlineData("CONT")]
    [InlineData("LINK")]
    public void AReferenceChainRunsTheScriptFunctionOnTheResolvedTarget(string head)
    {
        var runner = NewRunner("[FUNCTION f_mark]\nTAG.MARK=<ARGN1>\nRETURN 1\n", out string scp);
        var previousHook = ObjBase.RunScriptFunction;
        ObjBase.RunScriptFunction = (obj, name, args, console) =>
        {
            var fnArgs = new SphereNet.Scripting.Execution.TriggerArgs();
            fnArgs.InitFromRaw(args);
            return runner.TryRunFunction(name, obj, console, fnArgs, out _);
        };
        try
        {
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

            item.TryExecuteCommand($"{head}.f_mark", "37", new AdminConsole());

            Assert.True(expected.TryGetProperty("TAG.MARK", out string mark));
            Assert.Equal("37", mark);
            // ...and not on the item that carried the line.
            Assert.False(item.TryGetProperty("TAG.MARK", out string own) && own == "37");
        }
        finally
        {
            ObjBase.RunScriptFunction = previousHook;
            File.Delete(scp);
        }
    }

    // ============================================================ 12Y-4
    // A save is authoritative: the live scheduling cap is a SphereNet guard with no
    // upstream counterpart (CTimedFunctionHandler.cpp:103), so restoring must not
    // silently lose jobs - and the live path must report a refusal rather than
    // pretending it queued the work.

    [Fact]
    public void TheLiveSchedulingCapReportsARefusalInsteadOfDroppingSilently()
    {
        var world = TestHarness.CreateWorld();
        var item = GroundItem(world);

        for (int i = 0; i < 64; i++)
            Assert.True(item.AddTimerF(60_000, $"f_{i}", ""));

        Assert.False(item.AddTimerF(60_000, "f_64", ""));
        Assert.Equal(64, item.TimerFEntries.Count);
    }

    [Fact]
    public void AClassicSaveRestoresEveryJobItHolds()
    {
        const int jobCount = 65;
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_12xy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using (var w = SaveIO.OpenWriter(Path.Combine(dir, "sphereworld.scp"), SaveFormat.Text))
            {
                w.BeginRecord("WORLDITEM");
                w.WriteProperty("SERIAL", "040000001");
                w.WriteProperty("ID", "0EED");
                w.WriteProperty("P", "1000,1000,0,0");
                w.EndRecord();
            }
            using (var w = SaveIO.OpenWriter(Path.Combine(dir, "spheredata.scp"), SaveFormat.Text))
            {
                w.BeginRecord("TIMERF");
                for (int i = 0; i < jobCount; i++)
                {
                    w.WriteProperty("TimerFCall", $"f_mark {i}");
                    w.WriteProperty("TimerFNumbers", $"99,{0x40000001},60000");
                }
                w.EndRecord();
            }

            var world = TestHarness.CreateWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(world, dir);

            var item = world.FindItem(new Serial(0x40000001))!;
            Assert.Equal(jobCount, item.TimerFEntries.Count);
            // The last job is the one that used to fall off the end.
            Assert.Contains(item.TimerFEntries, e => e.Args == (jobCount - 1).ToString());
        }
        finally { Directory.Delete(dir, true); }
    }
}
