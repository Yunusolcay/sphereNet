using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Scripting;

namespace SphereNet.Game.Movement;

/// <summary>
/// Movement engine. Maps to CClient::Event_Walk and CChar::CanMoveWalkTo in Source-X.
/// Validates movement requests, checks collision, stamina, and speed.
/// </summary>
public sealed class MovementEngine
{
    private readonly World.GameWorld _world;
    private readonly TriggerDispatcher? _triggerDispatcher;
    private readonly WalkCheck _walkCheck;

    /// <summary>Optional SpellEngine for interrupting casts on movement.</summary>
    public SpellEngine? SpellEngine { get; set; }

    /// <summary>Source-X CClient::SysMessage hook used by region enter/leave
    /// announcements. Program.cs wires this so the moving character receives
    /// MSG_REGION_ENTER / MSG_REGION_GUARDED / MSG_REGION_PVPSAFE strings on
    /// the matching client only.</summary>
    public Action<Objects.Characters.Character, string>? OnSysMessage { get; set; }

    private const int WalkDelayFoot = 400;
    private const int WalkDelayMount = 200;
    private const int RunDelayFoot = 200;
    private const int RunDelayMount = 100;

    public MovementEngine(World.GameWorld world, TriggerDispatcher? triggerDispatcher = null)
    {
        _world = world;
        _triggerDispatcher = triggerDispatcher;
        _walkCheck = new WalkCheck(world);
    }

    /// <summary>
    /// Validate and execute a movement request.
    /// Maps to CClient::Event_Walk flow.
    /// Returns true if movement succeeded.
    /// </summary>
    public bool TryMove(Objects.Characters.Character ch, Direction dir, bool running, byte sequence)
    {
        return TryMoveDetailed(ch, dir, running, sequence, out _);
    }

    /// <summary>Same as <see cref="TryMove"/> but returns a
    /// <see cref="WalkCheck.Diagnostic"/> describing which stage of the
    /// movement algorithm accepted/rejected the step. Used by the walk-reject
    /// log.</summary>
    public bool TryMoveDetailed(Objects.Characters.Character ch, Direction dir, bool running,
        byte sequence, out WalkCheck.Diagnostic diag)
    {
        diag = default;
        // IsDead is intentionally NOT a hard reject here. Source-X /
        // OSI ghosts can walk freely (just slower, can't open most doors,
        // can't mount). Treating death as "cannot move" leaves the player
        // stuck in place after dying, which manifests in the death log as
        // "client receives 0x2C death status, draws ghost body, then sends
        // no walk packets". We still block Freeze (paralyze, GM .freeze)
        // and Stone (stone form / petrified) since those are explicit
        // immobility states even on living characters.
        if (ch.IsStatFlag(StatFlag.Freeze) || ch.IsStatFlag(StatFlag.Stone))
            return false;

        var current = new Point3D(ch.X, ch.Y, ch.Z, ch.MapIndex);

        Point3D target;
        GetDirectionDelta(dir, out short dx, out short dy);

        // GM with AllMove, or an uninitialized world (no MapData — unit tests
        // and in-memory fixtures) bypass the full terrain algorithm. Step on
        // pure delta, keeping Z unchanged.
        if ((ch.PrivLevel >= PrivLevel.GM && ch.AllMove) || _world.MapData == null)
        {
            target = new Point3D((short)(ch.X + dx), (short)(ch.Y + dy), ch.Z, ch.MapIndex);
        }
        else
        {
            if (!_walkCheck.CheckMovementDetailed(ch, current, dir, out int newZ, out diag))
                return false;

            target = new Point3D((short)(ch.X + dx), (short)(ch.Y + dy), (sbyte)newZ, ch.MapIndex);

            // Blocking mobiles at destination — WalkCheck already covers
            // ground-plane blockers; fall back to the existing shove rule for
            // anything it doesn't cover (mounted riders, invisible staff, etc.).
            foreach (var other in _world.GetCharsInRange(target, 0))
            {
                if (other == ch || other.IsDead) continue;
                if (other.X != target.X || other.Y != target.Y) continue;
                if (!CanShove(ch, other))
                    return false;
            }
        }

        ch.Direction = dir;

        // Spell interruption on movement
        SpellEngine?.TryInterruptFromMovement(ch);

        // Move
        _world.MoveCharacter(ch, target);

        // Region/item step effects
        CheckLocationEffects(ch, target);

        return true;
    }

