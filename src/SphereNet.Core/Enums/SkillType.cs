namespace SphereNet.Core.Enums;

/// <summary>
/// Skill types. Maps exactly to SKILL_TYPE in Source-X uofiles_enums.h.
/// SKILL_QTY = 99 (reserved slots), actual skills 0-57.
/// NPC actions start at 100.
/// </summary>
public enum SkillType : short
{
    None = -1,

    Alchemy = 0,
    Anatomy = 1,
    AnimalLore = 2,
    ItemId = 3,
    ArmsLore = 4,
    Parrying = 5,
    Begging = 6,
    Blacksmithing = 7,
    Bowcraft = 8,
    Peacemaking = 9,
    Camping = 10,
    Carpentry = 11,
    Cartography = 12,
    Cooking = 13,
    DetectingHidden = 14,
    Enticement = 15,
    EvalInt = 16,
    Healing = 17,
    Fishing = 18,
    Forensics = 19,
    Herding = 20,
    Hiding = 21,
    Provocation = 22,
    Inscription = 23,
    Lockpicking = 24,
    Magery = 25,
    MagicResistance = 26,
    Tactics = 27,
    Snooping = 28,
    Musicianship = 29,
    Poisoning = 30,
    Archery = 31,
    SpiritSpeak = 32,
    Stealing = 33,
    Tailoring = 34,
    Taming = 35,
    TasteId = 36,
    Tinkering = 37,
    Tracking = 38,
    Veterinary = 39,
    Swordsmanship = 40,
    MaceFighting = 41,
    Fencing = 42,
    Wrestling = 43,
    Lumberjacking = 44,
    Mining = 45,
    Meditation = 46,
    Stealth = 47,
    RemoveTrap = 48,
    Necromancy = 49,
    Focus = 50,
    Chivalry = 51,
    Bushido = 52,
    Ninjitsu = 53,
    Spellweaving = 54,
    Mysticism = 55,
    Imbuing = 56,
    Throwing = 57,

    Qty = 99,
}

/// <summary>
/// NPC action types. Maps to NPCACT_* in Source-X.
/// These follow skill indices, starting at 100.
/// </summary>
public enum NpcAction : short
{
    FollowTarg = 100,
    Stay = 101,
    GoTo = 102,
    Wander = 103,
    Looking = 104,
    Flee = 105,
    Talk = 106,
    TalkFollow = 107,
    GuardTarg = 108,
    GoHome = 109,
    Breath = 110,
    Ridden = 111,
    Throwing = 112,
    Training = 113,
    Napping = 114,
    Food = 115,
    RunTo = 116,

    Qty = 117,
}

/// <summary>
/// Skill definition flags. Maps to SKF_* in Source-X.
/// </summary>
[Flags]
public enum SkillFlag
{
    None        = 0,
    Scripted    = 0x0001,   // SKF_SCRIPTED — skill handled entirely by script triggers
    Fight       = 0x0002,   // SKF_FIGHT
    Magic       = 0x0004,   // SKF_MAGIC
    Craft       = 0x0008,   // SKF_CRAFT
    Gather      = 0x0010,   // SKF_GATHER
    Disabled    = 0x0020,   // SKF_DISABLED
    NoFail      = 0x0040,   // SKF_NOFAIL
    RankedTitle = 0x0080,   // SKF_RANKEDTITLE
}
