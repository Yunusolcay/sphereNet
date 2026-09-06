using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Delayed work: what a classic save carries, what a save taken mid-callback sees, and
/// who the delayed line runs as.
///
/// Source-X keeps delayed jobs in a [TIMERF] section of the world save
/// (CWorld.cpp:842) as a TimerFCall/TimerFNumbers pair whose first number tells the
/// dialect apart - 99 means milliseconds, less means 0.56 tenths of a second
/// (CTimedFunctionHandler.cpp:122/185). Each job is deleted inside its own tick
/// (CTimedFunction.cpp:77) while a save walks whatever is still pending (:203). The
/// call itself runs as the target's top-level object when that is a character, and as
/// the server otherwise (CTimedFunction.cpp:43), goes through the ordinary verb
/// dispatch - built-in table first, script function only for a name no verb owns
/// (CObjBase.cpp:2134) - and prepares its arguments the normal way, so leading numbers
/// arrive as ARGN1/2/3 (CScriptTriggerArgs.cpp:112).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DelayedCallParity12VTests
{
    // ================================================================ 12V-1

    private static string WriteSaveWithTimerFSection(string dir, string tick, string elapsed)
    {
        WriteItemFile(dir);
        // The delayed jobs live in the DATA file, outside the objects (CWorld.cpp:842).
        string path = Path.Combine(dir, "spheredata.scp");
        using (var w = SaveIO.OpenWriter(path, SaveFormat.Text))
        {
            w.BeginRecord("TIMERF");
            w.WriteProperty("TimerFCall", "f_payload 37");
            w.WriteProperty("TimerFNumbers", $"{tick},{0x40000001},{elapsed}");
            w.EndRecord();
        }
        return path;
    }

    private static void WriteItemFile(string dir)
    {
        using var w = SaveIO.OpenWriter(Path.Combine(dir, "sphereworld.scp"), SaveFormat.Text);
        w.BeginRecord("WORLDITEM");
        w.WriteProperty("SERIAL", "040000001");
        w.WriteProperty("ID", "0EED");
        w.WriteProperty("P", "1000,1000,0,0");
        w.EndRecord();
    }

    [Fact]
    public void AClassicTimerFSectionIsReattachedToItsObject()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_tf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            WriteSaveWithTimerFSection(dir, "99", "60000");   // Source-X: milliseconds

            var world = TestHarness.CreateWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(world, dir);

            var item = world.FindItem(new Serial(0x40000001));
            Assert.NotNull(item);
            var job = Assert.Single(item!.TimerFEntries);
            Assert.Equal("f_payload", job.FunctionName);
            Assert.Equal("37", job.Args);
            Assert.InRange(job.DueTickMs - Environment.TickCount64, 55_000, 60_000);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnOlderSaveCountsTenthsOfASecond()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_tf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A 0.56 save: the leading tick is below the marker, so 600 is 60 seconds.
            WriteSaveWithTimerFSection(dir, "1", "600");

            var world = TestHarness.CreateWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(world, dir);

            var item = world.FindItem(new Serial(0x40000001));
            var job = Assert.Single(item!.TimerFEntries);
            Assert.InRange(job.DueTickMs - Environment.TickCount64, 55_000, 60_000);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AHalfPairIsDroppedRatherThanGuessedAt()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_tf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            WriteItemFile(dir);
            using (var w = SaveIO.OpenWriter(Path.Combine(dir, "spheredata.scp"), SaveFormat.Text))
            {
                w.BeginRecord("TIMERF");
                w.WriteProperty("TimerFCall", "f_orphan");     // no numbers line
                w.WriteProperty("TimerFCall", "f_payload");
                w.WriteProperty("TimerFNumbers", $"99,{0x40000001},1000");
                w.EndRecord();
            }

            var world = TestHarness.CreateWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(world, dir);

            var item = world.FindItem(new Serial(0x40000001));
            var job = Assert.Single(item!.TimerFEntries);
            Assert.Equal("f_payload", job.FunctionName);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ================================================================ 12V-2

    [Fact]
    public void AJobThatHasNotRunYetIsStillInASaveTakenFromTheFirstCallback()
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        item.AddTimerF(0, "f_first", "");
        item.AddTimerF(0, "f_second", "");

        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_tf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lf = LoggerFactory.Create(_ => { });
            var seen = new List<string>();
            world.TimerFExpired = (_, entry) =>
            {
                seen.Add(entry.FunctionName);
                if (entry.FunctionName == "f_first")
                    new WorldSaver(lf).Save(world, dir);   // a script saving mid-callback
            };

            TestHarness.PumpTimerF(world, Environment.TickCount64);

            Assert.Equal(["f_first", "f_second"], seen);

            var reloaded = TestHarness.CreateWorld();
            new WorldLoader(lf).Load(reloaded, dir);
            var back = reloaded.FindItem(item.Uid)!;
            var pending = Assert.Single(back.TimerFEntries);
            Assert.Equal("f_second", pending.FunctionName);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AJobCancelledByAnEarlierCallbackDoesNotRun()
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        item.AddTimerF(0, "f_first", "");
        item.AddTimerF(0, "f_second", "");

        var seen = new List<string>();
        world.TimerFExpired = (obj, entry) =>
        {
            seen.Add(entry.FunctionName);
            if (entry.FunctionName == "f_first")
                obj.TryExecuteCommand("TIMERF", "CLEAR", null!);
        };

        TestHarness.PumpTimerF(world, Environment.TickCount64);

        Assert.Equal(["f_first"], seen);
    }

    // ================================================================ 12V-3

    private static DelayedCallDispatcher NewDispatcher(TriggerRunner? runner = null,
        Func<Character, ITextConsole?>? resolveClient = null, ITextConsole? server = null) =>
        new(() => runner, resolveClient, server ?? new AdminConsole());

    private sealed class AdminConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public string GetName() => "SERVER";
        public void SysMessage(string text) { }
    }

    [Fact]
    public void ADelayedCallOnACarriedItemRunsAsItsOwner()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
        owner.Backpack = pack; owner.Equip(pack, Layer.Pack);
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        pack.AddItem(item);

        var (src, console) = NewDispatcher().ResolveCaller(item);

        Assert.Same(owner, src);
        // Their privilege, not the server's: a player's item must not run engine
        // commands as an administrator.
        Assert.Equal(PrivLevel.Player, console.GetPrivLevel());
    }

    [Fact]
    public void ADelayedCallOnAGroundItemRunsAsTheServer()
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));

        var server = new AdminConsole();
        var (src, console) = NewDispatcher(server: server).ResolveCaller(item);

        Assert.Null(src);
        Assert.Same(server, console);
    }

    [Fact]
    public void TheOwnersOwnClientIsPreferredWhenTheyHaveOne()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        owner.Equip(item, Layer.Shirt);

        var clientConsole = new AdminConsole();
        var (src, console) = NewDispatcher(resolveClient: _ => clientConsole).ResolveCaller(item);

        Assert.Same(owner, src);
        Assert.Same(clientConsole, console);
    }

    // ================================================================ 12V-4

    [Theory]
    [InlineData("37", 37, 0, 0)]
    [InlineData("37,2,9", 37, 2, 9)]
    [InlineData("37 2 9", 37, 2, 9)]
    [InlineData("-5,4", -5, 4, 0)]
    [InlineData("hello 5", 0, 0, 0)]   // upstream only reads a LEADING number
    [InlineData("", 0, 0, 0)]
    public void LeadingNumbersReachTheScriptAsArgn(string raw, int n1, int n2, int n3)
    {
        var args = new SphereNet.Scripting.Execution.TriggerArgs();
        args.InitFromRaw(raw);

        Assert.Equal(raw, args.ArgString);   // ARGS is untouched either way
        Assert.Equal(n1, args.Number1);
        Assert.Equal(n2, args.Number2);
        Assert.Equal(n3, args.Number3);
    }

    [Fact]
    public void ADelayedFunctionSeesBothArgsAndArgn()
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        string scp = Path.Combine(Path.GetTempPath(), $"sphnet_fn_{Guid.NewGuid():N}.scp");
        File.WriteAllText(scp,
            "[FUNCTION f_payload]\nTAG.SEEN_ARGS=<ARGS>\nTAG.SEEN_N1=<ARGN1>\nRETURN 1\n");
        try
        {
            resources.LoadResourceFile(scp);
            var runner = new TriggerRunner(
                new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>()),
                resources, lf.CreateLogger<TriggerRunner>());

            var world = TestHarness.CreateWorld();
            var item = world.CreateItem();
            item.BaseId = 0x0EED;
            world.PlaceItem(item, new Point3D(100, 100, 0, 0));

            NewDispatcher(runner).Run(item, "f_payload", "37");

            Assert.True(item.TryGetProperty("TAG.SEEN_ARGS", out string seenArgs));
            Assert.Equal("37", seenArgs);
            Assert.True(item.TryGetProperty("TAG.SEEN_N1", out string seenN1));
            Assert.Equal("37", seenN1);
        }
        finally { File.Delete(scp); }
    }

    // ================================================================ 12V-5

    [Fact]
    public void AScriptFunctionDoesNotShadowTheEngineVerbOfTheSameName()
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        string scp = Path.Combine(Path.GetTempPath(), $"sphnet_fn_{Guid.NewGuid():N}.scp");
        File.WriteAllText(scp, "[FUNCTION REMOVE]\nTAG.SHADOWED=1\nRETURN 1\n");
        try
        {
            resources.LoadResourceFile(scp);
            var runner = new TriggerRunner(
                new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>()),
                resources, lf.CreateLogger<TriggerRunner>());

            var world = TestHarness.CreateWorld();
            var item = world.CreateItem();
            item.BaseId = 0x0EED;
            world.PlaceItem(item, new Point3D(100, 100, 0, 0));

            NewDispatcher(runner).Run(item, "REMOVE", "");

            Assert.True(item.IsDeleted);                                  // the engine verb ran
            Assert.False(item.TryGetProperty("TAG.SHADOWED", out string s) && s == "1");
        }
        finally { File.Delete(scp); }
    }

    [Fact]
    public void ANameNoVerbOwnsStillReachesTheScriptFunction()
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>());
        string scp = Path.Combine(Path.GetTempPath(), $"sphnet_fn_{Guid.NewGuid():N}.scp");
        File.WriteAllText(scp, "[FUNCTION f_only]\nTAG.RAN=1\nRETURN 1\n");
        try
        {
            resources.LoadResourceFile(scp);
            var runner = new TriggerRunner(
                new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>()),
                resources, lf.CreateLogger<TriggerRunner>());

            var world = TestHarness.CreateWorld();
            var item = world.CreateItem();
            item.BaseId = 0x0EED;
            world.PlaceItem(item, new Point3D(100, 100, 0, 0));

            NewDispatcher(runner).Run(item, "f_only", "");

            Assert.True(item.TryGetProperty("TAG.RAN", out string ran));
            Assert.Equal("1", ran);
        }
        finally { File.Delete(scp); }
    }
}
