using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Map;
using SphereNet.MapData.Tiles;

namespace SphereNet.Game.Movement;

/// <summary>
/// Per-step walk validator. For a given character and direction, walks the
/// full surface stack at both the start and target tiles — terrain, static
/// tiles, in-world items — and picks the single walkable Z that a 16-tall
/// character can stand on without head-butting anything above. Replaces an
/// earlier single-Z IsPassable + fixed MaxClimbHeight check that did not
/// model stair / slope / bridge / multi-surface cases.
///
/// Algorithm structure is modelled on the open-source reference
/// implementation in ServUO's MovementImpl (with credit to its authors);
/// the C# here is an independent reimplementation against our own map-data
/// readers and game objects.
/// </summary>
public sealed class WalkCheck
{
    private const int PersonHeight = 16;
    private const int StepHeight = 2;

    private const TileFlag ImpassableSurface = TileFlag.Impassable | TileFlag.Surface;

    private readonly GameWorld _world;

    public WalkCheck(GameWorld world)
    {
        _world = world;
    }

    /// <summary>Diagnostic result from <see cref="CheckMovementDetailed"/>,
    /// used by the walk-reject log so rejections can be attributed to the
    /// forward tile, a diagonal edge, or a mobile block instead of a generic
    /// "collision".</summary>
    public readonly record struct Diagnostic(
        int StartZ, int StartTop,
        bool ForwardOk, int ForwardNewZ,
        bool DiagonalChecked, bool LeftOk, bool RightOk,
        bool MobBlocked,
        // Forward tile geometry — populated when the forward check fails so
        // reject logs can distinguish "no surfaces at this tile" from "land
        // was there but IsOk rejected it" from "stair riser too tall".
        int FwdLandZ, int FwdLandCenter, int FwdLandTop,
        bool FwdLandBlocks, bool FwdConsiderLand,
        int FwdSurfaceCount, int FwdItemSurfaceCount,
        string FwdReason,
        // Raw tile inventory for the forward tile — useful when surface
        // counters are 0 to see what IS there (walls, decorative, etc.).
        int FwdStaticTotal, int FwdImpassableCount, ushort FwdLandTileId,
        string FwdStaticDump,
        int FwdMobileCount, string FwdMobileDump);

    /// <summary>Entry point. Returns true if <paramref name="mover"/> can step
    /// in direction <paramref name="d"/> from <paramref name="loc"/>, and sets
    /// <paramref name="newZ"/> to the Z the mover should land on. Matches
    /// ServUO MovementImpl.CheckMovement semantics including diagonal
    /// double-edge check.</summary>
    public bool CheckMovement(Character mover, Point3D loc, Direction d, out int newZ)
    {
        return CheckMovementDetailed(mover, loc, d, out newZ, out _);
    }

