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
/// </summary>
public sealed class GameWorld
{
    private readonly UidTable _uidTable = new();
    private readonly Dictionary<uint, ObjBase> _objects = [];
    private readonly Dictionary<Guid, ObjBase> _uuidIndex = [];
    private readonly Dictionary<uint, List<Item>> _containerIndex = [];
    private readonly Dictionary<int, Sector[,]> _sectors = [];
    private readonly List<Region> _regions = [];
    private readonly List<Room> _rooms = [];
    private readonly ILogger<GameWorld> _logger;

    private long _tickCount;
    private int _totalChars;
    private int _totalItems;
    public event Action<ObjBase>? ObjectCreated;
    public event Action<ObjBase>? ObjectDeleting;

    // --- Global script variables (VAR/VAR0 system) ---
    private readonly Dictionary<string, string> _globalVars = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Global OBJ reference UID for script cross-references.</summary>
    public Serial ObjReference { get; set; } = Serial.Invalid;

    // --- Delta-based update: dirty object tracking ---
    private readonly ConcurrentDictionary<uint, ObjBase> _dirtyObjects = new();

    // Reusable collections for OnTickParallel to avoid per-tick allocation
    private readonly List<Sector> _tickSectors = new(256);
    private readonly HashSet<Sector> _tickSectorsSeen = new(256);

    /// <summary>World clock: in-game minutes since epoch. 1 real second = configurable game minutes.</summary>
    private long _worldClock;
    private long _lastClockUpdate;

    /// <summary>Maps to map definitions: mapId → (width, height) in tiles.</summary>
    private readonly Dictionary<int, (int Width, int Height)> _mapDefs = [];

    /// <summary>Optional map data access for terrain queries.</summary>
    public MapData.MapDataManager? MapData { get; set; }

    private TerrainEngine? _terrain;
    /// <summary>Lazy terrain helper (LOS, ground height). Uses current MapData.</summary>
    public TerrainEngine Terrain => _terrain ??= new TerrainEngine(MapData);

    /// <summary>Max items allowed in a regular container (backpack, chest). sphere.ini CONTAINERMAXITEMS.</summary>
    public int MaxContainerItems { get; set; } = 125;
    /// <summary>Max items allowed in a bank box. sphere.ini BANKMAXITEMS.</summary>
    public int MaxBankItems { get; set; } = 125;
    /// <summary>Max total weight (stones) allowed in a bank box. sphere.ini BANKMAXWEIGHT.</summary>
    public int MaxBankWeight { get; set; } = 1600;
    /// <summary>AOS tooltip mode. 0=off, 1=enabled. sphere.ini TOOLTIPMODE.</summary>
    public int ToolTipMode { get; set; }

    /// <summary>Currently online, in-world players. Used to compute the set of
    /// active sectors each tick so that uninhabited regions don't iterate
    /// millions of NPCs/items needlessly. Source-X equivalent: the
    /// SECTOR_LIST active scan.</summary>
    private readonly HashSet<Objects.Characters.Character> _onlinePlayers = [];

    /// <summary>Called by GameClient.EnterWorld / OnDisconnect.</summary>
    public void AddOnlinePlayer(Objects.Characters.Character ch) => _onlinePlayers.Add(ch);
    public void RemoveOnlinePlayer(Objects.Characters.Character ch) => _onlinePlayers.Remove(ch);
    /// <summary>Read-only view of currently online players. Backing set
    /// is modified only on login/logout paths, so enumeration here is
    /// safe from a single tick-thread context.</summary>
    public IReadOnlyCollection<Objects.Characters.Character> OnlinePlayers => _onlinePlayers;

    /// <summary>Line-of-sight check between two map positions. Returns true when
    /// terrain + impassable statics do not occlude the ray from 'from' to 'to'.
    /// Source-X equivalent: CWorldMap::CanSeeLOS.</summary>
    public bool CanSeeLOS(Core.Types.Point3D from, Core.Types.Point3D to)
        => Terrain.CanSeeLOS(from, to);

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

