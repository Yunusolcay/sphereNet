using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;

namespace SphereNet.Game.Combat;

/// <summary>
/// Shared combat gates used by player and NPC swing paths. Mirrors Source-X
/// <c>f_combat_hit</c> / <c>Fight_CanHit</c> checks that must run before
/// swing recoil is consumed.
/// </summary>
public static class CombatHelper
{
    public static bool IsRangedWeapon(Item? weapon) =>
        weapon != null &&
        weapon.ItemType is ItemType.WeaponBow or ItemType.WeaponXBow or ItemType.WeaponThrowing;

    /// <summary>Throwing weapons are ranged but consume NO pack ammo — the
    /// wielded weapon itself is the projectile (Source-X SKILL_THROWING; pack
    /// t_weapon_throwing: TDATA3 empty, TDATA4 = flight animation art).</summary>
    public static bool IsThrowingWeapon(Item? weapon) =>
        weapon?.ItemType == ItemType.WeaponThrowing;

    public static bool IsMeleeWeapon(Item? weapon) => weapon == null || !IsRangedWeapon(weapon);

    /// <summary>Weapon min/max tile range. Ranged uses ITEMDEF RANGEL/RANGEH with ini fallback.</summary>
    public static (int Min, int Max) GetWeaponRange(Item? weapon)
    {
        if (!IsRangedWeapon(weapon))
        {
            var meleeDef = weapon != null ? DefinitionLoader.GetItemDef(weapon.BaseId) : null;
            int meleeMin = Math.Max(0, meleeDef?.RangeMin ?? 0);
            int meleeMax = meleeDef is { RangeMax: > 0 } ? meleeDef.RangeMax : 1;
            if (meleeMin > meleeMax)
                (meleeMin, meleeMax) = (meleeMax, meleeMin);
            return (meleeMin, meleeMax);
        }

        var def = weapon != null ? DefinitionLoader.GetItemDef(weapon.BaseId) : null;
        int minDist = def is { RangeMin: > 0 } ? def.RangeMin : Character.ArcheryMinDist;
        int maxDist = def is { RangeMax: > 0 } ? def.RangeMax : Character.ArcheryMaxDist;
        if (maxDist < 1)
            maxDist = Character.ArcheryMaxDist;
        if (minDist < 0)
            minDist = Character.ArcheryMinDist;

        // Malformed ITEMDEF ranges must not make the weapon permanently
        // unusable. Source-X's range parser normalises low/high; definitions
        // can also be supplied at runtime, so keep the combat boundary safe.
        minDist = Math.Max(0, minDist);
        maxDist = Math.Max(1, maxDist);
        if (minDist > maxDist)
            (minDist, maxDist) = (maxDist, minDist);
        return (minDist, maxDist);
    }

    /// <summary>State that makes a character invalid for a committed weapon
    /// swing. Kept here so player start, NPC start and the delayed hit phase
    /// cannot drift apart.</summary>
    public static bool IsInvalidSwingParticipant(Character ch, bool asTarget)
    {
        if (ch.IsDeleted || ch.IsDead)
            return true;
        if (ch.IsStatFlag(StatFlag.Stone))
            return true;
        if (asTarget && ch.IsStatFlag(StatFlag.Invul | StatFlag.Insubstantial | StatFlag.Ridden))
            return true;
        return false;
    }

