using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Stress-test harness. Generates a large population of items and NPCs
/// scattered across the main Britannia towns, collects runtime metrics
/// (RAM, GC counts, tick pacing), and cleans up on demand.
///
/// Objects created here carry a <c>STRESS_TEST=1</c> tag so cleanup can
/// target them without touching organic world state. Work is batched per
/// tick to keep the main loop responsive — generating 3.75M objects in a
/// single frame would stall the server for minutes.
/// </summary>
public sealed class StressTestEngine
{
    private readonly GameWorld _world;
    private readonly ILogger<StressTestEngine> _logger;
    private readonly Random _rng = new(12345); // deterministic seed

    // Batch budget per main-loop iteration (NOT per 250ms tick — OnTick is
    // called every iteration, which is ~1ms under TickSleepMode=0/2). With
    // these values a 1M-item run finishes in a few seconds; larger batches
    // risk GC pressure spikes so keep them modest.
    private int _itemsPerTick = 10000;
    private int _npcsPerTick  = 2000;

    private int _itemsRemaining;
    private int _npcsRemaining;
    private int _itemsCreated;
    private int _npcsCreated;
    private long _startTimeMs;
    private long _lastProgressLogMs;

    /// <summary>True while generation is in progress.</summary>
    public bool IsGenerating => _itemsRemaining > 0 || _npcsRemaining > 0;

    /// <summary>Tag marker written to every stress-test object.</summary>
    public const string StressTag = "STRESS_TEST";

    // Hotspots on map 0 — towns, dungeons, farms, crossroads. Radii are bigger
    // than the actual town footprint so 94K NPCs don't pile up at one density.
    // Previous 8-town layout produced ~15 NPCs/tile in Britain (disk π·45² ≈ 6K
    // tiles, ~94K NPCs per town); this list plus a wilderness-scatter weight
    // drops the density to <1/tile in every hotspot.
    private static readonly (string Name, short X, short Y, short Radius)[] Hotspots =
    [
        ("Britain",            1495, 1620, 140),
        ("Minoc",              2500,  570, 100),
        ("Yew",                 650,  800, 120),
        ("Vesper",             2900,  690, 100),
        ("Trinsic",            1820, 2820, 100),
        ("Moonglow",           4440, 1140, 100),
        ("Skara Brae",          590, 2220,  90),
        ("Magincia",           3720, 2180,  80),
        ("Ocllo",              3650, 2660,  70),
        ("Jhelom",             1370, 3780,  80),
        ("Cove",               2280, 1180,  70),
        ("Serpents Hold",      2890, 3430,  70),
        ("Nujelm",             3750, 1280,  70),
        ("Buccaneers Den",     2720, 2170,  70),
        ("Papua",              5670, 3270,  60),
        ("Delucia",            5250, 3990,  60),
        ("Wind",               5200,   36,  50),
        // Crossroads / landmarks
        ("Britain Farms",      1680, 1440, 100),
        ("Trinsic Passage",    1760, 2500, 100),
        ("Yew Crossroads",     1050, 1200, 100),
        ("Minoc Mountains",    2350,  800, 100),
        // Dungeon entrances
        ("Despise Entrance",   1300,  550,  50),
        ("Destard Entrance",   1340, 2560,  50),
        ("Deceit Entrance",    4110,  430,  50),
        ("Shame Entrance",      500, 1500,  50),
        ("Wrong Entrance",     2040,  230,  50),
    ];

    // Fraction of placements that bypass hotspots and scatter randomly across
    // the full Felucca bounding box. Keeps the wilderness populated instead
    // of packing every NPC into a town.
    private const double WildernessScatterChance = 0.35;
    private const int MapMinX = 0, MapMaxX = 5119;
    private const int MapMinY = 0, MapMaxY = 4095;

    // Common item graphics (loot-style clutter).
    private static readonly ushort[] ItemBaseIds =
    [
        0x0EED, // gold coin
        0x0F3F, // arrow
        0x1BFB, // bolt
        0x0F7D, // small potion
        0x0E21, // bandage
        0x1F4B, // scroll blank
        0x1BF5, // ingot
        0x1776, // boards
        0x0F85, // ruby
        0x0F8D, // emerald
        0x097B, // raw ribs
        0x0F10, // gold chunk
        0x170F, // leather
        0x0F7E, // mortar
        0x0DCA, // fishing pole
    ];

    // Common NPC bodies (low-tier mix).
    private static readonly ushort[] NpcBodyIds =
    [
        0x0001, // rat
        0x0021, // chicken
        0x00CD, // rabbit
        0x00C9, // cat
        0x00E1, // dog
        0x001D, // small snake
        0x00DD, // pig
        0x00E2, // cow
        0x0019, // llama
        0x0022, // goat
    ];

    public StressTestEngine(GameWorld world, ILoggerFactory lf)
    {
        _world = world;
        _logger = lf.CreateLogger<StressTestEngine>();
    }

