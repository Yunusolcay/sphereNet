using SphereNet.Core.Configuration;
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
    public NpcAIFlags Flags { get; set; } =
        NpcAIFlags.Path | NpcAIFlags.Combat | NpcAIFlags.Threat | NpcAIFlags.PersistentPath;

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
    private readonly SphereConfig _config;
    private readonly Random _rand = new();

    // Cached paths per NPC UID — avoids recalculating every tick
    private readonly Dictionary<uint, List<Point3D>> _pathCache = [];
    private readonly Dictionary<uint, int> _pathIndex = [];
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
            if (obj is not Character ch || ch.IsDeleted || ch.IsDead)
                (stale ??= []).Add(uid);
        }
        if (stale != null)
        {
            foreach (var uid in stale)
            {
                _pathCache.Remove(uid);
                _pathIndex.Remove(uid);
            }
        }
    }

    public NpcAI(GameWorld world, SphereConfig config)
    {
        _world = world;
        _config = config;
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

        // Pets and combat brains need the full OnTickAction path (ActPet,
        // ActGuard, ActMonster etc.) — route them through Legacy.
        if (npc.NpcMaster.IsValid ||
            npc.NpcBrain is NpcBrainType.Guard or NpcBrainType.Monster or NpcBrainType.Dragon or NpcBrainType.Berserk)
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

        switch (decision.Type)
        {
            case NpcDecisionType.Move:
                npc.NextNpcActionTime = decision.NextActionTick;
                npc.Direction = decision.Direction;
                _world.MoveCharacter(npc, decision.TargetPos);
                break;
            case NpcDecisionType.Legacy:
                // Let OnTickAction own the cadence update; setting NextNpcActionTime
                // before the legacy call would make the combat brain return early.
                OnTickAction(npc);
                break;
            default:
                npc.NextNpcActionTime = decision.NextActionTick;
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
        if (!isGuarded)
        {
            Wander(npc);
            return;
        }

        if (npc.FightTarget.IsValid)
        {
            var assigned = _world.FindChar(npc.FightTarget);
            if (assigned != null && !assigned.IsDead && !assigned.IsDeleted)
            {
                GuardEngage(npc, assigned);
                return;
            }
            npc.FightTarget = Serial.Invalid;
        }

        bool guardMurderers = _config.GuardsOnMurderers;
        foreach (var target in _world.GetCharsInRange(npc.Position, 12))
        {
            if (target == npc || target.IsDead) continue;
            bool isCriminal = target.IsStatFlag(StatFlag.Criminal) || target.IsCriminal;
            bool isMurderer = guardMurderers && target.IsMurderer;
            if (isCriminal || isMurderer)
            {
                npc.FightTarget = target.Uid;
                GuardEngage(npc, target);
                return;
            }
        }

        Wander(npc);
    }

    public Action<Character, string>? OnNpcSay { get; set; }
    public Action<Character>? OnGuardLightningStrike { get; set; }
    public Action<Character>? OnNpcTeleport { get; set; }

    private void GuardEngage(Character guard, Character target)
    {
        if (!guard.TryGetTag("GUARD_YELLED", out _))
        {
            guard.SetTag("GUARD_YELLED", "1");
            OnNpcSay?.Invoke(guard, "Halt, villain! Guards!");
        }

        int dist = guard.Position.GetDistanceTo(target.Position);
        if (_config.GuardsInstantKill)
        {
            if (dist > 1)
            {
                _world.MoveCharacter(guard, target.Position);
                OnNpcTeleport?.Invoke(guard);
            }
            OnGuardLightningStrike?.Invoke(target);
            target.Hits = 0;
            guard.FightTarget = Serial.Invalid;
            guard.RemoveTag("GUARD_YELLED");
            OnNpcKill?.Invoke(guard, target);
        }
        else
        {
            if (dist <= GetAttackRange(guard))
                TrySwingAttack(guard, target);
            else
                MoveToward(guard, target.Position);
        }
    }

    /// <summary>
    /// Monster/Dragon: look for targets to attack, fight, or wander.
    /// </summary>
    private void ActMonster(Character npc)
    {
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
            // Source-X: flee when HP < 25% of max (motivation < 0)
            if (npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 4)
            {
                MoveAway(npc, bestTarget.Position);
                return;
            }

            npc.FightTarget = bestTarget.Uid;
            npc.Memory_Fight_Start(bestTarget);
            if (bestDist <= GetAttackRange(npc))
                TrySwingAttack(npc, bestTarget);
            else
                MoveToward(npc, bestTarget.Position);
            return;
        }

        npc.FightTarget = Serial.Invalid;
        WanderHome(npc);
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
            npc.Memory_Fight_Start(nearest);

            if (nearestDist <= GetAttackRange(npc))
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
        WanderHome(npc);
    }

    /// <summary>Healer: resurrect dead, heal wounded, refuse criminals/evil.</summary>
    private void ActHealer(Character npc)
    {
        // Priority 1: resurrect dead players in range (Source-X: 3 tiles)
        foreach (var ch in _world.GetCharsInRange(npc.Position, 3))
        {
            if (ch == npc || !ch.IsDead || !ch.IsPlayer) continue;
            if (ch.IsCriminal || ch.IsMurderer) continue;

            OnHealerAction?.Invoke(npc, ch, true);
            ch.Resurrect();
            return;
        }

        // Priority 2: heal wounded friendly NPCs/players (HP < 50%)
        foreach (var ch in _world.GetCharsInRange(npc.Position, 3))
        {
            if (ch == npc || ch.IsDead) continue;
            if (ch.IsCriminal || ch.IsMurderer) continue;
            if (ch.MaxHits > 0 && ch.Hits < ch.MaxHits / 2)
            {
                int heal = Math.Max(1, npc.Int / 5);
                ch.Hits = (short)Math.Min(ch.Hits + heal, ch.MaxHits);
                OnHealerAction?.Invoke(npc, ch, false);
                return;
            }
        }

        ActHuman(npc);
    }

    /// <summary>Callback: healer performs action. Parameters: healer, target, isResurrect.
    /// Used by Program.cs to broadcast cast animation and sound.</summary>
    public Action<Character, Character, bool>? OnHealerAction { get; set; }

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
        foreach (var ch in _world.GetCharsInRange(npc.Position, 6))
        {
            if (ch == npc || ch.IsDead) continue;
            if (ch.IsStatFlag(StatFlag.War))
            {
                MoveAway(npc, ch.Position);
                return;
            }
        }

        if (_rand.Next(100) < 20)
            WanderHome(npc);
    }

    /// <summary>Human: idle, look around, wander occasionally.</summary>
    private void ActHuman(Character npc)
    {
        if (_rand.Next(100) < 10)
            WanderHome(npc);
    }

    /// <summary>
    /// Pet behavior — follows PetAIMode from owner speech commands.
    /// </summary>
    private void ActPet(Character npc)
    {
        if (npc.TickPetOwnershipTimers(Environment.TickCount64))
        {
            _world.DeleteObject(npc);
            npc.Delete();
            return;
        }

        var master = npc.ResolveControllerCharacter() ?? npc.ResolveOwnerCharacter();
        if (master == null || master.IsDead)
        {
            if (npc.IsSummoned)
            {
                _world.DeleteObject(npc);
                npc.Delete();
                return;
            }

            // Owner gone — uncontrolled pets idle instead of following stale state.
            Wander(npc);
            return;
        }

        switch (npc.PetAIMode)
        {
            case PetAIMode.Follow:
            case PetAIMode.Come:
            {
                Character followTarget = ResolvePetTargetCharacter(npc, "FOLLOW_TARGET") ?? master;
                int dist = npc.Position.GetDistanceTo(followTarget.Position);
                if (dist > 2)
                    MoveToward(npc, followTarget.Position);
                if (npc.TryGetTag("GO_TARGET", out string? goTag) &&
                    TryParsePoint(goTag, out Point3D goPos))
                {
                    int goDist = npc.Position.GetDistanceTo(goPos);
                    if (goDist > 1)
                        MoveToward(npc, goPos);
                    else
                        npc.RemoveTag("GO_TARGET");
                }
                break;
            }
            case PetAIMode.Guard:
            {
                Character guardTarget = ResolvePetTargetCharacter(npc, "GUARD_TARGET") ?? master;
                // Guard master — attack nearby aggressors
                foreach (var ch in _world.GetCharsInRange(guardTarget.Position, 6))
                {
                    if (ch == npc || ch == guardTarget || ch.IsDead) continue;
                    if (ch.FightTarget == guardTarget.Uid)
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
                int guardDist = npc.Position.GetDistanceTo(guardTarget.Position);
                if (guardDist > 3)
                    MoveToward(npc, guardTarget.Position);
                break;
            }
            case PetAIMode.Attack:
            {
                // Attack explicit pet target first, fall back to master's fight target.
                Character? target = ResolvePetTargetCharacter(npc, "ATTACK_TARGET");
                if (target == null && master.FightTarget.IsValid)
                    target = _world.FindChar(master.FightTarget);
                if (target != null && !target.IsDead)
                {
                    int dist = npc.Position.GetDistanceTo(target.Position);
                    if (dist <= 1)
                        TrySwingAttack(npc, target);
                    else
                        MoveToward(npc, target.Position);
                    return;
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
    /// Uses the same pre-AOS Source-X swing-speed formula as players
    /// (<see cref="SphereNet.Game.Clients.GameClient.GetSwingDelayMs"/>),
    /// gated by the same can-swing checks (dead / sleeping / frozen /
    /// out-of-stamina / mid-cast). Mirrors CChar::Fight_Hit:
    ///   <list type="number">
    ///     <item>Validate state and STAM &gt; 0.</item>
    ///     <item>Turn to face the target before launching the swing
    ///       so the animation plays the right way (UpdateDir).</item>
    ///     <item>Resolve damage and consume one stamina point.</item>
    ///     <item>Set <c>NextAttackTime</c> to the full swing recoil.</item>
    ///   </list>
    /// </summary>
    private void TrySwingAttack(Character npc, Character target)
    {
        if (npc.IsDead || npc.Hits <= 0 || target.IsDead || target.Hits <= 0)
            return;

        long now = Environment.TickCount64;
        if (now < npc.NextAttackTime)
            return;

        // Same gating Source-X applies to player attackers — see
        // GameClient.TrySwingAt for rationale.
        if (npc.Stam <= 0)
        {
            npc.NextAttackTime = now + 1000;
            return;
        }
        if (npc.IsStatFlag(StatFlag.Freeze) || npc.IsStatFlag(StatFlag.Sleeping))
        {
            npc.NextAttackTime = now + 500;
            return;
        }
        if (npc.TryGetTag("SPELL_CASTING", out _))
        {
            npc.NextAttackTime = now + 500;
            return;
        }

        Item? weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        int maxRange = GetAttackRange(npc, weapon);
        int distToTarget = npc.Position.GetDistanceTo(target.Position);
        if (distToTarget > maxRange)
            return;

        // Source-X formula 0 swing delay, identical to player code.
        int swingDelayMs = SphereNet.Game.Clients.GameClient.GetSwingDelayMs(npc, weapon);
        npc.NextAttackTime = now + swingDelayMs;

        // Face the target *before* the swing — Source-X UpdateDir(pCharTarg).
        // Direction setter marks the dirty flag; the actual 0x77 broadcast
        // is performed by Program.cs's OnNpcAttack handler when it sends
        // the swing animation, so a single network update covers both
        // the new facing and the swing.
        var newDir = npc.Position.GetDirectionTo(target.Position);
        if (newDir != npc.Direction)
            npc.Direction = newDir;

        if (npc.Stam > 0)
            npc.Stam = (short)(npc.Stam - 1);

        // Source-style owner attribution: pet/summon attacks in guarded towns
        // should criminal-flag the owner when targeting an innocent player.
        if (npc.OwnerSerial.IsValid && target.IsPlayer && Character.AttackingIsACrimeEnabled)
        {
            var owner = npc.ResolveOwnerCharacter();
            if (owner != null && owner.IsPlayer && !owner.IsDead)
            {
                var region = _world.FindRegion(owner.Position);
                bool targetInnocent = !target.IsCriminal && !target.IsMurderer;
                if (targetInnocent && region != null && region.IsFlag(RegionFlag.Guarded))
                    owner.MakeCriminal();
            }
        }

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
    }

    // --- Movement helpers ---

    private void Wander(Character npc)
    {
        int dx = _rand.Next(-1, 2);
        int dy = _rand.Next(-1, 2);
        if (dx == 0 && dy == 0) return;

        short nx = (short)(npc.X + dx);
        short ny = (short)(npc.Y + dy);
        var mapData = _world.MapData;
        sbyte nz = mapData?.GetEffectiveZ(npc.MapIndex, nx, ny, npc.Z) ?? npc.Z;
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
        if (mapData != null && !mapData.IsPassable(newPos.Map, newPos.X, newPos.Y, newPos.Z))
            return;

        _world.MoveCharacter(npc, newPos);
    }

    /// <summary>Wander with home range check. Source-X: m_Home_Dist_Wander.</summary>
    private void WanderHome(Character npc)
    {
        if (npc.TryGetTag("HOME_X", out string? hx) && npc.TryGetTag("HOME_Y", out string? hy) &&
            short.TryParse(hx, out short homeX) && short.TryParse(hy, out short homeY))
        {
            int homeDist = npc.TryGetTag("HOME_DIST", out string? hdStr) && int.TryParse(hdStr, out int hd) ? hd : 10;
            int curDist = Math.Abs(npc.X - homeX) + Math.Abs(npc.Y - homeY);
            if (curDist > homeDist)
            {
                sbyte homeZ = npc.Z;
                if (npc.TryGetTag("HOME_Z", out string? hz))
                    sbyte.TryParse(hz, out homeZ);
                MoveToward(npc, new Point3D(homeX, homeY, homeZ, npc.MapIndex));
                return;
            }
        }
        Wander(npc);
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

        if (!Flags.HasFlag(NpcAIFlags.Path))
        {
            npc.Direction = dir;
            return;
        }

        // Direct path blocked — use A* pathfinding
        uint uid = npc.Uid.Value;
        if (!Flags.HasFlag(NpcAIFlags.PersistentPath))
        {
            _pathCache.Remove(uid);
            _pathIndex.Remove(uid);
        }
        if (!_pathCache.TryGetValue(uid, out var path) || path.Count == 0)
        {
            // Calculate new path
            path = _pathfinder.FindPath(npc.Position, target, npc.MapIndex);
            if (path == null || path.Count == 0)
            {
                npc.Direction = dir;
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

    private Character? ResolvePetTargetCharacter(Character npc, string tagName)
    {
        if (!npc.TryGetTag(tagName, out string? uidText) || string.IsNullOrWhiteSpace(uidText))
            return null;
        if (!uint.TryParse(uidText, out uint uid))
            return null;
        var target = _world.FindChar(new Serial(uid));
        if (target == null || target.IsDeleted || target.IsDead)
        {
            npc.RemoveTag(tagName);
            return null;
        }
        return target;
    }

    private static bool TryParsePoint(string? raw, out Point3D pos)
    {
        pos = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return false;
        if (!short.TryParse(parts[0], out short x) ||
            !short.TryParse(parts[1], out short y) ||
            !sbyte.TryParse(parts[2], out sbyte z) ||
            !byte.TryParse(parts[3], out byte map))
            return false;
        pos = new Point3D(x, y, z, map);
        return true;
    }

    private static int GetAttackRange(Character npc, Item? weapon = null)
    {
        weapon ??= npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        if (weapon != null &&
            (weapon.ItemType == ItemType.WeaponBow || weapon.ItemType == ItemType.WeaponXBow))
        {
            return 10;
        }
        return 1;
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
