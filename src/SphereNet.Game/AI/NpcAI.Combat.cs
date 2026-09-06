// Fight execution: monster brain, ActFight, swings, flee, flanking.
// Decomposed from the former single-file NpcAI.cs (see NpcAI.cs core).
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Messages;
using SphereNet.Game.World;

namespace SphereNet.Game.AI;

public sealed partial class NpcAI
{
    /// <summary>Source-X NPC_OnTickAction: an engaged fight (the FightTarget
    /// assigned when someone attacks the NPC) continues REGARDLESS of brain —
    /// service NPCs defend themselves instead of ignoring their attacker, and
    /// animals fight back rather than only shying away. Fear still routes to
    /// flee via ActFight (negative motivation). Returns true when this tick
    /// was spent fighting or fleeing the assigned target.</summary>
    private bool TryFightAssignedTarget(Character npc)
    {
        if (!npc.FightTarget.IsValid)
            return false;
        var target = _world.FindChar(npc.FightTarget);
        if (target == null || target.IsDead || target.IsDeleted ||
            target.MapIndex != npc.MapIndex || !IsAttackable(target))
        {
            npc.FightTarget = Serial.Invalid;
            return false;
        }
        int motivation = GetAttackMotivation(npc, target);
        if (motivation == 0)
        {
            // Neutral score, but the target was assigned by an actual attack:
            // a service NPC still defends itself at minimum effort rather
            // than standing there taking hits.
            motivation = 1;
        }
        ActFight(npc, target, motivation);
        return true;
    }

