using System.Text;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Diagnostic harness (not a parity assertion). Loads the REAL mortechUO MUL data
/// and walks the exact staircase path from the in-game "thrown left" report,
/// printing the server WalkCheck's per-step Z decision and the full candidate
/// analysis at the blocking tile. Used to locate the client/server divergence
/// that snaps a climbing player onto the upper platform. They assert nothing —
/// they exercise the real walk-check against real data and print the decision —
/// so they are gated on the data being present rather than statically skipped:
/// they run on a box that has a client MUL set and no-op cleanly in CI.
/// </summary>
public class StairThrowDiagnosticTests
{
    /// <summary>Client data directories to probe, first match wins. The live
    /// server pack ships its own MUL set; the older standalone path is kept for
    /// boxes that still have it.</summary>
    private static readonly string[] MulCandidates =
    [
        @"C:\sphereNetServer\mul",
        @"C:\mortechUO\mul",
    ];

    private static string? FindMulDir() =>
        MulCandidates.FirstOrDefault(d =>
            Directory.Exists(d) && File.Exists(Path.Combine(d, "tiledata.mul")));

    /// <summary>Diagnostic dumps go to the temp dir, never into the repo — a test
    /// run must not leave the working tree dirty.</summary>
    private static string TraceOutputPath(string name) =>
        Path.Combine(Path.GetTempPath(), name);

    private readonly ITestOutputHelper _out;

