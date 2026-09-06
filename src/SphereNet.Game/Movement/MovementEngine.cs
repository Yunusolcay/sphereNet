using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;

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

    /// <summary>Fired when a character is teleported (telepad/moongate step-on).
    /// Program.cs wires this to send DrawPlayer + resync to the client.</summary>
    public Action<Objects.Characters.Character, Point3D, byte>? OnTeleport { get; set; }

    /// <summary>Source-X CClient::SysMessage hook used by region enter/leave
    /// announcements. Program.cs wires this so the moving character receives
    /// MSG_REGION_ENTER / MSG_REGION_GUARDED / MSG_REGION_PVPSAFE strings on
    /// the matching client only.</summary>
    public Action<Objects.Characters.Character, string>? OnSysMessage { get; set; }

    /// <summary>Optional housing ban check. Returns false if character cannot enter the tile.</summary>
    public Func<Objects.Characters.Character, Point3D, bool>? CanEnterHouse { get; set; }

    /// <summary>Optional ship-boarding ban check (enforced through the ship region).
    /// Returns false if the character is barred from the ship occupying the tile.</summary>
    public Func<Objects.Characters.Character, Point3D, bool>? CanBoardShip { get; set; }

    public static int WalkDelayFoot { get; set; } = 400;
    public static int WalkDelayMount { get; set; } = 200;
    public static int RunDelayFoot { get; set; } = 200;
    public static int RunDelayMount { get; set; } = 100;

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

        // Overweight running prevention — can't run when carrying more than max weight
        if (running && ch.IsPlayer && ch.GetTotalWeight() > ch.MaxWeight)
            running = false;

        var current = new Point3D(ch.X, ch.Y, ch.Z, ch.MapIndex);

        Point3D target;
        bool shoved = false;
        GetDirectionDelta(dir, out short dx, out short dy);

        // GM with AllMove, or an uninitialized world (no MapData — unit tests
        // and in-memory fixtures) bypass the full terrain algorithm: nothing
        // can block the step. The Z still follows the ground — freezing it let
        // a GM cross a dungeon at a stale height (the client computes its own
        // walk Z, so the drift stayed invisible until a self-redraw snapped
        // the char upward: the 5146,993 report, stored Z=10 over a floor at 1).
        if ((ch.PrivLevel >= PrivLevel.GM && ch.AllMove) || CharDefHelper.CanPassWalls(ch) || _world.MapData == null)
        {
            // The bypass skips collision ONLY — surface collection and Z
            // selection still run through the shared resolver (audit design:
            // GetEffectiveZ's closest-to-currentZ pick was self-reinforcing
            // for a drifted Z and saw neither multis nor headroom).
            var stand = _walkCheck.ResolveStandingSurface(ch, ch.MapIndex,
                ch.X + dx, ch.Y + dy, ch.Z, WalkCheck.StandingPolicy.IgnoreCollision);
            sbyte bypassZ = stand.Found ? stand.Z : ch.Z;
            target = new Point3D((short)(ch.X + dx), (short)(ch.Y + dy), bypassZ, ch.MapIndex);
            // Even the bypass branch may not step off the map — an off-map
            // char crashes the map readers on the next query.
            if (_world.GetSector(target) == null)
                return false;
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
                {
                    diag = diag with { MobBlocked = true };
                    return false;
                }
                // A living blocker we pushed past = a real shove.
                shoved = true;
                // @PersonalSpace (Source-X) — the mover stepped into another's tile.
                Character.OnPersonalSpace?.Invoke(ch, other);
            }
        }

        if (CanEnterHouse != null && !CanEnterHouse(ch, target))
        {
            diag = diag with { MobBlocked = true };
            return false;
        }

        if (CanBoardShip != null && !CanBoardShip(ch, target))
        {
            diag = diag with { MobBlocked = true };
            return false;
        }

        ch.Direction = dir;

        // Spell interruption on movement
        SpellEngine?.TryInterruptFromMovement(ch);
        ch.InterruptMeditation();

        if (ch.HasActiveSkillPending() &&
            SkillEngine.HasFlag((SkillType)ch.SkillPendingId, SkillFlag.Immobile))
        {
            int skillId = ch.ClearActiveSkillPending();
            if (skillId >= 0)
                Character.ActiveSkillAborted?.Invoke(ch, skillId);
        }

        // Move
        _world.MoveCharacter(ch, target);

        // Shove cost — applied once here (not in the two shove predicates) so a
        // player who pushes past a mobile spends 10 stamina and is revealed,
        // exactly once, only when the move actually commits.
        if (shoved && ch.PrivLevel < PrivLevel.Counsel && ch.MaxStam > 0)
        {
            ch.Stam = (short)Math.Max(0, ch.Stam - 10);
            if (ch.IsStatFlag(StatFlag.Hidden)) ch.ClearStatFlag(StatFlag.Hidden);
            if (ch.IsStatFlag(StatFlag.Invisible)) ch.ClearStatFlag(StatFlag.Invisible);
        }

        // Note: walking/running on foot does NOT drain stamina (Source-X
        // CClient::Event_Walk has no per-step stamina cost — only shoving past a
        // mobile, handled above, costs stamina). Mounted travel drains the
        // mount, not the rider, and is handled separately.

        TickStealthStep(ch);

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
        if (CharDefHelper.CanPassWalls(ch))
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
        // ServUO / RunUO Mobile.CheckShove parity.
        if (mover.PrivLevel >= PrivLevel.Counsel) return true;

        if (blocker.IsDead || mover.IsDead)
            return true;

        if ((blocker.IsStatFlag(StatFlag.Hidden) || blocker.IsStatFlag(StatFlag.Invisible))
            && blocker.PrivLevel >= PrivLevel.Counsel)
            return true;

        if (mover.Stam == mover.MaxStam && mover.MaxStam > 0)
            return true;

        return false;
    }

    /// <summary>Check step effects (traps, fields, region enter/leave).</summary>
    private void CheckLocationEffects(Objects.Characters.Character ch, Point3D originalPos)
    {
        var pos = originalPos;
        bool spellHit = false;
        foreach (var item in _world.GetItemsInRange(pos, 0))
        {
            // Source-X CheckLocation weeds out anything the character cannot
            // actually reach in Z before it even looks at @STEP
            // (CCharAct.cpp:4934) - a trap, moongate or field on the floor below
            // shares the X/Y but is a storey away.
            if (!item.IsWithinStepHeight(ch.Z)) continue;

            // Source-X parity: @Step trigger gets first chance. RETURN 1
            // cancels the hard-coded effect (trap, teleport, moongate, …)
            // so scripts can fully replace native behaviour.
            // ARGN1 = fStanding (Source-X CCharAct m_iN1): 0 here — the char is
            // walking onto the item, not standing on it.
            var stepResult = _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Step,
                new TriggerArgs { CharSrc = ch, ItemSrc = item, N1 = 0 });
            if (stepResult == TriggerResult.True)
                continue;

            switch (item.ItemType)
            {
                case ItemType.Web:
                    // Source-X Use_Item_Web: walking into a web sticks the char
                    // (giant spiders, ghosts and staff pass through). Freeze
                    // holds them until the web is destroyed by struggling
                    // (dclick, STR-based) or an outside hit knocks them free.
                    if (!ch.IsDead && ch.PrivLevel < PrivLevel.Counsel && ch.BodyId != 0x1C &&
                        !ch.IsStatFlag(StatFlag.Insubstantial))
                    {
                        if (item.HitsCur <= 0)
                            item.HitsCur = 60 + Random.Shared.Next(250); // Source-X CCharUse.cpp:638 web strength
                        ch.SetStatFlag(StatFlag.Freeze);
                        // Source-X LAYER_FLAG_Stuck (CCharAct.cpp:358) shows the
                        // paralyze icon while a char is held. No countdown here:
                        // the web is escaped by struggling, not by a timer.
                        Character.OnClientBuffChanged?.Invoke(
                            ch, BuffIcon.Paralyze, true, 0, null);
                    }
                    break;
                case ItemType.Trap:
                case ItemType.TrapActive:
                    // Source-X CCharAct CheckLocation: stepping springs the trap —
                    // Use_Trap() arms it and yields the MORE2 base damage.
                    int trapDamage = item.UseTrap();
                    if (!Combat.CombatEngine.IsDamageImmune(ch))
                    {
                        ch.Hits -= (short)Math.Min(trapDamage, ch.Hits);
                        if (ch.Hits <= 0 && !ch.IsDead)
                        {
                            if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, null);
                            else ch.Kill();
                        }
                    }
                    break;
                case ItemType.Telepad:
                case ItemType.Moongate:
                {
                    var dest = item.MoreP;
                    var md = _world.MapData;
                    bool destPassable = md == null || md.IsPassable(dest.Map, dest.X, dest.Y, dest.Z);
                    if ((dest.X != 0 || dest.Y != 0) && dest.X >= 0 && dest.Y >= 0 &&
                        _world.GetSector(dest) != null && destPassable)
                    {
                        byte oldMap = ch.MapIndex;
                        _world.MoveCharacter(ch, dest);
                        OnTeleport?.Invoke(ch, dest, oldMap);
                        pos = ch.Position;
                    }
                    break;
                }
            }

            // Typed field step effect (fire damages, poison poisons, paralyze
            // freezes, barriers inert — Source-X field spell on step). Falls
            // back to the legacy flat FIELD_DAMAGE for script-made fields.
            // Source-X caps one location check at a single spell effect
            // (CCharAct.cpp:4996): stacking Fire Fields on a tile would otherwise
            // multiply the damage of one step, and a Paralyze+Fire stack would
            // re-freeze the victim at every damage tick with no way out. The cap
            // follows the RESULT, not the attempt - a field that landed nothing
            // leaves the next one its chance.
            var touch = spellHit && item.TryGetTag("FIELD_SPELL", out _)
                ? FieldTouchResult.Handled      // a spell field already went off here
                : Character.FieldTouchHook?.Invoke(ch, item) ?? FieldTouchResult.NotHandled;
            if (touch == FieldTouchResult.SpellHit)
                spellHit = true;

            if (touch == FieldTouchResult.NotHandled &&
                item.TryGetTag("FIELD_DAMAGE", out string? fdStr) && int.TryParse(fdStr, out int fieldDmg) &&
                !Combat.CombatEngine.IsDamageImmune(ch))
            {
                ch.Hits -= (short)Math.Min(fieldDmg, ch.Hits);
                if (ch.Hits <= 0 && !ch.IsDead)
                {
                    if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, null);
                    else ch.Kill();
                }
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

                // Source-X CCharBase::Region_Notify is now centralised in
                // GameWorld.OnRegionChanged so walking, .go teleport, recall
                // and gate all produce the same MSG_REGION_ENTER / guard /
                // PvP banner — no per-engine duplication needed here.

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
    public static int GetMoveDelay(bool mounted, bool running, bool warMode = false)
    {
        // War mode does NOT slow movement. Neither the 2D/CUO client
        // (MovementSpeed.TimeToCompleteMovement takes no war flag — foot steps
        // stay 400/200ms in or out of combat) nor Source-X (Event_CheckWalkBuffer
        // and the fastwalk delay in CClientEvent use pure foot/mount × walk/run
        // timings with no combat-stance factor) apply a penalty. Pacing the server
        // 20% slower than the client sends desyncs the client's unconfirmed-step
        // budget and produces an on-foot in-combat walk stutter. The warMode
        // parameter is retained for call-site compatibility.
        _ = warMode;
        return (mounted, running) switch
        {
            (true, true) => RunDelayMount,
            (true, false) => WalkDelayMount,
            (false, true) => RunDelayFoot,
            (false, false) => WalkDelayFoot,
        };
    }

    public static int GetMoveDelay(bool mounted, bool running, bool warMode, byte speedMode)
    {
        // Source-X Event_Walk treats SPEEDMODE bit 0 like mounted movement:
        // foot walk/run cadence becomes 200/100ms instead of 400/200ms.
        bool speedMounted = mounted || ((speedMode & 0x01) != 0);
        return GetMoveDelay(speedMounted, running, warMode);
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

    private static int GetPackWeight(Objects.Items.Item pack)
    {
        return pack.TotalWeight;
    }

    private static void TickStealthStep(Objects.Characters.Character ch)
    {
        if (ch.StepStealth <= 0)
            return;

        ch.StepStealth--;
        Character.OnStepStealth?.Invoke(ch);

        if (ch.StepStealth <= 0)
            ch.ClearHiddenState();
    }
}
