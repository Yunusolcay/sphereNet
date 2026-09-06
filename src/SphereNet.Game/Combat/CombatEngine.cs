using SphereNet.Core.Enums;
using SphereNet.Game.Objects;
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

public enum ArmorHitRegion
{
    Head,
    Neck,
    Chest,
    Arms,
    Hands,
    Legs,
    Feet,
}

/// <summary>
/// On-hit trigger context threaded through <see cref="CombatEngine.OnHitDamage"/>
/// (the Source-X CChar::OnTakeDamage @GetHit block, CCharFight.cpp:750). The
/// engine seeds the armor-damage roll and the elemental split; the hook exposes
/// them to scripts as LOCAL.* and writes script changes back before the engine
/// rolls the durability wear.
/// </summary>
public sealed class HitDamageContext
{
    public required Character Attacker { get; init; }
    public required Character Target { get; init; }
    public Item? Weapon { get; init; }
    public int Damage { get; set; }

    /// <summary>Layer whose worn item takes the item @GetHit trigger and the
    /// durability wear (LOCAL.ItemDamageLayer, script-writable).</summary>
    public Layer ItemDamageLayer { get; set; }

    /// <summary>% chance the ItemDamageLayer item takes durability wear
    /// (LOCAL.ItemDamageChance, script-writable; Source-X seeds 25).</summary>
    public int ItemDamageChance { get; set; } = 25;

    /// <summary>Set by the hook when a trigger RETURNed 1: the hit is fully
    /// cancelled and skips the armor durability roll (Source-X returns 0
    /// before it).</summary>
    public bool Cancelled { get; set; }

    /// <summary>% chance the attacker's weapon takes durability wear on a hit
    /// (LOCAL.ItemDamageChance in the @Hit trigger args, script-writable;
    /// Source-X seeds 25 — a separate roll from the @GetHit armor-side
    /// ItemDamageChance above).</summary>
    public int WeaponDamageChance { get; set; } = 25;

    /// <summary>% chance a poisoned weapon loses poison charges after
    /// delivering (LOCAL.ItemPoisonReductionChance, script-writable;
    /// Source-X seeds 100).</summary>
    public int PoisonReductionChance { get; set; } = 100;

    /// <summary>Poison charges removed when the reduction chance passes
    /// (LOCAL.ItemPoisonReductionAmount, script-writable; SphereNet's
    /// level+charges poison model spends 1 per delivery by default).</summary>
    public int PoisonReductionAmount { get; set; } = 1;

    /// <summary>UID of the pack ammo stack a ranged shot draws from (0 for
    /// melee) — exposed to @Hit and the weapon item @Hit as LOCAL.Arrow
    /// (Source-X CCharFight.cpp:2158).</summary>
    public uint AmmoUid { get; init; }

    /// <summary>Set when a @Hit-chain script wrote LOCAL.ArrowHandled=1: the
    /// script owns the ammo's fate, so the caller must neither consume the
    /// stack nor run the stick-in-body economy (Source-X pAmmo = nullptr).</summary>
    public bool ArrowHandled { get; set; }

    /// <summary>COMBAT_ELEMENTAL_ENGINE active — the DamagePercent* split below
    /// is exposed to @GetHit as read-only locals.</summary>
    public bool Elemental { get; init; }
    public int DamPercentPhysical { get; init; }
    public int DamPercentFire { get; init; }
    public int DamPercentCold { get; init; }
    public int DamPercentPoison { get; init; }
    public int DamPercentEnergy { get; init; }
}

/// <summary>Source-X CObjBase DAMAGE verb context. Unlike a weapon hit this
/// fires only the target-side @GetHit chain; there is no attacker @Hit or
/// weapon/item-on-hit stage.</summary>
public sealed class DirectDamageContext
{
    public required Character Target { get; init; }
    public Character? Source { get; init; }
    public int Damage { get; set; }
    public DamageType DamageType { get; init; }
    public int PhysicalPercent { get; set; }
    public int FirePercent { get; set; }
    public int ColdPercent { get; set; }
    public int PoisonPercent { get; set; }
    public int EnergyPercent { get; set; }
    public bool Cancelled { get; set; }
}

/// <summary>
/// Core combat engine. Maps to CChar::Fight_* functions in Source-X CCharFight.cpp.
/// Handles hit/miss, damage calculation, armor, and weapon skill routing.
/// </summary>
public static class CombatEngine
{
    public const int AttackMiss = -1;
    public const int AttackParried = -2;
    public const int AttackResolvedByProc = -3;

    private static Random _rand => Random.Shared;
    private static readonly (ArmorHitRegion Region, Layer Layer, int Weight)[] _armorRegions =
    [
        (ArmorHitRegion.Head, Layer.Helm, 14),
        (ArmorHitRegion.Neck, Layer.Neck, 7),
        (ArmorHitRegion.Chest, Layer.Chest, 35),
        (ArmorHitRegion.Arms, Layer.Arms, 14),
        (ArmorHitRegion.Hands, Layer.Gloves, 7),
        (ArmorHitRegion.Legs, Layer.Legs, 22),
        (ArmorHitRegion.Feet, Layer.Shoes, 1),
    ];

    // Layers covering each hit region when COMBAT_STACKARMOR is on: a region
    // protected by several worn pieces (e.g. a tunic under a robe over the
    // chest) sums all their AR instead of only the primary layer's.
    private static readonly Dictionary<ArmorHitRegion, Layer[]> _stackArmorLayers = new()
    {
        [ArmorHitRegion.Head] = [Layer.Helm],
        [ArmorHitRegion.Neck] = [Layer.Neck],
        [ArmorHitRegion.Chest] = [Layer.Shirt, Layer.Chest, Layer.Tunic, Layer.Robe],
        [ArmorHitRegion.Arms] = [Layer.Arms, Layer.Cape, Layer.Robe],
        [ArmorHitRegion.Hands] = [Layer.Gloves],
        [ArmorHitRegion.Legs] = [Layer.Pants, Layer.Skirt, Layer.Waist, Layer.Robe, Layer.Legs],
        [ArmorHitRegion.Feet] = [Layer.Shoes, Layer.Legs],
    };

    public static bool DurabilityEnabled { get; set; }
    public static int DurabilityLossChance { get; set; } = 25;
    public static int DurabilityLossMin { get; set; } = 1;
    public static int DurabilityLossMax { get; set; } = 1;
    public static bool BreakOnZeroHits { get; set; } = true;
    public static int DefaultHits { get; set; } = 50;

    public static Action<Item>? OnItemBroken;
    public static Func<Item, int, bool>? OnItemDamaged;
    /// <summary>
    /// Fired on a successful parry. Returns the damage that still leaks THROUGH
    /// the parry — 0 (the default, when unwired or when no @HitParry script
    /// overrides ARGN1) is a full block; a positive value is a partial block.
    /// Args: defender, attacker, blockedDamage → damageThrough.
    /// </summary>
    public static Func<Character, Character, int, int>? OnHitParry;

    /// <summary>
    /// On-hit damage pipeline. Fires the @Hit / @GetHit char triggers and the
    /// weapon/armor item triggers on a connecting hit — after armor/parry have
    /// resolved a number, but BEFORE it is applied to HP — and returns the final
    /// damage. A script may raise, lower or fully cancel it (return &lt;= 0 or
    /// set <see cref="HitDamageContext.Cancelled"/>). Wired in the engine so
    /// the player and NPC swing paths share one trigger pipeline.
    /// </summary>
    public static Func<HitDamageContext, int>? OnHitDamage;

    /// <summary>Target-side @GetHit bridge for the DAMAGE verb. Returns the
    /// script-adjusted raw damage; setting Cancelled suppresses application.</summary>
    public static Func<DirectDamageContext, int>? OnDirectDamage;

    /// <summary>Host feedback after direct character damage is applied:
    /// interrupt casting, broadcast damage/health and run the death engine.</summary>
    public static Action<Character, Character?, int>? OnDirectCharacterDamageApplied;

    /// <summary>Leech feedback on an AOS on-hit drain (Source-X sound 0x44D
    /// at the attacker). Wired to a nearby-sound broadcast.</summary>
    public static Action<Character>? OnLeechEffect;

    /// <summary>HITAREA* splash (Source-X OnTakeDamageInflictArea): damage
    /// the chars around the struck target. Args: attacker, epicenter target,
    /// damage, damage type.</summary>
    public static Action<Character, Character, int, DamageType>? OnHitAreaDamage;

    /// <summary>HITFIREBALL/HARM/LIGHTNING/MAGICARROW/DISPEL proc (Source-X
    /// OnSpellEffect from Fight_Hit): cast the spell's effect directly on the
    /// victim — no cast time, mana or reagents. Args: attacker, target,
    /// SpellType id.</summary>
    public static Action<Character, Character, int>? OnHitSpell;

    /// <summary>Armor layers a hit may pick for the item @GetHit trigger and
    /// the durability wear (Source-X sm_ArmorDamageLayers, CCharFight.cpp:388).
    /// Hand layers (weapons/shields) are excluded.</summary>
    public static readonly Layer[] ArmorDamageLayers =
    [
        Layer.Shoes, Layer.Pants, Layer.Shirt, Layer.Helm, Layer.Gloves, Layer.Neck,
        Layer.Waist, Layer.Chest, Layer.Tunic, Layer.Arms, Layer.Cape, Layer.Robe,
        Layer.Skirt, Layer.Legs,
    ];

    /// <summary>
    /// Calculate hit chance. Maps to CServerConfig::Calc_CombatChanceToHit.
    /// Era 0 = Sphere custom, 1 = pre-AOS, 2 = AOS.
    /// </summary>
    public static int CalcHitChance(Character attacker, Character target, int era = 0)
        => CalcHitChanceCore(attacker, target, era, GetWeaponSkill(attacker));

