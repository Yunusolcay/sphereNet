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

/// <summary>Source-X CRESND_TYPE — creature sound categories.</summary>
public enum CreatureSoundType : byte
{
    Idle = 0,
    Notice = 1,
    Hit = 2,
    GetHit = 3,
    Die = 4,
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

        // Active-area gate: no player in view-range → park the NPC for 30-60s.
        // Pets bypass (they live next to their owner by definition). The long
        // park keeps timer wheel churn low on 100K+ NPC worlds; sector wake
        // in Program.cs reschedules NPCs immediately when a player enters.
        if (!npc.NpcMaster.IsValid && !_world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y))
        {
            npc.NextNpcActionTime = now + 30_000 + _rand.Next(0, 30_000);
            return;
        }

        // NPC tick cadence by role:
        //   Combat/active pets: 250ms (responsive fighting)
        //   Monsters/animals: 750ms (idle wander)
        //   Service NPCs (vendor/banker/healer/stable): 3-5s (minimal movement)
        bool isActive = npc.FightTarget.IsValid ||
            (npc.NpcMaster.IsValid && npc.PetAIMode is PetAIMode.Attack
                or PetAIMode.Follow or PetAIMode.Come or PetAIMode.Guard);
        bool isService = npc.NpcBrain is NpcBrainType.Vendor or NpcBrainType.Banker
            or NpcBrainType.Stable or NpcBrainType.Healer;
        if (isActive)
            npc.NextNpcActionTime = now + 250 + _rand.Next(0, 100);
        else if (isService)
            npc.NextNpcActionTime = now + 3000 + _rand.Next(0, 2000);
        else
            npc.NextNpcActionTime = now + 750 + _rand.Next(0, 250);

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
        if (npc.IsPlayer || npc.IsDead || npc.IsDeleted || npc.IsStatFlag(StatFlag.Ridden))
            return null;
        if (nowTick < npc.NextNpcActionTime)
            return null;

        // Active-area gate (see OnTickAction). Deterministic jitter keeps the
        // multicore path reproducible. 30-60s park — sector wake reschedules
        // these NPCs instantly when a player enters the area.
        if (!npc.NpcMaster.IsValid && !_world.IsInActiveArea(npc.MapIndex, npc.X, npc.Y))
        {
            npc.NextNpcActionTime = nowTick + 30_000 + DeterministicJitter(npc.Uid.Value, nowTick, 30_000);
            return null;
        }

        int spread = (int)((npc.Uid.Value * 2654435761u) % 400);
        long nextAction = nowTick + 600 + spread;

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
    /// Source-X: NPC_Act_Idle → NPC_LookAround → NPC_LookAtCharMonster + NPC_Act_Fight.
    /// </summary>
    private void ActMonster(Character npc)
    {
        int sightRange = GetNpcSight(npc);

        // If we have an existing target, check if it's still valid
        if (npc.FightTarget.IsValid)
        {
            var current = _world.FindChar(npc.FightTarget);
            if (current != null && !current.IsDead && !current.IsDeleted && IsAttackable(current))
            {
                int curMotivation = GetAttackMotivation(npc, current);
                if (curMotivation > 0)
                {
                    // Periodically scan for better target (Source-X: NPC_Act_Fight → NPC_LookAround)
                    if (_rand.Next(4) == 0)
                    {
                        var (betterTarget, betterMotivation) = FindBestTarget(npc, sightRange);
                        if (betterTarget != null && betterTarget != current && betterMotivation > curMotivation)
                        {
                            npc.FightTarget = betterTarget.Uid;
                            npc.Memory_Fight_Start(betterTarget);
                            current = betterTarget;
                            curMotivation = betterMotivation;
                        }
                    }

                    ActFight(npc, current, curMotivation);
                    return;
                }
            }
            npc.FightTarget = Serial.Invalid;
        }

        // No current target — scan for new one
        var (bestTarget, bestMotivation) = FindBestTarget(npc, sightRange);
        if (bestTarget != null && bestMotivation > 0)
        {
            npc.FightTarget = bestTarget.Uid;
            npc.Memory_Fight_Start(bestTarget);
            EmitSound(npc, CreatureSoundType.Notice);
            ActFight(npc, bestTarget, bestMotivation);
            return;
        }

        npc.FightTarget = Serial.Invalid;
        if (_rand.Next(8) == 0)
            EmitSound(npc, _rand.Next(2) == 0 ? CreatureSoundType.Idle : CreatureSoundType.Notice);
        WanderHome(npc);
    }

    /// <summary>Berserk: attack nearest visible character (hostile to everyone).</summary>
    private void ActBerserk(Character npc)
    {
        int sightRange = GetNpcSight(npc);

        if (npc.FightTarget.IsValid)
        {
            var current = _world.FindChar(npc.FightTarget);
            if (current != null && !current.IsDead && !current.IsDeleted && IsAttackable(current))
            {
                ActFight(npc, current, 100);
                return;
            }
            npc.FightTarget = Serial.Invalid;
        }

        Character? nearest = null;
        int nearestDist = int.MaxValue;

        foreach (var ch in _world.GetCharsInRange(npc.Position, sightRange))
        {
            if (ch == npc || !IsAttackable(ch)) continue;
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
            ActFight(npc, nearest, 100);
            return;
        }

        npc.FightTarget = Serial.Invalid;
        if (_rand.Next(6) == 0)
            EmitSound(npc, CreatureSoundType.Idle);
        WanderHome(npc);
    }

    /// <summary>
    /// Shared fight action. Source-X: NPC_Act_Fight — flee / special / spell / archery / melee.
    /// </summary>
    private void ActFight(Character npc, Character target, int motivation)
    {
        // Source-X: flee when motivation < 0 (non-pets only)
        if (!npc.IsStatFlag(StatFlag.Pet) && motivation < 0)
        {
            npc.FleeStepsMax = 20;
            npc.FleeStepsCurrent = 0;
            ActFlee(npc, target);
            return;
        }

        // Already fleeing? Continue.
        if (npc.FleeStepsCurrent > 0 && npc.FleeStepsCurrent < npc.FleeStepsMax)
        {
            ActFlee(npc, target);
            return;
        }
        npc.FleeStepsCurrent = 0;

        int dist = npc.Position.GetDistanceTo(target.Position);

        // Random idle combat sound (Source-X: Berserk or 1/6 chance)
        if (npc.NpcBrain == NpcBrainType.Berserk || _rand.Next(6) == 0)
            EmitSound(npc, CreatureSoundType.Idle);

        // Dragon breath (Source-X: NPCACT_BREATH, range 0-8, stam >= 50%, 3s cooldown)
        if (npc.NpcBrain == NpcBrainType.Dragon && dist <= 8
            && npc.Stam >= npc.MaxStam / 2)
        {
            long now = Environment.TickCount64;
            long nextBreath = 0;
            if (npc.TryGetTag("BREATH_CD", out string? cdStr))
                long.TryParse(cdStr, out nextBreath);
            if (now >= nextBreath)
            {
                int breathDmg = GetBreathDamage(npc);
                if (breathDmg > 0)
                {
                    npc.Stam = (short)Math.Max(0, npc.Stam - 10);
                    npc.SetTag("BREATH_CD", (now + 3000).ToString());
                    OnNpcBreath?.Invoke(npc, target, breathDmg);
                    return;
                }
            }
        }

        // Object throwing (Source-X: NPCACT_THROWING, range 2-8, stam >= 50%)
        if (dist >= 2 && dist <= 8 && npc.Stam >= npc.MaxStam / 2
            && npc.TryGetTag("THROWOBJ", out _))
        {
            int throwDmg = Math.Max(1, npc.Dex / 4 + _rand.Next(npc.Dex / 4 + 1));
            int throwMin = 2, throwMax = 8;
            if (npc.TryGetTag("THROWRANGE", out string? trStr) && !string.IsNullOrWhiteSpace(trStr))
            {
                var parts = trStr.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int mn) && int.TryParse(parts[1], out int mx))
                { throwMin = mn; throwMax = mx; }
                else if (int.TryParse(parts[0], out int single))
                    throwMax = single;
            }
            if (npc.TryGetTag("THROWDAM", out string? tdStr) && !string.IsNullOrWhiteSpace(tdStr))
            {
                var parts = tdStr.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int lo) && int.TryParse(parts[1], out int hi))
                    throwDmg = lo + _rand.Next(hi - lo + 1);
                else if (int.TryParse(parts[0], out int flat))
                    throwDmg = flat;
            }
            if (dist >= throwMin && dist <= throwMax)
            {
                npc.Stam = (short)Math.Max(0, npc.Stam - _rand.Next(4, 11));
                OnNpcThrow?.Invoke(npc, target, throwDmg);
                return;
            }
        }

        // NPC spellcasting (Source-X: NPC_FightMagery)
        if (TryNpcCastSpell(npc, target, dist))
            return;

        // Melee / ranged
        if (dist <= GetAttackRange(npc))
            TrySwingAttack(npc, target);
        else
            MoveToward(npc, target.Position);
    }

    /// <summary>Source-X: NPC_Act_Flee — step-counted retreat.</summary>
    private void ActFlee(Character npc, Character target)
    {
        npc.FleeStepsCurrent++;
        if (npc.FleeStepsCurrent >= npc.FleeStepsMax)
        {
            npc.FleeStepsCurrent = 0;
            npc.FightTarget = Serial.Invalid;
            return;
        }
        MoveAway(npc, target.Position);
    }

    /// <summary>
    /// Source-X: NPC_FightMagery — attempt to cast a spell at the target.
    /// Requires INT >= 5, spell list on NPC, distance 3+ (kiting), mana available.
    /// </summary>
    private bool TryNpcCastSpell(Character npc, Character target, int dist)
    {
        if (npc.Int < 5 || npc.NpcSpells.Count == 0)
            return false;
        if (dist < 3 || dist > GetNpcSight(npc))
            return false;

        // NoMagic region check
        var region = _world.FindRegion(npc.Position);
        if (region != null && region.NoMagic)
            return false;

        // Mana-based cast chance (Source-X formula)
        int mana = npc.Mana;
        int intStat = npc.Int;
        int chance = mana >= intStat / 2 ? mana : intStat - mana;
        if (_rand.Next(chance + 1) < intStat / 4)
        {
            // Failed chance — but if mana is decent, kite instead
            if (mana > intStat / 3)
            {
                if (dist < 4)
                    MoveAway(npc, target.Position);
                else if (dist > 8)
                    MoveToward(npc, target.Position);
                return true;
            }
            return false;
        }

        // Pick a random spell from the NPC's list
        int startIdx = _rand.Next(npc.NpcSpells.Count);
        for (int i = 0; i < npc.NpcSpells.Count; i++)
        {
            var spell = npc.NpcSpells[(startIdx + i) % npc.NpcSpells.Count];
            if (spell == SpellType.None) continue;

            OnNpcCastSpell?.Invoke(npc, target, spell);
            return true;
        }

        return false;
    }

    /// <summary>Source-X: BREATH.DAM — defaults to STR*5/100, clamped 1-200.</summary>
    private static int GetBreathDamage(Character npc)
    {
        if (npc.TryGetTag("BREATH.DAM", out string? dmgStr) && int.TryParse(dmgStr, out int custom))
            return Math.Clamp(custom, 1, 500);
        int dmg = npc.Str * 5 / 100;
        return Math.Clamp(dmg, 1, 200);
    }

    /// <summary>
    /// Find the best target in sight range by motivation score.
    /// Source-X: NPC_LookAround → NPC_LookAtCharMonster loop.
    /// </summary>
    private (Character? target, int motivation) FindBestTarget(Character npc, int sightRange)
    {
        Character? bestTarget = null;
        int bestMotivation = 0;

        foreach (var ch in _world.GetCharsInRange(npc.Position, sightRange))
        {
            if (ch == npc || !IsAttackable(ch)) continue;
            int motivation = GetAttackMotivation(npc, ch);
            if (motivation > bestMotivation)
            {
                bestMotivation = motivation;
                bestTarget = ch;
            }
        }

        return (bestTarget, bestMotivation);
    }

    /// <summary>
    /// Source-X: NPC_GetAttackMotivation — computes how much this NPC wants to attack target.
    /// Returns &lt;0 to flee, 0 for no interest, &gt;0 to attack.
    /// </summary>
    private int GetAttackMotivation(Character npc, Character target)
    {
        var region = _world.FindRegion(target.Position);
        if (region != null && region.IsFlag(RegionFlag.Safe))
            return 0;

        int hostility = GetHostilityLevel(npc, target);
        if (hostility <= 0)
            return hostility;

        if (npc.NpcBrain == NpcBrainType.Berserk || npc.NpcBrain == NpcBrainType.Guard)
            return 100;

        int motivation = hostility;

        // Bonus for current target (Source-X: +10)
        if (npc.FightTarget == target.Uid)
            motivation += 10;

        // Distance penalty
        motivation -= npc.Position.GetDistanceTo(target.Position);

        // Fear: flee if HP is low (Source-X: MonsterFear + STR check)
        if (_config.MonsterFear && npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 2)
            motivation -= 50 + (npc.Int / 16);

        return motivation;
    }

    /// <summary>
    /// Source-X: NPC_GetHostilityLevelToward — base hostility by creature type.
    /// 100=extreme hatred, 0=neutral, -100=love.
    /// </summary>
    private int GetHostilityLevel(Character npc, Character target)
    {
        // If target is a pet, evaluate hostility toward its owner
        if (target.OwnerSerial.IsValid)
        {
            var owner = target.ResolveOwnerCharacter();
            if (owner != null && owner != npc)
                return GetHostilityLevel(npc, owner);
        }

        // Players and berserk always hostile
        if (target.IsPlayer || npc.NpcBrain == NpcBrainType.Berserk)
            return 100;

        // NPC vs NPC
        if (!target.IsPlayer)
        {
            if (!_config.MonsterFight)
                return 0;

            // Same body type → never attack own kind
            if (npc.BodyId == target.BodyId)
                return -100;

            // Same brain type → mild alliance
            if (npc.NpcBrain == target.NpcBrain)
                return -30;

            return 100;
        }

        return 0;
    }

    /// <summary>
    /// Source-X: Fight_IsAttackable — checks if a character can be targeted.
    /// </summary>
    private static bool IsAttackable(Character ch)
    {
        if (ch.IsDead) return false;
        if (ch.IsStatFlag(StatFlag.Invul)) return false;
        if (ch.IsStatFlag(StatFlag.Stone)) return false;
        if (ch.IsStatFlag(StatFlag.Invisible)) return false;
        if (ch.IsStatFlag(StatFlag.Hidden)) return false;
        if (ch.IsStatFlag(StatFlag.Insubstantial)) return false;
        return true;
    }

    /// <summary>
    /// NPC sight range. Source-X uses a per-NPC GetSight() value (typically 14-16).
    /// We use INT-based range: smarter monsters see further.
    /// </summary>
    private static int GetNpcSight(Character npc)
    {
        int baseRange = 10 + Math.Clamp(npc.Int / 20, 0, 8);
        return Math.Min(baseRange, 18);
    }

    /// <summary>Source-X: SoundChar — emit a creature sound via callback.</summary>
    private void EmitSound(Character npc, CreatureSoundType type)
    {
        OnNpcSound?.Invoke(npc, type);
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

    /// <summary>Vendor/Banker/Stable: stay near home, barely move.</summary>
    private void ActVendor(Character npc)
    {
        if (!npc.TryGetTag("HOME_X", out string? hx) || !npc.TryGetTag("HOME_Y", out string? hy))
            return;
        if (!short.TryParse(hx, out short homeX) || !short.TryParse(hy, out short homeY))
            return;

        sbyte homeZ = npc.Z;
        if (npc.TryGetTag("HOME_Z", out string? hz))
            sbyte.TryParse(hz, out homeZ);

        var home = new Point3D(homeX, homeY, homeZ, npc.MapIndex);
        int dist = npc.Position.GetDistanceTo(home);
        if (dist > 3)
        {
            MoveToward(npc, home);
            return;
        }

        if (_rand.Next(100) < 3)
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

        if (_rand.Next(12) == 0)
            EmitSound(npc, CreatureSoundType.Idle);
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
                if (npc.FightTarget.IsValid)
                {
                    var current = _world.FindChar(npc.FightTarget);
                    if (current != null && !current.IsDead && !current.IsDeleted && IsAttackable(current))
                    {
                        ActFight(npc, current, 50);
                        return;
                    }
                    npc.FightTarget = Serial.Invalid;
                }
                // Master'ın saldırdığı hedefe otomatik katıl
                if (master.FightTarget.IsValid)
                {
                    var masterTarget = _world.FindChar(master.FightTarget);
                    if (masterTarget != null && !masterTarget.IsDead && IsAttackable(masterTarget) && masterTarget != npc)
                    {
                        npc.FightTarget = masterTarget.Uid;
                        ActFight(npc, masterTarget, 50);
                        return;
                    }
                }
                foreach (var ch in _world.GetCharsInRange(guardTarget.Position, 6))
                {
                    if (ch == npc || ch == guardTarget || ch.IsDead || !IsAttackable(ch)) continue;
                    if (ch.FightTarget == guardTarget.Uid)
                    {
                        npc.FightTarget = ch.Uid;
                        ActFight(npc, ch, 50);
                        return;
                    }
                }
                int guardDist = npc.Position.GetDistanceTo(guardTarget.Position);
                if (guardDist > 3)
                    MoveToward(npc, guardTarget.Position);
                break;
            }
            case PetAIMode.Attack:
            {
                Character? target = ResolvePetTargetCharacter(npc, "ATTACK_TARGET");
                if (target == null && master.FightTarget.IsValid)
                    target = _world.FindChar(master.FightTarget);
                if (target == null && npc.FightTarget.IsValid)
                    target = _world.FindChar(npc.FightTarget);
                if (target != null && !target.IsDead && IsAttackable(target))
                {
                    npc.FightTarget = target.Uid;
                    int motivation = GetAttackMotivation(npc, target);
                    ActFight(npc, target, Math.Max(motivation, 50));
                    return;
                }
                npc.FightTarget = Serial.Invalid;
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

    /// <summary>Callback: NPC casts a spell. Parameters: caster, target, spell.
    /// Program.cs handles SpellEngine.CastStart + broadcast.</summary>
    public Action<Character, Character, SpellType>? OnNpcCastSpell { get; set; }

    /// <summary>Callback: dragon breath attack. Parameters: npc, target, damage.</summary>
    public Action<Character, Character, int>? OnNpcBreath { get; set; }

    /// <summary>Callback: NPC throws object. Parameters: npc, target, damage.</summary>
    public Action<Character, Character, int>? OnNpcThrow { get; set; }

    /// <summary>Callback: creature sound. Parameters: npc, sound type.
    /// Program.cs resolves body-specific sound ID and broadcasts 0x54.</summary>
    public Action<Character, CreatureSoundType>? OnNpcSound { get; set; }

    /// <summary>Callback: wake an NPC for immediate action (e.g. retaliation).
    /// Program.cs reschedules the NPC in the timer wheel so it acts next tick.</summary>
    public Action<Character>? OnWakeNpc { get; set; }

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
            EmitSound(npc, CreatureSoundType.Hit);
            if (!target.IsPlayer)
                EmitSound(target, CreatureSoundType.GetHit);
            OnNpcAttack?.Invoke(npc, target, damage);

            // Retaliation: NPC targets that aren't already fighting back
            // acquire the attacker as their fight target (Source-X parity).
            if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
            {
                target.FightTarget = npc.Uid;
                target.NextNpcActionTime = 0;
                OnWakeNpc?.Invoke(target);
            }

            if (target.Hits <= 0 && !target.IsDead)
            {
                if (!target.IsPlayer)
                    EmitSound(target, CreatureSoundType.Die);
                OnNpcKill?.Invoke(npc, target);
            }
        }

        // Reactive armor reflect may have killed the attacker
        if (npc.Hits < hpBefore && npc.Hits <= 0 && !npc.IsDead)
        {
            EmitSound(npc, CreatureSoundType.Die);
            OnNpcKill?.Invoke(npc, npc);
        }
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
        var dir = npc.Position.GetDirectionTo(target);
        GetDirectionDelta(dir, out short dx, out short dy);
        dir |= Direction.Running;

        short nx = (short)(npc.X + dx);
        short ny = (short)(npc.Y + dy);
        var mapData = _world.MapData;
        sbyte nz = mapData?.GetEffectiveZ(npc.MapIndex, nx, ny, npc.Z) ?? npc.Z;
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var directPos = new Point3D(nx, ny, nz, npc.MapIndex);

        bool directBlocked = false;
        if (mapData != null && !mapData.IsPassable(directPos.Map, directPos.X, directPos.Y, directPos.Z))
            directBlocked = true;
        if (!directBlocked)
        {
            foreach (var item in _world.GetItemsInRange(directPos, 0))
            {
                if (item.IsStaticBlock) { directBlocked = true; break; }
            }
        }

        if (!directBlocked)
        {
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
        GetDirectionDelta(dir, out short dx, out short dy);

        short nx = (short)(npc.X - dx);
        short ny = (short)(npc.Y - dy);
        var mapData = _world.MapData;
        sbyte nz = mapData?.GetEffectiveZ(npc.MapIndex, nx, ny, npc.Z) ?? npc.Z;
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
        if (mapData != null && !mapData.IsPassable(newPos.Map, newPos.X, newPos.Y, newPos.Z))
            return;

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
