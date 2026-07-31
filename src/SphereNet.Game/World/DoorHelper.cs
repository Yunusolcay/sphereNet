using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Items;
using SphereNet.MapData;
using SphereNet.MapData.Map;
using SphereNet.MapData.Tiles;

namespace SphereNet.Game.World;

/// <summary>Tiledata-based door detection and map-static door lookup.</summary>
public static class DoorHelper
{
    public static bool IsDoorGraphic(MapDataManager? mapData, ushort graphic)
    {
        if (HasDoorTileFlag(mapData, graphic))
            return true;

        // Open/closed door art is usually ±1; some portcullis pairs use ±2.
        if (graphic > 0 && HasDoorTileFlag(mapData, (ushort)(graphic - 1)))
            return true;
        if (graphic < ushort.MaxValue && HasDoorTileFlag(mapData, (ushort)(graphic + 1)))
            return true;
        if (graphic > 1 && HasDoorTileFlag(mapData, (ushort)(graphic - 2)))
            return true;
        return graphic < ushort.MaxValue - 1 && HasDoorTileFlag(mapData, (ushort)(graphic + 2));
    }

    public static bool IsDoorItem(Item item, MapDataManager? mapData)
    {
        if (item.ItemType is ItemType.Door or ItemType.DoorLocked or ItemType.DoorOpen
            or ItemType.Portculis or ItemType.PortLocked)
            return true;

        if (IsDoorGraphic(mapData, item.BaseId) || IsDoorGraphic(mapData, item.DispIdFull))
            return true;

        var def = DefinitionLoader.GetItemDef(item.BaseId);
        if (def == null)
            return false;

        if (def.Type is ItemType.Door or ItemType.DoorLocked or ItemType.DoorOpen
            or ItemType.Portculis or ItemType.PortLocked)
            return true;

        return (def.TFlags & (ulong)TileFlag.Door) != 0;
    }

    // Source-X CItemBase::IsID_Door base table (uofiles_enums_itemid.h).
    // DECLARATION order matters: the lookup takes the FIRST base whose
    // 16-slot window contains the id, reproducing the reference's overlap
    // quirks (e.g. the 4-apart elven door sets).
    private static readonly ushort[] DoorBases =
    [
        0x00E8, 0x0314, 0x0324, 0x0334, 0x0344, 0x0354, // secret 1-6
        0x0675, 0x0685, 0x0695, 0x06A5, 0x06B5, 0x06C5, 0x06D5, 0x06E5, // metal_s..wooden_4
        0x0824, 0x0839, 0x084C, 0x0866, // iron/wooden gates
        0x1FED,                         // bar metal
        0x2420,                         // wood black
        0x31AC, 0x2D63, 0x2D67, 0x2D6F, // elven bark/simple/ornate/plain
        0x2FE4, 0x367B, 0x368B,         // moon, crystal, shadow
        0x409B, 0x410C, 0x41C2, 0x41CF, // gargish green/brown, sun, gargish grey
        0x46DD, 0x4D1A, 0x50C8, 0x5142, // ruined, gargish blue/red/prison
        0x9AD7, 0x9B3C,                 // jungle, shadowguard
    ];

    /// <summary>Source-X <c>CItemBase::IsID_Door − 1</c>: the 0-15 slot of the
    /// id within its classic door set (even = closed art, odd = open art), or
    /// −1 for ids outside every known set. 0x190E/0x190F is the two-piece bar
    /// door anomaly.</summary>
    public static int GetDoorDir(ushort id)
    {
        if (id == 0x190E) return 0;
        if (id == 0x190F) return 1;
        foreach (ushort b in DoorBases)
        {
            if (id < b) continue;
            int did = id - b;
            if (did <= 15) return did;
        }
        return -1;
    }

    /// <summary>Source-X <c>CItem::Use_Door</c> hinge shift applied when the
    /// door LEAVES the given slot (even slot = opening shift, odd = the exact
    /// negation for closing). Without the move the open art renders hinged at
    /// the wrong tile — the door looks like it swings backwards.</summary>
    public static (short Dx, short Dy) GetDoorShift(int doorDir) => doorDir switch
    {
        0 => (-1, 1),
        1 => (1, -1),
        2 => (1, 1),
        3 => (-1, -1),
        4 => (-1, 0),
        5 => (1, 0),
        6 => (1, -1),
        7 => (-1, 1),
        8 => (1, 1),
        9 => (-1, -1),
        10 => (1, -1),
        11 => (-1, 1),
        14 => (0, -1),
        15 => (0, 1),
        _ => (0, 0), // 12/13 (no shift) and non-classic doors
    };

