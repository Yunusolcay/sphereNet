using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scheduling;
using SphereNet.Game.World;
using SphereNet.Server;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using System.Reflection;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

public class CoreRuntimeStabilityTests
{
    private static GameWorld CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void DirtySnapshot_LeavesNewDirtyForNextDrain()
    {
        var world = CreateWorld();
        var first = world.CreateItem();
        var second = world.CreateItem();

        first.Name = "first";
        var snapshot = world.DrainDirtyObjectsSnapshot();
        second.Name = "second";

        Assert.Contains(first, snapshot);
        Assert.DoesNotContain(second, snapshot);
        Assert.True(world.HasDirty);

        var next = world.DrainDirtyObjectsSnapshot();
        Assert.Contains(second, next);
        Assert.False(world.HasDirty);
    }

    [Fact]
    public void DeleteObject_CleansDirtyNotifyAndContainerChildren()
    {
        var world = CreateWorld();
        var parent = world.CreateItem();
        var child = world.CreateItem();
        parent.AddItem(child);
        child.ContainedIn = parent.Uid;

        parent.Name = "dirty";
        world.DeleteObject(parent);

        Assert.Null(world.FindItem(parent.Uid));
        Assert.Null(world.FindItem(child.Uid));
        Assert.False(world.HasDirty);

        parent.Name = "after-delete";
        Assert.False(world.HasDirty);
    }

    [Fact]
    public void TimerWheel_Remove_DropsNpcBeforeAdvance()
    {
        var npc = new Character();
        npc.SetUid(new Serial(0x00000001));
        var wheel = new TimerWheel(0);

        wheel.Schedule(npc, 100);
        wheel.Remove(npc);

        Assert.Equal(0, wheel.Count);
        Assert.Empty(wheel.Advance(200));
    }

    // N1 (perf): a wake burst is spread across tick slots by a uid-derived offset
    // (WakeNewlyActiveSectorNpcs) so the timer wheel doesn't fire ~140 NPCs in one slot.
    // This proves staggered fire times land in different slots (distributed Advance)
    // and that none are lost.
    [Fact]
    public void TimerWheel_StaggeredSchedule_SpreadsAcrossSlots()
    {
        const long spread = 800; // mirrors Program.NpcWakeSpreadMs (8 x 100ms slots)
        const int count = 8;
        var wheel = new TimerWheel(0);
        for (uint i = 1; i <= count; i++)
        {
            var npc = new Character();
            npc.SetUid(new Serial(i * 100)); // offsets 100..700,0 → distinct slots
            wheel.Schedule(npc, 100 + (npc.Uid.Value % spread));
        }

        // The first slot must not drain the whole burst — that is the point of staggering.
        int firstSlots = wheel.Advance(200).Count;
        Assert.True(firstSlots < count, "staggered wakes must not all fire in one slot");

        // Advancing past the spread window fires the rest; nothing is dropped.
        int rest = wheel.Advance(100 + spread + 100).Count;
        Assert.Equal(count, firstSlots + rest);
    }

    // S1 (perf): the NPC "turn to face the speaker" facing is delivered via the
    // dirty->view pipeline (SendUpdateMobile) instead of a synchronous per-NPC broadcast.
    // That relies on the Direction setter marking DirtyFlag.Direction on a change — guard
    // it so a future setter refactor can't silently stop NPCs facing whoever addresses them.
    [Fact]
    public void Direction_Setter_MarksDirtyOnChange()
    {
        var ch = new Character();
        ch.SetUid(new Serial(0x00000001));
        ch.ConsumeDirty(); // clear construction dirt (direction defaults to North=0)

        ch.Direction = Direction.East; // East != North → setter must mark the change dirty
        Assert.True((ch.DirtyFlags & DirtyFlag.Direction) != 0);

        // Setting the same direction again is a no-op (no spurious dirty / packet).
        ch.ConsumeDirty();
        ch.Direction = Direction.East;
        Assert.True((ch.DirtyFlags & DirtyFlag.Direction) == 0);
    }

    [Fact]
    public void ScriptFunction_MaxCallDepth_StopsRecursiveCall()
    {
        using var temp = new TempScriptFile(
            "[FUNCTION recurse]\n" +
            "CALL recurse\n" +
            "RETURN 1\n");

        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(temp.Path);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
            runner.TryRunFunction(name, target, source, args, scope, out var callResult)
                ? callResult
                : TriggerResult.Default;

        var scope = new ScriptScope { MaxCallDepth = 3 };
        bool handled = runner.TryRunFunction("recurse", new Character(), null, new ExecTriggerArgs(), scope, out var result);

        Assert.True(handled);
        Assert.Equal(TriggerResult.True, result);
    }

    // S2 (perf): the per-NPC f_onchar_speech hear hook is gated by HasFunction so an
    // undefined global function (the common case) skips building trigger args on every
    // spoken line. HasFunction must report a defined function as present (exact name and
    // f_-prefix fallback) and an undefined one as absent.
    [Fact]
    public void HasFunction_TracksRegisteredFunctions()
    {
        using var temp = new TempScriptFile(
            "[FUNCTION f_onchar_speech]\n" +
            "RETURN 0\n\n" +
            "[FUNCTION greet]\n" +
            "RETURN 0\n");

        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(temp.Path);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());