    private static int CalcHitChanceCore(Character attacker, Character target, int era,
        SkillType attackerWeaponSkill)
    {
        int attackSkill = GetHitChanceSkill(attacker, attackerWeaponSkill);
        int targetSkill = GetHitChanceSkill(target, GetWeaponSkill(target));
        int tacticsAtk = GetHitChanceTactics(attacker, attackSkill);
        int tacticsDef = GetHitChanceTactics(target, targetSkill);

        switch (era)
        {
            case 1: // pre-AOS
            {
                int chance = (attackSkill + 500) * 100 / Math.Max(1, (targetSkill + 500) * 2);
                return Math.Clamp(chance, 5, 95);
            }
            case 2: // AOS
            {
                // Source-X Calc_CombatChanceToHit era-2 (CResourceCalc.cpp:208):
                // each side scales by its Hit/Defense Chance Increase, capped at
                // +45%. HCI/DCI are AOS suit-wide properties (Source-X accumulates
                // every equipped item's value into PROPCH_INCREASEHITCHANCE/
                // INCREASEDEFCHANCE at equip time), so read the full suit via
                // GetEquipmentPropertyValue (char tag + all layers) — the previous
                // GetOnHitPropertyValue(...,weapon:null,...) missed armor/jewelry HCI.
                int hci = Math.Clamp(GetEquipmentPropertyValue(attacker, "INCREASEHITCHANCE"), 0, 45);
                int dci = Math.Clamp(GetEquipmentPropertyValue(target, "INCREASEDEFCHANCE"), 0, 45);
                int atkCalc = (attackSkill / 10 + 20) * (100 + hci);
                int defCalc = (targetSkill / 10 + 20) * (100 + dci);
                int chance = atkCalc * 100 / Math.Max(1, defCalc * 2);
                return Math.Clamp(chance, 5, 95);
            }
            default: // Sphere custom (era 0) — Source-X Calc_CombatChanceToHit
            {
                // Sleeping/frozen target: Source-X returns rand(10) as the skill-
                // check DIFFICULTY (trivially easy → a near-certain hit). In this
                // percent model that means a fixed high chance, not a random one —
                // the old `rand(10)*10` averaged 45% and made paralyzed targets
                // dodge more than half the swings.
                if (target.IsStatFlag(StatFlag.Freeze))
                    return 95;

                int iSkillVal = attackSkill;
                // Offence: weapon skill + tactics, averaged.
                int iSkillAttack = (iSkillVal + tacticsAtk) / 2;
                // Defence: target's tactics blended with their DEX (the key
                // factor the old formula dropped entirely).
                int iSkillDefend = tacticsDef;
                int iStam = target.Dex;
                bool targetRanged = IsRangedSkill(GetWeaponSkill(target));
                bool attackerRanged = IsRangedSkill(attackerWeaponSkill);
                if (targetRanged && !attackerRanged)
                    iSkillDefend = (iSkillDefend + iStam * 9) / 2;  // bows are easier to hit
                else
                    iSkillDefend = (iSkillDefend + iStam * 10) / 2;

                int iDiff = (iSkillAttack - iSkillDefend) / 5;
                iDiff = (iSkillVal - iDiff) / 10;
                return Math.Clamp(iDiff, 0, 100);
            }
        }
    }

    private static int GetHitChanceSkill(Character ch, SkillType skill)
    {
        // Source-X uses the raw chardef/@Create skill — an NPC whose def grants
        // no weapon skill fights at 0 and almost never lands a hit. The old
        // stat*10 inference (floor 25.0) had no reference basis and silently
        // buffed every under-scripted NPC.
        return ch.GetSkill(skill);
    }

    private static int GetHitChanceTactics(Character ch, int weaponSkill)
    {
        return ch.GetSkill(SkillType.Tactics);
    }

    /// <summary>
    /// Calculate weapon damage range. Maps to Fight_CalcDamage in Source-X.
    /// </summary>
    /// <summary>Optional lookup for weapon definitions (BaseId → (damMin, damMax)).</summary>
    public static Func<ushort, (int Min, int Max)?>? WeaponDefLookup { get; set; }

    /// <summary>Optional lookup for NPC natural damage from CHARDEF (CharDefIndex → (damMin, damMax)).</summary>
    public static Func<int, (int Min, int Max)?>? NpcDamageDefLookup { get; set; }

    public static (int Min, int Max) CalcWeaponDamage(Character attacker, Item? weapon, int era = 0)
    {
        int dmgMin, dmgMax;

        if (weapon == null)
        {
            // Source-X Fight_CalcDamage unarmed: iDmgMin = m_attackBase, i.e.
            // the CHARDEF's DAM line — for players too (c_man DAM=1,4 is the
            // classic fists damage). The old path invented Str/4 for anyone
            // whose chardef didn't resolve.
            var defDam = NpcDamageDefLookup?.Invoke(attacker.CharDefIndex);
            if (defDam.HasValue && defDam.Value.Max > 0)
            {
                dmgMin = defDam.Value.Min;
                dmgMax = defDam.Value.Max;
            }
            else if (attacker.IsPlayer)
            {
                dmgMin = 1; // c_man DAM=1,4 — classic bare-fist range
                dmgMax = 4;
            }
            else
            {
                dmgMin = 0; // chardef declares no DAM → attackBase 0
                dmgMax = 1;
            }

            // Source-X FEATURE_AOS_UPDATE_B Horrific Beast form replaces the
            // character's natural (unarmed) base damage with 5-15.
            if (HasHorrificBeastCombatForm(attacker))
            {
                dmgMin = 5;
                dmgMax = 15;
            }
        }
        else
        {
            var defDamage = WeaponDefLookup?.Invoke(weapon.BaseId);
            if (defDamage.HasValue && defDamage.Value.Max > 0)
            {
                dmgMin = defDamage.Value.Min;
                dmgMax = defDamage.Value.Max;
            }
            else
            {
                // Source-X Weapon_GetAttack: a weapon whose def declares no DAM
                // has attackBase 0 (anything non-weapon acting as one hits 1).
                // The old fallback derived damage from the ART TILE ID (/10),
                // so high-graphic items hit absurdly hard.
                dmgMin = 0;
                dmgMax = 1;
            }
        }

        // Source-X Fight_CalcDamage: the bonus is a PERCENTAGE applied to the
        // base damage and is era-specific (tactics/anatomy only count in era 1/2).
        int tactics = attacker.GetSkill(SkillType.Tactics);
        int anatomy = attacker.GetSkill(SkillType.Anatomy);
        int dmgBonus; // percent
        switch (era)
        {
            case 1: // pre-AOS
                dmgBonus = (tactics - 500) / 10;
                dmgBonus += anatomy / 50;
                if (anatomy >= 1000) dmgBonus += 10;
                if (weapon != null && weapon.ItemType == ItemType.WeaponAxe)
                {
                    int lj = attacker.GetSkill(SkillType.Lumberjacking);
                    dmgBonus += lj / 50;
                    if (lj >= 1000) dmgBonus += 10;
                }
                dmgBonus += EffectiveStr(attacker) * 20 / 100;
                break;
            case 2: // AOS
                dmgBonus = tactics / 16;
                if (tactics >= 1000) dmgBonus += 6;
                dmgBonus += anatomy / 20;
                if (anatomy >= 1000) dmgBonus += 5;
                if (weapon != null && weapon.ItemType == ItemType.WeaponAxe)
                {
                    int lj = attacker.GetSkill(SkillType.Lumberjacking);
                    dmgBonus += lj / 50;
                    if (lj >= 1000) dmgBonus += 10;
                }
                if (EffectiveStr(attacker) >= 100) dmgBonus += 5;
                dmgBonus += EffectiveStr(attacker) * 30 / 100;
                break;
            default: // era 0 — Sphere custom: STR% only, no tactics/anatomy
                dmgBonus = EffectiveStr(attacker) * 10 / 100;
                break;
        }

        // Definitions and callback-provided ranges are external input. Keep
        // arithmetic in 64-bit space and normalise reversed/negative ranges
        // before Random receives them.
        int baseLow = Math.Min(dmgMin, dmgMax);
        int baseHigh = Math.Max(dmgMin, dmgMax);
        long adjustedLow = baseLow + (long)baseLow * dmgBonus / 100;
        long adjustedHigh = baseHigh + (long)baseHigh * dmgBonus / 100;
        int low = (int)Math.Clamp(Math.Min(adjustedLow, adjustedHigh), 1L, short.MaxValue);
        int high = (int)Math.Clamp(Math.Max(adjustedLow, adjustedHigh), 1L, short.MaxValue);
        return (low, high);
    }

    /// <summary>
    /// Calculate armor defense rating. Maps to CalcArmorDefense in Source-X.
    /// Non-elemental: sums equipped armor AR by body region coverage.
    /// </summary>
    public static int CalcArmorDefense(Character defender, bool elementalEngine = false)
    {
        // Keep the compatibility overload on the same implementation as the
        // exact overload. The former copy ignored STACKARMOR, MODAR and
        // Discordance, so CalcArmorDefense(ch, false) disagreed with
        // CalcArmorDefense(ch).
        return elementalEngine ? 0 : CalcArmorDefense(defender);
    }

    public static ArmorHitRegion RollArmorHitRegion()
    {
        int total = 0;
        foreach (var region in _armorRegions)
            total += region.Weight;

        int roll = _rand.Next(total);
        int cumulative = 0;
        foreach (var region in _armorRegions)
        {
            cumulative += region.Weight;
            if (roll < cumulative)
                return region.Region;
        }

        return ArmorHitRegion.Chest;
    }

