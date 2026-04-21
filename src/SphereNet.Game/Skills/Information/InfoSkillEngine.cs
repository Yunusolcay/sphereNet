using System.Globalization;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Skills.Information;

/// <summary>
/// Source-X parity for the "information" skills dispatched from CClientTarg.cpp
/// after a skill-target click: Anatomy, AnimalLore, ArmsLore, EvalInt, Forensics,
/// ItemID, TasteID. Each method preserves the upstream branch order, constant
/// lookup tables, and message delivery channel (SysMessage vs. addObjMessage),
/// so the text sequence the client sees is byte-identical to Source-X.
///
/// Engine entry points never touch the network directly -- they emit via the
/// <see cref="IInfoSkillSink"/> passed in, which lets unit tests record the
/// exact message stream produced for any (skill, target) pair.
///
/// Skill values are read in the 0..1000 scale SphereNet's <c>GetSkill</c>
/// returns, matching Source-X's iSkillLevel convention.
/// </summary>
public static class InfoSkillEngine
{
    private const int SKTRIG_QTY = 1;

    // ---------------------------------------------------------------- Anatomy

    /// <summary>
    /// Source-X <c>CClient::OnSkill_Anatomy</c>. Buckets STR/DEX into 10-slot
    /// description tables and renders a single overhead line on the target,
    /// optionally followed by an "aura of magic" note for conjured creatures.
    /// </summary>
    public static int Anatomy(IInfoSkillSink sink, Character target, int iSkillLevel, bool fTest = false)
    {
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.NonAlive));
            return -1;
        }

        if (fTest)
        {
            if (target == sink.Self) return 2;
            return sink.Random.Next(60);
        }

        string strDesc = StatBandDesc(target.Str, s_anatomyStr);
        string dexDesc = StatBandDesc(target.Dex, s_anatomyDex);

        string result = ServerMessages.GetFormatted(
            Msg.AnatomyResult, target.Name, strDesc, dexDesc);
        sink.ObjectMessage(target, result);

        if (target.IsStatFlag(StatFlag.Conjured))
            sink.ObjectMessage(target, ServerMessages.Get(Msg.AnatomyMagic));

        return iSkillLevel;
    }

    private static readonly string[] s_anatomyStr =
    {
        Msg.AnatomyStr1, Msg.AnatomyStr2, Msg.AnatomyStr3, Msg.AnatomyStr4, Msg.AnatomyStr5,
        Msg.AnatomyStr6, Msg.AnatomyStr7, Msg.AnatomyStr8, Msg.AnatomyStr9, Msg.AnatomyStr10,
    };
    private static readonly string[] s_anatomyDex =
    {
        Msg.AnatomyDex1, Msg.AnatomyDex2, Msg.AnatomyDex3, Msg.AnatomyDex4, Msg.AnatomyDex5,
        Msg.AnatomyDex6, Msg.AnatomyDex7, Msg.AnatomyDex8, Msg.AnatomyDex9, Msg.AnatomyDex10,
    };

    // ---------------------------------------------------------- Animal Lore

    /// <summary>
    /// Source-X <c>CClient::OnSkill_AnimalLore</c>. Emits up to three overhead
    /// lines: trade-name identification, master-relationship line, food level.
    /// </summary>
    public static int AnimalLore(IInfoSkillSink sink, Character target, GameWorld world, int iSkillLevel, bool fTest = false)
    {
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.NonAlive));
            return -1;
        }

        if (fTest)
        {
            if (target == sink.Self) return 2;
            if (target.IsPlayer) return sink.Random.Next(10);
            return sink.Random.Next(60);
        }

        string pronoun = target.GetPronoun();
        string possess = target.GetPossessPronoun();

        // Individual name -> "Rex, the large horse."
        if (target.IsIndividualName())
        {
            sink.ObjectMessage(target, ServerMessages.GetFormatted(
                Msg.AnimalloreResult, target.Name, target.GetTradeName()));
        }

        // Master relationship.
        Character? owner = target.GetPetOwner(world);
        string masterLine;
        if (owner == null)
        {
            masterLine = ServerMessages.GetFormatted(Msg.AnimalloreFree, pronoun, possess);
        }
        else
        {
            string ownerName = owner == sink.Self
                ? ServerMessages.Get(Msg.AnimalloreMasterYou)
                : owner.Name;
            masterLine = ServerMessages.GetFormatted(Msg.AnimalloreMaster, pronoun, ownerName);
        }
        sink.ObjectMessage(target, masterLine);

        // Food state. Conjured creatures always render the same line.
        string foodText = target.IsStatFlag(StatFlag.Conjured)
            ? ServerMessages.Get(Msg.AnimalloreConjured)
            : GetFoodLevelMessage(target, ownerOwned: owner != null);

        sink.ObjectMessage(target, ServerMessages.GetFormatted(
            Msg.AnimalloreFood, pronoun, foodText));

        return 0;
    }

    /// <summary>
    /// Returns one of the MSG_PET_FOOD_* bands based on the character's current
    /// food counter. Source-X CChar::Food_GetLevelMessage reads in 30-minute
    /// intervals; we approximate with SphereNet's 0..60 Food range split into
    /// 8 Source-X bands.
    /// </summary>
    private static string GetFoodLevelMessage(Character ch, bool ownerOwned)
    {
        // 8 bands (MSG_PET_FOOD_1..8 / MSG_FOOD_LVL_1..8 for free wildlife).
        string[] keys = ownerOwned
            ? new[]
            {
                Msg.MsgPetFood1, Msg.MsgPetFood2, Msg.MsgPetFood3, Msg.MsgPetFood4,
                Msg.MsgPetFood5, Msg.MsgPetFood6, Msg.MsgPetFood7, Msg.MsgPetFood8,
            }
            : new[]
            {
                Msg.MsgFoodLvl1, Msg.MsgFoodLvl2, Msg.MsgFoodLvl3, Msg.MsgFoodLvl4,
                Msg.MsgFoodLvl5, Msg.MsgFoodLvl6, Msg.MsgFoodLvl7, Msg.MsgFoodLvl8,
            };

        int idx = Math.Clamp(ch.Food * keys.Length / Math.Max(1, 60), 0, keys.Length - 1);
        return ServerMessages.Get(keys[idx]);
    }

    // ---------------------------------------------------------------- ItemID

    /// <summary>
    /// Source-X <c>CClient::OnSkill_ItemID</c>. For characters it prints the
    /// trade-name line; for items it stamps <c>ATTR_IDENTIFIED</c>, prints
    /// estimated gold value, and -- above 40% skill -- the made-of resource list.
    /// </summary>
    public static int ItemID(IInfoSkillSink sink, object? target, int iSkillLevel, bool fTest = false)
    {
        if (target is Character ch)
        {
            if (fTest) return 1;
            sink.SysMessage(ServerMessages.GetFormatted(Msg.ItemidResult, ch.Name));
            return 1;
        }

        if (target is not Item item)
            return -1;

        if (fTest)
        {
            // Already identified -> easier check.
            return item.IsAttr(ObjAttributes.Identified) ? sink.Random.Next(20) : sink.Random.Next(60);
        }

        item.SetAttr(ObjAttributes.Identified);

        int price = item.Price > 0 ? item.Price : EstimateVendorPrice(item);
        if (price <= 0)
        {
            sink.SysMessage(ServerMessages.Get(Msg.ItemidNoval));
        }
        else
        {
            sink.SysMessage(ServerMessages.GetFormatted(
                Msg.ItemidGold, price * item.Amount, item.GetNameFull(true)));
        }

        // High-skill check: list base resources if any.
        if (iSkillLevel > 400)
        {
            var def = Definitions.DefinitionLoader.GetItemDef(item.BaseId);
            if (def != null && def.BaseResources.Count > 0)
            {
                var resources = string.Join(", ", def.BaseResources.ConvertAll(r => r.ToString()));
                sink.SysMessage(ServerMessages.Get(Msg.ItemidMadeof) + resources);
            }
        }

        return iSkillLevel;
    }

    private static int EstimateVendorPrice(Item it)
    {
        var def = Definitions.DefinitionLoader.GetItemDef(it.BaseId);
        if (def == null) return 0;
        if (def.ValueMin == def.ValueMax) return def.ValueMin;
        return (def.ValueMin + def.ValueMax) / 2;
    }

    // -------------------------------------------------------------- EvalInt

    /// <summary>
    /// Source-X <c>CClient::OnSkill_EvalInt</c>. Prints the intellect band,
    /// then -- only at iSkillLevel > 400 -- a second line with magic-skill and
    /// current-mana descriptors.
    /// </summary>
    public static int EvalInt(IInfoSkillSink sink, Character target, int iSkillLevel, bool fTest = false)
    {
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.NonAlive));
            return -1;
        }
        if (fTest)
        {
            if (target == sink.Self) return 2;
            return sink.Random.Next(60);
        }

        int iIntVal = target.Int;
        int iIntEntry = Math.Clamp((iIntVal - 1) / 10, 0, s_evalIntInt.Length - 1);

        sink.SysMessage(ServerMessages.GetFormatted(
            Msg.EvalintResult, target.Name, ServerMessages.Get(s_evalIntInt[iIntEntry])));

        if (iSkillLevel > 400)
        {
            int magerySkill = target.GetSkill(SkillType.Magery);
            int necroSkill  = target.GetSkill(SkillType.Necromancy);
            int magicSkill  = Math.Max(magerySkill, necroSkill);

            int iMagicEntry = Math.Clamp(magicSkill / 200, 0, s_evalIntMag.Length - 1);
            int iManaEntry  = 0;
            if (iIntVal > 0)
                iManaEntry = IMulDiv(target.Mana, s_evalIntMan.Length - 1, iIntVal);
            iManaEntry = Math.Clamp(iManaEntry, 0, s_evalIntMan.Length - 1);

            sink.SysMessage(ServerMessages.GetFormatted(
                Msg.EvalintResult2,
                ServerMessages.Get(s_evalIntMag[iMagicEntry]),
                ServerMessages.Get(s_evalIntMan[iManaEntry])));
        }

        return iSkillLevel;
    }

    private static readonly string[] s_evalIntInt =
    {
        Msg.EvalintInt1, Msg.EvalintInt2, Msg.EvalintInt3, Msg.EvalintInt4, Msg.EvalintInt5,
        Msg.EvalintInt6, Msg.EvalintInt7, Msg.EvalintInt8, Msg.EvalintInt9, Msg.EvalintInt10,
    };
    private static readonly string[] s_evalIntMag =
    {
        Msg.EvalintMag1, Msg.EvalintMag2, Msg.EvalintMag3, Msg.EvalintMag4, Msg.EvalintMag5, Msg.EvalintMag6,
    };
    private static readonly string[] s_evalIntMan =
    {
        Msg.EvalintMan1, Msg.EvalintMan2, Msg.EvalintMan3, Msg.EvalintMan4, Msg.EvalintMan5, Msg.EvalintMan6,
    };

    // ------------------------------------------------------------- ArmsLore

    /// <summary>
    /// Source-X <c>CClient::OnSkill_ArmsLore</c>. Composite multi-clause
    /// SysMessage covering defense/damage, repair state, magic/newbie/repair
    /// flags, and -- for weapons -- a poison level band.
    /// </summary>
    public static int ArmsLore(IInfoSkillSink sink, Item? target, int iSkillLevel, bool fTest = false)
    {
        if (target == null || !target.IsTypeArmorWeapon())
        {
            sink.SysMessage(ServerMessages.Get(Msg.ArmsloreUnable));
            return -SKTRIG_QTY;
        }

        if (fTest) return sink.Random.Next(60);

        bool fWeapon = target.ArmsLoreShowsAsWeapon();
        int iHitsCur = target.GetHitsCur();
        int iHitsMax = target.GetHitsMax();

        string header = fWeapon
            ? ServerMessages.GetFormatted(Msg.ArmsloreDam, target.GetWeaponAttack())
            : ServerMessages.GetFormatted(Msg.ArmsloreDef, target.GetArmorDefense());

        string body = header + ServerMessages.GetFormatted(Msg.ArmsloreRep, target.GetArmorRepairDesc());

        if (iHitsCur <= 3 || iHitsMax <= 3)
            body += ServerMessages.Get(Msg.ArmsloreRep0);

        if (target.IsAttr(ObjAttributes.Magic))
            body += ServerMessages.Get(Msg.ItemMagic);
        else if (target.IsAttr(ObjAttributes.Newbie) || target.IsAttr(ObjAttributes.Move_Never))
            body += ServerMessages.Get(Msg.ItemNewbie);

        // Repairable flag -- Source-X toggles based on ITEMDEF REPAIR.
        var def = Definitions.DefinitionLoader.GetItemDef(target.BaseId);
        if (def != null && !def.Repair)
            body += ServerMessages.Get(Msg.ItemRepair);

        // Weapon poison level.
        if (fWeapon)
        {
            int poisonSkill = target.GetPoisonSkill();
            if (poisonSkill > 0)
            {
                int level = IMulDiv(poisonSkill, s_armsLorePoison.Length, 100);
                level = Math.Clamp(level, 0, s_armsLorePoison.Length - 1);
                body += " " + ServerMessages.Get(s_armsLorePoison[level]);
            }
        }

        sink.SysMessage(body);
        return iSkillLevel;
    }

    private static readonly string[] s_armsLorePoison =
    {
        Msg.ArmslorePsn1, Msg.ArmslorePsn2, Msg.ArmslorePsn3, Msg.ArmslorePsn4, Msg.ArmslorePsn5,
        Msg.ArmslorePsn6, Msg.ArmslorePsn7, Msg.ArmslorePsn8, Msg.ArmslorePsn9, Msg.ArmslorePsn10,
    };

    // ------------------------------------------------------------ Forensics

    /// <summary>
    /// Source-X <c>CClient::OnSkill_Forensics</c>. Parses corpse state (sleeping
    /// ghost, carved, freshly-killed) and prints the matching carve/timer clause.
    /// </summary>
    public static int Forensics(IInfoSkillSink sink, Item? corpse, Character? killer, long secondsSinceDeath,
        bool isCorpseSleeping, bool isCarved, int iSkillLevel, bool fTest = false)
    {
        if (corpse == null || corpse.ItemType != ItemType.Corpse)
        {
            sink.SysMessage(ServerMessages.Get(Msg.ForensicsCorpse));
            return -SKTRIG_QTY;
        }
        if (!sink.Self.CanTouch(corpse))
        {
            sink.SysMessage(ServerMessages.Get(Msg.ForensicsReach));
            return -SKTRIG_QTY;
        }

        if (fTest)
            return sink.Random.Next(60);

        string? killerName = killer?.Name;

        if (isCorpseSleeping)
        {
            sink.SysMessage(ServerMessages.GetFormatted(
                Msg.ForensicsAlive, killerName ?? "It"));
            return 1;
        }

        string body;
        if (isCarved)
        {
            body = ServerMessages.GetFormatted(Msg.ForensicsCarve1, corpse.Name);
            body += killerName != null
                ? ServerMessages.GetFormatted(Msg.ForensicsCarve2, killerName)
                : ServerMessages.Get(Msg.ForensicsFailname);
        }
        else if (secondsSinceDeath > 0)
        {
            body = ServerMessages.GetFormatted(Msg.ForensicsTimer, corpse.Name, secondsSinceDeath);
            body += killerName != null
                ? ServerMessages.GetFormatted(Msg.ForensicsName, killerName)
                : ServerMessages.Get(Msg.ForensicsFailname);
        }
        else
        {
            // Corpse is fresh but no killer captured -- nothing to say, mirror empty path upstream.
            return iSkillLevel;
        }

        sink.SysMessage(body);
        return iSkillLevel;
    }

    // -------------------------------------------------------------- TasteID

    /// <summary>
    /// Source-X <c>CClient::OnSkill_TasteID</c>. Extracts the poison skill
    /// embedded in a potion / food / blade and maps it onto the shared
    /// ArmsLore poison table; falls back to TASTEID_RESULT for everything else.
    /// </summary>
    public static int TasteID(IInfoSkillSink sink, object target, int iSkillLevel, bool fTest = false)
    {
        if (target is not Item item)
        {
            // Null/Character target -> self vs. someone else branch.
            if (target is Character ch && ch == sink.Self)
                sink.SysMessage(ServerMessages.Get(Msg.TasteidSelf));
            else
                sink.SysMessage(ServerMessages.Get(Msg.TasteidChar));
            return -SKTRIG_QTY;
        }

        if (!sink.Self.CanTouch(item))
        {
            sink.SysMessage(ServerMessages.Get(Msg.TasteidUnable));
            return -SKTRIG_QTY;
        }

        int poisonLevel = 0;
        switch (item.ItemType)
        {
            case ItemType.Potion:
                // Source-X: only SPELL_Poison potions expose m_dwSkillQuality.
                if (item.TryGetTag("POTION_SPELL", out string? spell) &&
                    string.Equals(spell, "Poison", StringComparison.OrdinalIgnoreCase))
                {
                    poisonLevel = item.Quality;
                }
                break;
            case ItemType.Food:
            case ItemType.FoodRaw:
            case ItemType.Fruit:
            case ItemType.MeatRaw:
                poisonLevel = item.GetPoisonSkill() * 10;
                break;
            case ItemType.WeaponMaceSharp:
                poisonLevel = item.GetPoisonSkill() * 10;
                break;
            default:
                if (!fTest)
                    sink.SysMessage(ServerMessages.GetFormatted(Msg.TasteidResult, item.GetNameFull(false)));
                return 1;
        }

        if (fTest) return sink.Random.Next(60);

        if (poisonLevel > 0)
        {
            int level = IMulDiv(poisonLevel, s_armsLorePoison.Length, 1000);
            level = Math.Clamp(level, 0, s_armsLorePoison.Length - 1);
            sink.SysMessage(ServerMessages.Get(s_armsLorePoison[level]));
        }
        else
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.TasteidResult, item.GetNameFull(false)));
        }

        return iSkillLevel;
    }

    // ----------------------------------------------------------- primitives

    /// <summary>
    /// Source-X CObjBase::CanTouch stub -- range + visibility. SphereNet's
    /// Character today exposes range via <c>Position.GetDistSight</c>; we
    /// recreate the upstream "LOS + within 3 tiles" rule here.
    /// </summary>
    private static bool CanTouch(this Character self, Objects.ObjBase target)
    {
        if (self == target) return true;
        var (tx, ty, tz) = ResolvePosition(target);
        int dx = Math.Abs(self.Position.X - tx);
        int dy = Math.Abs(self.Position.Y - ty);
        int dist = Math.Max(dx, dy);
        return dist <= 3;
    }

    private static (int X, int Y, int Z) ResolvePosition(Objects.ObjBase obj)
    {
        return obj switch
        {
            Character c => (c.Position.X, c.Position.Y, c.Position.Z),
            Item i      => (i.Position.X, i.Position.Y, i.Position.Z),
            _           => (0, 0, 0),
        };
    }

    private static string StatBandDesc(short stat, string[] bandKeys)
    {
        int idx = Math.Clamp((stat - 1) / 10, 0, bandKeys.Length - 1);
        return ServerMessages.Get(bandKeys[idx]);
    }

    /// <summary>Source-X IMulDiv: (a * b) / c with 64-bit intermediate.</summary>
    private static int IMulDiv(int a, int b, int c)
    {
        if (c == 0) return 0;
        return (int)(((long)a * b) / c);
    }
}
