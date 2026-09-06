using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    // Main-loop iteration stall detector. Catches latency the tick telemetry
    // cannot see: GC suspensions, OS scheduling stalls, blocking I/O in
    // periodic jobs — anything that delays the 0x73 ping echo. Threshold comes
    // from sphere.ini LoopStallWarnMs (0 disables the warning).
    private static long _lastLoopStallLogMs;

    // Main-loop iteration guard (İş 01 / C2): a throw anywhere in an iteration is
    // logged with the phase it happened in and the loop keeps running, instead of
    // escaping to ServerMain (which would kill the process and skip the shutdown
    // save). Log is throttled so a fault that reproduces every tick can't spam.
    private static long _lastMainLoopFailureLogMs;
    private static int _mainLoopFailureCount;

    private static void RunMainLoop()
    {
            _mainLoopThreadId = Environment.CurrentManagedThreadId;
            _log.LogInformation("Type 'help' for commands. Enter commands directly (e.g. save, status, quit).");

            // Do not enqueue the whole saved NPC population at startup. Large
            // worlds can contain 80K+ NPCs; draining that initial wheel while no
            // player is online stalls the login handshake. NPCs are woken when a
            // player activates their sector via WakeNewlyActiveSectorNpcs().
            if (_npcTimerWheel != null)
            {
                int npcCount = 0;
                foreach (var obj in _world.GetAllObjects())
                    if (obj is Character c && !c.IsPlayer && !c.IsDead && !c.IsDeleted)
                        npcCount++;
                _log.LogInformation("NPC timer wheel initialized: {Count} NPCs deferred until sectors become active", npcCount);
            }

            // --- 9. Main Game Loop ---
            _running = true;
            var sw = Stopwatch.StartNew();
            int TickIntervalMs = _config.ServerTickMs; // default 100 (10 ticks/s, Source-X MSECS_PER_TICK); ini: ServerTickMs
            const int MaxCatchUpTicksPerLoop = 4;
            long nextTickMs = TickIntervalMs;

            try
            {
            while (_running)
            {
                string loopPhase = "begin";
                try
                {
                // Loop-stall diagnostics: client-visible latency (0x73 ping echo)
                // lives in the WHOLE iteration — network I/O, periodic jobs and
                // the yield — not just RunServerTick. tick_stats can read 0.2ms
                // avg while ping spikes to 160ms, because ticks cover <1% of
                // wall time. Timestamp each segment and log a breakdown for any
                // iteration over the threshold, with the GC collection delta so
                // a GC pause is distinguishable from a slow job.
                long iterTs0 = Stopwatch.GetTimestamp();
                _loopIterationCount++;
                int iterG0 = GC.CollectionCount(0);
                int iterG1 = GC.CollectionCount(1);
                int iterG2 = GC.CollectionCount(2);

                long now = sw.ElapsedMilliseconds;

                // Console input (from WinForms command queue or headless stdin queue)
    #if WINFORMS
                if (_consoleForm != null)
                {
                    while (_consoleForm.CommandQueue.TryDequeue(out string? consoleCmd))
                        HandleConsoleCommand(consoleCmd);
                }
                else
    #endif
                {
                    while (_headlessCommandQueue.TryDequeue(out string? consoleCmd))
                        HandleConsoleCommand(consoleCmd);
                }

                while (_mainLoopActions.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception ex) { _log.LogError(ex, "Main-loop action failed"); }
                }

                long iterTs1 = Stopwatch.GetTimestamp(); // console/actions done

                // Network I/O runs every iteration for low latency
                loopPhase = "net_in";
                _network.CheckNewConnections();
                _network.ProcessAllInput();

                long iterTs2 = Stopwatch.GetTimestamp(); // network input done

                // Drain queued movement EVERY loop iteration, not just on the
                // 50ms server tick. ClassicUO animates its own steps on its local
                // timer and keeps only MAX_STEP_COUNT (5) unconfirmed steps; if
                // move acks/rejects arrive on the coarse 50ms tick cadence, fast
                // or stair sections starve that 5-step budget — the client pauses,
                // then replays the backlog in a burst ("throws forward" on
                // stairs). Processing here, right after input, lets the ack flush
                // in the SAME iteration (ProcessAllOutput below). The per-move
                // _nextMoveTime gate still paces the real drain, so this only cuts
                // latency — it does not let the player move faster.
                if (_clients.Count > 0)
                {
                    long moveNowMs = GameClient.MoveClock();
                    foreach (var client in _clients.Values)
                    {
                        if (client.IsPlaying)
                            client.TickMovementQueue(moveNowMs);
                    }
                }

                // Fast-path dirty: mark nearby clients for refresh, then run
                // UpdateClientView only for those clients. Gated by HasDirty.
                const bool FastPathViewDeltaEnabled = true;
                if (FastPathViewDeltaEnabled && _world.HasDirty)
                {
                    var dirtyObjects = _world.DrainDirtyObjectsSnapshot();
                    MarkClientsNearDirtyObjects(dirtyObjects);
                    foreach (var client in _clients.Values)
                    {
                        if (client.ViewNeedsRefresh && client.IsPlaying)
                        {
                            client.UpdateClientView();
                            client.ViewNeedsRefresh = false;
                        }
                    }
                }

                // Stress-test batch generation / cleanup — both are cooperative:
                // no-op when queues are empty. Runs every main-loop iteration so
                // long jobs finish quickly without starving the tick.
                if (_stressEngine != null)
                {
                    if (_stressEngine.IsGenerating) _stressEngine.OnTick();
                    if (_stressEngine.IsCleaning)   _stressEngine.TickCleanup();
                }

                // Bot restock — every 3 minutes, refresh bot inventories
                if (now - _lastBotRestockMs > 180_000)
                {
                    _lastBotRestockMs = now;
                    RestockBotCharacters();
                }

                // E2: land a finished background save's completion side effects
                // (hooks/counter/broadcast) on the main thread.
                CompleteBackgroundSave();

                // Periodic world auto-save (sphere.ini SavePeriod, minutes). 0 = off.
                // Runs on the main loop so it shares the same single-threaded save
                // path as the manual 'save' command. First save fires one full
                // period after startup, not at boot. With SAVEBACKGROUND > 0 only
                // the world walk happens here; the write runs on a worker.
                if (_config.SavePeriodMinutes > 0
                    && now - _lastAutoSaveMs > _config.SavePeriodMinutes * 60_000L)
                {
                    _lastAutoSaveMs = now;
                    PerformSave();
                }

                if (_config.TimerCallMinutes > 0
                    && now - _lastServerHookTimerMs >= _config.TimerCallMinutes * 60_000L)
                {
                    _lastServerHookTimerMs = now;
                    _systemHooks.DispatchServer("timer", _serverHookContext);
                }

                // Replay packet delivery runs every main-loop iteration
                // (~1-15ms) for smooth character movement instead of being
                // batched into the 100ms server tick.
                if (_recordingEngine.HasActiveReplays)
                    TickReplayPackets();

                long iterTs3 = Stopwatch.GetTimestamp(); // periodic jobs done

                loopPhase = "net_out";
                _network.ProcessAllOutput();
                _network.Tick();

                long iterTs4 = Stopwatch.GetTimestamp(); // network output done

                loopPhase = "ticks";
                int catchUpTicks = ComputeDueTickCount(now, ref nextTickMs, TickIntervalMs, MaxCatchUpTicksPerLoop);
                for (int tick = 0; tick < catchUpTicks; tick++)
                {
                    RunServerTick();
                    _network.ProcessAllOutput();
                }

                long iterTs5 = Stopwatch.GetTimestamp(); // server ticks done

                // Pass the slack until the next tick is due so the adaptive yield
                // (TickSleepMode=3) can sleep out an idle window without ever
                // overrunning the tick cadence. Other modes ignore the argument.
                TickYieldStrategy.Yield(_config.TickSleepMode, nextTickMs - sw.ElapsedMilliseconds);

                long iterTs6 = Stopwatch.GetTimestamp();
                long iterTotalUs = ToMicroseconds(iterTs6 - iterTs0);
                long stallThresholdUs = _config.LoopStallWarnMs * 1000L;
                if (stallThresholdUs > 0 && iterTotalUs > stallThresholdUs)
                {
                    long stallNowMs = Environment.TickCount64;
                    if (stallNowMs - _lastLoopStallLogMs > 2000)
                    {
                        _lastLoopStallLogMs = stallNowMs;
                        _log.LogWarning(
                            "[loop_stall] total={TotalMs}ms cmd={CmdMs}ms net_in={NetInMs}ms jobs={JobsMs}ms net_out={NetOutMs}ms ticks={TicksMs}ms yield={YieldMs}ms gc0=+{G0} gc1=+{G1} gc2=+{G2} pkts={Pkts} slowest_pkt=0x{SlowOp:X2}@{SlowMs}ms",
                            (iterTotalUs / 1000.0).ToString("F1"),
                            (ToMicroseconds(iterTs1 - iterTs0) / 1000.0).ToString("F1"),
                            (ToMicroseconds(iterTs2 - iterTs1) / 1000.0).ToString("F1"),
                            (ToMicroseconds(iterTs3 - iterTs2) / 1000.0).ToString("F1"),
                            (ToMicroseconds(iterTs4 - iterTs3) / 1000.0).ToString("F1"),
                            (ToMicroseconds(iterTs5 - iterTs4) / 1000.0).ToString("F1"),
                            (ToMicroseconds(iterTs6 - iterTs5) / 1000.0).ToString("F1"),
                            GC.CollectionCount(0) - iterG0,
                            GC.CollectionCount(1) - iterG1,
                            GC.CollectionCount(2) - iterG2,
                            _network.LastInputPassPacketCount,
                            _network.LastInputPassSlowestOpcode,
                            _network.LastInputPassSlowestMs.ToString("F1"));
                    }
                }
                }
                catch (Exception ex)
                {
                    // Last-defense containment: keep the shard alive through a
                    // fault in any iteration phase rather than crashing out (and
                    // losing all state since the last save). Throttle the log.
                    long failNowMs = Environment.TickCount64;
                    _mainLoopFailureCount++;
                    if (failNowMs - _lastMainLoopFailureLogMs > 2000)
                    {
                        _log.LogError(ex,
                            "[loop_guard] main-loop iteration failed in phase={Phase} ({Count} failures since last log); server continues",
                            loopPhase, _mainLoopFailureCount);
                        _lastMainLoopFailureLogMs = failNowMs;
                        _mainLoopFailureCount = 0;
                    }
                    // Don't spin a hot crash-loop pegging the CPU if the fault
                    // reproduces on every iteration.
                    System.Threading.Thread.Sleep(1);
                }
            }
            }
            finally
            {

            // --- 10. Shutdown ---
            _log.LogInformation("Shutting down...");
            try { _systemHooks.DispatchServer("exit", _serverHookContext); }
            catch (Exception ex) { _log.LogError(ex, "Exit hook dispatch failed"); }

            // Persist the world on a clean shutdown so changes since the last periodic
            // save are not lost (safe default on; SaveOnShutdown=0 to disable).
            if (_config.SaveOnShutdown)
            {
                try
                {
                    // An in-flight background save must land before the final
                    // shutdown save touches the same files (E2).
                    WaitForBackgroundSave();
                    _log.LogInformation("Saving world on shutdown...");
                    PerformSave();
                    WaitForBackgroundSave();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Shutdown save failed — world state since the last periodic save may be lost.");
                }
            }
            else
            {
                _log.LogWarning("Auto-save on shutdown is disabled (SaveOnShutdown=0). Use 'save' before quitting to persist world state.");
            }

            _stateRecorder?.Dispose();
            _ipcCts?.Cancel();
            try { _ipcTask?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException ex) { _log.LogDebug(ex.Flatten(), "IPC server stopped with an error"); }
            _ipcServer?.Dispose();
            _ipcTask = null;
            _ipcServer = null;
            _ipcCts?.Dispose();
            _ipcCts = null;
            _telnet?.Dispose();
            _webStatus?.Dispose();
            _network.Dispose();
            _mapData?.Dispose();
            _scriptDb.Close();
            _scriptLdb.Close();
            _scriptMdb.Close();
            _scriptFile?.Dispose();
            _mainLoopThreadId = 0;

            _log.LogInformation("SphereNet stopped.");
            Log.CloseAndFlush();

    #if WINFORMS
            // Close the WinForms window if still open
            if (_consoleForm != null && !_consoleForm.IsDisposed)
            {
                _consoleForm.BeginInvoke(() => _consoleForm.Close());
            }
    #endif
            }
    }

    private static int ComputeDueTickCount(long nowMs, ref long nextTickMs, int tickIntervalMs, int maxCatchUpTicks)
    {
        int due = 0;
        while (nowMs >= nextTickMs && due < maxCatchUpTicks)
        {
            nextTickMs += tickIntervalMs;
            due++;
        }

        if (due == maxCatchUpTicks && nowMs >= nextTickMs)
            nextTickMs = nowMs + tickIntervalMs;

        return due;
    }

    // The NPC batch a multicore tick consumed from the timer wheel (Advance
    // removes them). Set right after Advance and cleared once the tick has
    // rescheduled them; if the tick fails in between, RunServerTick reschedules
    // this batch so a failed tick can't drop those NPCs out of the wheel entirely.
    // TimerWheel.Schedule is idempotent (dedups on uid), so re-adding an NPC the
    // apply phase already rescheduled is a no-op.
    private static List<Character>? _multicoreConsumedNpcs;

    /// <summary>Reschedule the active-sector NPCs (and pets) in <paramref name="npcs"/>
    /// into the wheel. Shared by the normal multicore reschedule and the
    /// failed-tick recovery. Dead/deleted/sleeping NPCs are left out, matching the
    /// single-thread guard.</summary>
    private static void RescheduleActiveNpcs(List<Character> npcs)
    {
        if (_npcTimerWheel == null) return;
        foreach (var npc in npcs)
            if (!npc.IsDead && !npc.IsDeleted && !npc.IsPlayer &&
                (npc.NpcMaster.IsValid || _world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y)))
                _npcTimerWheel.Schedule(npc, npc.NextNpcActionTime);
    }

    /// <summary>Put back the NPCs a failed/abandoned multicore tick consumed from
    /// the wheel, so they aren't dropped from the schedule. Idempotent and a no-op
    /// once the tick has already rescheduled them.</summary>
    private static void RecoverConsumedNpcsAfterFailedTick()
    {
        var consumed = _multicoreConsumedNpcs;
        _multicoreConsumedNpcs = null;
        if (consumed != null)
            RescheduleActiveNpcs(consumed);
    }

    private static void RunServerTick()
    {
        _tickCounter++;
        long tickStart = Stopwatch.GetTimestamp();
        try
        {
            if (!_multicoreRuntimeEnabled && _multicoreFallbackMs > 0
                && Environment.TickCount64 - _multicoreFallbackMs >= MulticoreRecoveryCooldownMs)
            {
                _multicoreRuntimeEnabled = true;
                _multicoreFallbackMs = 0;
                _log.LogInformation("Multicore mode re-enabled after {Cooldown}s cooldown.", MulticoreRecoveryCooldownMs / 1000);
            }

            if (_multicoreRuntimeEnabled)
                RunMulticoreTick();
            else
                RunSingleThreadTick();
        }
        catch (OperationCanceledException oce)
        {
            // Do NOT re-run this tick single-threaded: the multicore attempt
            // already applied part of it (sector ticks, spell expirations), and
            // replaying would double-apply those side effects. Abandon this partial
            // tick, recover the NPCs it consumed from the timer wheel, drop to
            // single-thread mode, and let the NEXT scheduled tick run cleanly.
            _log.LogError(oce, "Multicore tick timed out; abandoning this tick and falling back to single-thread mode.");
            _multicoreRuntimeEnabled = false;
            _multicoreFallbackMs = Environment.TickCount64;
            RecoverConsumedNpcsAfterFailedTick();
        }
        catch (Exception ex)
        {
            if (_multicoreRuntimeEnabled)
            {
                // Same as the timeout path: abandon (don't replay) to avoid
                // double-applying the partial tick, recover consumed NPCs, and
                // continue single-threaded next tick.
                _log.LogError(ex, "Multicore tick failed; abandoning this tick and falling back to single-thread mode.");
                _multicoreRuntimeEnabled = false;
                _multicoreFallbackMs = Environment.TickCount64;
                RecoverConsumedNpcsAfterFailedTick();
            }
            else
            {
                throw;
            }
        }
        finally
        {
            long totalUs = ToMicroseconds(Stopwatch.GetTimestamp() - tickStart);
            if (totalUs > _telemetryMaxTickUs)
                _telemetryMaxTickUs = totalUs;
            TickHistogram.Record((int)(totalUs / 1000));

            long nowMs = Environment.TickCount64;
            long slowTickThresholdUs = _config.SlowTickWarnMs * 1000L;
            if (slowTickThresholdUs > 0 && totalUs > slowTickThresholdUs
                && nowMs - _lastSlowTickWarningMs > 10_000)
            {
                _lastSlowTickWarningMs = nowMs;
                _slowTickCount++;
                _lastSlowTickDominantPhase = GetDominantTickPhase();
                _log.LogWarning(
                    "[slow_tick] mode={Mode} tick={Tick} total={TotalMs}ms dominant={DominantPhase} snapshot={SnapshotMs}ms (world_tick={WorldTickMs}ms) compute={ComputeMs}ms (npc_build={NpcBuildMs}ms client_state={ClientStateMs}ms npc_apply={NpcApplyMs}ms [commit={NpcApplyCommitMs}ms/{DecisionCount} purge={NpcApplyPurgeMs}ms dirty={NpcApplyDirtyMs}ms/{DirtyCount}] view_build={ViewBuildMs}ms) apply={ApplyMs}ms post_apply={PostApplyMs}ms flush={FlushMs}ms",
                    _multicoreRuntimeEnabled ? "multicore" : "single",
                    _tickCounter,
                    (totalUs / 1000.0).ToString("F1"),
                    _lastSlowTickDominantPhase,
                    (_telemetrySnapshotUs / 1000.0).ToString("F1"),
                    (_telemetryWorldTickUs / 1000.0).ToString("F1"),
                    (_telemetryComputeUs / 1000.0).ToString("F1"),
                    (_telemetryNpcBuildUs / 1000.0).ToString("F1"),
                    (_telemetryClientStateUs / 1000.0).ToString("F1"),
                    (_telemetryNpcApplyUs / 1000.0).ToString("F1"),
                    (_telemetryNpcApplyDecisionsUs / 1000.0).ToString("F1"),
                    _telemetryNpcApplyDecisionCount,
                    (_telemetryNpcApplyPurgeUs / 1000.0).ToString("F1"),
                    (_telemetryNpcApplyDirtyUs / 1000.0).ToString("F1"),
                    _telemetryNpcApplyDirtyCount,
                    (_telemetryViewBuildUs / 1000.0).ToString("F1"),
                    (_telemetryApplyUs / 1000.0).ToString("F1"),
                    (_telemetryPostApplyUs / 1000.0).ToString("F1"),
                    (_telemetryFlushUs / 1000.0).ToString("F1"));
            }

            // Periodic tick stats: log average and max tick time every 30 seconds
            RecordTickTelemetry(totalUs);
            _tickStatsTotalUs += totalUs;
            if (totalUs > _tickStatsMaxUs) _tickStatsMaxUs = totalUs;
            _tickStatsCount++;

            if (nowMs - _lastTickStatsLogMs >= 30_000)
            {
                double avgMs = _tickStatsCount > 0 ? (_tickStatsTotalUs / _tickStatsCount / 1000.0) : 0;
                double maxMs = _tickStatsMaxUs / 1000.0;
                // Idle-CPU gauge: main-loop iterations per tick this window. A hot
                // spinning yield reports a large number here (the loop churns
                // between ticks doing nothing); an adaptive/sleeping yield reports
                // ~1. This is the measurement to take before/after changing
                // TickSleepMode on a small host.
                double loopsPerTick = _tickStatsCount > 0 ? (double)_loopIterationCount / _tickStatsCount : 0;
                var tickTelemetry = GetTickTelemetrySnapshot();
                int onlinePlayers = _clients.Values.Count(c => c.IsPlaying);
                var (chars, items, _) = _world.GetStats();

                // Include bot stats if bots are active
                if (_botEngine != null && _botEngine.TotalBots > 0)
                {
                    var botStats = _botEngine.GetStats();
                    _log.LogDebug(
                        "[tick_stats] ticks={Count} avg={AvgMs:F1}ms max={MaxMs:F1}ms p50={P50Ms:F1}ms p95={P95Ms:F1}ms p99={P99Ms:F1}ms loops={Loops} lpt={Lpt:F0} players={Players} chars={Chars} items={Items} bots={Bots}/{BotTotal} pps_in={PpsIn:F0} pps_out={PpsOut:F0}",
                        _tickStatsCount, avgMs, maxMs, tickTelemetry.P50Ms, tickTelemetry.P95Ms, tickTelemetry.P99Ms, _loopIterationCount, loopsPerTick, onlinePlayers, chars, items,
                        botStats.ActiveBots, botStats.TotalBots, botStats.PacketsPerSecIn, botStats.PacketsPerSecOut);
                }
                else
                {
                    _log.LogDebug(
                        "[tick_stats] ticks={Count} avg={AvgMs:F1}ms max={MaxMs:F1}ms p50={P50Ms:F1}ms p95={P95Ms:F1}ms p99={P99Ms:F1}ms loops={Loops} lpt={Lpt:F0} players={Players} chars={Chars} items={Items}",
                        _tickStatsCount, avgMs, maxMs, tickTelemetry.P50Ms, tickTelemetry.P95Ms, tickTelemetry.P99Ms, _loopIterationCount, loopsPerTick, onlinePlayers, chars, items);
                }

                // GC pressure for the same window. Gen2 collections and pause%
                // are the GC-stall signal; alloc rate is a relative gauge
                // (in-process bots inflate the absolute number).
                long allocNow = GC.GetTotalAllocatedBytes();
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                long shedNow = SphereNet.Network.State.NetState.DroppedChatterPackets;
                if (_gcWindowInit)
                {
                    long windowMs = Math.Max(1, nowMs - _lastTickStatsLogMs);
                    long allocDelta = Math.Max(0, allocNow - _gcWindowStartAllocBytes);
                    double allocMBs = allocDelta / 1048576.0 / (windowMs / 1000.0);
                    double allocPerTickKB = _tickStatsCount > 0 ? allocDelta / 1024.0 / _tickStatsCount : 0;
                    var gcInfo = GC.GetGCMemoryInfo();
                    _log.LogDebug(
                        "[gc_stats] alloc={AllocMBs:F0}MB/s ({PerTickKB:F0}KB/tick) gen0={G0} gen1={G1} gen2={G2} pause%={Pause:F1} heap={Heap}MB rss={Rss}MB shed={Shed}",
                        allocMBs, allocPerTickKB, g0 - _gcWindowStartGen0, g1 - _gcWindowStartGen1, g2 - _gcWindowStartGen2,
                        gcInfo.PauseTimePercentage, GC.GetTotalMemory(false) / 1048576,
                        Environment.WorkingSet / 1048576, shedNow - _gcWindowStartShed);
                }
                _gcWindowStartAllocBytes = allocNow;
                _gcWindowStartGen0 = g0; _gcWindowStartGen1 = g1; _gcWindowStartGen2 = g2;
                _gcWindowStartShed = shedNow;
                _gcWindowInit = true;

                _tickStatsTotalUs = 0;
                _tickStatsMaxUs = 0;
                _tickStatsCount = 0;
                _loopIterationCount = 0;
                _lastTickStatsLogMs = nowMs;
            }
        }
    }

    private static string GetDominantTickPhase()
    {
        var phases = new[]
        {
            ("world_tick", _telemetryWorldTickUs),
            // Residual of the pre-compute group after the parallel sector tick:
            // spell expiry + sector wake + wheel advance + client snapshot.
            ("snapshot", Math.Max(0, _telemetrySnapshotUs - _telemetryWorldTickUs)),
            ("post_apply", _telemetryPostApplyUs),
            ("compute", _telemetryComputeUs),
            ("npc_build", _telemetryNpcBuildUs),
            ("client_state", _telemetryClientStateUs),
            ("npc_apply", _telemetryNpcApplyUs),
            ("view_build", _telemetryViewBuildUs),
            ("apply", _telemetryApplyUs),
            ("flush", _telemetryFlushUs),
        };
        return phases.OrderByDescending(p => p.Item2).First().Item1;
    }

    private static void RecordTickTelemetry(long totalUs)
    {
        _tickTelemetryWindowUs[_tickTelemetryWriteIndex] = totalUs;
        _tickTelemetryWriteIndex = (_tickTelemetryWriteIndex + 1) % TickTelemetryWindowSize;
        if (_tickTelemetrySampleCount < TickTelemetryWindowSize)
            _tickTelemetrySampleCount++;
    }

    private static TickTelemetrySnapshot GetTickTelemetrySnapshot()
    {
        int count = _tickTelemetrySampleCount;
        if (count == 0)
            return new TickTelemetrySnapshot(false, _multicoreRuntimeEnabled, 0, 0, 0, 0, 0, 0, 0);

        var samples = new long[count];
        Array.Copy(_tickTelemetryWindowUs, samples, count);
        Array.Sort(samples);

        double p50 = Percentile(samples, 0.50) / 1000.0;
        double p95 = Percentile(samples, 0.95) / 1000.0;
        double p99 = Percentile(samples, 0.99) / 1000.0;
        double max = samples[^1] / 1000.0;
        double avg = samples.Average() / 1000.0;
        return new TickTelemetrySnapshot(true, _multicoreRuntimeEnabled, count, avg, max, p50, p95, p99,
            _telemetryMaxTickUs / 1000.0);
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private readonly record struct TickTelemetrySnapshot(
        bool HasSamples,
        bool MulticoreEnabled,
        int SampleCount,
        double AvgMs,
        double MaxMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxSinceStartMs);

    private static void RunSingleThreadTick()
    {
        long p0 = Stopwatch.GetTimestamp();

        _world.OnTick();
        _spellEngine.ProcessExpirations(Environment.TickCount64);

        // Wake NPCs in sectors that just became active (player entered area)
        WakeNewlyActiveSectorNpcs();

        // NPC AI via timer wheel — only reschedule NPCs that remain in
        // active sectors. Sleeping NPCs exit the wheel entirely and get
        // bulk-woken by WakeNewlyActiveSectorNpcs when a player enters.
        {
            long now = Environment.TickCount64;
            var dueNpcs = _npcTimerWheel.Advance(now);
            ApplyNpcTickBudget(dueNpcs);
            foreach (var npc in dueNpcs)
            {
                _npcAI.OnTickAction(npc);
                if (!npc.IsDead && !npc.IsDeleted && (npc.NpcMaster.IsValid || _world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y)))
                    _npcTimerWheel.Schedule(npc, npc.NextNpcActionTime);
            }
            _npcAI.PurgeStalePaths();
        }

        _telemetrySnapshotUs = ToMicroseconds(Stopwatch.GetTimestamp() - p0);
        _telemetryWorldTickUs = 0;
        _telemetryPostApplyUs = 0;
        _telemetryComputeUs = 0;
        _telemetryNpcBuildUs = 0;
        _telemetryClientStateUs = 0;
        _telemetryNpcApplyUs = 0;
        _telemetryNpcApplyDecisionsUs = 0;
        _telemetryNpcApplyPurgeUs = 0;
        _telemetryNpcApplyDirtyUs = 0;
        _telemetryNpcApplyDecisionCount = 0;
        _telemetryNpcApplyDirtyCount = 0;
        _telemetryViewBuildUs = 0;

        long p1 = Stopwatch.GetTimestamp();

        var dirtyObjects = _world.HasDirty ? _world.DrainDirtyObjectsSnapshot() : [];
        MarkClientsNearDirtyObjects(dirtyObjects);

        long rttNow = Environment.TickCount64;
        foreach (var client in _clients.Values)
        {
            client.NetState.SendRttPing(rttNow);
            client.TickClientState();
            if (client.ViewNeedsRefresh)
            {
                client.UpdateClientView();
                client.ViewNeedsRefresh = false;
            }
        }

        if (_recordingEngine.HasActiveReplays)
            TickReplayOverlays();

        // Pass a provider, not a materialized collection: StateRecorder only walks the
        // roster every 2s/15s, and GetAllCharactersSnapshot is char-only (not the full
        // ~52K object table). Avoids a per-tick full-world array copy.
        _stateRecorder?.Tick(Environment.TickCount64, _world.GetAllCharactersSnapshot);

        _macroEngine?.Tick(Environment.TickCount64,
            uid => _clientsByCharUid.GetValueOrDefault(new Serial(uid)),
            FindItemInBackpack,
            (uid, msg) => { if (_world.FindChar(new Serial(uid)) is { } c) SendSysMessage(c, msg); });

        _telemetryApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p2 = Stopwatch.GetTimestamp();
        RunPostTickMaintenance();
        _telemetryFlushUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2);

        MaybeRunDeterminismGuardrail();
    }

    // Below this many work items, Parallel.ForEach is pure overhead
    // (partitioner + worker ramp-up + lambda capture cost more than
    // the actual loop body). Empirically a single 1-vCPU VDS handles
    // 4 ticks/sec at ~5 ms when this threshold short-circuits the
    // 1-client / 0-NPC steady state, vs 50–300 ms when forced through
    // ParallelForEach. Mirrors GameWorld.OnTickParallel sector cutoff.
    private const int ParallelComputeMinBatch = 16;

    // Per-tick NPC AI budget. A mass sector activation (a player entering a
    // densely populated area) can make thousands of NPCs due in a single tick;
    // running BuildDecision for all of them at once stalls the tick for
    // seconds. Process at most this many per tick and defer the overflow to the
    // next tick. No effect on normal loads where the due count is below it.
    private const int MaxNpcsPerTick = 500;

    private static void ApplyNpcTickBudget(List<Character> due)
    {
        if (_npcTimerWheel == null || due.Count <= MaxNpcsPerTick) return;
        long deferAt = Environment.TickCount64;
        for (int i = MaxNpcsPerTick; i < due.Count; i++)
            _npcTimerWheel.Schedule(due[i], deferAt);
        // Removing the tail truncates the count without shifting elements.
        due.RemoveRange(MaxNpcsPerTick, due.Count - MaxNpcsPerTick);
    }

    private static void RunMulticoreTick()
    {
        // Auto worker count leaves one core for the main thread: saturating every
        // core with phase workers starves the serial apply/flush phases on
        // shared/overcommitted VMs (visible as yield/net_in loop stalls). An
        // explicit MulticoreWorkerCount still wins.
        int workerCount = _config.MulticoreWorkerCount > 0
            ? _config.MulticoreWorkerCount
            : Math.Max(1, Environment.ProcessorCount - 1);
        int timeoutMs = Math.Max(100, _config.MulticorePhaseTimeoutMs);
        using var cts = new CancellationTokenSource(timeoutMs);

        long p0 = Stopwatch.GetTimestamp();
        _world.OnTickParallel(workerCount, cts.Token);
        _telemetryWorldTickUs = ToMicroseconds(Stopwatch.GetTimestamp() - p0);
        _spellEngine.ProcessExpirations(Environment.TickCount64);

        // Wake NPCs in sectors that just became active (player entered area)
        WakeNewlyActiveSectorNpcs();

        // NPC AI via timer wheel. Advance REMOVES the due NPCs from the wheel, so
        // track them: if this tick fails before rescheduling, RunServerTick's
        // recovery puts them back (see _multicoreConsumedNpcs).
        var npcSnapshot = _npcTimerWheel.Advance(Environment.TickCount64);
        ApplyNpcTickBudget(npcSnapshot);
        _multicoreConsumedNpcs = npcSnapshot;

        _reusableClientSnapshot.Clear();
        foreach (var c in _clients.Values)
        {
            if (c.IsPlaying)
                _reusableClientSnapshot.Add(c);
        }
        var clientSnapshot = _reusableClientSnapshot;
        _telemetrySnapshotUs = ToMicroseconds(Stopwatch.GetTimestamp() - p0);

        long p1 = Stopwatch.GetTimestamp();
        long nowTick = Environment.TickCount64;

        // Reuse buffers — both lists are cleared at end of tick. Avoids
        // ConcurrentBag/ConcurrentDictionary allocation churn that was
        // visible as slow_tick spikes on light loads.
        var decisionList = _reusableDecisionList;
        decisionList.Clear();
        // Bound the prestage A* cost per tick: a chase burst on a low-core box
        // showed up as npc_build=130ms when several 500-node searches landed in
        // one build phase. Over-budget chasers defer ~150ms. Field-tuned 4 → 2:
        // npc_build still hit 90ms with 4 on the live box.
        _npcAI.BeginTickPathfindBudget(2);
        if (npcSnapshot.Count >= ParallelComputeMinBatch)
        {
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cts.Token
            };
            Parallel.ForEach(npcSnapshot, po, npc =>
            {
                var decision = _npcAI.BuildDecision(npc, nowTick);
                if (decision.HasValue)
                    lock (decisionList) decisionList.Add(decision.Value);
            });
        }
        else
        {
            foreach (var npc in npcSnapshot)
            {
                var decision = _npcAI.BuildDecision(npc, nowTick);
                if (decision.HasValue)
                    decisionList.Add(decision.Value);
            }
        }
        _telemetryNpcBuildUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p1b = Stopwatch.GetTimestamp();
        long rttNow = Environment.TickCount64;
        foreach (var client in clientSnapshot)
        {
            client.NetState.SendRttPing(rttNow);
            client.TickClientState();
        }
        _telemetryClientStateUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1b);

        long p1c = Stopwatch.GetTimestamp();
        // Parallel.ForEach adds decisions in completion order, which is
        // non-deterministic and differs from the single-thread wheel order.
        // Apply order matters for order-sensitive side effects (ally rally,
        // surround steps, shared-RNG draws), so sort by UID for a stable,
        // reproducible apply order before committing.
        decisionList.Sort(static (a, b) => a.NpcUid.CompareTo(b.NpcUid));

        // Apply NPC decisions — fires CharacterMoved for each NPC move,
        // which immediately notifies nearby clients via OnCharacterMoved.
        foreach (var decision in decisionList)
            _npcAI.ApplyDecision(decision);
        long p1cApply = Stopwatch.GetTimestamp();
        _telemetryNpcApplyDecisionsUs = ToMicroseconds(p1cApply - p1c);
        _telemetryNpcApplyDecisionCount = decisionList.Count;

        _npcAI.PurgeStalePaths();
        long p1cPurge = Stopwatch.GetTimestamp();
        _telemetryNpcApplyPurgeUs = ToMicroseconds(p1cPurge - p1cApply);

        // Mark clients near dirty objects for refresh
        var dirtyObjects = _world.HasDirty ? _world.DrainDirtyObjectsSnapshot() : [];
        MarkClientsNearDirtyObjects(dirtyObjects);
        _telemetryNpcApplyDirtyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1cPurge);
        _telemetryNpcApplyDirtyCount = dirtyObjects.Count;
        _telemetryNpcApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1c);

        // View delta: only for clients flagged ViewNeedsRefresh (moved or
        // had nearby objects change). Most clients are idle and skip entirely.
        long p1d = Stopwatch.GetTimestamp();
        var refreshClients = _reusableRefreshClients;
        refreshClients.Clear();
        foreach (var client in clientSnapshot)
        {
            if (client.ViewNeedsRefresh)
                refreshClients.Add(client);
        }

        var clientDeltas = _reusableClientDeltas;
        clientDeltas.Clear();
        if (refreshClients.Count >= ParallelComputeMinBatch)
        {
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cts.Token
            };
            var concurrent = _reusableViewDeltaConcurrent;
            concurrent.Clear();
            Parallel.ForEach(refreshClients, po, client =>
            {
                var delta = client.BuildViewDelta();
                if (delta != null)
                    concurrent[client.NetState.Id] = delta;
            });
            foreach (var kv in concurrent)
                clientDeltas[kv.Key] = kv.Value;
        }
        else
        {
            foreach (var client in refreshClients)
            {
                var delta = client.BuildViewDelta();
                if (delta != null)
                    clientDeltas[client.NetState.Id] = delta;
            }
        }
        _telemetryViewBuildUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1d);
        _telemetryComputeUs = ToMicroseconds(Stopwatch.GetTimestamp() - p1);

        long p2 = Stopwatch.GetTimestamp();
        bool hasRecordings = _recordingEngine.HasActiveRecordings;
        foreach (var client in refreshClients)
        {
            // Only consume the request when a delta was actually built for it.
            // BuildViewDelta returns null while the character is mid-handoff
            // (not attached yet, not playing, spectating a replay); clearing the
            // flag there dropped the refresh on the floor and the client stayed
            // on a stale view until something else happened to set it again.
            if (!clientDeltas.TryGetValue(client.NetState.Id, out var delta))
                continue;
            client.ViewNeedsRefresh = false;

            client.ApplyViewDelta(delta);
            client.SyncOpenMapStaticDoors();

            if (hasRecordings && delta.NewChars.Count > 0)
            {
                uint charUid = client.Character?.Uid.Value ?? 0;
                if (charUid != 0 && _recordingEngine.IsRecording(charUid))
                {
                    foreach (var (ch, _) in delta.NewChars)
                    {
                        var equip = new List<(uint, ushort, byte, ushort)>();
                        for (int layer = 1; layer <= (int)Layer.Horse; layer++)
                        {
                            var item = ch.GetEquippedItem((Layer)layer);
                            if (item != null)
                                equip.Add((item.Uid.Value, item.DispIdFull, (byte)layer, item.Hue));
                        }
                        byte flags = 0;
                        if (ch.IsInWarMode) flags |= 0x40;
                        if (ch.IsInvisible) flags |= 0x80;
                        var pkt = new PacketDrawObject(
                            ch.Uid.Value, ch.BodyId, ch.X, ch.Y, ch.Z,
                            (byte)ch.Direction, ch.Hue, flags, 0x01,
                            equip.ToArray());
                        _recordingEngine.CapturePacket(charUid, ch.Position,
                            pkt.Build().Span.ToArray());
                    }
                }
            }
        }
        _telemetryApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2);

        long p2b = Stopwatch.GetTimestamp();
        if (_recordingEngine.HasActiveReplays)
            TickReplayOverlays();

        // Pass a provider, not a materialized collection: StateRecorder only walks the
        // roster every 2s/15s, and GetAllCharactersSnapshot is char-only (not the full
        // ~52K object table). Avoids a per-tick full-world array copy.
        _stateRecorder?.Tick(Environment.TickCount64, _world.GetAllCharactersSnapshot);

        _macroEngine?.Tick(Environment.TickCount64,
            uid => _clientsByCharUid.GetValueOrDefault(new Serial(uid)),
            FindItemInBackpack,
            (uid, msg) => { if (_world.FindChar(new Serial(uid)) is { } c) SendSysMessage(c, msg); });

        // Re-schedule only active-sector NPCs (and pets). Sleeping NPCs exit the
        // wheel — WakeNewlyActiveSectorNpcs handles sector transitions. Clearing
        // the tracked batch afterwards means a failure in a LATER phase won't
        // double-reschedule what we've already put back.
        RescheduleActiveNpcs(npcSnapshot);
        _multicoreConsumedNpcs = null;
        _telemetryPostApplyUs = ToMicroseconds(Stopwatch.GetTimestamp() - p2b);

        long p3 = Stopwatch.GetTimestamp();
        RunPostTickMaintenance();
        _telemetryFlushUs = ToMicroseconds(Stopwatch.GetTimestamp() - p3);

        MaybeRunDeterminismGuardrail();
    }

    // A wake burst (a fresh login lighting up the 5x5 sector window can wake ~140 NPCs at
    // once) is spread across this many milliseconds by a deterministic per-NPC offset, so
    // the timer wheel doesn't drop them all into one 100ms slot and spike the serial NPC
    // apply phase. Only the bulk sector-activation path staggers; single-target wakes
    // (aggro/interaction) stay immediate.
    private const long NpcWakeSpreadMs = 800;

    // Single-arg overload kept as a distinct method so it still binds to Action<Character>
    // where WakeNpc is used as a delegate (single-target aggro/interaction wakes).
    private static void WakeNpc(Character npc) => WakeNpc(npc, 0);

    private static void WakeNpc(Character npc, long extraDelayMs)
    {
        if (npc.IsPlayer || npc.IsDeleted || npc.IsDead) return;
        npc.NextNpcActionTime = 0;
        _npcTimerWheel?.Remove(npc);
        _npcTimerWheel?.Schedule(npc, Environment.TickCount64 + 100 + extraDelayMs);
    }

    private static void WakeNewlyActiveSectorNpcs()
    {
        if (_npcTimerWheel == null) return;
        var sectors = _world.NewlyActiveSectors;
        if (sectors.Count == 0) return;
        foreach (var sector in sectors)
        {
            foreach (var ch in sector.Characters)
            {
                if (!ch.IsPlayer && !ch.IsDeleted && !ch.IsDead)
                    // uid-derived offset (stable, no RNG — RNG would break tick determinism).
                    WakeNpc(ch, NpcWakeSpreadMs > 0 ? ch.Uid.Value % NpcWakeSpreadMs : 0);
            }
        }
    }

    private static void CleanupSummonedGuards(long now)
    {
        if (_summonedGuardExpiry.Count == 0)
            return;

        foreach (var (uid, expireAt) in _summonedGuardExpiry.ToArray())
        {
            var guard = _world.FindChar(uid);
            if (guard == null || guard.IsDeleted || guard.IsDead)
            {
                _summonedGuardExpiry.Remove(uid);
                continue;
            }

            if (now < expireAt)
                continue;

            var removePacket = new PacketDeleteObject(uid.Value);
            BroadcastNearby(guard.Position, 18, removePacket, 0);
            _world.DeleteObject(guard);
            guard.Delete();
            _summonedGuardExpiry.Remove(uid);
        }
    }

    private static void RunDecayCatchup(long now)
    {
        if (now < _nextDecayCatchupTick)
            return;
        _nextDecayCatchupTick = now + 5000;

        // Sector sleep skips far-away sectors. This catch-up sweep ensures
        // decaying ground items/corpses still expire on time even when
        // nobody is nearby. Collection completes before any deletion below,
        // so the direct (snapshot-free) world enumeration is safe here.
        _decayCatchupBuffer.Clear();
        _world.CollectExpiredGroundItems(now, 256, _decayCatchupBuffer);

        foreach (var item in _decayCatchupBuffer)
        {
            // Drive the normal item decay path first (corpse spill, spawn cleanup).
            _ = item.OnTick();
            if (!item.IsDeleted)
                continue;

            var removePacket = new PacketDeleteObject(item.Uid.Value);
            BroadcastNearby(item.Position, 18, removePacket, 0);
            _world.DeleteObject(item);
            item.Delete();
        }
    }

    /// <summary>Swing shut any map-static door whose 20s auto-close timer has
    /// elapsed. Item-doors self-close via Item.OnTick; map-static doors are just
    /// an open-overlay set, so their auto-close is driven here. Mirrors the
    /// manual toggle-close broadcast (restore the closed static art + play the
    /// shut sound) so the client sees exactly the same close it would from a
    /// player-triggered one.</summary>
    private static void CloseExpiredStaticDoors(long now)
    {
        if (now < _nextStaticDoorTick)
            return;
        _nextStaticDoorTick = now + 1000;

        var md = _world.MapData;
        if (md == null)
            return;

        int closed = SphereNet.Game.World.StaticDoorSweeper.CollectExpired(
            _world, md, now, _staticDoorCloseBuffer, _expiredStaticDoorBuffer,
            out int dropped);
        if (closed == 0 && dropped == 0)
            return;

        foreach (var op in _staticDoorCloseBuffer)
        {
            var pos = new Point3D(op.X, op.Y, op.Z, op.Map);
            BroadcastNearby(pos, 18, new PacketSound(0x00F1, op.X, op.Y, op.Z), 0);
            BroadcastNearby(pos, 18,
                new PacketWorldItem(op.Serial, op.ClosedArt, 1, op.X, op.Y, op.Z, op.Hue), 0);
        }
    }

    private static void RunPostTickMaintenance()
    {
        long now = Environment.TickCount64;
        CleanupSummonedGuards(now);
        RunDecayCatchup(now);
        CloseExpiredStaticDoors(now);
        ProcessRespawnResetChunk();

        long lightMinute = _world.WorldClockMinutes;
        if (lightMinute != _lastLightWorldMinute)
        {
            _lastLightWorldMinute = lightMinute;
            foreach (var client in _clients.Values)
            {
                if (!client.IsPlaying) continue;
                // Recalculate per position: sector longitude, both moon phases,
                // weather and underground regions can all change perceived light.
                var ch = client.Character;
                if (ch == null) continue;
                byte light = ch.IsDead ? (byte)0 : _world.GetLightLevel(ch.Position);
                var region = _world.FindRegion(ch.Position);
                var weather = _weatherEngine.GetWeatherForRegion(region).Type;
                ch.UpdateEnvironment(light, (byte)weather,
                    ch.IsDead ? (byte)SeasonType.Desolation : (byte)_weatherEngine.CurrentSeason);
                client.Send(new PacketGlobalLight(light));
            }
        }

        // Weather & season update
        bool seasonChanged = _weatherEngine.OnTick();
        if (seasonChanged)
            BroadcastSeasonChange(playSound: true);

        // Region periodic triggers (Source-X CSector environ tick): fire
        // @CliPeriodic for every online player on their current region, and
        // @RegPeriodic once per region that holds at least one player (a
        // representative player is the SRC, mirroring iRegionPeriodic). Only
        // regions with players present tick — uninhabited regions never fire.
        if (_world.TickCount % RegionPeriodicTicks == 0 && _triggerDispatcher != null)
        {
            _regPeriodicFired.Clear();
            foreach (var client in _clients.Values)
            {
                if (!client.IsPlaying) continue;
                var ch = client.Character;
                if (ch == null) continue;
                var region = _world.FindRegion(ch.Position);
                if (region == null) continue;

                // @RegPeriodic: once per region this tick (first player = SRC).
                if (_regPeriodicFired.Add(region.Uid))
                    _triggerDispatcher.FireRegionEvents(region, "RegPeriodic", ch,
                        new TriggerArgs { CharSrc = ch, S1 = region.Name });

                // @CliPeriodic: once per online player in a region.
                _triggerDispatcher.FireRegionEvents(region, "CliPeriodic", ch,
                    new TriggerArgs { CharSrc = ch, S1 = region.Name });
            }
        }

        // Party member HP bars (Source-X CPartyDef::AddStatsUpdate): push every
        // member's health to every OTHER online member so the party gump bars
        // track them beyond visual range. ~2s cadence (40 ticks at 50ms).
        if (_world.TickCount % 40 == 0 && _partyManager != null)
            PushPartyStats();

        // Ship movement ticks
        _shipEngine?.OnTickAll();

        // House decay (check every ~120 ticks = ~6s at 50ms tick)
        if (_world.TickCount % 120 == 0 && _housingEngine != null)
        {
            var collapsed = _housingEngine.OnTickDecay();
            foreach (var house in collapsed)
                _log.LogInformation("House 0x{Uid:X} collapsed from decay", house.MultiItem.Uid.Value);
        }

        ProcessIdleTimeout();
        _telnet?.Tick();
        _webStatus?.Tick();
    }

    /// <summary>Send each party member's health bar to every other online
    /// member (Source-X CPartyDef::AddStatsUpdate). Distance-independent —
    /// that is the point: the party gump tracks members out of visual range.</summary>
    private static void PushPartyStats()
    {
        foreach (var party in _partyManager.Parties)
        {
            if (party.MemberCount < 2) continue;

            // Resolve online members once per party.
            var online = new List<(SphereNet.Game.Objects.Characters.Character Ch, GameClient Client)>(party.MemberCount);
            foreach (var uid in party.Members)
            {
                var ch = _world.FindChar(uid);
                if (ch == null || ch.IsDeleted) continue;
                if (TryGetClientFor(ch, out var gc))
                    online.Add((ch, gc));
            }
            if (online.Count < 2) continue;

            foreach (var (subject, _) in online)
            {
                var pkt = new SphereNet.Network.Packets.Outgoing.PacketUpdateHealth(
                    subject.Uid.Value, subject.MaxHits, subject.Hits);
                // Refresh the subject's party-member map pin (Source-X
                // CPartyDef::UpdateWaypointAll) alongside the HP bar, so the map
                // tracks members out of visual range on waypoint-capable clients.
                var waypoint = new SphereNet.Network.Packets.Outgoing.PacketWaypointAdd(
                    subject.Uid.Value, subject.X, subject.Y, subject.Z, subject.MapIndex,
                    type: 2, subject.GetName()); // 2 = party member
                foreach (var (other, client) in online)
                {
                    if (other == subject) continue;
                    client.Send(pkt);
                    if (client.NetState.SupportsMapWaypoints)
                        client.Send(waypoint);
                }
            }
        }
    }

    private static void ProcessIdleTimeout()
    {
        long idleThresholdMs = _config.NetTTL * 1000L;
        if (idleThresholdMs <= 0)
            return;

        long tickNow = Environment.TickCount64;
        foreach (var state in _network.GetActiveStates())
        {
            if (state.LastActivityTick > 0 &&
                tickNow - state.LastActivityTick > idleThresholdMs)
            {
                _log.LogInformation("Idle timeout for connection #{Id} ({Account})",
                    state.Id, state.AccountName);
                state.MarkClosing();
            }
        }
    }

    private static void MaybeRunDeterminismGuardrail()
    {
        if (!_config.MulticoreDeterminismDebug || _tickCounter > 2000)
            return;

        string hash = ComputeDeterminismHash();
        if (_tickCounter == 2000)
        {
            _log.LogInformation("[determinism] hash at tick {Tick}: {Hash}", _tickCounter, hash);

            if (!string.IsNullOrWhiteSpace(_config.MulticoreDeterminismExpectedHash) &&
                !string.Equals(hash, _config.MulticoreDeterminismExpectedHash, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogError(
                    "[determinism] hash mismatch! expected={Expected} actual={Actual}",
                    _config.MulticoreDeterminismExpectedHash,
                    hash);
            }
        }
    }

    private static string ComputeDeterminismHash()
    {
        var sb = new StringBuilder();
        sb.Append("tick:").Append(_tickCounter).Append('\n');
        sb.Append("world:").Append(_world.ComputeStateHash()).Append('\n');
        foreach (var client in _clients.Values.OrderBy(c => c.NetState.Id))
        {
            sb.Append(client.NetState.Id).Append(':');
            if (client.Character != null)
                sb.Append(client.Character.Uid.Value).Append('@').Append(client.Character.X).Append(',').Append(client.Character.Y).Append(',').Append(client.Character.Z);
            sb.Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private static long ToMicroseconds(long stopwatchTicks)
    {
        return (long)(stopwatchTicks * (1_000_000.0 / Stopwatch.Frequency));
    }

    /// <summary>
    /// Parse the [SPHERE] LogFileLevel knob into either a single-value
    /// minimum threshold OR a discrete whitelist set.
    ///   "Warning"                            → minLevel=Warning, whitelist=null
    ///   "Verbose | Warning | Error | Fatal"  → minLevel=Verbose,
    ///                                          whitelist={Verbose, Warning,
    ///                                                     Error, Fatal}
    /// Unknown / empty input falls back to Warning-threshold.  When a
    /// whitelist is returned the caller must wire a Serilog
    /// <c>Filter.ByIncludingOnly</c> against it; the min-level is set to
    /// the lowest whitelisted entry so the sink doesn't pre-drop those
    /// events upstream.
    /// </summary>
}