    public StairThrowDiagnosticTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TraceClimbAt_1460_1651_North()
    {
        string? MulDir = FindMulDir();
        if (MulDir == null)
        {
            _out.WriteLine($"SKIP: no MUL data found in {string.Join(", ", MulCandidates)}");
            return;
        }

        var lf = LoggerFactory.Create(b => { });
        var map = new MapDataManager(MulDir);
        map.Load();
        map.InitMap(0, 7168, 4096); // loads UOP terrain + statics0 for map 0

        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;

        var walk = new WalkCheck(world);

        var ch = world.CreateCharacter();
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 50; ch.MaxStam = 50; ch.MaxMana = 50;
        ch.Hits = 50; ch.Stam = 50; ch.Mana = 50;
        ch.IsPlayer = true;
        // Start one tile south of the climb, matching the in-game log.
        world.PlaceCharacter(ch, new Point3D(1460, 1652, 10, 0));

        var sb = new StringBuilder();
        sb.AppendLine("=== Climb trace: North from 1460,1652,z10 (real mortechUO data) ===");

        // Walk North repeatedly, committing each accepted step exactly like the
        // game does, so the Z chain accumulates as in-game.
        for (int step = 0; step < 8; step++)
        {
            var loc = ch.Position;
            bool ok = walk.CheckMovementDetailed(ch, loc, Direction.North, out int newZ, out var d);
            sb.AppendLine(
                $"step {step}: from {loc.X},{loc.Y},{loc.Z} North -> ok={ok} newZ={(ok ? newZ : -999)} " +
                $"| startZ={d.StartZ} startTop={d.StartTop} fwdOk={d.ForwardOk} fwdNewZ={d.ForwardNewZ} " +
                $"reason={d.FwdReason} fwdLand=({d.FwdLandZ}/{d.FwdLandCenter}/{d.FwdLandTop}) " +
                $"landTile=0x{d.FwdLandTileId:X} statics={d.FwdStaticTotal} imp={d.FwdImpassableCount} " +
                $"surf={d.FwdSurfaceCount} tiles=[{d.FwdStaticDump}]");

            if (!ok)
            {
                sb.AppendLine("  -> BLOCKED here. Stop.");
                break;
            }
            // commit
            world.MoveCharacter(ch, new Point3D((short)loc.X, (short)(loc.Y - 1), (sbyte)newZ, ch.MapIndex));
        }

        // Also probe the exact reported block tile directly: player standing at
        // 1460,1648,z20 trying North into 1460,1647.
        sb.AppendLine();
        sb.AppendLine("=== Direct probe: stand 1460,1648,z20, step North into 1460,1647 ===");
        world.MoveCharacter(ch, new Point3D(1460, 1648, 20, ch.MapIndex));
        bool ok2 = walk.CheckMovementDetailed(ch, ch.Position, Direction.North, out int nz2, out var d2);
        sb.AppendLine(
            $"ok={ok2} newZ={(ok2 ? nz2 : -999)} startZ={d2.StartZ} startTop={d2.StartTop} " +
            $"reason={d2.FwdReason} fwdLand=({d2.FwdLandZ}/{d2.FwdLandCenter}/{d2.FwdLandTop}) " +
            $"landTile=0x{d2.FwdLandTileId:X} statics={d2.FwdStaticTotal} imp={d2.FwdImpassableCount} " +
            $"surf={d2.FwdSurfaceCount} tiles=[{d2.FwdStaticDump}]");

        // Probe whether the upper platform (pavers z40) is reachable as a climb
        // from z20 — i.e. does the SERVER ever let the player onto z40 here?
        sb.AppendLine();
        sb.AppendLine("=== Probe: can server reach pavers z40? step North from 1460,1648 at various Z ===");
        foreach (int testZ in new[] { 20, 25, 30, 35, 40 })
        {
            world.MoveCharacter(ch, new Point3D(1460, 1648, (sbyte)testZ, ch.MapIndex));
            bool okz = walk.CheckMovementDetailed(ch, ch.Position, Direction.North, out int nzz, out var dz);
            sb.AppendLine($"  fromZ={testZ}: ok={okz} newZ={(okz ? nzz : -999)} reason={dz.FwdReason}");
        }

        string outPath = TraceOutputPath("walkcheck_trace.txt");
        File.WriteAllText(outPath, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(written to {outPath})");
    }

    [Fact]
    public void CompareMap0_vs_Map0x_Terrain_AroundBuilding()
    {
        string? MulDir = FindMulDir();
        if (MulDir == null)
        {
            _out.WriteLine("SKIP: no MUL data found");
            return;
        }
        string nonX = Path.Combine(MulDir, "map0LegacyMUL.uop");
        string xVar = Path.Combine(MulDir, "map0xLegacyMUL.uop");
        if (!File.Exists(nonX) || !File.Exists(xVar))
        {
            _out.WriteLine("SKIP: UOP map files not found");
            return;
        }

        // Server FindUopMap prefers the 'x' variant; ClassicUO (default) reads the
        // non-'x'. If their terrain Z differs at the climb tiles, that IS the
        // client/server disagreement that snaps the player.
        using var map0 = new SphereNet.MapData.Map.UopMapReader(nonX, 7168, 4096);   // client
        using var map0x = new SphereNet.MapData.Map.UopMapReader(xVar, 7168, 4096);  // server

        var sb = new StringBuilder();
        sb.AppendLine("=== Terrain compare: map0 (client) vs map0x (server) around 1455..1465, 1644..1655 ===");
        sb.AppendLine("    marking tiles where Z or TileId DIFFER with  <<< DIFF");
        int diffs = 0;
        for (int y = 1644; y <= 1655; y++)
        {
            for (int x = 1455; x <= 1465; x++)
            {
                var c0 = map0.GetCell(x, y);
                var cx = map0x.GetCell(x, y);
                bool diff = c0.Z != cx.Z || c0.TileId != cx.TileId;
                if (diff)
                {
                    diffs++;
                    sb.AppendLine($"  {x},{y}: map0 tile=0x{c0.TileId:X} z={c0.Z}  |  map0x tile=0x{cx.TileId:X} z={cx.Z}   <<< DIFF");
                }
            }
        }
        sb.AppendLine($"total diffs in region: {diffs}");
        sb.AppendLine();
        sb.AppendLine("=== Focused: the climb column x=1460, y=1647..1652 ===");
        for (int y = 1652; y >= 1647; y--)
        {
            var c0 = map0.GetCell(1460, y);
            var cx = map0x.GetCell(1460, y);
            string flag = (c0.Z != cx.Z || c0.TileId != cx.TileId) ? "  <<< DIFF" : "";
            sb.AppendLine($"  1460,{y}: map0 tile=0x{c0.TileId:X} z={c0.Z}  |  map0x tile=0x{cx.TileId:X} z={cx.Z}{flag}");
        }

        string outPath = TraceOutputPath("map_compare.txt");
        File.WriteAllText(outPath, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(written to {outPath})");
    }

    // ---- Faithful port of ClassicUO Pathfinder.CalculateNewZ ----------------
    // Mirrors src/ClassicUO.Client/Game/Pathfinder.cs (CreateItemList,
    // CalculateMinMaxZ, CalculateNewZ) against our MapDataManager, so we can run
    // the CLIENT's exact decision on real data next to the server's WalkCheck.
    private const int BLOCK = 16;            // Constants.DEFAULT_BLOCK_HEIGHT
    private const uint POF_IMP_OR_SURF = 1;  // POF_IMPASSABLE_OR_SURFACE
    private const uint POF_SURFACE = 2;
    private const uint POF_BRIDGE = 4;

    private readonly record struct PObj(uint Flags, int Z, int AvgZ, int Height);

    private static void CuoItemList(MapDataManager md, int mapId, int x, int y, List<PObj> list)
    {
        // Land
        var land = md.GetTerrainTile(mapId, x, y);
        ushort g = land.TileId;
        bool ignored = MapDataManager.IsLandIgnored(g);
        if (!ignored)
        {
            var ld = md.GetLandTileData(g);
            uint flags = POF_IMP_OR_SURF;
            if (!ld.IsImpassable) flags |= POF_SURFACE | POF_BRIDGE;
            md.GetAverageZ(mapId, x, y, out int low, out int avg, out int _);
            list.Add(new PObj(flags, low, avg, avg - low));
        }
        // Statics
        md.ForEachStatic(mapId, x, y, s =>
        {
            var id = md.GetItemTileData(s.TileId);
            uint flags = 0;
            if (id.IsImpassable || id.IsSurface) flags |= POF_IMP_OR_SURF;
            if (!id.IsImpassable)
            {
                if (id.IsSurface) flags |= POF_SURFACE;
                if (id.IsBridge) flags |= POF_BRIDGE;
            }
            if (flags == 0) return;
            int h = id.Height;
            int avgZ = h;
            if (id.IsBridge) avgZ /= 2;
            list.Add(new PObj(flags, s.Z, avgZ + s.Z, h));
        });
    }

    private static void CuoMinMax(MapDataManager md, int mapId, int srcX, int srcY,
        int currentZ, int newDirection, out int minZ, out int maxZ)
    {
        minZ = -128;
        maxZ = currentZ;
        var list = new List<PObj>();
        CuoItemList(md, mapId, srcX, srcY, list);
        // distinguish stretched land for the directional avg
        var land = md.GetTerrainTile(mapId, srcX, srcY);
        md.GetAverageZ(mapId, srcX, srcY, out int lLow, out int lAvg, out int lTop);
        bool landStretched = !MapDataManager.IsLandIgnored(land.TileId) && (lLow != lTop);

        foreach (var obj in list)
        {
            int averageZ = obj.AvgZ;
            // The land entry is the first one we added (if present). Detect it by
            // matching its avg to the land avg and being a surface+bridge land.
            bool isLandStretchedEntry = landStretched && obj.AvgZ == lAvg && obj.Z == lLow
                                        && (obj.Flags & POF_SURFACE) != 0 && (obj.Flags & POF_BRIDGE) != 0;
            if (averageZ <= currentZ && isLandStretchedEntry)
            {
                int avgZ = md.GetDirectionalLandZ(mapId, srcX, srcY, newDirection);
                if (minZ < avgZ) minZ = avgZ;
                if (maxZ < avgZ) maxZ = avgZ;
            }
            else
            {
                if ((obj.Flags & POF_IMP_OR_SURF) != 0 && averageZ <= currentZ && minZ < averageZ)
                    minZ = averageZ;
                if ((obj.Flags & POF_BRIDGE) != 0 && currentZ == averageZ)
                {
                    int z = obj.Z;
                    int height = z + obj.Height;
                    if (maxZ < height) maxZ = height;
                    if (minZ > z) minZ = z;
                }
            }
        }
        maxZ += 2;
    }

    private static bool CuoCalculateNewZ(MapDataManager md, int mapId, int x, int y,
        ref int z, int direction)
    {
        // source = target offset back by (direction ^ 4)
        int back = (direction ^ 4) & 7;
        WalkCheck.Offset((Direction)back, ref x, ref y); // x,y now = SOURCE
        int srcX = x, srcY = y;
        // restore target
        WalkCheck.Offset((Direction)(direction & 7), ref x, ref y); // back to TARGET

        CuoMinMax(md, mapId, srcX, srcY, z, direction, out int minZ, out int maxZ);

        var list = new List<PObj>();
        CuoItemList(md, mapId, x, y, list);
        if (list.Count == 0) return false;

        list.Sort((a, b) => a.Z.CompareTo(b.Z));
        list.Add(new PObj(POF_IMP_OR_SURF, 128, 128, 128));

        int resultZ = -128;
        if (z < minZ) z = minZ;
        int currentTempObjZ = 1_000_000;
        int currentZ = -128;

        for (int i = 0; i < list.Count; i++)
        {
            var obj = list[i];
            if ((obj.Flags & POF_IMP_OR_SURF) != 0)
            {
                int objZ = obj.Z;
                if (objZ - minZ >= BLOCK)
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        var t = list[j];
                        if ((t.Flags & (POF_SURFACE | POF_BRIDGE)) != 0)
                        {
                            int tAvg = t.AvgZ;
                            if (tAvg >= currentZ && objZ - tAvg >= BLOCK &&
                                ((tAvg <= maxZ && (t.Flags & POF_SURFACE) != 0) ||
                                 ((t.Flags & POF_BRIDGE) != 0 && t.Z <= maxZ)))
                            {
                                int delta = Math.Abs(z - tAvg);
                                if (delta < currentTempObjZ)
                                {
                                    currentTempObjZ = delta;
                                    resultZ = tAvg;
                                }
                            }
                        }
                    }
                }
                int avgZ2 = obj.AvgZ;
                if (minZ < avgZ2) minZ = avgZ2;
                if (currentZ < avgZ2) currentZ = avgZ2;
            }
        }
        z = resultZ;
        return resultZ != -128;
    }

