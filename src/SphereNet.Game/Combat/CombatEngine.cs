using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;

namespace SphereNet.Game.Combat;

/// <summary>
/// Combat flags. Maps exactly to COMBATFLAGS_TYPE in Source-X CServerConfig.h.
/// </summary>
[Flags]
public enum CombatFlags : uint
{
    None = 0,
    NoDirChange = 0x1,
    FaceCombat = 0x2,
    PreHit = 0x4,
    ElementalEngine = 0x8,
    DClickSelfUnmounts = 0x20,
    AllowHitFromShip = 0x40,
    NoPetDesert = 0x80,
    ArcheryCanMove = 0x100,
    StayInRange = 0x200,
    StackArmor = 0x1000,
    NoPoisonHit = 0x2000,
    Slayer = 0x4000,
    SwingNoRange = 0x8000,
    AnimHitSmooth = 0x10000,
    FirstHitInstant = 0x20000,
    NpcBonusDamage = 0x40000,
    ParalyzeCanSwing = 0x80000,
    AttackNoAggreived = 0x100000,
}

/// <summary>
/// Combat swing state. Maps to WAR_SWING_TYPE in Source-X.
/// </summary>
public enum SwingState
{
    Invalid = -1,
    Equipping = 0,
    Ready = 1,
    Swinging = 2,
    EquippingNoWait = 10,
}

/// <summary>
/// Damage type flags. Maps to DAMAGE_TYPE in Source-X.
/// </summary>
[Flags]
public enum DamageType : ushort
{
    General = 0x00,
    Physical = 0x01,
    Magic = 0x02,
    Poison = 0x04,
    Fire = 0x08,
    Cold = 0x10,
    Energy = 0x20,
    HitBlunt = 0x40,
    HitPierce = 0x80,
    HitSlash = 0x100,
    God = 0x200,
    NoReveal = 0x400,
    NoUnparalyze = 0x800,
    Fixed = 0x1000,
}

/// <summary>
/// Core combat engine. Maps to CChar::Fight_* functions in Source-X CCharFight.cpp.
/// Handles hit/miss, damage calculation, armor, and weapon skill routing.
/// </summary>
public static class CombatEngine
{
    private static readonly Random _rand = new();

    /// <summary>
    /// Calculate hit chance. Maps to CServerConfig::Calc_CombatChanceToHit.
    /// Era 0 = Sphere custom, 1 = pre-AOS, 2 = AOS.
    /// </summary>
    public static int CalcHitChance(Character attacker, Character target, int era = 0)
    {
        int attackSkill = attacker.GetSkill(GetWeaponSkill(attacker));
        int targetSkill = target.GetSkill(GetWeaponSkill(target));
        int tacticsAtk = attacker.GetSkill(SkillType.Tactics);
        int tacticsDef = target.GetSkill(SkillType.Tactics);

        switch (era)
        {
            case 1: // pre-AOS
            {
                int chance = (attackSkill + 500) * 100 / Math.Max(1, (targetSkill + 500) * 2);
                return Math.Clamp(chance, 0, 100);
            }
            case 2: // AOS
            {
                int atkCalc = (attackSkill / 10 + 20) * 100;
                int defCalc = (targetSkill / 10 + 20) * 100;
                int chance = atkCalc * 100 / Math.Max(1, defCalc * 2);
                return Math.Clamp(chance, 2, 100);
            }
            default: // Sphere custom (era 0)
            {
                // Source-X Calc_CombatChanceToHit: base 50% shifted by
                // the attack/defend skill difference, clamped 5-95%.
                int iSkillAttack = (attackSkill + tacticsAtk) / 2;
                int iSkillDefend = (targetSkill + tacticsDef) / 2;
                int iDiff = iSkillAttack - iSkillDefend;
                int chance = 50 + iDiff / 10;
                return Math.Clamp(chance, 5, 95);
            }
        }
    }

    /// <summary>
    /// Calculate weapon damage range. Maps to Fight_CalcDamage in Source-X.
    /// </summary>
    /// <summary>Optional lookup for weapon definitions (BaseId → (damMin, damMax)).</summary>
    public static Func<ushort, (int Min, int Max)?>? WeaponDefLookup { get; set; }

