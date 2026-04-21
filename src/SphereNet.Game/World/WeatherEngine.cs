using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Core.Configuration;

namespace SphereNet.Game.World;

/// <summary>
/// Weather type for regions. Maps to WEATHER_TYPE in Source-X.
/// </summary>
public enum WeatherType : byte
{
    None = 0xFF,
    Rain = 0x00,
    Storm = 0x01,
    Snow = 0x02,
}

/// <summary>
/// Season type. Maps to SEASON_TYPE in Source-X.
/// </summary>
public enum SeasonType : byte
{
    Spring = 0,
    Summer = 1,
    Fall = 2,
    Winter = 3,
    Desolation = 4,
}

/// <summary>
/// Weather and light engine. Manages weather effects per region,
/// global season cycling, and dungeon light levels.
/// Maps to CWorld::OnTick_Weather / CWorld::GetSeason in Source-X.
/// </summary>
public sealed class WeatherEngine
{
    private readonly GameWorld _world;
    private readonly Random _rand = new();

    // Region-specific weather state
    private readonly Dictionary<string, WeatherState> _regionWeather = [];

    // Reusable scratch set for OnTick's active-region pass; allocated
    // once to avoid per-tick GC churn when 500+ players are online.
    private readonly HashSet<Regions.Region> _activeRegionsScratch = [];

    // Global season
    private SeasonType _currentSeason = SeasonType.Spring;
    private long _lastSeasonChangeTick;
    private SeasonMode _seasonMode = SeasonMode.Auto;

    /// <summary>Season change interval in milliseconds (default: 30 minutes).</summary>
    public int SeasonChangeInterval { get; set; } = 30 * 60 * 1000;

    public SeasonType CurrentSeason => _currentSeason;
    public SeasonMode CurrentSeasonMode => _seasonMode;

    public WeatherEngine(GameWorld world)
    {
        _world = world;
        _world.CurrentSeason = (byte)_currentSeason;
        _lastSeasonChangeTick = Environment.TickCount64;
    }

    public void Configure(SeasonMode mode, SeasonType defaultSeason, int intervalMs)
    {
        _seasonMode = mode;
        SeasonChangeInterval = Math.Max(0, intervalMs);
        SetSeason(defaultSeason, resetCycleTimer: true);
    }

    public bool SetSeason(SeasonType season, bool resetCycleTimer = true)
    {
        bool changed = _currentSeason != season || _world.CurrentSeason != (byte)season;
        _currentSeason = season;
        _world.CurrentSeason = (byte)season;
        if (resetCycleTimer)
            _lastSeasonChangeTick = Environment.TickCount64;
        return changed;
    }

    /// <summary>
    /// Get the current weather for a region. Returns (type, intensity, temp).
    /// </summary>
    public (WeatherType Type, byte Intensity, byte Temperature) GetWeatherForRegion(string regionName)
    {
        if (_regionWeather.TryGetValue(regionName, out var state))
            return (state.Type, state.Intensity, state.Temperature);
        return (WeatherType.None, 0, 20);
    }

    /// <summary>
    /// Set weather for a specific region.
    /// </summary>
    public void SetRegionWeather(string regionName, WeatherType type, byte intensity, byte temp)
    {
        _regionWeather[regionName] = new WeatherState
        {
            Type = type,
            Intensity = intensity,
            Temperature = temp,
            EndTick = Environment.TickCount64 + 300_000 // 5 min default duration
        };
    }

    /// <summary>
    /// Periodic weather update. Called from game tick.
    /// Returns true if season changed.
    /// </summary>
    public bool OnTick()
    {
        long now = Environment.TickCount64;

        // Expire region weather
        var expired = new List<string>();
        foreach (var (name, state) in _regionWeather)
        {
            if (now >= state.EndTick)
                expired.Add(name);
        }
        foreach (var name in expired)
            _regionWeather.Remove(name);

        // Random weather generation — only for regions that actually
        // have an online player in them. The old full _world.Regions
        // scan burned measurable time on large shards where thousands
        // of named regions exist (every named house, guild hall, tiny
        // dungeon zone); weather in a region with zero players has no
        // observer so rolling the dice there is pure waste.
        _activeRegionsScratch.Clear();
        foreach (var player in _world.OnlinePlayers)
        {
            if (player.IsDeleted || !player.IsOnline) continue;
            var region = _world.FindRegion(player.Position);
            if (region != null) _activeRegionsScratch.Add(region);
        }
        foreach (var region in _activeRegionsScratch)
        {
            if (string.IsNullOrEmpty(region.Name)) continue;
            if (_regionWeather.ContainsKey(region.Name)) continue;
            if (region.IsFlag(RegionFlag.Underground)) continue; // no weather underground

            if (_rand.Next(1000) < 5) // 0.5% chance per tick
            {
                var type = _currentSeason == SeasonType.Winter ? WeatherType.Snow : WeatherType.Rain;
                byte intensity = (byte)_rand.Next(10, 50);
                byte temp = _currentSeason switch
                {
                    SeasonType.Spring => 15,
                    SeasonType.Summer => 25,
                    SeasonType.Fall => 10,
                    SeasonType.Winter => 0,
                    _ => 20
                };
                SetRegionWeather(region.Name, type, intensity, temp);
            }
        }

        if (_seasonMode != SeasonMode.Auto || SeasonChangeInterval <= 0)
            return false;

        if (now - _lastSeasonChangeTick < SeasonChangeInterval)
            return false;

        var nextSeason = (SeasonType)(((int)_currentSeason + 1) % 4);
        return SetSeason(nextSeason, resetCycleTimer: true);
    }

    /// <summary>
    /// Get the appropriate light level for a position (considers dungeon/underground).
    /// </summary>
    public byte GetLightLevel(Point3D pos)
    {
        var region = _world.FindRegion(pos);
        if (region != null && region.IsFlag(RegionFlag.Underground))
            return 28; // dungeon darkness

        return _world.GlobalLight;
    }

    /// <summary>
    /// Get music ID for a region. Returns 0 if no music assigned.
    /// </summary>
    public ushort GetRegionMusic(Regions.Region? region)
    {
        if (region == null) return 0;

        // Check region TAG for music
        if (region.TryGetTag("MUSIC", out string? musicStr) && ushort.TryParse(musicStr, out ushort musicId))
            return musicId;

        return 0;
    }

    private class WeatherState
    {
        public WeatherType Type;
        public byte Intensity;
        public byte Temperature;
        public long EndTick;
    }
}
