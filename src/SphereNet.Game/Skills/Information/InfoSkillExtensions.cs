using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.Skills.Information;

/// <summary>
/// Helpers used by <see cref="InfoSkillEngine"/> that wrap small Source-X CChar /
/// CItem accessors not (yet) present on SphereNet's domain types. Centralised here
/// so the engine itself stays a literal port of CClientTarg.cpp and can be diff'd
/// 1:1 against upstream.
/// </summary>
internal static class InfoSkillExtensions
{
    // ---- Character: pronouns (Source-X CChar::GetPronoun / GetPossessPronoun) ----

    /// <summary>True if the character body is a human female (0x0191) or female ghost (0x0193).</summary>
    public static bool IsFemaleBody(this Character ch) =>
        ch.BodyId is 0x0191 or 0x0193;

    /// <summary>
    /// "he" / "she" / "it" via DEFMSG lookup. Animals + monsters resolve to "it" the
    /// same way Source-X's GetPronoun() does (NPC brain + body class fallback).
    /// </summary>
    public static string GetPronoun(this Character ch)
    {
        if (ch.NpcBrain is NpcBrainType.Animal or NpcBrainType.Monster
            or NpcBrainType.Berserk or NpcBrainType.Dragon)
            return ServerMessages.Get(Msg.PronounIt);

        return ServerMessages.Get(ch.IsFemaleBody() ? Msg.PronounShe : Msg.PronounHe);
    }

    /// <summary>"his" / "her" / "its" (Source-X DEFMSG_POSSESSPRONOUN_*).</summary>
    public static string GetPossessPronoun(this Character ch)
    {
        if (ch.NpcBrain is NpcBrainType.Animal or NpcBrainType.Monster
            or NpcBrainType.Berserk or NpcBrainType.Dragon)
            return ServerMessages.Get(Msg.PossesspronounIts);

        return ServerMessages.Get(ch.IsFemaleBody() ? Msg.PossesspronounHer : Msg.PossesspronounHis);
    }

    /// <summary>Trade name == the chardef's display name (Source-X CCharDef::GetTradeName).</summary>
    public static string GetTradeName(this Character ch)
    {
        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        if (def != null && !string.IsNullOrEmpty(def.Name))
            return def.Name;
        return ch.Name;
    }

    /// <summary>
    /// True when the character carries a unique name that differs from its chardef
    /// trade name -- mirrors Source-X CChar::IsIndividualName().
    /// </summary>
    public static bool IsIndividualName(this Character ch)
    {
        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        if (def == null || string.IsNullOrEmpty(def.Name)) return false;
        return !ch.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pet master lookup. Returns null when the character has no master or the
    /// master can't be resolved (e.g. owner offline + not loaded). Mirrors
    /// CChar::NPC_PetGetOwner().
    /// </summary>
    public static Character? GetPetOwner(this Character ch, World.GameWorld world)
    {
        return ch.ResolveOwnerCharacter() ?? (ch.OwnerSerial.IsValid ? world.FindChar(ch.OwnerSerial) : null);
    }

    // ---- Item: ARMSLORE / TASTEID accessors ----

    /// <summary>True for any armor / shield / weapon / clothing / jewelry that ArmsLore inspects.</summary>
    public static bool IsTypeArmorWeapon(this Item it) =>
        IsArmorPiece(it.ItemType) || IsWeapon(it.ItemType);

    public static bool IsArmorPiece(ItemType t) => t is
        ItemType.Armor or ItemType.Shield or ItemType.Clothing or ItemType.Jewelry;

    public static bool IsWeapon(ItemType t) => t is
        ItemType.WeaponMaceSharp;

    /// <summary>
    /// Weapon type set that ArmsLore prints damage for. Source-X enumerates
    /// IT_WEAPON_MACE_*, IT_WEAPON_SWORD, IT_WEAPON_AXE, IT_WEAPON_FENCE,
    /// IT_WEAPON_BOW, IT_WEAPON_XBOW, IT_WEAPON_THROWING. SphereNet's ItemType
    /// only exposes <see cref="ItemType.WeaponMaceSharp"/> directly today; richer
    /// weapon variants live in tags / chardef so we treat any non-armor combat
    /// item as a weapon for the ArmsLore branch.
    /// </summary>
    public static bool ArmsLoreShowsAsWeapon(this Item it) => IsWeapon(it.ItemType);

    /// <summary>
    /// Defense rating used by ARMSLORE_DEF. Pulls from the itemdef's ARMOR
    /// property when available; falls back to the runtime ARMOR tag (Source-X
    /// CItem::Armor_GetDefense). Returns 0 when nothing is set so the line still
    /// renders deterministically.
    /// </summary>
    public static int GetArmorDefense(this Item it)
    {
        if (it.TryGetTag("ARMOR", out string? tag) && int.TryParse(tag, out int v))
            return v;
        var def = DefinitionLoader.GetItemDef(it.BaseId);
        if (def == null) return 0;
        // Source-X averages min/max when both present.
        if (def.DefenseMin == def.DefenseMax) return def.DefenseMin;
        return (def.DefenseMin + def.DefenseMax) / 2;
    }

    /// <summary>Attack rating used by ARMSLORE_DAM (CItem::Weapon_GetAttack).</summary>
    public static int GetWeaponAttack(this Item it)
    {
        if (it.TryGetTag("DAM", out string? tag) && int.TryParse(tag, out int v))
            return v;
        var def = DefinitionLoader.GetItemDef(it.BaseId);
        if (def == null) return 0;
        if (def.AttackMin == def.AttackMax) return def.AttackMin;
        return (def.AttackMin + def.AttackMax) / 2;
    }

    /// <summary>Repair quality desc -- Source-X CItem::Armor_GetRepairDesc().</summary>
    public static string GetArmorRepairDesc(this Item it)
    {
        // Source-X buckets HP into 5 quality bands; we approximate from current/max
        // hits which themselves are stored as tags ("HITS"/"HITSMAX") today.
        int cur = it.GetHitsCur();
        int max = it.GetHitsMax();
        if (max <= 0) return "in good repair";
        int pct = (cur * 100) / Math.Max(1, max);
        return pct switch
        {
            >= 90 => "in nearly perfect condition",
            >= 70 => "in good repair",
            >= 50 => "showing some wear",
            >= 30 => "in need of repair",
            _     => "barely holding together",
        };
    }

    public static int GetHitsCur(this Item it) =>
        it.TryGetTag("HITS", out string? s) && int.TryParse(s, out int v) ? v : 0;

    public static int GetHitsMax(this Item it) =>
        it.TryGetTag("HITSMAX", out string? s) && int.TryParse(s, out int v) ? v : 0;

    public static int GetPoisonSkill(this Item it) =>
        it.TryGetTag("POISON_SKILL", out string? s) && int.TryParse(s, out int v) ? v : 0;

    /// <summary>
    /// Item full name (Source-X CItem::GetNameFull). Falls back to <see cref="Item.Name"/>
    /// with a "an" / "a" article prefix, applying the standard vowel rule.
    /// </summary>
    public static string GetNameFull(this Item it, bool addArticle)
    {
        string name = string.IsNullOrEmpty(it.Name) ? "item" : it.Name;
        if (it.Amount > 1)
            return $"{it.Amount} {name}";
        if (!addArticle) return name;
        char c0 = char.ToLowerInvariant(name[0]);
        bool vowel = c0 is 'a' or 'e' or 'i' or 'o' or 'u';
        return (vowel ? "an " : "a ") + name;
    }

    public static bool HasObjAttr(this Item it, ObjAttributes attr) =>
        (it.Attributes & attr) != 0;
}