    /// <summary>Same as <see cref="CheckMovement"/> but also returns a
    /// <see cref="Diagnostic"/> breakdown of which stage accepted/rejected the
    /// step. Intended for walk-reject telemetry.</summary>
    public bool CheckMovementDetailed(Character mover, Point3D loc, Direction d,
        out int newZ, out Diagnostic diag)
    {
        newZ = loc.Z;
        diag = default;
        var md = _world.MapData;
        if (md == null) return false;

        int mapId = mover.MapIndex;
        int xStart = loc.X;
        int yStart = loc.Y;

        int xForward = xStart, yForward = yStart;
        int xRight = xStart, yRight = yStart;
        int xLeft = xStart, yLeft = yStart;

        bool checkDiagonals = ((int)d & 0x1) == 0x1;

        Offset(d, ref xForward, ref yForward);
        Offset((Direction)(((int)d - 1) & 0x7), ref xLeft, ref yLeft);
        Offset((Direction)(((int)d + 1) & 0x7), ref xRight, ref yRight);

        // Bounds — reject walking off the map edge.
        if (xForward < 0 || yForward < 0) return false;

        var itemsStart = CollectItems(mapId, xStart, yStart);
        var itemsForward = CollectItems(mapId, xForward, yForward);
        var itemsLeft = checkDiagonals ? CollectItems(mapId, xLeft, yLeft) : null;
        var itemsRight = checkDiagonals ? CollectItems(mapId, xRight, yRight) : null;

        var mobsForward = CollectMobiles(mapId, xForward, yForward, mover);

        GetStartZ(mover, md, mapId, xStart, yStart, loc.Z, itemsStart, out int startZ, out int startTop);

        // Pre-capture raw tile inventory at the forward tile for diagnostics
        // — total statics, count of impassable ones, and a compact dump of
        // every static's (id,z,h,flags). This lets the reject log show what
        // is actually on the tile beyond the Surface-only candidate count.
        var fwdStatics = md.GetStatics(mapId, xForward, yForward);
        int fwdImpassable = 0;
        var dump = new System.Text.StringBuilder();
        foreach (var s in fwdStatics)
        {
            var sd = md.GetItemTileData(s.TileId);
            if (sd.IsImpassable) fwdImpassable++;
            if (dump.Length > 0) dump.Append(',');
            // Flag değerini ve ilk 12 karakter adı da yaz — böylece tile'ın
            // gerçekten ne olduğunu (dekoratif mi, yüzey mi, yanlış flag'li
            // mi) ayırt edebiliriz.
            string nm = sd.Name ?? "";
            if (nm.Length > 12) nm = nm.Substring(0, 12);
            dump.Append($"0x{s.TileId:X}@{s.Z}h{sd.Height}f0x{(ulong)sd.Flags:X}'{nm}'");
        }
        var fwdLandTile = md.GetTerrainTile(mapId, xForward, yForward);
        var mobDump = new System.Text.StringBuilder();
        foreach (var mob in mobsForward)
        {
            if (mobDump.Length > 0) mobDump.Append(',');
            string nm = mob.Name ?? "";
            if (nm.Length > 12) nm = nm.Substring(0, 12);
            mobDump.Append($"0x{mob.Uid.Value:X} z={mob.Z} dead={mob.IsDead} player={mob.IsPlayer} war={mob.IsInWarMode} '{nm}'");
        }

        var fwdTrace = new CheckTrace();
        bool forwardOk = Check(mover, md, mapId, xForward, yForward, startTop, startZ, loc.Z,
            itemsForward, mobsForward, out newZ, ref fwdTrace);
        int forwardNewZ = newZ;
        bool moveOk = forwardOk;
        bool mobBlocked = false;

        // If the forward tile alone passed the surface check but a mob
        // blocker flipped it to false, we know that was the cause.
        if (!forwardOk && mobsForward.Count > 0)
        {
            var noMobTrace = new CheckTrace();
            bool surfaceWithoutMob = Check(mover, md, mapId, xForward, yForward, startTop, startZ,
                loc.Z, itemsForward, null, out _, ref noMobTrace);
            if (surfaceWithoutMob) mobBlocked = true;
        }

        bool leftOk = false, rightOk = false;
        if (moveOk && checkDiagonals)
        {
            // ServUO rule: players (non-staff) need BOTH diagonal edges clear;
            // mobs / staff need only ONE. We apply the stricter rule to
            // everyone below GM so corners cannot be cut through walls.
            bool bothRequired = mover.PrivLevel < PrivLevel.GM;

            var leftTrace = new CheckTrace();
            var rightTrace = new CheckTrace();
            leftOk = Check(mover, md, mapId, xLeft, yLeft, startTop, startZ, loc.Z,
                itemsLeft!, null, out _, ref leftTrace);
            rightOk = Check(mover, md, mapId, xRight, yRight, startTop, startZ, loc.Z,
                itemsRight!, null, out _, ref rightTrace);

            moveOk = bothRequired ? (leftOk && rightOk) : (leftOk || rightOk);
        }

        if (!moveOk) newZ = startZ;

        diag = new Diagnostic(startZ, startTop, forwardOk, forwardNewZ,
            checkDiagonals, leftOk, rightOk, mobBlocked,
            fwdTrace.LandZ, fwdTrace.LandCenter, fwdTrace.LandTop,
            fwdTrace.LandBlocks, fwdTrace.ConsiderLand,
            fwdTrace.SurfaceCandidates, fwdTrace.ItemSurfaceCandidates,
            fwdTrace.LastReason,
            fwdStatics.Length, fwdImpassable, fwdLandTile.TileId, dump.ToString(),
            mobsForward.Count, mobDump.ToString());
        return moveOk;
    }