    /// <summary>Begin generating the requested population. Per-tick batch
    /// sizes are optional — defaults are tuned for a 250ms tick.</summary>
    public void QueueGenerate(int items, int npcs, int itemsPerTick = 10000, int npcsPerTick = 2000)
    {
        if (IsGenerating)
        {
            _logger.LogWarning("Stress generation already in progress ({Items} items / {Npcs} NPCs remaining)",
                _itemsRemaining, _npcsRemaining);
            return;
        }

        _itemsRemaining = Math.Max(0, items);
        _npcsRemaining  = Math.Max(0, npcs);
        _itemsPerTick   = Math.Max(100, itemsPerTick);
        _npcsPerTick    = Math.Max(50, npcsPerTick);
        _itemsCreated   = 0;
        _npcsCreated    = 0;
        _startTimeMs    = Environment.TickCount64;
        _lastProgressLogMs = _startTimeMs;

        // Suppress dirty-notify during bulk generation. Otherwise every new
        // Item/Character's Position setter adds to _dirtyObjects and the
        // fast-path drain spins on 1M+ entries per loop iteration — observed
        // as 150-500ms ping spikes while .stress runs. The dirty set is
        // cleared explicitly so in-flight fast-path work doesn't lag behind.
        _world.SuppressDirtyNotify = true;
        _world.ConsumeDirtyObjects();

        _logger.LogInformation(
            "[STRESS] Queued generation: {Items} items, {Npcs} NPCs (batch {IB}/{NB} per iter, dirty-notify suppressed)",
            items, npcs, _itemsPerTick, _npcsPerTick);
    }

    /// <summary>Called each server tick. Creates up to the per-tick batch
    /// budget and emits periodic progress logs. Safe to call when idle.</summary>
    public void OnTick()
    {
        if (!IsGenerating) return;

        int itemBatch = Math.Min(_itemsPerTick, _itemsRemaining);
        for (int i = 0; i < itemBatch; i++)
            CreateRandomItem();
        _itemsRemaining -= itemBatch;
        _itemsCreated += itemBatch;

        int npcBatch = Math.Min(_npcsPerTick, _npcsRemaining);
        for (int i = 0; i < npcBatch; i++)
            CreateRandomNpc();
        _npcsRemaining -= npcBatch;
        _npcsCreated += npcBatch;

        long now = Environment.TickCount64;
        if (now - _lastProgressLogMs >= 5000 || !IsGenerating)
        {
            long elapsed = now - _startTimeMs;
            string line = $"[STRESS] Progress: {_itemsCreated}/{_itemsCreated + _itemsRemaining} items, " +
                          $"{_npcsCreated}/{_npcsCreated + _npcsRemaining} NPCs — " +
                          $"elapsed {elapsed / 1000}s, RSS {Environment.WorkingSet / (1024 * 1024)}MB";
            _logger.LogInformation("{Line}", line);
            Console.WriteLine(line);
            _lastProgressLogMs = now;
        }

        if (!IsGenerating)
        {
            // Re-enable dirty propagation so normal gameplay resumes.
            _world.SuppressDirtyNotify = false;
            _world.ConsumeDirtyObjects();

            long totalMs = now - _startTimeMs;
            string done = $"[STRESS] Generation complete in {totalMs / 1000}s. " +
                          $"Created {_itemsCreated} items, {_npcsCreated} NPCs.";
            _logger.LogInformation("{Line}", done);
            Console.WriteLine(done);
            LogReport();
        }
    }

    private void CreateRandomItem()
    {
        var (x, y) = RandomTownPoint();
        sbyte z = _world.MapData?.GetEffectiveZ(0, x, y) ?? 0;
        var item = _world.CreateItem();
        item.BaseId = ItemBaseIds[_rng.Next(ItemBaseIds.Length)];
        item.Amount = (ushort)(1 + _rng.Next(5));
        item.SetTag(StressTag, "1");
        _world.PlaceItem(item, new Point3D(x, y, z, 0));
    }

    private void CreateRandomNpc()
    {
        var (x, y) = RandomTownPoint();
        sbyte z = _world.MapData?.GetEffectiveZ(0, x, y) ?? 0;
        var npc = _world.CreateCharacter();
        npc.BodyId = NpcBodyIds[_rng.Next(NpcBodyIds.Length)];
        npc.IsPlayer = false;
        npc.Str = 50; npc.Dex = 50; npc.Int = 10;
        npc.MaxHits = 50; npc.Hits = 50;
        npc.Name = "stressling";
        npc.SetTag(StressTag, "1");
        _world.PlaceCharacter(npc, new Point3D(x, y, z, 0));
    }

    private (short X, short Y) RandomTownPoint()
    {
        if (_rng.NextDouble() < WildernessScatterChance)
        {
            short wx = (short)_rng.Next(MapMinX, MapMaxX + 1);
            short wy = (short)_rng.Next(MapMinY, MapMaxY + 1);
            return (wx, wy);
        }

        var town = Hotspots[_rng.Next(Hotspots.Length)];
        // Uniform disk sample: sqrt for radial uniformity.
        double r = town.Radius * Math.Sqrt(_rng.NextDouble());
        double theta = _rng.NextDouble() * 2 * Math.PI;
        int x = (int)(town.X + r * Math.Cos(theta));
        int y = (int)(town.Y + r * Math.Sin(theta));
        if (x < MapMinX) x = MapMinX; else if (x > MapMaxX) x = MapMaxX;
        if (y < MapMinY) y = MapMinY; else if (y > MapMaxY) y = MapMaxY;
        return ((short)x, (short)y);
    }

