// Steps and routing: MoveToward, A* cache, tile checks, wander, doors.
// Decomposed from the former single-file NpcAI.cs (see NpcAI.cs core).
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Messages;
using SphereNet.Game.World;

namespace SphereNet.Game.AI;

public sealed partial class NpcAI
{
    // Cached paths per NPC UID — avoids recalculating every tick
    private readonly Dictionary<uint, List<Point3D>> _pathCache = [];

    private readonly Dictionary<uint, int> _pathIndex = [];

    private readonly Dictionary<uint, long> _pathTime = [];

    // Destination associated with the cached path/backoff. A path toward an
    // old combat target must never be reused after the target changes or moves
    // materially.
    private readonly Dictionary<uint, Point3D> _pathGoal = [];

    private const long PathCacheMaxAge = 10_000;

    // Earliest time A* may be recomputed for this NPC. Survives path-cache
    // drops (unlike _pathTime) so a churning crowd — where the cached step is
    // invalidated almost every tick — cannot trigger a full A* recompute every
    // tick. A successful compute allows the next one after PathThrottleMs; a
    // FAILED compute (unreachable target — the search burns the whole node
    // budget before giving up) backs off for PathFailBackoffMs instead. The
    // direct-step fast path above A* still runs every action, so the NPC
    // resumes immediately once the straight line opens.
    private readonly Dictionary<uint, long> _nextPathfindMs = [];

    private const long PathThrottleMs = 750;

    private const long PathFailBackoffMs = 5_000;

    // NPC combat-chase A* node budget. Far below the full Pathfinder cap (which
    // is sized for player half-continent .walk): a creature only needs to route
    // around local obstacles toward a target in sight. Each explored node costs
    // ~35µs (per-tile map/static/object walkability checks), so an unreachable
    // target burns the WHOLE budget in the single-threaded apply phase — at
    // 2000 nodes that was a 40-75ms main-loop stall (visible as a ~160ms ping
    // spike). 500 nodes bounds the worst case to ~17ms and still covers any
    // realistic local detour.
    private const int NpcPathMaxNodes = 500;

    // Source-X NPC pathfinding eligibility (CCharNPCAct.cpp NPC_Pathfinding):
    // the pathfinder is a fixed 28x28 local box (MAX_NPC_PATH_STORAGE_SIZE =
    // UO_MAP_VIEW_SIGHT*2), so a target at dist >= 14 is "too far... too slow"
    // (:2432) and an adjacent one (dist < 2, :2434) needs no route — both take
    // direct/side steps instead. The same radius bounds the A* search box, so
    // an unreachable target in an enclosed space exhausts the box, not the
    // whole node budget.
    private const int NpcPathMinDist = 2;

    private const int NpcPathMaxDist = 14;

    private long _lastPathPurge;

    public void PurgeStalePaths()
    {
        long now = Environment.TickCount64;
        if (now - _lastPathPurge < 30_000) return;
        _lastPathPurge = now;

        List<uint>? stale = null;
        foreach (var uid in _pathCache.Keys)
        {
            var obj = _world.FindObject(new Core.Types.Serial(uid));
            bool expired = _pathTime.TryGetValue(uid, out long t) && now - t > PathCacheMaxAge;
            if (obj is not Character ch || ch.IsDeleted || ch.IsDead || expired)
                (stale ??= []).Add(uid);
        }
        if (stale != null)
        {
            foreach (var uid in stale)
            {
                _pathCache.Remove(uid);
                _pathIndex.Remove(uid);
                _pathTime.Remove(uid);
                _pathGoal.Remove(uid);
                _losFailCounts.Remove(uid);
                _nextPathfindMs.Remove(uid);
                _lastAttackNotify.Remove(uid);
                _nextItemScan.Remove(uid);
            }
        }

        // Throttle timestamps can outlive the path cache (they survive path
        // drops by design), so sweep them for gone NPCs here as well.
        if (_nextPathfindMs.Count > 0)
        {
            List<uint>? stalePf = null;
            foreach (var uid in _nextPathfindMs.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue;
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (stalePf ??= []).Add(uid);
            }
            if (stalePf != null)
                foreach (var uid in stalePf)
                {
                    _nextPathfindMs.Remove(uid);
                    _pathGoal.Remove(uid);
                }
        }

        // LOS-fail counters can accumulate for NPCs that never built a path
        // cache entry (target keeps moving but the straight line stays open),
        // so the path-cache sweep above never reaches them. Purge them here too.
        if (_losFailCounts.Count > 0)
        {
            List<uint>? staleLos = null;
            foreach (var uid in _losFailCounts.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue; // already handled above
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (staleLos ??= []).Add(uid);
            }
            if (staleLos != null)
                foreach (var uid in staleLos)
                    _losFailCounts.Remove(uid);
        }

        // Item-scan timers accumulate for every acting NPC regardless of the
        // path cache — sweep them for gone NPCs like the other per-uid state.
        if (_nextItemScan.Count > 0)
        {
            List<uint>? staleScan = null;
            foreach (var uid in _nextItemScan.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue; // already handled above
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (staleScan ??= []).Add(uid);
            }
            if (staleScan != null)
                foreach (var uid in staleScan)
                    _nextItemScan.Remove(uid);
        }

        // Attack-notify latches outlive the path cache the same way (an NPC
        // can fight without ever pathfinding), so sweep them for gone NPCs.
        if (_lastAttackNotify.Count > 0)
        {
            List<uint>? staleNotify = null;
            foreach (var uid in _lastAttackNotify.Keys)
            {
                if (_pathCache.ContainsKey(uid)) continue; // already handled above
                var obj = _world.FindObject(new Core.Types.Serial(uid));
                if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                    (staleNotify ??= []).Add(uid);
            }
            if (staleNotify != null)
                foreach (var uid in staleNotify)
                    _lastAttackNotify.Remove(uid);
        }
    }

