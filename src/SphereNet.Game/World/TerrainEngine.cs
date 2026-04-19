using SphereNet.Core.Types;

namespace SphereNet.Game.World;

/// <summary>
/// Terrain engine for height validation and line-of-sight checks.
/// Maps to CWorldMap::GetHeightPoint and CanSeeLOS in Source-X.
/// Wraps MapDataManager for movement and LOS validation.
/// </summary>
public sealed class TerrainEngine
{
    private readonly MapData.MapDataManager? _mapData;

    /// <summary>Max climb height per step (Source-X default).</summary>
    private const int MaxClimb = 18;

    /// <summary>Default character height for LOS checks.</summary>
    private const int PersonHeight = 16;

    public TerrainEngine(MapData.MapDataManager? mapData)
    {
        _mapData = mapData;
    }

    /// <summary>Get the ground Z height at a position.</summary>
    public sbyte GetGroundHeight(short x, short y, byte mapId)
    {
        if (_mapData == null) return 0;
        var cell = _mapData.GetTerrainTile(mapId, x, y);
        return cell.Z;
    }

    /// <summary>
    /// Get the effective standing Z including statics/bridges.
    /// </summary>
    public sbyte GetEffectiveZ(short x, short y, byte mapId, sbyte currentZ = 0)
    {
        if (_mapData == null) return 0;
        return _mapData.GetEffectiveZ(mapId, x, y, currentZ);
    }

    /// <summary>
    /// Validate that a move to the target position is physically possible.
    /// Checks terrain height difference and blocking statics.
    /// </summary>
    public bool CanMoveToPosition(Point3D from, Point3D to)
    {
        if (_mapData == null) return true;

        // Check impassable statics
        if (!_mapData.IsPassable(to.Map, to.X, to.Y, from.Z))
            return false;

        // Check height difference using current Z as reference
        sbyte targetZ = _mapData.GetEffectiveZ(to.Map, to.X, to.Y, from.Z);
        int heightDiff = Math.Abs(targetZ - from.Z);
        if (heightDiff > MaxClimb)
            return false;

        return true;
    }

    /// <summary>
    /// Check line of sight between two points.
    /// Simplified Bresenham-based LOS with terrain + static obstruction.
    /// </summary>
    public bool CanSeeLOS(Point3D from, Point3D to)
    {
        if (_mapData == null) return true;
        if (from.Map != to.Map) return false;

        int dx = Math.Abs(to.X - from.X);
        int dy = Math.Abs(to.Y - from.Y);
        int steps = Math.Max(dx, dy);
        if (steps == 0) return true;

        float stepX = (float)(to.X - from.X) / steps;
        float stepY = (float)(to.Y - from.Y) / steps;

        // LOS height: eye level (Z + PersonHeight)
        float fromEye = from.Z + PersonHeight;
        float toEye = to.Z + PersonHeight;
        float stepZ = (toEye - fromEye) / steps;

        float cx = from.X, cy = from.Y, cz = fromEye;

        for (int i = 1; i < steps; i++)
        {
            cx += stepX;
            cy += stepY;
            cz += stepZ;

            short checkX = (short)Math.Round(cx);
            short checkY = (short)Math.Round(cy);
            int checkZ = (int)Math.Round(cz);

            // Check terrain blocks LOS
            sbyte groundZ = _mapData.GetEffectiveZ(from.Map, checkX, checkY);
            if (groundZ > checkZ)
                return false;

            // Check impassable statics at this ray point
            if (!_mapData.IsPassable(from.Map, checkX, checkY, checkZ))
                return false;
        }

        return true;
    }
}
