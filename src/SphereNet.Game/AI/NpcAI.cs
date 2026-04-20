using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.AI;

/// <summary>
/// NPC AI flags. Maps to NPC_AI_* defines in Source-X CServerConfig.h.
/// </summary>
[Flags]
public enum NpcAIFlags : uint
{
    None = 0,
    Path = 0x0001,
    Food = 0x0002,
    Extra = 0x0004,
    AlwaysInt = 0x0008,
    IntFood = 0x0010,
    Combat = 0x0020,
    VendTime = 0x0040,
    Looting = 0x0080,
    MoveObstacles = 0x0100,
    PersistentPath = 0x0200,
    Threat = 0x0400,
}

/// <summary>
/// NPC AI engine. Maps to CChar::NPC_* functions in Source-X CCharNPCAct.cpp.
/// Handles brain-based decision making and action execution per tick.
/// </summary>
public sealed class NpcAI
{
    public enum NpcDecisionType
    {
        None = 0,
        Move = 1,
        Legacy = 2
    }

    public readonly record struct NpcDecision(
        uint NpcUid,
        NpcDecisionType Type,
        Point3D TargetPos,
        Direction Direction,
        long NextActionTick);

    private readonly GameWorld _world;
    private readonly Pathfinder _pathfinder;
    private readonly Random _rand = new();

    // Cached paths per NPC UID — avoids recalculating every tick
    private readonly Dictionary<uint, List<Point3D>> _pathCache = [];
    private readonly Dictionary<uint, int> _pathIndex = [];

    public NpcAI(GameWorld world)
    {
        _world = world;
        _pathfinder = new Pathfinder(world);
    }

    /// <summary>
    /// Main NPC tick action. Maps to NPC_OnTickAction in Source-X.
    /// Called every tick for each living, non-frozen NPC.
    /// </summary>
    public void OnTickAction(Character npc)
    {
        if (npc.IsPlayer || npc.IsDead || npc.IsStatFlag(StatFlag.Ridden)) return;

        long now = Environment.TickCount64;
        if (now < npc.NextNpcActionTime)
            return;

        // Active-area gate: no player in view-range → park the NPC for ~5s.
        // Pets bypass (they live next to their owner by definition). Without
        // this, million-entity worlds fire 750K+ brain ticks per second even
        // when the player is alone in the wilderness.
        if (!npc.NpcMaster.IsValid && !_world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y))
        {
            npc.NextNpcActionTime = now + 5000 + _rand.Next(0, 5000);
            return;
        }

        // NPC decision/action cadence: avoid moving every server tick (250ms),
        // which floods nearby clients with 0x20 movement updates.
        npc.NextNpcActionTime = now + 700 + _rand.Next(0, 200);

        // Regen
        npc.OnTick();

        // Pet behavior — owned NPCs follow pet AI mode
        if (npc.NpcMaster.IsValid)
        {
            ActPet(npc);
            return;
        }

