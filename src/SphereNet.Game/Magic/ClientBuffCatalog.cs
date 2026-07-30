using SphereNet.Core.Enums;

namespace SphereNet.Game.Magic;

/// <summary>Source-X buff icon and cliloc mapping used by packet 0xDF.</summary>
public readonly record struct ClientBuffDefinition(
    BuffIcon Icon, uint TitleCliloc, uint DescriptionCliloc);

public static class ClientBuffCatalog
{
    // Cliloc pairs are taken verbatim from the Source-X addBuff call sites
    // (CClientMsg.cpp resendBuffs, CCharSpell.cpp Spell_Effect_Add). A few of
    // them reuse another spell's clilocs upstream (Arcane Empowerment and
    // Corpse Skin both pass 1075805/1075804); that quirk is reproduced rather
    // than "corrected", so the client shows the same text Source-X shows.
    private static readonly IReadOnlyDictionary<BuffIcon, ClientBuffDefinition> s_byIcon =
        new Dictionary<BuffIcon, ClientBuffDefinition>
        {
            [BuffIcon.Hidden] = new(BuffIcon.Hidden, 1075655, 1075656),
            [BuffIcon.ActiveMeditation] = new(BuffIcon.ActiveMeditation, 1075657, 1075658),
            [BuffIcon.NightSight] = new(BuffIcon.NightSight, 1075643, 1075644),
            [BuffIcon.Clumsy] = new(BuffIcon.Clumsy, 1075831, 1075832),
            [BuffIcon.Feeblemind] = new(BuffIcon.Feeblemind, 1075833, 1075834),
            [BuffIcon.Weaken] = new(BuffIcon.Weaken, 1075837, 1075838),
            [BuffIcon.Curse] = new(BuffIcon.Curse, 1075835, 1075836),
            [BuffIcon.MassCurse] = new(BuffIcon.MassCurse, 1075839, 1075840),
            [BuffIcon.Strength] = new(BuffIcon.Strength, 1075845, 1075846),
            [BuffIcon.Agility] = new(BuffIcon.Agility, 1075841, 1075842),
            [BuffIcon.Cunning] = new(BuffIcon.Cunning, 1075843, 1075844),
            [BuffIcon.Bless] = new(BuffIcon.Bless, 1075847, 1075848),
            [BuffIcon.ReactiveArmor] = new(BuffIcon.ReactiveArmor, 1075812, 1070722),
            [BuffIcon.Protection] = new(BuffIcon.Protection, 1075814, 1070722),
            [BuffIcon.ArchProtection] = new(BuffIcon.ArchProtection, 1075816, 1070722),
            [BuffIcon.Poison] = new(BuffIcon.Poison, 1017383, 1070722),
            [BuffIcon.Incognito] = new(BuffIcon.Incognito, 1075819, 1075820),
            [BuffIcon.Paralyze] = new(BuffIcon.Paralyze, 1075827, 1075828),
            [BuffIcon.MagicReflection] = new(BuffIcon.MagicReflection, 1075817, 1070722),
            [BuffIcon.Invisibility] = new(BuffIcon.Invisibility, 1075825, 1075826),
            [BuffIcon.Polymorph] = new(BuffIcon.Polymorph, 1075824, 1070722),

            // Polymorph-layer forms — Source-X passes the shared polymorph
            // clilocs with a per-form icon (CCharSpell.cpp:1130).
            [BuffIcon.HorrificBeast] = new(BuffIcon.HorrificBeast, 1075824, 1070722),
            [BuffIcon.LichForm] = new(BuffIcon.LichForm, 1075824, 1070722),
            [BuffIcon.VampiricEmbrace] = new(BuffIcon.VampiricEmbrace, 1075824, 1070722),
            [BuffIcon.WraithForm] = new(BuffIcon.WraithForm, 1075824, 1070722),
            [BuffIcon.ReaperForm] = new(BuffIcon.ReaperForm, 1075824, 1070722),
            [BuffIcon.StoneForm] = new(BuffIcon.StoneForm, 1075824, 1070722),

            // Necromancy / Spellweaving effects with their own icons.
            [BuffIcon.Strangle] = new(BuffIcon.Strangle, 1075794, 1075795),
            [BuffIcon.CorpseSkin] = new(BuffIcon.CorpseSkin, 1075805, 1075804),
            [BuffIcon.BloodOathCurse] = new(BuffIcon.BloodOathCurse, 1075659, 1075660),
            [BuffIcon.BloodOathCaster] = new(BuffIcon.BloodOathCaster, 1075661, 1075662),
            [BuffIcon.GiftOfRenewal] = new(BuffIcon.GiftOfRenewal, 1075796, 1075797),
            [BuffIcon.AttuneWeapon] = new(BuffIcon.AttuneWeapon, 1075798, 1075799),
            [BuffIcon.Thunderstorm] = new(BuffIcon.Thunderstorm, 1075800, 1075801),
            [BuffIcon.EssenceOfWind] = new(BuffIcon.EssenceOfWind, 1075802, 1075803),
            [BuffIcon.EtherealVoyage] = new(BuffIcon.EtherealVoyage, 1075804, 1075805),
            [BuffIcon.GiftOfLife] = new(BuffIcon.GiftOfLife, 1075806, 1075807),
            [BuffIcon.ArcaneEmpowerment] = new(BuffIcon.ArcaneEmpowerment, 1075805, 1075804),
            [BuffIcon.MortalStrike] = new(BuffIcon.MortalStrike, 1075810, 1075811),
            [BuffIcon.CriminalStatus] = new(BuffIcon.CriminalStatus, 1153802, 1153828),
            // Source-X receive.cpp:3366 (gargoyle fly toggle, 0xBF.0x32).
            [BuffIcon.GargoyleFly] = new(BuffIcon.GargoyleFly, 1112193, 1112567),
        };