    /// <summary>
    /// Flip a closed, unlocked door item to its open art and mark it with the
    /// DOOR_OPEN tag (same state transition as the player double-click path,
    /// without any network side effects — the caller broadcasts). Classic-set
    /// doors also shift by the Source-X hinge offset. Returns true when the
    /// door is open after the call (already-open is success), false for
    /// locked doors unless <paramref name="ignoreLock"/> is requested by the
    /// privileged Source-X USEDOOR verb.
    /// </summary>
    public static bool TryOpenDoorState(Item door, bool ignoreLock = false)
    {
        if (!ignoreLock && door.ItemType is (ItemType.DoorLocked or ItemType.PortLocked))
            return false;

        int doorDir = GetDoorDir(door.DispIdFull);
        bool isOpen = doorDir >= 0
            ? (doorDir & 1) != 0 // the graphic is the state for classic sets
            : door.TryGetTag("DOOR_OPEN", out string? openStr) && openStr == "1";
        if (isOpen)
            return true;

        bool isPortcullis = door.ItemType is ItemType.Portculis;
        int offset = isPortcullis ? 2 : 1;
        ushort newDisplayId = (ushort)(door.DispIdFull + offset);
        if (door.DispIdOverride != 0)
            door.TrySetProperty("DISPID", $"0{newDisplayId:X}");
        else
            door.BaseId = newDisplayId;
        door.SetTag("DOOR_OPEN", "1");
        MoveDoorLeaf(door, doorDir);
        // Source-X _SetTimeoutS(20): the door swings shut on its own.
        door.SetTimeout(Environment.TickCount64 + 20_000);
        return true;
    }

    /// <summary>Apply the hinge shift for a door leaving <paramref name="doorDir"/>.
    /// Uses the ambient world for proper sector bookkeeping when available.</summary>
    public static void MoveDoorLeaf(Item door, int doorDir)
    {
        if (doorDir < 0) return;
        var (dx, dy) = GetDoorShift(doorDir);
        if (dx == 0 && dy == 0) return;
        var pos = new Core.Types.Point3D(
            (short)(door.X + dx), (short)(door.Y + dy), door.Z, door.MapIndex);
        var world = Objects.ObjBase.ResolveWorld?.Invoke();
        if (world == null)
        {
            // No ambient world (unit tests): position only, there is no sector
            // index to keep in step.
            door.Position = pos;
            return;
        }
        // PlaceItem refuses an out-of-bounds tile. Writing Position anyway would
        // leave the leaf registered in its OLD sector while reporting the new
        // coordinates, and the view scan walks sectors — the door would then be
        // found or missed depending on which sectors the viewer happens to
        // cover, and reappear only after a resync. Leave it where it is.
        world.PlaceItem(door, pos);
    }

    public static bool FindNearestStaticDoor(
        MapDataManager? mapData,
        byte mapId,
        short centerX,
        short centerY,
        int range,
        out short doorX,
        out short doorY,
        out sbyte doorZ,
        out ushort tileId,
        out ushort hue)
    {
        doorX = 0;
        doorY = 0;
        doorZ = 0;
        tileId = 0;
        hue = 0;

        if (mapData == null || range < 0)
            return false;

        int bestDist = int.MaxValue;
        bool found = false;

        for (int dx = -range; dx <= range; dx++)
        {
            for (int dy = -range; dy <= range; dy++)
            {
                short x = (short)(centerX + dx);
                short y = (short)(centerY + dy);
                foreach (var s in mapData.GetStatics(mapId, x, y))
                {
                    if (!IsDoorGraphic(mapData, s.TileId))
                        continue;

                    int dist = Math.Abs(dx) + Math.Abs(dy);
                    if (dist >= bestDist)
                        continue;

                    bestDist = dist;
                    doorX = x;
                    doorY = y;
                    doorZ = s.Z;
                    tileId = s.TileId;
                    hue = s.Hue;
                    found = true;
                }
            }
        }

        return found;
    }

    private static bool HasDoorTileFlag(MapDataManager? mapData, ushort graphic)
    {
        if (mapData == null)
            return false;
        var data = mapData.GetItemTileData(graphic);
        return (data.Flags & TileFlag.Door) != 0;
    }
}
