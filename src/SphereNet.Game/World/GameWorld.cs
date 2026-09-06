using Microsoft.Extensions.Logging;
using SphereNet.Core.Collections;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World.Regions;
using SphereNet.Game.World.Sectors;
using SphereNet.Core.Enums;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SphereNet.Game.World;

/// <summary>
/// The game world. Manages all objects, sectors, regions, and the world clock.
/// Maps to CWorld in Source-X.
///
/// THREADING CONTRACT (multicore tick — Program.Tick.RunMulticoreTick):
/// <list type="bullet">
/// <item><c>_objects</c>, <c>_uuidIndex</c>, <c>_containerIndex</c>, <c>_sectors</c>,
/// <c>_regions</c> and the other plain collections are single-writer: they may
/// only be MUTATED on the main tick thread (serial phases — packet handling,
/// ApplyDecision, world load). Parallel phases (NPC BuildDecision, view-delta
/// building) may READ them concurrently because the serial phases never run
/// at the same time as the parallel ones.</item>
/// <item><c>_dirtyObjects</c> and <c>_regionCache</c> are ConcurrentDictionary
/// because parallel phases write to them (ObjBase dirty-flag transitions call
/// <see cref="NotifyDirty"/>; region lookups populate the cache).</item>
/// <item>When adding a new parallel stage: it must not call CreateItem /
/// CreateCharacter / PlaceCharacter / MoveCharacter / DeleteObject — collect
/// intents in the stage and apply them in the serial phase, mirroring how
/// NpcAI decisions are applied.</item>
/// </list>
/// </summary>
public sealed class GameWorld
{
    private readonly UidTable _uidTable = new();
    private readonly Dictionary<uint, ObjBase> _objects = [];
    // Char-only mirror of _objects — cheap roster snapshots without copying
    // the full 100K+ object table (see GetAllCharactersSnapshot).
    private readonly HashSet<Character> _allCharacters = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Guid, ObjBase> _uuidIndex = [];
    private readonly Dictionary<uint, List<Item>> _containerIndex = [];
    private readonly Dictionary<int, Sector[,]> _sectors = [];
    private readonly List<Region> _regions = [];
    private readonly List<Room> _rooms = [];
    // Only successful (non-null) lookups are cached — see FindRegion for why a
    // null result must never poison an 8x8 cell.
    private readonly ConcurrentDictionary<long, Region> _regionCache = new();
    private readonly ILogger<GameWorld> _logger;
    private readonly List<ObjBase> _timerFSnapshot = [];
    // Active-set of objects that currently hold at least one TIMERF entry, so the
    // per-tick sweep (TickTimerF) iterates only timer-bearing objects instead of the
    // whole world. Populated from ObjBase.AddTimerF via ResolveWorld; self-prunes on
    // tick (empty/deleted) and on DeleteObject.
    private readonly HashSet<ObjBase> _objectsWithTimerF = [];
    // Index of on-ground (top-level) items, kept as a superset from the sole
    // sector.AddItem choke point in PlaceItem. The decay catch-up sweep iterates this
    // instead of the full object dictionary; stale entries (an item picked up via an
    // external sector.RemoveItem) are pruned lazily by the IsOnGround re-check.
    private readonly HashSet<Item> _groundItems = [];
    private readonly List<Item> _groundPrune = [];

    private long _tickCount;
    private int _totalChars;
    private int _totalItems;
    public event Action<ObjBase>? ObjectCreated;
    public event Action<ObjBase>? ObjectDeleting;
    public event Action<Character, Point3D>? CharacterMoved;
    public event Action<Character>? CharacterPlaced;
    public event Action<Character>? ClientLingerExpired;
    public Action<ObjBase, ObjBase.TimerFEntry>? TimerFExpired { get; set; }
    public Action<Character, byte>? OnSectorLight { get; set; }

    /// <summary>A sector's weather or season changed under a player standing in it.
    /// Source-X publishes both on the spot and fires @EnvironChange
    /// (CSector.cpp:879/904).</summary>
    public Action<Sector, Character>? OnSectorEnvironment { get; set; }

    /// <summary>sphere.ini AllowLightOverride - whether a pinned sector light counts.</summary>
    public Func<bool>? AllowLightOverride { get; set; }

    // --- Global script variables (VAR/VAR0 system) ---
    private readonly Dictionary<string, string> _globalVars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _globalLists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Global OBJ reference UID for script cross-references.</summary>
    public Serial ObjReference { get; set; } = Serial.Invalid;

    // --- Delta-based update: dirty object tracking ---
    private readonly ConcurrentDictionary<uint, ObjBase> _dirtyObjects = new();

    // Active sector tracking — shared by single-thread and multicore tick paths.
    // _activeSectors is the authoritative set for the current tick; NpcAI uses it
    // for O(1) "is this NPC near a player?" checks instead of iterating all players.
    private readonly List<Sector> _tickSectors = new(256);
    private readonly HashSet<Sector> _activeSectors = new(256);
    private readonly HashSet<Sector> _prevActiveSectors = new(256);
    private readonly List<Sector> _newlyActiveSectors = new(64);
    // Sectors flagged SECF_NoSleep — always kept in the tick set regardless of
    // player proximity (Source-X CSector::_CanSleep NoSleep veto).
    private readonly HashSet<Sector> _alwaysAwakeSectors = [];

    /// <summary>World clock: in-game minutes since epoch. 1 real second = configurable game minutes.</summary>
    private long _worldClock;
    private long _lastClockUpdate;

    /// <summary>Interval (ms) between maintenance ticks for sleeping sectors.
    /// Keeps item timers (decay, spawn, TIMER) alive in empty areas.</summary>
    private const long SleepingMaintenanceIntervalMs = 180_000; // 3 minutes
    private long _lastMaintenanceTick;
    // Sleeping-sector maintenance is spread across ticks instead of running the whole
    // world in one tick every interval (which produced a periodic latency spike). A
    // sweep is "armed" when the interval elapses, then drained a bounded number of
    // sectors per tick via a resume cursor until every grid cell has been visited once.
    private bool _maintenanceSweepActive;
    private int[]? _maintenanceMapKeys;
    private int _maintenanceMapIdx;
    private int _maintenanceCursorX;
    private int _maintenanceCursorY;
    // Per-tick budgets: at most this many expensive OnMaintenanceTick calls, and at most
    // this many (cheap) grid cells examined, so a single tick's cost stays bounded no
    // matter how the item-bearing sleeping sectors are distributed. Instance-scoped so
    // tests can shrink them without leaking across the suite.
    // Field-tuned down from 256/4096: the first slice of a 3-minute sweep still
    // landed a 219ms world_tick on a small box (each maintenance call walks the
    // sector's item list). 64 calls bounds the slice to tens of ms and the
    // sweep just takes a few more ticks to cover the grid.
    internal int MaintenanceCallsPerTick { get; set; } = 64;
    internal int MaintenanceExaminePerTick { get; set; } = 2048;
    // Diagnostics/test observability for the current sweep.
    internal bool MaintenanceSweepActive => _maintenanceSweepActive;
    internal int MaintenanceCallsThisSweep { get; private set; }

    /// <summary>Maps to map definitions: mapId → (width, height) in tiles.</summary>
    private readonly Dictionary<int, (int Width, int Height)> _mapDefs = [];

    /// <summary>Optional map data access for terrain queries.</summary>
    public MapData.MapDataManager? MapData { get; set; }

    private Movement.WalkCheck? _standing;

    /// <summary>Shared character surface resolver (the WalkCheck geometry
    /// authority). EVERY path that seats a character — login, mount,
    /// dismount, teleport-derive, GM bypass steps, NPC step Z — resolves
    /// through this instead of MapDataManager.GetEffectiveZ, which sees
    /// neither dynamic items, multis, custom houses nor headroom.</summary>
    public Movement.WalkCheck Standing => _standing ??= new Movement.WalkCheck(this);

    /// <summary>Auto-close delay for an opened map-static door, matching the
    /// item-door Use_Door timer (Source-X: an opened door swings shut 20s later).
    /// Map-static doors (doors baked into the .mul statics rather than real Item
    /// objects) are tracked here as an open-overlay set; without this timer they
    /// stayed open forever because they never receive Item.OnTick.</summary>
    public const long StaticDoorAutoCloseMs = 20_000;

    // Value = TickCount64 at which the door auto-closes (0 = no auto-close).
    private readonly Dictionary<(byte Map, short X, short Y, sbyte Z), long> _openMapStaticDoors = [];

    public bool IsMapStaticDoorOpen(byte map, short x, short y, sbyte z) =>
        _openMapStaticDoors.ContainsKey((map, x, y, z));

    public void SetMapStaticDoorOpen(byte map, short x, short y, sbyte z, bool open) =>
        SetMapStaticDoorOpen(map, x, y, z, open,
            open ? Environment.TickCount64 + StaticDoorAutoCloseMs : 0);

    /// <summary>Open/close a map-static door with an explicit auto-close deadline
    /// (<paramref name="expiryTick"/> is a TickCount64 value; 0 keeps it open).</summary>
    public void SetMapStaticDoorOpen(byte map, short x, short y, sbyte z, bool open, long expiryTick)
    {
        var key = (map, x, y, z);
        if (open)
            _openMapStaticDoors[key] = expiryTick;
        else
            _openMapStaticDoors.Remove(key);
    }

    public IReadOnlyCollection<(byte Map, short X, short Y, sbyte Z)> OpenMapStaticDoors =>
        _openMapStaticDoors.Keys;

    /// <summary>Collect the map-static doors whose auto-close deadline has passed,
    /// into <paramref name="buffer"/>. The caller closes them (broadcasts the
    /// shut) and must not mutate the door set concurrently — invoked from the
    /// single-threaded post-tick maintenance pass.</summary>
    public void CollectExpiredStaticDoors(long now,
        List<(byte Map, short X, short Y, sbyte Z)> buffer)
    {
        foreach (var (key, expiry) in _openMapStaticDoors)
            if (expiry != 0 && now >= expiry)
                buffer.Add(key);
    }

    private TerrainEngine? _terrain;
    /// <summary>Lazy terrain helper (LOS, ground height). Uses current MapData.</summary>
    public TerrainEngine Terrain => _terrain ??= new TerrainEngine(MapData)
    {
        DynamicOccluderAt = HasDynamicLosOccluder,
    };

    /// <summary>Does a dynamic (in-world) item or multi/custom-house wall at this
    /// cell occlude a LOS ray at the given height? (Source-X CanSeeLOS_New
    /// LOS_NB_DYNAMIC + LOS_NB_MULTI passes.) Statics from the MUL files are
    /// handled by the terrain engine; this covers items placed at runtime and the
    /// virtual walls of placed houses/ships, which are neither real items nor MUL
    /// statics.</summary>
    private bool HasDynamicLosOccluder(byte mapId, short x, short y, int rayZ)
    {
        if (MapData == null) return false;
        var cell = new Core.Types.Point3D(x, y,
            (sbyte)Math.Clamp(rayZ, sbyte.MinValue, sbyte.MaxValue), mapId);

        // Real placed items sitting on this cell.
        foreach (var item in GetItemsInRange(cell, 0))
        {
            if (item.X != x || item.Y != y || item.MapIndex != mapId)
                continue;
            var data = MapData.GetItemTileData(item.BaseId);
            if (!TerrainEngine.GraphicBlocksLos(item.BaseId, data))
                continue;
            int height = Math.Max(1, Math.Max(data.Height, data.CalcHeight));
            if (rayZ >= item.Z && rayZ <= item.Z + height)
                return true;
        }

        return HasMultiLosOccluder(mapId, x, y, rayZ);
    }

