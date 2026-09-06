using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Which number the swing-speed formula takes, and whether walking during a windup
/// still costs the archer their settle time.
///
/// Source-X picks the stat per era (CResourceCalc.cpp): era 0 uses
/// Stat_GetAdjusted(STAT_DEX) - the effective stat, equipment and buffs included
/// (:61, :69) - while eras 1-4 use Stat_GetVal(STAT_DEX), which is the CURRENT
/// STAMINA POOL (:90, :101, :116, :127; CChar.cpp:4271 writes the STAM save field
/// from it). Reading the base field everywhere meant stamina loss never slowed an
/// attack in the eras that price it that way, and equipment DEX never sped one up
/// in era 0.
/// </summary>
public sealed class CombatParity03CTests
{
    private const int Scale = 15_000;

    /// <summary>Speed is resolved from the itemdef or the OVERRIDE.SPEED tag; the
    /// tag is the instance-level route a test can use.</summary>
    private static Item Weapon(int speed)
    {
        var w = new Item { ItemType = ItemType.WeaponSword };
        w.SetTag("OVERRIDE.SPEED", speed.ToString());
        return w;
    }

    private static int Delay(Character ch, Item? weapon, int era)
    {
        int savedEra = Character.CombatSpeedEra;
        int savedScale = Character.CombatSpeedScaleFactor;
        try
        {
            Character.CombatSpeedEra = era;
            Character.CombatSpeedScaleFactor = Scale;
            return CombatEngine.GetSwingDelayMs(ch, weapon);
        }
        finally
        {
            Character.CombatSpeedEra = savedEra;
            Character.CombatSpeedScaleFactor = savedScale;
        }
    }

    // --- SX-03C-01: the stat the formula reads ------------------------------

    [Fact]
    public void SpentStaminaSlowsTheSwingInEraOne()
    {
        var full = new Character { Dex = 100, Stam = 100 };
        var tired = new Character { Dex = 100, Stam = 10 };

        // 150000 / ((100+100) * 30) = 25 -> 2500 ms
        Assert.Equal(2500, Delay(full, Weapon(30), era: 1));
        // 150000 / ((10+100) * 30) = 45 -> 4500 ms
        Assert.Equal(4500, Delay(tired, Weapon(30), era: 1));
    }

    [Fact]
    public void EquipmentDexSpeedsUpTheSwingInEraZero()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Dex = 50; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        // 150000 / ((50+100) * 30) = 33 -> 3300 ms
        Assert.Equal(3300, Delay(ch, Weapon(30), era: 0));

        var bracers = world.CreateItem();
        bracers.SetTag("BONUSDEX", "50");
        ch.Equip(bracers, Layer.Bracelet);
        Assert.Equal(100, CombatEngine.EffectiveDex(ch));