    /// <summary>Per-tile trace captured during <see cref="Check"/> so the walk
    /// diagnostic can report why the forward tile rejected every candidate.</summary>
    private struct CheckTrace
    {
        public int LandZ, LandCenter, LandTop;
        public bool LandBlocks, ConsiderLand;
        public int SurfaceCandidates;       // static tiles with Surface flag
        public int ItemSurfaceCandidates;   // in-world items with Surface flag
        public string LastReason;
    }

    // -----------------------------------------------------------------
    //  Per-tile checks (Check / GetStartZ / IsOk) — structural clone of
    //  the ServUO functions. Comments mirror the original intent.
    // -----------------------------------------------------------------

    private bool Check(Character mover, MapDataManager md, int mapId, int x, int y,
        int startTop, int startZ, int moverZ, List<Item> items, List<Character>? mobiles, out int newZ,
        ref CheckTrace trace)
    {
        newZ = 0;

        var statics = md.GetStatics(mapId, x, y);
        var landTile = md.GetTerrainTile(mapId, x, y);
        var landData = md.GetLandTileData(landTile.TileId);

        // Only water (Impassable + Wet) land blocks walking — barriers like
        // deep ocean that need a bridge static above. Land that is Impassable
        // alone is treated as walkable: stock tiledata.mul flags some
        // decorative rock / slope textures as Impassable, but the UO client
        // (and standard sphere maps) use them as walkable sloped terrain. Real
        // walls must be modeled as static items, which are checked properly.
        bool landBlocks = landData.IsImpassable && landData.IsWet;
        bool considerLand = !MapDataManager.IsLandIgnored(landTile.TileId);

        md.GetAverageZ(mapId, x, y, out int landZ, out int landCenter, out int landTop);

        trace.LandZ = landZ;
        trace.LandCenter = landCenter;
        trace.LandTop = landTop;
        trace.LandBlocks = landBlocks;
        trace.ConsiderLand = considerLand;
        trace.LastReason = "no_candidates";

        bool moveIsOk = false;
        int stepTop = startTop + StepHeight;
        int checkTop = startZ + PersonHeight;

        // --- Static tiles ---
        for (int i = 0; i < statics.Length; i++)
        {
            var tile = statics[i];
            var itemData = md.GetItemTileData(tile.TileId);
            TileFlag flags = itemData.Flags;

            // Walkable surface (Surface flag and NOT Impassable).
            if ((flags & ImpassableSurface) == TileFlag.Surface)
            {
                trace.SurfaceCandidates++;

                int itemZ = tile.Z;
                int itemTop = itemZ;
                int ourZ = itemZ + itemData.CalcHeight;

                if (moveIsOk)
                {
                    // Prefer the Z closest to the mover's current Z (ServUO
                    // uses `p.Z`, not startZ — they diverge when the mover
                    // stands on a surface whose Z != the tile's baseline).
                    int cmp = Math.Abs(ourZ - moverZ) - Math.Abs(newZ - moverZ);
                    if (cmp > 0 || (cmp == 0 && ourZ > newZ)) continue;
                }

                int testTop = checkTop;
                if (ourZ + PersonHeight > testTop) testTop = ourZ + PersonHeight;

                if (!itemData.IsBridge) itemTop += itemData.Height;

                if (stepTop >= itemTop)
                {
                    int landCheck = itemZ +
                        (itemData.Height >= StepHeight ? StepHeight : itemData.Height);

                    if (considerLand && landCheck < landCenter && landCenter > ourZ && testTop > landZ)
                    { trace.LastReason = $"static_land_cover id=0x{tile.TileId:X} z={itemZ} ourZ={ourZ}"; continue; }

                    if (IsOk(mover, ourZ, testTop, statics, items, md))
                    {
                        newZ = ourZ;
                        moveIsOk = true;
                        trace.LastReason = $"accepted_static id=0x{tile.TileId:X} ourZ={ourZ}";
                    }
                    else
                    {
                        trace.LastReason = $"static_headroom id=0x{tile.TileId:X} ourZ={ourZ} top={testTop}";
                    }
                }
                else
                {
                    trace.LastReason = $"static_too_tall id=0x{tile.TileId:X} top={itemTop} step={stepTop}";
                }
            }
        }

        // --- In-world items (same treatment as static tiles) ---
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var itemData = md.GetItemTileData(item.BaseId);
            if (!ShouldTreatAsMovementGeometry(item, itemData)) continue;
            TileFlag flags = itemData.Flags;

            if ((flags & ImpassableSurface) == TileFlag.Surface)
            {
                trace.ItemSurfaceCandidates++;

                int itemZ = item.Z;
                int itemTop = itemZ;
                int ourZ = itemZ + itemData.CalcHeight;

                if (moveIsOk)
                {
                    int cmp = Math.Abs(ourZ - moverZ) - Math.Abs(newZ - moverZ);
                    if (cmp > 0 || (cmp == 0 && ourZ > newZ)) continue;
                }

                int testTop = checkTop;
                if (ourZ + PersonHeight > testTop) testTop = ourZ + PersonHeight;

                if (!itemData.IsBridge) itemTop += itemData.Height;

                if (stepTop >= itemTop)
                {
                    int landCheck = itemZ +
                        (itemData.Height >= StepHeight ? StepHeight : itemData.Height);

                    if (considerLand && landCheck < landCenter && landCenter > ourZ && testTop > landZ)
                    { trace.LastReason = $"item_land_cover id=0x{item.BaseId:X} z={itemZ}"; continue; }

                    if (IsOk(mover, ourZ, testTop, statics, items, md))
                    {
                        newZ = ourZ;
                        moveIsOk = true;
                        trace.LastReason = $"accepted_item id=0x{item.BaseId:X} ourZ={ourZ}";
                    }
                    else
                    {
                        trace.LastReason = $"item_headroom id=0x{item.BaseId:X} ourZ={ourZ}";
                    }
                }
                else
                {
                    trace.LastReason = $"item_too_tall id=0x{item.BaseId:X}";
                }
            }
        }