    /// <summary>Virtual multi geometry (placed house/ship components and committed
    /// custom-house design tiles) occluding the ray — mirrors the walk-geometry
    /// scan in WalkCheck. These walls are rendered client-side, not stored as
    /// items/statics, so LOS must synthesize them the same way.</summary>
    private bool HasMultiLosOccluder(byte mapId, short x, short y, int rayZ)
    {
        var pivot = new Core.Types.Point3D(x, y, (sbyte)0, mapId);
        foreach (var multi in GetItemsInRange(pivot, 32))
        {
            if (multi.IsDeleted || multi.IsEquipped || !multi.IsOnGround)
                continue;
            if (multi.ItemType is not (Core.Enums.ItemType.Multi or Core.Enums.ItemType.MultiCustom
                or Core.Enums.ItemType.MultiAddon or Core.Enums.ItemType.Ship))
                continue;

            var def = MapData!.GetMulti(multi.BaseId);
            if (def != null)
            {
                foreach (var comp in def.Components)
                {
                    if (!comp.IsVisible) continue;
                    if (multi.X + comp.XOffset != x || multi.Y + comp.YOffset != y) continue;
                    if (MultiTileBlocksLos(comp.TileId, multi.Z + comp.ZOffset, rayZ))
                        return true;
                }
            }

            if (multi.ItemType == Core.Enums.ItemType.MultiCustom &&
                Movement.WalkCheck.ResolveCustomDesign != null)
            {
                foreach (var tile in Movement.WalkCheck.ResolveCustomDesign(multi))
                {
                    if (!tile.Visible) continue; // materialized fixture — the real item occludes
                    if (multi.X + tile.X != x || multi.Y + tile.Y != y) continue;
                    if (MultiTileBlocksLos(tile.TileId, multi.Z + tile.Z, rayZ))
                        return true;
                }
            }
        }
        return false;
    }

    private bool MultiTileBlocksLos(ushort tileId, int baseZ, int rayZ)
    {
        var data = MapData!.GetItemTileData(tileId);
        if (!TerrainEngine.GraphicBlocksLos(tileId, data))
            return false;
        int height = Math.Max(1, Math.Max(data.Height, data.CalcHeight));
        return rayZ >= baseZ && rayZ <= baseZ + height;
    }

    /// <summary>Max items allowed in a regular container (backpack, chest).
    /// sphere.ini CONTAINERMAXITEMS; Source-X default MAX_ITEMS_CONT = 255.</summary>
    public int MaxContainerItems { get; set; } = 255;
    /// <summary>Max items allowed in a bank box. sphere.ini BANKMAXITEMS;
    /// Source-X default 1000.</summary>
    public int MaxBankItems { get; set; } = 1000;
    /// <summary>Max total weight (stones) allowed in a bank box. sphere.ini
    /// BANKMAXWEIGHT; Source-X default 1000 stones.</summary>
    public int MaxBankWeight { get; set; } = 1000;
    /// <summary>Max total weight (stones) allowed in a regular container. sphere.ini CONTAINERMAXWEIGHT. 0=unlimited.</summary>
    public int MaxContainerWeight { get; set; } = 400;
    /// <summary>AOS tooltip mode. 0=off, 1=revision/request, 2=force full. sphere.ini TOOLTIPMODE.</summary>
    public int ToolTipMode { get; set; } = 1;
    public int ToolTipCache { get; set; } = 30;

    /// <summary>Currently online, in-world players. Used to compute the set of
    /// active sectors each tick so that uninhabited regions don't iterate
    /// millions of NPCs/items needlessly. Source-X equivalent: the
    /// SECTOR_LIST active scan.</summary>
    private readonly HashSet<Objects.Characters.Character> _onlinePlayers = [];

    /// <summary>Called by GameClient.EnterWorld / OnDisconnect.</summary>
    public void AddOnlinePlayer(Objects.Characters.Character ch)
    {
        _onlinePlayers.Add(ch);
        GetSector(ch.Position)?.AddOnlinePlayer(ch);
    }
    public void RemoveOnlinePlayer(Objects.Characters.Character ch)
    {
        _onlinePlayers.Remove(ch);
        GetSector(ch.Position)?.RemoveOnlinePlayer(ch);
    }
    /// <summary>Read-only view of currently online players. Backing set
    /// is modified only on login/logout paths, so enumeration here is
    /// safe from a single tick-thread context.</summary>
    public IReadOnlyCollection<Objects.Characters.Character> OnlinePlayers => _onlinePlayers;

    /// <summary>Line-of-sight check between two map positions. Returns true when
    /// terrain + impassable statics do not occlude the ray from 'from' to 'to'.
    /// Source-X equivalent: CWorldMap::CanSeeLOS.</summary>
    public bool CanSeeLOS(Core.Types.Point3D from, Core.Types.Point3D to)
        => Terrain.CanSeeLOS(from, to);

    /// <summary>Line-of-sight with Source-X LOS flags (e.g. LosFlags.Fishing).</summary>
    public bool CanSeeLOS(Core.Types.Point3D from, Core.Types.Point3D to, Core.Enums.LosFlags flags)
        => Terrain.CanSeeLOS(from, to, flags);

    public long TickCount => _tickCount;
    public int TotalObjects => _objects.Count;
    public int TotalChars => _totalChars;
    public int TotalItems => _totalItems;
    public IReadOnlyList<Region> Regions => _regions;
    public IReadOnlyList<Room> Rooms => _rooms;

    /// <summary>Last created character UID (for SERV.LASTNEWCHAR).</summary>
    public Serial LastNewChar { get; set; } = Serial.Invalid;
    /// <summary>Last created item UID (for SERV.LASTNEWITEM).</summary>
    public Serial LastNewItem { get; set; } = Serial.Invalid;

    /// <summary>Current world hour (0-23).</summary>
    public int WorldHour => (int)((_worldClock / 60) % 24);

    /// <summary>Current world minute (0-59).</summary>
    public int WorldMinute => (int)(_worldClock % 60);
    public long WorldClockMinutes => _worldClock;

    public int LightDay { get; set; } = 0;
    public int LightNight { get; set; } = 25;
    public int DungeonLight { get; set; } = 27;

    /// <summary>Current season. Updated by WeatherEngine.</summary>
    public byte CurrentSeason { get; set; } = 0; // default spring

    /// <summary>The weather in force at a position: type, intensity and temperature.
    /// Wired to the weather engine by the server so the login and resync paths can send
    /// it without reaching for the engine themselves.</summary>
    public Func<Core.Types.Point3D, (byte Type, byte Intensity, byte Temperature)>? ResolveWeather { get; set; }

    /// <summary>The weather to announce at a position, dry when nothing answers.</summary>
    public (byte Type, byte Intensity, byte Temperature) WeatherAt(Core.Types.Point3D pt) =>
        ResolveWeather?.Invoke(pt) ?? ((byte)WeatherType.None, (byte)0, (byte)20);

    /// <summary>Compatibility light at the map origin. Position-aware callers
    /// should use <see cref="GetLightLevel"/>.</summary>
    public byte GlobalLight => GetLightLevel(new Point3D(0, 0, 0, 0));

    public GameWorld(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GameWorld>();
    }

    // --- Dirty object tracking for delta-based view updates ---

    /// <summary>When true, NotifyDirty is a no-op. Used during bulk mutations
    /// (stress-test generation, world load) where the dirty set would otherwise
    /// balloon to millions of entries and stall the fast-path drain. Callers
    /// must ensure a full view resync follows, since intermediate updates are
    /// discarded.</summary>
    public bool SuppressDirtyNotify { get; set; }

    /// <summary>Called by ObjBase on clean→dirty transition. Thread-safe.</summary>
    public void NotifyDirty(ObjBase obj)
    {
        if (SuppressDirtyNotify) return;
        _dirtyObjects.TryAdd(obj.Uid.Value, obj);
    }

    /// <summary>O(1) check — true if any object has pending delta updates.</summary>
    public bool HasDirty => !_dirtyObjects.IsEmpty;

    /// <summary>Consume all dirty objects and clear the set. Call once per tick after mutations.</summary>
    public void ConsumeDirtyObjects()
    {
        DrainDirtyObjectsSnapshot();
    }

    /// <summary>
    /// Snapshot and consume the objects dirty at drain start.
    /// Objects dirtied during the drain remain queued for the next pass.
    /// </summary>
    public List<ObjBase> DrainDirtyObjectsSnapshot()
    {
        var list = new List<ObjBase>(_dirtyObjects.Count);
        uint[] keys = _dirtyObjects.Keys.ToArray();
        foreach (uint key in keys)
        {
            if (!_dirtyObjects.TryRemove(key, out var obj))
                continue;

            obj.ConsumeDirty();
            if (!obj.IsDeleted)
                list.Add(obj);
        }
        return list;
    }

    /// <summary>
    /// Initialize the sector grid for a map.
    /// Must be called for each map before objects can be placed.
    /// </summary>
    public void InitMap(int mapId, int width, int height)
    {
        _mapDefs[mapId] = (width, height);
        int sectorCols = (width + Sector.SectorSize - 1) / Sector.SectorSize;
        int sectorRows = (height + Sector.SectorSize - 1) / Sector.SectorSize;

        var grid = new Sector[sectorCols, sectorRows];
        for (int x = 0; x < sectorCols; x++)
            for (int y = 0; y < sectorRows; y++)
            {
                byte mapIndex = (byte)mapId;
                var sector = new Sector(x, y, mapIndex, sectorCols)
                {
                    GetWorldMinutes = () => _worldClock,
                    GetWorldTime = () => (WorldHour, WorldMinute),
                    GetLightSettings = () => (LightDay, LightNight, DungeonLight),
                    SendLight = (character, level) => OnSectorLight?.Invoke(character, level),
                    OnEnvironmentChanged = (s, character) => OnSectorEnvironment?.Invoke(s, character),
                    AllowLightOverride = () => AllowLightOverride?.Invoke() ?? true,
                    GetAdjacentSector = (sx, sy) => GetSector(mapIndex, sx, sy),
                    OnNoSleepChanged = (s, isNoSleep) =>
                    {
                        if (isNoSleep) _alwaysAwakeSectors.Add(s);
                        else _alwaysAwakeSectors.Remove(s);
                    },
                };
                int px = Math.Min(short.MaxValue, x * Sector.SectorSize);
                int py = Math.Min(short.MaxValue, y * Sector.SectorSize);
                sector.IsDungeon = () => FindRegion(new Point3D((short)px, (short)py, 0, mapIndex))?
                    .IsFlag(RegionFlag.Underground) == true;
                grid[x, y] = sector;
            }

        _sectors[mapId] = grid;
        _logger.LogInformation("Map {Id} initialized: {W}x{H} ({Cols}x{Rows} sectors)",
            mapId, width, height, sectorCols, sectorRows);
    }

    /// <summary>Move the game clock on by however many game minutes have really
    /// passed, keeping the remainder.
    ///
    /// Source-X adds the elapsed interval to the clock and divides it by the game
    /// minute length (CWorldClock::Advance, CWorldGameTime.cpp:18). Bumping the clock
    /// by exactly one and then snapping the mark to "now" threw away every whole
    /// minute past the first AND the leftover milliseconds, so a long save, a
    /// breakpoint or a stalled main loop left the world running behind the wall clock
    /// for good.</summary>
    private void AdvanceWorldClock(long currentTime)
    {
        if (GameMinuteLengthMs <= 0) return;
        long elapsed = currentTime - _lastClockUpdate;
        if (elapsed < GameMinuteLengthMs) return;
        long minutes = elapsed / GameMinuteLengthMs;
        _worldClock += minutes;
        _lastClockUpdate += minutes * GameMinuteLengthMs;
    }

    /// <summary>Every map this world has actually initialized, ascending.</summary>
    public IEnumerable<int> LoadedMaps => _mapDefs.Keys.OrderBy(m => m);

    /// <summary>The declared size of a loaded map.</summary>
    public bool TryGetMapSize(int mapId, out int width, out int height)
    {
        if (_mapDefs.TryGetValue(mapId, out var def))
        {
            (width, height) = def;
            return true;
        }
        width = height = 0;
        return false;
    }