    public static Layer GetArmorLayerForRegion(ArmorHitRegion hitRegion)
    {
        foreach (var region in _armorRegions)
        {
            if (region.Region == hitRegion)
                return region.Layer;
        }

        return Layer.Chest;
    }

    // Source-X sm_ArmorLayers coverage percentages (CCharFight.cpp:409): the
    // share of the humanoid body each region contributes to the whole-body AR.
    private static readonly (ArmorHitRegion Region, int Coverage)[] _armorCoverage =
    [
        (ArmorHitRegion.Head, 15),
        (ArmorHitRegion.Neck, 7),
        (ArmorHitRegion.Chest, 35),
        (ArmorHitRegion.Arms, 14),
        (ArmorHitRegion.Hands, 7),
        (ArmorHitRegion.Legs, 22),
        (ArmorHitRegion.Feet, 0),
    ];

    /// <summary>
    /// Whole-body armor rating (Source-X CChar::CalcArmorDefense): each body
    /// region takes the best (or, with COMBAT_STACKARMOR, the summed) AR of
    /// the pieces covering it, weighted by the region's body-coverage percent.
    /// Every worn piece thus softens EVERY blow proportionally — the old
    /// single-region model made a helm matter only on the ~14% of hits that
    /// landed on the head.
    /// </summary>
    public static int CalcArmorDefense(Character defender)
    {
        bool stack = (Character.CombatFlags & (int)CombatFlags.StackArmor) != 0;
        bool scaleShield = (Character.CombatParryingEra & (int)ParryEraFlags.ArmorScaling) != 0;
        Item? shield = defender.GetEquippedItem(Layer.TwoHanded);
        int shieldDefense = shield?.ItemType == ItemType.Shield
            ? Math.Max(0, shield.GetArmorDefense())
            : 0;
        long total = 0;
        foreach (var (region, coverage) in _armorCoverage)
        {
            if (coverage == 0) continue;
            long regionAr = 0;
            if (_stackArmorLayers.TryGetValue(region, out var layers))
            {
                foreach (var layer in layers)
                {
                    int def = Math.Max(0, defender.GetEquippedItem(layer)?.GetArmorDefense() ?? 0);
                    regionAr = stack ? regionAr + def : Math.Max(regionAr, def);
                }
            }
            // Without PARRYERA_ARSCALING Source-X places shield AR in the
            // hands zone, so it contributes the legacy flat 7% coverage and
            // competes/stacks with gloves exactly like another hands piece.
            if (region == ArmorHitRegion.Hands && shieldDefense > 0 && !scaleShield)
                regionAr = stack ? regionAr + shieldDefense : Math.Max(regionAr, shieldDefense);
            total += coverage * regionAr;
        }

        if (shieldDefense > 0 && scaleShield)
        {
            // Source-X PARRYERA_ARSCALING: ((base Parry * shield AR) / 2000) + 1,
            // capped at half of the shield's base AR, with 100% body coverage.
            int parrying = defender.GetSkill(SkillType.Parrying);
            long skillScaled = ((long)parrying * shieldDefense / 2000) + 1;
            total += 100L * Math.Min(shieldDefense / 2L, skillScaled);
        }

        long rawAr = total / 100 + defender.ModAr + defender.ProtectionArmor;
        int ar = (int)Math.Clamp(rawAr, 0L, int.MaxValue);

        // Discordance temporarily lowers the target's defenses.
        int discord = GetActiveDiscordPct(defender);
        if (discord > 0)
            ar = (int)Math.Max(0L, ar - (long)ar * discord / 100);
        return Math.Max(0, ar);
    }

    public static int CalcArmorDefenseForRegion(Character defender, ArmorHitRegion hitRegion)
    {
        int ar;
        // COMBAT_STACKARMOR: sum the AR of every piece covering this region.
        if ((Character.CombatFlags & (int)CombatFlags.StackArmor) != 0 &&
            _stackArmorLayers.TryGetValue(hitRegion, out var layers))
        {
            long total = 0;
            foreach (var layer in layers)
            {
                var piece = defender.GetEquippedItem(layer);
                if (piece != null)
                    total += Math.Max(0, piece.GetArmorDefense());
            }
            ar = (int)Math.Min(total, int.MaxValue);
        }
        else
        {
            var armor = defender.GetEquippedItem(GetArmorLayerForRegion(hitRegion));
            ar = Math.Max(0, armor?.GetArmorDefense() ?? 0);
        }

        // Discordance temporarily lowers the target's defenses.
        int discord = GetActiveDiscordPct(defender);
        if (discord > 0)
            ar = (int)Math.Max(0L, ar - (long)ar * discord / 100);
        return Math.Max(0, ar);
    }

    /// <summary>Active Discordance defense penalty % on a character (0 when none
    /// or expired). Read from the DISCORD_PCT / DISCORD_UNTIL tags set by the
    /// Discordance skill — lazy expiry, no separate timer.</summary>
    private static int GetActiveDiscordPct(Character ch)
    {
        if (!ch.TryGetTag("DISCORD_PCT", out string? p) || !int.TryParse(p, out int pct) || pct <= 0)
            return 0;
        if (ch.TryGetTag("DISCORD_UNTIL", out string? u) && long.TryParse(u, out long until) &&
            Environment.TickCount64 > until)
            return 0;
        return Math.Clamp(pct, 0, 100);
    }

    /// <summary>Apply one durability-loss roll to an arbitrary item (e.g. a
    /// crafting tool). Honors the DurabilityEnabled gate and break handling.</summary>
    public static void DamageItem(Item item)
    {
        if (DurabilityEnabled)
            ApplyDurabilityLoss(item);
    }

    /// <summary>Source-X CChar::OnTakeDamage (CCharFight.cpp) bounces every blow —
    /// returning 0 damage — while the target carries STATF_INVUL, unless the strike
    /// is flagged DAMAGE_GOD. SphereNet has no single damage choke-point, so each
    /// damage site (melee, spells, script DAMAGE, traps, fields) consults this guard.</summary>
    public static bool IsDamageImmune(Character target, DamageType type = DamageType.Physical)
        => target.IsStatFlag(StatFlag.Invul) && !type.HasFlag(DamageType.God);

    /// <summary>
    /// Apply damage that a defender bounces back at its attacker — Blood Oath,
    /// Reactive Armor, REFLECTPHYSICALDAM.
    ///
    /// Source-X sends reflected damage back through the SAME entry as any other
    /// blow: OnTakeDamage(dam, src, DAMAGE_FIXED | DAMAGE_REACTIVE)
    /// (CCharFight.cpp:1021), whose first act is to bounce everything without
    /// DAMAGE_GOD off an invulnerable target (:642). Recursion is prevented by the
    /// DAMAGE_REACTIVE flag (:1015), NOT by the damage being "fixed" — SphereNet's
    /// three reflect branches wrote attacker.Hits directly on the belief that fixed
    /// damage cannot recurse, and so skipped the immunity gate entirely: a character
    /// that cannot be harmed still lost hit points to its own victim.
    ///
    /// Reflected damage is never itself reflected, which is what the direct write
    /// bought and what this reproduces without giving up the guard.
    /// </summary>
    /// <returns>The damage actually dealt; 0 when the recipient is immune.</returns>
    public static int ApplyReflectedDamage(Character recipient, Character from, int damage)
    {
        if (damage <= 0) return 0;
        if (recipient.IsDeleted || recipient.IsDead) return 0;
        if (IsDamageImmune(recipient)) return 0;

        recipient.Hits -= (short)Math.Min(damage, short.MaxValue);
        // Credit it so a reflect kill attributes to the defender (murder count,
        // karma/fame, loot rights).
        recipient.RecordAttack(from.Uid, damage);
        return damage;
    }

    /// <summary>Apply the Source-X CObjBase DAMAGE verb to a character or item.
    /// Character damage honors @GetHit and elemental resists; item damage uses
    /// the existing @Damage cancellation and durability-break callbacks.</summary>
    public static int ApplyScriptDamage(
        ObjBase target,
        int rawDamage,
        DamageType damageType,
        Character? source = null,
        int physicalPercent = 0,
        int firePercent = 0,
        int coldPercent = 0,
        int poisonPercent = 0,
        int energyPercent = 0)
    {
        int damage = Math.Clamp(rawDamage, 0, short.MaxValue);
        if (damage <= 0) return 0;

        if (target is Item item)
            return ApplyDirectItemDamage(item, damage);
        if (target is not Character character || character.IsDeleted || character.IsDead)
            return 0;
        if (IsDamageImmune(character, damageType))
            return 0;

        var context = new DirectDamageContext
        {
            Target = character,
            Source = source,
            Damage = damage,
            DamageType = damageType,
            PhysicalPercent = physicalPercent,
            FirePercent = firePercent,
            ColdPercent = coldPercent,
            PoisonPercent = poisonPercent,
            EnergyPercent = energyPercent
        };
        if (OnDirectDamage != null)
            damage = Math.Clamp(OnDirectDamage(context), 0, short.MaxValue);
        if (context.Cancelled || damage <= 0)
            return 0;

        if (!damageType.HasFlag(DamageType.Fixed) && !damageType.HasFlag(DamageType.God))
        {
            int splitTotal = context.PhysicalPercent + context.FirePercent + context.ColdPercent +
                context.PoisonPercent + context.EnergyPercent;
            damage = splitTotal > 0
                ? ApplyDamageSplitResist(character, damage,
                    context.PhysicalPercent, context.FirePercent, context.ColdPercent,
                    context.PoisonPercent, context.EnergyPercent)
                : ApplyElementalResist(character, damage, damageType);
        }
        damage = Math.Clamp(damage, 0, short.MaxValue);
        if (damage <= 0) return 0;

        character.Hits -= (short)damage;
        if (source != null && source != character)
            character.RecordAttack(source.Uid, damage);
        OnDirectCharacterDamageApplied?.Invoke(character, source, damage);
        return damage;
    }

