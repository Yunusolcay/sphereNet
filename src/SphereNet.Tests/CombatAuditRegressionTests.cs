using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;

namespace SphereNet.Tests;

/// <summary>Regression coverage for defects found by the combat code audit.</summary>
public class CombatAuditRegressionTests
{
    [Fact]
    public void ValidateSwingPrep_RejectsCrossMapAndInvalidTargetStates()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeCharacter(world, 100, 100, map: 0);
        var target = MakeCharacter(world, 101, 100);
        target.Position = new Point3D(101, 100, 0, 1);

        var crossMap = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, (_, _) => true);
        Assert.Equal(CombatHelper.SwingPrepResult.Abort, crossMap.Result);

        target.Position = new Point3D(101, 100, 0, 0);
        target.SetStatFlag(StatFlag.Invul);
        var invulnerable = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, (_, _) => true);
        Assert.Equal(CombatHelper.SwingPrepResult.Abort, invulnerable.Result);

        target.ClearStatFlag(StatFlag.Invul);
        target.SetStatFlag(StatFlag.Hidden);
        var hidden = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, (_, _) => true);
        Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, hidden.Result);
    }

    [Fact]
    public void EvaluateHitTime_UsesPerSwingNoRangeAndDropsNewSafeRegion()
    {
        int oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = 0;
            var world = TestHarness.CreateWorld();
            var attacker = MakeCharacter(world, 100, 100);
            var target = MakeCharacter(world, 110, 100);
            long now = Environment.TickCount64;

            var waiting = CombatHelper.EvaluateHitTime(world, attacker, target, null,
                PrivLevel.Player, now, now + 5_000, (_, _) => true,
                swingNoRange: true);
            Assert.Equal(CombatHelper.HitTimeDecision.Wait, waiting);

            world.MoveCharacter(target, new Point3D(101, 100, 0, 0));
            var safe = new Region { Name = "late_safe", Flags = RegionFlag.Safe, MapIndex = 0 };
            safe.AddRect(101, 100, 101, 100);
            world.AddRegion(safe);
            var blocked = CombatHelper.EvaluateHitTime(world, attacker, target, null,
                PrivLevel.Player, now, now + 5_000, (_, _) => true,
                swingNoRange: true);
            Assert.Equal(CombatHelper.HitTimeDecision.Drop, blocked);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void Character_TargetSwitchDeathAndPeaceClearPendingSwing()
    {
        var ch = new Character();
        ch.FightTarget = new Serial(10);
        ch.BeginSwingWindup(Environment.TickCount64, 1_000, 1_000,
            ch.FightTarget, Environment.TickCount64 + 2_000);
        Assert.True(ch.HasPendingHit);

        ch.FightTarget = new Serial(11);
        Assert.False(ch.HasPendingHit);

        ch.BeginSwingWindup(Environment.TickCount64, 1_000, 1_000,
            ch.FightTarget, Environment.TickCount64 + 2_000);
        ch.Kill();
        Assert.False(ch.HasPendingHit);
    }

    [Fact]
    public void ValidateSwingPrep_HonorsNpcEffectiveReach()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeCharacter(world, 100, 100);
        var target = MakeCharacter(world, 102, 100);

        var normal = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, (_, _) => true);
        var innateReach = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, (_, _) => true,
            effectiveRange: (1, 2));

        Assert.Equal(CombatHelper.SwingPrepResult.RetryLater, normal.Result);
        Assert.Equal(CombatHelper.SwingPrepResult.Ready, innateReach.Result);

        world.MoveCharacter(target, attacker.Position);
        var overlap = CombatHelper.ValidateSwingPrep(world, attacker, target, null,
            PrivLevel.Player, Environment.TickCount64, (_, _) => true);
        Assert.Equal(CombatHelper.SwingPrepResult.Ready, overlap.Result);
    }

    [Fact]
    public void ResolveAttack_CancelledContextCannotReturnPositiveDamage()
    {
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            var world = TestHarness.CreateWorld();
            var attacker = MakeCharacter(world, 100, 100);
            var target = MakeCharacter(world, 101, 100);
            attacker.PrivLevel = PrivLevel.GM;
            short hpBefore = target.Hits;
            CombatEngine.OnHitDamage = ctx =>
            {
                ctx.Cancelled = true;
                return 500;
            };

            int damage = CombatEngine.ResolveAttack(attacker, target, null,
                CombatFlags.None, 0, 0, ammoUid: 123, out bool ammoHandled);

            Assert.Equal(0, damage);
            Assert.Equal(hpBefore, target.Hits);
            Assert.True(ammoHandled);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    [Fact]
    public void ArmorCompatibilityOverloadUsesExactArmorPipeline()
    {
        int oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = (int)CombatFlags.StackArmor;
            var target = new Character { ModAr = 7 };
            var chest = new Item();
            chest.SetTag("ARMOR", "40");
            target.Equip(chest, Layer.Chest);
            var robe = new Item();
            robe.SetTag("ARMOR", "20");
            target.Equip(robe, Layer.Robe);
            var cape = new Item();
            cape.SetTag("ARMOR", "10");
            target.Equip(cape, Layer.Cape);
            var apron = new Item();
            apron.SetTag("ARMOR", "8");
            target.Equip(apron, Layer.Waist);

            Assert.Equal(CombatEngine.CalcArmorDefense(target),
                CombatEngine.CalcArmorDefense(target, elementalEngine: false));
            Assert.Equal(60, CombatEngine.CalcArmorDefenseForRegion(target, ArmorHitRegion.Chest));
            Assert.Equal(30, CombatEngine.CalcArmorDefenseForRegion(target, ArmorHitRegion.Arms));
            Assert.Equal(28, CombatEngine.CalcArmorDefenseForRegion(target, ArmorHitRegion.Legs));
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void MalformedWeaponDamageAndAttackerTotalsCannotOverflow()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        try
        {
            CombatEngine.WeaponDefLookup = _ => (int.MaxValue, 1);
            var attacker = new Character { Str = short.MaxValue };
            var weapon = new Item { BaseId = 1, ItemType = ItemType.WeaponSword };
            var range = CombatEngine.CalcWeaponDamage(attacker, weapon);
            Assert.InRange(range.Min, 1, short.MaxValue);
            Assert.InRange(range.Max, range.Min, short.MaxValue);

            var world = TestHarness.CreateWorld();
            var defender = MakeCharacter(world, 100, 100);
            var source = MakeCharacter(world, 101, 100);
            defender.RecordAttack(source.Uid, int.MaxValue);
            defender.RecordAttack(source.Uid, 1);
            Assert.Equal(int.MaxValue, Assert.Single(defender.Attackers).TotalDamage);

            defender.Kills = -10;
            Assert.Equal(0, defender.Kills);
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
        }
    }

    [Fact]
    public void RangedWindupKeepsCommittedWeaponAcrossEquipmentSwap()
    {
        int oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = (int)CombatFlags.StayInRange;
            var (client, world, attacker, target, bow, arrows) = MakeRangedFight(1701);
            short staminaBefore = attacker.Stam;

            client.TickCombat();
            Assert.True(attacker.HasPendingHit);
            Assert.Equal(bow.Uid, attacker.PendingHitWeapon);
            Assert.Equal(staminaBefore, attacker.Stam);

            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            attacker.Equip(sword, Layer.TwoHanded);
            attacker.SwingHitTime = Environment.TickCount64 - 1;
            client.TickCombat();

            Assert.False(attacker.HasPendingHit);
            Assert.Equal(1, arrows.Amount);
            Assert.True(target.Hits <= target.MaxHits);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void RangedWindupCannotResolveAFreeHitAfterAmmoDisappears()
    {
        int oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = (int)CombatFlags.StayInRange;
            var (client, world, attacker, target, _, arrows) = MakeRangedFight(1702);
            client.TickCombat();
            Assert.True(attacker.HasPendingHit);

            world.RemoveItem(arrows);
            short hpBefore = target.Hits;
            attacker.SwingHitTime = Environment.TickCount64 - 1;
            client.TickCombat();

            Assert.False(attacker.HasPendingHit);
            Assert.Equal(hpBefore, target.Hits);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void StayInRangeRangedMissDoesNotSpendAmmo()
    {
        int oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags = (int)CombatFlags.StayInRange;
            var (client, world, attacker, target, _, arrows) = MakeRangedFight(1703);
            client.TickCombat();
            Assert.True(attacker.HasPendingHit);

            world.MoveCharacter(target, new Point3D(120, 100, 0, 0));
            attacker.SwingHitTime = Environment.TickCount64 - 1;
            client.TickCombat();

            Assert.False(attacker.HasPendingHit);
            // The swing is SPENT, not missed: the target stepped out of reach during
            // the windup. Source-X reaches that state through the range re-check
            // (CCharFight.cpp:1896) and returns WAR_SWING_EQUIPPING BEFORE it looks
            // for ammunition at :1862, so no arrow is taken - one is spent only on a
            // genuine miss roll (:2023). This test previously asserted the opposite.
            Assert.Equal(2, arrows.Amount);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
        }
    }

    [Fact]
    public void AttackingACriminalDoesNotCountAsHelpingTheCriminal()
    {
        bool oldHelping = Character.HelpingCriminalsIsACrimeEnabled;
        bool oldAttacking = Character.AttackingIsACrimeEnabled;
        try
        {
            Character.HelpingCriminalsIsACrimeEnabled = true;
            Character.AttackingIsACrimeEnabled = true;
            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(loggerFactory, world,
                new AccountManager(loggerFactory), 1704);
            var attacker = MakeCharacter(world, 100, 100);
            TestHarness.AttachCharacter(client, attacker);
            var criminal = MakeCharacter(world, 101, 100);
            criminal.SetCriminal(60_000);
            var innocent = MakeCharacter(world, 102, 100);
            criminal.FightTarget = innocent.Uid;

            client.HandleAttack(criminal.Uid.Value);

            Assert.False(attacker.IsCriminal);
            Assert.False(attacker.IsStatFlag(StatFlag.Criminal));
            Assert.NotNull(attacker.Memory_FindObjTypes(criminal.Uid, MemoryType.IAggressor));
            Assert.NotNull(criminal.Memory_FindObjTypes(attacker.Uid, MemoryType.HarmedBy));
        }
        finally
        {
            Character.HelpingCriminalsIsACrimeEnabled = oldHelping;
            Character.AttackingIsACrimeEnabled = oldAttacking;
        }
    }

    [Fact]
    public void CancelledCombatStartDoesNotDesertOwnedPetOrSetFightTarget()
    {
        string script = Path.Combine(Path.GetTempPath(), $"spherenet_combatstart_{Guid.NewGuid():N}.scp");
        File.WriteAllText(script, """
            [EVENTS e_cancel_combat_start]
            ON=@CombatStart
            RETURN 1
            """);
        int oldFlags = Character.CombatFlags;
        try
        {
            Character.CombatFlags &= ~(int)CombatFlags.NoPetDesert;
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(script);
            stack.Dispatcher.BuildUsedTriggerCache();
            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = TestHarness.CreateWorld();
            var client = TestHarness.CreateClient(loggerFactory, world,
                new AccountManager(loggerFactory), 1705);
            var attacker = MakeCharacter(world, 100, 100);
            attacker.Events.Add(stack.Resources.ResolveDefName("e_cancel_combat_start"));
            TestHarness.AttachCharacter(client, attacker);
            client.SetEngines(triggerDispatcher: stack.Dispatcher);
            var pet = MakeCharacter(world, 101, 100);
            pet.IsPlayer = false;
            pet.NpcMaster = attacker.Uid;

            client.HandleAttack(pet.Uid.Value);

            Assert.Equal(attacker.Uid, pet.OwnerSerial);
            Assert.False(attacker.FightTarget.IsValid);
            Assert.False(attacker.IsInWarMode);
        }
        finally
        {
            Character.CombatFlags = oldFlags;
            File.Delete(script);
        }
    }

    [Fact]
    public void FullParryHasDistinctOutcomeFromMiss()
    {
        var savedParry = CombatEngine.OnHitParry;
        try
        {
            var world = TestHarness.CreateWorld();
            var attacker = MakeCharacter(world, 100, 100);
            attacker.PrivLevel = PrivLevel.GM;
            var target = MakeCharacter(world, 101, 100);
            target.SetSkill(SkillType.Parrying, 1000);
            target.Equip(new Item { ItemType = ItemType.Shield }, Layer.TwoHanded);
            bool parried = false;
            CombatEngine.OnHitParry = (_, _, _) => { parried = true; return 0; };

            int result = 0;
            for (int i = 0; i < 500 && !parried; i++)
            {
                target.Hits = target.MaxHits;
                result = CombatEngine.ResolveAttack(attacker, target, null);
            }

            Assert.True(parried);
            Assert.Equal(CombatEngine.AttackParried, result);
        }
        finally
        {
            CombatEngine.OnHitParry = savedParry;
        }
    }

    [Fact]
    public void ScriptDurabilityChanceIsNotRolledTwice()
    {
        var savedHook = CombatEngine.OnHitDamage;
        bool savedEnabled = CombatEngine.DurabilityEnabled;
        int savedChance = CombatEngine.DurabilityLossChance;
        int savedMin = CombatEngine.DurabilityLossMin;
        int savedMax = CombatEngine.DurabilityLossMax;
        try
        {
            CombatEngine.DurabilityEnabled = true;
            CombatEngine.DurabilityLossChance = 0;
            CombatEngine.DurabilityLossMin = CombatEngine.DurabilityLossMax = 1;
            var world = TestHarness.CreateWorld();
            var attacker = MakeCharacter(world, 100, 100);
            attacker.PrivLevel = PrivLevel.GM;
            var target = MakeCharacter(world, 101, 100);
            var chest = new Item { HitsMax = 50, HitsCur = 50 };
            target.Equip(chest, Layer.Chest);
            CombatEngine.OnHitDamage = ctx =>
            {
                ctx.ItemDamageLayer = Layer.Chest;
                ctx.ItemDamageChance = 100;
                return ctx.Damage;
            };

            Assert.True(CombatEngine.ResolveAttack(attacker, target, null) >= 0);
            Assert.Equal(49, chest.HitsCur);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            CombatEngine.DurabilityEnabled = savedEnabled;
            CombatEngine.DurabilityLossChance = savedChance;
            CombatEngine.DurabilityLossMin = savedMin;
            CombatEngine.DurabilityLossMax = savedMax;
        }
    }

    /// <summary>
    /// Source-X Fight_Hit applies the weapon's own damage first (OnTakeDamage,
    /// CCharFight.cpp:2259) and runs the procs afterwards (:2270-2361). This test
    /// used to assert the opposite ordering — that a killing proc aborted the strike
    /// before its damage landed and before it was recorded — which handed the kill,
    /// the murder count and the loot rights to the proc instead of to the blow that
    /// was actually swung.
    /// </summary>
    [Fact]
    public void OnHitProcKillStillCreditsTheStrikeThatLanded()
    {
        var savedSpell = CombatEngine.OnHitSpell;
        try
        {
            var world = TestHarness.CreateWorld();
            var attacker = MakeCharacter(world, 100, 100);
            attacker.PrivLevel = PrivLevel.GM;
            var target = MakeCharacter(world, 101, 100);
            var weapon = new Item { ItemType = ItemType.WeaponSword };
            weapon.SetTag("HITFIREBALL", "100");
            CombatEngine.OnHitSpell = (_, victim, _) => victim.Kill();

            int result = CombatEngine.ResolveAttack(attacker, target, weapon);

            // The proc still short-circuits the REST of the tail once it kills.
            Assert.Equal(CombatEngine.AttackResolvedByProc, result);
            Assert.True(target.IsDead);

            // ...but the swing that preceded it is applied and attributed.
            Assert.NotEmpty(target.Attackers);
            Assert.Contains(target.Attackers, a => a.Uid == attacker.Uid);
        }
        finally
        {
            CombatEngine.OnHitSpell = savedSpell;
        }
    }

    [Fact]
    public void SwingSpeedUsesConfiguredSourceXScaleFactor()
    {
        int oldEra = Character.CombatSpeedEra;
        int oldScale = Character.CombatSpeedScaleFactor;
        try
        {
            Character.CombatSpeedEra = 1;
            // Era 1 reads the current stamina pool, not the base stat.
            var attacker = new Character { Dex = 100, Stam = 100 };
            var weapon = new Item { ItemType = ItemType.WeaponSword };

            Character.CombatSpeedScaleFactor = 10_000;
            int fastScale = CombatEngine.GetSwingDelayMs(attacker, weapon);
            Character.CombatSpeedScaleFactor = 20_000;
            int slowScale = CombatEngine.GetSwingDelayMs(attacker, weapon);

            Assert.Equal(1_000, fastScale);
            Assert.Equal(2_000, slowScale);
            attacker.SetStatFlag(StatFlag.OnHorse);
            Assert.Equal(slowScale, CombatEngine.GetSwingDelayMs(attacker, weapon));
        }
        finally
        {
            Character.CombatSpeedEra = oldEra;
            Character.CombatSpeedScaleFactor = oldScale;
        }
    }

    [Fact]
    public void WeaponDamageTypeHonorsRuntimeOverride()
    {
        var weapon = new Item { ItemType = ItemType.WeaponSword };
        weapon.SetTag("OVERRIDE.DAMAGETYPE", "0x28");

        Assert.Equal(DamageType.Fire | DamageType.Energy,
            CombatEngine.GetWeaponDamageType(weapon));
    }

    [Fact]
    public void ElementalPercentagesAreNotSilentlyRenormalized()
    {
        var attacker = new Character { DamPhysical = 25 };
        var target = new Character();

        Assert.Equal(25, CombatEngine.ApplyElementalDamageSplit(attacker, target, 100, null));
    }

    [Fact]
    public void RangedAmmoSearchIncludesNestedBags()
    {
        var world = TestHarness.CreateWorld();
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        var bag = world.CreateItem();
        bag.ItemType = ItemType.Container;
        pack.AddItem(bag);
        var arrows = world.CreateItem();
        arrows.ItemType = ItemType.WeaponArrow;
        arrows.Amount = 10;
        bag.AddItem(arrows);

        Assert.Same(arrows, CombatHelper.FindAmmoInContainer(
            pack, baseId: 0, fallbackType: ItemType.WeaponArrow));
    }

    [Fact]
    public void DeathClearsAttackerLogAfterAwardingCredit()
    {
        var world = TestHarness.CreateWorld();
        var victim = MakeCharacter(world, 100, 100);
        var killer = MakeCharacter(world, 101, 100);
        victim.RecordAttack(killer.Uid, 25);

        var corpse = new DeathEngine(world).ProcessDeath(victim);

        Assert.True(victim.IsDead);
        Assert.Empty(victim.Attackers);
        Assert.NotNull(corpse);
        Assert.True(corpse!.TryGetTag("KILLER_UID", out string? killerUid));
        Assert.Equal(killer.Uid.Value.ToString(), killerUid);
    }

    [Fact]
    public void KillTriggerCancellationOnlySkipsThatAttacker()
    {
        var world = TestHarness.CreateWorld();
        var victim = MakeCharacter(world, 100, 100);
        victim.Fame = 1_000;
        victim.Karma = 1_000;
        var blocked = MakeCharacter(world, 101, 100);
        var credited = MakeCharacter(world, 102, 100);
        victim.RecordAttack(blocked.Uid, 25);
        victim.RecordAttack(credited.Uid, 20);
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Kill", (ch, _) =>
            ch == blocked ? TriggerResult.True : TriggerResult.Default);

        new DeathEngine(world) { TriggerDispatcher = dispatcher }
            .ProcessDeath(victim, blocked);

        Assert.Equal(0, blocked.Fame);
        Assert.True(credited.Fame > 0);
    }

    private static Character MakeCharacter(GameWorld world, short x, short y, byte map = 0)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = ch.Dex = ch.Int = 100;
        ch.MaxHits = ch.Hits = 100;
        ch.MaxStam = ch.Stam = 100;
        ch.SetSkill(SkillType.Swordsmanship, 1000);
        ch.SetSkill(SkillType.Tactics, 1000);
        world.PlaceCharacter(ch, new Point3D(x, y, 0, map));
        return ch;
    }

    private static (GameClient Client, GameWorld World, Character Attacker,
        Character Target, Item Bow, Item Arrows) MakeRangedFight(int port)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(loggerFactory, world,
            new AccountManager(loggerFactory), port);
        var attacker = MakeCharacter(world, 100, 100);
        attacker.PrivLevel = PrivLevel.GM;
        attacker.SetSkill(SkillType.Archery, 1000);
        attacker.SetStatFlag(StatFlag.War);
        TestHarness.AttachCharacter(client, attacker);
        client.BroadcastNearby = (_, _, _, _) => { };

        var target = MakeCharacter(world, 105, 100);
        var bow = world.CreateItem();
        bow.ItemType = ItemType.WeaponBow;
        bow.BaseId = 0x13B2;
        attacker.Equip(bow, Layer.TwoHanded);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        attacker.Backpack = pack;
        var arrows = world.CreateItem();
        arrows.ItemType = ItemType.WeaponArrow;
        arrows.BaseId = 0x0F3F;
        arrows.Amount = 2;
        pack.AddItem(arrows);

        attacker.FightTarget = target.Uid;
        attacker.NextAttackTime = 0;
        return (client, world, attacker, target, bow, arrows);
    }
}
