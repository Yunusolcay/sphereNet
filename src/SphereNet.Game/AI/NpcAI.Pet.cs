// Pet brain: ActPet order modes, hireling wages, pet helpers.
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
    /// <summary>GO-order arrival: Source-X NPCACT_GOTO falls back to Act_Idle,
    /// which re-evaluates and typically resumes the previous order — restore
    /// the mode saved when the order was issued instead of parking in Stay.</summary>
    private static void FinishGoOrder(Character npc)
    {
        npc.RemoveTag("GO_TARGET");
        // PetAIMode is byte-backed, and Enum.IsDefined THROWS when handed a boxed Int32
        // for such an enum rather than answering false. Every GO order issued by the
        // real command - which does record the previous mode - therefore blew up here,
        // leaving the pet stuck in Come with its GO_TARGET already deleted.
        if (npc.TryGetTag("PREV_PET_MODE", out string? prev) &&
            byte.TryParse(prev, out byte prevMode) &&
            Enum.IsDefined(typeof(PetAIMode), prevMode))
        {
            npc.PetAIMode = (PetAIMode)prevMode;
            npc.RemoveTag("PREV_PET_MODE");
        }
        else
        {
            npc.PetAIMode = PetAIMode.Stay;
        }
    }

    /// <summary>Where a follower should head for. A target it can see gives its own
    /// position, and that position is remembered; one it cannot gives the last place it
    /// was seen, and null once there is nothing remembered to walk to.
    ///
    /// Hidden and Invisible are visibility rules of their own - a geometric line of
    /// sight would not answer them - and staff-level concealment stays visible to the
    /// creature that owns the follower, as it is elsewhere in the engine.</summary>
    private static Point3D? ResolveFollowPoint(Character npc, Character target)
    {
        bool concealed = target.IsStatFlag(StatFlag.Hidden) || target.IsStatFlag(StatFlag.Invisible);
        if (!concealed || npc.PrivLevel >= PrivLevel.Counsel)
        {
            npc.SetTag(FollowLastSeenTag, $"{target.X},{target.Y},{target.Z},{target.MapIndex}");
            return target.Position;
        }

        if (!npc.TryGetTag(FollowLastSeenTag, out string? raw) || !TryParsePoint(raw, out var seen))
            return null;
        return seen.Map == npc.MapIndex ? seen : null;
    }

    private const string FollowLastSeenTag = "FOLLOW_LAST_SEEN";

    /// <summary>How close a follower tries to get by default. The reference's own
    /// default is 1; SphereNet has always closed to two and that is what a pack
    /// scripting nothing keeps.</summary>
    private const int PetFollowDistance = 2;

    /// <summary>Pet follow gives up beyond this distance on the same map
    /// (reference parity: UO_MAP_VIEW_RADAR = 36); it resumes when the owner
    /// comes back in range. 0 disables the leash.</summary>
    public static int PetFollowMaxDistance { get; set; } = 36;

    /// <summary>Default hireling pay interval (ms) when HIRE_PERIOD isn't set.</summary>
    private const long DefaultHirePeriodMs = 30 * 60 * 1000; // 30 minutes

    private static bool IsValidPetEnemy(
        Character npc, Character master, Character? target, Character? protectedTarget = null) =>
        target != null && target != npc && target != master && target != protectedTarget &&
        !target.IsDead && !target.IsDeleted && target.MapIndex == npc.MapIndex &&
        !npc.IsFriendOf(target.Uid) && IsAttackable(target);


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

        // Hireling upkeep (Source-X NPC_CheckHirelingStatus): the day wage
        // comes from the CHARDEF (HIREDAYWAGE; legacy HIRE_WAGE tag overrides)
        // and drains the NPC's own PREPAID balance — funded when the player
        // hands it gold (NPC_OnHirePay) — a period-fraction at a time. It
        // never touches the master's live bank; on depletion it speaks the
        // wage-cost/time-up messages and deserts.
        uint dayWage = DefinitionLoader.GetCharDef(npc.CharDefIndex)?.HireDayWage ?? 0;
        if (npc.TryGetTag("HIRE_WAGE", out string? wageStr) &&
            uint.TryParse(wageStr, out uint tagWage) && tagWage > 0)
            dayWage = tagWage;
        if (dayWage > 0)
        {
            long nowMs = Environment.TickCount64;
            long period = npc.TryGetTag("HIRE_PERIOD", out string? ps) &&
                long.TryParse(ps, out long p) && p > 0
                    ? Math.Clamp(p, 1_000, 30L * 24 * 60 * 60 * 1_000)
                    : DefaultHirePeriodMs;
            long nextPay = npc.TryGetTag("HIRE_NEXT_PAY", out string? np) &&
                long.TryParse(np, out long n) ? n : 0;
            if (nextPay == 0)
                npc.SetTag("HIRE_NEXT_PAY", (nowMs + period).ToString());
            else if (nowMs >= nextPay)
            {
                long periodWage = (long)Math.Clamp(
                    (decimal)dayWage * period / 86_400_000m, 1m, long.MaxValue);
                long balance = npc.TryGetTag("HIRE_BALANCE", out string? bs) &&
                    long.TryParse(bs, out long bal) ? bal : 0;
                if (balance >= periodWage)
                {
                    npc.SetTag("HIRE_BALANCE", (balance - periodWage).ToString());
                    npc.SetTag("HIRE_NEXT_PAY", (nowMs + period).ToString());
                }
                else
                {
                    OnNpcSay?.Invoke(npc, ServerMessages.GetFormatted(Msg.NpcPetWageCost, dayWage.ToString()));
                    OnNpcSay?.Invoke(npc, ServerMessages.Get(Msg.NpcPetHireTimeup));
                    npc.RemoveTag("HIRE_NEXT_PAY");
                    npc.RemoveTag("HIRE_BALANCE");
                    npc.ClearOwnership(clearFriends: true);
                    npc.PetAIMode = PetAIMode.Stay;
                    return;
                }
            }
        }

        // Self-defense (Source-X Memory_FightStart): an attacked pet fights
        // back regardless of its order state. Follow/Come/Stay never read
        // FightTarget, so a pet under attack just stood there while the
        // aggressor beat it down. Explicit orders clear FightTarget, so the
        // fight ends when the owner calls the pet off.
        if (npc.PetAIMode is PetAIMode.Follow or PetAIMode.Come or PetAIMode.Stay &&
            npc.FightTarget.IsValid)
        {
            var aggressor = _world.FindChar(npc.FightTarget);
            if (IsValidPetEnemy(npc, master, aggressor))
            {
                ActFight(npc, aggressor!, 50);
                return;
            }
            npc.FightTarget = Serial.Invalid;
        }

        switch (npc.PetAIMode)
        {
            case PetAIMode.Follow:
            case PetAIMode.Come:
            {
                // "all go" — an explicit GO order overrides following entirely
                // (Source-X NPCACT_GOTO): the pet walks to the ordered spot and
                // stays there instead of returning to the owner. Running the
                // follow step and the GO step in the same tick made the two
                // moves cancel out, so the pet oscillated between the owner
                // and the goal without ever arriving.
                if (npc.TryGetTag("GO_TARGET", out string? goTag) &&
                    TryParsePoint(goTag, out Point3D goPos))
                {
                    if (npc.MapIndex != goPos.Map)
                    {
                        // Pet AI must not teleport between facets. Travel
                        // systems may transfer followers explicitly; a stale
                        // cross-map GO order is simply completed here.
                        FinishGoOrder(npc);
                        break;
                    }
                    // Walk onto the tile itself. Source-X keeps stepping until there
                    // is no direction left to take (NPC_WalkToPoint, CCharNPCAct.cpp:437)
                    // - being one tile short is not arrival, and treating it as such
                    // meant a GO to an adjacent tile never moved the pet at all.
                    int goDist = npc.Position.GetDistanceTo(goPos);
                    if (goDist == 0)
                    {
                        FinishGoOrder(npc);
                        break;
                    }

                    var beforeGoStep = npc.Position;
                    MoveToward(npc, goPos, run: goDist > 3);

                    // The last tile can simply be unreachable - occupied, blocked or
                    // walled off. Standing beside it and getting nowhere ends the
                    // order rather than retrying it forever.
                    if (goDist == 1 && npc.Position == beforeGoStep)
                        FinishGoOrder(npc);
                    break;
                }

                Character followTarget = ResolvePetTargetCharacter(npc, "FOLLOW_TARGET") ?? master;

                // Source-X @NPCActFollow: RETURN 1 gives up following entirely,
                // RETURN 0 means the script handled this call, and falling through
                // carries on with the arguments the script may have rewritten.
                var followArgs = new FollowTriggerArgs { MaxDistance = PetFollowDistance };
                if (OnNpcActFollow != null)
                {
                    var followed = OnNpcActFollow(npc, followTarget, followArgs);
                    if (followed == FollowTriggerResult.GiveUp)
                    {
                        npc.PetAIMode = PetAIMode.Stay;
                        break;
                    }
                    if (followed == FollowTriggerResult.Handled)
                        break;
                }
                if (npc.MapIndex != followTarget.MapIndex)
                {
                    break;
                }
                // Source-X only takes the follow point from a target it can SEE
                // (NPC_Act_Follow, CCharNPCAct.cpp:1386; CanSee, CCharStatus.cpp:1189).
                // SphereNet read the position unconditionally, so a pet tracked an
                // owner who had just hidden - across the map, to wherever they went.
                // A hidden target leaves the last place it WAS seen standing, which is
                // what the pet walks to.
                var followPoint = ResolveFollowPoint(npc, followTarget);
                if (followPoint == null)
                    break;

                int dist = npc.Position.GetDistanceTo(followPoint.Value);
                bool leashed = PetFollowMaxDistance > 0 && dist > PetFollowMaxDistance;
                int keep = Math.Max(0, followArgs.MaxDistance);
                if (dist > keep && !leashed)
                    MoveToward(npc, followPoint.Value, run: dist > 3);
                break;
            }
            case PetAIMode.Guard:
            {
                Character guardTarget = ResolvePetTargetCharacter(npc, "GUARD_TARGET") ?? master;
                if (guardTarget.MapIndex != npc.MapIndex)
                    break;
                if (npc.FightTarget.IsValid)
                {
                    var current = _world.FindChar(npc.FightTarget);
                    if (IsValidPetEnemy(npc, master, current, guardTarget))
                    {
                        ActFight(npc, current!, 50);
                        return;
                    }
                    npc.FightTarget = Serial.Invalid;
                }
                // Protect the selected guarded character, not always the owner.
                if (guardTarget.FightTarget.IsValid)
                {
                    var masterTarget = _world.FindChar(guardTarget.FightTarget);
                    if (IsValidPetEnemy(npc, master, masterTarget, guardTarget))
                    {
                        npc.FightTarget = masterTarget!.Uid;
                        ActFight(npc, masterTarget, 50);
                        return;
                    }
                }
                foreach (var ch in _world.GetCharsInRange(guardTarget.Position, 6))
                {
                    if (!IsValidPetEnemy(npc, master, ch, guardTarget)) continue;
                    if (ch.FightTarget == guardTarget.Uid &&
                        _world.CanSeeLOS(npc.Position, ch.Position))
                    {
                        npc.FightTarget = ch.Uid;
                        ActFight(npc, ch, 50);
                        return;
                    }
                }
                int guardDist = npc.Position.GetDistanceTo(guardTarget.Position);
                if (guardDist > 3 &&
                    (PetFollowMaxDistance <= 0 || guardDist <= PetFollowMaxDistance))
                    MoveToward(npc, guardTarget.Position, run: true);
                break;
            }
            case PetAIMode.Attack:
            {
                Character? target = ResolvePetTargetCharacter(npc, "ATTACK_TARGET");
                if (target == null && master.FightTarget.IsValid)
                    target = _world.FindChar(master.FightTarget);
                if (target == null && npc.FightTarget.IsValid)
                    target = _world.FindChar(npc.FightTarget);
                if (IsValidPetEnemy(npc, master, target))
                {
                    npc.FightTarget = target!.Uid;
                    int motivation = GetAttackMotivation(npc, target);
                    ActFight(npc, target, Math.Max(motivation, 50));
                    return;
                }
                // Target dead/gone — revert to the mode the pet was in before the
                // attack order (Guard/Follow), instead of trailing the master.
                npc.FightTarget = Serial.Invalid;
                npc.RemoveTag("ATTACK_TARGET");
                PetAIMode revertMode = PetAIMode.Follow;
                if (npc.TryGetTag("PREV_PET_MODE", out string? prevTag) &&
                    int.TryParse(prevTag, out int prevVal) &&
                    Enum.IsDefined(typeof(PetAIMode), prevVal) &&
                    (PetAIMode)prevVal != PetAIMode.Attack)
                    revertMode = (PetAIMode)prevVal;
                npc.RemoveTag("PREV_PET_MODE");
                npc.PetAIMode = revertMode;
                int d = npc.Position.GetDistanceTo(master.Position);
                if (d > 2 && (PetFollowMaxDistance <= 0 || d <= PetFollowMaxDistance))
                    MoveToward(npc, master.Position, run: d > 3);
                break;
            }
            case PetAIMode.Stay:
            case PetAIMode.Stop:
                // Stay in place
                break;
        }
    }

    private Character? ResolvePetTargetCharacter(Character npc, string tagName)
    {
        if (!npc.TryGetTag(tagName, out string? uidText) || string.IsNullOrWhiteSpace(uidText))
            return null;
        uint uid = SphereNet.Game.Objects.ObjBase.ParseHexOrDecUInt(uidText);
        if (uid == 0)
        {
            npc.RemoveTag(tagName);
            return null;
        }
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
}