    public static (int Min, int Max) CalcWeaponDamage(Character attacker, Item? weapon, int era = 0)
    {
        int dmgMin, dmgMax;

        if (weapon == null)
        {
            dmgMin = 1;
            dmgMax = Math.Max(1, attacker.Str / 4);
        }
        else
        {
            // Try to look up actual weapon damage from ItemDef
            var defDamage = WeaponDefLookup?.Invoke(weapon.BaseId);
            if (defDamage.HasValue && defDamage.Value.Max > 0)
            {
                dmgMin = defDamage.Value.Min;
                dmgMax = defDamage.Value.Max;
            }
            else
            {
                dmgMin = Math.Max(1, weapon.BaseId / 10);
                dmgMax = dmgMin + 5;
            }
        }

        // Stat bonus
        int strBonus;
        switch (era)
        {
            case 1: // pre-AOS: STR * 20%
                strBonus = attacker.Str * 20 / 100;
                break;
            case 2: // AOS: STR * 30%
                strBonus = attacker.Str * 30 / 100;
                break;
            default: // Sphere: STR * 10%
                strBonus = attacker.Str * 10 / 100;
                break;
        }

        // Tactics bonus
        int tacticsBonus = era switch
        {
            1 => (attacker.GetSkill(SkillType.Tactics) - 500) / 10,
            2 => attacker.GetSkill(SkillType.Tactics) / 16,
            _ => attacker.GetSkill(SkillType.Tactics) / 20,
        };

        // Anatomy bonus
        int anatomyBonus = era switch
        {
            2 => attacker.GetSkill(SkillType.Anatomy) / 20,
            _ => attacker.GetSkill(SkillType.Anatomy) / 50,
        };

        int totalBonus = strBonus + tacticsBonus + anatomyBonus;
        dmgMin += dmgMin * totalBonus / 100;
        dmgMax += dmgMax * totalBonus / 100;

        return (Math.Max(1, dmgMin), Math.Max(1, dmgMax));
    }

    /// <summary>
    /// Calculate armor defense rating. Maps to CalcArmorDefense in Source-X.
    /// Non-elemental: sums equipped armor AR by body region coverage.
    /// </summary>
    public static int CalcArmorDefense(Character defender, bool elementalEngine = false)
    {
        if (elementalEngine)
            return 0; // elemental uses per-type resists

        int totalAR = 0;
        // Check equipment layers for armor
        Layer[] armorLayers = [Layer.Helm, Layer.Neck, Layer.Chest, Layer.Arms, Layer.Gloves, Layer.Legs, Layer.Shoes, Layer.TwoHanded];
        int[] coverage = [15, 7, 35, 14, 7, 22, 3, 0]; // body region coverage %

        for (int i = 0; i < armorLayers.Length; i++)
        {
            var item = defender.GetEquippedItem(armorLayers[i]);
            if (item == null) continue;

            int ar = item.GetArmorDefense();
            totalAR += coverage[i] * ar;
        }

        return Math.Max(0, totalAR / 100);
    }

    /// <summary>
    /// Perform a full attack resolution. Returns damage dealt (0 = miss/blocked).
    /// Maps to CChar::Fight_Hit flow in Source-X.
    /// </summary>
    public static int ResolveAttack(Character attacker, Character target, Item? weapon, CombatFlags flags = CombatFlags.None, int era = 0)
    {
        if (attacker.IsDead || target.IsDead)
            return 0;

        // Hit check
        int hitChance = CalcHitChance(attacker, target, era);
        if (_rand.Next(100) >= hitChance)
            return 0; // miss

        // Calculate raw damage
        var (dmgMin, dmgMax) = CalcWeaponDamage(attacker, weapon, era);
        int damage = _rand.Next(dmgMin, dmgMax + 1);

        // Parry check (simplified)
        int parrySkill = target.GetSkill(SkillType.Parrying);
        var shield = target.GetEquippedItem(Layer.TwoHanded);
        if (shield != null && parrySkill > 0)
        {
            int parryChance = parrySkill / 30;
            if (_rand.Next(100) < parryChance)
                return 0; // parried
        }

        // Armor reduction
        if (flags.HasFlag(CombatFlags.ElementalEngine))
        {
            // Split damage by attacker's elemental percentages, apply per-element resist
            damage = ApplyElementalDamageSplit(attacker, target, damage, weapon);
        }
        else
        {
            int armorRating = CalcArmorDefense(target);
            int arMax = armorRating * _rand.Next(7, 36) / 100;
            int arMin = arMax / 2;
            int defense = _rand.Next(arMin, arMax + 1);
            damage -= defense;
        }

        damage = Math.Max(0, damage);

        // Apply damage — do NOT call Kill() here; the caller handles death
        // via DeathEngine.ProcessDeath which creates the corpse, drops loot,
        // and sends the delete packet. Calling Kill() early makes
        // ProcessDeath bail out ("already dead") leaving a ghost NPC.
        if (damage > 0)
        {
            target.Hits -= (short)damage;
            target.RecordAttack(attacker.Uid, damage);

            if (target.IsStatFlag(StatFlag.Reactive) && attacker != target && !attacker.IsDead)
            {
                int reflect = Math.Max(1, damage / 4);
                attacker.Hits -= (short)reflect;
            }
        }

        return damage;
    }

    /// <summary>
    /// Get the combat skill used by the attacker's weapon.
    /// Maps to CItem::Weapon_GetSkill in Source-X.
    /// </summary>
    public static SkillType GetWeaponSkill(Character ch)
    {
        var weapon = ch.GetEquippedItem(Layer.OneHanded) ?? ch.GetEquippedItem(Layer.TwoHanded);
        if (weapon == null)
            return SkillType.Wrestling;

        return weapon.ItemType switch
        {
            ItemType.WeaponSword or ItemType.WeaponAxe => SkillType.Swordsmanship,
            ItemType.WeaponFence => SkillType.Fencing,
            ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or
            ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or
            ItemType.WeaponMacePick or ItemType.WeaponWhip => SkillType.MaceFighting,
            ItemType.WeaponBow or ItemType.WeaponXBow => SkillType.Archery,
            ItemType.WeaponThrowing => SkillType.Throwing,
            _ => SkillType.Wrestling,
        };
    }