    internal static int ApplyDirectItemDamage(Item item, int damage)
    {
        if (item.IsDeleted || OnItemDamaged?.Invoke(item, damage) == true)
            return 0;

        // Source-X: an item whose def gives it no HITPOINTS has no durability
        // to damage — inventing (and persisting!) a synthetic pool here turned
        // an unbreakable item into a breakable one on the first scripted hit.
        int maxHits = item.GetHitsMax();
        if (maxHits <= 0)
            return 0;
        int before = item.GetHitsCur();
        if (before <= 0)
            return 0;
        int dealt = Math.Min(before, damage);
        item.HitsCur = Math.Max(0, before - damage);
        if (item.HitsCur <= 0 && BreakOnZeroHits)
            OnItemBroken?.Invoke(item);
        return dealt;
    }

    /// <summary>Apply an explicit physical/fire/cold/poison/energy percentage
    /// split. Missing percentage is physical; totals above 100 are normalized
    /// so malformed scripts cannot amplify the raw damage.</summary>
    public static int ApplyDamageSplitResist(Character target, int damage,
        int physicalPercent, int firePercent, int coldPercent,
        int poisonPercent, int energyPercent)
    {
        if (damage <= 0) return 0;
        int phys = Math.Clamp(physicalPercent, 0, 100);
        int fire = Math.Clamp(firePercent, 0, 100);
        int cold = Math.Clamp(coldPercent, 0, 100);
        int poison = Math.Clamp(poisonPercent, 0, 100);
        int energy = Math.Clamp(energyPercent, 0, 100);
        int total = phys + fire + cold + poison + energy;
        if (total <= 0) return damage;
        if (total < 100)
        {
            phys += 100 - total;
            total = 100;
        }

        long resisted = 0;
        resisted += (long)phys * (100 - EffResPhysical(target));
        resisted += (long)fire * (100 - EffResFire(target));
        resisted += (long)cold * (100 - EffResCold(target));
        resisted += (long)poison * (100 - EffResPoison(target));
        resisted += (long)energy * (100 - EffResEnergy(target));
        return (int)Math.Clamp((long)damage * resisted / (total * 100L), 0, damage);
    }

    /// <summary>
    /// Perform a full attack resolution. Returns damage dealt, 0 for a
    /// connecting but fully absorbed/cancelled hit, or an Attack* sentinel.
    /// Maps to CChar::Fight_Hit flow in Source-X.
    /// </summary>
    /// <summary>Attacker's Damage Increase % (Source-X INCREASEDAM), from the
    /// INCREASEDAM tag. 0 when absent or unparseable.</summary>
    private static int GetDamageIncrease(Character ch) =>
        ch.TryGetTag("INCREASEDAM", out string? s) && int.TryParse(s, out int v) ? v : 0;

    /// <summary>Source-X Fight_CalcDamage additive Damage Increase modifiers.
    /// The configured INCREASEDAM value is capped first; racial/form bonuses
    /// are then added and may take the effective total above 100%.</summary>
    public static int CalculateDamageIncrease(Character attacker)
    {
        int increase = Math.Clamp(GetDamageIncrease(attacker), -100, 100);
        if ((((RacialFlags)Character.RacialFlags) & RacialFlags.GargoyleBerserk) != 0 &&
            attacker.IsGargoyle)
        {
            int lostHits = Math.Max(0, attacker.MaxHits - attacker.Hits);
            increase += Math.Min(15 * (lostHits / 20), 60);
        }

        if (HasHorrificBeastCombatForm(attacker))
            increase += 25;
        return increase;
    }

    private static bool HasHorrificBeastCombatForm(Character attacker) =>
        (Character.FeatureAOS & 0x02) != 0 && attacker.HorrificBeastActive;

    /// <summary>Source-X Calc_CombatChanceToParry, including the Samurai
    /// Empire Bushido formulas and COMBATPARRYINGERA equipment gates.</summary>
    public static int CalculateParryChance(Character defender, out Item? parryItem)
    {
        parryItem = null;
        var era = (ParryEraFlags)Character.CombatParryingEra;
        bool canShield = era.HasFlag(ParryEraFlags.ShieldBlock);
        bool canOneHand = era.HasFlag(ParryEraFlags.OneHandBlock);
        bool canTwoHand = era.HasFlag(ParryEraFlags.TwoHandBlock);
        bool seFormula = (Character.FeatureSE & 0x02) != 0 &&
            era.HasFlag(ParryEraFlags.SeFormula);

        int parrying = defender.GetSkill(SkillType.Parrying);
        int chance = -1;
        var twoHand = defender.GetEquippedItem(Layer.TwoHanded);
        bool hasShield = CombatHelper.HasShieldEquipped(defender);
        var oneHand = defender.GetEquippedItem(Layer.OneHanded);
        Item? weapon = oneHand ?? (hasShield ? null : twoHand);

        if (seFormula)
        {
            int bushido = defender.GetSkill(SkillType.Bushido);
            if (canShield && hasShield)
            {
                parryItem = twoHand;
                chance = (parrying - bushido) / 40;
                if (parrying >= 1000 || bushido >= 1000)
                    chance += 5;
                chance = Math.Max(0, chance);
            }
            else if (weapon != null)
            {
                int seChance;
                int legacyChance = parrying / 80;
                if (parrying >= 1000)
                    legacyChance += 5;

                if (weapon == oneHand && canOneHand)
                {
                    parryItem = weapon;
                    seChance = parrying * bushido / 48_000;
                }
                else if (weapon == twoHand && canTwoHand)
                {
                    parryItem = weapon;
                    seChance = parrying * bushido / 41_140;
                }
                else
                {
                    seChance = -1;
                }

                if (seChance >= 0)
                {
                    if (parrying >= 1000 || bushido >= 1000)
                        seChance += 5;
                    chance = Math.Max(seChance, legacyChance);
                }
            }
        }
        else
        {
            if (canShield && hasShield)
            {
                parryItem = twoHand;
                chance = parrying / 40;
            }
            else if (weapon != null &&
                     ((weapon == oneHand && canOneHand) || (weapon == twoHand && canTwoHand)))
            {
                parryItem = weapon;
                chance = parrying / 80;
            }

            if (chance > 0 && parrying >= 1000)
                chance += 5;
        }

        if (chance < 0)
            return 0;
        int dex = defender.Dex;
        if (dex < 80)
            chance = (int)(chance * (1.0f - ((80 - dex) / 100.0f)));
        return Math.Max(0, chance);
    }

    public static int ResolveAttack(
        Character attacker,
        Character target,
        Item? weapon,
        CombatFlags flags = CombatFlags.None,
        int hitEra = -1,
        int damageEra = -1) =>
        ResolveAttack(attacker, target, weapon, flags, hitEra, damageEra, 0, out _);