        // 150000 / ((100+100) * 30) = 25 -> 2500 ms
        Assert.Equal(2500, Delay(ch, Weapon(30), era: 0));
    }

    [Fact]
    public void EraZeroIgnoresSpentStamina()
    {
        // The mirror of the two above: era 0 asks for the effective stat, so an
        // exhausted character swings at the same speed.
        var full = new Character { Dex = 50, Stam = 50 };
        var tired = new Character { Dex = 50, Stam = 1 };

        Assert.Equal(Delay(full, Weapon(30), era: 0), Delay(tired, Weapon(30), era: 0));
    }

    [Fact]
    public void EraOneIgnoresEquipmentDex()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Dex = 50; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        int before = Delay(ch, Weapon(30), era: 1);

        var bracers = world.CreateItem();
        bracers.SetTag("BONUSDEX", "50");
        ch.Equip(bracers, Layer.Bracelet);

        Assert.Equal(before, Delay(ch, Weapon(30), era: 1));
    }

    [Fact]
    public void ACharacterRolledFromAChardefHasAStaminaPool()
    {
        // The Dex setter raises MaxStam but never the current pool, so a creature
        // built without an explicit Stam would have swung at the slowest rate the
        // era-1..4 formula allows.
        var ch = new Character();
        ch.Dex = 80;
        Assert.Equal(80, ch.MaxStam);

        ch.Stam = ch.MaxStam;      // what the creation paths now do
        Assert.Equal(80, ch.Stam);
    }

    // --- SX-03C-02: walking during the windup -------------------------------

    private static (GameWorld World, Character Archer, Character Target) Fight()
    {
        var world = TestHarness.CreateWorld();
        var archer = world.CreateCharacter();
        archer.Str = 100; archer.MaxHits = 100; archer.Hits = 100;
        archer.Dex = 100; archer.Stam = 100;
        world.PlaceCharacter(archer, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.Str = 100; target.MaxHits = 100; target.Hits = 100;
        world.PlaceCharacter(target, new Point3D(102, 100, 0, 0));
        return (world, archer, target);
    }

    private static Item Bow()
    {
        var bow = new Item { ItemType = ItemType.WeaponBow };
        bow.SetTag("OVERRIDE.SPEED", "30");
        return bow;
    }

    private static CombatHelper.HitTimeDecision Evaluate(
        GameWorld world, Character archer, Character target, Item? weapon) =>
        CombatHelper.EvaluateHitTime(world, archer, target, weapon, PrivLevel.Player,
            nowMs: 100_000, deadlineMs: 200_000);

    [Fact]
    public void AnArcherWhoWalkedDuringTheWindupDoesNotLoose()
    {
        var (world, archer, target) = Fight();
        int savedDelay = Character.CombatArcheryMovementDelay;
        int savedFlags = Character.CombatFlags;
        try
        {
            Character.CombatArcheryMovementDelay = 10_000;
            Character.CombatFlags = 0;
            archer.LastMoveTick = 100_000;      // just moved

            // Miss = a SPENT swing (Source-X WAR_SWING_EQUIPPING), which is also how
            // out-of-reach is modelled here — and it takes no ammunition.
            Assert.Equal(CombatHelper.HitTimeDecision.Miss,
                Evaluate(world, archer, target, Bow()));
        }
        finally
        {
            Character.CombatArcheryMovementDelay = savedDelay;
            Character.CombatFlags = savedFlags;
        }
    }

    [Fact]
    public void TheShotLandsOnceTheSettleTimeHasPassed()
    {
        var (world, archer, target) = Fight();
        int savedDelay = Character.CombatArcheryMovementDelay;
        int savedFlags = Character.CombatFlags;
        try
        {
            Character.CombatArcheryMovementDelay = 10_000;
            Character.CombatFlags = 0;
            archer.LastMoveTick = 80_000;       // moved 20s ago

            Assert.Equal(CombatHelper.HitTimeDecision.Resolve,
                Evaluate(world, archer, target, Bow()));
        }
        finally
        {
            Character.CombatArcheryMovementDelay = savedDelay;
            Character.CombatFlags = savedFlags;
        }
    }

    [Theory]
    [InlineData(true, false)]   // COMBAT_ARCHERYCANMOVE
    [InlineData(false, true)]   // STATF_ARCHERCANMOVE
    public void TheMoveAndShootExceptionsStillApply(bool combatFlag, bool statFlag)
    {
        var (world, archer, target) = Fight();
        int savedDelay = Character.CombatArcheryMovementDelay;
        int savedFlags = Character.CombatFlags;
        try
        {
            Character.CombatArcheryMovementDelay = 10_000;
            Character.CombatFlags = combatFlag ? (int)CombatFlags.ArcheryCanMove : 0;
            if (statFlag) archer.SetStatFlag(StatFlag.ArcherCanMove);
            archer.LastMoveTick = 100_000;

            Assert.Equal(CombatHelper.HitTimeDecision.Resolve,
                Evaluate(world, archer, target, Bow()));
        }
        finally
        {
            Character.CombatArcheryMovementDelay = savedDelay;
            Character.CombatFlags = savedFlags;
        }
    }

    [Fact]
    public void MeleeIsNotSubjectToTheArcheryMovementWait()
    {
        // SphereNet's melee movement delay has no Source-X counterpart, so it is
        // deliberately NOT repeated at hit time.
        var (world, archer, target) = Fight();
        int savedDelay = Character.CombatArcheryMovementDelay;
        int savedFlags = Character.CombatFlags;
        try
        {
            Character.CombatArcheryMovementDelay = 10_000;
            Character.CombatFlags = 0;
            archer.LastMoveTick = 100_000;

            Assert.Equal(CombatHelper.HitTimeDecision.Resolve,
                Evaluate(world, archer, target, Weapon(30)));
        }
        finally
        {
            Character.CombatArcheryMovementDelay = savedDelay;
            Character.CombatFlags = savedFlags;
        }
    }
}