    private static readonly IReadOnlyDictionary<SpellType, BuffIcon> s_spellIcons =
        new Dictionary<SpellType, BuffIcon>
        {
            [SpellType.NightSight] = BuffIcon.NightSight,
            [SpellType.Clumsy] = BuffIcon.Clumsy,
            [SpellType.Feeblemind] = BuffIcon.Feeblemind,
            [SpellType.Weaken] = BuffIcon.Weaken,
            [SpellType.Curse] = BuffIcon.Curse,
            [SpellType.MassCurse] = BuffIcon.MassCurse,
            [SpellType.Strength] = BuffIcon.Strength,
            [SpellType.Agility] = BuffIcon.Agility,
            [SpellType.Cunning] = BuffIcon.Cunning,
            [SpellType.Bless] = BuffIcon.Bless,
            [SpellType.ReactiveArmor] = BuffIcon.ReactiveArmor,
            [SpellType.Protection] = BuffIcon.Protection,
            [SpellType.ArchProtection] = BuffIcon.ArchProtection,
            [SpellType.Poison] = BuffIcon.Poison,
            [SpellType.Incognito] = BuffIcon.Incognito,
            [SpellType.Paralyze] = BuffIcon.Paralyze,
            [SpellType.MagicReflect] = BuffIcon.MagicReflection,
            [SpellType.Invisibility] = BuffIcon.Invisibility,

            // Polymorph layer — Source-X CCharSpell.cpp:621-656 picks the icon
            // per form and shares the polymorph clilocs.
            [SpellType.Polymorph] = BuffIcon.Polymorph,
            [SpellType.BeastForm] = BuffIcon.Polymorph,
            [SpellType.MonsterForm] = BuffIcon.Polymorph,
            [SpellType.HorrificBeast] = BuffIcon.HorrificBeast,
            [SpellType.LichForm] = BuffIcon.LichForm,
            [SpellType.VampiricEmbrace] = BuffIcon.VampiricEmbrace,
            [SpellType.WraithForm] = BuffIcon.WraithForm,
            [SpellType.ReaperForm] = BuffIcon.ReaperForm,
            [SpellType.StoneForm] = BuffIcon.StoneForm,

            [SpellType.Strangle] = BuffIcon.Strangle,
            [SpellType.CorpseSkin] = BuffIcon.CorpseSkin,
            [SpellType.BloodOath] = BuffIcon.BloodOathCurse,
            [SpellType.GiftOfRenewal] = BuffIcon.GiftOfRenewal,
            [SpellType.Attunement] = BuffIcon.AttuneWeapon,
            [SpellType.Thunderstorm] = BuffIcon.Thunderstorm,
            [SpellType.EssenceOfWind] = BuffIcon.EssenceOfWind,
            [SpellType.EtherealVoyage] = BuffIcon.EtherealVoyage,
            [SpellType.GiftOfLife] = BuffIcon.GiftOfLife,
            [SpellType.ArcaneEmpowerment] = BuffIcon.ArcaneEmpowerment,
        };

    public static bool TryGet(BuffIcon icon, out ClientBuffDefinition definition) =>
        s_byIcon.TryGetValue(icon, out definition);

    public static bool TryGet(SpellType spell, out ClientBuffDefinition definition)
    {
        if (s_spellIcons.TryGetValue(spell, out var icon))
            return TryGet(icon, out definition);
        definition = default;
        return false;
    }
}