    /// <summary>The sector grid of a loaded map: columns, rows and total.</summary>
    public bool TryGetSectorGrid(int mapId, out int columns, out int rows)
    {
        if (_sectors.TryGetValue(mapId, out var grid))
        {
            columns = grid.GetLength(0);
            rows = grid.GetLength(1);
            return true;
        }
        columns = rows = 0;
        return false;
    }

    /// <summary>A sector by its LINEAR index, the addressing scripts use.
    /// Source-X resolves it against that map's own sector data
    /// (CWorldMap::GetSectorByIndex, CWorldMap.cpp:229) - not against a fixed
    /// column count.</summary>
    public Sector? GetSectorByIndex(int mapId, int index)
    {
        if (index < 0 || !_sectors.TryGetValue(mapId, out var grid)) return null;
        int columns = grid.GetLength(0);
        int rows = grid.GetLength(1);
        if (columns <= 0 || index >= columns * rows) return null;
        return grid[index % columns, index / columns];
    }

    public Sector? GetSector(Point3D pt)
    {
        // C# integer division truncates toward zero, so X in [-63,-1] would
        // land in sector 0 and slip past the bounds guard — an off-map char
        // then crashes the map readers (negative cell index). Reject the
        // negative side explicitly; the upper bound is caught below.
        if (pt.X < 0 || pt.Y < 0) return null;
        int sx = pt.X / Sector.SectorSize;
        int sy = pt.Y / Sector.SectorSize;
        return GetSector(pt.Map, sx, sy);
    }

    public Sector? GetSector(int mapId, int sectorX, int sectorY)
    {
        if (!_sectors.TryGetValue(mapId, out var grid)) return null;
        if (sectorX < 0 || sectorX >= grid.GetLength(0)) return null;
        if (sectorY < 0 || sectorY >= grid.GetLength(1)) return null;
        return grid[sectorX, sectorY];
    }

    /// <summary>Position-aware Source-X sector light.</summary>
    public byte GetLightLevel(Point3D position, bool quickSet = true)
    {
        var sector = GetSector(position);
        if (sector == null)
            return (byte)Math.Clamp(LightNight, 0, 30);
        bool dungeon = FindRegion(position)?.IsFlag(RegionFlag.Underground) == true;
        return sector.GetLightCalc(quickSet, dungeonOverride: dungeon);
    }

    public void LightFlash(Point3D position) => GetSector(position)?.LightFlash();

    /// <summary>Set the game clock, in game minutes. Public because the save loader
    /// restores it (Source-X InitTime from the TIMEHIRES header, CWorld.cpp:1625).</summary>
    public void SetWorldClockMinutes(long minutes) => _worldClock = Math.Max(0, minutes);

    // --- Object creation ---

    public Item CreateItem()
    {
        var uid = _uidTable.AllocateItem();
        var item = new Item();
        item.SetUid(uid);
        item.SetDirtyNotify(NotifyDirty);
        _objects[uid.Value] = item;
        _uuidIndex[item.Uuid] = item;
        _totalItems++;
        LastNewItem = uid;
        ObjectCreated?.Invoke(item);
        return item;
    }

    public Character CreateCharacter()
    {
        var uid = _uidTable.AllocateChar();
        var ch = new Character();
        ch.SetUid(uid);
        ch.SetDirtyNotify(NotifyDirty);
        _objects[uid.Value] = ch;
        _allCharacters.Add(ch);
        _uuidIndex[ch.Uuid] = ch;
        _totalChars++;
        LastNewChar = uid;
        ObjectCreated?.Invoke(ch);
        return ch;
    }

    public void DeleteObject(ObjBase obj)
    {
        // A stale reference may outlive UID recycling. Never let deleting that
        // old instance remove/free the newer object now registered at the same
        // serial.
        if (!_objects.TryGetValue(obj.Uid.Value, out var registered) ||
            !ReferenceEquals(registered, obj))
        {
            if (obj is Item staleItem) staleItem.Delete();
            else if (obj is Character staleChar) staleChar.Delete();
            return;
        }

        ObjectDeleting?.Invoke(obj);
        _dirtyObjects.TryRemove(obj.Uid.Value, out _);
        obj.ConsumeDirty();
        obj.SetDirtyNotify(null);

        if (obj is Item container)
        {
            foreach (var child in container.Contents.ToArray())
                DeleteObject(child);
        }
        else if (obj is Character owner)
        {
            // Character destruction owns the complete equipment tree. Leaving
            // those items registered produces dangling CONT links to a recycled
            // character serial (account deletion, NPC decay, summons, guards).
            var seenEquipment = new HashSet<Item>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < (int)Layer.Qty; i++)
            {
                var equipped = owner.GetEquippedItem((Layer)i);
                if (equipped != null && seenEquipment.Add(equipped))
                    DeleteObject(equipped);
            }
        }

        if (_objects.Remove(obj.Uid.Value))
        {
            if (obj.IsChar) _totalChars--;
            else if (obj.IsItem) _totalItems--;
        }
        if (obj is Character removedChar)
            _allCharacters.Remove(removedChar);
        else if (obj is Item removedItem)
        {
            _groundItems.Remove(removedItem);
            _multiItems.Remove(removedItem);
        }
        _objectsWithTimerF.Remove(obj);
        // Only drop the UUID index entry if it actually points at THIS object.
        // A duplicate-UUID load sets obj.Uuid to a value another live object owns
        // (the reindex was rejected), and an unconditional Remove would evict that
        // other object's entry — mirrors the serial-registration guard above.
        if (_uuidIndex.TryGetValue(obj.Uuid, out var uuidOwner) && ReferenceEquals(uuidOwner, obj))
            _uuidIndex.Remove(obj.Uuid);
        _uidTable.Free(obj.Uid);

        if (obj is Item delItem && delItem.ContainedIn.IsValid)
        {
            ContainerIndexRemove(delItem.ContainedIn.Value, delItem);
            if (_objects.TryGetValue(delItem.ContainedIn.Value, out var parentObj))
            {
                if (parentObj is Item parentItem)
                    parentItem.RemoveItem(delItem);
                // Equipped items live in the wearer's layer array, not in a
                // container's contents — clear the slot so the layer does not
                // retain a dead reference to the deleted item. The cached
                // Backpack field needs the same cleanup or it keeps handing
                // back the deleted pack.
                else if (parentObj is Character parentChar && delItem.IsEquipped)
                {
                    parentChar.Unequip(delItem.EquipLayer);
                    parentChar.ClearBackpackReference(delItem);
                }
            }
        }

        var sector = GetSector(obj.Position);
        if (sector != null)
        {
            if (obj is Character ch) sector.RemoveCharacter(ch);
            else if (obj is Item item) sector.RemoveItem(item);
        }

