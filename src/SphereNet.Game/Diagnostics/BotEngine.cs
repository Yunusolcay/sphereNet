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
    private BotBehavior _lastBehavior = BotBehavior.SmartAI;
    private int _lastCount;

    // Stats
    private long _lastStatsLogMs;
    private int _lastPacketsSent;
    private int _lastPacketsReceived;

    /// <summary>City bounding boxes for bot spawning (minX, minY, maxX, maxY, defaultZ).</summary>
    public static readonly Dictionary<BotSpawnCity, (short MinX, short MinY, short MaxX, short MaxY, sbyte Z)> CityBounds = new()
    {
        [BotSpawnCity.Britain]  = (1400, 1520, 1540, 1760, 10),
        [BotSpawnCity.Trinsic]  = (1820, 2680, 2050, 2870,  0),
        [BotSpawnCity.Moonglow] = (4400, 1040, 4560, 1220,  0),
        [BotSpawnCity.Yew]      = ( 470,  810,  650, 1010,  0),
        [BotSpawnCity.Minoc]    = (2460,  380, 2580,  570, 15),
        [BotSpawnCity.Vesper]   = (2840,  670, 3010,  860,  0),
        [BotSpawnCity.Skara]    = ( 560, 2060,  670, 2210,  0),
        [BotSpawnCity.Jhelom]   = (1290, 3750, 1470, 3860,  0),
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

    /// <summary>Get a random spawn location within a city bounding box.</summary>
    public (short X, short Y, sbyte Z) GetRandomSpawnLocation(Random rng)
    {
        var cities = _spawnCity == BotSpawnCity.All
            ? CityBounds.Keys.ToArray()
            : [_spawnCity];

        var city = cities[rng.Next(cities.Length)];
        var b = CityBounds[city];
        short x = (short)rng.Next(b.MinX, b.MaxX + 1);
        short y = (short)rng.Next(b.MinY, b.MaxY + 1);
        return (x, y, b.Z);
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