    /// <summary>Full overload for the ranged path: <paramref name="ammoUid"/>
    /// is the pack ammo stack the shot draws from (exposed to the @Hit chain
    /// as LOCAL.Arrow); <paramref name="ammoHandled"/> reports a script's
    /// LOCAL.ArrowHandled=1 takeover so the caller skips the ammo economy.</summary>
    public static int ResolveAttack(
        Character attacker,
        Character target,
        Item? weapon,
        CombatFlags flags,
        int hitEra,
        int damageEra,
        uint ammoUid,
        out bool ammoHandled)
    {
        ammoHandled = false;
        if (hitEra < 0) hitEra = Character.CombatHitChanceEra;
        if (damageEra < 0) damageEra = Character.CombatDamageEra;

        if (attacker == target || attacker.MapIndex != target.MapIndex ||
            CombatHelper.IsInvalidSwingParticipant(attacker, asTarget: false) ||
            CombatHelper.IsInvalidSwingParticipant(target, asTarget: true))
            return 0;

        // Hit check. A failed roll is a true miss: return -1 so the caller can
        // distinguish it from a connecting hit that armor fully absorbs (which
        // returns 0 — Source-X still plays the hit sound/animation for that).
        //
        // Era 0 is the Source-X two-stage roll: Calc_CombatChanceToHit returns
        // a RANDOM difficulty 0..iDiff which then runs Skill_CheckSuccess on
        // the attacker's weapon skill (bell curve). The drawn difficulty is
        // m_Act_Difficulty — it also feeds the passive skill gain below. The
        // old single percent-roll flattened the compounded variance.
        int hitChance;
        if (hitEra == 0)
        {
            // Frozen/sleeping target: the reference returns rand(10) — a
            // trivially easy difficulty — instead of the computed iDiff.
            int diffCap = target.IsStatFlag(StatFlag.Freeze)
                ? 9
                : CalcHitChanceCore(attacker, target, 0, GetWeaponSkill(attacker, weapon));
            int actDifficulty = _rand.Next(diffCap + 1);
            int effSkill = GetHitChanceSkill(attacker, GetWeaponSkill(attacker, weapon));
            bool hitLanded = attacker.PrivLevel >= PrivLevel.GM ||
                Skills.SkillEngine.CheckSuccessValue(effSkill, actDifficulty);
            hitChance = actDifficulty; // m_Act_Difficulty for the gain rolls
            if (!hitLanded)
                return AttackMiss;
        }
        else
        {
            hitChance = CalcHitChanceCore(attacker, target, hitEra, GetWeaponSkill(attacker, weapon));
            if (_rand.Next(100) >= hitChance)
                return AttackMiss;
        }

        // Calculate raw damage
        var (dmgMin, dmgMax) = CalcWeaponDamage(attacker, weapon, damageEra);
        // Guard against malformed weapon/NPC damage defs where Min > Max, which
        // would make Random.Next throw and crash the combat tick.
        if (dmgMax < dmgMin) (dmgMin, dmgMax) = (dmgMax, dmgMin);
        int damage = (int)_rand.NextInt64(dmgMin, (long)dmgMax + 1);

        // Damage Increase (Source-X PROPCH_INCREASEDAM): applies to players
        // always, and to NPCs only when COMBAT_NPC_BONUSDAMAGE is set, capped at
        // ±100%. Read from the attacker's INCREASEDAM tag (absent/0 = none).
        if (attacker.IsPlayer || flags.HasFlag(CombatFlags.NpcBonusDamage))
        {
            int di = CalculateDamageIncrease(attacker);
            if (di != 0)
                damage += damage * di / 100;
        }

        // Parry check — Source-X Calc_CombatChanceToParry, selected by the
        // COMBATPARRYINGERA mask (legacy or Samurai Empire/Bushido formula).
        int parryChance = CalculateParryChance(target, out Item? parryItem);
        if (parryChance > 0)
        {
            // Source-X rolls through Skill_UseQuick, which trains Parrying on
            // the attempt using the computed parry chance as difficulty.
            if (target.IsPlayer)
                Skills.SkillEngine.GainExperience(target, SkillType.Parrying, parryChance);

            if (_rand.Next(100) < parryChance)
            {
                bool seWeaponParry = (Character.FeatureSE & 0x02) != 0 &&
                    ((ParryEraFlags)Character.CombatParryingEra).HasFlag(ParryEraFlags.SeFormula) &&
                    parryItem?.ItemType != ItemType.Shield;
                if (target.IsPlayer && seWeaponParry)
                    Skills.SkillEngine.GainExperience(target, SkillType.Bushido, parryChance);

                // A parry fully blocks by default. A wired @HitParry can let
                // some damage leak through (partial block) by returning a
                // positive value; that damage then still runs through armor.
                int through = OnHitParry?.Invoke(target, attacker, damage) ?? 0;
                if (through <= 0)
                    return AttackParried;
                damage = Math.Min(damage, through);
            }
        }

        // Armor reduction
        if (flags.HasFlag(CombatFlags.ElementalEngine))
        {
            // Split damage by attacker's elemental percentages, apply per-element resist
            damage = ApplyElementalDamageSplit(attacker, target, damage, weapon);
        }
        else
        {
            // Source-X OnTakeDamage pre-AOS path: the WHOLE-BODY coverage-
            // weighted AR mitigates every hit; which worn piece takes the
            // durability wear is the @GetHit ItemDamageLayer roll below.
            int armorRating = CalcArmorDefense(target);
            int arMax = (int)Math.Min((long)armorRating * _rand.Next(7, 36) / 100, int.MaxValue);
            int arMin = arMax / 2;
            int defense = (int)_rand.NextInt64(arMin, (long)arMax + 1);
            damage -= defense;
        }

        damage = Math.Max(0, damage);

        // On-hit damage triggers (@Hit / @GetHit and the weapon/armor item
        // hooks) run here — after armor/parry resolved a number, but BEFORE it
        // is applied to HP — so a script may raise, lower or fully cancel the
        // damage. Source-X fires these around damage application; centralizing
        // them in one hook means the player and NPC swing paths share a single
        // pipeline. The context carries the Source-X @GetHit armor-damage roll
        // (LOCAL.ItemDamageLayer / ItemDamageChance) and the elemental split
        // (LOCAL.DamagePercent*) for the hook to expose to scripts.
        bool elemental = flags.HasFlag(CombatFlags.ElementalEngine);
        var split = elemental ? GetElementalSplit(attacker) : default;
        var hitCtx = new HitDamageContext
        {
            Attacker = attacker,
            Target = target,
            Weapon = weapon,
            Damage = damage,
            ItemDamageLayer = ArmorDamageLayers[_rand.Next(ArmorDamageLayers.Length)],
            ItemDamageChance = Math.Clamp(DurabilityLossChance, 0, 100),
            WeaponDamageChance = Math.Clamp(DurabilityLossChance, 0, 100),
            AmmoUid = ammoUid,
            Elemental = elemental,
            DamPercentPhysical = split.Phys,
            DamPercentFire = split.Fire,
            DamPercentCold = split.Cold,
            DamPercentPoison = split.Poison,
            DamPercentEnergy = split.Energy,
        };
        if (OnHitDamage != null)
            damage = Math.Clamp(OnHitDamage(hitCtx), 0, short.MaxValue);
        ammoHandled = hitCtx.ArrowHandled || hitCtx.Cancelled;

        // RETURN 1 is a full cancellation, irrespective of a buggy/custom
        // hook returning a positive number. Source-X exits before durability,
        // poison, procs, HP and skill gain; it also leaves ranged ammo alone.
        if (hitCtx.Cancelled)
            return 0;

        // Source-X OnTakeDamage tail of the @GetHit block: the (script-final)
        // ItemDamageLayer item wears ItemDamageChance% of the time — in BOTH
        // armor modes, so elemental combat wears armor too. A RETURN 1 anywhere
        // in the chain (Cancelled) returned before this roll in Source-X.
        if (!hitCtx.Cancelled && DurabilityEnabled &&
            _rand.Next(100) < Math.Clamp(hitCtx.ItemDamageChance, 0, 100))
        {
            var itemHit = target.GetEquippedItem(hitCtx.ItemDamageLayer);
            if (itemHit != null)
                ApplyDurabilityLoss(itemHit, rollConfiguredChance: false);
        }

        // COMBAT_SLAYER (Source-X OnTakeDamage, CCharFight.cpp:821): the
        // weapon's (or talisman's) SLAYER vs the victim's FACTION scales the
        // damage after the trigger chain has settled it.
        if (damage > 0 && flags.HasFlag(CombatFlags.Slayer))
            damage = ApplySlayerDamage(attacker, target, damage, weapon);
        damage = Math.Clamp(damage, 0, short.MaxValue);

        // Weapon poison on-hit: transfer poison from weapon to target.
        // Source-X: HIT_POISON attribute on weapon. SphereNet: POISON_SKILL tag
        // set by Poisoning skill. Uses 1 charge per hit; cleared at 0.
        bool poisonApplied = false;
        if (damage > 0 && weapon != null && !flags.HasFlag(CombatFlags.NoPoisonHit))
        {
            if (weapon.TryGetTag("POISON_SKILL", out string? poisonStr) &&
                int.TryParse(poisonStr, out int poisonLevel) && poisonLevel > 0)
            {
                // OSI SetPoison banding from the weapon's stored envenom skill
                // (melee range → no distance falloff).
                byte targetLevel = CalcOsiPoisonLevel(poisonLevel, 1, evilOmen: false);
                target.ApplyPoison(targetLevel, attacker.Uid);
                poisonApplied = true;

                int charges = 1;
                if (weapon.TryGetTag("POISON_CHARGES", out string? chargesStr))
                    int.TryParse(chargesStr, out charges);
                charges = Math.Max(0, charges);
                // Source-X @Hit LOCAL.ItemPoisonReductionChance/Amount: the
                // script controls whether (and by how much) delivering the
                // poison spends the weapon's charges. Defaults (100% / 1)
                // reproduce the fixed 1-per-hit spend.
                if (_rand.Next(100) < Math.Clamp(hitCtx.PoisonReductionChance, 0, 100))
                {
                    long remaining = (long)charges - Math.Max(0, hitCtx.PoisonReductionAmount);
                    charges = (int)Math.Max(remaining, int.MinValue);
                }
                if (charges <= 0)
                {
                    weapon.RemoveTag("POISON_SKILL");
                    weapon.RemoveTag("POISON_CHARGES");
                }
                else
                    weapon.SetTag("POISON_CHARGES", charges.ToString());
            }
        }

        // Creature innate poison-on-hit (Source-X CChar::Fight_Hit: m_pNPC && 50%
        // && SKILL_POISONING > 0). A venomous creature — spider, snake, scorpion —
        // envenoms on a bite from its Poisoning skill alone, with no weapon and no
        // POISON_SKILL tag. Skipped when the weapon already poisoned this hit or
        // when poison hits are globally disabled. Poison level uses the OSI
        // SetPoison banding of the creature's Poisoning skill (melee → no falloff).
        if (damage > 0 && !poisonApplied && !attacker.IsPlayer &&
            !flags.HasFlag(CombatFlags.NoPoisonHit))
        {
            int poisonSkill = attacker.GetSkill(SkillType.Poisoning);
            if (poisonSkill > 0 && _rand.Next(100) < 50)
            {
                byte level = CalcOsiPoisonLevel(poisonSkill, 1, evilOmen: false);
                target.ApplyPoison(level, attacker.Uid);
            }
        }

        // Apply damage — do NOT call Kill() here; the caller handles death
        // via DeathEngine.ProcessDeath which creates the corpse, drops loot,
        // and sends the delete packet. Calling Kill() early makes
        // ProcessDeath bail out ("already dead") leaving a ghost NPC.
        // STATF_INVUL bounces the blow (no Hits loss, no reflect).
        if (damage > 0 && !IsDamageImmune(target))
        {
            // Necromancy Evil Omen (reference OnTakeDamage): the victim's next
            // harmful hit lands 25% harder, then the omen is spent.
            if (target.ConsumeEvilOmen())
                damage = (int)Math.Min((long)damage + damage / 4, short.MaxValue);

            target.Hits -= (short)Math.Min(damage, short.MaxValue);
            target.RecordAttack(attacker.Uid, damage);

            // Necromancy Blood Oath (reference OnTakeDamage): a bonded victim
            // struck by its linked enemy suffers an extra 10% and reflects
            // (100 - level)% back as fixed damage — no recursion (the reflect is
            // applied directly, not routed back through this block).
            if (target.BloodOathEnemy == attacker.Uid && target.BloodOathLevel > 0 &&
                attacker != target && !attacker.IsDead)
            {
                int extra = damage / 10;
                if (extra > 0)
                    target.Hits -= (short)Math.Min(extra, short.MaxValue);
                int reflect = damage * (100 - target.BloodOathLevel) / 100;
                ApplyReflectedDamage(attacker, target, reflect);
            }

            if (target.IsStatFlag(StatFlag.Reactive) && attacker != target && !attacker.IsDead)
                ApplyReflectedDamage(attacker, target, Math.Max(1, damage / 4));

            // AOS REFLECTPHYSICALDAM (Source-X OnTakeDamage, CCharFight.cpp:1013):
            // the defender's suit bounces a percentage of the damage back at the
            // attacker, capped at 250%. Separate from the Reactive Armor spell above.
            if (attacker != target && !attacker.IsDead)
            {
                int reflectPct = Math.Min(GetOnHitPropertyValue(target, null, "REFLECTPHYSICALDAM"), 250);
                ApplyReflectedDamage(attacker, target, damage * reflectPct / 100);
            }

            // Source-X @Hit LOCAL.ItemDamageChance: the weapon wears only
            // WeaponDamageChance% of the time (script-writable, seeded 25).
            if (DurabilityEnabled && weapon != null &&
                _rand.Next(100) < Math.Clamp(hitCtx.WeaponDamageChance, 0, 100))
                ApplyDurabilityLoss(weapon, rollConfiguredChance: false);
        }

        // AOS on-hit properties: leeches, mana drain, hit-area splashes and on-hit
        // spell procs.
        //
        // Source-X Fight_Hit applies the weapon's own damage FIRST
        // (OnTakeDamage, CCharFight.cpp:2259) and only then runs the procs
        // (:2270-2361). Running them first meant a proc that killed the target
        // aborted the strike before its damage ever landed and before RecordAttack
        // ran for it - so the kill, the murder count and the loot rights went to the
        // proc rather than to the blow that was actually swung.
        //
        // Deliberately OUTSIDE the immunity block above: Source-X gates the procs on
        // `iDmg > 0` and discards OnTakeDamage's return, so they fire even when the
        // target bounced the blow. Moving this inside would be a new deviation.
        if (damage > 0)
            ApplyAosOnHitEffects(attacker, target, damage, weapon, flags);

        // A proc can synchronously kill either party through the normal spell/death
        // engine. The strike itself is already applied and credited by this point;
        // stop here so the tail below does not add poison, durability or death
        // feedback on top of a mobile that is already dead.
        if (attacker.IsDeleted || attacker.IsDead || target.IsDeleted || target.IsDead)
            return AttackResolvedByProc;

        // Passive combat skill gain — Source-X Fight_Hit tail: every landed
        // swing trains the active weapon skill AND Tactics for a player
        // attacker, unless the victim is a player standing in a NO_PVP region.
        // The gain difficulty mirrors m_Act_Difficulty (the 0-100 hit-chance
        // value), so easy prey stops training via the GAINRADIUS gate.
        if (attacker.IsPlayer)
        {
            bool noPvpBlock = false;
            if (target.IsPlayer)
            {
                var region = Objects.ObjBase.ResolveWorld?.Invoke()?.FindRegion(target.Position);
                noPvpBlock = region != null && region.IsFlag(RegionFlag.NoPvP);
            }
            if (!noPvpBlock)
            {
                Skills.SkillEngine.GainExperience(attacker, GetWeaponSkill(attacker, weapon), hitChance);
                Skills.SkillEngine.GainExperience(attacker, SkillType.Tactics, hitChance);
            }
        }

        return damage;
    }