    /// <summary>Idle fidget animation hook — wired by the server to the
    /// body-aware animation broadcast.</summary>
    public Action<Character>? OnNpcFidget { get; set; }

    /// <summary>Open an unlocked closed door for an NPC (state flip +
    /// observer broadcast — wired by the server). Returns true when the
    /// door ended up open so the blocked step can be re-validated.</summary>
    public Func<Character, Item, bool>? OnNpcOpenDoor { get; set; }

    /// <summary>NPC_AI_MOVEOBSTACLES: try to shift a movable, blocking item on
    /// <paramref name="blocked"/> onto the NPC's own tile (Source-X moves it to
    /// the char's position). Gated on the flag, CAN_C_USEHANDS and the Source-X
    /// smartness roll (INT &gt; rand(100)). Returns true when an item moved.</summary>
    internal bool TryClearObstacle(Character npc, Point3D blocked)
    {
        if (!GetNpcFlags(npc).HasFlag(NpcAIFlags.MoveObstacles))
            return false;
        var can = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.Can ?? CanFlags.None;
        if (!can.HasFlag(CanFlags.C_UseHands))
            return false;
        if (npc.Int <= _rand.Next(100))
            return false;

        foreach (var item in _world.GetItemsInRange(blocked, 1))
        {
            if (item.IsDeleted || item.ContainedIn.IsValid) continue;
            if (item.X != blocked.X || item.Y != blocked.Y) continue;
            var def = DefinitionLoader.GetItemDef(item.BaseId);
            if (def == null || !def.Can.HasFlag(CanFlags.I_Block)) continue; // only actual blockers
            // Source-X measures from the item's TOP (GetTopZ = base + height).
            int topZ = item.Z + (_world.MapData?.GetItemTileData(item.DispIdFull).Height ?? 0);
            if (Math.Abs(topZ - npc.Z) > 3) continue;
            if ((item.Attributes & (ObjAttributes.Move_Never | ObjAttributes.LockedDown |
                ObjAttributes.Secure | ObjAttributes.Static)) != 0) continue;

            // PlaceItem updates sector membership. Assigning Position directly
            // leaves the item in its old sector when this crosses a boundary,
            // corrupting range queries and walkability checks.
            if (!_world.PlaceItem(item, npc.Position))
                continue;
            OnNpcMovedItem?.Invoke(npc, item);
            return true;
        }
        return false;
    }

    // --- Movement helpers ---

    private bool CanNpcEnterTile(Character npc, Point3D pos)
    {
        var mapData = _world.MapData;
        if (mapData == null) return true;

        if (StandsOnWater(mapData, pos))
        {
            var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            bool canSwim = charDef != null && (charDef.Can & Core.Enums.CanFlags.C_Swim) != 0;
            if (!canSwim) return false;
        }

        if (IsTileDangerous(npc, pos))
            return false;

        return true;
    }