    /// <summary>Current season. Updated by WeatherEngine.</summary>
    public byte CurrentSeason { get; set; } = 1; // default spring

    /// <summary>Global light level based on time of day (0=bright, 30=dark).</summary>
    public byte GlobalLight
    {
        get
        {
            int hour = WorldHour;
            if (hour >= 6 && hour < 18) return 0;             // daytime
            if (hour >= 18 && hour < 20) return (byte)((hour - 18) * 10); // dusk
            if (hour >= 4 && hour < 6) return (byte)((6 - hour) * 10);    // dawn
            return 25;                                          // night (20-23, 0-3)
        }
    }

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
        foreach (var kvp in _dirtyObjects)
            kvp.Value.ConsumeDirty();
        _dirtyObjects.Clear();
    }

    /// <summary>Snapshot and clear dirty set. Call once per tick after mutations.</summary>
    [Obsolete("Use ConsumeDirtyObjects() to avoid allocation")]
    public List<ObjBase> DrainDirtyObjects()
    {
        var list = new List<ObjBase>(_dirtyObjects.Count);
        foreach (var kvp in _dirtyObjects)
            list.Add(kvp.Value);
        _dirtyObjects.Clear();
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
                grid[x, y] = new Sector(x, y, (byte)mapId);

        _sectors[mapId] = grid;
        _logger.LogInformation("Map {Id} initialized: {W}x{H} ({Cols}x{Rows} sectors)",
            mapId, width, height, sectorCols, sectorRows);
    }

    public Sector? GetSector(Point3D pt)
    {
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
        _uuidIndex[ch.Uuid] = ch;
        _totalChars++;
        LastNewChar = uid;
        ObjectCreated?.Invoke(ch);
        return ch;
    }

    public void DeleteObject(ObjBase obj)
    {
        ObjectDeleting?.Invoke(obj);
        if (_objects.Remove(obj.Uid.Value))
        {
            if (obj.IsChar) _totalChars--;
            else if (obj.IsItem) _totalItems--;
        }
        _uuidIndex.Remove(obj.Uuid);
        _uidTable.Free(obj.Uid);

        if (obj is Item delItem && delItem.ContainedIn.IsValid)
            ContainerIndexRemove(delItem.ContainedIn.Value, delItem);

        var sector = GetSector(obj.Position);
        if (sector != null)
        {
            if (obj is Character ch) sector.RemoveCharacter(ch);
            else if (obj is Item item) sector.RemoveItem(item);
        }
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
    public void ReRegisterObject(ObjBase obj, Serial oldUid, Serial newUid)
    {
        _objects.Remove(oldUid.Value);
        _uidTable.ReRegister(oldUid, newUid, obj);
        obj.SetUid(newUid);
        _objects[newUid.Value] = obj;
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
    public void ReIndexUuid(ObjBase obj, Guid oldUuid)
    {
        _uuidIndex.Remove(oldUuid);
        _uuidIndex[obj.Uuid] = obj;
    }

    // --- Placement ---

    /// <summary>Place an item in the world at a position.</summary>
    public void PlaceItem(Item item, Point3D pos)
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
            return;
        }
        RemoveFromSector(item);
        item.Position = pos;
        item.ContainedIn = Serial.Invalid;
        sector.AddItem(item);
    }

    /// <summary>Source-X CCharBase::Region_Notify hook. Fired for every
    /// character move (walk, .go teleport, recall/gate, NPC drift) so a
    /// single point delivers MSG_REGION_ENTER / guard / PvP banners
    /// regardless of the call path. Args: (character, oldRegion, newRegion).</summary>
    public Action<Character, Regions.Region?, Regions.Region?>? OnRegionChanged { get; set; }

    /// <summary>Move a character to a new position.</summary>
    public void MoveCharacter(Character ch, Point3D newPos)
    {
        var newSector = GetSector(newPos);
        if (newSector == null)
        {
            // Off-map target (negative coord, past map bounds, unknown
            // map id). Silently dropping the move would strand the
            // character in its old sector with a bad Position — refuse
            // the move and log instead.
            _logger.LogWarning(
                "MoveCharacter: refusing move of 0x{Uid:X} to out-of-bounds {X},{Y},{Z} map={Map} — staying at {OldX},{OldY},{OldZ} map={OldMap}",
                ch.Uid.Value, newPos.X, newPos.Y, newPos.Z, newPos.Map,
                ch.Position.X, ch.Position.Y, ch.Position.Z, ch.Position.Map);
            return;
        }
        var oldRegion = FindRegion(ch.Position);
        var oldSector = GetSector(ch.Position);
        if (oldSector != newSector)
        {
            oldSector?.RemoveCharacter(ch);
            newSector.AddCharacter(ch);
        }
        ch.Position = newPos;
        var newRegion = FindRegion(newPos);
        if (oldRegion != newRegion)
            OnRegionChanged?.Invoke(ch, oldRegion, newRegion);
    }

    /// <summary>Place a character in the world.</summary>
    public void PlaceCharacter(Character ch, Point3D pos)
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
            return;
        }
        RemoveFromSector(ch);
        ch.Position = pos;
        sector.AddCharacter(ch);
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
        if (obj is Character ch) sector.RemoveCharacter(ch);
        else if (obj is Item item) sector.RemoveItem(item);
    }

    // --- Regions ---

    public void AddRegion(Region region) => _regions.Add(region);

    public Region? FindRegion(Point3D pt)
    {
        foreach (var region in _regions)
        {
            if (region.Contains(pt))
                return region;
        }
        return null;
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
        int minSx = (center.X - range) / Sector.SectorSize;
        int maxSx = (center.X + range) / Sector.SectorSize;
        int minSy = (center.Y - range) / Sector.SectorSize;
        int maxSy = (center.Y + range) / Sector.SectorSize;

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

    // --- World Tick ---

    /// <summary>Game-minute length in real milliseconds. Default = 20 real seconds per game minute.</summary>
    public int GameMinuteLengthMs { get; set; } = 20_000;

    /// <summary>
    /// Main world tick. Called from the game loop.
    /// Iterates all sectors and ticks their objects.
    /// </summary>
    public void OnTick()
    {
        _tickCount++;
        long currentTime = Environment.TickCount64;

        // Advance world clock
        if (currentTime - _lastClockUpdate >= GameMinuteLengthMs)
        {
            _worldClock++;
            _lastClockUpdate = currentTime;
        }

        // Sector sleep: only tick sectors within view-range of an online player.
        // Without this, a 130K-sector map with 1M+ NPCs spends ~150ms per tick
        // iterating sectors no client can see — pings spike to 300-500ms.
        TickActiveSectors(currentTime);
    }

    /// <summary>Tick the 5x5 sector window centered on every online player.
    /// 5x5 (vs 3x3) covers the worst case of a client with max view range (24
    /// tiles) standing on a sector boundary — the target tile can legally be
    /// two sectors away. Each sector ticks at most once per call via a dedup
    /// set so overlapping player windows do not double-tick.</summary>
    private readonly HashSet<Sectors.Sector> _activeSectorsScratch = [];
    private const int ActiveSectorRadius = 2; // 5x5 window
    private void TickActiveSectors(long currentTime)
    {
        _activeSectorsScratch.Clear();

        foreach (var player in _onlinePlayers)
        {
            if (player.IsDeleted || !player.IsOnline) continue;
            int sx = player.X / Sectors.Sector.SectorSize;
            int sy = player.Y / Sectors.Sector.SectorSize;
            for (int dx = -ActiveSectorRadius; dx <= ActiveSectorRadius; dx++)
            {
                for (int dy = -ActiveSectorRadius; dy <= ActiveSectorRadius; dy++)
                {
                    var s = GetSector(player.MapIndex, sx + dx, sy + dy);
                    if (s != null) _activeSectorsScratch.Add(s);
                }
            }
        }

        foreach (var sector in _activeSectorsScratch)
            sector.OnTick(currentTime);

        // Notoriety decay — only the online players. NPCs and offline chars
        // get their decay via the NPC tick / login path. A full _objects scan
        // here burned 150-300ms per tick on million-entity worlds even with an
        // early filter: the dictionary enumeration + type dispatch dominated.
        foreach (var player in _onlinePlayers)
        {
            if (player.IsDeleted) continue;
            player.TickNotorietyDecay(currentTime);
        }
    }

    /// <summary>Coarse gate used by NPC AI to skip expensive brain work when
    /// no online player is in view-range. Matches the 5x5 sector window used
    /// by sector sleep, converted to tile distance (128 tiles).</summary>
    public bool IsInActiveArea(int mapIdx, int x, int y)
    {
        const int TileRadius = ActiveSectorRadius * Sectors.Sector.SectorSize;
        foreach (var p in _onlinePlayers)
        {
            if (p.IsDeleted || !p.IsOnline) continue;
            if (p.MapIndex != mapIdx) continue;
            int dx = p.X - x; if (dx < 0) dx = -dx;
            if (dx > TileRadius) continue;
            int dy = p.Y - y; if (dy < 0) dy = -dy;
            if (dy > TileRadius) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Multicore-friendly world tick: sector updates can run in parallel because each
    /// sector owns its own item/character lists. Mutations crossing sectors should stay
    /// in later apply phases.
    /// </summary>
    public void OnTickParallel(int workerCount = 0, CancellationToken cancellationToken = default)
    {
        _tickCount++;
        long currentTime = Environment.TickCount64;

        if (currentTime - _lastClockUpdate >= GameMinuteLengthMs)
        {
            _worldClock++;
            _lastClockUpdate = currentTime;
        }

        // Sector sleep (parallel variant): only include active sectors.
        // 5x5 window — see TickActiveSectors comment for rationale.
        _tickSectors.Clear();
        _tickSectorsSeen.Clear();
        foreach (var player in _onlinePlayers)
        {
            if (player.IsDeleted || !player.IsOnline) continue;
            int sx = player.X / Sector.SectorSize;
            int sy = player.Y / Sector.SectorSize;
            for (int dx = -ActiveSectorRadius; dx <= ActiveSectorRadius; dx++)
            {
                for (int dy = -ActiveSectorRadius; dy <= ActiveSectorRadius; dy++)
                {
                    var s = GetSector(player.MapIndex, sx + dx, sy + dy);
                    if (s != null && _tickSectorsSeen.Add(s)) _tickSectors.Add(s);
                }
            }
        }
        var sectors = _tickSectors;

        // Below ~50 sectors the thread-pool overhead (context switches,
        // work-stealing, lambda capture) exceeds the actual work.
        // Fall back to sequential to avoid 50-60ms spikes on light loads.
        if (sectors.Count < 50)
        {
            foreach (var sector in sectors)
                sector.OnTick(currentTime);
        }
        else
        {
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount > 0 ? workerCount : Environment.ProcessorCount,
                CancellationToken = cancellationToken
            };
            Parallel.ForEach(sectors, po, sector => sector.OnTick(currentTime));
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

    /// <summary>Default ground item decay time in ms (10 minutes).</summary>
    public const long DefaultDecayTimeMs = 600_000;

    /// <summary>Place an item on the ground with the default decay timer.</summary>
    public void PlaceItemWithDecay(Item item, Point3D pos, long decayMs = DefaultDecayTimeMs)
    {
        PlaceItem(item, pos);
        item.DecayTime = Environment.TickCount64 + decayMs;
    }

    /// <summary>Enumerate all objects in the world (items + characters), including contained items.</summary>
    public IEnumerable<ObjBase> GetAllObjects() => _objects.Values;

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
}
