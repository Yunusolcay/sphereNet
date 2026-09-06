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

    // Region-specific weather state, keyed by the region's own identity. The visible
    // NAME used to be the key, so two Britains on two facets shared one sky - and a
    // rename silently orphaned a region's weather.
    private readonly Dictionary<(byte Map, uint RegionUid), WeatherState> _regionWeather = [];

    // Reusable scratch set for OnTick's active-region pass; allocated
    // once to avoid per-tick GC churn when 500+ players are online.
    private readonly HashSet<Regions.Region> _activeRegionsScratch = [];

    // Global season
    private SeasonType _currentSeason = SeasonType.Spring;
    private long _lastSeasonChangeTick;
    private SeasonMode _seasonMode = SeasonMode.Auto;

    /// <summary>The sector climate covering a region: the chance of precipitation and,
    /// given precipitation, the chance it falls as snow (Source-X RAINCHANCE /
    /// COLDCHANCE).</summary>
    public Func<Regions.Region, (int Rain, int Cold)?>? GetClimate { get; set; }

    /// <summary>Source-X SetDefaultWeatherChance's mid-latitude values, used when no
    /// sector answers for the region.</summary>
    private const int DefaultRainChance = 15;
    private const int DefaultColdChance = 5;

    /// <summary>Season change interval in milliseconds (default: 30 minutes).</summary>
    public int SeasonChangeInterval { get; set; } = 30 * 60 * 1000;

    public SeasonType CurrentSeason => _currentSeason;
    public SeasonMode CurrentSeasonMode => _seasonMode;

    /// <summary>Fired when weather changes in a region. The REGION is passed, not its
    /// name: the broadcast has to reach exactly the players in that region and no
    /// same-named region on another map.</summary>
    public Action<Regions.Region, WeatherType, byte, byte>? OnWeatherChanged { get; set; }

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
    public (WeatherType Type, byte Intensity, byte Temperature) GetWeatherForRegion(Regions.Region? region)
    {
        if (region != null && _regionWeather.TryGetValue(KeyOf(region), out var state))
            return (state.Type, state.Intensity, state.Temperature);
        return (WeatherType.None, 0, 20);
    }

    private static (byte, uint) KeyOf(Regions.Region region) => (region.MapIndex, region.Uid);

    /// <summary>
    /// Set weather for a specific region.
    /// </summary>
    public void SetRegionWeather(Regions.Region region, WeatherType type, byte intensity, byte temp)
    {
        _regionWeather[KeyOf(region)] = new WeatherState
        {
            Region = region,
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
        var expired = new List<(byte, uint)>();
        foreach (var (key, state) in _regionWeather)
        {
            if (now >= state.EndTick)
                expired.Add(key);
        }
        foreach (var key in expired)
        {
            var state = _regionWeather[key];
            _regionWeather.Remove(key);
            if (state.Region != null)
                OnWeatherChanged?.Invoke(state.Region, WeatherType.None, 0, 20);
        }

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
            if (_regionWeather.ContainsKey(KeyOf(region))) continue;
            if (region.IsFlag(RegionFlag.Underground)) continue; // no weather underground

            if (_rand.Next(1000) >= 5) continue; // 0.5% chance per tick

            // The local climate decides, not a flat table. Source-X rolls the sector's
            // own RAINCHANCE for precipitation and then its COLDCHANCE for snow
            // (GetWeatherCalc, CSector.cpp:857); both properties were accepted and
            // stored here and then never read, so a region prepared as rainless still
            // rained and a cold coast never saw snow out of season.
            var climate = GetClimate?.Invoke(region);
            int rainChance = climate?.Rain ?? DefaultRainChance;
            int coldChance = climate?.Cold ?? DefaultColdChance;

            int roll = _rand.Next(100);
            if (roll >= rainChance) continue;

            byte temp = _currentSeason switch
            {
                SeasonType.Spring => 15,
                SeasonType.Summer => 25,
                SeasonType.Fall => 10,
                SeasonType.Winter => 0,
                _ => 20
            };
            WeatherType type;
            if (coldChance > 0 && _rand.Next(100) <= coldChance)
                type = WeatherType.Snow;
            else if (temp <= 0)
                type = WeatherType.Snow;
            else
                type = _rand.Next(100) < 20 ? WeatherType.Storm : WeatherType.Rain;
            byte intensity = type == WeatherType.Storm
                ? (byte)_rand.Next(50, 90)
                : (byte)_rand.Next(10, 50);
            SetRegionWeather(region, type, intensity, temp);
            OnWeatherChanged?.Invoke(region, type, intensity, temp);
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
        return _world.GetLightLevel(pos);
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
        public Regions.Region? Region;
        public WeatherType Type;
        public byte Intensity;
        public byte Temperature;
        public long EndTick;
    }
}