    private bool IsTileDangerous(Character npc, Point3D pos)
    {
        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            if (!item.TryGetTag("FIELD_DAMAGE", out _))
                continue;
            var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            bool fireImmune = charDef != null && (charDef.Can & CanFlags.C_FireImmune) != 0;
            if (!fireImmune)
                return true;
        }
        return false;
    }

    /// <summary>The part of the decision the shared walk check does NOT answer: whether
    /// the tile is dangerous or wants swimming, and whether somebody is already standing
    /// there.
    ///
    /// Split out because once CheckMovement has approved a step, re-running the cruder
    /// land-level tests over it only takes the approval away again - a dry deck laid
    /// over water is impassable ground by that measure, which kept a landlocked
    /// creature off its own pier even once the swim rule itself was corrected.</summary>
    private bool CanNpcOccupy(Character npc, Point3D pos, bool checkChars = true)
    {
        if (pos.X < 0 || pos.Y < 0)
            return false;

        if (!CanNpcEnterTile(npc, pos))
            return false;

        if (CharDefHelper.CanPassWalls(npc))
            return true;

        foreach (var other in checkChars ? _world.GetCharsInRange(pos, 0) : [])
        {
            if (other == npc || other.IsDeleted || other.IsDead)
                continue;
            if (other.MapIndex != pos.Map || other.X != pos.X || other.Y != pos.Y)
                continue;
            if ((other.IsStatFlag(StatFlag.Hidden) || other.IsStatFlag(StatFlag.Invisible))
                && other.PrivLevel >= PrivLevel.Counsel)
                continue;
            if (!SharesHeightWith(other, pos.Z))
                continue;
            return false;
        }

        return true;
    }

    private bool CanNpcMoveTo(Character npc, Point3D pos, bool checkChars = true)
    {
        if (!CanNpcOccupy(npc, pos, checkChars))
            return false;

        if (!CharDefHelper.CanPassWalls(npc))
        {
            var mapData = _world.MapData;
            if (mapData != null && !mapData.IsPassable(pos.Map, pos.X, pos.Y, pos.Z))
                return false;

            foreach (var item in _world.GetItemsInRange(pos, 0))
            {
                if (item.IsStaticBlock && BlocksAtHeight(item, pos.Z))
                    return false;
            }
        }

        return true;
    }

    /// <summary>Whether a step landing at <paramref name="pos"/> would put the creature
    /// ON the water, rather than on something dry above it.
    ///
    /// Source-X resolves the surface first and only then asks for the ability that
    /// surface needs - CAN_C_SWIM for water, CAN_C_WALK for a platform
    /// (CCharStatus.cpp:1812/1858). SphereNet asked the raw land tile, so a dry jetty,
    /// bridge or deck laid over water demanded that the creature be able to swim, and
    /// a landlocked NPC could not walk across its own pier.</summary>
    internal static bool StandsOnWater(MapData.MapDataManager mapData, Point3D pos)
    {
        var terrain = mapData.GetTerrainTile(pos.Map, pos.X, pos.Y);
        if (!mapData.GetLandTileData(terrain.TileId).IsWet)
            return false;

        // Above the water line something else is holding the creature up.
        return pos.Z <= terrain.Z;
    }

    /// <summary>Roughly a character's own height, as the shared walk check measures
    /// it (WalkCheck.PersonHeight). Used to decide whether something occupies the same
    /// vertical space as a creature standing on a tile.</summary>
    private const int NpcPersonHeight = 16;

    /// <summary>Whether a blocking item's own vertical extent reaches a creature
    /// standing at <paramref name="standZ"/>. Source-X hands the item's Z and height to
    /// CheckTile_Item, which separates what lies below the floor from what is a ceiling
    /// overhead (CServerMap.cpp:178). SphereNet decided from the item's TYPE alone, so a
    /// wall or a door on another storey closed off the ground underneath it.</summary>
    internal static bool BlocksAtHeight(Item item, int standZ)
    {
        int top = item.Z + Math.Max(1, (int)item.DefHeight);
        return top > standZ && item.Z < standZ + NpcPersonHeight;
    }

    /// <summary>Whether another character stands close enough in Z to be in the way.
    /// Source-X skips anyone more than five Z from the step's destination
    /// (ShoveCharAtPosition, CCharAct.cpp:4622) - someone on the floor above is not
    /// blocking the floor below. SphereNet compared X and Y only.</summary>
    internal static bool SharesHeightWith(Character other, int standZ) =>
        Math.Abs(other.Z - standZ) <= 5;

    private void Wander(Character npc)
    {
        if (OnNpcActWander?.Invoke(npc) == true)
            return;

        // Idle fidget (reference parity: idle NPCs randomly play a fidget
        // animation) — occasionally animate in place instead of stepping so
        // standing NPCs look alive without extra packet pressure.
        if (_rand.Next(8) == 0)
        {
            OnNpcFidget?.Invoke(npc);
            return;
        }

        // Source-X NPC_Act_Wander: step in the CURRENT facing turned by only
        // −1/0/+1 (m_dirFace persistence) — a gently curving "staggering
        // walk". Independent random deltas made wanderers jitter in place.
        var wanderDir = (Direction)((((int)npc.Direction & 0x07) + _rand.Next(-1, 2) + 8) % 8);
        GetDirectionDelta(wanderDir, out short dx, out short dy);
        npc.Direction = wanderDir;

        short nx = (short)(npc.X + dx);
        short ny = (short)(npc.Y + dy);
        var mapData = _world.MapData;
        sbyte nz = ResolveNpcStepZ(npc, nx, ny);
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, newPos))
        {
            if (OnNpcOpenDoor != null && _rand.Next(2) == 0)
            {
                var door = FindClosedDoorAt(newPos);
                if (door != null)
                    OnNpcOpenDoor(npc, door);
            }
            if (!CanNpcMoveTo(npc, newPos))
                TryClearObstacle(npc, newPos);
            if (!CanNpcMoveTo(npc, newPos))
            {
                TrySideStep(npc, wanderDir);
                return;
            }
        }

        // Face the step direction so the 0x77 move matches the tile delta and the
        // client walk-animates instead of snapping the NPC.
        npc.Direction = npc.Position.GetDirectionTo(newPos);
        _world.MoveCharacter(npc, newPos);
    }

    /// <summary>Source-X NPC_WalkToPoint blocked-step fallback: every mover —
    /// regardless of INT or the PATH flag — gets a ~70% chance to sidestep by
    /// turning ±1..±4 directions off the blocked heading and taking that tile.
    /// Without it a dumb/pathless NPC froze facing the wall until the straight
    /// line cleared on its own.</summary>
    private bool TrySideStep(Character npc, Direction dir)
    {
        int roll = _rand.Next(100);
        if (roll < 30)
            return false;
        int diff = roll < 35 ? 4 : roll < 40 ? 3 : roll < 65 ? 2 : 1;
        if (_rand.Next(2) == 0)
            diff = -diff;
        var sideDir = (Direction)((((int)dir & 0x07) + diff + 8) % 8);
        GetDirectionDelta(sideDir, out short dx, out short dy);
        short nx = (short)(npc.X + dx), ny = (short)(npc.Y + dy);
        sbyte nz = ResolveNpcStepZ(npc, nx, ny);
        if (Math.Abs(nz - npc.Z) > 12)
            return false;
        var pos = new Point3D(nx, ny, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, pos))
            return false;
        npc.Direction = sideDir;
        _world.MoveCharacter(npc, pos);
        return true;
    }

    /// <summary>Wander with home range check. Source-X: m_Home_Dist_Wander.</summary>
    private void WanderHome(Character npc)
    {
        if (!TryResolveHome(npc, out Point3D home, out int homeDist))
        {
            Wander(npc);
            return;
        }

        // Chebyshev like Source-X GetDist — the old Manhattan sum over-counted
        // diagonals, halving the effective HOMEDIST leash on the diagonal.
        int curDist = npc.MapIndex == home.Map
            ? npc.Position.GetDistanceTo(home)
            : int.MaxValue;
        if (curDist > homeDist)
        {
            if (npc.MapIndex != home.Map)
            {
                if (Character.OnNpcLostTeleport?.Invoke(npc) != true)
                    _world.MoveCharacter(npc, home);
                return;
            }
            MoveToward(npc, home);
            return;
        }
        Wander(npc);
    }

    /// <summary>Home from Character.Home field; legacy TAG.HOME_* fallback.</summary>
    /// <summary>NPC step Z via the shared standing resolver (audit design):
    /// GetEffectiveZ saw neither multis, dynamics nor custom houses, so an
    /// NPC walking a ship deck or house floor drifted onto the terrain
    /// underneath the structure.</summary>
    private sbyte ResolveNpcStepZ(Character npc, int x, int y) =>
        TryResolveNpcStepZ(npc, x, y, out sbyte z) ? z : npc.Z;

    /// <summary>The height the NPC would actually stand at on this tile, and whether a
    /// surface was found at all. A cached A* step carries only the search's cheap
    /// approximation (Pathfinder uses MapData.GetEffectiveZ, which sees neither multis
    /// nor dynamics), so applying its Z verbatim walked an NPC off a ship deck or house
    /// floor onto the terrain underneath. Source-X resolves the surface at the moment
    /// of the step instead (CheckValidMove, CCharStatus.cpp:1972).</summary>
    private bool TryResolveNpcStepZ(Character npc, int x, int y, out sbyte z)
    {
        if (_world.Standing.ResolveStandingSurface(npc, npc.MapIndex, x, y, npc.Z,
                WalkCheck.StandingPolicy.Settle) is { Found: true } stand)
        {
            z = stand.Z;
            return true;
        }

        z = npc.Z;
        return false;
    }

    /// <summary>Whether the NPC may take this single step. On top of the destination
    /// tile itself, a DIAGONAL step needs both of the orthogonal tiles beside it:
    /// Source-X tests them from the old point before moving (CheckValidMove,
    /// CCharStatus.cpp:1988) so a creature cannot cut through the corner where two
    /// walls meet. The reference skips that test while pathfinding (fPathFinding) and
    /// applies it to the real step, which is why the search alone was never the place
    /// for it - the direct step never runs A* at all.</summary>
    /// <summary>Whether the NPC is physically able to walk at all right now.
    ///
    /// Source-X asks this at the real step, through CanMoveWalkTo -> CanMove
    /// (CCharAct.cpp:4716/4571): a GM is exempt, a frozen or stoned creature cannot
    /// move (OnFreezeCheck, :4525), and a living one out of stamina cannot either. The
    /// reference reads the CURRENT stamina pool there - Stat_GetVal(STAT_DEX) - not the
    /// base dexterity.
    ///
    /// It belongs at the step rather than at the decision: a freeze landing between the
    /// two used to leave the walk running anyway. NpcAI.OnTickAction's own comment said
    /// "living, non-frozen NPC" while nothing checked it; combat had the test, movement
    /// did not.</summary>
    private static bool CanNpcMove(Character npc)
    {
        if (npc.PrivLevel >= PrivLevel.GM)
            return true;
        if (npc.IsStatFlag(StatFlag.Freeze) || npc.IsStatFlag(StatFlag.Stone))
            return false;
        // Only where a stamina pool actually exists. A creature with no MaxStam has no
        // stamina model at all - the Dex setter raises the ceiling but never fills the
        // pool - and reading its empty pool as exhaustion would leave it standing
        // still forever rather than tiring it out.
        return npc.IsDead || npc.MaxStam <= 0 || npc.Stam > 0;
    }

    /// <summary>Work out the step the NPC would take in <paramref name="dir"/>, and
    /// where it would land.
    ///
    /// The geometry comes from the shared walk check - the very one a player's step
    /// goes through - which knows tile heights, ceilings, climb limits, the diagonal
    /// double-edge rule and the surface to land on. The NPC path used to answer from
    /// item TYPE alone, so it walked into an impassable object that happened not to be
    /// a Wall or Door, and under a ceiling too low to fit; the height it moved to was
    /// whatever a separate resolver guessed. Source-X routes the real NPC step through
    /// CanMoveWalkTo -> CheckValidMove for exactly this reason
    /// (NPC_WalkToPoint, CCharNPCAct.cpp:493).
    ///
    /// Without map data there is no geometry to consult - the shape a unit-test world
    /// has - so the older tile checks stand in, with the diagonal corner rule applied
    /// on its own.</summary>
    private bool TryNpcStep(Character npc, Direction dir, out Point3D dest)
    {
        var plain = dir & ~Direction.Running;
        GetDirectionDelta(plain, out short dx, out short dy);
        short nx = (short)(npc.X + dx);
        short ny = (short)(npc.Y + dy);
        dest = default;

        if (_world.MapData != null)
        {
            if (!_world.Standing.CheckMovement(npc, npc.Position, plain, out int landZ))
                return false;

            dest = new Point3D(nx, ny, (sbyte)landZ, npc.MapIndex);
            return CanNpcOccupy(npc, dest);
        }

        sbyte fallbackZ = ResolveNpcStepZ(npc, nx, ny);
        if (Math.Abs(fallbackZ - npc.Z) > 12)
            return false;

        dest = new Point3D(nx, ny, fallbackZ, npc.MapIndex);
        return CanNpcStepTo(npc, plain, dest);
    }

    private bool CanNpcStepTo(Character npc, Direction dir, Point3D dest)
    {
        if (!CanNpcMoveTo(npc, dest))
            return false;

        var plain = dir & ~Direction.Running;
        if (((byte)plain & 1) == 0)
            return true;    // an orthogonal step has no corner to cut

        foreach (var side in new[]
        {
            (Direction)(((byte)plain + 7) % 8),   // first orthogonal
            (Direction)(((byte)plain + 1) % 8),   // second
        })
        {
            GetDirectionDelta(side, out short sdx, out short sdy);
            short sx = (short)(npc.X + sdx);
            short sy = (short)(npc.Y + sdy);
            // Terrain and structures only. Source-X tests the side tiles with
            // CheckValidMove, which knows nothing about characters - blocking mobiles
            // are weighed against the DESTINATION alone (CanMoveWalkTo's fCheckChars).
            // Counting them here would stop a creature walking diagonally past anyone
            // standing beside it.
            var sidePos = new Point3D(sx, sy, ResolveNpcStepZ(npc, sx, sy), npc.MapIndex);
            if (!CanNpcMoveTo(npc, sidePos, checkChars: false))
                return false;
        }

        return true;
    }

    private static bool TryResolveHome(Character npc, out Point3D home, out int wanderDist)
    {
        // Source-X default m_Home_Dist_Wander = INT16_MAX ("as far as I
        // want") — a home point is only a leash when HOMEDIST is scripted.
        // The old default of 10 confined every homed spawn to a small box.
        wanderDist = npc.HomeDist > 0 ? npc.HomeDist : short.MaxValue;
        if (npc.Home.X != 0 || npc.Home.Y != 0)
        {
            home = npc.Home;
            return true;
        }

        if (npc.TryGetTag("HOME_X", out string? hx) && npc.TryGetTag("HOME_Y", out string? hy) &&
            short.TryParse(hx, out short homeX) && short.TryParse(hy, out short homeY))
        {
            sbyte homeZ = npc.Z;
            if (npc.TryGetTag("HOME_Z", out string? hz) &&
                sbyte.TryParse(hz, out sbyte parsedZ))
                homeZ = parsedZ;
            byte homeMap = npc.MapIndex;
            if (npc.TryGetTag("HOME_MAP", out string? hm) &&
                byte.TryParse(hm, out byte parsedMap))
                homeMap = parsedMap;
            home = new Point3D(homeX, homeY, homeZ, homeMap);
            if (npc.TryGetTag("HOME_DIST", out string? hdStr) &&
                int.TryParse(hdStr, out int hd) && hd > 0)
                wanderDist = Math.Clamp(hd, 1, short.MaxValue);
            return true;
        }

        home = default;
        return false;
    }

    private void MoveToward(Character npc, Point3D target, bool run = false)
    {
        if (target.Map != npc.MapIndex || (target.X == npc.X && target.Y == npc.Y))
            return;

        var dir = npc.Position.GetDirectionTo(target);
        GetDirectionDelta(dir, out short dx, out short dy);
        if (run)
            dir |= Direction.Running;

        // Physically able to walk at all? Read at the step, not at the decision.
        if (!CanNpcMove(npc))
            return;

        short nx = (short)(npc.X + dx);
        short ny = (short)(npc.Y + dy);
        var mapData = _world.MapData;

        // The tile the step aims at, for the door/obstacle lookups below; the height
        // the NPC would actually land on is settled by TryNpcStep.
        var stepTile = new Point3D(nx, ny, npc.Z, npc.MapIndex);

        bool directBlocked = !TryNpcStep(npc, dir, out var directPos);

        // Reference parity (NPC door handling in the idle look-at path): a
        // blocked adjacent step may just be a closed door — try to open it
        // (50% per attempt, like the reference) and re-check the tile.
        if (directBlocked && OnNpcOpenDoor != null && _rand.Next(2) == 0)
        {
            var door = FindClosedDoorAt(stepTile);
            if (door != null && OnNpcOpenDoor(npc, door))
                directBlocked = !TryNpcStep(npc, dir, out directPos);
        }

        if (directBlocked && TryClearObstacle(npc, stepTile))
            directBlocked = !TryNpcStep(npc, dir, out directPos);

        if (!directBlocked)
        {
            npc.Direction = dir;
            _world.MoveCharacter(npc, directPos);
            _pathCache.Remove(npc.Uid.Value);
            _pathIndex.Remove(npc.Uid.Value);
            _pathTime.Remove(npc.Uid.Value);
            _pathGoal.Remove(npc.Uid.Value);
            _nextPathfindMs.Remove(npc.Uid.Value);
            return;
        }

        var npcFlags = GetNpcFlags(npc);
        if (!npcFlags.HasFlag(NpcAIFlags.Path))
        {
            if (!TrySideStep(npc, dir))
            {
                npc.Direction = dir;
                // Source-X retries a blocked route in 0.5s instead of waiting
                // out the full walk delay (up to 5s of standing still).
                npc.NextNpcActionTime = Math.Min(npc.NextNpcActionTime, Environment.TickCount64 + 500);
            }
            return;
        }

        // Source-X NPC_Pathfinding intelligence gate: a creature only routes
        // with A* when it is smart enough (effective INT >= 30). NPC_AI_ALWAYSINT
        // bypasses the check (treated as INT 300). A dumb creature just faces the
        // target and takes the blocked-direct step on later ticks as the line
        // opens — it never burns the A* node budget.
        int effInt = npcFlags.HasFlag(NpcAIFlags.AlwaysInt) ? 300 : npc.Int;
        if (effInt < 30)
        {
            if (!TrySideStep(npc, dir))
            {
                npc.Direction = dir;
                npc.NextNpcActionTime = Math.Min(npc.NextNpcActionTime, Environment.TickCount64 + 500);
            }
            return;
        }

        // Source-X distance eligibility (NPC_Pathfinding :2432/:2434): A* only
        // routes to targets 2..13 tiles out. Farther ones take direct/side steps
        // until in range — this alone removes the "chase across the map into a
        // full node-budget burn" case; adjacent ones never need a route.
        int targetDist = npc.Position.GetDistanceTo(target);
        if (targetDist < NpcPathMinDist || targetDist >= NpcPathMaxDist)
        {
            if (!TrySideStep(npc, dir))
            {
                npc.Direction = dir;
                npc.NextNpcActionTime = Math.Min(npc.NextNpcActionTime, Environment.TickCount64 + 500);
            }
            return;
        }

        // Direct path blocked — use A* pathfinding
        uint uid = npc.Uid.Value;
        if (_pathGoal.TryGetValue(uid, out Point3D oldGoal) &&
            (oldGoal.Map != target.Map || oldGoal.GetDistanceTo(target) > 2))
        {
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            _pathTime.Remove(uid);
            _pathGoal.Remove(uid);
            _nextPathfindMs.Remove(uid);
        }
        if (!npcFlags.HasFlag(NpcAIFlags.PersistentPath))
        {
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            _pathTime.Remove(uid);
        }
        if (!_pathCache.TryGetValue(uid, out var path) || path.Count == 0)
        {
            // Throttle full A* recomputes per NPC. In a churning crowd the
            // cached step is blocked nearly every tick, which would otherwise
            // force a fresh A* search every tick for every NPC. Between allowed
            // recomputes just face the target and hold — a closer/unblocked NPC
            // keeps the pressure on, and the path refreshes shortly after.
            long nowMs = Environment.TickCount64;
            if (_nextPathfindMs.TryGetValue(uid, out long nextPf) && nowMs < nextPf)
            {
                npc.Direction = dir;
                return;
            }

            // Calculate new path
            var npcDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            var npcCanFlags = npcDef?.Can ?? Core.Enums.CanFlags.None;
            path = _pathfinder.FindPath(npc.Position, target, npc.MapIndex, npcCanFlags, npc,
                NpcPathMaxNodes, NpcPathMaxDist);
            if (path == null || path.Count == 0)
            {
                _nextPathfindMs[uid] = nowMs + PathFailBackoffMs;
                _pathGoal[uid] = target;
                npc.Direction = dir;
                return;
            }
            _nextPathfindMs[uid] = nowMs + PathThrottleMs;
            _pathCache[uid] = path;
            _pathIndex[uid] = 0;
            _pathTime[uid] = Environment.TickCount64;
            _pathGoal[uid] = target;
        }

        int idx = _pathIndex.GetValueOrDefault(uid, 0);
        if (idx >= path.Count)
        {
            // Path exhausted — recalculate
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            _pathTime.Remove(uid);
            return;
        }

        var nextStep = path[idx];

        // The step has to still be a STEP. Source-X drops a stored route whose next
        // point is no longer one tile away (NPC_WalkToPoint, CCharNPCAct.cpp:463);
        // SphereNet applied it regardless, so an NPC teleported elsewhere while a path
        // was cached snapped back onto the old route - six tiles in a single move, with
        // nothing walked in between.
        if (nextStep.Map != npc.MapIndex || npc.Position.GetDistanceTo(nextStep) != 1)
        {
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            _pathTime.Remove(uid);
            return;
        }

        // The search's Z is an approximation; the real landing surface is resolved
        // here, at the step. Without a surface the step is refused and the route
        // recomputed rather than committed at the guessed height.
        // The route's own Z is the search's approximation; the step that gets applied
        // is the one the walk check works out from where the NPC is standing now.
        var pathDir = npc.Position.GetDirectionTo(nextStep);
        if (!TryNpcStep(npc, pathDir, out var landing) ||
            landing.X != nextStep.X || landing.Y != nextStep.Y)
        {
            // NPC_AI_MOVEOBSTACLES (Source-X NPC_WalkToPoint, CCharNPCAct.cpp:525):
            // a hands-capable, smart-enough NPC shifts a movable blocking item
            // onto its own tile before giving the path up.
            TryClearObstacle(npc, nextStep);
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            _pathTime.Remove(uid);
            npc.Direction = pathDir;
            return;
        }

        nextStep = landing;

        npc.Direction = run ? pathDir | Direction.Running : pathDir;
        _world.MoveCharacter(npc, nextStep);
        _pathIndex[uid] = idx + 1;
    }

    // --- N2: parallel-phase pathfind prestage ---

    // Per-tick A* budget consumed by the prestage. On a low-core box the
    // parallel build phase cannot absorb many 500-node searches (a chase burst
    // showed up live as npc_build=130ms); over-budget NPCs take a short defer
    // instead — the serial side sees the closed throttle window and just faces
    // the target until their turn comes.
    private int _tickPathfindBudget = int.MaxValue;

    private const long PathDeferMs = 150;

    /// <summary>Arm the per-tick prestage A* budget (called once per multicore
    /// tick before the parallel BuildDecision fan-out).</summary>
    public void BeginTickPathfindBudget(int budget) => _tickPathfindBudget = budget;

    /// <summary>Read-only mirror of the conditions under which the serial
    /// <see cref="MoveToward"/> would run a full A* this tick for a combat/pet
    /// chase. When they hold, runs the search HERE (parallel build phase —
    /// Pathfinder scratch state is thread-static, world reads follow the
    /// parallel-compute-phase safety contract) and returns the result to be
    /// carried on the <see cref="NpcDecision"/>. Mutates nothing.</summary>
    private (List<Point3D>? Path, Point3D Goal, bool Ran, bool Deferred) TryPrestagePathfind(Character npc)
    {
        // The chase goal the serial brain will walk toward this tick: the fight
        // target, else the master for a following pet. Other MoveToward callers
        // (wander-home leash, corpse looting, investigate) stay serial-computed.
        Character? target = null;
        if (npc.FightTarget.IsValid)
            target = _world.FindChar(npc.FightTarget);
        else if (npc.NpcMaster.IsValid &&
                 npc.PetAIMode is PetAIMode.Follow or PetAIMode.Come or PetAIMode.Guard)
            target = _world.FindChar(npc.NpcMaster);
        if (target == null || target.IsDeleted || target.IsDead || target.MapIndex != npc.MapIndex)
            return (null, default, false, false);

        Point3D goal = target.Position;
        int dist = npc.Position.GetDistanceTo(goal);
        if (dist < NpcPathMinDist || dist >= NpcPathMaxDist)
            return (null, default, false, false);

        var npcFlags = GetNpcFlags(npc);
        if (!npcFlags.HasFlag(NpcAIFlags.Path))
            return (null, default, false, false);
        int effInt = npcFlags.HasFlag(NpcAIFlags.AlwaysInt) ? 300 : npc.Int;
        if (effInt < 30)
            return (null, default, false, false);

        // Direct step open → the serial side takes it without A*. (The serial
        // path may additionally open a door / shift an obstacle first; if that
        // frees the step, the seeded state is simply cleared by the direct-step
        // branch, same as any other stale cache entry.)
        var dir = npc.Position.GetDirectionTo(goal);
        GetDirectionDelta(dir, out short dx, out short dy);
        short nx = (short)(npc.X + dx), ny = (short)(npc.Y + dy);
        sbyte nz = ResolveNpcStepZ(npc, nx, ny);
        if (Math.Abs(nz - npc.Z) <= 12 &&
            CanNpcMoveTo(npc, new Point3D(nx, ny, nz, npc.MapIndex)))
            return (null, default, false, false);

        uint uid = npc.Uid.Value;
        // A fresh cached path toward (about) this goal → serial reuses it as-is.
        if (_pathCache.TryGetValue(uid, out var cachedPath) && cachedPath.Count > 0 &&
            _pathGoal.TryGetValue(uid, out var cachedGoal) &&
            cachedGoal.Map == goal.Map && cachedGoal.GetDistanceTo(goal) <= 2)
            return (null, default, false, false);
        // Throttle/backoff window still closed → serial won't recompute either.
        if (_nextPathfindMs.TryGetValue(uid, out long nextPf) &&
            Environment.TickCount64 < nextPf)
            return (null, default, false, false);

        // Per-tick A* budget: over-budget chasers take a short defer instead of
        // stacking 500-node searches into one build phase.
        if (System.Threading.Interlocked.Decrement(ref _tickPathfindBudget) < 0)
            return (null, goal, false, true);

        var npcCanFlags = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.Can ?? CanFlags.None;
        var path = _pathfinder.FindPath(npc.Position, goal, npc.MapIndex, npcCanFlags, npc,
            NpcPathMaxNodes, NpcPathMaxDist);
        return (path, goal, true, false);
    }

    /// <summary>Serial-phase merge of a parallel prestage result into the path
    /// cache, applied only when the serial state still calls for a recompute
    /// (no fresh path, throttle open) — the serial side stays the source of
    /// truth. A failed search records the same fail-backoff the serial compute
    /// would have, so the ~17ms unreachable burn never hits the apply phase.
    /// The cached step is still re-validated by MoveToward before use.</summary>
    private void SeedPrestagedPath(Character npc, in NpcDecision decision)
    {
        uint uid = npc.Uid.Value;
        long nowMs = Environment.TickCount64;

        if (_pathCache.TryGetValue(uid, out var existing) && existing.Count > 0)
            return;
        if (_nextPathfindMs.TryGetValue(uid, out long nextPf) && nowMs < nextPf)
            return;

        // Over-budget defer: close the serial window briefly so this tick's
        // apply phase doesn't run the search the budget just refused.
        if (decision.PrestageDeferred)
        {
            _nextPathfindMs[uid] = nowMs + PathDeferMs;
            _pathGoal[uid] = decision.PrestageGoal;
            return;
        }

        if (decision.PrestagedPath is { Count: > 0 } path)
        {
            _pathCache[uid] = path;
            _pathIndex[uid] = 0;
            _pathTime[uid] = nowMs;
            _pathGoal[uid] = decision.PrestageGoal;
            _nextPathfindMs[uid] = nowMs + PathThrottleMs;
        }
        else
        {
            _nextPathfindMs[uid] = nowMs + PathFailBackoffMs;
            _pathGoal[uid] = decision.PrestageGoal;
        }
    }

    /// <summary>Find a closed, unlocked door item on the given tile
    /// (within a reasonable Z window). Internal for tests.</summary>
    internal Item? FindClosedDoorAt(Point3D pos)
    {
        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            if (item.IsDeleted) continue;
            if (Math.Abs(item.Z - pos.Z) > 15) continue;
            if (!DoorHelper.IsDoorItem(item, _world.MapData)) continue;
            if (item.ItemType is ItemType.DoorLocked or ItemType.PortLocked) continue;
            if (item.TryGetTag("DOOR_OPEN", out string? openStr) && openStr == "1") continue;
            return item;
        }
        return null;
    }

    private static void GetDirectionDelta(Direction dir, out short dx, out short dy)
    {
        dx = 0; dy = 0;
        switch (dir)
        {
            case Direction.North: dy = -1; break;
            case Direction.NorthEast: dx = 1; dy = -1; break;
            case Direction.East: dx = 1; break;
            case Direction.SouthEast: dx = 1; dy = 1; break;
            case Direction.South: dy = 1; break;
            case Direction.SouthWest: dx = -1; dy = 1; break;
            case Direction.West: dx = -1; break;
            case Direction.NorthWest: dx = -1; dy = -1; break;
        }
    }

    private static int DeterministicJitter(uint uid, long nowTick, int maxExclusive)
    {
        if (maxExclusive <= 0) return 0;
        unchecked
        {
            uint mixed = uid * 2654435761u ^ (uint)nowTick;
            return (int)(mixed % (uint)maxExclusive);
        }
    }

}