        if (obj is Item deletedItem) deletedItem.Delete();
        else if (obj is Character deletedChar) deletedChar.Delete();
    }

    /// <summary>Remove an item from the world and mark it deleted.</summary>
    public void RemoveItem(Item item)
    {
        DeleteObject(item);
        item.Delete();
    }

    /// <summary>
    /// Re-register an object under a new serial (for save/load when serial is read from file).
    /// Removes the old dictionary entry and adds the new one.
    /// </summary>
    public bool TryReRegisterObject(ObjBase obj, Serial oldUid, Serial newUid, out ObjBase? existing)
    {
        existing = null;
        if (_objects.TryGetValue(newUid.Value, out var found) && !ReferenceEquals(found, obj))
        {
            existing = found;
            return false;
        }

        _objects.Remove(oldUid.Value);
        _uidTable.ReRegister(oldUid, newUid, obj);
        obj.SetUid(newUid);
        _objects[newUid.Value] = obj;
        return true;
    }

    public void ReRegisterObject(ObjBase obj, Serial oldUid, Serial newUid)
    {
        if (!TryReRegisterObject(obj, oldUid, newUid, out var existing))
            throw new InvalidOperationException(
                $"Duplicate object serial 0x{newUid.Value:X8}: existing={existing!.GetType().Name} new={obj.GetType().Name}");
    }

    public ObjBase? FindObject(Serial uid) =>
        _objects.GetValueOrDefault(uid.Value);

    public Item? FindItem(Serial uid) =>
        _objects.GetValueOrDefault(uid.Value) as Item;

    public Character? FindChar(Serial uid) =>
        _objects.GetValueOrDefault(uid.Value) as Character;

    public ObjBase? FindByUuid(Guid uuid) =>
        _uuidIndex.GetValueOrDefault(uuid);

    /// <summary>Update the UUID index when an object's UUID changes (e.g. on load).</summary>
    public bool TryReIndexUuid(ObjBase obj, Guid oldUuid, out ObjBase? existing)
    {
        existing = null;
        if (_uuidIndex.TryGetValue(obj.Uuid, out var found) && !ReferenceEquals(found, obj))
        {
            existing = found;
            return false;
        }

        _uuidIndex.Remove(oldUuid);
        _uuidIndex[obj.Uuid] = obj;
        return true;
    }

    public void ReIndexUuid(ObjBase obj, Guid oldUuid)
    {
        if (!TryReIndexUuid(obj, oldUuid, out var existing))
            throw new InvalidOperationException(
                $"Duplicate object UUID {obj.Uuid}: existing=0x{existing!.Uid.Value:X8} new=0x{obj.Uid.Value:X8}");
    }

    // --- Placement ---

    /// <summary>Place an item in the world at a position.</summary>
    /// <summary>Place an item in the world at the given point. Returns false when
    /// the point is out of bounds (no sector) and the item was NOT placed — the
    /// caller must then delete the item rather than leave it orphaned.</summary>
    public bool PlaceItem(Item item, Point3D pos)
    {
        var sector = GetSector(pos);
        if (sector == null)
        {
            // Out-of-bounds placement would strand the item with a bad
            // Position and no sector registration — BroadcastNearby,
            // sector tick and DrainDirtyObjects would all miss it.
            // Refuse and log, matching the MoveCharacter/PlaceCharacter
            // guard.
            _logger.LogWarning(
                "PlaceItem: refusing placement of 0x{Uid:X} at out-of-bounds {X},{Y},{Z} map={Map}",
                item.Uid.Value, pos.X, pos.Y, pos.Z, pos.Map);
            return false;
        }

        // A ground item cannot remain referenced by its previous container or
        // equipment slot. Centralising the detach here protects script, vendor,
        // trade and decay fallbacks that place an item without a packet pickup.
        if (item.ContainedIn.IsValid && _objects.TryGetValue(item.ContainedIn.Value, out var parent))
        {
            if (parent is Item parentItem)
                parentItem.RemoveItem(item);
            else if (parent is Character parentChar && item.IsEquipped &&
                     parentChar.GetEquippedItem(item.EquipLayer) == item)
                parentChar.Unequip(item.EquipLayer);
        }
        item.IsEquipped = false;
        RemoveFromSector(item);
        item.Position = pos;
        item.ContainedIn = Serial.Invalid;
        sector.AddItem(item);
        // Sole sector.AddItem choke point — index every on-ground item so the decay
        // catch-up can sweep this set instead of the full object dictionary.
        _groundItems.Add(item);
        // Multi index upkeep: walk geometry iterates GroundMultis with cheap
        // bounds checks instead of a per-step 32-tile spatial scan (the cost
        // that dominated the live apply phase). The ItemType getter resolves
        // def-typed multis from legacy saves here too.
        if (Item.IsMultiItemType(item.ItemType) && !_multiItems.Contains(item))
            _multiItems.Add(item);
        return true;
    }

    private readonly List<Item> _multiItems = [];

    /// <summary>Ground multi structures (houses, ships, addons, custom
    /// foundations) — maintained on place/remove/retype so hot paths can
    /// scan this short list instead of running spatial range queries.</summary>
    public IReadOnlyList<Item> GroundMultis => _multiItems;

    /// <summary>Called by the Item.ItemType setter when a ground item's
    /// multi classification flips after placement.</summary>
    public void NotifyGroundItemMultiChanged(Item item, bool isMulti)
    {
        if (isMulti)
        {
            if (!_multiItems.Contains(item))
                _multiItems.Add(item);
        }
        else
        {
            _multiItems.Remove(item);
        }
    }

    /// <summary>Source-X CCharBase::Region_Notify hook. Fired for every
    /// character move (walk, .go teleport, recall/gate, NPC drift) so a
    /// single point delivers MSG_REGION_ENTER / guard / PvP banners
    /// regardless of the call path. Args: (character, oldRegion, newRegion).</summary>
    public Action<Character, Regions.Region?, Regions.Region?>? OnRegionChanged { get; set; }

    /// <summary>
    /// Source-X parity: fired after an NPC has been placed into the world
    /// by an automated spawner (CItemSpawn / IT_SPAWN_CHAR). The handler
    /// is expected to fire @Create, finalise the NPC brain (Animal
    /// fallback for None) and run @NPCRestock + @CreateLoot, mirroring
    /// the manual ".add" path in <c>GameClient.CreateNpcFromDef</c>.
    /// Routing this through a hook keeps the trigger-dispatch dependency
    /// out of the spawn component itself.
    /// </summary>
    public Action<Character>? OnNpcSpawned { get; set; }

    /// <summary>Move a character to a new position. When <paramref name="fireRegionEvents"/>
    /// is false the region-change detection is skipped — used when a movable multi
    /// (ship) carries a deck character: the ship's region moves WITH the character,
    /// so they never logically cross a region boundary and must not fire @Enter/@Exit
    /// or flip CURRENT_REGION as the hull sails.</summary>
    public bool MoveCharacter(Character ch, Point3D newPos, bool fireRegionEvents = true)
    {
        var newSector = GetSector(newPos);
        if (newSector == null) return false;
        var oldPos = ch.Position;
        var oldSector = GetSector(oldPos);
        if (oldSector != newSector)
        {
            ch.Position = newPos;
            newSector.AddCharacter(ch);
            if (ch.IsPlayer)
            {
                if (ch.IsOnline || ch.IsClientLingering)
                    newSector.AddOnlinePlayer(ch);
            }
            oldSector?.RemoveCharacter(ch);
            if (ch.IsPlayer)
                oldSector?.RemoveOnlinePlayer(ch);
        }
        else
        {
            ch.Position = newPos;
        }
        if (!oldPos.Equals(newPos))
            ch.LastMoveTick = Environment.TickCount64;

        CharacterMoved?.Invoke(ch, oldPos);

        if (ch.IsPlayer && fireRegionEvents)
        {
            var oldRegion = FindRegion(oldPos);
            var newRegion = FindRegion(newPos);
            if (oldRegion != newRegion)
            {
                ch.SetTag("CURRENT_REGION", newRegion?.Name ?? "");
                ch.SetTag("CURRENT_REGION_UID", newRegion?.Uid.ToString() ?? "");
                OnRegionChanged?.Invoke(ch, oldRegion, newRegion);
            }
        }
        return true;
    }

    /// <summary>Place a character in the world.</summary>
    /// <summary>Place a character at the given point. Returns false when the point
    /// is out of bounds (no sector) and the character was NOT placed — a spawner
    /// must then delete the would-be NPC rather than leave it orphaned.</summary>
    public bool PlaceCharacter(Character ch, Point3D pos)
    {
        var sector = GetSector(pos);
        if (sector == null)
        {
            // Same protection as MoveCharacter — never leave a character
            // without a sector, because BroadcastNearby would then miss
            // it entirely.
            _logger.LogWarning(
                "PlaceCharacter: refusing placement of 0x{Uid:X} at out-of-bounds {X},{Y},{Z} map={Map}",
                ch.Uid.Value, pos.X, pos.Y, pos.Z, pos.Map);
            return false;
        }
        ch.Position = pos;
        sector.AddCharacter(ch);
        if (ch.IsPlayer && (ch.IsOnline || ch.IsClientLingering))
            sector.AddOnlinePlayer(ch);
        else if (!ch.IsPlayer && _onlinePlayers.Count > 0)
            CharacterPlaced?.Invoke(ch);

        RemoveFromOtherSectors(ch, sector);
        return true;
    }

    /// <summary>
    /// Remove an object from its sector without deleting it from the world.
    /// Used by mount system to hide NPC while mounted.
    /// </summary>
    public void HideFromSector(ObjBase obj) => RemoveFromSector(obj);

    private void RemoveFromSector(ObjBase obj)
    {
        var sector = GetSector(obj.Position);
        if (sector == null) return;
        if (obj is Character ch)
        {
            sector.RemoveCharacter(ch);
            if (ch.IsPlayer)
                sector.RemoveOnlinePlayer(ch);
        }
        else if (obj is Item item) sector.RemoveItem(item);
    }

    private void RemoveFromOtherSectors(Character ch, Sectors.Sector keepSector)
    {
        foreach (var (_, grid) in _sectors)
        {
            int cols = grid.GetLength(0);
            int rows = grid.GetLength(1);
            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    var sector = grid[x, y];
                    if (sector == keepSector)
                        continue;
                    sector.RemoveCharacter(ch);
                    if (ch.IsPlayer)
                        sector.RemoveOnlinePlayer(ch);
                }
            }
        }
    }

    // --- Regions ---

    public void AddRegion(Region region)
    {
        _regions.Add(region);
        _regionCache.Clear();
    }

    /// <summary>Remove a dynamically-created region (e.g. a collapsed house's
    /// footprint region) by its uid. Static AREADEF regions are never removed.</summary>
    public bool RemoveRegion(uint uid)
    {
        int removed = _regions.RemoveAll(r => r.Uid == uid);
        if (removed > 0)
            _regionCache.Clear();
        return removed > 0;
    }

    public void InvalidateRegionCache() => _regionCache.Clear();

    /// <summary>Source-X region nesting: a region that opted into inheritance (an
    /// InheritParent* flag) pulls its containing parent's flags / tags / events.
    /// The "parent" is the smallest region with a larger total area that contains
    /// this region's representative point. Run once after all AREADEFs load.</summary>
    public void ApplyRegionInheritance()
    {
        const RegionFlag inheritMask = RegionFlag.InheritParentFlags
            | RegionFlag.InheritParentTags | RegionFlag.InheritParentEvents;
        foreach (var region in _regions)
        {
            if ((region.Flags & inheritMask) == 0) continue;
            var pt = region.RepresentativePoint;
            if (pt == null) continue;

            Region? parent = null;
            long parentArea = long.MaxValue;
            foreach (var other in _regions)
            {
                if (other == region || other.MapIndex != region.MapIndex) continue;
                if (other.TotalArea <= region.TotalArea) continue;
                if (!other.Contains(pt.Value)) continue;
                if (other.TotalArea < parentArea) { parentArea = other.TotalArea; parent = other; }
            }
            if (parent != null)
                region.InheritFromParent(parent);
        }
        _regionCache.Clear();
    }

    public Region? FindRegion(Point3D pt)
    {
        // 8x8 tile grid cache key — same cell ⇒ almost always same region.
        long cacheKey = ((long)pt.Map << 40) | ((long)(pt.X >> 3) << 20) | (long)(pt.Y >> 3);

        if (_regionCache.TryGetValue(cacheKey, out var cached) && cached.Contains(pt))
            return cached;

        var result = FindRegionUncached(pt);
        // Only cache hits. Region edges are not 8-aligned, so a single 8x8 cell
        // can straddle a region boundary. Caching a null would poison the whole
        // cell: a later query for a tile in the SAME cell that DOES fall inside a
        // region would wrongly read the cached null. Non-null entries are
        // self-validating via Contains() above — an edge tile that misses the
        // cached region simply re-resolves rather than returning a stale hit.
        if (result != null)
            _regionCache[cacheKey] = result;
        return result;
    }

    private Region? FindRegionUncached(Point3D pt)
    {
        // Source-X CSector::GetRegion parity: when AREADEFs overlap (a small
        // landmark inside a wider continent definition, e.g. "Britain Castle"
        // inside "Britain"), the most-specific = smallest-total-area region
        // wins.
        Region? best = null;
        long bestArea = long.MaxValue;
        foreach (var region in _regions)
        {
            if (!region.Contains(pt)) continue;
            long area = region.TotalArea;
            if (area <= 0) continue;
            if (area < bestArea)
            {
                bestArea = area;
                best = region;
            }
        }
        return best;
    }

    /// <summary>
    /// Diagnostic helper: returns every region whose rect set contains the
    /// given point, ordered by total rect area ascending. Used by the region
    /// lookup tracer to expose AREADEF overlaps.
    /// </summary>
    public IEnumerable<(Region Region, long Area)> FindAllRegions(Point3D pt)
    {
        var list = new List<(Region Region, long Area)>();
        foreach (var region in _regions)
        {
            if (!region.Contains(pt)) continue;
            list.Add((region, region.TotalArea));
        }
        list.Sort((a, b) => a.Area.CompareTo(b.Area));
        return list;
    }

    /// <summary>The smallest region with a strictly larger area than <paramref name="self"/>
    /// that contains <paramref name="pt"/> — i.e. the background region a movable multi
    /// (ship) nests inside. Recomputed as the multi sails so its inherited flags stay
    /// current. Excludes <paramref name="self"/> by reference so the multi's own region
    /// (which would otherwise win FindRegion by smallest area) is skipped.</summary>
    public Region? FindParentRegion(Region self, Point3D pt)
    {
        Region? best = null;
        long bestArea = long.MaxValue;
        long selfArea = self.TotalArea;
        foreach (var region in _regions)
        {
            if (ReferenceEquals(region, self)) continue;
            if (region.MapIndex != pt.Map) continue;
            if (!region.Contains(pt)) continue;
            long area = region.TotalArea;
            if (area <= selfArea) continue;
            if (area < bestArea) { bestArea = area; best = region; }
        }
        return best;
    }

    public Region? FindRegionByUid(uint uid)
    {
        foreach (var region in _regions)
        {
            if (region.Uid == uid)
                return region;
        }
        return null;
    }

    /// <summary>Find a region by its NAME or DEFNAME (case-insensitive), mirroring
    /// Source-X CServerConfig::GetRegion which matches GetNameStr()/GetResourceName().
    /// Used to resolve data-driven jail cells ("jail" / "jailN").</summary>
    public Region? FindRegionByName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        foreach (var region in _regions)
        {
            if (string.Equals(region.DefName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(region.Name, name, StringComparison.OrdinalIgnoreCase))
                return region;
        }
        return null;
    }

    // Legacy Britain jail coords, used only when no jail AREADEF is loaded so
    // shards without a jail region keep working (matches the historical hardcode).
    private static readonly Point3D LegacyJailPoint = new(1476, 1604, 20, 0);

    /// <summary>Resolve a jail cell anchor point — Source-X GetRegionPoint("jail" /
    /// "jailN"): look up the AREADEF region named "jail" (cell 0) or "jail{cell}"
    /// and return its anchor point. Falls back to the legacy hardcoded coords when
    /// no such region is defined.</summary>
    public Point3D GetJailPoint(int cell)
    {
        var region = cell > 0
            ? (FindRegionByName($"jail{cell}") ?? FindRegionByName("jail"))
            : FindRegionByName("jail");
        return region?.RepresentativePoint ?? LegacyJailPoint;
    }

    // --- Rooms ---

    public void AddRoom(Room room) => _rooms.Add(room);

    public Room? FindRoom(Point3D pt)
    {
        foreach (var room in _rooms)
        {
            if (room.Contains(pt))
                return room;
        }
        return null;
    }

    public Room? FindRoomByUid(uint uid)
    {
        foreach (var room in _rooms)
        {
            if (room.Uid == uid)
                return room;
        }
        return null;
    }

    // --- Queries ---

    /// <summary>
    /// Get all objects within range. Iterates neighboring sectors.
    /// </summary>
    public IEnumerable<ObjBase> GetObjectsInRange(Point3D center, int range = 18)
    {
        var (minSx, maxSx, minSy, maxSy) = GetSectorRange(center, range);

        for (int sx = minSx; sx <= maxSx; sx++)
        {
            for (int sy = minSy; sy <= maxSy; sy++)
            {
                var sector = GetSector(center.Map, sx, sy);
                if (sector == null) continue;
                foreach (var obj in sector.GetObjectsInRange(center, range))
                    yield return obj;
            }
        }
    }

    /// <summary>Any sector overlapping the range window holds a listening ground
    /// item (comm crystal / multi)? Source-X CSector::HasListenItems — the speech
    /// path uses this to skip the per-utterance item scan entirely when nothing
    /// nearby can hear (the common case).</summary>
    public bool HasListenItemsInRange(Point3D center, int range)
    {
        var (minSx, maxSx, minSy, maxSy) = GetSectorRange(center, range);
        for (int sx = minSx; sx <= maxSx; sx++)
        for (int sy = minSy; sy <= maxSy; sy++)
        {
            var sector = GetSector(center.Map, sx, sy);
            if (sector != null && sector.HasListenItems)
                return true;
        }
        return false;
    }

    /// <summary>Rebalance the owning sector's listen-item count after a script
    /// retyped a ground item (Item.ItemType setter hook).</summary>
    public void NotifyGroundItemTypeChanged(Item item,
        Core.Enums.ItemType oldType, Core.Enums.ItemType newType)
        => GetSector(item.Position)?.OnItemTypeChanged(item, oldType, newType);

    public void VisitInRange(Point3D center, int range, Action<Character>? visitChar, Action<Item>? visitItem)
    {
        var (minSx, maxSx, minSy, maxSy) = GetSectorRange(center, range);
        for (int sx = minSx; sx <= maxSx; sx++)
        {
            for (int sy = minSy; sy <= maxSy; sy++)
            {
                var sector = GetSector(center.Map, sx, sy);
                if (sector == null) continue;

                var chars = sector.Characters;
                for (int i = chars.Count - 1; i >= 0; i--)
                {
                    var ch = chars[i];
                    if (!ch.IsDeleted && center.GetDistanceTo(ch.Position) <= range)
                        visitChar?.Invoke(ch);
                }

                var items = sector.Items;
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    var item = items[i];
                    if (!item.IsDeleted && item.IsOnGround && center.GetDistanceTo(item.Position) <= range)
                        visitItem?.Invoke(item);
                }
            }
        }
    }

    private static (int MinSx, int MaxSx, int MinSy, int MaxSy) GetSectorRange(Point3D center, int range)
    {
        int minSx = (center.X - range) / Sector.SectorSize;
        int maxSx = (center.X + range) / Sector.SectorSize;
        int minSy = (center.Y - range) / Sector.SectorSize;
        int maxSy = (center.Y + range) / Sector.SectorSize;
        return (minSx, maxSx, minSy, maxSy);
    }

    /// <summary>Every character standing inside a region, NPCs included. Walks only
    /// the sectors the region's footprint touches, the way Source-X reaches the
    /// characters of the sectors a change applies to.</summary>
    public IEnumerable<Character> CharactersInRegion(Regions.Region? region)
    {
        if (region == null) yield break;
        if (!_sectors.TryGetValue(region.MapIndex, out var grid)) yield break;

        if (region.Rects.Count == 0) yield break;
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
        foreach (var r in region.Rects)
        {
            x1 = Math.Min(x1, r.X1); y1 = Math.Min(y1, r.Y1);
            x2 = Math.Max(x2, r.X2); y2 = Math.Max(y2, r.Y2);
        }
        int sx1 = Math.Max(0, x1 / Sector.SectorSize);
        int sy1 = Math.Max(0, y1 / Sector.SectorSize);
        int sx2 = Math.Min(grid.GetLength(0) - 1, Math.Max(0, x2) / Sector.SectorSize);
        int sy2 = Math.Min(grid.GetLength(1) - 1, Math.Max(0, y2) / Sector.SectorSize);

        for (int sx = sx1; sx <= sx2; sx++)
            for (int sy = sy1; sy <= sy2; sy++)
                foreach (var ch in grid[sx, sy].Characters.ToList())
                    if (!ch.IsDeleted && FindRegion(ch.Position) == region)
                        yield return ch;
    }

    public IEnumerable<Character> GetCharsInRange(Point3D center, int range = 18)
    {
        foreach (var obj in GetObjectsInRange(center, range))
        {
            if (obj is Character ch) yield return ch;
        }
    }

    public IEnumerable<Item> GetItemsInRange(Point3D center, int range = 18)
    {
        foreach (var obj in GetObjectsInRange(center, range))
        {
            if (obj is Item item) yield return item;
        }
    }

    /// <summary>Allocation-free single-tile walkability blocker check for the
    /// pathfinder. Returns true if a living, visible, non-self character or a
    /// static-blocking / forbidden-field item occupies <paramref name="pos"/>.
    /// Mirrors the char/item half of <c>Pathfinder.IsWalkable</c> exactly, but
    /// iterates the owning sector's lists directly instead of going through the
    /// yield-based range queries — A* calls this for every explored neighbour,
    /// and those iterator chains were by far the dominant pathfinding
    /// allocation under load. Reads sector lists by index, matching the
    /// parallel-compute-phase safety contract of <c>Sector.GetObjectsInRange</c>.</summary>
    public bool IsPathTileBlockedByObject(Point3D pos, CanFlags canFlags, Character? self,
        bool ignoreChars = false)
    {
        var (minSx, maxSx, minSy, maxSy) = GetSectorRange(pos, 0);
        for (int sx = minSx; sx <= maxSx; sx++)
        for (int sy = minSy; sy <= maxSy; sy++)
        {
            var sector = GetSector(pos.Map, sx, sy);
            if (sector == null) continue;

            if (!ignoreChars)
            {
                var chars = sector.Characters;
                for (int i = chars.Count - 1; i >= 0; i--)
                {
                    if (i >= chars.Count) continue;
                    var ch = chars[i];
                    if (ch.IsDeleted || pos.GetDistanceTo(ch.Position) > 0) continue;
                    if (ch == self || ch.IsDead || ch.IsStatFlag(StatFlag.Invisible) || ch.IsStatFlag(StatFlag.Hidden))
                        continue;
                    // GetDistanceTo is an X/Y distance, so this had found everyone in
                    // the whole vertical column. The search has to agree with the step
                    // it is planning for; see AI.NpcAI.SharesHeightWith.
                    if (!AI.NpcAI.SharesHeightWith(ch, pos.Z)) continue;
                    return true;
                }
            }

            var items = sector.Items;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (i >= items.Count) continue;
                var item = items[i];
                if (item.IsDeleted || !item.IsOnGround || pos.GetDistanceTo(item.Position) > 0) continue;
                if (item.IsStaticBlock && AI.NpcAI.BlocksAtHeight(item, pos.Z)) return true;
                if ((canFlags & CanFlags.C_FireImmune) == 0 && item.TryGetTag("FIELD_DAMAGE", out _))
                    return true;
            }
        }
        return false;
    }

    // --- World Tick ---

    /// <summary>Game-minute length in real milliseconds. Default = 20 real seconds per game minute.</summary>
    public int GameMinuteLengthMs { get; set; } = 20_000;

    /// <summary>The game clock in MILLISECONDS - the unit Source-X keeps it in
    /// (CWorldGameTime.cpp:11) and stamps things like a champion's activation
    /// with.</summary>
    public long GameClockMs => _worldClock * (long)Math.Max(1, GameMinuteLengthMs);

    /// <summary>
    /// Main world tick. Called from the game loop.
    /// Iterates all sectors and ticks their objects.
    /// </summary>
    public void OnTick()
    {
        _tickCount++;
        long currentTime = Environment.TickCount64;

        AdvanceWorldClock(currentTime);

        // Sector sleep: only tick sectors within view-range of an online player.
        // Without this, a 130K-sector map with 1M+ NPCs spends ~150ms per tick
        // iterating sectors no client can see — pings spike to 300-500ms.
        TickActiveSectors(currentTime);

        // Periodically run item timers in sleeping sectors so spawn points,
        // decay and TIMER triggers stay alive even when no player is nearby.
        TickSleepingMaintenance(currentTime);

        TickTimerF(currentTime);
        TickOffGroundTimers(currentTime);
    }

    // Timed items OFF the ground (worn gear, contained items) belong to no
    // sector tick list — sector ticks cover ground items only, so a WORN
    // flash robe's 1 s color TIMER never fired. Registered from SetTimeout,
    // pumped here in the serial phase (@Timer bodies mutate the world).
    private readonly HashSet<Item> _offGroundTimers = [];
    private readonly List<Item> _offGroundTimerBuffer = [];

    internal void TrackOffGroundTimer(Item item) => _offGroundTimers.Add(item);

    private void TickOffGroundTimers(long nowMs)
    {
        if (_offGroundTimers.Count == 0)
            return;

        _offGroundTimerBuffer.Clear();
        foreach (var item in _offGroundTimers)
        {
            // Ground items tick through their sector; deleted or disarmed
            // ones just leave the registry.
            if (item.IsDeleted || item.Timeout <= 0 || item.IsOnGround ||
                nowMs >= item.Timeout)
                _offGroundTimerBuffer.Add(item);
        }

        foreach (var item in _offGroundTimerBuffer)
        {
            _offGroundTimers.Remove(item);
            if (!item.IsDeleted && !item.IsOnGround &&
                item.Timeout > 0 && nowMs >= item.Timeout)
            {
                // A re-armed timer (@Timer body runs TIMER n again)
                // re-registers through SetTimeout.
                item.OnTick();
            }
        }
    }

    /// <summary>Register an object as holding a pending TIMERF entry so
    /// <see cref="TickTimerF"/> can iterate only timer-bearing objects. Called from
    /// <c>ObjBase.AddTimerF</c> via <c>ResolveWorld</c>. Idempotent (backed by a set).</summary>
    internal void TrackTimerFObject(ObjBase obj) => _objectsWithTimerF.Add(obj);

    private void TickTimerF(long nowMs)
    {
        if (TimerFExpired == null || _objectsWithTimerF.Count == 0)
            return;

        // Snapshot the active-set (small — only objects with live TIMERF entries) so
        // the set can be pruned during the pass without mutating-while-iterating.
        _timerFSnapshot.Clear();
        _timerFSnapshot.AddRange(_objectsWithTimerF);

        foreach (var obj in _timerFSnapshot)
        {
            if (obj.IsDeleted || obj.TimerFEntries.Count == 0)
            {
                _objectsWithTimerF.Remove(obj);
                continue;
            }
            foreach (var entry in obj.DequeueDueTimerF(nowMs))
            {
                if (!obj.IsDeleted)
                    TimerFExpired?.Invoke(obj, entry);
            }
            if (obj.IsDeleted || obj.TimerFEntries.Count == 0)
                _objectsWithTimerF.Remove(obj);
        }
    }

    private const int ActiveSectorRadius = 2; // 5x5 window

    /// <summary>Rebuild the active-sector set from online player positions.
    /// Detects sectors that just woke up (weren't active last tick) so the
    /// caller can bulk-wake their NPCs in the timer wheel.</summary>
    public IReadOnlyList<Sector> NewlyActiveSectors => _newlyActiveSectors;

    private void RefreshActiveSectors()
    {
        _prevActiveSectors.Clear();
        foreach (var s in _activeSectors) _prevActiveSectors.Add(s);
        _activeSectors.Clear();
        _tickSectors.Clear();
        _newlyActiveSectors.Clear();

        long nowMs = Environment.TickCount64;
        foreach (var player in _onlinePlayers)
        {
            if (player.IsDeleted || (!player.IsOnline && !player.IsClientLingering)) continue;
            int sx = player.X / Sector.SectorSize;
            int sy = player.Y / Sector.SectorSize;
            // Refresh the last-client timestamp on the player's own sector so the
            // Source-X sleep timeout (CanSleep) is measured from here.
            GetSector(player.MapIndex, sx, sy)?.SetLastClientTime(nowMs);
            for (int dx = -ActiveSectorRadius; dx <= ActiveSectorRadius; dx++)
            {
                for (int dy = -ActiveSectorRadius; dy <= ActiveSectorRadius; dy++)
                {
                    var s = GetSector(player.MapIndex, sx + dx, sy + dy);
                    if (s != null && _activeSectors.Add(s))
                    {
                        _tickSectors.Add(s);
                        if (!_prevActiveSectors.Contains(s))
                            _newlyActiveSectors.Add(s);
                    }
                }
            }
        }

        // SECF_NoSleep sectors always tick even with no player nearby.
        foreach (var s in _alwaysAwakeSectors)
        {
            if (_activeSectors.Add(s))
            {
                _tickSectors.Add(s);
                if (!_prevActiveSectors.Contains(s))
                    _newlyActiveSectors.Add(s);
            }
        }
    }

    /// <summary>Tick the 5x5 sector window centered on every online player.
    /// Each sector ticks at most once per call via a dedup set so overlapping
    /// player windows do not double-tick.</summary>
    private void TickActiveSectors(long currentTime)
    {
        RefreshActiveSectors();

        foreach (var sector in _activeSectors)
            sector.OnTick(currentTime);

        // Notoriety decay — only the online players. NPCs and offline chars
        // get their decay via the NPC tick / login path.
        foreach (var player in _onlinePlayers)
        {
            if (player.IsDeleted) continue;
            player.TickNotorietyDecay(currentTime);
        }

        ExpireClientLingers();
    }

    /// <summary>Run a lightweight item-only tick on every sleeping sector that
    /// contains items. Keeps spawn points, decay timers and TIMER triggers
    /// progressing even when no player is nearby. Called every 3 minutes.</summary>
    /// <summary>Drive sleeping-sector maintenance without the periodic all-at-once spike.
    /// The interval arms a sweep; each call then processes a bounded slice of the sector
    /// grid from a resume cursor until the whole world has been visited once, after which
    /// the sweep idles until the next interval. Cost per invocation is capped by
    /// <see cref="MaintenanceCallsPerTick"/> (expensive maintenance ticks) and
    /// <see cref="MaintenanceExaminePerTick"/> (cheap cell visits).</summary>
    internal void TickSleepingMaintenance(long currentTime)
    {
        // Arm a fresh sweep once the interval elapses and none is mid-flight. The cadence
        // is measured from arm time, so draining across ticks does not shift the schedule.
        if (!_maintenanceSweepActive && currentTime - _lastMaintenanceTick >= SleepingMaintenanceIntervalMs)
        {
            _lastMaintenanceTick = currentTime;
            _maintenanceSweepActive = true;
            _maintenanceMapKeys = new int[_sectors.Count];
            _sectors.Keys.CopyTo(_maintenanceMapKeys, 0);
            _maintenanceMapIdx = 0;
            _maintenanceCursorX = 0;
            _maintenanceCursorY = 0;
            MaintenanceCallsThisSweep = 0;
        }

        if (!_maintenanceSweepActive || _maintenanceMapKeys == null)
            return;

        int calls = 0;
        int examined = 0;
        while (_maintenanceMapIdx < _maintenanceMapKeys.Length &&
               calls < MaintenanceCallsPerTick && examined < MaintenanceExaminePerTick)
        {
            if (!_sectors.TryGetValue(_maintenanceMapKeys[_maintenanceMapIdx], out var grid))
            {
                _maintenanceMapIdx++;
                _maintenanceCursorX = 0;
                _maintenanceCursorY = 0;
                continue;
            }
            int cols = grid.GetLength(0);
            int rows = grid.GetLength(1);
            while (_maintenanceCursorX < cols &&
                   calls < MaintenanceCallsPerTick && examined < MaintenanceExaminePerTick)
            {
                while (_maintenanceCursorY < rows &&
                       calls < MaintenanceCallsPerTick && examined < MaintenanceExaminePerTick)
                {
                    var sector = grid[_maintenanceCursorX, _maintenanceCursorY];
                    _maintenanceCursorY++;
                    examined++;
                    if (sector == null) continue;
                    if (_activeSectors.Contains(sector)) continue;
                    if (sector.ItemCount == 0) continue;
                    sector.OnMaintenanceTick();
                    calls++;
                }
                if (_maintenanceCursorY >= rows)
                {
                    _maintenanceCursorY = 0;
                    _maintenanceCursorX++;
                }
            }
            if (_maintenanceCursorX >= cols)
            {
                _maintenanceCursorX = 0;
                _maintenanceMapIdx++;
            }
        }

        MaintenanceCallsThisSweep += calls;

        if (_maintenanceMapIdx >= _maintenanceMapKeys.Length)
            _maintenanceSweepActive = false; // full pass done — idle until the next interval
    }

    /// <summary>O(1) gate used by NPC AI to skip expensive brain work when
    /// no online player is in view-range. Checks whether the NPC's sector
    /// is in the active set (5x5 window around each player).</summary>
    public bool IsInActiveArea(int mapIdx, int x, int y)
    {
        var s = GetSector(mapIdx, x / Sector.SectorSize, y / Sector.SectorSize);
        return s != null && _activeSectors.Contains(s);
    }

    /// <summary>
    /// Multicore world tick. The sector-update phase runs SEQUENTIALLY (see below);
    /// the parallelism in multicore mode is the NPC read-only pathfinding prestage
    /// (<c>NpcAI.BuildDecision</c>) driven by the caller, not this method.
    /// <para>
    /// <b>Why sequential:</b> <c>Sector.OnTick</c> is not a pure per-sector compute.
    /// Spawn components create/place/delete world objects; poison and standing-field
    /// deaths route into the death engine (corpse creation, equipment moves,
    /// broadcasts); item <c>@Timer</c> and corpse-decay fire script triggers. Those
    /// mutate shared, non-thread-safe state — <c>Sector._characters</c>/<c>_items</c>
    /// lists and the world's <c>Dictionary</c>/<c>HashSet</c> indexes — so running
    /// sectors concurrently raced those structures and could wedge a worker in a
    /// corrupted-bucket probe, hanging the main loop with no watchdog. Serializing
    /// this phase removes the hazard; it also means no script trigger / expression
    /// evaluation runs on a worker thread, so the shared evaluator stays single-use.
    /// </para>
    /// <para>
    /// The workerCount / cancellationToken parameters are retained for the caller's
    /// contract and a future compute/apply decomposition that could re-parallelize a
    /// mutation-free subset; they are not used to parallelize sector ticks today.
    /// </para>
    /// </summary>
    public void OnTickParallel(int workerCount = 0, CancellationToken cancellationToken = default)
    {
        _tickCount++;
        long currentTime = Environment.TickCount64;

        AdvanceWorldClock(currentTime);

        RefreshActiveSectors();
        var sectors = _tickSectors;

        // Sector ticks mutate shared world state (spawns, deaths, @Timer scripts)
        // and are therefore serialized — see the method contract above.
        foreach (var sector in sectors)
            sector.OnTick(currentTime);

        // Notoriety decay for online players — must match single-threaded OnTick behavior.
        // Without this, murder counts (_kills) never decay in multicore mode.
        foreach (var player in _onlinePlayers)
        {
            if (player.IsDeleted) continue;
            player.TickNotorietyDecay(currentTime);
        }

        ExpireClientLingers();

        // Sleeping sector maintenance — item timers, spawn points, decay in sectors
        // with no nearby players. Without this, remote spawns freeze in multicore mode.
        TickSleepingMaintenance(currentTime);

        // Script TIMERF callbacks — must run in sequential phase (callbacks can mutate world).
        TickTimerF(currentTime);
        TickOffGroundTimers(currentTime);
    }

    private void ExpireClientLingers()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var player in _onlinePlayers.ToArray())
        {
            if (player.IsOnline ||
                !player.TryGetTag("CLIENT_LINGER_UNTIL", out string? untilText) ||
                !long.TryParse(untilText, out long until) || now < until)
                continue;

            player.RemoveTag("CLIENT_LINGER_UNTIL");
            RemoveOnlinePlayer(player);
            ClientLingerExpired?.Invoke(player);
        }
    }

    /// <summary>
    /// Deterministic state hash for debug guardrails. Orders by UID to avoid dictionary
    /// enumeration randomness.
    /// </summary>
    public string ComputeStateHash()
    {
        var sb = new StringBuilder(_objects.Count * 24);
        foreach (var obj in _objects.Values.OrderBy(o => o.Uid.Value))
        {
            sb.Append(obj.Uid.Value).Append('|')
              .Append(obj.X).Append(',').Append(obj.Y).Append(',').Append(obj.Z).Append(',').Append(obj.MapIndex).Append('|');
            if (obj is Character ch)
            {
                sb.Append('C').Append('|')
                  .Append(ch.BodyId).Append('|')
                  .Append(ch.Hits).Append('|')
                  .Append(ch.Stam).Append('|')
                  .Append(ch.Mana).Append('|')
                  .Append((int)ch.StatFlags);
            }
            else if (obj is Item item)
            {
                sb.Append('I').Append('|')
                  .Append(item.BaseId).Append('|')
                  .Append(item.Amount).Append('|')
                  .Append((int)item.ItemType);
            }
            sb.Append('\n');
        }

        byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    // --- Container reverse index ---

    /// <summary>Rebuild the whole container reverse index from the authoritative
    /// <see cref="Item.ContainedIn"/> of every live item. The index is normally
    /// maintained incrementally by the ContainedIn setter, but that setter only
    /// fires <see cref="ContainerIndexAdd"/> when <c>Item.ResolveWorld</c> is
    /// already wired. During the initial world load the save is read BEFORE the
    /// engine wiring runs, so every contained item lands in its parent's
    /// <c>Contents</c> (what .edit reads) yet never enters this index (what the
    /// client's open-container 0x3C batch reads) — the container renders empty on
    /// the client while .edit shows the items. A single rebuild after load closes
    /// that gap independently of wiring order.</summary>
    public void RebuildContainerIndex()
    {
        _containerIndex.Clear();
        foreach (var obj in GetAllObjects())
        {
            if (obj is Item item && !item.IsDeleted && item.ContainedIn.IsValid)
                ContainerIndexAdd(item.ContainedIn.Value, item);
        }
    }

    /// <summary>Add an item to the container index.</summary>
    public void ContainerIndexAdd(uint containerUid, Item item)
    {
        if (!_containerIndex.TryGetValue(containerUid, out var list))
        {
            list = [];
            _containerIndex[containerUid] = list;
        }
        list.Add(item);
    }

    /// <summary>Remove an item from the container index.</summary>
    public void ContainerIndexRemove(uint containerUid, Item item)
    {
        if (_containerIndex.TryGetValue(containerUid, out var list))
        {
            list.Remove(item);
            if (list.Count == 0)
                _containerIndex.Remove(containerUid);
        }
    }

    /// <summary>Get all items contained in a given container serial. O(1) lookup via reverse index.</summary>
    public IEnumerable<Item> GetContainerContents(Serial containerUid)
    {
        if (!_containerIndex.TryGetValue(containerUid.Value, out var list))
            yield break;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            if (!item.IsDeleted)
                yield return item;
        }
    }

    /// <summary>
    /// Every item reachable inside <paramref name="containerUid"/>, descending into
    /// sub-containers the way Source-X CContainer::ContentConsume does
    /// (CContainer.cpp:441) - it recurses through anything
    /// <see cref="Item.IsSearchableContainer"/> allows.
    ///
    /// Resource searches that only looked at the top level made a player's own
    /// organisation break the game: reagents tidied into a pouch inside the backpack
    /// read as "you lack the reagents", while gold and crafting materials were
    /// already counted recursively.
    ///
    /// Depth-bounded (16) like the other containment walks, and cycle-safe: a
    /// container that somehow contains itself cannot spin here.
    /// </summary>
    public IEnumerable<Item> GetContainerContentsRecursive(Serial containerUid, int maxDepth = 16)
    {
        var seen = new HashSet<uint>();
        var pending = new Stack<(Serial Uid, int Depth)>();
        pending.Push((containerUid, 0));

        while (pending.Count > 0)
        {
            var (uid, depth) = pending.Pop();
            if (!seen.Add(uid.Value))
                continue;

            foreach (var item in GetContainerContents(uid))
            {
                yield return item;

                // Anything actually holding items is worth descending into; a
                // non-container reports zero. The searchable gate is what keeps the
                // bank, a vendor box, an open trade and locked chests out.
                if (depth + 1 < maxDepth && item.ContentCount > 0 && item.IsSearchableContainer)
                    pending.Push((item.Uid, depth + 1));
            }
        }
    }

    /// <summary>Total item count in a container's whole subtree (nested
    /// containers included). Source-X counts a bank's deep contents against its
    /// cap so a player can't nest bags to bypass the limit. Depth-bounded (16)
    /// to match the containment-chain guard elsewhere.</summary>
    public int GetContainerItemCountDeep(Serial containerUid, int maxDepth = 16)
    {
        if (maxDepth <= 0 || !_containerIndex.TryGetValue(containerUid.Value, out var list))
            return 0;
        int count = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            if (item.IsDeleted) continue;
            count++;
            count += GetContainerItemCountDeep(item.Uid, maxDepth - 1);
        }
        return count;
    }

    /// <summary>Default ground item decay time in ms. Source-X m_iDecay_Item
    /// default is 30 minutes (ini DecayTimer, minutes) — the old hardcoded
    /// 10 minutes rotted dropped items 3x faster than reference. Program wires
    /// this from sphere.ini at startup.</summary>
    public static long DefaultDecayTimeMs { get; set; } = 1_800_000;

    /// <summary>Place an item on the ground with the default decay timer
    /// (pass a positive <paramref name="decayMs"/> to override).</summary>
    public bool PlaceItemWithDecay(Item item, Point3D pos, long decayMs = 0)
    {
        if (item.IsDeleted) return false;
        if (!PlaceItem(item, pos)) return false;
        long safeDelay = Math.Max(0, decayMs > 0 ? decayMs : DefaultDecayTimeMs);
        // Through the shared contract, so an item already running a script timer of its
        // own is left to finish it (SetDecayTime, CItem.cpp:1485). Arming a separate
        // shorter decay here destroyed the item well before the interval its script had
        // chosen while it was still in a bag.
        item.SetDecayTime(safeDelay);
        return true;
    }

    /// <summary>Enumerate all objects in the world (items + characters), including contained items.</summary>
    public IEnumerable<ObjBase> GetAllObjects() => _objects.Values.ToArray();

    /// <summary>Snapshot of every character (players + NPCs). A ~9K-entry copy
    /// instead of the 100K+ full-object array GetAllObjects() allocates —
    /// use this for char-only sweeps (follower counts, roster scans).</summary>
    public Character[] GetAllCharactersSnapshot() => [.. _allCharacters];

    /// <summary>
    /// Source-X CWorld::GarbageCollection → FixWeirdness: sweep every item and
    /// repair or delete the malformed ones so the world file self-heals across
    /// saves — dangling CONT links, equipped-flag mismatches, containment-flag
    /// mismatches, decay-flagged ground items with no timer, graphicless items.
    /// Memory-item / special-layer objects are engine state, not inventory, and
    /// are left alone. Returns (checked, fixed, deleted); reasons go to
    /// <paramref name="log"/> Source-X-style.
    /// </summary>
    public (int Checked, int Fixed, int Deleted) GarbageCollection(Action<string>? log = null)
    {
        int checkedCount = 0, fixedCount = 0, deletedCount = 0;

        foreach (var obj in GetAllObjects())
        {
            if (obj is not Item item || item.IsDeleted)
                continue;
            checkedCount++;

            // Engine-state items (spell/fight memories and other Special-layer
            // equips) are managed by their subsystems.
            if (item.ItemType == SphereNet.Core.Enums.ItemType.EqMemoryObj ||
                (item.IsEquipped && item.EquipLayer == SphereNet.Core.Enums.Layer.Special))
                continue;

            // 0x2103: an item with no graphic can never render or be used.
            if (item.BaseId == 0)
            {
                log?.Invoke($"GC: deleted graphicless item 0x{item.Uid.Value:X} (0x2103)");
                RemoveItem(item);
                deletedCount++;
                continue;
            }

            if (item.ContainedIn.IsValid)
            {
                var parent = FindObject(item.ContainedIn);
                bool parentGone = parent == null ||
                    (parent is Item pi && pi.IsDeleted) ||
                    (parent is Character pc && pc.IsDeleted);
                if (parentGone)
                {
                    // 0x2205: mislinked — the container/wearer no longer exists.
                    // Recover to the ground when the position is placeable,
                    // otherwise the item is unreachable garbage.
                    item.IsEquipped = false;
                    item.ContainedIn = SphereNet.Core.Types.Serial.Invalid;
                    if (GetSector(item.Position) != null)
                    {
                        PlaceItemWithDecay(item, item.Position);
                        log?.Invoke($"GC: recovered mislinked item 0x{item.Uid.Value:X} to the ground (0x2205)");
                        fixedCount++;
                    }
                    else
                    {
                        log?.Invoke($"GC: deleted unreachable mislinked item 0x{item.Uid.Value:X} (0x2205)");
                        RemoveItem(item);
                        deletedCount++;
                    }
                    continue;
                }

                if (item.IsEquipped && parent is Character wearer)
                {
                    // 0x2202: flagged equipped but not actually on the layer —
                    // drop it into the wearer's pack.
                    if (wearer.GetEquippedItem(item.EquipLayer) != item)
                    {
                        item.IsEquipped = false;
                        if (wearer.Backpack != null && wearer.Backpack != item)
                        {
                            bool packed = wearer.Backpack.TryAddItem(item);
                            if (!packed)
                                PlaceItemWithDecay(item, wearer.Position);
                            log?.Invoke($"GC: unequipped phantom-equip item 0x{item.Uid.Value:X} " +
                                (packed ? "into pack" : "at wearer feet") + " (0x2202)");
                            fixedCount++;
                        }
                        else
                        {
                            log?.Invoke($"GC: deleted phantom-equip item 0x{item.Uid.Value:X} (0x2202)");
                            RemoveItem(item);
                            deletedCount++;
                        }
                    }
                }
                else if (!item.IsEquipped && parent is Item container &&
                         !container.Contents.Contains(item))
                {
                    // 0x2106: flagged contained but missing from the container's
                    // content list — relink so it is reachable again.
                    bool relinked = container.TryAddItem(item);
                    if (!relinked)
                        PlaceItemWithDecay(item, container.GetTopLevelPosition());
                    log?.Invoke($"GC: " + (relinked ? "relinked" : "grounded") +
                        $" orphaned contained item 0x{item.Uid.Value:X} (0x2106)");
                    fixedCount++;
                }
            }
            else if (item.IsAttr(SphereNet.Core.Enums.ObjAttributes.Decay) && item.DecayTime == 0)
            {
                // 0x2236: decay-flagged ground item with no timer never rots.
                item.DecayTime = Environment.TickCount64 + DefaultDecayTimeMs;
                log?.Invoke($"GC: armed missing decay timer on 0x{item.Uid.Value:X} (0x2236)");
                fixedCount++;
            }
        }

        return (checkedCount, fixedCount, deletedCount);
    }

    /// <summary>Admin/console RESPAWN: top every char/item spawner in the world up
    /// to its max immediately, independent of sector sleep (Source-X global
    /// RESPAWN). Must run on the main loop. Returns the number of spawners ticked.</summary>
    public int RespawnAllSpawners()
    {
        int count = 0;
        foreach (var obj in GetAllObjects())
        {
            if (obj is not Item item || item.IsDeleted) continue;
            if (item.SpawnChar != null) { item.SpawnChar.RespawnNow(); count++; }
            if (item.SpawnItem != null) { item.SpawnItem.RespawnNow(); count++; }
        }
        return count;
    }

    /// <summary>Hard respawn (console RESPAWN FULL): delete every spawner
    /// child first, then refill from scratch. Clears out children that were
    /// materialized in a broken state by an older build. Also sweeps ORPHANED
    /// spawn children — NPCs still carrying the Spawned flag / SPAWNITEM tag
    /// whose spawner no longer lists them (a broken ADDOBJ link left the old
    /// 1-hp children alive while fresh ones spawned on top).</summary>
    public (int Spawners, int Orphans) ResetAllSpawners()
    {
        int count = 0;
        var legitChildren = new HashSet<uint>();
        foreach (var obj in GetAllObjects())
        {
            if (obj is not Item item || item.IsDeleted) continue;
            if (ResetSpawner(item, legitChildren))
                count++;
        }
        return (count, SweepOrphanedSpawnChildren(legitChildren));
    }

    /// <summary>Kill and refill one spawner's children, accumulating the fresh
    /// child uids into <paramref name="legitChildren"/> for the orphan sweep.
    /// Returns true when the item carried a spawn component. The RESPAWN FULL
    /// console flow drives this incrementally across ticks — resetting 4K+
    /// spawners in one pass froze the main loop for ~47 seconds.</summary>
    public bool ResetSpawner(Item item, HashSet<uint> legitChildren)
    {
        bool touched = false;
        if (item.SpawnChar != null)
        {
            item.SpawnChar.KillAll();
            item.SpawnChar.RespawnNow();
            foreach (var uid in item.SpawnChar.SpawnedUids)
                legitChildren.Add(uid.Value);
            touched = true;
        }
        if (item.SpawnItem != null)
        {
            item.SpawnItem.KillAll();
            item.SpawnItem.RespawnNow();
            touched = true;
        }
        return touched;
    }

    /// <summary>Delete every non-owned NPC that carries the Spawned flag or
    /// SPAWNITEM tag but is NOT in <paramref name="legitChildren"/> — the
    /// broken-ADDOBJ leftovers that duplicated under their spawner.</summary>
    public int SweepOrphanedSpawnChildren(HashSet<uint> legitChildren)
    {
        int orphans = 0;
        foreach (var ch in GetAllCharactersSnapshot())
        {
            if (ch.IsPlayer || ch.IsDeleted) continue;
            if (ch.NpcMaster.IsValid) continue; // tamed/owned — never sweep
            bool marked = ch.IsStatFlag(Core.Enums.StatFlag.Spawned) ||
                          ch.TryGetTag("SPAWNITEM", out _);
            if (!marked || legitChildren.Contains(ch.Uid.Value)) continue;

            DeleteObject(ch);
            ch.Delete();
            orphans++;
        }
        return orphans;
    }

    /// <summary>
    /// Collect ground items whose decay timer has expired into <paramref name="buffer"/>,
    /// up to <paramref name="max"/> entries. Sweeps the <c>_groundItems</c> index (top-level
    /// items only) instead of the full object dictionary, so cost scales with the number of
    /// loose ground items rather than the total object count. Entries that are no longer on
    /// the ground (picked up via an external <c>sector.RemoveItem</c>) or deleted are pruned
    /// lazily here. Serial-phase only: the caller must not add/remove world objects until the
    /// collection pass returns.
    /// </summary>
    public void CollectExpiredGroundItems(long now, int max, List<Item> buffer)
    {
        _groundPrune.Clear();
        foreach (var it in _groundItems)
        {
            if (it.IsDeleted || !it.IsOnGround)
            {
                _groundPrune.Add(it); // no longer a ground item — drop from the index
                continue;
            }
            if (it.DecayTime > 0 && it.DecayTime <= now)
            {
                buffer.Add(it);
                if (buffer.Count >= max)
                    break;
            }
        }
        for (int i = 0; i < _groundPrune.Count; i++)
            _groundItems.Remove(_groundPrune[i]);
    }

    // --- Stats ---

    // ==================== Global Variables (VAR/VAR0) ====================

    /// <summary>Get a global variable value. Returns null if not set.</summary>
    public string? GetGlobalVar(string name) =>
        _globalVars.GetValueOrDefault(name);

    /// <summary>Get a global variable value. Returns "0" if not set (VAR0 behavior).</summary>
    public string GetGlobalVar0(string name) =>
        _globalVars.GetValueOrDefault(name) ?? "0";

    /// <summary>Set a global variable. Empty/null value removes it.</summary>
    public void SetGlobalVar(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            _globalVars.Remove(name);
        else
            _globalVars[name] = value;
    }

    /// <summary>Clear all global variables, optionally filtered by prefix.</summary>
    public int ClearGlobalVars(string? prefix = null)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            int count = _globalVars.Count;
            _globalVars.Clear();
            return count;
        }
        var toRemove = _globalVars.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var k in toRemove)
            _globalVars.Remove(k);
        return toRemove.Count;
    }

    /// <summary>Get all global variable names for listing.</summary>
    public IEnumerable<string> GetGlobalVarNames() => _globalVars.Keys;

    /// <summary>Get all global variables as key-value pairs (for save).</summary>
    public IEnumerable<KeyValuePair<string, string>> GetAllGlobalVars() => _globalVars;

    // ==================== GM Page Queue ====================
    // Player help/GM pages. Source-X keeps these in g_World.m_GMPages and writes
    // them as GMPAGE sections (CGMPage::r_Write, "SAVED in World"), so the world
    // owns the queue and it survives a restart.

    public readonly record struct GmPageRecord(
        string Account, string Reason, string Handler, string Status, long Created);

    private readonly List<GmPageRecord> _gmPages = new();

    public IReadOnlyList<GmPageRecord> GmPages => _gmPages;
    public void AddGmPage(in GmPageRecord page) => _gmPages.Add(page);
    public void RemoveGmPageAt(int index)
    {
        if (index >= 0 && index < _gmPages.Count)
            _gmPages.RemoveAt(index);
    }
    public void ClearGmPages() => _gmPages.Clear();

    // ==================== Global Lists ====================

    public List<string> GetOrCreateList(string name)
    {
        if (!_globalLists.TryGetValue(name, out var list))
        {
            list = [];
            _globalLists[name] = list;
        }
        return list;
    }

    public IEnumerable<KeyValuePair<string, List<string>>> GetAllGlobalLists() => _globalLists;

    /// <summary>Look up an existing global list without creating it.</summary>
    public List<string>? GetList(string name) =>
        _globalLists.TryGetValue(name, out var list) ? list : null;

    /// <summary>Drop a whole global list (Source-X CListDefMap::DeleteKey / LIST.name.clear).</summary>
    public bool RemoveGlobalList(string name) => _globalLists.Remove(name);

    /// <summary>
    /// Global list mutation — ports Source-X CListDefMap::r_LoadVal
    /// (ListDefContMap.cpp:768-963). <paramref name="keyPath"/> is name[.op] or
    /// name[.index[.op]]. Ops: clear / add / set / append / sort; index ops:
    /// remove / insert; bare name replaces the list with a single element
    /// (empty value = delete). Returns true when the list changed.
    /// </summary>
    public bool MutateGlobalList(string keyPath, string value)
    {
        keyPath = keyPath.Trim();
        value = value.Trim();
        if (keyPath.Length == 0) return false;

        string[] parts = keyPath.Split('.');
        string name = parts[0].Trim();
        if (name.Length == 0) return false;

        // LIST.<name> = value → replace list with single element (or delete).
        if (parts.Length == 1)
        {
            if (value.Length == 0)
                return RemoveGlobalList(name);
            var list = GetOrCreateList(name);
            list.Clear();
            list.Add(StripListValue(value));
            return true;
        }

        string sub1 = parts[1].Trim();

        // LIST.<name>.<operation>
        if (!IsListIndex(sub1, out int index))
        {
            if (sub1.StartsWith("clear", StringComparison.OrdinalIgnoreCase))
                return RemoveGlobalList(name);
            if (sub1.StartsWith("add", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length == 0) return false;
                GetOrCreateList(name).Add(StripListValue(value));
                return true;
            }
            if (sub1.StartsWith("set", StringComparison.OrdinalIgnoreCase) ||
                sub1.StartsWith("append", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length == 0) return false;
                if (sub1.StartsWith("set", StringComparison.OrdinalIgnoreCase))
                    RemoveGlobalList(name);
                var list = GetOrCreateList(name);
                foreach (var element in value.Split(','))
                    list.Add(StripListValue(element.Trim()));
                return true;
            }
            if (sub1.StartsWith("sort", StringComparison.OrdinalIgnoreCase))
            {
                var list = GetList(name);
                if (list == null) return false;
                SortGlobalList(list, value);
                return true;
            }
            return false;
        }

        // LIST.<name>.<index>[.<operation>]
        var target = GetList(name);
        if (parts.Length >= 3)
        {
            string op = parts[2].Trim();
            if (op.StartsWith("remove", StringComparison.OrdinalIgnoreCase))
            {
                if (target != null && index >= 0 && index < target.Count)
                {
                    target.RemoveAt(index);
                    return true;
                }
                return false;
            }
            if (op.StartsWith("insert", StringComparison.OrdinalIgnoreCase) && value.Length != 0)
            {
                string v = StripListValue(value);
                target ??= GetOrCreateList(name);
                if (index >= target.Count)
                    target.Add(v);
                else if (index >= 0)
                    target.Insert(index, v);
                else
                    return false;
                return true;
            }
            return false;
        }

        // LIST.<name>.<index> = value → set element at index.
        if (target != null && index >= 0 && index < target.Count && value.Length != 0)
        {
            target[index] = StripListValue(value);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Read a global list expression — ports Source-X CListDefMap::r_WriteVal
    /// (ListDefContMap.cpp:975-1037). <paramref name="sub"/> is the text after
    /// "LIST.": name / name.count / name.&lt;index&gt; / name[.start].findelement value.
    /// Returns null only when the list is missing on an index read.
    /// </summary>
    public string ResolveGlobalListRead(string sub)
    {
        int dot = sub.IndexOf('.');
        string name = (dot >= 0 ? sub[..dot] : sub).Trim();
        string rest = dot >= 0 ? sub[(dot + 1)..] : "";
        var list = GetList(name);
        if (list == null)
            return rest.Equals("COUNT", StringComparison.OrdinalIgnoreCase) ? "0" : "";
        if (rest.Length == 0 || rest.Equals("COUNT", StringComparison.OrdinalIgnoreCase))
            return list.Count.ToString();
        if (int.TryParse(rest, out int index))
            return index >= 0 && index < list.Count ? list[index] : "";

        // findelement [start]: name.findelement v  |  name.<start>.findelement v
        int startIdx = 0;
        string feExpr = rest;
        int secondDot = rest.IndexOf('.');
        if (secondDot >= 0 && int.TryParse(rest[..secondDot], out int si))
        {
            startIdx = Math.Max(0, si);
            feExpr = rest[(secondDot + 1)..];
        }
        if (feExpr.StartsWith("findelem", StringComparison.OrdinalIgnoreCase))
        {
            int sp = feExpr.IndexOf(' ');
            string needle = sp >= 0 ? feExpr[(sp + 1)..].Trim() : "";
            needle = StripListValue(needle);
            for (int i = startIdx; i < list.Count; i++)
                if (list[i].Equals(needle, StringComparison.OrdinalIgnoreCase))
                    return i.ToString();
            return "-1";
        }
        return "";
    }

    /// <summary>Source-X IsStrNumeric-style index token test (decimal or 0x hex).</summary>
    private static bool IsListIndex(string token, out int index)
    {
        index = 0;
        if (token.Length == 0) return false;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(token[2..], System.Globalization.NumberStyles.HexNumber, null, out index);
        return int.TryParse(token, out index);
    }

    /// <summary>Strip one enclosing quote pair, matching Source-X GetArgStr.</summary>
    private static string StripListValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }

    /// <summary>Sort a global list in place (Source-X CListDefCont::Sort). Mode:
    /// asc (default) / i|iasc (case-insensitive asc) / desc / idesc. Numeric-aware
    /// when both operands parse as integers, else lexical.</summary>
    private static void SortGlobalList(List<string> list, string mode)
    {
        mode = mode.Trim();
        bool desc = mode.StartsWith("desc", StringComparison.OrdinalIgnoreCase) ||
                    mode.StartsWith("idesc", StringComparison.OrdinalIgnoreCase);
        bool ci = mode.StartsWith("i", StringComparison.OrdinalIgnoreCase); // i / iasc / idesc
        list.Sort((a, b) =>
        {
            if (long.TryParse(a, out long na) && long.TryParse(b, out long nb))
                return na.CompareTo(nb);
            return string.Compare(a, b, ci
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        });
        if (desc)
            list.Reverse();
    }

    /// <summary>Number of global lists currently defined (Source-X PRINTLISTS header).</summary>
    public int GlobalListCount => _globalLists.Count;

    /// <summary>Clear global script lists (Source-X SERV.CLEARLISTS). With no prefix
    /// every list is dropped; with a prefix only matching list names are removed.
    /// Returns how many lists were cleared. Mirrors <see cref="ClearGlobalVars"/>,
    /// whose list counterpart was missing.</summary>
    public int ClearGlobalLists(string? prefix = null)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            int count = _globalLists.Count;
            _globalLists.Clear();
            return count;
        }
        var toRemove = _globalLists.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var k in toRemove)
            _globalLists.Remove(k);
        return toRemove.Count;
    }

    /// <summary>Every sector of every map with its map id — used by the world
    /// saver to persist per-sector environment overrides (Source-X CSector::r_Write).</summary>
    public IEnumerable<(int MapId, Sector Sector)> EnumerateSectors()
    {
        foreach (var (mapId, grid) in _sectors.OrderBy(kv => kv.Key))
        {
            int cols = grid.GetLength(0);
            int rows = grid.GetLength(1);
            for (int x = 0; x < cols; x++)
                for (int y = 0; y < rows; y++)
                    yield return (mapId, grid[x, y]);
        }
    }

    public (int Chars, int Items, int Sectors) GetStats()
    {
        int chars = 0, items = 0, sectorCount = 0;
        foreach (var (_, grid) in _sectors)
        {
            int cols = grid.GetLength(0);
            int rows = grid.GetLength(1);
            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    var sector = grid[x, y];
                    sectorCount++;
                    chars += sector.CharacterCount;
                    items += sector.ItemCount;
                }
            }
        }
        return (chars, items, sectorCount);
    }

    public IReadOnlyList<MapRuntimeStats> GetMapStats()
    {
        var result = new List<MapRuntimeStats>();
        foreach (var (mapId, grid) in _sectors.OrderBy(kv => kv.Key))
        {
            int chars = 0, items = 0, sectors = 0, activeSectors = 0, onlinePlayers = 0;
            int cols = grid.GetLength(0);
            int rows = grid.GetLength(1);
            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    var sector = grid[x, y];
                    sectors++;
                    chars += sector.CharacterCount;
                    items += sector.ItemCount;
                    if (_activeSectors.Contains(sector))
                        activeSectors++;
                    onlinePlayers += sector.OnlinePlayers.Count;
                }
            }
            result.Add(new MapRuntimeStats(mapId, chars, items, sectors, activeSectors, onlinePlayers));
        }
        return result;
    }
}

public sealed record MapRuntimeStats(
    int MapId,
    int Chars,
    int Items,
    int Sectors,
    int ActiveSectors,
    int OnlinePlayers);
