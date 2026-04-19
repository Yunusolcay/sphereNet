namespace SphereNet.Core.Enums;

/// <summary>
/// Spell IDs. Maps to SPELL_TYPE in Source-X uofiles_enums.h.
/// Organized by skill/circle: Magery (1-64, 8 circles×8), Necromancy (101+),
/// Chivalry (201+), Bushido (401+), Ninjitsu (501+), Spellweaving (601+),
/// Mysticism (678+), Bard Masteries (701+), Skill Masteries (707+).
/// </summary>
public enum SpellType : ushort
{
    None = 0,

    // --- Magery: 1st Circle ---
    Clumsy = 1, CreateFood, Feeblemind, Heal, MagicArrow, NightSight, ReactiveArmor, Weaken,
    // --- 2nd Circle ---
    Agility = 9, Cunning, Cure, Harm, MagicTrap, MagicUntrap, Protection, Strength,
    // --- 3rd Circle ---
    Bless = 17, Fireball, MagicLock, Poison, Telekinesis, Teleport, Unlock, WallOfStone,
    // --- 4th Circle ---
    ArchCure = 25, ArchProtection, Curse, FireField, GreaterHeal, Lightning, ManaDrain, Recall,
    // --- 5th Circle ---
    BladeSpirit = 33, DispelField, Incognito, MagicReflect, MindBlast, Paralyze, PoisonField, SummonCreature,
    // --- 6th Circle ---
    Dispel = 41, EnergyBolt, Explosion, Invisibility, Mark, MassCurse, ParalyzeField, Reveal,
    // --- 7th Circle ---
    ChainLightning = 49, EnergyField, Flamestrike, GateTravel, ManaVampire, MassDispel, MeteorSwarm, Polymorph,
    // --- 8th Circle ---
    Earthquake = 57, EnergyVortex, Resurrection, AirElemental, SummonDaemon, EarthElemental, FireElemental, WaterElemental,

    MageryQty = WaterElemental,

    // --- Necromancy (AOS) ---
    AnimateDeadAOS = 101, BloodOath, CorpseSkin, CurseWeapon, EvilOmen,
    HorrificBeast, LichForm, MindRot, PainSpike, PoisonStrike,
    Strangle, SummonFamiliar, VampiricEmbrace, VengefulSpirit, Wither, WraithForm,
    Exorcism,

    // --- Chivalry ---
    CleanseByFire = 201, CloseWounds, ConsecrateWeapon, DispelEvil, DivineFury,
    EnemyOfOne, HolyLight, NobleSacrifice, RemoveCurse, SacredJourney,

    // --- Bushido ---
    HonorableExecution = 401, Confidence, Evasion, CounterAttack, LightningStrike, MomentumStrike,

    // --- Ninjitsu ---
    FocusAttack = 501, DeathStrike, AnimalForm, KiAttack, SurpriseAttack, Backstab, Shadowjump, MirrorImage,

    // --- Spellweaving ---
    ArcaneCircle = 601, GiftOfRenewal, ImmolatingWeapon, Attunement, Thunderstorm,
    NatureFury, SummonFey, SummonFiend, ReaperForm, Wildfire,
    EssenceOfWind, DryadAllure, EtherealVoyage, WordOfDeath, GiftOfLife, ArcaneEmpowerment,

    // --- Mysticism ---
    NetherBolt = 678, HealingStone, PurgeMagic, Enchant, Sleep, EagleStrike,
    AnimatedWeapon, StoneForm, SpellTrigger, MassSleep, CleansingWinds,
    Bombard, SpellPlague, HailStorm, NetherCyclone, RisingColossus,

    // --- Custom Sphere spells ---
    SummonUndead = 1000, Hallucination,
}

/// <summary>
/// Spell flags. Maps to SPELLFLAG_* defines in Source-X game_macros.h.
/// </summary>
[Flags]
public enum SpellFlag : ulong
{
    None = 0,
    DirAnim = 0x0000001,
    TargItem = 0x0000002,
    TargChar = 0x0000004,
    TargXYZ = 0x0000008,
    TargObj = TargItem | TargChar,
    Harm = 0x0000010,
    FxBolt = 0x0000020,
    FxTarg = 0x0000040,
    Field = 0x0000080,
    Summon = 0x0000100,
    Good = 0x0000200,
    Resist = 0x0000400,
    TargNoSelf = 0x0000800,
    FreezeOnCast = 0x0001000,
    FieldRandomDecay = 0x0002000,
    NoElementalEngine = 0x0004000,
    Disabled = 0x0008000,
    Scripted = 0x0010000,
    PlayerOnly = 0x0020000,
    NoUnparalyze = 0x0040000,
    NoCastAnim = 0x0080000,
    TargNoPlayer = 0x0100000,
    TargNoNPC = 0x0200000,
    NoPrecast = 0x0400000,
    NoFreezeOnCast = 0x0800000,
    Area = 0x1000000,
    Poly = 0x2000000,
    TargDead = 0x4000000,
    Damage = 0x8000000,
    Bless = 0x10000000,
    Curse = 0x20000000,
    Heal = 0x40000000,
    Tick = 0x80000000,
}