    public static int GetChebyshevDistance(Character a, Character b)
    {
        if (a.MapIndex != b.MapIndex) return int.MaxValue;
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>Source-X safe/nopvp region combat blocks for f_combat_hit.</summary>
    public static bool IsCombatBlockedByRegion(GameWorld world, Character attacker, Character target)
    {
        var atkRegion = world.FindRegion(attacker.Position);
        if (atkRegion != null && atkRegion.IsFlag(RegionFlag.Safe))
            return true;

        var tgtRegion = world.FindRegion(target.Position);
        if (tgtRegion != null && tgtRegion.IsFlag(RegionFlag.Safe))
            return true;

        if (attacker.IsPlayer && target.IsPlayer)
        {
            if (atkRegion != null && atkRegion.IsFlag(RegionFlag.NoPvP))
                return true;
            if (tgtRegion != null && tgtRegion.IsFlag(RegionFlag.NoPvP))
                return true;
        }

        // Source-X Fight_CanHit ship gate (CCharFight.cpp:1725): combat across
        // a ship boundary — one side aboard, the other not (different regions)
        // — is blocked unless COMBAT_ALLOWHITFROMSHIP. Both on the SAME ship
        // may always fight.
        if (!IsCombatFlagSet(CombatFlags.AllowHitFromShip) && atkRegion != tgtRegion &&
            ((atkRegion != null && atkRegion.IsFlag(RegionFlag.Ship)) ||
             (tgtRegion != null && tgtRegion.IsFlag(RegionFlag.Ship))))
            return true;

        return false;
    }

    /// <summary>Source-X Cmd_Use_Obj self-dclick gate (CClientEvent.cpp:2368):
    /// without COMBAT_DCLICKSELF_UNMOUNTS, a mounted char in war mode with an
    /// active fight opens the paperdoll instead of accidentally dismounting.</summary>
    public static bool DClickSelfKeepsMount(Character ch) =>
        !IsCombatFlagSet(CombatFlags.DClickSelfUnmounts) &&
        ch.IsInWarMode && ch.FightTarget.IsValid;

    /// <summary>COMBAT_FACECOMBAT: player must face target within ±1 direction.</summary>
    public static bool IsFacingTarget(Character attacker, Character target)
    {
        if ((Character.CombatFlags & (int)CombatFlags.FaceCombat) == 0)
            return true;
        if (!attacker.IsPlayer)
            return true;

        var desired = attacker.Position.GetDirectionTo(target.Position);
        int cur = (int)attacker.Direction & 0x07;
        int want = (int)desired & 0x07;
        int diff = Math.Abs(cur - want);
        if (diff > 4)
            diff = 8 - diff;
        return diff <= 1;
    }

    public static bool HasShieldEquipped(Character ch)
    {
        if (ch.IsStatFlag(StatFlag.HasShield))
            return true;
        var twoHand = ch.GetEquippedItem(Layer.TwoHanded);
        return twoHand != null && twoHand.ItemType == ItemType.Shield;
    }

    /// <summary>Non-GM attackers lose hidden/invisible when initiating a swing.</summary>
    public static void RevealOnAttack(Character attacker, PrivLevel privLevel)
    {
        if (privLevel >= PrivLevel.GM)
            return;
        attacker.ClearHiddenState();
    }

    public enum SwingPrepResult
    {
        Ready,
        RetryLater,
        Abort,
    }

    public readonly struct SwingPrepFailure
    {
        public SwingPrepFailure(SwingPrepResult result, long retryMs, string? messageKey = null)
        {
            Result = result;
            RetryMs = retryMs;
            MessageKey = messageKey;
        }

        public SwingPrepResult Result { get; }
        public long RetryMs { get; }
        public string? MessageKey { get; }
    }

    /// <summary>
    /// Validates swing preconditions without consuming recoil. Returns
    /// <see cref="SwingPrepResult.Ready"/> when ResolveAttack may proceed.
    /// </summary>
    public static SwingPrepFailure ValidateSwingPrep(
        GameWorld world,
        Character attacker,
        Character target,
        Item? weapon,
        PrivLevel privLevel,
        long nowMs,
        Func<Point3D, Point3D, bool>? canSeeLos = null,
        bool ignoreRangeLos = false,
        (int Min, int Max)? effectiveRange = null)
    {
        if (attacker == target || attacker.MapIndex != target.MapIndex ||
            IsInvalidSwingParticipant(attacker, asTarget: false) ||
            IsInvalidSwingParticipant(target, asTarget: true))
            return new SwingPrepFailure(SwingPrepResult.Abort, 0);

        // Fight_CanHit holds the swing while the target is hidden/invisible.
        // A known/stale serial must never bypass visibility and take damage.
        if (target.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible))
            return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

        if (IsCombatBlockedByRegion(world, attacker, target))
            return new SwingPrepFailure(SwingPrepResult.Abort, 0);

        if (!IsFacingTarget(attacker, target))
            return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

        if (IsRangedWeapon(weapon))
        {
            if (HasShieldEquipped(attacker))
                return new SwingPrepFailure(SwingPrepResult.Abort, 0, Msg.ItemuseBowShield);

            // COMBAT_SWING_NORANGE: the swing may START at any range / without LoS;
            // reach + LoS are re-checked when the hit resolves instead.
            if (!ignoreRangeLos)
            {
                int dist = GetChebyshevDistance(attacker, target);
                var (minRange, maxRange) = effectiveRange ?? GetWeaponRange(weapon);
                NormaliseRange(ref minRange, ref maxRange);
                if (dist < minRange)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250, Msg.CombatArchTooclose);
                if (dist > maxRange)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

                if (privLevel < PrivLevel.GM)
                {
                    canSeeLos ??= world.CanSeeLOS;
                    if (!canSeeLos(attacker.Position, target.Position))
                        return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);
                }
            }