        Assert.True(runner.HasFunction("f_onchar_speech")); // exact match
        Assert.True(runner.HasFunction("greet"));           // exact match
        Assert.True(runner.HasFunction("onchar_speech"));   // f_-prefix fallback → f_onchar_speech
        Assert.False(runner.HasFunction("f_nonexistent_hook"));
    }

    [Fact]
    public void ScriptFunction_LocalScope_DoesNotLeakToCaller()
    {
        // A plain function LINE builds a fresh CScriptTriggerArgs (CObjBase.cpp:2138),
        // and the LOCAL pool lives on that object - so the callee gets its own and the
        // caller keeps its value. (CALL is the other case: see the test below.)
        using var temp = new TempScriptFile(
            "[FUNCTION child]\n" +
            "LOCAL.X=child\n" +
            "RETURN 1\n\n" +
            "[FUNCTION parent]\n" +
            "LOCAL.X=parent\n" +
            "child\n" +
            "TAG.RESULT=<LOCAL.X>\n" +
            "RETURN 1\n");

        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(temp.Path);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
            runner.TryRunFunction(name, target, source, args, scope, out var callResult)
                ? callResult
                : TriggerResult.Default;

        var target = new Character();
        Assert.True(runner.TryRunFunction("parent", target, null, new ExecTriggerArgs(), out _));
        Assert.True(target.TryGetProperty("TAG.RESULT", out string value));
        Assert.Equal("parent", value);
    }

    [Fact]
    public void ScriptFunction_CallSharesTheCallersLocalPool()
    {
        // CALL hands the callee the CALLER'S OWN args object (Execute_Call,
        // CScriptObj.cpp:1505) and the LOCAL pool lives on that object
        // (m_VarsLocal) - Init does not clear it - so the two share one pool: the
        // child reads what the parent set, and what the child writes is visible on
        // return. This test used to assert the opposite, which is the isolation the
        // plain function line above provides, not the one CALL has.
        using var temp = new TempScriptFile(
            "[FUNCTION child]\n" +
            "TAG.SEEN=<LOCAL.X>\n" +
            "LOCAL.X=child\n" +
            "RETURN 1\n\n" +
            "[FUNCTION parent]\n" +
            "LOCAL.X=parent\n" +
            "CALL child\n" +
            "TAG.RESULT=<LOCAL.X>\n" +
            "RETURN 1\n");

        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(temp.Path);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        interpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
            runner.TryRunFunction(name, target, source, args, scope, out var callResult)
                ? callResult
                : TriggerResult.Default;

        var target = new Character();
        Assert.True(runner.TryRunFunction("parent", target, null, new ExecTriggerArgs(), out _));

        Assert.True(target.TryGetProperty("TAG.SEEN", out string seen));
        Assert.Equal("parent", seen);                    // the child reads the caller's
        Assert.True(target.TryGetProperty("TAG.RESULT", out string result));
        Assert.Equal("child", result);                   // ...and the caller sees its write
    }

    [Fact]
    public void CombatArmor_RegionDefenseIgnoresOtherBodyParts()
    {
        var ch = new Character();
        var chest = new Item();
        chest.SetTag("ARMOR", "60");
        ch.Equip(chest, Layer.Chest);

        Assert.Equal(60, CombatEngine.CalcArmorDefenseForRegion(ch, ArmorHitRegion.Chest));
        Assert.Equal(0, CombatEngine.CalcArmorDefenseForRegion(ch, ArmorHitRegion.Head));
        Assert.True(CombatEngine.CalcArmorDefense(ch) > 0);
    }

    [Fact]
    public void SpellExpirations_DropsDeletedTargets()
    {
        var engine = new SpellEngine(CreateWorld(), new SpellRegistry());
        var ch = new Character();
        var schedule = typeof(SpellEngine).GetMethod("ScheduleEffectExpiry",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var def = new SpellDef { Id = SpellType.Bless, DurationBase = 10, DurationScale = 10 };
        schedule.Invoke(engine, [ch, ch, SpellType.Bless, def, 0]);

        ch.Delete();
        engine.ProcessExpirations(0);

        Assert.Equal(ch.BodyId, engine.GetResurrectBody(ch));
    }

    [Fact]
    public void TickAccumulator_CatchesUpAndCapsSpiral()
    {
        var method = typeof(SphereNet.Server.Program).GetMethod("ComputeDueTickCount",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        object?[] args = [350L, 100L, 100, 4];
        int due = (int)method.Invoke(null, args)!;
        Assert.Equal(3, due);
        Assert.Equal(400L, args[1]);

        args = [1000L, 100L, 100, 4];
        due = (int)method.Invoke(null, args)!;
        Assert.Equal(4, due);
        Assert.Equal(1100L, args[1]);
    }

    private sealed class TempScriptFile : IDisposable
    {
        public TempScriptFile(string contents)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spherenet_core_runtime_{Guid.NewGuid():N}.scp");
            File.WriteAllText(Path, contents);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