    private static void ApplyDurabilityLoss(Item item, bool rollConfiguredChance = true)
    {
        int chance = Math.Clamp(DurabilityLossChance, 0, 100);
        if (rollConfiguredChance && _rand.Next(100) >= chance)
            return;

        // Source-X: only items whose def (or crafting) gave them HITPOINTS
        // wear out. Classic packs define none on most gear — inventing a
        // synthetic durability here made every weapon and armor piece break
        // and vanish after a few hundred swings.
        int maxHits = item.GetHitsMax();
        if (maxHits <= 0)
            return;

        int curHits = item.GetHitsCur();
        int minLoss = Math.Max(0, Math.Min(DurabilityLossMin, DurabilityLossMax));
        int maxLoss = Math.Max(minLoss, Math.Max(DurabilityLossMin, DurabilityLossMax));
        int loss = minLoss == maxLoss
            ? minLoss
            : (int)_rand.NextInt64(minLoss, (long)maxLoss + 1);

        if (loss <= 0)
            return;

        if (OnItemDamaged?.Invoke(item, loss) == true)
            return;

        curHits = Math.Max(0, curHits - loss);
        item.HitsCur = curHits;

        if (curHits <= 0 && BreakOnZeroHits)
            OnItemBroken?.Invoke(item);
    }

    /// <summary>
    /// Get the combat skill used by the attacker's weapon.
    /// Maps to CItem::Weapon_GetSkill in Source-X.
    /// </summary>
    /// <summary>True for ranged weapon skills (Source-X SKF_RANGED).</summary>
    private static bool IsRangedSkill(SkillType skill) =>
        skill is SkillType.Archery or SkillType.Throwing;

    private static bool IsWeaponSkill(SkillType skill) =>
        skill is SkillType.Wrestling or SkillType.Swordsmanship or SkillType.Fencing or
            SkillType.MaceFighting or SkillType.Archery or SkillType.Throwing;

    public static SkillType GetWeaponSkill(Character ch)
    {
        var weapon = ch.GetEquippedItem(Layer.OneHanded) ?? ch.GetEquippedItem(Layer.TwoHanded);
        return GetWeaponSkill(ch, weapon);
    }