    /// <summary>
    /// Monster/Dragon: look for targets to attack, fight, or wander.
    /// Source-X: NPC_Act_Idle → NPC_LookAround → NPC_LookAtCharMonster + NPC_Act_Fight.
    /// </summary>
    private void ActMonster(Character npc)
    {
        int sightRange = GetNpcSight(npc);

        // Forced target override (bard provoke / scripted constant focus) takes
        // priority over normal target selection.
        if (TryForcedTarget(npc))
            return;

        // If we have an existing target, check if it's still valid
        if (npc.FightTarget.IsValid)
        {
            var current = _world.FindChar(npc.FightTarget);
            if (current != null && !current.IsDead && !current.IsDeleted && IsAttackable(current))
            {
                // Remember where the target was while we can see it, and clear
                // any hidden-pursuit state.
                npc.SetTag("LAST_TGT_LOC", $"{current.X},{current.Y},{current.Z},{current.MapIndex}");
                npc.RemoveTag("HIDE_PURSUIT");

                int curMotivation = GetAttackMotivation(npc, current);
                if (curMotivation > 0)
                {
                    // Mid-fight better-target rescan. Source-X keeps this
                    // DISABLED ("probably unnecessary… breaks the @NPCActFight
                    // trigger") — run it only under multi-attacker pressure,
                    // where the THREAT/tank switch actually needs it.
                    if (npc.Attackers.Count > 1 && _rand.Next(4) == 0)
                    {
                        var (betterTarget, betterMotivation) = FindBestTarget(npc, sightRange);
                        if (betterTarget != null && !betterTarget.IsDeleted && betterTarget != current && betterMotivation > curMotivation)
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
                if (curMotivation < 0)
                {
                    // Negative motivation = fear: flee from the target instead of
                    // silently dropping it (Source-X NPC_LookAtChar fear path).
                    // ActFight routes motivation < 0 into ActFlee.
                    ActFight(npc, current, curMotivation);
                    return;
                }
                // motivation == 0: neutral — let go of the target.
            }
            else if (current != null && !current.IsDead && !current.IsDeleted &&
                     (current.IsStatFlag(StatFlag.Hidden) || current.IsStatFlag(StatFlag.Invisible)))
            {
                // Target hid — don't give up instantly (ServUO reveal behavior):
                // try to reveal it, or move to its last known spot for a few ticks.
                if (PursueHiddenTarget(npc, current))
                    return;
            }
            npc.FightTarget = Serial.Invalid;
            npc.RemoveTag("HIDE_PURSUIT");
            npc.RemoveTag("LAST_TGT_LOC");
        }

        // No current target — scan for a new one, but throttle the full-range
        // scan so idle NPCs don't sweep every tick (ModernUO ReacquireDelay).
        // RecordAttack zeroes NextNpcReacquireTime so retaliation is immediate.
        long nowReac = Environment.TickCount64;
        if (nowReac >= npc.NextNpcReacquireTime)
        {
            var (bestTarget, bestMotivation) = FindBestTarget(npc, sightRange);
            if (bestTarget != null && bestMotivation > 0)
            {
                npc.NextNpcReacquireTime = 0;
                npc.FightTarget = bestTarget.Uid;
                npc.Memory_Fight_Start(bestTarget);
                EmitSound(npc, CreatureSoundType.Notice);
                NotifyNearbyAllies(npc, bestTarget);
                ActFight(npc, bestTarget, bestMotivation);
                return;
            }
            // Nothing found — back off the next scan.
            npc.NextNpcReacquireTime = nowReac + ReacquireDelayMs;
        }

        npc.FightTarget = Serial.Invalid;

        // No enemy — looters scavenge nearby corpses (Source-X NPC_AI_LOOTING).
        if ((GetNpcFlags(npc).HasFlag(NpcAIFlags.Looting) || npc.TryGetTag("LOOTING", out _))
            && TryLoot(npc))
            return;

        // Idle monsters notice desirable ground items too (Source-X
        // NPC_LookAtItem runs for every brain, not just humans).
        LookAtNearbyItems(npc);

        if (_rand.Next(8) == 0)
            EmitSound(npc, _rand.Next(2) == 0 ? CreatureSoundType.Idle : CreatureSoundType.Notice);
        WanderHome(npc);
    }

    /// <summary>Looter NPCs walk to a nearby corpse with contents and take one
    /// item into their pack (Source-X NPC_Act_Looting). Empty corpses are
    /// skipped, so no separate loot-memory is needed. Returns true if busy.</summary>
    private bool TryLoot(Character npc)
    {
        if (npc.Backpack == null) return false;
        if (npc.Backpack.Contents.Count >= Item.MaxContainerItems) return false;
        // Source-X looting guards: hands required, and never inside guarded
        // or safe territory. (Summons leave no corpse in this engine, so the
        // reference's summon-corpse exclusion has nothing to act on.)
        var lootCan = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.Can ?? CanFlags.None;
        if ((lootCan & CanFlags.C_UseHands) == 0) return false;
        if (IsProtectedGround(npc.Position)) return false;

        Item? corpse = null;
        int best = int.MaxValue;
        foreach (var it in _world.GetItemsInRange(npc.Position, 4))
        {
            if (it.IsDeleted || it.ItemType != ItemType.Corpse || it.Contents.Count == 0) continue;
            if (!_world.CanSeeLOS(npc.Position, it.Position)) continue;
            int d = npc.Position.GetDistanceTo(it.Position);
            if (d < best) { best = d; corpse = it; }
        }
        if (corpse == null) return false;

        if (best > 1)
        {
            MoveToward(npc, corpse.Position);
            return true;
        }

        // Adjacent — grab one random item. @NPCLookAtItem sees ARGN1=dist,
        // ARGN2=want (seeded 100 — the engine already decided to loot; a
        // script may lower it below the roll to skip, RETURN 1 to take over
        // or RETURN 0 to leave the piece alone).
        if (corpse.Contents.Count > 0)
        {
            var loot = corpse.Contents[_rand.Next(corpse.Contents.Count)];
            int want = 100;
            if (OnNpcLookAtItem != null && !IsLookAtItemExcluded(loot))
            {
                var d = OnNpcLookAtItem(npc, loot, best, 100);
                if (d.Handled || d.Ignore)
                    return true;
                want = d.Want;
            }
            if (want > _rand.Next(100))
            {
                corpse.RemoveItem(loot);
                if (!npc.Backpack.TryAddItem(loot))
                    corpse.TryAddItem(loot);
            }
        }
        return true;
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
            if (!_world.CanSeeLOS(npc.Position, ch.Position)) continue;
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
    /// Alert nearby same-type NPCs to join the fight. Source-X parity:
    /// monsters of the same body type rally when one detects a threat.
    /// </summary>
    private void NotifyNearbyAllies(Character npc, Character target)
    {
        foreach (var ally in _world.GetCharsInRange(npc.Position, 8))
        {
            if (ally == npc || ally.IsPlayer || ally.IsDead || ally.IsDeleted) continue;
            if (ally.FightTarget.IsValid) continue;
            if (ally.NpcMaster.IsValid) continue;
            if (ally.BodyId != npc.BodyId) continue;
            if (ally.NpcBrain is not (NpcBrainType.Monster or NpcBrainType.Dragon or NpcBrainType.Berserk))
                continue;

            ally.FightTarget = target.Uid;
            ally.NextNpcActionTime = 0;
            OnWakeNpc?.Invoke(ally);
        }
    }

    /// <summary>
    /// Shared fight action. Source-X: NPC_Act_Fight — flee / special / spell / archery / melee.
    /// </summary>
    private void ActFight(Character npc, Character target, int motivation)
    {
        if (npc.MapIndex != target.MapIndex)
        {
            npc.FightTarget = Serial.Invalid;
            return;
        }

        // A committed swing is the NPC's action until its hit phase resolves.
        // Otherwise a winding-up NPC can cast, breathe, throw, move or
        // sidestep before the pending hit lands.
        if (npc.HasPendingHit)
        {
            long pendingNow = Environment.TickCount64;
            if (pendingNow >= npc.SwingHitTime)
                ResolveNpcHit(npc, pendingNow);
            return;
        }

        // Source-X NPC_Act_Fight fSkipHardcoded: a script can keep the engine's
        // flee/magery/melee but bypass the hardcoded breath/throw specials by
        // setting LOCAL.SKIPHARDCODED=1 in @NPCActFight (distinct from RETURN 1,
        // which suppresses the whole action).
        bool skipHardcoded = false;
        if (OnNpcActFight != null)
        {
            int dist0 = npc.Position.GetDistanceTo(target.Position);
            var decision = OnNpcActFight(npc, target, dist0, motivation);
            if (decision.Handled)
                return; // RETURN 1 — script fully handled the fight action
            // LOCAL.skill + LOCAL.spell forced cast (Source-X magic-skill path):
            // a forced spell is cast at the target; a forced non-cast skill yields
            // this action to the script without the engine's default combat.
            if (decision.ForcedSpell != SpellType.None)
            {
                if (CastViaTrigger(npc, target, decision.ForcedSpell))
                    return;
            }
            if (decision.ForcedSkill != SkillType.None)
                return;
            skipHardcoded = decision.SkipHardcoded;
            motivation = decision.Motivation; // ARGN2 readback (may flip to flee)
        }

        // Source-X: flee when motivation < 0 (non-pets only)
        if (!npc.IsStatFlag(StatFlag.Pet) && motivation < 0)
        {
            npc.FleeStepsMax = 20; // Source-X CCharNPCAct.cpp:412 (m_atFlee.m_iStepsMax)
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

        // Home leash: a wild (non-pet, masterless) monster that has chased its
        // target too far from home gives up and walks back, instead of trailing
        // a player across the whole map. Pets/summons follow their master and
        // are exempt; NPCs without a home anchor can't leash.
        if (!npc.IsStatFlag(StatFlag.Pet) && !npc.NpcMaster.IsValid &&
            TryResolveHome(npc, out Point3D leashHome, out int leashWander))
        {
            int homeDist = npc.MapIndex == leashHome.Map
                ? npc.Position.GetDistanceTo(leashHome)
                : int.MaxValue;
            int leash = Math.Max(leashWander * 2, 18);
            if (homeDist > leash)
            {
                npc.FightTarget = Serial.Invalid;
                ClearLosFailCount(npc);
                // Severely lost — way past the leash radius: teleport straight
                // home (Source-X lost-NPC go-home behaviour). @NPCLostTeleport
                // fires first; RETURN 1 cancels the teleport and the NPC walks
                // back instead. Safe to fire here: ActChase runs in the serial
                // ApplyDecision phase.
                if (homeDist > leash * 3 &&
                    Character.OnNpcLostTeleport?.Invoke(npc) != true)
                {
                    _world.MoveCharacter(npc, leashHome);
                    return;
                }
                MoveToward(npc, leashHome, run: true);
                return;
            }
        }

        // Past every give-up branch (flee, leash) — the NPC is committed to
        // this fight. Announce the engagement once per target so the victim
        // sees the aggression while the NPC is still closing in, not only
        // after the first hit lands.
        if (!_lastAttackNotify.TryGetValue(npc.Uid.Value, out uint lastNotified) ||
            lastNotified != target.Uid.Value)
        {
            _lastAttackNotify[npc.Uid.Value] = target.Uid.Value;
            OnNpcAttackNotify?.Invoke(npc, target);
        }

        int dist = npc.Position.GetDistanceTo(target.Position);
        bool hasLOS = _world.CanSeeLOS(npc.Position, target.Position);

        // No line of sight — pathfind around obstacles to reach target
        if (!hasLOS && dist > 1)
        {
            IncrementLosFailCount(npc);
            int losFails = GetLosFailCount(npc);
            // Stuck for a while — a caster that knows Teleport blinks toward the
            // target instead of giving up (ModernUO OnFailedMove smart-AI).
            if (losFails >= 8 && npc.NpcSpells.Contains(SpellType.Teleport)
                && npc.Mana >= npc.Int / 4 && _rand.Next(3) == 0)
            {
                ClearLosFailCount(npc);
                CastViaTrigger(npc, target, SpellType.Teleport);
                return;
            }
            if (losFails > 15)
            {
                npc.FightTarget = Serial.Invalid;
                ClearLosFailCount(npc);
                return;
            }
            MoveToward(npc, target.Position, run: true);
            return;
        }
        ClearLosFailCount(npc);

        // HP-based tactical retreat: melee-only NPCs briefly disengage when
        // critically wounded, then re-engage. Casters already kite via spells.
        if (npc.NpcSpells.Count == 0 && npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 4
            && dist <= 1 && _rand.Next(3) == 0)
        {
            MoveAway(npc, target.Position);
            EmitSound(npc, CreatureSoundType.GetHit);
            return;
        }

        // Random idle combat sound (Source-X: Berserk or 1/6 chance)
        if (npc.NpcBrain == NpcBrainType.Berserk || _rand.Next(6) == 0)
            EmitSound(npc, CreatureSoundType.Idle);

        // Dragon breath: fires for Dragon brain, dragon-family bodies, fire-immune
        // monsters, or explicit BREATH.DAM tag. The body check matches the legacy
        // body-derived creature type: script packs routinely keep
        // BRAIN=brain_monster on dragons and still expect the breath attack.
        bool canBreath = npc.NpcBrain == NpcBrainType.Dragon || IsDragonBody(npc.BodyId);
        if (!canBreath)
        {
            var breathDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
            canBreath = breathDef != null && (breathDef.Can & CanFlags.C_FireImmune) != 0
                        && npc.NpcBrain is NpcBrainType.Monster or NpcBrainType.Dragon or NpcBrainType.Berserk;
        }
        if (!canBreath)
            canBreath = npc.TryGetTag("BREATH.DAM", out _);

        // Source-X NPC_Act_Fight gates breath/throw behind FULL stamina
        // (Stat_GetVal(STAT_DEX) >= Stat_GetAdjusted(STAT_DEX)) so these
        // specials fire on the opening exchange / after a rest, not every tick.
        bool fullStam = npc.MaxStam <= 0 || npc.Stam >= npc.MaxStam;
        // Source-X: iDist >= 1 — no breath onto the overlapping tile.
        if (!skipHardcoded && canBreath && dist >= 1 && dist <= 8 && fullStam && hasLOS)
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

        // Object throwing (Source-X NPCACT_THROWING, range 2-9, full stamina).
        // Throwers are ogre/ettin/cyclops bodies (the hardcoded default rock
        // throwers) or any creature carrying a THROWOBJ tag. A default rock
        // thrower must actually have a throwable rock (ItemType.ARock) in its
        // pack — Source-X ContentFind(IT_AROCK); a THROWOBJ-tagged creature
        // throws on the tag alone (SphereNet flag semantics, THROWDAM/THROWRANGE).
        bool throwObjTag = npc.TryGetTag("THROWOBJ", out _);
        bool defaultThrower = !throwObjTag && IsRockThrowerBody(npc.BodyId) && HasThrowableRock(npc);
        if (!skipHardcoded && dist >= 2 && fullStam && hasLOS && (throwObjTag || defaultThrower))
        {
            // Source-X Skill_Act_Throwing default damage (CCharSkill.cpp:3447).
            int throwDmg = Math.Max(1, npc.Dex / 4 + _rand.Next(npc.Dex / 4 + 1));
            int throwMin = 2, throwMax = 9;
            if (npc.TryGetTag("THROWRANGE", out string? trStr) && !string.IsNullOrWhiteSpace(trStr))
            {
                var parts = trStr.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int mn) && int.TryParse(parts[1], out int mx))
                {
                    throwMin = Math.Max(0, Math.Min(mn, mx));
                    throwMax = Math.Max(throwMin, Math.Max(mn, mx));
                }
                else if (int.TryParse(parts[0], out int single))
                    throwMax = Math.Max(throwMin, single);
            }
            if (npc.TryGetTag("THROWDAM", out string? tdStr) && !string.IsNullOrWhiteSpace(tdStr))
            {
                var parts = tdStr.Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out int lo) && int.TryParse(parts[1], out int hi))
                {
                    int minDamage = Math.Max(0, Math.Min(lo, hi));
                    int maxDamage = Math.Max(minDamage, Math.Max(lo, hi));
                    throwDmg = minDamage == maxDamage
                        ? minDamage
                        : (int)_rand.NextInt64(minDamage, (long)maxDamage + 1);
                }
                else if (int.TryParse(parts[0], out int flat))
                    throwDmg = Math.Max(0, flat);
            }
            if (dist >= throwMin && dist <= throwMax)
            {
                // Source-X: throwing spends 4 + rand(6) stamina (CCharSkill.cpp:3372).
                npc.Stam = (short)Math.Max(0, npc.Stam - (4 + _rand.Next(6)));
                OnNpcThrow?.Invoke(npc, target, throwDmg);
                return;
            }
        }

        // NPC spellcasting — requires LOS for ranged spells
        if (hasLOS && TryNpcCastSpell(npc, target, dist))
            return;

        // Melee / ranged
        var weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        var range = GetFightRange(npc, weapon);

        // Ranged kiting (Source-X NPC_FightArchery): when the target has closed
        // inside the weapon's minimum range a ranged attacker cannot fire, so
        // back off to reopen the gap (≈50%) instead of standing locked in melee.
        if (range.Max > 1 && dist < range.Min)
        {
            if (_rand.Next(2) == 0)
                MoveAway(npc, target.Position);
            return;
        }

        // COMBAT_SWING_NORANGE: a swing may start even when out of range.
        if (dist <= range.Max || CombatHelper.SwingIgnoresStartRange())
        {
            // Surround/flank sidesteps only happen on ticks where the swing
            // did NOT fire (recoil window). Moving in the same tick as a
            // fired swing makes the client cancel the attack animation —
            // the same move-pair conflict class as the pet GO/follow bug.
            bool swung = TrySwingAttack(npc, target);
            if (!swung && dist <= 1 && _rand.Next(3) == 0)
                TrySurroundStep(npc, target);
        }
        else
        {
            // When closing distance, approach from an open flank if possible
            if (dist <= 3)
                MoveTowardFlank(npc, target);
            else
                MoveToward(npc, target.Position, run: true);
        }
    }

