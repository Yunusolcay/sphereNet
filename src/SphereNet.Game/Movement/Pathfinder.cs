using SphereNet.Core.Enums;
using SphereNet.Core.Types;

namespace SphereNet.Game.Movement;

/// <summary>
/// A* pathfinding for NPC movement. Grid-based 2D pathfinding.
/// Maps to NPC_AI_PATH logic in Source-X.
/// </summary>
public sealed class Pathfinder
{
    private readonly World.GameWorld _world;
    // Sized for player-triggered .walk commands crossing half-continents.
    // NPC AI calls this every tick, so keep enough budget for long paths but
    // not unbounded — A* with open set + closed set is O(N log N) worst-case.
    private const int MaxNodes = 20000;
    private const int MaxPathLength = 256;

    public Pathfinder(World.GameWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// Find a path from start to goal. Returns the next step direction,
    /// or null if no path found.
    /// </summary>
    public List<Point3D>? FindPath(Point3D start, Point3D goal, byte mapIndex, CanFlags canFlags = CanFlags.None)
    {
        if (start.GetDistanceTo(goal) <= 1)
            return [goal];

        var openSet = new PriorityQueue<PathNode, int>();
        var closedSet = new HashSet<long>();
        var cameFrom = new Dictionary<long, PathNode>();

        var startNode = new PathNode(start.X, start.Y, start.Z, 0, Heuristic(start, goal));
        openSet.Enqueue(startNode, startNode.F);

        int nodesExplored = 0;

        while (openSet.Count > 0 && nodesExplored < MaxNodes)
        {
            var current = openSet.Dequeue();
            long currentKey = PackKey(current.X, current.Y);

            if (current.X == goal.X && current.Y == goal.Y)
                return ReconstructPath(cameFrom, current);

            if (!closedSet.Add(currentKey))
                continue;

            nodesExplored++;

            // Explore 8 neighbors
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;

                    short nx = (short)(current.X + dx);
                    short ny = (short)(current.Y + dy);
                    long neighborKey = PackKey(nx, ny);

                    if (closedSet.Contains(neighborKey))
                        continue;

                    // Each tile has its own surface Z (terrain vs. static floors,
                    // bridges, steps). Using current.Z for every neighbor makes
                    // pathfinding fail as soon as terrain height changes by one
                    // step. Resolve the effective Z per-tile using MapData.
                    sbyte nz = _world.MapData?.GetEffectiveZ(mapIndex, nx, ny, current.Z) ?? current.Z;

                    // Climb limit: avoid jumping onto rooftops / off cliffs.
                    if (Math.Abs(nz - current.Z) > 12)
                        continue;

                    var neighborPos = new Point3D(nx, ny, nz, mapIndex);
                    if (!IsWalkable(neighborPos, canFlags))
                        continue;

                    int moveCost = (dx != 0 && dy != 0) ? 14 : 10; // diagonal vs cardinal
                    int newG = current.G + moveCost;
                    int h = Heuristic(neighborPos, goal);

                    var neighbor = new PathNode(nx, ny, nz, newG, h);

                    if (cameFrom.ContainsKey(neighborKey))
                    {
                        var existing = cameFrom[neighborKey];
                        if (newG >= existing.G)
                            continue;
                    }

                    cameFrom[neighborKey] = current;
                    openSet.Enqueue(neighbor, neighbor.F);
                }
            }
        }

        return null; // no path found
    }

    private bool IsWalkable(Point3D pos, CanFlags canFlags = CanFlags.None)
    {
        foreach (var ch in _world.GetCharsInRange(pos, 0))
        {
            if (!ch.IsDead) return false;
        }

        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            if (item.IsStaticBlock) return false;
            if (item.TryGetTag("FIELD_DAMAGE", out _))
            {
                if ((canFlags & CanFlags.C_FireImmune) == 0)
                    return false;
            }
        }

        var mapData = _world.MapData;
        if (mapData != null)
        {
            if (!mapData.IsPassable(pos.Map, pos.X, pos.Y, pos.Z))
                return false;

            var terrain = mapData.GetTerrainTile(pos.Map, pos.X, pos.Y);
            var landData = mapData.GetLandTileData(terrain.TileId);
            if (landData.IsWet && (canFlags & CanFlags.C_Swim) == 0)
                return false;
        }

        return true;
    }

    private static int Heuristic(Point3D a, Point3D b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return 10 * (dx + dy) + (14 - 20) * Math.Min(dx, dy); // octile distance
    }

    private static long PackKey(short x, short y) =>
        ((long)(ushort)x << 16) | (ushort)y;

    private static List<Point3D> ReconstructPath(Dictionary<long, PathNode> cameFrom, PathNode current)
    {
        var path = new List<Point3D>();
        var node = current;
        long key = PackKey(node.X, node.Y);

        while (cameFrom.ContainsKey(key))
        {
            path.Add(new Point3D(node.X, node.Y, node.Z, 0));
            node = cameFrom[key];
            key = PackKey(node.X, node.Y);
        }

        path.Reverse();
        if (path.Count > MaxPathLength)
            path.RemoveRange(MaxPathLength, path.Count - MaxPathLength);
        return path;
    }

    private readonly struct PathNode(short x, short y, sbyte z, int g, int h)
    {
        public short X { get; } = x;
        public short Y { get; } = y;
        public sbyte Z { get; } = z;
        public int G { get; } = g;
        public int H { get; } = h;
        public int F => G + H;
    }
}