    [Fact]
    public void Compare_ServerWalkCheck_vs_ClassicUOPort_AtBuilding()
    {
        string? MulDir = FindMulDir();
        if (MulDir == null)
        {
            _out.WriteLine($"SKIP: no MUL data found in {string.Join(", ", MulCandidates)}");
            return;
        }
        var lf = LoggerFactory.Create(b => { });
        var map = new MapDataManager(MulDir);
        map.Load();
        map.InitMap(0, 7168, 4096);
        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        var walk = new WalkCheck(world);
        var ch = world.CreateCharacter();
        ch.Str = 50; ch.MaxStam = 50; ch.Stam = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.IsPlayer = true;

        var sb = new StringBuilder();
        sb.AppendLine("=== SERVER WalkCheck vs ClassicUO port — North at each climb tile ===");
        // (sourceX, sourceY, sourceZ) stepping North into (x, y-1)
        (int x, int y, int z)[] steps =
        {
            (1460, 1652, 10),
            (1460, 1651, 10),
            (1460, 1650, 15),
            (1460, 1649, 20),
            (1460, 1648, 20),   // the throw tile: North into the building wall
        };
        foreach (var s in steps)
        {
            world.MoveCharacter(ch, new Point3D((short)s.x, (short)s.y, (sbyte)s.z, ch.MapIndex));
            bool srvOk = walk.CheckMovementDetailed(ch, ch.Position, Direction.North, out int srvZ, out _);

            int cz = s.z;
            bool cliOk = CuoCalculateNewZ(map, 0, s.x, s.y - 1, ref cz, (int)Direction.North);

            string mark = (srvOk != cliOk) ? "   <<< DISAGREE" : (srvOk && srvZ != cz ? "   <<< Z-DIFF" : "");
            sb.AppendLine($"  from {s.x},{s.y},z{s.z} North -> SERVER ok={srvOk} z={(srvOk ? srvZ : -999)} | CLASSICUO ok={cliOk} z={(cliOk ? cz : -999)}{mark}");
            // Dump the forward tile's LAND flags — the suspected divergence is the
            // LandBlocks rule (server treats dry-impassable land as walkable;
            // ClassicUO does not add Surface/Bridge to impassable land).
            var ft = map.GetTerrainTile(0, s.x, s.y - 1);
            var fl = map.GetLandTileData(ft.TileId);
            map.GetAverageZ(0, s.x, s.y - 1, out int flo, out int favg, out int ftop);
            sb.AppendLine($"        fwdLandTile=0x{ft.TileId:X} z={ft.Z} impassable={fl.IsImpassable} wet={fl.IsWet} texId={fl.TextureId} avgZ=({flo}/{favg}/{ftop})");
        }

        string outPath = TraceOutputPath("parity_compare.txt");
        File.WriteAllText(outPath, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(written to {outPath})");
    }
}
