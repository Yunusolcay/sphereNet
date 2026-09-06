using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Combat: who a reflected blow may hurt, and where a resource search may look.
///
/// Reflected damage went straight to attacker.Hits on the belief that "fixed damage
/// cannot recurse". Source-X sends it back through the ordinary entry -
/// OnTakeDamage(dam, src, DAMAGE_FIXED | DAMAGE_REACTIVE), CCharFight.cpp:1021 -
/// whose first act is to bounce anything without DAMAGE_GOD off an invulnerable
/// target (:642). Recursion is stopped by DAMAGE_REACTIVE (:1015), not by the damage
/// being fixed, so writing HP directly bought nothing and skipped the immunity gate.
///
/// Resource searches recursed into every container. Source-X gates the descent on
/// IsSearchable (CContainer::ContentFind, CContainer.cpp:236), so a locked chest in
/// the pack is not part of the stock a bow or a craft may draw from.
/// </summary>
public sealed class CombatParity03ATests
{
    private static Character MakeChar(GameWorld world, int x, int hits)
    {
        var ch = world.CreateCharacter();
        ch.Str = 100;
        ch.MaxHits = (short)hits;
        ch.Hits = (short)hits;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static Item MakePack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    // --- SX-03A-02: reflection respects immunity ----------------------------

    [Fact]
    public void AnInvulnerableAttackerTakesNoReflectedDamage()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var defender = MakeChar(world, 101, 100);
        attacker.SetStatFlag(StatFlag.Invul);

        Assert.Equal(0, CombatEngine.ApplyReflectedDamage(attacker, defender, 20));
        Assert.Equal(100, attacker.Hits);
    }

    [Fact]
    public void AnOrdinaryAttackerStillTakesReflectedDamageAndCreditsIt()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var defender = MakeChar(world, 101, 100);