            // COMBAT_ARCHERYCANMOVE lets an archer fire while/just after moving,
            // bypassing the post-move settle delay.
            if (Character.CombatArcheryMovementDelay > 0 && attacker.LastMoveTick > 0 &&
                !IsCombatFlagSet(CombatFlags.ArcheryCanMove) &&
                !attacker.IsStatFlag(StatFlag.ArcherCanMove))
            {
                long sinceMove = nowMs - attacker.LastMoveTick;
                if (sinceMove < Character.CombatArcheryMovementDelay)
                {
                    long wait = Character.CombatArcheryMovementDelay - sinceMove;
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, wait);
                }
            }
        }
        else
        {
            if (!ignoreRangeLos)
            {
                var (minRange, maxRange) = effectiveRange ?? GetWeaponRange(weapon);
                NormaliseRange(ref minRange, ref maxRange);
                int distance = GetChebyshevDistance(attacker, target);
                if (distance < minRange || distance > maxRange)
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);

                if (privLevel < PrivLevel.GM)
                {
                    canSeeLos ??= world.CanSeeLOS;
                    if (!canSeeLos(attacker.Position, target.Position))
                        return new SwingPrepFailure(SwingPrepResult.RetryLater, 250);
                }
            }

            if (Character.CombatMeleeMovementDelay > 0 && attacker.LastMoveTick > 0)
            {
                long sinceMove = nowMs - attacker.LastMoveTick;
                if (sinceMove < Character.CombatMeleeMovementDelay)
                {
                    long wait = Character.CombatMeleeMovementDelay - sinceMove;
                    return new SwingPrepFailure(SwingPrepResult.RetryLater, wait);
                }
            }
        }

        return new SwingPrepFailure(SwingPrepResult.Ready, 0);
    }

    public static CombatFlags ActiveCombatFlags =>
        (CombatFlags)(Character.CombatFlags & 0xFFFFFFFF);

    /// <summary>True when the given COMBATFLAGS bit is enabled in sphere.ini.</summary>
    public static bool IsCombatFlagSet(CombatFlags flag) =>
        (Character.CombatFlags & (int)flag) != 0;

    // =====================================================================
    // Two-phase swing (Source-X windup -> hit). The default (flagless) case
    // keeps a zero-length windup so the hit resolves in the same tick the
    // swing starts — byte-for-byte the previous atomic behaviour. A windup
    // window only opens for STAYINRANGE / SWING_NORANGE (and PREHIT forces the
    // hit back to swing-start and disables SWING_NORANGE).
    // =====================================================================

    /// <summary>Outcome of the hit-time reach/LoS re-check.</summary>
    public enum HitTimeDecision { Resolve, Miss, Wait, Drop }

    /// <summary>Whether the attacker is within the weapon's reach AND has LoS to
    /// the target right now (used to re-validate at hit time).</summary>
    public static bool InWeaponReachAndLos(
        GameWorld world, Character attacker, Character target, Item? weapon,
        PrivLevel privLevel, Func<Point3D, Point3D, bool>? canSeeLos = null,
        (int Min, int Max)? effectiveRange = null)
    {
        int dist = GetChebyshevDistance(attacker, target);
        var (min, max) = effectiveRange ?? GetWeaponRange(weapon);
        NormaliseRange(ref min, ref max);
        if (dist < min || dist > max)
            return false;
        if (privLevel < PrivLevel.GM)
        {
            canSeeLos ??= world.CanSeeLOS;
            if (!canSeeLos(attacker.Position, target.Position)) return false;
        }
        return true;
    }

    /// <summary>Decide what to do with a pending hit when its windup elapses
    /// (Source-X StayInRange / SwingNoRange semantics).</summary>
    public static HitTimeDecision EvaluateHitTime(
        GameWorld world, Character attacker, Character? target, Item? weapon,
        PrivLevel privLevel, long nowMs, long deadlineMs,
        Func<Point3D, Point3D, bool>? canSeeLos = null,
        bool? swingNoRange = null,
        (int Min, int Max)? effectiveRange = null)
    {
        if (target == null || attacker == target || attacker.MapIndex != target.MapIndex ||
            IsInvalidSwingParticipant(attacker, asTarget: false) ||
            IsInvalidSwingParticipant(target, asTarget: true) ||
            target.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible) ||
            IsCombatBlockedByRegion(world, attacker, target))
            return HitTimeDecision.Drop;

        if (InWeaponReachAndLos(world, attacker, target, weapon, privLevel, canSeeLos, effectiveRange))
            return HitTimeDecision.Resolve;

        // Out of reach / LoS when the hit should land:
        bool preHit = IsCombatFlagSet(CombatFlags.PreHit);
        if (IsCombatFlagSet(CombatFlags.StayInRange) && !preHit)
            return HitTimeDecision.Miss;                 // moved out -> miss
        if ((swingNoRange ?? IsCombatFlagSet(CombatFlags.SwingNoRange)) && !preHit)
            return nowMs >= deadlineMs ? HitTimeDecision.Drop : HitTimeDecision.Wait;
        // Default: resolve anyway — a flagless swing only starts in range, so the
        // hit landing in the same tick is exactly the previous atomic behaviour.
        return HitTimeDecision.Resolve;
    }

    private static void NormaliseRange(ref int min, ref int max)
    {
        min = Math.Max(0, min);
        max = Math.Max(0, max);
        if (min > max)
            (min, max) = (max, min);
    }

    /// <summary>Windup length before the hit lands. 0 = atomic (hit at swing
    /// start), which is the flagless default and the PREHIT case. STAYINRANGE /
    /// SWING_NORANGE (without PREHIT) open a full-swing window so the reach/LoS
    /// re-check has meaning. <paramref name="swingNoRange"/> overrides the
    /// SWING_NORANGE flag for this swing (the @HitCheck LOCAL.Recoil_NoRange
    /// contract); null keeps the global flag.</summary>
    public static int GetSwingHitDelayMs(int swingDelayMs, bool? swingNoRange = null)
    {
        if (IsCombatFlagSet(CombatFlags.PreHit)) return 0;
        bool window = IsCombatFlagSet(CombatFlags.StayInRange) ||
            (swingNoRange ?? IsCombatFlagSet(CombatFlags.SwingNoRange));
        return window ? Math.Max(0, swingDelayMs) : 0;
    }

    /// <summary>True when the swing may START out of range / without LoS
    /// (COMBAT_SWING_NORANGE, unless PREHIT overrides it).</summary>
    public static bool SwingIgnoresStartRange() =>
        IsCombatFlagSet(CombatFlags.SwingNoRange) && !IsCombatFlagSet(CombatFlags.PreHit);

    /// <summary>Legacy 0x6E animation per-frame delay for COMBAT_ANIM_HIT_SMOOTH:
    /// 0 when the flag is off (the fixed default swing speed), otherwise a value
    /// scaled to the swing time so a slow weapon shows a correspondingly slow swing.
    /// The exact pacing is client-interpreted; the value is proportional and clamped
    /// to a byte.</summary>
    public static byte GetSwingAnimDelay(int swingDelayMs)
    {
        if (!IsCombatFlagSet(CombatFlags.AnimHitSmooth)) return 0;
        // ~7-frame attack animation paced across the swing: per-frame delay scales
        // with the swing time, in the 0x6E delay unit. At least 1 when enabled.
        return (byte)Math.Clamp(swingDelayMs / 70, 1, 255);
    }

    /// <summary>
    /// Resolve which ammo a ranged weapon fires from its ITEMDEF. AMMOTYPE names
    /// the exact ammo item (resolved to a baseid via <paramref name="resolveDefName"/>)
    /// and AMMOANIM overrides the in-flight projectile graphic. When the weapon
    /// def specifies neither, the legacy defaults apply: arrows (0x0F3F) for bows,
    /// bolts (0x1BFB) for crossbows. A zero <c>BaseId</c> means "match by the
    /// fallback ammo ItemType" instead of a specific item id.
    /// </summary>
    public static (ushort BaseId, ItemType FallbackType, ushort Gfx) ResolveAmmoSpec(
        SphereNet.Scripting.Definitions.ItemDef? weaponDef, ItemType weaponType, Func<string, ushort>? resolveDefName)
    {
        bool bow = weaponType == ItemType.WeaponBow;
        bool throwing = weaponType == ItemType.WeaponThrowing;
        ItemType fallbackType = bow ? ItemType.WeaponArrow : ItemType.WeaponBolt;
        // Throwing: no legacy bolt default — gfx 0 tells the caller to fly the
        // weapon's own graphic (TDATA4/AMMOANIM still overrides when set).
        ushort gfx = bow ? (ushort)0x0F3F : throwing ? (ushort)0 : (ushort)0x1BFB;
        ushort baseId = 0;

        if (weaponDef != null)
        {
            if (!string.IsNullOrWhiteSpace(weaponDef.AmmoType) && resolveDefName != null)
            {
                ushort resolved = resolveDefName(weaponDef.AmmoType);
                if (resolved != 0) baseId = resolved;
            }
            if (weaponDef.AmmoAnim != 0) gfx = weaponDef.AmmoAnim;
        }
        return (baseId, fallbackType, gfx);
    }

    /// <summary>Find ranged ammo anywhere in the backpack tree. Source-X's
    /// ContentFind searches nested bags; limiting this to direct pack contents
    /// made ordinary bagged arrows unusable. Depth/cycle guards also tolerate a
    /// malformed loaded containment graph.</summary>
    public static Item? FindAmmoInContainer(Item? root, ushort baseId,
        ItemType fallbackType, int maxDepth = 16)
    {
        if (root == null || root.IsDeleted || maxDepth <= 0)
            return null;
        return FindAmmoInContainerCore(root, baseId, fallbackType, maxDepth, []);
    }

    private static Item? FindAmmoInContainerCore(Item container, ushort baseId,
        ItemType fallbackType, int depth, HashSet<uint> visited)
    {
        if (depth <= 0 || !visited.Add(container.Uid.Value))
            return null;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = baseId != 0 ? item.BaseId == baseId : item.ItemType == fallbackType;
            if (matches && item.Amount > 0)
                return item;
            // Source-X CContainer::ContentFind checks IsSearchable before descending
            // (CContainer.cpp:236), so a locked chest inside the pack is not part of
            // an ordinary resource search — arrows separated from the player's
            // reachable quiver were still being fired.
            // Source-X CContainer::ContentFind checks IsSearchable before descending
            // (CContainer.cpp:236), so a locked chest inside the pack is not part of
            // an ordinary resource search — arrows separated from the player's
            // reachable quiver were still being fired.
            if (!item.IsSearchableContainer) continue;

            var nested = FindAmmoInContainerCore(item, baseId, fallbackType, depth - 1, visited);
            if (nested != null)
                return nested;
        }
        return null;
    }

    public static int ActiveDamageEra => Character.CombatDamageEra;
    public static int ActiveHitChanceEra => Character.CombatHitChanceEra;
}
