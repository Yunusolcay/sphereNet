using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Manages multiple bot clients for stress testing.
/// Creates bots that connect via real TCP and simulate player behavior.
/// </summary>
public sealed class BotEngine : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, BotClient> _bots = new();
    private CancellationTokenSource? _globalCts;
    private int _nextBotId;
    private bool _disposed;
    private BotSpawnCity _spawnCity = BotSpawnCity.All;
    private BotBehavior _lastBehavior = BotBehavior.FullSimulation;
    private int _lastCount;

    // Stats
    private long _lastStatsLogMs;
    private int _lastPacketsSent;
    private int _lastPacketsReceived;

    /// <summary>Spawn locations for bots by city.</summary>
    public static readonly Dictionary<BotSpawnCity, (short X, short Y, sbyte Z)[]> SpawnLocations = new()
    {
        [BotSpawnCity.Britain] = [
            (1495, 1629, 10), (1428, 1695, 0), (1475, 1645, 20), (1522, 1756, 5),
            (1416, 1696, 0), (1467, 1528, 30), (1512, 1610, 20), (1438, 1550, 22)
        ],
        [BotSpawnCity.Trinsic] = [
            (1823, 2821, 0), (1867, 2780, 0), (1914, 2725, 0), (1856, 2684, 0),
            (1996, 2765, 0), (2050, 2855, 0), (1907, 2804, 0), (1838, 2745, 0)
        ],
        [BotSpawnCity.Moonglow] = [
            (4408, 1168, 0), (4442, 1122, 0), (4471, 1062, 0), (4551, 1107, 0),
            (4516, 1161, 0), (4487, 1212, 0), (4405, 1074, 0), (4536, 1045, 0)
        ],
        [BotSpawnCity.Yew] = [
            (542, 985, 0), (612, 815, 0), (573, 880, 0), (471, 1002, 0),
            (630, 865, 0), (553, 927, 0), (504, 974, 0), (582, 838, 0)
        ],
        [BotSpawnCity.Minoc] = [
            (2498, 392, 15), (2525, 509, 0), (2576, 469, 15), (2467, 439, 15),
            (2503, 565, 0), (2529, 422, 15), (2559, 525, 0), (2485, 490, 15)
        ],
        [BotSpawnCity.Vesper] = [
            (2899, 676, 0), (2950, 816, 0), (3002, 743, 6), (2871, 732, 0),
            (2925, 854, 0), (2978, 689, 0), (2848, 792, 0), (2964, 776, 6)
        ],
        [BotSpawnCity.Skara] = [
            (596, 2138, 0), (640, 2067, 0), (620, 2200, 0), (567, 2105, 0),
            (662, 2135, 0), (604, 2172, 0), (575, 2078, 0), (648, 2188, 0)
        ],
        [BotSpawnCity.Jhelom] = [
            (1417, 3821, 0), (1324, 3773, 0), (1380, 3850, 0), (1462, 3779, 0),
            (1350, 3815, 0), (1404, 3755, 0), (1441, 3842, 0), (1298, 3802, 0)
        ]
    };

    public int TotalBots => _bots.Count;
    public int ActiveBots => _bots.Values.Count(b => b.State == BotState.Playing);
    public int ConnectingBots => _bots.Values.Count(b => b.State == BotState.Connecting || b.State == BotState.LoggingIn);
    public int TotalPacketsSent => _bots.Values.Sum(b => b.PacketsSent);
    public int TotalPacketsReceived => _bots.Values.Sum(b => b.PacketsReceived);
    public long TotalBytesSent => _bots.Values.Sum(b => (long)b.BytesSent);
    public long TotalBytesReceived => _bots.Values.Sum(b => (long)b.BytesReceived);
    public BotSpawnCity SpawnCity => _spawnCity;

    public BotEngine(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Set spawn city for new bots.</summary>
    public void SetSpawnCity(BotSpawnCity city)
    {
        _spawnCity = city;
        _logger.LogInformation("[BOT] Spawn city set to: {City}", city);
    }

    /// <summary>Get a random spawn location based on current city setting.</summary>
    public (short X, short Y, sbyte Z) GetRandomSpawnLocation(Random rng)
    {
        var cities = _spawnCity == BotSpawnCity.All
            ? SpawnLocations.Keys.ToArray()
            : [_spawnCity];

        var city = cities[rng.Next(cities.Length)];
        var locs = SpawnLocations[city];
        return locs[rng.Next(locs.Length)];
    }

    /// <summary>Get all bot account names for cleanup.</summary>
    public IEnumerable<string> GetBotAccountNames()
    {
        for (int i = 1; i <= _nextBotId; i++)
            yield return $"bot{i:D4}";
    }

    /// <summary>Restart previously stopped bots.</summary>
    public async Task RestartBotsAsync(string host, int port)
    {
        if (_lastCount <= 0)
        {
            _logger.LogWarning("[BOT] No previous bot session to restart. Use .bot <count> first.");
            return;
        }
        await SpawnBotsAsync(_lastCount, _lastBehavior, host, port);
    }

    /// <summary>
    /// Spawn bots that connect to the server via TCP.
    /// </summary>
    public async Task SpawnBotsAsync(int count, BotBehavior behavior, string host = "127.0.0.1", int port = 2593)
    {
        _globalCts?.Cancel();
        _globalCts = new CancellationTokenSource();
        var ct = _globalCts.Token;

        _lastCount = count;
        _lastBehavior = behavior;

        string cityInfo = _spawnCity == BotSpawnCity.All ? "all cities" : _spawnCity.ToString();
        _logger.LogInformation("[BOT] Starting {Count} bots with {Behavior} behavior in {City}...", 
            count, behavior, cityInfo);
        
        long startMs = Environment.TickCount64;
        int connected = 0;
        int failed = 0;

        // Spawn in batches to avoid overwhelming the server
        const int batchSize = 50;
        const int batchDelayMs = 100;

        for (int i = 0; i < count && !ct.IsCancellationRequested; i += batchSize)
        {
            int batchCount = Math.Min(batchSize, count - i);
            var tasks = new List<Task<bool>>();

            for (int j = 0; j < batchCount; j++)
            {
                int botId = Interlocked.Increment(ref _nextBotId);
                var bot = new BotClient(botId, _logger);
                _bots[botId] = bot;
                
                tasks.Add(Task.Run(async () =>
                {
                    bool success = await bot.ConnectAndLoginAsync(host, port, ct);
                    if (success)
                    {
                        bot.StartBehavior(behavior, ct);
                        return true;
                    }
                    return false;
                }, ct));
            }

            var results = await Task.WhenAll(tasks);
            connected += results.Count(r => r);
            failed += results.Count(r => !r);

            // Progress log every batch
            _logger.LogInformation("[BOT] Progress: {Connected}/{Total} connected, {Failed} failed",
                connected, i + batchCount, failed);

            if (i + batchSize < count)
                await Task.Delay(batchDelayMs, ct);
        }

        long elapsedMs = Environment.TickCount64 - startMs;
        
        // Log state breakdown
        int playing = _bots.Values.Count(b => b.State == BotState.Playing);
        int connecting = _bots.Values.Count(b => b.State == BotState.Connecting);
        int loggingIn = _bots.Values.Count(b => b.State == BotState.LoggingIn);
        int disconnected = _bots.Values.Count(b => b.State == BotState.Disconnected);
        _logger.LogInformation("[BOT] State breakdown: Playing={Playing}, Connecting={Connecting}, LoggingIn={LoggingIn}, Disconnected={Disconnected}",
            playing, connecting, loggingIn, disconnected);
        
        _logger.LogInformation("[BOT] Spawn complete in {Elapsed}ms. Connected: {Connected}, Failed: {Failed}",
            elapsedMs, connected, failed);
    }

    /// <summary>
    /// Stop all bots (disconnect TCP but keep account tracking for cleanup).
    /// </summary>
    public void StopAllBots()
    {
        _logger.LogInformation("[BOT] Stopping all {Count} bots...", _bots.Count);
        
        _globalCts?.Cancel();

        foreach (var bot in _bots.Values)
        {
            try { bot.Dispose(); } catch { }
        }
        _bots.Clear();

        _logger.LogInformation("[BOT] All bots stopped. Use .bot clean to remove characters from world.");
    }

    /// <summary>
    /// Reset bot ID counter (call after cleaning characters).
    /// </summary>
    public void ResetBotCounter()
    {
        _nextBotId = 0;
        _lastCount = 0;
        _logger.LogInformation("[BOT] Bot counter reset.");
    }

    /// <summary>
    /// Get the highest bot ID created (for cleanup range).
    /// </summary>
    public int GetMaxBotId() => _nextBotId;

    /// <summary>
    /// Get statistics for logging.
    /// </summary>
    public BotStats GetStats()
    {
        long nowMs = Environment.TickCount64;
        long elapsedMs = nowMs - _lastStatsLogMs;
        if (elapsedMs <= 0) elapsedMs = 1;

        int currentSent = TotalPacketsSent;
        int currentReceived = TotalPacketsReceived;

        float packetsPerSecIn = (currentReceived - _lastPacketsReceived) * 1000f / elapsedMs;
        float packetsPerSecOut = (currentSent - _lastPacketsSent) * 1000f / elapsedMs;

        _lastStatsLogMs = nowMs;
        _lastPacketsSent = currentSent;
        _lastPacketsReceived = currentReceived;

        return new BotStats
        {
            TotalBots = TotalBots,
            ActiveBots = ActiveBots,
            ConnectingBots = ConnectingBots,
            TotalPacketsSent = currentSent,
            TotalPacketsReceived = currentReceived,
            TotalBytesSent = TotalBytesSent,
            TotalBytesReceived = TotalBytesReceived,
            PacketsPerSecIn = packetsPerSecIn,
            PacketsPerSecOut = packetsPerSecOut
        };
    }

    /// <summary>
    /// Log current stats.
    /// </summary>
    public void LogStats()
    {
        var stats = GetStats();
        _logger.LogInformation(
            "[bot_stats] bots={Active}/{Total} pkt_in={PktIn} pkt_out={PktOut} pps_in={PpsIn:F0} pps_out={PpsOut:F0} bytes_in={BytesIn}KB bytes_out={BytesOut}KB",
            stats.ActiveBots,
            stats.TotalBots,
            stats.TotalPacketsReceived,
            stats.TotalPacketsSent,
            stats.PacketsPerSecIn,
            stats.PacketsPerSecOut,
            stats.TotalBytesReceived / 1024,
            stats.TotalBytesSent / 1024);
    }

    /// <summary>
    /// Clean up disconnected bots.
    /// </summary>
    public int CleanupDisconnected()
    {
        var toRemove = _bots.Where(kv => kv.Value.State == BotState.Disconnected).Select(kv => kv.Key).ToList();
        foreach (var id in toRemove)
        {
            if (_bots.TryRemove(id, out var bot))
                bot.Dispose();
        }
        return toRemove.Count;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAllBots();
        _globalCts?.Dispose();
    }
}

public struct BotStats
{
    public int TotalBots;
    public int ActiveBots;
    public int ConnectingBots;
    public int TotalPacketsSent;
    public int TotalPacketsReceived;
    public long TotalBytesSent;
    public long TotalBytesReceived;
    public float PacketsPerSecIn;
    public float PacketsPerSecOut;
}

public enum BotSpawnCity
{
    All,
    Britain,
    Trinsic,
    Moonglow,
    Yew,
    Minoc,
    Vesper,
    Skara,
    Jhelom
}