        Assert.Equal(20, CombatEngine.ApplyReflectedDamage(attacker, defender, 20));
        Assert.Equal(80, attacker.Hits);
        // Credited so a reflect kill attributes to the defender.
        Assert.Contains(attacker.Attackers, a => a.Uid == defender.Uid);
    }

    [Fact]
    public void ReflectedDamageIsNotAppliedToTheDeadOrDeleted()
    {
        var world = TestHarness.CreateWorld();
        var attacker = MakeChar(world, 100, 100);
        var defender = MakeChar(world, 101, 100);
        attacker.SetStatFlag(StatFlag.Dead);

        Assert.Equal(0, CombatEngine.ApplyReflectedDamage(attacker, defender, 20));
        Assert.Equal(100, attacker.Hits);
    }

    [Fact]
    public void ReflectPhysicalDamDoesNotHurtAnInvulnerableAttacker()
    {
        var savedHook = CombatEngine.OnHitDamage;
        int savedEra = Character.CombatHitChanceEra;
        try
        {
            Character.CombatHitChanceEra = 0;
            CombatEngine.OnHitDamage = ctx => 20;

            var world = TestHarness.CreateWorld();
            var attacker = MakeChar(world, 100, 100);
            attacker.PrivLevel = PrivLevel.GM;      // deterministic hit, not a privilege claim
            attacker.SetStatFlag(StatFlag.Invul);
            var target = MakeChar(world, 101, 100);
            target.SetTag("REFLECTPHYSICALDAM", "100");

            CombatEngine.ResolveAttack(attacker, target, null);

            Assert.Equal(80, target.Hits);
            Assert.Equal(100, attacker.Hits);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            Character.CombatHitChanceEra = savedEra;
        }
    }

    [Fact]
    public void ReactiveArmorDoesNotHurtAnInvulnerableAttacker()
    {
        var savedHook = CombatEngine.OnHitDamage;
        int savedEra = Character.CombatHitChanceEra;
        try
        {
            Character.CombatHitChanceEra = 0;
            CombatEngine.OnHitDamage = ctx => 20;

            var world = TestHarness.CreateWorld();
            var attacker = MakeChar(world, 100, 100);
            attacker.PrivLevel = PrivLevel.GM;
            attacker.SetStatFlag(StatFlag.Invul);
            var target = MakeChar(world, 101, 100);
            target.SetStatFlag(StatFlag.Reactive);

            CombatEngine.ResolveAttack(attacker, target, null);

            Assert.Equal(100, attacker.Hits);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            Character.CombatHitChanceEra = savedEra;
        }
    }

    [Fact]
    public void APoisonTickCannotHarmAnInvulnerableVictim()
    {
        var world = TestHarness.CreateWorld();
        var victim = MakeChar(world, 100, 100);
        victim.SetStatFlag(StatFlag.Invul);
        victim.ApplyPoison(4);

        for (int i = 0; i < 10; i++)
            victim.ProcessPoisonTick(Environment.TickCount64 + (i * 5000));

        Assert.Equal(100, victim.Hits);
    }

    // --- SX-03A-03: the swing lands before its procs ------------------------

    [Fact]
    public void AnOnHitProcSeesTheDamageTheSwingAlreadyDid()
    {
        var savedHook = CombatEngine.OnHitDamage;
        var savedSpell = CombatEngine.OnHitSpell;
        int savedEra = Character.CombatHitChanceEra;
        try
        {
            Character.CombatHitChanceEra = 0;
            CombatEngine.OnHitDamage = ctx => 20;

            var world = TestHarness.CreateWorld();
            var attacker = MakeChar(world, 100, 100);
            attacker.PrivLevel = PrivLevel.GM;
            var target = MakeChar(world, 101, 100);
            var weapon = new Item { ItemType = ItemType.WeaponSword };
            weapon.SetTag("HITFIREBALL", "100");

            int observed = -1;
            CombatEngine.OnHitSpell = (_, victim, _) => observed = victim.Hits;

            CombatEngine.ResolveAttack(attacker, target, weapon);

            // Source-X Fight_Hit: OnTakeDamage first (:2259), procs after (:2270).
            Assert.Equal(80, observed);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            CombatEngine.OnHitSpell = savedSpell;
            Character.CombatHitChanceEra = savedEra;
        }
    }

    [Fact]
    public void AnInvulnerableTargetIsNotSwungAtAtAll()
    {
        // Worth pinning because it bounds the fix above: SphereNet refuses the swing
        // outright (CombatHelper.IsInvalidSwingParticipant treats Invul as an invalid
        // target), so no damage and no proc ever run. Source-X instead swings and
        // bounces the blow inside OnTakeDamage, which means its procs DO fire against
        // an invulnerable target. That difference is not addressed here - it was not
        // part of this round's findings - but it is why the proc call is deliberately
        // left outside the immunity block rather than moved inside it.
        var savedHook = CombatEngine.OnHitDamage;
        var savedSpell = CombatEngine.OnHitSpell;
        int savedEra = Character.CombatHitChanceEra;
        try
        {
            Character.CombatHitChanceEra = 0;
            CombatEngine.OnHitDamage = ctx => 20;

            var world = TestHarness.CreateWorld();
            var attacker = MakeChar(world, 100, 100);
            attacker.PrivLevel = PrivLevel.GM;
            var target = MakeChar(world, 101, 100);
            target.SetStatFlag(StatFlag.Invul);
            var weapon = new Item { ItemType = ItemType.WeaponSword };
            weapon.SetTag("HITFIREBALL", "100");

            bool fired = false;
            CombatEngine.OnHitSpell = (_, _, _) => fired = true;

            Assert.Equal(0, CombatEngine.ResolveAttack(attacker, target, weapon));
            Assert.False(fired);
            Assert.Equal(100, target.Hits);
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
            CombatEngine.OnHitSpell = savedSpell;
            Character.CombatHitChanceEra = savedEra;
        }
    }

    // --- SX-03A-01: searches respect the searchable contract ----------------

    [Fact]
    public void AmmoInsideALockedChestIsNotFound()
    {
        var world = TestHarness.CreateWorld();
        var archer = MakeChar(world, 100, 100);
        var pack = MakePack(world, archer);

        var chest = world.CreateItem();
        chest.ItemType = ItemType.ContainerLocked;
        Assert.True(pack.TryAddItem(chest));

        var arrows = world.CreateItem();
        arrows.BaseId = 0x0F3F; arrows.Amount = 10;
        Assert.True(chest.TryAddItem(arrows));

        Assert.Null(CombatHelper.FindAmmoInContainer(pack, 0x0F3F, ItemType.Normal));
    }

    [Fact]
    public void AmmoInsideAnOrdinaryPouchIsStillFound()
    {
        var world = TestHarness.CreateWorld();
        var archer = MakeChar(world, 100, 100);
        var pack = MakePack(world, archer);

        var pouch = world.CreateItem();
        pouch.ItemType = ItemType.Container;
        Assert.True(pack.TryAddItem(pouch));

        var arrows = world.CreateItem();
        arrows.BaseId = 0x0F3F; arrows.Amount = 10;
        Assert.True(pouch.TryAddItem(arrows));

        Assert.Same(arrows, CombatHelper.FindAmmoInContainer(pack, 0x0F3F, ItemType.Normal));
    }

    [Theory]
    [InlineData(ItemType.ContainerLocked)]
    [InlineData(ItemType.EqBankBox)]
    [InlineData(ItemType.EqVendorBox)]
    [InlineData(ItemType.EqTradeWindow)]
    public void NoUnsearchableContainerContributesAmmo(ItemType type)
    {
        var world = TestHarness.CreateWorld();
        var archer = MakeChar(world, 100, 100);
        var pack = MakePack(world, archer);

        var box = world.CreateItem();
        box.ItemType = type;
        Assert.True(pack.TryAddItem(box));

        var arrows = world.CreateItem();
        arrows.BaseId = 0x0F3F; arrows.Amount = 10;
        Assert.True(box.TryAddItem(arrows));

        Assert.Null(CombatHelper.FindAmmoInContainer(pack, 0x0F3F, ItemType.Normal));
    }
}