    /// <summary>Weapon-snapshot overload used by delayed swings. The character
    /// parameter is retained for API symmetry and future char-level overrides.</summary>
    public static SkillType GetWeaponSkill(Character ch, Item? weapon)
    {
        if (weapon == null)
            return SkillType.Wrestling;

        // Item-level skill override: a weapon may declare a non-default combat
        // skill via TAG.OVERRIDE_SKILL (the SkillType number), so e.g. a blade
        // can be wielded with Fencing. Honored only when it names a real weapon
        // skill; otherwise the ItemType-inferred default below applies.
        string? ovr = null;
        if (!weapon.TryGetTag("OVERRIDE_SKILL", out ovr))
            weapon.TryGetTag("OVERRIDE.SKILL", out ovr);
        if (ovr != null &&
            int.TryParse(ovr, out int ovrId) &&
            IsWeaponSkill((SkillType)ovrId))
            return (SkillType)ovrId;

        var weaponDef = Definitions.DefinitionLoader.GetItemDef(weapon.BaseId);
        if (weaponDef is { HasSkill: true } && IsWeaponSkill(weaponDef.Skill))
            return weaponDef.Skill;

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

        string? overrideRaw = null;
        if (!weapon.TryGetTag("OVERRIDE.DAMAGETYPE", out overrideRaw))
            weapon.TryGetTag("OVERRIDE_DAMAGETYPE", out overrideRaw);
        if (overrideRaw == null)
        {
            var def = Definitions.DefinitionLoader.GetItemDef(weapon.BaseId);
            overrideRaw = def?.TagDefs.Get("OVERRIDE.DAMAGETYPE")
                ?? def?.TagDefs.Get("OVERRIDE_DAMAGETYPE");
        }
        if (!string.IsNullOrWhiteSpace(overrideRaw))
        {
            uint numeric = Objects.ObjBase.ParseHexOrDecUInt(overrideRaw);
            return (DamageType)(ushort)Math.Min(numeric, ushort.MaxValue);
        }

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
    /// OSI poison level from an envenoming skill — Source-X CChar::SetPoison
    /// (CCharAct.cpp:4204). Skill bands: &gt;=1000 Lethal (with a 1/10 bump to
    /// Deadly), &gt;850 Greater, &gt;650 Standard, else Lesser. Beyond 3 tiles a
    /// distance falloff of -dist/2 weakens it, and an Evil-Omen on the victim adds
    /// one level. Returned on SphereNet's 1-5 scale (OSI 0-4 + 1, clamped).
    /// </summary>
    public static byte CalcOsiPoisonLevel(int skill, int distance, bool evilOmen)
    {
        int level;
        if (skill >= 1000) level = 3 + (_rand.Next(10) == 0 ? 1 : 0);
        else if (skill > 850) level = 2;
        else if (skill > 650) level = 1;
        else level = 0;
        if (distance >= 4)
            level = Math.Max(0, level - distance / 2);
        if (evilOmen)
            level += 1;
        return (byte)Math.Clamp(level + 1, 1, 5);
    }

    /// <summary>
    /// Apply elemental resist to damage. Maps to COMBAT_ELEMENTAL_ENGINE in Source-X.
    /// Splits damage by attacker's elemental percentages then applies per-element resist.
    /// </summary>
    public static int ApplyElementalDamageSplit(Character attacker, Character target, int damage, Item? weapon)
    {
        var (physPct, firePct, coldPct, poisonPct, energyPct) = GetElementalSplit(attacker);
        long total = (long)damage * physPct * (100 - EffResPhysical(target));
        total += (long)damage * firePct * (100 - EffResFire(target));
        total += (long)damage * coldPct * (100 - EffResCold(target));
        total += (long)damage * poisonPct * (100 - EffResPoison(target));
        total += (long)damage * energyPct * (100 - EffResEnergy(target));
        // Source-X lets full resists zero the hit (CCharFight.cpp:730) — no
        // forced 1-damage floor.
        return (int)Math.Clamp(total / 10_000, 0L, int.MaxValue);
    }

    /// <summary>COMBAT_SLAYER damage scaling (Source-X OnTakeDamage,
    /// CCharFight.cpp:835-877). The attacker's weapon SLAYER is consulted
    /// first; when it yields nothing, the equipped talisman is the fallback.
    /// An NPC victim takes the slayer bonus (Lesser +200% / Super +100%); a
    /// player victim hit by an NPC wielding a Super Slayer of the player's
    /// opposing faction group takes the Source-X penalty arithmetic (-100%).
    /// (Source-X also consults the spellbook for magic damage — SphereNet's
    /// spell damage does not flow through ResolveAttack, so that path is
    /// deferred.)</summary>
    public static int ApplySlayerDamage(Character attacker, Character target, int damage, Item? weapon)
    {
        var victimFaction = SlayerFaction.FromChar(target);
        if (victimFaction.IsNone)
            return damage;

        int bonusPct = 0;
        if (weapon != null)
            bonusPct = SlayerBonusPercent(SlayerFaction.FromItem(weapon), victimFaction, attacker, target);
        if (bonusPct == 0)
        {
            var talisman = attacker.GetEquippedItem(Layer.Talisman);
            if (talisman != null)
                bonusPct = SlayerBonusPercent(SlayerFaction.FromItem(talisman), victimFaction, attacker, target);
        }

        long scaled = damage + (long)damage * bonusPct / 100;
        return (int)Math.Clamp(scaled, 0L, int.MaxValue);
    }

    private static int SlayerBonusPercent(SlayerFaction slayer, SlayerFaction victimFaction,
        Character attacker, Character target)
    {
        if (slayer.IsNone)
            return 0;
        if (!target.IsPlayer)
            return slayer.GetSlayerDamageBonusPercent(victimFaction);
        if (!attacker.IsPlayer)
            return slayer.GetSlayerDamagePenaltyPercent(victimFaction);
        return 0;
    }

    /// <summary>AOS on-hit properties (Source-X Fight_Hit tail,
    /// CCharFight.cpp:2270-2361), with the reference formulas:
    /// HITLEECHLIFE heals rand(0 .. dmg×prop×30/10000), HITLEECHMANA
    /// rand(0 .. dmg×prop×40/10000), HITLEECHSTAM restores the full damage
    /// prop% of the time, HITMANADRAIN steals 20% of the damage from the
    /// victim's mana prop% of the time. With a weapon equipped, HITAREA*
    /// splash half the damage around the victim (the elemental variants only
    /// under COMBAT_ELEMENTAL_ENGINE) and HITDISPEL/FIREBALL/HARM/LIGHTNING/
    /// MAGICARROW proc their spell — both via engine hooks. Property values
    /// are read from the attacker plus the weapon and talisman (instance tag,
    /// then ITEMDEF def-tag).</summary>
    public static void ApplyAosOnHitEffects(Character attacker, Character target, int damage,
        Item? weapon, CombatFlags flags)
    {
        bool leeched = false;

        int leechLife = GetOnHitPropertyValue(attacker, weapon, "HITLEECHLIFE");
        // Source-X CCharFight.cpp:2272-2275: a Curse Weapon effect adds its level
        // to the life-leech percent, but only with a weapon equipped.
        if (weapon != null && attacker.CurseWeaponLevel > 0)
            leechLife += attacker.CurseWeaponLevel;
        // Necromancy Vampiric Embrace (reference SPELL_Vampiric_Embrace): the form
        // leeches life on any damaging hit, armed or not.
        if (attacker.VampiricEmbraceActive)
            leechLife += 20;
        if (leechLife > 0)
        {
            long maxHeal = (long)damage * leechLife * 30 / 10000;
            int heal = (int)_rand.NextInt64(0, Math.Min(maxHeal, short.MaxValue) + 1);
            attacker.Hits = (short)Math.Min((long)attacker.MaxHits, (long)attacker.Hits + heal);
            leeched = true;
        }

        int leechMana = GetOnHitPropertyValue(attacker, weapon, "HITLEECHMANA");
        if (leechMana > 0)
        {
            long maxGain = (long)damage * leechMana * 40 / 10000;
            int gain = (int)_rand.NextInt64(0, Math.Min(maxGain, short.MaxValue) + 1);
            attacker.Mana = (short)Math.Min((long)attacker.MaxMana, (long)attacker.Mana + gain);
            leeched = true;
        }

        if (RollOnHitChance(attacker, weapon, "HITLEECHSTAM"))
        {
            attacker.Stam = (short)Math.Min((long)attacker.MaxStam, (long)attacker.Stam + damage);
            leeched = true;
        }

        int manaDrain = 0;
        if (RollOnHitChance(attacker, weapon, "HITMANADRAIN"))
            manaDrain = (int)((long)damage * 20 / 100);
        // Source-X CCharFight.cpp:2299-2304: Wraith Form drains the target's mana
        // to the attacker, scaled by SpiritSpeak, on any damaging hit.
        if (attacker.WraithFormActive)
            manaDrain += 5 + 15 * attacker.GetSkill(SkillType.SpiritSpeak) / 1000;
        manaDrain = Math.Min(manaDrain, (int)target.Mana);
        if (manaDrain > 0)
        {
            target.Mana = (short)(target.Mana - manaDrain);
            attacker.Mana = (short)Math.Min((long)attacker.MaxMana, (long)attacker.Mana + manaDrain);
            leeched = true;
        }

        if (leeched)
            OnLeechEffect?.Invoke(attacker);

        // The weapon-borne procs (Source-X gates the whole block on pWeapon).
        if (weapon == null)
            return;

        if (RollOnHitChance(attacker, weapon, "HITAREAPHYSICAL"))
            OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Physical);
        if (flags.HasFlag(CombatFlags.ElementalEngine))
        {
            if (RollOnHitChance(attacker, weapon, "HITAREAFIRE"))
                OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Fire);
            if (RollOnHitChance(attacker, weapon, "HITAREACOLD"))
                OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Cold);
            if (RollOnHitChance(attacker, weapon, "HITAREAPOISON"))
                OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Poison);
            if (RollOnHitChance(attacker, weapon, "HITAREAENERGY"))
                OnHitAreaDamage?.Invoke(attacker, target, damage / 2, DamageType.Energy);
        }

        if (RollOnHitChance(attacker, weapon, "HITDISPEL"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.Dispel);
            if (!CanContinueOnHitProcs(attacker, target)) return;
        }
        if (RollOnHitChance(attacker, weapon, "HITFIREBALL"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.Fireball);
            if (!CanContinueOnHitProcs(attacker, target)) return;
        }
        if (RollOnHitChance(attacker, weapon, "HITHARM"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.Harm);
            if (!CanContinueOnHitProcs(attacker, target)) return;
        }
        if (RollOnHitChance(attacker, weapon, "HITLIGHTNING"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.Lightning);
            if (!CanContinueOnHitProcs(attacker, target)) return;
        }
        if (RollOnHitChance(attacker, weapon, "HITMAGICARROW"))
        {
            OnHitSpell?.Invoke(attacker, target, (int)SpellType.MagicArrow);
        }
    }

    private static bool CanContinueOnHitProcs(Character attacker, Character target) =>
        !attacker.IsDeleted && !attacker.IsDead && !target.IsDeleted && !target.IsDead;

    private static bool RollOnHitChance(Character attacker, Item? weapon, string property) =>
        _rand.Next(100) < Math.Clamp(GetOnHitPropertyValue(attacker, weapon, property), 0, 100);

    /// <summary>An AOS on-hit property's effective value: the attacker's own
    /// tag plus the weapon's and the equipped talisman's (instance tag first,
    /// ITEMDEF def-tag fallback). Source-X reads a char-level aggregate;
    /// SphereNet aggregates at read time from the realistic carriers.</summary>
    public static int GetOnHitPropertyValue(Character attacker, Item? weapon, string prop)
    {
        long total = 0;
        if (attacker.TryGetTag(prop, out var raw) && int.TryParse(raw, out int own))
            total += own;
        if (weapon != null)
            total += GetItemNumProperty(weapon, prop);
        var talisman = attacker.GetEquippedItem(Layer.Talisman);
        if (talisman != null && talisman != weapon)
            total += GetItemNumProperty(talisman, prop);
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    /// <summary>Source-X CCPropsChar equipment aggregate. LayerAdd/LayerRemove
    /// maintains this eagerly upstream; SphereNet derives it on demand from
    /// the character plus every equipped item to avoid stale totals.</summary>
    public static int GetEquipmentPropertyValue(Character character, string property)
    {
        long total = character.Tags.GetInt(property);
        for (int layerIndex = (int)Layer.OneHanded; layerIndex <= (int)Layer.Horse; layerIndex++)
        {
            var equipped = character.GetEquippedItem((Layer)layerIndex);
            if (equipped != null)
                total += GetItemNumProperty(equipped, property);
        }
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    private static int GetItemNumProperty(Item item, string prop)
    {
        if (item.TryGetTag(prop, out var raw) && int.TryParse(raw, out int v))
            return v;
        var def = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
        if (def != null && int.TryParse(def.TagDefs.Get(prop), out int dv))
            return dv;
        return 0;
    }

    /// <summary>Sum a resist property across every equipped item — the AOS suit
    /// contribution (reference CCPropsChar equip aggregate). Excludes the
    /// character's own base field so it can be added to it without double count.</summary>
    private static int SumEquippedItemProperty(Character ch, string prop)
    {
        long total = 0;
        for (int layerIndex = (int)Layer.OneHanded; layerIndex <= (int)Layer.Horse; layerIndex++)
        {
            var equipped = ch.GetEquippedItem((Layer)layerIndex);
            if (equipped != null)
                total += GetItemNumProperty(equipped, prop);
        }
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    /// <summary>Effective elemental resist: base resist plus the suit contribution
    /// from equipped items, derived on read (no equip-time mutation, no persistence
    /// risk). Clamped to a valid 0-100 percent.</summary>
    public static int EffectiveResist(Character ch, DamageType type)
    {
        (string prop, int baseVal) = type switch
        {
            DamageType.Fire => ("RESFIRE", (int)ch.ResFire),
            DamageType.Cold => ("RESCOLD", (int)ch.ResCold),
            DamageType.Poison => ("RESPOISON", (int)ch.ResPoison),
            DamageType.Energy => ("RESENERGY", (int)ch.ResEnergy),
            _ => ("RESPHYSICAL", (int)ch.ResPhysical),
        };
        return Math.Clamp(baseVal + SumEquippedItemProperty(ch, prop), 0, 100);
    }

    public static int EffResPhysical(Character ch) => EffectiveResist(ch, DamageType.Physical);
    public static int EffResFire(Character ch) => EffectiveResist(ch, DamageType.Fire);
    public static int EffResCold(Character ch) => EffectiveResist(ch, DamageType.Cold);
    public static int EffResPoison(Character ch) => EffectiveResist(ch, DamageType.Poison);
    public static int EffResEnergy(Character ch) => EffectiveResist(ch, DamageType.Energy);

    /// <summary>Effective STR/DEX/INT: the base stat plus the suit contribution
    /// from equipped items (BONUSSTR/BONUSDEX/BONUSINT), derived on read. Used by
    /// the correctness-facing reads (display, melee damage, carry weight, REQSTR
    /// gate, skill contribution); the stored max pools stay derived from the base
    /// stat so no feedback loop forms.</summary>
    public static int EffectiveStr(Character ch) =>
        Math.Max(0, ch.Str + SumEquippedItemProperty(ch, "BONUSSTR"));
    public static int EffectiveDex(Character ch) =>
        Math.Max(0, ch.Dex + SumEquippedItemProperty(ch, "BONUSDEX"));
    public static int EffectiveInt(Character ch) =>
        Math.Max(0, ch.Int + SumEquippedItemProperty(ch, "BONUSINT"));

    /// <summary>Effective max hit/mana/stamina pool: the stored base pool plus the
    /// suit contribution (Source-X BONUSHITSMAX/BONUSMANAMAX/BONUSSTAMMAX), derived
    /// on read so the base field persists clean. Floored at 0 to preserve the
    /// old-save "MaxHits &lt;= 0 -> backfill from stat" login path. Reads the raw base
    /// field (BaseMaxHits), never the effective property, so there is no recursion.</summary>
    public static int EffectiveMaxHits(Character ch) =>
        Math.Max(0, ch.BaseMaxHits + SumEquippedItemProperty(ch, "BONUSHITSMAX"));
    public static int EffectiveMaxMana(Character ch) =>
        Math.Max(0, ch.BaseMaxMana + SumEquippedItemProperty(ch, "BONUSMANAMAX"));
    public static int EffectiveMaxStam(Character ch) =>
        Math.Max(0, ch.BaseMaxStam + SumEquippedItemProperty(ch, "BONUSSTAMMAX"));

    /// <summary>Effective luck: the base Luck plus the suit contribution (Source-X
    /// PROPCH_LUCK equip-time accumulation). Derived on read like the stat slice.
    /// Source-X aggregates and DISPLAYS luck but consumes it nowhere — no loot,
    /// damage, or spawn hook reads it — so this feeds the status display only;
    /// wiring it into drop quality would diverge from the reference. Unclamped:
    /// UNLUCKY items push it below zero, matching Source-X.</summary>
    public static int EffectiveLuck(Character ch) =>
        ch.Luck + SumEquippedItemProperty(ch, "LUCK");

    /// <summary>Attacker's elemental damage split percentages. Source-X
    /// OnTakeDamage (CCharFight.cpp:721): an unset physical share is assumed
    /// to be the remainder the elemental percents leave of 100.</summary>
    public static (int Phys, int Fire, int Cold, int Poison, int Energy) GetElementalSplit(Character attacker)
    {
        int fire = attacker.DamFire, cold = attacker.DamCold,
            poison = attacker.DamPoison, energy = attacker.DamEnergy;
        int phys = attacker.DamPhysical;
        if (phys == 0)
            phys = Math.Max(0, 100 - (fire + cold + poison + energy));
        return (phys, fire, cold, poison, energy);
    }

    /// <summary>
    /// Uses the highest matching resist for the damage type.
    /// </summary>
    public static int ApplyElementalResist(Character target, int damage, DamageType dmgType)
    {
        if (damage <= 0) return 0;

        int resist = 0;

        // Physical damage types (blunt/slash/pierce). Resists are the effective
        // suit totals (base + equipped-item bonuses), derived on read.
        if (dmgType.HasFlag(DamageType.Physical) || dmgType.HasFlag(DamageType.HitBlunt) ||
            dmgType.HasFlag(DamageType.HitSlash) || dmgType.HasFlag(DamageType.HitPierce))
            resist = Math.Max(resist, EffResPhysical(target));

        if (dmgType.HasFlag(DamageType.Fire))
            resist = Math.Max(resist, EffResFire(target));

        if (dmgType.HasFlag(DamageType.Cold))
            resist = Math.Max(resist, EffResCold(target));

        if (dmgType.HasFlag(DamageType.Poison))
            resist = Math.Max(resist, EffResPoison(target));

        if (dmgType.HasFlag(DamageType.Energy) || dmgType.HasFlag(DamageType.Magic))
            resist = Math.Max(resist, EffResEnergy(target));

        // God damage ignores resist
        if (dmgType.HasFlag(DamageType.God))
            resist = 0;

        resist = Math.Clamp(resist, 0, 100);
        return (int)(damage - (long)damage * resist / 100);
    }

    /// <summary>
    /// Pre-AOS swing delay (Source-X <c>Calc_CombatAttackSpeed</c> formula 0).
    /// Returns the full swing recoil in milliseconds.
    /// </summary>
    public static int GetSwingDelayMs(Character attacker, Item? weapon)
    {
        int weaponSpeed = weapon?.Speed ?? 0;
        int baseSpeed = weaponSpeed > 0 ? weaponSpeed : 50;
        int dex = Math.Max(0, (int)attacker.Dex);
        long speedScale = Math.Max(1, Character.CombatSpeedScaleFactor);
        int swingSpeedIncrease = Math.Clamp(GetEquipmentPropertyValue(
            attacker, CombatSpeedProperties.IncreaseSwingSpeed), -99, 10_000);
        long deciseconds;
        switch (Character.CombatSpeedEra)
        {
            case 2: // AOS (Source-X era 2)
            {
                long swingSpeed = (long)(dex + 100) * baseSpeed;
                swingSpeed = Math.Max(1, swingSpeed * (100 + swingSpeedIncrease) / 100);
                deciseconds = ((speedScale * 10) / swingSpeed) / 2;
                if (deciseconds < 12) deciseconds = 12;
                break;
            }
            case 3: // Samurai Empire (Source-X era 3)
            {
                long swingSpeed = Math.Max(1,
                    (long)baseSpeed * (100 + swingSpeedIncrease) / 100);
                long ticks = speedScale / Math.Max(1, (dex + 100L) * swingSpeed) - 2;
                if (ticks < 5) ticks = 5;
                deciseconds = ticks * 10 / 4;
                break;
            }
            case 4: // Mondain's Legacy (Source-X era 4)
            {
                long speedFactor = 100 / Math.Max(1, 100 + swingSpeedIncrease);
                long ticks = ((long)baseSpeed * 4 - (dex / 30)) * speedFactor;
                if (ticks < 5) ticks = 5;
                deciseconds = ticks * 10 / 4;
                break;
            }
            case 1: // pre-AOS (Source-X era 1)
            {
                long swingSpeed = Math.Max(1, (long)(dex + 100) * baseSpeed);
                deciseconds = (speedScale * 10) / swingSpeed;
                if (deciseconds < 1) deciseconds = 1;
                break;
            }
            default: // Sphere custom (Source-X era 0)
            {
                if (weapon != null && weaponSpeed > 0)
                {
                    long swingSpeed = Math.Max(1, (long)(dex + 100) * weaponSpeed);
                    deciseconds = (speedScale * 10) / swingSpeed;
                    if (deciseconds < 5) deciseconds = 5;
                    break;
                }

                deciseconds = (long)(100 - dex) * 40 / 100;
                if (deciseconds < 5) deciseconds = 5;
                else deciseconds += 5;
                if (weapon != null)
                {
                    long weightMod = (long)Math.Max(0, weapon.Weight) * 10 /
                        (4 * Item.WeightUnits);
                    if (weapon.IsTwoHanded)
                        weightMod += deciseconds / 2;
                    deciseconds += weightMod;
                }
                else
                {
                    deciseconds += 2;
                }
                break;
            }
        }

        return (int)Math.Clamp(deciseconds * 100, 100L, 60_000L);
    }
}