        // --- Land (terrain) candidate ---
        if (considerLand && !landBlocks && stepTop >= landZ)
        {
            int ourZ = landCenter;
            int testTop = checkTop;
            if (ourZ + PersonHeight > testTop) testTop = ourZ + PersonHeight;

            bool shouldCheck = true;
            if (moveIsOk)
            {
                int cmp = Math.Abs(ourZ - moverZ) - Math.Abs(newZ - moverZ);
                if (cmp > 0 || (cmp == 0 && ourZ > newZ)) shouldCheck = false;
            }

            if (shouldCheck && IsOk(mover, ourZ, testTop, statics, items, md))
            {
                newZ = ourZ;
                moveIsOk = true;
                trace.LastReason = $"accepted_land ourZ={ourZ}";
            }
            else if (shouldCheck)
            {
                trace.LastReason = $"land_headroom ourZ={ourZ} top={testTop}";
            }
        }
        else if (!considerLand)
            trace.LastReason = $"land_ignored tile=0x{landTile.TileId:X}";
        else if (landBlocks)
            trace.LastReason = $"land_impassable tile=0x{landTile.TileId:X}";
        else
            trace.LastReason = $"land_too_tall landZ={landZ} stepTop={stepTop}";

        // --- Mobile blocking ---
        if (moveIsOk && mobiles != null)
        {
            for (int i = 0; moveIsOk && i < mobiles.Count; i++)
            {
                var mob = mobiles[i];
                if (mob == mover) continue;
                if ((mob.Z + 15) > newZ && (newZ + 15) > mob.Z && !CanMoveOver(mover, mob))
                    moveIsOk = false;
            }
        }

