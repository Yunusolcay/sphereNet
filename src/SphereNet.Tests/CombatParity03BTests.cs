using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What happens to a swing that was wound up before the world changed under it.
///
/// SphereNet checked freeze and sleep only where a swing STARTS, and a pending hit
/// never re-enters that path — so a blow wound up before a paralyse still landed
/// after one. Source-X re-runs Fight_CanHit at the top of the hit phase
/// (Fight_Hit, CCharFight.cpp:1813) and refuses to proceed unless it answers READY.
///
/// The disposition matters as much as the check. Fight_CanHit returns
/// WAR_SWING_SWINGING - hold the swing - for a frozen or sleeping attacker and for
/// a sleeping target (CCharFight.cpp:1696-1699), and only WAR_SWING_INVALID (drop)
/// for dead / stone / invulnerable / insubstantial. Clearing the pending hit for
/// the first group would be a different rule from the reference.
/// </summary>
public sealed class CombatParity03BTests
{
    private static (GameWorld World, Character Attacker, Character Target) Setup()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100);
        var target = MakeChar(world, 101);
        return (world, attacker, target);
    }

    private static Character MakeChar(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static CombatHelper.HitTimeDecision Evaluate(
        GameWorld world, Character attacker, Character target) =>
        CombatHelper.EvaluateHitTime(world, attacker, target, null, PrivLevel.Player,
            nowMs: 1000, deadlineMs: 5000);

    // --- SX-03B-01: state that arrives during the windup --------------------

    [Fact]
    public void AReadySwingStillResolves()
    {
        var (world, attacker, target) = Setup();
        Assert.Equal(CombatHelper.HitTimeDecision.Resolve, Evaluate(world, attacker, target));
    }

    [Fact]
    public void AFrozenAttackerHoldsThePendingSwing()
    {
        var (world, attacker, target) = Setup();
        attacker.SetStatFlag(StatFlag.Freeze);

        // Wait, not Drop: Source-X answers SWINGING, which keeps the swing pending.
        Assert.Equal(CombatHelper.HitTimeDecision.Wait, Evaluate(world, attacker, target));
    }

    [Fact]
    public void ASleepingAttackerHoldsThePendingSwing()
    {
        var (world, attacker, target) = Setup();
        attacker.SetStatFlag(StatFlag.Sleeping);
        Assert.Equal(CombatHelper.HitTimeDecision.Wait, Evaluate(world, attacker, target));
    }

    [Fact]
    public void ASleepingTargetHoldsThePendingSwing()
    {
        var (world, attacker, target) = Setup();
        target.SetStatFlag(StatFlag.Sleeping);
        Assert.Equal(CombatHelper.HitTimeDecision.Wait, Evaluate(world, attacker, target));
    }

    [Fact]
    public void ParalyzeCanSwingLetsAFrozenAttackerThrough()
    {
        var (world, attacker, target) = Setup();
        int saved = Character.CombatFlags;
        try
        {
            Character.CombatFlags = saved | (int)CombatFlags.ParalyzeCanSwing;
            attacker.SetStatFlag(StatFlag.Freeze);

            Assert.Equal(CombatHelper.HitTimeDecision.Resolve, Evaluate(world, attacker, target));
        }
        finally { Character.CombatFlags = saved; }
    }

    [Fact]
    public void ParalyzeCanSwingDoesNotExcuseSleep()
    {
        var (world, attacker, target) = Setup();
        int saved = Character.CombatFlags;
        try
        {
            Character.CombatFlags = saved | (int)CombatFlags.ParalyzeCanSwing;
            attacker.SetStatFlag(StatFlag.Sleeping);

            Assert.Equal(CombatHelper.HitTimeDecision.Wait, Evaluate(world, attacker, target));
        }
        finally { Character.CombatFlags = saved; }
    }

    [Fact]
    public void ADeadTargetStillDropsTheSwing()
    {
        // The INVALID group keeps its old disposition — only the SWINGING group
        // changed from "resolve anyway" to "hold".
        var (world, attacker, target) = Setup();
        target.SetStatFlag(StatFlag.Dead);
        Assert.Equal(CombatHelper.HitTimeDecision.Drop, Evaluate(world, attacker, target));
    }

    // --- SX-03B-02: an unspent swing must not cost ammo ---------------------

    [Fact]
    public void AThrowingWeaponDemandsNoPackAmmo()
    {
        // The thrown weapon IS the projectile. Its fallback type was still
        // WeaponBolt, so the out-of-range miss branch matched any bolt stack in the
        // pack and consumed one - a thrown spear was eating crossbow bolts.
        var world = TestHarness.CreateWorld();
        var spear = world.CreateItem();
        spear.ItemType = ItemType.WeaponThrowing;

        Assert.True(CombatHelper.IsThrowingWeapon(spear));

        var spec = CombatHelper.ResolveAmmoSpec(null, ItemType.WeaponThrowing, null);
        Assert.Equal(0, spec.BaseId);
        Assert.Equal(0, spec.Gfx);
    }

    [Fact]
    public void MovingOutOfReachYieldsAMissDecisionNotAResolve()
    {
        // Pins the branch the ammo fix lives on: with STAYINRANGE the swing is spent
        // when the target has stepped away. Source-X reaches this state through the
        // range re-check (CCharFight.cpp:1896) well BEFORE it locates any ammo, so
        // no arrow may be taken here - only a real miss roll costs one
        // (:2023 m_Act_Difficulty < 0).
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100);
        var target = MakeChar(world, 100);
        world.PlaceCharacter(target, new Point3D(140, 100, 0, 0));   // far out of reach

        int saved = Character.CombatFlags;
        try
        {
            Character.CombatFlags = saved | (int)CombatFlags.StayInRange;
            Assert.Equal(CombatHelper.HitTimeDecision.Miss,
                CombatHelper.EvaluateHitTime(world, attacker, target, null, PrivLevel.Player,
                    nowMs: 1000, deadlineMs: 5000));
        }
        finally { Character.CombatFlags = saved; }
    }
}