        // Brain-based behavior
        switch (npc.NpcBrain)
        {
            case NpcBrainType.Guard:
                ActGuard(npc);
                break;
            case NpcBrainType.Monster:
            case NpcBrainType.Dragon:
                ActMonster(npc);
                break;
            case NpcBrainType.Berserk:
                ActBerserk(npc);
                break;
            case NpcBrainType.Healer:
                ActHealer(npc);
                break;
            case NpcBrainType.Vendor:
            case NpcBrainType.Banker:
            case NpcBrainType.Stable:
                ActVendor(npc);
                break;
            case NpcBrainType.Animal:
                ActAnimal(npc);
                break;
            case NpcBrainType.Human:
            default:
                ActHuman(npc);
                break;
        }
    }

    /// <summary>
    /// Build a deterministic AI decision without mutating world state.
    /// Returns null when no action should be applied this tick.
    /// </summary>
    public NpcDecision? BuildDecision(Character npc, long nowTick)
    {
        if (npc.IsPlayer || npc.IsDead || npc.IsDeleted)
            return null;
        if (nowTick < npc.NextNpcActionTime)
            return null;

        // Active-area gate (see OnTickAction). Deterministic jitter keeps the
        // multicore path reproducible.
        if (!npc.NpcMaster.IsValid && !_world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y))
        {
            npc.NextNpcActionTime = nowTick + 5000 + DeterministicJitter(npc.Uid.Value, nowTick, 5000);
            return null;
        }

        long nextAction = nowTick + 700 + DeterministicJitter(npc.Uid.Value, nowTick, 200);

        // Keep combat-oriented brains on legacy path for behavior parity.
        if (npc.NpcBrain is NpcBrainType.Guard or NpcBrainType.Monster or NpcBrainType.Dragon or NpcBrainType.Berserk)
        {
            return new NpcDecision(npc.Uid.Value, NpcDecisionType.Legacy, npc.Position, npc.Direction, nextAction);
        }

        // Deterministic wander decision for non-combat brains.
        var dir = GetDeterministicDirection(npc.Uid.Value, nowTick);
        GetDirectionDelta(dir, out short dx, out short dy);
        if (dx == 0 && dy == 0)
            return new NpcDecision(npc.Uid.Value, NpcDecisionType.None, npc.Position, npc.Direction, nextAction);

        var target = new Point3D(
            (short)(npc.X + dx),
            (short)(npc.Y + dy),
            npc.Z,
            npc.MapIndex);

        return new NpcDecision(npc.Uid.Value, NpcDecisionType.Move, target, dir, nextAction);
    }

    /// <summary>
    /// Apply a previously computed decision in a single-threaded phase.
    /// </summary>
    public void ApplyDecision(NpcDecision decision)
    {
        var npc = _world.FindChar(new Serial(decision.NpcUid));
        if (npc == null || npc.IsDeleted || npc.IsDead || npc.IsPlayer)
            return;

        npc.NextNpcActionTime = decision.NextActionTick;
        switch (decision.Type)
        {
            case NpcDecisionType.Move:
                npc.Direction = decision.Direction;
                _world.MoveCharacter(npc, decision.TargetPos);
                break;
            case NpcDecisionType.Legacy:
                OnTickAction(npc);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Guard: patrol, attack criminals/murderers in guarded regions.
    /// </summary>
    private void ActGuard(Character npc)
    {
        var region = _world.FindRegion(npc.Position);
        bool isGuarded = region?.IsGuarded ?? false;

        foreach (var target in _world.GetCharsInRange(npc.Position, 12))
        {
            if (target == npc || target.IsDead) continue;

            if (isGuarded && target.IsStatFlag(StatFlag.Criminal))
            {
                // Instant kill criminal in guarded area (if GUARDSINSTANTKILL)
                target.Kill();
                return;
            }
        }

        Wander(npc);
    }

    /// <summary>
    /// Monster/Dragon: look for targets to attack, fight, or wander.
    /// </summary>
    private void ActMonster(Character npc)
    {
        // Look for nearest player target
        Character? bestTarget = null;
        int bestDist = int.MaxValue;

        foreach (var ch in _world.GetCharsInRange(npc.Position, 10))
        {
            if (ch == npc || ch.IsDead || !ch.IsPlayer) continue;
            if (ch.IsStatFlag(StatFlag.Invisible) || ch.IsStatFlag(StatFlag.Hidden)) continue;

            int dist = npc.Position.GetDistanceTo(ch.Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestTarget = ch;
            }
        }

        if (bestTarget != null)
        {
            npc.FightTarget = bestTarget.Uid;

            if (bestDist <= 1)
            {
                // In melee range — attack with swing timer
                TrySwingAttack(npc, bestTarget);
            }
            else
            {
                MoveToward(npc, bestTarget.Position);
            }
            return;
        }

        npc.FightTarget = Serial.Invalid;
        Wander(npc);
    }

    /// <summary>Berserk: attack nearest visible character (hostile to everyone).</summary>
    private void ActBerserk(Character npc)
    {
        Character? nearest = null;
        int nearestDist = int.MaxValue;

        foreach (var ch in _world.GetCharsInRange(npc.Position, 8))
        {
            if (ch == npc || ch.IsDead) continue;
            int dist = npc.Position.GetDistanceTo(ch.Position);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = ch;
            }
        }

        if (nearest != null)
        {
            npc.FightTarget = nearest.Uid;

            if (nearestDist <= 1)
            {
                TrySwingAttack(npc, nearest);
            }
            else
            {
                MoveToward(npc, nearest.Position);
            }
            return;
        }

        npc.FightTarget = Serial.Invalid;
        Wander(npc);
    }

    /// <summary>Healer: look for dead players to resurrect, then act human.</summary>
    private void ActHealer(Character npc)
    {
        foreach (var ch in _world.GetCharsInRange(npc.Position, 4))
        {
            if (ch == npc || !ch.IsDead || !ch.IsPlayer) continue;

            ch.Resurrect();
            return;
        }

        ActHuman(npc);
    }

    /// <summary>Vendor/Banker/Stable: stay near home, respond to speech.</summary>
    private void ActVendor(Character npc)
    {
        // Return to home position if too far
        if (npc.TryGetTag("HOME_X", out string? hx) && npc.TryGetTag("HOME_Y", out string? hy))
        {
            if (short.TryParse(hx, out short homeX) && short.TryParse(hy, out short homeY))
            {
                sbyte homeZ = npc.Z;
                if (npc.TryGetTag("HOME_Z", out string? hz))
                    sbyte.TryParse(hz, out homeZ);

                var home = new Point3D(homeX, homeY, homeZ, npc.MapIndex);
                int dist = npc.Position.GetDistanceTo(home);
                if (dist > 5)
                {
                    MoveToward(npc, home);
                    return;
                }
            }
        }

        // Occasionally look around (idle behavior)
        if (_rand.Next(100) < 5)
            Wander(npc);
    }

    /// <summary>Animal: wander, flee from combat.</summary>
    private void ActAnimal(Character npc)
    {
        // Check for nearby threats
        foreach (var ch in _world.GetCharsInRange(npc.Position, 6))
        {
            if (ch == npc || ch.IsDead) continue;
            if (ch.IsStatFlag(StatFlag.War))
            {
                // Flee
                MoveAway(npc, ch.Position);
                return;
            }
        }

        if (_rand.Next(100) < 20) // 20% chance to wander each tick
            Wander(npc);
    }

    /// <summary>Human: idle, look around, wander occasionally.</summary>
    private void ActHuman(Character npc)
    {
        if (_rand.Next(100) < 10)
            Wander(npc);
    }

    /// <summary>
    /// Pet behavior — follows PetAIMode from owner speech commands.
    /// </summary>
    private void ActPet(Character npc)
    {
        var master = _world.FindChar(npc.NpcMaster);
        if (master == null || master.IsDead)
        {
            // Master gone — wander
            Wander(npc);
            return;
        }

        switch (npc.PetAIMode)
        {
            case PetAIMode.Follow:
            case PetAIMode.Come:
            {
                int dist = npc.Position.GetDistanceTo(master.Position);
                if (dist > 2)
                    MoveToward(npc, master.Position);
                break;
            }
            case PetAIMode.Guard:
            {
                // Guard master — attack nearby aggressors
                foreach (var ch in _world.GetCharsInRange(master.Position, 6))
                {
                    if (ch == npc || ch == master || ch.IsDead) continue;
                    if (ch.FightTarget == master.Uid)
                    {
                        int dist = npc.Position.GetDistanceTo(ch.Position);
                        if (dist <= 1)
                            TrySwingAttack(npc, ch);
                        else
                            MoveToward(npc, ch.Position);
                        return;
                    }
                }
                // No threats — follow master
                int masterDist = npc.Position.GetDistanceTo(master.Position);
                if (masterDist > 3)
                    MoveToward(npc, master.Position);
                break;
            }
            case PetAIMode.Attack:
            {
                // Attack the master's fight target
                if (master.FightTarget.IsValid)
                {
                    var target = _world.FindChar(master.FightTarget);
                    if (target != null && !target.IsDead)
                    {
                        int dist = npc.Position.GetDistanceTo(target.Position);
                        if (dist <= 1)
                            TrySwingAttack(npc, target);
                        else
                            MoveToward(npc, target.Position);
                        return;
                    }
                }
                // No target — follow
                int d = npc.Position.GetDistanceTo(master.Position);
                if (d > 2)
                    MoveToward(npc, master.Position);
                break;
            }
            case PetAIMode.Stay:
            case PetAIMode.Stop:
                // Stay in place
                break;
        }
    }

    /// <summary>
    /// Callback for when an NPC successfully deals damage. Used by Program.cs to broadcast effects.
    /// Parameters: attacker, target, damage dealt
    /// </summary>
    public Action<Character, Character, int>? OnNpcAttack { get; set; }

    /// <summary>
    /// Callback for when an NPC kills a target. Used by Program.cs to run DeathEngine + broadcast.
    /// Parameters: killer, victim
    /// </summary>
    public Action<Character, Character>? OnNpcKill { get; set; }

    /// <summary>
    /// Try to swing attack a target with swing timer throttle.
    /// </summary>
    private void TrySwingAttack(Character npc, Character target)
    {
        if (npc.IsDead || npc.Hits <= 0 || target.IsDead || target.Hits <= 0)
            return;

        long now = Environment.TickCount64;
        if (now < npc.NextAttackTime)
            return;

        // Source-X Fight_CalcDelay: speed * 100 / (DEX + 100) in ticks (100ms)
        int speed = 35;
        int dexMod = Math.Max(10, (int)npc.Dex);
        int swingDelayMs = Math.Clamp(speed * 100 * 100 / (dexMod + 100), 1250, 5000);
        npc.NextAttackTime = now + swingDelayMs;

        // Get NPC weapon (if any)
        Item? weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);

        short hpBefore = npc.Hits;
        int damage = CombatEngine.ResolveAttack(npc, target, weapon);
        if (damage > 0)
        {
            OnNpcAttack?.Invoke(npc, target, damage);

            if (target.Hits <= 0 && !target.IsDead)
                OnNpcKill?.Invoke(npc, target);
        }

        // Reactive armor reflect may have killed the attacker
        if (npc.Hits < hpBefore && npc.Hits <= 0 && !npc.IsDead)
            OnNpcKill?.Invoke(npc, npc);

        // Face the target
        npc.Direction = npc.Position.GetDirectionTo(target.Position);
    }

    // --- Movement helpers ---

    private void Wander(Character npc)
    {
        int dx = _rand.Next(-1, 2);
        int dy = _rand.Next(-1, 2);
        if (dx == 0 && dy == 0) return;

        var newPos = new Point3D(
            (short)(npc.X + dx),
            (short)(npc.Y + dy),
            npc.Z,
            npc.MapIndex
        );

        _world.MoveCharacter(npc, newPos);
    }

    private void MoveToward(Character npc, Point3D target)
    {
        // Try direct line-of-sight move first
        var dir = npc.Position.GetDirectionTo(target);
        GetDirectionDelta(dir, out short dx, out short dy);

        var directPos = new Point3D(
            (short)(npc.X + dx),
            (short)(npc.Y + dy),
            npc.Z,
            npc.MapIndex
        );

        // Check if direct path is walkable
        bool directBlocked = false;
        foreach (var item in _world.GetItemsInRange(directPos, 0))
        {
            if (item.IsStaticBlock) { directBlocked = true; break; }
        }
        if (!directBlocked)
        {
            var mapData = _world.MapData;
            if (mapData != null && !mapData.IsPassable(directPos.Map, directPos.X, directPos.Y, directPos.Z))
                directBlocked = true;
        }

        if (!directBlocked)
        {
            // Direct move works — use it and clear any cached path
            npc.Direction = dir;
            _world.MoveCharacter(npc, directPos);
            _pathCache.Remove(npc.Uid.Value);
            _pathIndex.Remove(npc.Uid.Value);
            return;
        }

        // Direct path blocked — use A* pathfinding
        uint uid = npc.Uid.Value;
        if (!_pathCache.TryGetValue(uid, out var path) || path.Count == 0)
        {
            // Calculate new path
            path = _pathfinder.FindPath(npc.Position, target, npc.MapIndex);
            if (path == null || path.Count == 0)
            {
                // No path found — try direct move anyway
                npc.Direction = dir;
                _world.MoveCharacter(npc, directPos);
                return;
            }
            _pathCache[uid] = path;
            _pathIndex[uid] = 0;
        }

        int idx = _pathIndex.GetValueOrDefault(uid, 0);
        if (idx >= path.Count)
        {
            // Path exhausted — recalculate
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
            return;
        }

        var nextStep = path[idx];
        npc.Direction = npc.Position.GetDirectionTo(nextStep);
        _world.MoveCharacter(npc, nextStep);
        _pathIndex[uid] = idx + 1;
    }

    private void MoveAway(Character npc, Point3D threat)
    {
        var dir = npc.Position.GetDirectionTo(threat);
        // Reverse direction
        GetDirectionDelta(dir, out short dx, out short dy);

        var newPos = new Point3D(
            (short)(npc.X - dx),
            (short)(npc.Y - dy),
            npc.Z,
            npc.MapIndex
        );

        _world.MoveCharacter(npc, newPos);
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

    private static Direction GetDeterministicDirection(uint uid, long nowTick)
    {
        unchecked
        {
            uint mixed = uid * 1103515245u + (uint)nowTick * 12345u;
            return (Direction)(mixed & 0x07);
        }
    }
}