        return moveIsOk;
    }

    /// <summary>Source tile Z baseline — what does "standing here" mean? Walks
    /// the surface stack at <paramref name="loc"/> and picks the highest
    /// surface that the mover's feet rest on.</summary>
    private void GetStartZ(Character mover, MapDataManager md, int mapId,
        int x, int y, int locZ, List<Item> itemList, out int zLow, out int zTop)
    {
        var landTile = md.GetTerrainTile(mapId, x, y);
        var landData = md.GetLandTileData(landTile.TileId);
        // Match the landBlocks rule in Check() — only water (Impassable+Wet)
        // is treated as a barrier. See the comment in Check for rationale.
        bool landBlocks = landData.IsImpassable && landData.IsWet;
        bool considerLand = !MapDataManager.IsLandIgnored(landTile.TileId);

        md.GetAverageZ(mapId, x, y, out int landZ, out int landCenter, out int landTopAvg);

        int zCenter = 0;
        zLow = 0;
        zTop = 0;
        bool isSet = false;

        if (considerLand && !landBlocks && locZ >= landCenter)
        {
            zLow = landZ;
            zCenter = landCenter;
            if (!isSet || landTopAvg > zTop) zTop = landTopAvg;
            isSet = true;
        }

        var staticTiles = md.GetStatics(mapId, x, y);
        for (int i = 0; i < staticTiles.Length; i++)
        {
            var tile = staticTiles[i];
            var id = md.GetItemTileData(tile.TileId);
            int calcTop = tile.Z + id.CalcHeight;

            if ((!isSet || calcTop >= zCenter) && id.IsSurface && locZ >= calcTop)
            {
                zLow = tile.Z;
                zCenter = calcTop;
                int top = tile.Z + id.Height;
                if (!isSet || top > zTop) zTop = top;
                isSet = true;
            }
        }

        for (int i = 0; i < itemList.Count; i++)
        {
            var item = itemList[i];
            var id = md.GetItemTileData(item.BaseId);
            if (!ShouldTreatAsMovementGeometry(item, id)) continue;
            int calcTop = item.Z + id.CalcHeight;

            if ((!isSet || calcTop >= zCenter) && id.IsSurface && locZ >= calcTop)
            {
                zLow = item.Z;
                zCenter = calcTop;
                int top = item.Z + id.Height;
                if (!isSet || top > zTop) zTop = top;
                isSet = true;
            }
        }

        if (!isSet)
        {
            zLow = zTop = locZ;
        }
        else if (locZ > zTop)
        {
            zTop = locZ;
        }
    }

    /// <summary>"Is there anything above the target Z that our head would
    /// collide with?" — scans impassable/surface tiles whose Z span overlaps
    /// [ourZ, ourTop) and rejects if so. ServUO's IsOk.</summary>
    private bool IsOk(Character mover, int ourZ, int ourTop,
        StaticItem[] tiles, List<Item> items, MapDataManager md)
    {
        for (int i = 0; i < tiles.Length; i++)
        {
            var check = tiles[i];
            var data = md.GetItemTileData(check.TileId);
            if ((data.Flags & ImpassableSurface) != 0)
            {
                int checkZ = check.Z;
                int checkTop = checkZ + data.CalcHeight;
                if (checkTop > ourZ && ourTop > checkZ) return false;
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var data = md.GetItemTileData(item.BaseId);
            if (!ShouldTreatAsMovementGeometry(item, data)) continue;
            if ((data.Flags & ImpassableSurface) != 0)
            {
                int checkZ = item.Z;
                int checkTop = checkZ + data.CalcHeight;
                if (checkTop > ourZ && ourTop > checkZ) return false;
            }
        }

        return true;
    }

    private static bool ShouldTreatAsMovementGeometry(Item item, ItemTileData data)
    {
        // Only treat world items as movement geometry when they are meaningful
        // obstacles/floors. Small loose drops like reagents, weapons, bags,
        // etc. should not become collision just because tiledata carries a
        // Surface bit, while bulky/anchored objects still should.
        if (item.ItemType == ItemType.Corpse) return false;
        if (item.IsStaticBlock) return true;
        if (item.IsAttr(ObjAttributes.Static)
            || item.IsAttr(ObjAttributes.Move_Never)
            || item.IsAttr(ObjAttributes.LockedDown))
            return true;
        if (data.IsBridge) return true;

        // Loose items that are low enough to step over should not affect
        // movement. Keep explicit impassables blocking even when movable.
        if (!data.IsImpassable && data.CalcHeight <= StepHeight)
            return false;

        return true;
    }

    // -----------------------------------------------------------------
    //  Helpers — tile / mobile collection and direction offsets.
    // -----------------------------------------------------------------

    /// <summary>Items placed on the ground at exactly (x, y) on mover's map.
    /// Filters out contained/equipped items since those are not walk-relevant.</summary>
    private List<Item> CollectItems(int mapId, int x, int y)
    {
        var list = new List<Item>();
        var pivot = new Point3D((short)x, (short)y, 0, (byte)mapId);
        foreach (var item in _world.GetItemsInRange(pivot, 0))
        {
            if (item.IsDeleted || item.IsEquipped || !item.IsOnGround) continue;
            if (item.X != x || item.Y != y) continue;
            list.Add(item);
        }
        return list;
    }

    private List<Character> CollectMobiles(int mapId, int x, int y, Character mover)
    {
        var list = new List<Character>();
        var pivot = new Point3D((short)x, (short)y, 0, (byte)mapId);
        foreach (var ch in _world.GetCharsInRange(pivot, 0))
        {
            if (ch == mover || ch.IsDeleted || ch.IsDead) continue;
            if (ch.X != x || ch.Y != y) continue;
            list.Add(ch);
        }
        return list;
    }

    private static bool CanMoveOver(Character mover, Character blocker)
    {
        // ServUO / RunUO-style shove rule:
        // - staff can always move through mobiles
        // - dead bodies / dead movers do not block
        // - hidden staff do not block
        // - players can shove only when at full stamina, spending 10 stam
        if (mover.PrivLevel >= PrivLevel.Counsel) return true;
        if (!mover.IsDead && mover.Stam == mover.MaxStam && mover.MaxStam > 0)
        {
            mover.Stam -= 10;
            if (mover.IsStatFlag(StatFlag.Hidden)) mover.ClearStatFlag(StatFlag.Hidden);
            if (mover.IsStatFlag(StatFlag.Invisible)) mover.ClearStatFlag(StatFlag.Invisible);
            return true;
        }

        if (blocker.IsDead) return true;
        if (mover.IsDead) return true;
        if ((blocker.IsStatFlag(StatFlag.Hidden) || blocker.IsStatFlag(StatFlag.Invisible))
            && blocker.PrivLevel >= PrivLevel.Counsel)
            return true;

        return false;
    }

    public static void Offset(Direction d, ref int x, ref int y)
    {
        switch (d)
        {
            case Direction.North: y--; break;
            case Direction.South: y++; break;
            case Direction.West: x--; break;
            case Direction.East: x++; break;
            case Direction.NorthEast: x++; y--; break;
            case Direction.SouthWest: x--; y++; break;
            case Direction.SouthEast: x++; y++; break;
            case Direction.NorthWest: x--; y--; break;
        }
    }
}