    /// <summary>
    /// Step to an open adjacent tile around the target to surround it.
    /// Picks a random unoccupied neighbor of the target that is still in melee range.
    /// </summary>
    private void TrySurroundStep(Character npc, Character target)
    {
        Span<(short x, short y)> candidates = stackalloc (short, short)[8];
        int count = 0;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                short tx = (short)(target.X + dx);
                short ty = (short)(target.Y + dy);
                if (tx == npc.X && ty == npc.Y) continue;

                // A surround step must be a single tile from the NPC's current
                // position. Tiles around the target on the far side are 2 tiles
                // away from the NPC; moving there in one step is not a walk the
                // client can animate, so it teleports the NPC. Only keep tiles
                // adjacent (Chebyshev <= 1) to the NPC.
                if (Math.Abs(tx - npc.X) > 1 || Math.Abs(ty - npc.Y) > 1)
                    continue;

                bool occupied = false;
                foreach (var ch in _world.GetCharsInRange(new Point3D(tx, ty, target.Z, target.MapIndex), 0))
                {
                    if (!ch.IsDead && ch != target) { occupied = true; break; }
                }
                if (!occupied)
                    candidates[count++] = (tx, ty);
            }
        }

        if (count == 0) return;

        var pick = candidates[_rand.Next(count)];
        var mapData = _world.MapData;
        sbyte nz = ResolveNpcStepZ(npc, pick.x, pick.y);
        if (Math.Abs(nz - npc.Z) > 12) return;
        var pos = new Point3D(pick.x, pick.y, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, pos)) return;

        // Face the step direction before moving. The 0x77 move packet carries this
        // direction; if it doesn't match the actual tile delta the client can't
        // walk-animate and snaps the NPC ("1-tile teleport" during combat
        // surround steps). The next swing re-faces the target via TrySwingAttack.
        npc.Direction = npc.Position.GetDirectionTo(pos);
        _world.MoveCharacter(npc, pos);
    }

    /// <summary>
    /// Move toward the target but prefer an unoccupied flank direction.
    /// </summary>
    private void MoveTowardFlank(Character npc, Character target)
    {
        // Check which sides of the target are already occupied by allies
        var dir = npc.Position.GetDirectionTo(target.Position);
        int dirInt = (int)dir & 0x07;

        // Try clockwise and counter-clockwise rotations to find an open approach
        for (int rot = 0; rot <= 2; rot++)
        {
            foreach (int sign in rot == 0 ? new[] { 0 } : new[] { 1, -1 })
            {
                int tryDir = (dirInt + sign * rot) & 0x07;
                GetDirectionDelta((Direction)tryDir, out short dx, out short dy);
                short adjX = (short)(target.X - dx);
                short adjY = (short)(target.Y - dy);

                bool occupied = false;
                foreach (var ch in _world.GetCharsInRange(
                    new Point3D(adjX, adjY, target.Z, target.MapIndex), 0))
                {
                    if (!ch.IsDead && ch != npc && ch != target) { occupied = true; break; }
                }

                if (!occupied)
                {
                    var approachPos = new Point3D(adjX, adjY, target.Z, target.MapIndex);
                    MoveToward(npc, approachPos, run: true);
                    return;
                }
            }
        }

        // All flanks occupied, just go direct
        MoveToward(npc, target.Position, run: true);
    }

    private readonly Dictionary<uint, int> _losFailCounts = [];

    private void IncrementLosFailCount(Character npc)
    {
        _losFailCounts.TryGetValue(npc.Uid.Value, out int count);
        _losFailCounts[npc.Uid.Value] = count + 1;
    }

    private int GetLosFailCount(Character npc)
    {
        _losFailCounts.TryGetValue(npc.Uid.Value, out int count);
        return count;
    }

    private void ClearLosFailCount(Character npc)
    {
        _losFailCounts.Remove(npc.Uid.Value);
    }

    /// <summary>Source-X: NPC_Act_Flee — step-counted retreat with kiting spellcast.</summary>
    private void ActFlee(Character npc, Character target)
    {
        npc.FleeStepsCurrent++;
        if (npc.FleeStepsCurrent >= npc.FleeStepsMax)
        {
            npc.FleeStepsCurrent = 0;
            npc.FightTarget = Serial.Invalid;
            return;
        }

        int dist = npc.Position.GetDistanceTo(target.Position);

        // Kiting: cast a spell while fleeing if mana allows (every 3rd step)
        if (npc.NpcSpells.Count > 0 && npc.Mana >= npc.Int / 3
            && dist >= 2 && dist <= 8
            && npc.FleeStepsCurrent % 3 == 0
            && _world.CanSeeLOS(npc.Position, target.Position))
        {
            var (spell, castTarget) = ChooseBestSpell(npc, target, dist);
            if (spell != SpellType.None && CastViaTrigger(npc, castTarget, spell))
                return;
        }

        // Self-heal while fleeing (every 4th step)
        if (npc.NpcSpells.Count > 0 && npc.MaxHits > 0 && npc.Hits < npc.MaxHits / 3
            && npc.FleeStepsCurrent % 4 == 0)
        {
            if (npc.NpcSpells.Contains(SpellType.GreaterHeal))
            {
                if (CastViaTrigger(npc, npc, SpellType.GreaterHeal))
                    return;
            }
            else if (npc.NpcSpells.Contains(SpellType.Heal))
            {
                if (CastViaTrigger(npc, npc, SpellType.Heal))
                    return;
            }
        }

        // Scout retreat: a creature that can hide vanishes mid-flee once it has
        // some distance, breaking pursuit (ServUO OrcScout guerilla style). The
        // pursuer then loses LOS and falls into hidden-target pursuit.
        if (dist >= 4 && !npc.IsStatFlag(StatFlag.Hidden)
            && npc.GetSkill(SkillType.Hiding) > 0 && _rand.Next(8) == 0)
        {
            npc.SetStatFlag(StatFlag.Hidden);
            npc.FleeStepsCurrent = 0;
            npc.FightTarget = Serial.Invalid;
            return;
        }

        // Pathfinder-based escape: find a direction away from threat
        FleeAway(npc, target.Position);
    }

    /// <summary>Honor a forced combat target: bard Provocation (PROVOKED_TARGET)
    /// or a scripted ConstantFocus (CONSTANT_FOCUS). Returns true if engaged.</summary>
    private static bool TryReadUidTag(Character npc, string name, out uint uid)
    {
        uid = 0;
        if (!npc.TryGetTag(name, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return false;
        uid = SphereNet.Game.Objects.ObjBase.ParseHexOrDecUInt(raw);
        return uid != 0;
    }

    private bool TryForcedTarget(Character npc)
    {
        Serial uid = Serial.Invalid;
        bool isProvoke = false;
        if (TryReadUidTag(npc, "PROVOKED_TARGET", out uint pu))
        {
            uid = new Serial(pu);
            isProvoke = true;
        }
        else if (TryReadUidTag(npc, "CONSTANT_FOCUS", out uint cu))
        {
            uid = new Serial(cu);
        }
        if (!uid.IsValid) return false;

        var t = _world.FindChar(uid);
        if (t == null || t.IsDead || t.IsDeleted || t == npc ||
            t.MapIndex != npc.MapIndex || !IsAttackable(t))
        {
            if (isProvoke) npc.RemoveTag("PROVOKED_TARGET"); // expired/dead provoke clears
            return false;
        }
        npc.FightTarget = t.Uid;
        ActFight(npc, t, 100);
        return true;
    }

    private void FleeAway(Character npc, Point3D threat)
    {
        // Run once there is room; walk while cornered (reference
        // NPC_Act_Follow flee path: NPC_WalkToPoint(iDist > 3)).
        bool run = npc.Position.GetDistanceTo(threat) > 3;
        // Try the direct opposite direction first
        var dir = npc.Position.GetDirectionTo(threat);
        GetDirectionDelta(dir, out short dx, out short dy);

        short nx = (short)(npc.X - dx);
        short ny = (short)(npc.Y - dy);
        var mapData = _world.MapData;
        sbyte nz = ResolveNpcStepZ(npc, nx, ny);

        if (Math.Abs(nz - npc.Z) <= 12)
        {
            var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
            if (CanNpcMoveTo(npc, newPos))
            {
                var fleeDir = npc.Position.GetDirectionTo(newPos);
                npc.Direction = run ? fleeDir | Direction.Running : fleeDir;
                _world.MoveCharacter(npc, newPos);
                return;
            }
        }

        // Direct blocked — try two diagonal alternatives
        for (int rot = 1; rot <= 2; rot++)
        {
            foreach (int sign in new[] { 1, -1 })
            {
                int altDir = (((int)dir & 0x07) + sign * rot) & 0x07;
                GetDirectionDelta((Direction)altDir, out short adx, out short ady);
                short ax = (short)(npc.X - adx);
                short ay = (short)(npc.Y - ady);
                sbyte az = ResolveNpcStepZ(npc, ax, ay);
                if (Math.Abs(az - npc.Z) > 12) continue;
                var altPos = new Point3D(ax, ay, az, npc.MapIndex);
                if (CanNpcMoveTo(npc, altPos))
                {
                    var altFleeDir = npc.Position.GetDirectionTo(altPos);
                    npc.Direction = run ? altFleeDir | Direction.Running : altFleeDir;
                    _world.MoveCharacter(npc, altPos);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Callback for when an NPC successfully deals damage. Used by Program.cs to broadcast effects.
    /// Parameters: attacker, target, damage dealt
    /// </summary>
    public Action<Character, Character, Item?, int, uint>? OnNpcAttack { get; set; }

    /// <summary>
    /// Callback for when an NPC engages a new fight target (reference
    /// Attacker_Add). Fires before the first swing — at engage, while the NPC
    /// may still be closing in — so Program.cs can broadcast the
    /// "*X is attacking Y!*" emote pair. Parameters: attacker, target.
    /// </summary>
    public Action<Character, Character>? OnNpcAttackNotify { get; set; }

    /// <summary>
    /// Callback for when an NPC kills a target. Used by Program.cs to run DeathEngine + broadcast.
    /// Parameters: killer, victim
    /// </summary>
    public Action<Character, Character>? OnNpcKill { get; set; }

    /// <summary>Callback: dragon breath attack. Parameters: npc, target, damage.</summary>
    public Action<Character, Character, int>? OnNpcBreath { get; set; }

    /// <summary>Callback: NPC throws object. Parameters: npc, target, damage.</summary>
    public Action<Character, Character, int>? OnNpcThrow { get; set; }

    /// <summary>@HitTry hook fired before an NPC swing's recoil is set. Receives
    /// the swing delay in tenths of a second and returns a (possibly modified)
    /// delay, or a negative value to abort the swing this tick. Mirrors the
    /// player path's @HitTry. Args: npc, target, weapon, swingDelayTenths.</summary>
    public Func<Character, Character, Item?, int, int>? OnNpcHitTry { get; set; }

    /// <summary>@HitCheck hook fired before range/LoS validation. Receives and
    /// returns the per-swing Recoil_NoRange value plus a forced-miss decision,
    /// matching the player path's trigger contract.</summary>
    public Func<Character, Character, Item?, bool,
        (bool ForceMiss, bool SwingNoRange)>? OnNpcHitCheck { get; set; }

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
    ///     <item>Resolve damage after the committed windup.</item>
    ///     <item>Set <c>NextAttackTime</c> to the full swing recoil.</item>
    ///   </list>
    /// </summary>
    private bool TrySwingAttack(Character npc, Character target)
    {
        if (npc == target || npc.MapIndex != target.MapIndex ||
            CombatHelper.IsInvalidSwingParticipant(npc, asTarget: false) ||
            CombatHelper.IsInvalidSwingParticipant(target, asTarget: true))
            return false;

        long now = Environment.TickCount64;
        if (now < npc.NextAttackTime)
            return false;
        // A started swing whose hit hasn't landed yet must not be overwritten;
        // the NPC tick pumps the pending hit (resolved inline for atomic swings).
        if (npc.HasPendingHit)
            return false;

        // Same gating Source-X applies to player attackers — see
        // GameClient.TrySwingAt for rationale.
        if (npc.Stam <= 0)
        {
            npc.NextAttackTime = now + 1000;
            return false;
        }
        // COMBAT_PARALYZE_CANSWING (old-sphere): a paralyzed (Freeze) attacker
        // can keep swinging; sleeping always blocks — on either side, since
        // Source-X Fight_CanHit (CCharFight.cpp:1697) answers SWINGING for
        // STATF_SLEEPING on the attacker AND the target.
        bool paralyzeCanSwing = CombatHelper.IsCombatFlagSet(CombatFlags.ParalyzeCanSwing);
        if ((npc.IsStatFlag(StatFlag.Freeze) && !paralyzeCanSwing) ||
            npc.IsStatFlag(StatFlag.Sleeping) ||
            target.IsStatFlag(StatFlag.Sleeping))
        {
            npc.NextAttackTime = now + 500;
            return false;
        }
        if (npc.IsCasting)
        {
            OnNpcTickSpellCast?.Invoke(npc);
            if (npc.IsCasting)
            {
                npc.NextAttackTime = now + 250;
                return false;
            }
        }

        Item? weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        var effectiveRange = GetFightRange(npc, weapon);
        bool swingNoRange = CombatHelper.SwingIgnoresStartRange();
        int swingDelayMs = SphereNet.Game.Clients.GameClient.GetSwingDelayMs(npc, weapon);

        if (OnNpcHitCheck != null)
        {
            var hitCheck = OnNpcHitCheck(npc, target, weapon, swingNoRange);
            swingNoRange = hitCheck.SwingNoRange;
            if (hitCheck.ForceMiss)
            {
                npc.NextAttackTime = now + swingDelayMs;
                OnNpcAttack?.Invoke(npc, target, weapon, CombatEngine.AttackMiss, 0);
                return true;
            }
        }

        var prep = CombatHelper.ValidateSwingPrep(
            _world, npc, target, weapon, PrivLevel.Player, now, _world.CanSeeLOS,
            ignoreRangeLos: swingNoRange, effectiveRange: effectiveRange);
        switch (prep.Result)
        {
            case CombatHelper.SwingPrepResult.Abort:
                npc.FightTarget = Serial.Invalid;
                return false;
            case CombatHelper.SwingPrepResult.RetryLater:
                npc.NextAttackTime = now + Math.Max(prep.RetryMs, 250);
                return false;
        }

        CombatHelper.RevealOnAttack(npc, PrivLevel.Player);

        int stagger = (int)(npc.Uid.Value * 2654435761u % 200);

        // @HitTry (parity with the player path): fires before recoil is set so a
        // script can adjust the swing delay or abort the swing this tick.
        if (OnNpcHitTry != null)
        {
            int tenths = Math.Max(1, swingDelayMs / 100);
            int newTenths = OnNpcHitTry(npc, target, weapon, tenths);
            if (newTenths < 0)
            {
                npc.NextAttackTime = now + 250; // aborted; recheck shortly, no recoil burned
                return false;
            }
            swingDelayMs = Math.Clamp(newTenths, 1, short.MaxValue) * 100;
        }

        var newDir = npc.Position.GetDirectionTo(target.Position);
        if (!CombatHelper.IsCombatFlagSet(CombatFlags.NoDirChange) && newDir != npc.Direction)
        {
            npc.Direction = newDir;
            OnNpcFacingChanged?.Invoke(npc);
        }

        // Two-phase swing windup (Source-X): commit recoil + a pending hit now.
        // A zero windup resolves the hit inline below (the atomic default);
        // STAYINRANGE / SWING_NORANGE defer it to the NPC tick's pending-hit pump.
        int recoilMs = swingDelayMs + stagger;
        int hitDelayMs = CombatHelper.GetSwingHitDelayMs(recoilMs, swingNoRange);
        npc.BeginSwingWindup(now, hitDelayMs, recoilMs, target.Uid,
            now + recoilMs * 2L, weapon != null ? weapon.Uid : Serial.Invalid, swingNoRange,
            effectiveRange.Min, effectiveRange.Max);

        // Source-style owner attribution: a commanded pet/summon attack on an
        // innocent criminal-flags its player owner. Guard response is regional;
        // the criminal flag itself is not.
        if (npc.OwnerSerial.IsValid && target.IsPlayer && Character.AttackingIsACrimeEnabled)
        {
            var owner = npc.ResolveOwnerCharacter();
            if (owner != null && owner.IsPlayer && !owner.IsDead)
            {
                bool targetInnocent = SphereNet.Game.Clients.GameClient.ComputeNotoriety(
                    _world, owner, target) == 1;
                if (targetInnocent &&
                    !CombatHelper.IsCombatFlagSet(CombatFlags.AttackNoAggreived))
                    owner.MakeCriminal();
            }
        }

        if (now >= npc.SwingHitTime)
            ResolveNpcHit(npc, now);
        return true;
    }

    /// <summary>Resolve a started NPC swing's hit (Source-X hit phase). Re-checks
    /// reach/LoS per the combat flags (STAYINRANGE -> miss, SWING_NORANGE -> wait),
    /// fires @HitCheck, runs ResolveAttack and the NPC hit feedback. Called inline
    /// for an atomic swing, or from the NPC tick once the windup elapses.</summary>
    private static Item? FindNpcAmmo(Character npc, Item weapon)
    {
        // Throwing weapons fire themselves — no pack ammo to find/consume.
        if (CombatHelper.IsThrowingWeapon(weapon)) return null;
        var pack = npc.Backpack;
        if (pack == null) return null;

        var spec = CombatHelper.ResolveAmmoSpec(
            DefinitionLoader.GetItemDef(weapon.BaseId),
            weapon.ItemType,
            Item.ResolveDefName);
        return CombatHelper.FindAmmoInContainer(pack, spec.BaseId, spec.FallbackType);
    }

    private void ConsumeNpcAmmo(Item ammo)
    {
        if (ammo.Amount <= 1)
            _world.RemoveItem(ammo);
        else
            ammo.Amount--;
    }

    private void ResolveNpcHit(Character npc, long now)
    {
        if (!npc.HasPendingHit) return;

        var target = _world.FindChar(npc.PendingHitTarget);
        Serial committedWeaponUid = npc.PendingHitWeapon;
        bool weaponCaptured = npc.PendingHitWeaponCaptured;
        bool swingNoRange = npc.PendingHitSwingNoRange;
        (int Min, int Max)? committedRange =
            npc.PendingHitRangeMin >= 0 && npc.PendingHitRangeMax >= 0
                ? (npc.PendingHitRangeMin, npc.PendingHitRangeMax)
                : null;
        Item? weapon = weaponCaptured
            ? (committedWeaponUid.IsValid ? _world.FindItem(committedWeaponUid) : null)
            : npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        if (weaponCaptured && committedWeaponUid.IsValid && (weapon == null || weapon.IsDeleted))
        {
            npc.ClearPendingHit();
            return;
        }

        switch (CombatHelper.EvaluateHitTime(_world, npc, target, weapon,
            PrivLevel.Player, now, npc.PendingHitDeadline, _world.CanSeeLOS,
            swingNoRange, committedRange))
        {
            case CombatHelper.HitTimeDecision.Wait:
                return; // keep the pending hit; retry next tick
            case CombatHelper.HitTimeDecision.Drop:
                npc.ClearPendingHit();
                return;
            case CombatHelper.HitTimeDecision.Miss:
                npc.ClearPendingHit();
                if (target != null)
                {
                    Item? missedAmmo = weapon != null && CombatHelper.IsRangedWeapon(weapon)
                        ? FindNpcAmmo(npc, weapon)
                        : null;
                    OnNpcAttack?.Invoke(npc, target, weapon, CombatEngine.AttackMiss,
                        missedAmmo?.Uid.Value ?? 0);
                }
                return;
        }

        npc.ClearPendingHit();
        if (target == null) return;

        Item? ammoStack = weapon != null && CombatHelper.IsRangedWeapon(weapon)
            ? FindNpcAmmo(npc, weapon)
            : null;

        short hpBefore = npc.Hits;
        int damage = CombatEngine.ResolveAttack(
            npc, target, weapon, CombatHelper.ActiveCombatFlags,
            -1, -1, ammoStack?.Uid.Value ?? 0, out bool ammoHandled);
        OnNpcAttack?.Invoke(npc, target, weapon, damage, ammoStack?.Uid.Value ?? 0);

        // NPCs remain lenient when templates omit ammo, but a stocked archer
        // consumes the weapon's actual AMMOTYPE (arrow vs bolt/custom). The
        // full combat overload also exposes LOCAL.Arrow and honors a script's
        // LOCAL.ArrowHandled takeover.
        if (ammoStack != null &&
            (damage >= 0 || damage == CombatEngine.AttackResolvedByProc) && !ammoHandled)
            ConsumeNpcAmmo(ammoStack);

        if (damage > 0)
        {
            // Source-X SoundChar(CRESND_HIT): an armed strike makes the weapon
            // sound (emitted by the OnNpcAttack hit feedback), so only fall back to
            // the creature's own attack vocalization when unarmed — otherwise an
            // armed creature would double up (creature sound + weapon sound).
            if (weapon == null)
                EmitSound(npc, CreatureSoundType.Hit);
            // The struck target's get-hit vocalization (creature SOUNDGETHIT or a
            // human "oomf") is emitted by the OnNpcAttack hit feedback so it covers
            // both creature and player targets uniformly.

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
            OnNpcKill?.Invoke(target, npc);
        }
    }

    private static int GetAttackRange(Character npc, Item? weapon = null)
    {
        weapon ??= npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        return GetFightRange(npc, weapon).Max;
    }

    /// <summary>Source-X Fight_CalcRange: effective reach is max(creature
    /// innate RANGE from the chardef, weapon range). The chardef RANGE keys
    /// were parsed but never read anywhere in the Game layer, so reach
    /// creatures (RANGE=2 serpents etc.) closed to 1 tile and forfeited it.</summary>
    internal static (int Min, int Max) GetFightRange(Character npc, Item? weapon)
    {
        var range = CombatHelper.GetWeaponRange(weapon);
        int innate = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.RangeMax ?? 0;
        return innate > range.Max ? (range.Min, innate) : range;
    }

    private void MoveAway(Character npc, Point3D threat)
    {
        var dir = npc.Position.GetDirectionTo(threat);
        GetDirectionDelta(dir, out short dx, out short dy);

        short nx = (short)(npc.X - dx);
        short ny = (short)(npc.Y - dy);
        var mapData = _world.MapData;
        sbyte nz = ResolveNpcStepZ(npc, nx, ny);
        if (Math.Abs(nz - npc.Z) > 12)
            return;
        var newPos = new Point3D(nx, ny, nz, npc.MapIndex);
        if (!CanNpcMoveTo(npc, newPos))
            return;

        npc.Direction = npc.Position.GetDirectionTo(newPos);
        _world.MoveCharacter(npc, newPos);
    }
}