    /// <summary>Emit a full runtime report: object counts, managed heap
    /// breakdown, GC collection counts, working set, tick telemetry.
    /// Writes to both the logger (file/Serilog sinks) and Console.Out so the
    /// report is always visible regardless of log level or console routing.</summary>
    public void LogReport()
    {
        var proc = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();

        long workingSetMB = Environment.WorkingSet / (1024 * 1024);
        long managedHeapMB = GC.GetTotalMemory(false) / (1024 * 1024);
        long committedMB = gcInfo.TotalCommittedBytes / (1024 * 1024);
        long highMemPct = gcInfo.HighMemoryLoadThresholdBytes > 0
            ? gcInfo.MemoryLoadBytes * 100 / gcInfo.HighMemoryLoadThresholdBytes
            : 0;
        double lastPauseMs = gcInfo.PauseDurations.IsEmpty
            ? 0
            : gcInfo.PauseDurations[0].TotalMilliseconds;

        var lines = new[]
        {
            "──────────── STRESS REPORT ────────────",
            $"World: {_world.TotalChars} chars, {_world.TotalItems} items, {_world.TotalObjects} total",
            $"Memory: WorkingSet {workingSetMB}MB, ManagedHeap {managedHeapMB}MB, Committed {committedMB}MB",
            $"GC: Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)}  HighMem={highMemPct}%  Pause(last)={lastPauseMs:F0}ms",
            $"CPU: {(int)proc.TotalProcessorTime.TotalSeconds}s total process time, Threads={proc.Threads.Count}",
            "────────────────────────────────────────",
        };

        foreach (var line in lines)
        {
            _logger.LogInformation("{Line}", line);
            Console.WriteLine(line);
        }
    }

    /// <summary>Delete every object tagged with STRESS_TEST. Work is batched
    /// internally — on a 3M+ delete even a single tick would stall the loop,
    /// so cleanup runs across multiple ticks via RunCleanupBatch().</summary>
    public int QueueCleanup()
    {
        int queued = 0;
        foreach (var obj in _world.GetAllObjects())
        {
            if (obj.IsDeleted) continue;
            if (!obj.TryGetTag(StressTag, out _)) continue;
            _cleanupQueue.Enqueue(obj.Uid.Value);
            queued++;
        }
        _logger.LogInformation("[STRESS] Cleanup queued: {Count} stress-tagged objects", queued);
        // Suppress dirty notify during bulk delete for the same reason as
        // generation — DeleteObject / sector removal shouldn't drown the
        // fast-path in millions of one-shot dirty entries.
        _world.SuppressDirtyNotify = true;
        _world.ConsumeDirtyObjects();
        _startTimeMs = Environment.TickCount64;
        _lastProgressLogMs = _startTimeMs;
        _cleanupInitial = queued;
        return queued;
    }

    private readonly Queue<uint> _cleanupQueue = new();
    private int _cleanupInitial;
    private const int CleanupBatchSize = 50000;

    /// <summary>Is a cleanup in progress? Drives the main loop tick hook.</summary>
    public bool IsCleaning => _cleanupQueue.Count > 0;

    /// <summary>Delete up to CleanupBatchSize objects per call. Call from tick.</summary>
    public void TickCleanup()
    {
        if (_cleanupQueue.Count == 0) return;

        int budget = Math.Min(CleanupBatchSize, _cleanupQueue.Count);
        for (int i = 0; i < budget; i++)
        {
            uint uid = _cleanupQueue.Dequeue();
            var serial = new Core.Types.Serial(uid);
            // FindChar/FindItem are O(1) dictionary lookups; avoids scanning
            // the whole object table for every delete (which would turn the
            // cleanup into O(N²) — catastrophic on 3M+ entities).
            ObjBase? obj = (ObjBase?)_world.FindChar(serial) ?? _world.FindItem(serial);
            if (obj == null || obj.IsDeleted) continue;
            _world.DeleteObject(obj);
            if (obj is Item item)        item.Delete();
            else if (obj is Character c) c.Delete();
        }

        long now = Environment.TickCount64;
        if (now - _lastProgressLogMs >= 5000 || !IsCleaning)
        {
            int done = _cleanupInitial - _cleanupQueue.Count;
            string line = $"[STRESS] Cleanup progress: {done}/{_cleanupInitial} — " +
                          $"elapsed {(now - _startTimeMs) / 1000}s, RSS {Environment.WorkingSet / (1024 * 1024)}MB";
            _logger.LogInformation("{Line}", line);
            Console.WriteLine(line);
            _lastProgressLogMs = now;
        }

        if (_cleanupQueue.Count == 0)
        {
            _world.SuppressDirtyNotify = false;
            _world.ConsumeDirtyObjects();

            string line = "[STRESS] Cleanup complete. Running GC to release memory.";
            _logger.LogInformation("{Line}", line);
            Console.WriteLine(line);
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            LogReport();
        }
    }
}