    /// <summary>
    /// Get the damage type flags based on weapon type.
    /// Maps to Fight_GetWeaponDamType in Source-X.
    /// </summary>
    public static DamageType GetWeaponDamageType(Item? weapon)
    {
        if (weapon == null)
            return DamageType.HitBlunt;

        return weapon.ItemType switch
        {
            ItemType.WeaponSword or ItemType.WeaponAxe or ItemType.WeaponThrowing
                => DamageType.HitBlunt | DamageType.HitSlash,
            ItemType.WeaponFence or ItemType.WeaponBow or ItemType.WeaponXBow
                => DamageType.HitBlunt | DamageType.HitPierce,
            _ => DamageType.HitBlunt,
        };
    }

    /// <summary>
    /// Apply elemental resist to damage. Maps to COMBAT_ELEMENTAL_ENGINE in Source-X.
    /// Splits damage by attacker's elemental percentages then applies per-element resist.
    /// </summary>
    public static int ApplyElementalDamageSplit(Character attacker, Character target, int damage, Item? weapon)
    {
        int physPct = attacker.DamPhysical;
        int firePct = attacker.DamFire;
        int coldPct = attacker.DamCold;
        int poisonPct = attacker.DamPoison;
        int energyPct = attacker.DamEnergy;

        // If no elemental split defined, fall back to weapon damage type
        if (firePct == 0 && coldPct == 0 && poisonPct == 0 && energyPct == 0)
            return ApplyElementalResist(target, damage, GetWeaponDamageType(weapon));

        int total = 0;
        if (physPct > 0) total += ApplyElementalResist(target, damage * physPct / 100, DamageType.Physical);
        if (firePct > 0) total += ApplyElementalResist(target, damage * firePct / 100, DamageType.Fire);
        if (coldPct > 0) total += ApplyElementalResist(target, damage * coldPct / 100, DamageType.Cold);
        if (poisonPct > 0) total += ApplyElementalResist(target, damage * poisonPct / 100, DamageType.Poison);
        if (energyPct > 0) total += ApplyElementalResist(target, damage * energyPct / 100, DamageType.Energy);
        return total;
    }

    /// <summary>
    /// Uses the highest matching resist for the damage type.
    /// </summary>
    public static int ApplyElementalResist(Character target, int damage, DamageType dmgType)
    {
        if (damage <= 0) return 0;

        int resist = 0;

        // Physical damage types (blunt/slash/pierce)
        if (dmgType.HasFlag(DamageType.Physical) || dmgType.HasFlag(DamageType.HitBlunt) ||
            dmgType.HasFlag(DamageType.HitSlash) || dmgType.HasFlag(DamageType.HitPierce))
            resist = Math.Max(resist, target.ResPhysical);

        if (dmgType.HasFlag(DamageType.Fire))
            resist = Math.Max(resist, target.ResFire);

        if (dmgType.HasFlag(DamageType.Cold))
            resist = Math.Max(resist, target.ResCold);

        if (dmgType.HasFlag(DamageType.Poison))
            resist = Math.Max(resist, target.ResPoison);

        if (dmgType.HasFlag(DamageType.Energy) || dmgType.HasFlag(DamageType.Magic))
            resist = Math.Max(resist, target.ResEnergy);

        // God damage ignores resist
        if (dmgType.HasFlag(DamageType.God))
            resist = 0;

        resist = Math.Clamp(resist, 0, 100);
        return damage - (damage * resist / 100);
    }

    /// <summary>
    /// Pre-AOS swing delay (Source-X <c>Calc_CombatAttackSpeed</c> formula 0).
    /// Returns the full swing recoil in milliseconds.
    /// </summary>
    public static int GetSwingDelayMs(Character attacker, Item? weapon)
    {
        const int speedScaleFactor = 15000;

        int baseSpeed = weapon?.Speed > 0 ? weapon.Speed : 0;
        if (baseSpeed <= 0)
            baseSpeed = weapon == null ? 50 : 35;

        int dex = Math.Max(0, (int)attacker.Dex);
        long iSwingSpeed = (long)(dex + 100) * baseSpeed;
        if (iSwingSpeed < 1) iSwingSpeed = 1;

        long deciseconds = (speedScaleFactor * 10L) / iSwingSpeed;
        if (deciseconds < 5) deciseconds = 5;

        if (weapon != null && weapon.IsTwoHanded)
            deciseconds += deciseconds / 4;

        int delayMs = (int)(deciseconds * 100);

        if (attacker.IsMounted)
            delayMs -= 200;

        return Math.Clamp(delayMs, 500, 7000);
    }
}