    /// <summary>
    /// Check if a character can walk to the given adjacent position.
    /// Used by pathfinding / AI / teleporters that already know the target
    /// tile. Delegates to the ServUO movement algorithm for consistency with
    /// player walk packets.
    /// </summary>
    public bool CanWalkTo(Objects.Characters.Character ch, Point3D target)
    {
        if (ch.PrivLevel >= PrivLevel.GM && ch.AllMove)
            return true;

        if (target.X < 0 || target.Y < 0)
            return false;

        // Derive direction from delta; non-adjacent tiles are not walkable.
        int dx = target.X - ch.X;
        int dy = target.Y - ch.Y;
        if (dx < -1 || dx > 1 || dy < -1 || dy > 1 || (dx == 0 && dy == 0))
            return false;

        // In-memory / unit-test fixtures without loaded map data — accept any
        // adjacent tile so long as no character blocks it. The ServUO algorithm
        // cannot run without terrain + statics.
        if (_world.MapData == null)
        {
            foreach (var other in _world.GetCharsInRange(target, 0))
            {
                if (other == ch || other.IsDead) continue;
                if (other.X != target.X || other.Y != target.Y) continue;
                if (!CanShove(ch, other)) return false;
            }
            return true;
        }

        Direction d = (dx, dy) switch
        {
            (0, -1) => Direction.North,
            (1, -1) => Direction.NorthEast,
            (1, 0) => Direction.East,
            (1, 1) => Direction.SouthEast,
            (0, 1) => Direction.South,
            (-1, 1) => Direction.SouthWest,
            (-1, 0) => Direction.West,
            (-1, -1) => Direction.NorthWest,
            _ => Direction.North,
        };

        var here = new Point3D(ch.X, ch.Y, ch.Z, ch.MapIndex);
        if (!_walkCheck.CheckMovement(ch, here, d, out _))
            return false;

        foreach (var other in _world.GetCharsInRange(target, 0))
        {
            if (other == ch || other.IsDead) continue;
            if (other.X != target.X || other.Y != target.Y) continue;
            if (!CanShove(ch, other))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if one character can push past another.
    /// </summary>
    private static bool CanShove(Objects.Characters.Character mover, Objects.Characters.Character blocker)
    {
        // Staff characters only shove when .allmove is on (consistent with wall bypass).
        if (mover.PrivLevel >= PrivLevel.GM && mover.AllMove) return true;
        if (blocker.IsStatFlag(StatFlag.Invisible)) return true;

        // NPCs can be shoved by players
        if (!blocker.IsPlayer) return true;

        // In war mode, characters block
        if (blocker.IsInWarMode) return false;

        return true;
    }

    /// <summary>Check step effects (traps, fields, region enter/leave).</summary>
    private void CheckLocationEffects(Objects.Characters.Character ch, Point3D pos)
    {
        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            // Fire @Step on every item at this location (Source-X style)
            _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                new TriggerArgs { CharSrc = ch, ItemSrc = item });

            switch (item.ItemType)
            {
                case ItemType.Trap:
                case ItemType.TrapActive:
                    int trapDamage = 5 + new Random().Next(15);
                    ch.Hits -= (short)Math.Min(trapDamage, ch.Hits);
                    if (ch.Hits <= 0) ch.Kill();
                    break;
                case ItemType.Telepad:
                case ItemType.Moongate:
                    if (item.TryGetTag("LINK_X", out string? lx) &&
                        item.TryGetTag("LINK_Y", out string? ly))
                    {
                        short.TryParse(lx, out short tx);
                        short.TryParse(ly, out short ty);
                        sbyte tz = 0;
                        if (item.TryGetTag("LINK_Z", out string? lz))
                            sbyte.TryParse(lz, out tz);
                        byte mapId = ch.MapIndex;
                        if (item.TryGetTag("LINK_MAP", out string? lm) && byte.TryParse(lm, out byte tm))
                            mapId = tm;
                        _world.MoveCharacter(ch, new Point3D(tx, ty, tz, mapId));
                    }
                    break;
            }

            // Field damage (fire field, poison field, etc.)
            if (item.TryGetTag("FIELD_DAMAGE", out string? fdStr) && int.TryParse(fdStr, out int fieldDmg))
            {
                ch.Hits -= (short)Math.Min(fieldDmg, ch.Hits);
                if (ch.Hits <= 0) ch.Kill();
            }
        }

        // Region enter/leave detection
        var newRegion = _world.FindRegion(pos);
        string? prevRegionName = null;
        ch.TryGetTag("CURRENT_REGION", out prevRegionName);
        string newRegionName = newRegion?.Name ?? "";

        if (prevRegionName != newRegionName)
        {
            // Exit old region — fire region's own EVENTS @Exit
            if (!string.IsNullOrEmpty(prevRegionName))
            {
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.RegionLeave,
                    new TriggerArgs { S1 = prevRegionName });

                // Fire old region's EVENTS
                ch.TryGetTag("CURRENT_REGION_UID", out string? prevRegionUidStr);
                if (!string.IsNullOrEmpty(prevRegionUidStr) && uint.TryParse(prevRegionUidStr, out uint oldRegionUid))
                {
                    var oldRegion = _world.FindRegionByUid(oldRegionUid);
                    if (oldRegion != null && _triggerDispatcher != null)
                    {
                        _triggerDispatcher.FireRegionEvents(oldRegion, "Exit", ch,
                            new TriggerArgs { CharSrc = ch, S1 = oldRegion.Name });
                    }
                }

                // Optional global hook — silently skip if not defined in scripts.
                _triggerDispatcher?.Runner?.TryRunFunction(
                    "f_onchar_regionleave",
                    ch,
                    null,
                    new SphereNet.Scripting.Execution.TriggerArgs(ch, 0, 0, prevRegionName),
                    out _);
            }

            // Enter new region — fire region's own EVENTS @Enter
            if (!string.IsNullOrEmpty(newRegionName))
            {
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.RegionEnter,
                    new TriggerArgs { S1 = newRegionName });

                if (newRegion != null && _triggerDispatcher != null)
                {
                    _triggerDispatcher.FireRegionEvents(newRegion, "Enter", ch,
                        new TriggerArgs { CharSrc = ch, S1 = newRegion.Name });
                }

                // Source-X CCharBase::Region_Notify: announce region entry
                // and any special flags (guards/PvP) to the moving client.
                if (newRegion != null && OnSysMessage != null && ch.IsPlayer)
                {
                    OnSysMessage(ch, SphereNet.Game.Messages.ServerMessages.GetFormatted(
                        SphereNet.Game.Messages.Msg.MsgRegionEnter, newRegion.Name));
                    if (newRegion.IsFlag(RegionFlag.Guarded))
                    {
                        OnSysMessage(ch, SphereNet.Game.Messages.ServerMessages.Get(
                            SphereNet.Game.Messages.Msg.MsgRegionGuards1));
                    }
                    if (newRegion.IsFlag(RegionFlag.NoPvP))
                    {
                        OnSysMessage(ch, SphereNet.Game.Messages.ServerMessages.Get(
                            SphereNet.Game.Messages.Msg.MsgRegionPvpsafe));
                    }
                }

                // Optional global hook — silently skip if not defined in scripts.
                _triggerDispatcher?.Runner?.TryRunFunction(
                    "f_onchar_regionenter",
                    ch,
                    null,
                    new SphereNet.Scripting.Execution.TriggerArgs(ch, 0, 0, newRegionName),
                    out _);
            }
            ch.SetTag("CURRENT_REGION", newRegionName);
            ch.SetTag("CURRENT_REGION_UID", newRegion?.Uid.ToString() ?? "");
        }
        else if (newRegion != null && _triggerDispatcher != null)
        {
            // Step within same region — fire @Step
            _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RegionStep,
                new TriggerArgs { S1 = newRegion.Name });
            _triggerDispatcher.FireRegionEvents(newRegion, "Step", ch,
                new TriggerArgs { CharSrc = ch, S1 = newRegion.Name });
        }

        // Room enter/leave/step detection
        var newRoom = _world.FindRoom(pos);
        ch.TryGetTag("CURRENT_ROOM", out string? prevRoomUid);
        string newRoomUid = newRoom?.Uid.ToString() ?? "";

        if (prevRoomUid != newRoomUid)
        {
            // Exit old room
            if (!string.IsNullOrEmpty(prevRoomUid) && uint.TryParse(prevRoomUid, out uint oldRoomId))
            {
                var oldRoom = _world.FindRoomByUid(oldRoomId);
                if (oldRoom != null && _triggerDispatcher != null)
                {
                    _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RoomLeave,
                        new TriggerArgs { S1 = oldRoom.Name });
                    _triggerDispatcher.FireRoomEvents(oldRoom, "Exit", ch,
                        new TriggerArgs { CharSrc = ch, S1 = oldRoom.Name });
                }
            }

            // Enter new room
            if (newRoom != null && _triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RoomEnter,
                    new TriggerArgs { S1 = newRoom.Name });
                _triggerDispatcher.FireRoomEvents(newRoom, "Enter", ch,
                    new TriggerArgs { CharSrc = ch, S1 = newRoom.Name });
            }

            ch.SetTag("CURRENT_ROOM", newRoomUid);
        }
        else if (newRoom != null && _triggerDispatcher != null)
        {
            // Step within same room
            _triggerDispatcher.FireCharTrigger(ch, CharTrigger.RoomStep,
                new TriggerArgs { S1 = newRoom.Name });
            _triggerDispatcher.FireRoomEvents(newRoom, "Step", ch,
                new TriggerArgs { CharSrc = ch, S1 = newRoom.Name });
        }
    }

    /// <summary>
    /// Get expected delay between movement steps.
    /// Maps to speed check in Event_Walk / Event_CheckWalkBuffer.
    /// </summary>
    public static int GetMoveDelay(bool mounted, bool running) => (mounted, running) switch
    {
        (true, true) => RunDelayMount,
        (true, false) => WalkDelayMount,
        (false, true) => RunDelayFoot,
        (false, false) => WalkDelayFoot,
    };

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

    private static int GetPackWeight(Objects.Items.Item pack)
    {
        int total = 0;
        foreach (var item in pack.Contents)
            total += Math.Max(1, (int)item.Amount);
        return total;
    }
}
