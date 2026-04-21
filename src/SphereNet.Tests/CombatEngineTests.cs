using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

public class CombatEngineTests
{
    private static Character MakeChar(short str = 50, short dex = 50, short intel = 50)
    {
        var ch = new Character();
        ch.Str = str; ch.Dex = dex; ch.Int = intel;
        ch.MaxHits = str; ch.MaxMana = intel; ch.MaxStam = dex;
        ch.Hits = str; ch.Mana = intel; ch.Stam = dex;
        ch.SetSkill(SkillType.Swordsmanship, 800);
        ch.SetSkill(SkillType.Tactics, 800);
        ch.SetSkill(SkillType.Anatomy, 500);
        ch.SetSkill(SkillType.Parrying, 500);
        return ch;
    }

    [Fact]
    public void GetWeaponSkill_Unarmed_ReturnsWrestling()
    {
        var ch = MakeChar();
        Assert.Equal(SkillType.Wrestling, CombatEngine.GetWeaponSkill(ch));
    }

    [Fact]
    public void CalcHitChance_Era0_ReturnsBetween0And100()
    {
        var attacker = MakeChar();
        var target = MakeChar();
        int chance = CombatEngine.CalcHitChance(attacker, target, 0);
        Assert.InRange(chance, 0, 100);
    }

    [Fact]
    public void CalcHitChance_Era1_PreAOS_ReturnsBetween0And100()
    {
        var attacker = MakeChar();
        var target = MakeChar();
        int chance = CombatEngine.CalcHitChance(attacker, target, 1);
        Assert.InRange(chance, 0, 100);
    }

    [Fact]
    public void CalcHitChance_Era2_AOS_ClampsMinTo2()
    {
        var attacker = MakeChar(str: 10);
        attacker.SetSkill(SkillType.Swordsmanship, 0);
        attacker.SetSkill(SkillType.Tactics, 0);
        var target = MakeChar(str: 100);
        int chance = CombatEngine.CalcHitChance(attacker, target, 2);
        Assert.True(chance >= 2, $"AOS hit chance should be >= 2, got {chance}");
    }

    [Fact]
    public void CalcWeaponDamage_Unarmed_MinIsAtLeast1()
    {
        var ch = MakeChar(str: 10);
        var (min, max) = CombatEngine.CalcWeaponDamage(ch, null, 0);
        Assert.True(min >= 1);
        Assert.True(max >= min);
    }

    [Fact]
    public void CalcWeaponDamage_HigherStr_HigherDamage()
    {
        var weak = MakeChar(str: 10);
        var strong = MakeChar(str: 100);
        var (_, maxWeak) = CombatEngine.CalcWeaponDamage(weak, null, 0);
        var (_, maxStrong) = CombatEngine.CalcWeaponDamage(strong, null, 0);
        Assert.True(maxStrong > maxWeak);
    }

    [Fact]
    public void CalcArmorDefense_NoArmor_ReturnsZero()
    {
        var ch = MakeChar();
        int ar = CombatEngine.CalcArmorDefense(ch);
        Assert.Equal(0, ar);
    }

    [Fact]
    public void CalcArmorDefense_Elemental_ReturnsZero()
    {
        var ch = MakeChar();
        int ar = CombatEngine.CalcArmorDefense(ch, elementalEngine: true);
        Assert.Equal(0, ar);
    }

    [Fact]
    public void CalcArmorDefense_UsesArmorValueFromItemDefOrTag()
    {
        var ch = MakeChar();
        var chest = new Item();
        chest.SetTag("ARMOR", "40");
        ch.Equip(chest, Layer.Chest);

        int ar = CombatEngine.CalcArmorDefense(ch);

        Assert.Equal(14, ar);
    }

    [Fact]
    public void ResolveAttack_DeadAttacker_ReturnsZero()
    {
        var attacker = MakeChar();
        attacker.Kill();
        var target = MakeChar();
        int dmg = CombatEngine.ResolveAttack(attacker, target, null);
        Assert.Equal(0, dmg);
    }

    [Fact]
    public void ResolveAttack_DeadTarget_ReturnsZero()
    {
        var attacker = MakeChar();
        var target = MakeChar();
        target.Kill();
        int dmg = CombatEngine.ResolveAttack(attacker, target, null);
        Assert.Equal(0, dmg);
    }

    [Fact]
    public void GetWeaponDamageType_NullWeapon_ReturnsBlunt()
    {
        var type = CombatEngine.GetWeaponDamageType(null);
        Assert.Equal(DamageType.HitBlunt, type);
    }
}
