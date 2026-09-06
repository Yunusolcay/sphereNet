using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Skills.Information;

/// <summary>
/// Source-X parity for the "active" skills dispatched by CChar::Skill_*.
/// Every entry point folds the upstream stage machine
/// (SKTRIG_START → STROKE → SUCCESS/FAIL) into a single synchronous call:
///
///   1. Pre-check phase (target validity, tool present, range).
///   2. START emote / SysMessage.
///   3. Roll difficulty + <see cref="SkillEngine.UseQuick"/>.
///   4. SUCCESS or FAIL message + side effects (consume bandage, set Hidden,
///      push criminal flag, ...).
///
/// Side effects use only <see cref="IActiveSkillSink"/>, which keeps the
/// engine network-free and unit-testable. The engine itself is deliberately
/// stateless: it resolves the SUCCESS/FAIL outcome in one call and holds no
/// per-tick state. The Source-X per-tick STROKE loop is layered on top by the
/// client (GameClient.TickPendingSkill / TryScheduleActiveSkillDelay), which,
/// for skills with a DELAY, fires @SkillStroke on an interval and then invokes
/// these methods at completion. Ordering and interrupt behaviour of that loop
/// are locked by ActiveSkillStrokeMatrixTests / SkillDelayTests.
/// </summary>
public static class ActiveSkillEngine
{
    // ---------------------------------------------------------------- Hiding

    /// <summary>Source-X CChar::Skill_Hiding. Light source aborts; success sets STATF_HIDDEN.</summary>
    public static bool Hiding(IActiveSkillSink sink)
    {
        var ch = sink.Self;
        if (ch.IsInWarMode) return false;

        // Source-X iterates equipped items for CAN_I_LIGHT. SphereNet does not
        // model the can-flag yet; mirror the upstream gate via a tag the world
        // can set (e.g. equipped torch/lantern emits LIGHT_CARRIED=1).
        if (ch.TryGetTag("LIGHT_CARRIED", out string? lit) && lit == "1")
        {
            sink.SysMessage(ServerMessages.Get(Msg.HidingToolit));
            return false;
        }

        bool success = SkillEngine.UseQuick(ch, SkillType.Hiding, sink.Random.Next(70));
        if (success)
        {
            ch.SetStatFlag(StatFlag.Hidden);
            ch.ClearStatFlag(StatFlag.Invisible);
            sink.ObjectMessage(ch, ServerMessages.Get(Msg.HidingSuccess));
        }
        else
        {
            ch.ClearStatFlag(StatFlag.Hidden);
            ch.ClearStatFlag(StatFlag.Invisible);
            // Source-X Skill_Hiding FAIL just Reveal()s (CCharSkill.cpp:2481) —
            // DEFMSG_HIDING_STUMBLE belongs to walking into a hidden char and
            // takes that char's name; it was printed here with a raw %s.
        }
        return success;
    }

    // --------------------------------------------------------------- Stealth

    /// <summary>Source-X stealth: must already be hidden; success grants StepStealth walk budget.</summary>
    public static bool Stealth(IActiveSkillSink sink)
    {
        var ch = sink.Self;
        if (ch.IsInWarMode) return false;
        if (!ch.IsStatFlag(StatFlag.Hidden))
        {
            sink.SysMessage("You must be hidden to use stealth.");
            return false;
        }

        bool success = SkillEngine.UseQuick(ch, SkillType.Stealth, sink.Random.Next(60, 90));
        if (success)
        {
            ch.StepStealth = (short)Math.Clamp(Math.Max(1, ch.GetSkill(SkillType.Stealth) / 100), 1, 10);
            ch.SetStatFlag(StatFlag.Hidden);
            ch.ClearStatFlag(StatFlag.Invisible);
            sink.SysMessage("You begin to move quietly.");
        }
        else
        {
            ch.ClearHiddenState();
            sink.SysMessage("You fail to move quietly.");
        }
        return success;
    }

    // ---------------------------------------------------------- DetectHidden

    /// <summary>Source-X CChar::Skill_DetectHidden. Reveals hidden chars in radius based on skill diff.</summary>
    public static bool DetectHidden(IActiveSkillSink sink)
    {
        var ch = sink.Self;
        bool succeeded = SkillEngine.UseQuick(ch, SkillType.DetectingHidden, 10);
        if (!succeeded) return false;

        int detectSkill = SkillEngine.GetAdjustedSkill(ch, SkillType.DetectingHidden);
        int radius = Math.Max(0, SkillEngine.GetEffect(SkillType.DetectingHidden,
            detectSkill, detectSkill / 100));
        bool found = false;
        foreach (var nearby in sink.World.GetCharsInRange(ch.Position, radius))
        {
            if (nearby == ch || !nearby.IsStatFlag(StatFlag.Hidden | StatFlag.Invisible)) continue;
            int sourceRoll = detectSkill + sink.Random.Next(210) - 100;
            int targetRoll = SkillEngine.GetAdjustedSkill(nearby, SkillType.Hiding) +
                sink.Random.Next(210) - 100;
            if (sourceRoll >= targetRoll && nearby.ClearHiddenState())
            {
                // Source-X SysMessagef(DEFMSG_DETECTHIDDEN_SUCC, name) — CCharSkill.cpp:1746
                sink.SysMessage(ServerMessages.GetFormatted(Msg.DetecthiddenSucc, nearby.Name));
                found = true;
            }
        }
        return found;
    }

    // ----------------------------------------------------------- Meditation

    /// <summary>Source-X CChar::Skill_Meditation. STAT_INT cap rejects, success regens mana.</summary>
    public static bool Meditation(IActiveSkillSink sink)
    {
        var ch = sink.Self;
        if (ch.Mana >= ch.MaxMana)
        {
            sink.SysMessage(ServerMessages.Get(Msg.MeditationPeace1));
            return false;
        }

        // Source-X parity: meditation blocked by metal armor (chest/legs/gloves)
        if (IsWearingMeditationBlockingArmor(ch))
        {
            sink.SysMessage("You cannot focus with all that armor on.");
            return false;
        }

        sink.SysMessage(ServerMessages.Get(Msg.MeditationTry));

        bool success = SkillEngine.UseQuick(ch, SkillType.Meditation, sink.Random.Next(100));
        if (success)
        {
            ch.SetStatFlag(StatFlag.Meditation);
            if (!SkillEngine.HasFlag(SkillType.Meditation, SkillFlag.NoSfx))
                sink.Sound(0x0F9);
            sink.SysMessage(ServerMessages.Get(Msg.MeditationSuccess));
        }
        return success;
    }

    // ---------------------------------------------------------- SpiritSpeak

    /// <summary>Source-X CChar::Skill_SpiritSpeak. Success sets STATF_SPIRITSPEAK + sound 0x24A.</summary>
    public static bool SpiritSpeak(IActiveSkillSink sink)
    {
        var ch = sink.Self;
        if (ch.IsStatFlag(StatFlag.SpiritSpeak)) return false;

        bool success = SkillEngine.UseQuick(ch, SkillType.SpiritSpeak, sink.Random.Next(90));
        if (success)
        {
            if (!SkillEngine.HasFlag(SkillType.SpiritSpeak, SkillFlag.NoSfx))
                sink.Sound(0x24A);
            sink.SysMessage(ServerMessages.Get(Msg.SpiritspeakSuccess));
            ch.SetStatFlag(StatFlag.SpiritSpeak);
            ch.SetTag("SPIRITSPEAK_UNTIL", // 4*60 tenths = 24s (Source-X CCharSkill.cpp:2644)
                (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 24_000).ToString());
        }
        return success;
    }

    // -------------------------------------------------------------- Begging

    /// <summary>Source-X CChar::Skill_Begging. Targets human NPC; success grants 1-10 gold.</summary>
    public static bool Begging(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        if (target == null || target.IsDeleted || target.IsDead || target.IsPlayer ||
            target.NpcBrain != NpcBrainType.Human ||
            !CanReachPoint(ch, target.Position, sink.World,
                SkillEngine.GetUseRange(SkillType.Begging, 3)))
            return false;

        // Source-X SysMessagef(DEFMSG_BEGGING_START, pChar->GetName()) — CCharSkill.cpp:2944
        sink.SysMessage(ServerMessages.GetFormatted(Msg.BeggingStart, target.Name));
        bool success = SkillEngine.UseQuick(ch, SkillType.Begging, 40);
        if (success)
        {
            if (ch.Backpack == null)
                return success;
            int amount = sink.Random.Next(1, 11);
            var gold = sink.World.CreateItem();
            gold.BaseId = 0x0EED; gold.Name = "Gold";
            gold.ItemType = ItemType.Gold;
            gold.Amount = (ushort)amount;
            sink.DeliverItem(gold);
        }
        return success;
    }

    // ------------------------------------------------------------- Stealing

    /// <summary>Source-X CChar::Skill_Stealing. Pick item from container, MakeCriminal on noticed fail.</summary>
    public static bool Stealing(IActiveSkillSink sink, Item? target)
    {
        var ch = sink.Self;
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingNothing));
            return false;
        }
        Character? owner = ResolveItemOwner(target, sink.World);
        if (!CanReachItem(ch, target, sink.World, SkillType.Stealing, 2))
        {
            sink.SysMessage("That is too far away.");
            return false;
        }
        if (target.GetWeight() > Math.Max(1, ch.GetSkill(SkillType.Stealing) / 10))
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingHeavy));
            return false;
        }

        if (target.IsAttr(ObjAttributes.Blessed) || target.IsAttr(ObjAttributes.Blessed2) ||
            target.IsAttr(ObjAttributes.Newbie) || target.IsAttr(ObjAttributes.Nodropt))
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingNothing));
            return false;
        }

        if (owner == ch)
            return false;
        if (owner != null)
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.StealingPickpocket, owner.Name));
        }

        bool success = SkillEngine.UseQuick(ch, SkillType.Stealing, sink.Random.Next(60));

        // No backpack = nowhere to stash the loot, so the theft can't succeed.
        // Without this the item silently vanished AND the thief could still be
        // flagged criminal for a "successful" steal that moved nothing.
        if (success && ch.Backpack == null)
            success = false;

        if (success)
        {
            Item? sourceContainer = null;
            if (target.ContainedIn.IsValid)
            {
                sourceContainer = sink.World.FindItem(target.ContainedIn);
                sourceContainer?.RemoveItem(target);
            }

            var actual = ch.Backpack!.TryAddItemWithStack(target);
            if (actual == null)
            {
                if (sourceContainer?.TryAddItem(target) != true)
                    sink.World.PlaceItemWithDecay(target, owner?.Position ?? ch.Position);
                success = false;
            }
            else if (actual != target)
            {
                sink.World.RemoveItem(target);
            }
        }

        // Source-X CChar::Skill_Stealing: every nearby witness who wins the
        // perception contest notices the theft — whether or not it succeeded —
        // and remembers it (personal grey via MEMORY_SAWCRIME); a guarded-area
        // guard flags the thief globally. A theft no one sees has no consequence,
        // replacing the old blind 50% MakeCriminal coin flip.
        if (CrimeWitnessService.CheckCrimeSeen(sink.World, ch, owner, SkillType.Stealing, sink.Random)
            && owner != null)
            sink.SysMessage(ServerMessages.GetFormatted(Msg.StealingMark, owner.Name));

        return success;
    }

    // -------------------------------------------------------------- Snooping

    /// <summary>Source-X CChar::Skill_Snooping. Always-on container; fail emits SNOOPING_FAILED + optional crim.</summary>
    public static bool Snooping(IActiveSkillSink sink, Item? container)
    {
        var ch = sink.Self;
        if (container == null || container.ItemType is not (ItemType.Container or ItemType.ContainerLocked))
        {
            sink.SysMessage(ServerMessages.Get(Msg.SnoopingCant));
            return false;
        }

        var ownerChar = ResolveItemOwner(container, sink.World);
        if (!CanReachItem(ch, container, sink.World, SkillType.Snooping, 2))
        {
            sink.SysMessage("That is too far away.");
            return false;
        }

        sink.SysMessage(ServerMessages.Get(Msg.SnoopingAttempting));
        bool success = SkillEngine.UseQuick(ch, SkillType.Snooping, sink.Random.Next(50));
        if (!success)
            sink.SysMessage(ServerMessages.Get(Msg.SnoopingFailed));
        else
            sink.OpenContainer(container);

        // Source-X CChar::Skill_Snooping: nearby witnesses may notice the snoop
        // (perception contest + the snoop-criminal chance). @SeeSnoop fires, the
        // witness remembers it (personal grey), and a guarded-area guard flags the
        // snooper. Gated by the SnoopCriminal config toggle.
        if (Character.SnoopCriminalEnabled)
            CrimeWitnessService.CheckCrimeSeen(sink.World, ch, ownerChar, SkillType.Snooping,
                sink.Random, isSnoop: true);

        return success;
    }

    // ---------------------------------------------------------- Lockpicking

    /// <summary>Source-X CChar::Skill_Lockpicking. Requires lockpick in pack; success unlocks container/door.</summary>
    public static bool Lockpicking(IActiveSkillSink sink, Item? lockedTarget)
    {
        var ch = sink.Self;
        if (lockedTarget == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.LockpickingReach));
            return false;
        }
        if (lockedTarget.ItemType is not (ItemType.ContainerLocked or ItemType.DoorLocked))
        {
            sink.SysMessage(ServerMessages.Get(Msg.LockpickingWitem));
            return false;
        }
        if (!CanReachItem(ch, lockedTarget, sink.World, SkillType.Lockpicking, 2))
        {
            sink.SysMessage("That is too far away.");
            return false;
        }

        var pick = sink.FindBackpackItem(ItemType.Lockpick);
        if (pick == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.LockpickingNopick));
            return false;
        }

        bool success = SkillEngine.UseQuick(ch, SkillType.Lockpicking, sink.Random.Next(60));
        if (success)
        {
            // Convert locked variant -> open variant.
            lockedTarget.ItemType = lockedTarget.ItemType == ItemType.ContainerLocked
                ? ItemType.Container
                : ItemType.Door;
            sink.Sound(0x241);
        }
        else if (sink.Random.Next(3) == 0)
        {
            sink.ConsumeAmount(pick); // ~33% to break the pick on failure (Source-X).
        }
        return success;
    }

    // ----------------------------------------------------------- RemoveTrap

    /// <summary>Source-X CChar::Skill_RemoveTrap. Targets trap item; success disarms.</summary>
    public static bool RemoveTrap(IActiveSkillSink sink, Item? trap)
    {
        var ch = sink.Self;
        if (trap == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RemovetrapsReach));
            return false;
        }
        if (trap.ItemType is not (ItemType.Trap or ItemType.TrapActive))
        {
            sink.SysMessage(ServerMessages.Get(Msg.RemovetrapsWitem));
            return false;
        }
        if (!CanReachItem(ch, trap, sink.World, SkillType.RemoveTrap, 2))
        {
            sink.SysMessage("That is too far away.");
            return false;
        }

        // Source-X Skill_RemoveTrap: difficulty = rand(95) (CCharSkill.cpp:2913).
        bool success = SkillEngine.UseQuick(ch, SkillType.RemoveTrap, sink.Random.Next(95));
        if (success)
        {
            trap.ItemType = ItemType.Trap; // disarm: clear active variant.
        }
        else if (trap.ItemType == ItemType.TrapActive)
        {
            // Source-X: a botched disarm springs the trap (Use_Item → Use_Trap).
            // Damage is the trap's OWN damage field, default 2 (CItem.cpp:5507)
            // — never an invented 5-19 roll.
            int trapDmg = 2;
            if (trap.TryGetTag("TRAP_DAMAGE", out string? tdStr) &&
                int.TryParse(tdStr, out int td) && td > 0)
                trapDmg = td;
            else if (trap.MoreP.Z > 0)
                trapDmg = trap.MoreP.Z;
            ch.Hits = (short)Math.Max(0, ch.Hits - trapDmg);
            sink.SysMessage(ServerMessages.Get("removetraps_fail"));
            if (ch.Hits <= 0 && !ch.IsDead)
            {
                if (Character.OnLifecycleKill != null) Character.OnLifecycleKill(ch, null);
                else ch.Kill();
            }
        }
        return success;
    }

    // -------------------------------------------------------------- Healing

    /// <summary>
    /// Source-X CChar::Skill_Healing. Requires bandage; checks reach; healthy/dead/poisoned
    /// branches emit specific DEFMSG_HEALING_* messages and consume the bandage on fail
    /// or success (per upstream).
    /// </summary>
    public static bool Healing(IActiveSkillSink sink, Character? target,
        SkillType healingSkill = SkillType.Healing, Item? selectedCorpse = null)
    {
        var ch = sink.Self;

        // "No target given, treat me" and "the target I picked is gone" are different
        // answers. A corpse whose owner cannot be resolved - already resurrected, or
        // deleted with its NPC - used to fall through to the self-heal default, so
        // aiming a bandage at a stale corpse quietly bandaged the healer and spent the
        // bandage doing it. Source-X stops with HEALING_BEYOND instead
        // (CCharSkill.cpp:2769), and IsCorpseResurrectable refuses an owner that is no
        // longer dead (CItemCorpse.cpp:37).
        if (target == null && selectedCorpse?.ItemType == ItemType.Corpse)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingBeyond));
            return false;
        }

        target ??= ch;

        bool veterinary = healingSkill == SkillType.Veterinary;
        if (veterinary && (target.IsPlayer || target.NpcBrain is not
            (NpcBrainType.Animal or NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon)))
        {
            sink.SysMessage("You can only use veterinary care on animals.");
            return false;
        }

        var bandage = sink.FindBackpackItem(ItemType.Bandage);
        if (bandage == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingNoaids));
            return false;
        }

        // Source-X gathers every corpse-side condition into IsCorpseResurrectable and
        // runs it BEFORE the bandage is spent or the skill rolled (Skill_Healing,
        // CCharSkill.cpp:2796). SphereNet had only the container half of it.
        if (selectedCorpse?.ItemType == ItemType.Corpse &&
            !IsCorpseResurrectable(sink, ch, target, selectedCorpse))
            return false;

        Point3D healAnchor = selectedCorpse?.ItemType == ItemType.Corpse
            ? selectedCorpse.Position
            : target.Position;
        if (!CanReachPoint(ch, healAnchor, sink.World,
                SkillEngine.GetUseRange(healingSkill, 2)))
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.HealingToofar, target.Name));
            return false;
        }

        if (!target.IsStatFlag(StatFlag.Poisoned | StatFlag.Dead) && target.Hits >= target.MaxHits)
        {
            sink.SysMessage(target == ch
                ? ServerMessages.Get(Msg.HealingHealthy)
                : ServerMessages.GetFormatted(Msg.HealingNoneed, target.Name));
            return false;
        }

        // START emote.
        sink.Emote(target == ch
            ? ServerMessages.Get(Msg.HealingSelf)
            : ServerMessages.GetFormatted(Msg.HealingTo, target.Name));

        // Difficulty: dead = 85+25, poisoned = 50+50, normal = 0..80 —
        // verbatim Source-X Skill_Healing (CCharSkill.cpp:2836-2840).
        int diff = target.IsStatFlag(StatFlag.Dead) ? 85 + sink.Random.Next(25)
                 : target.IsStatFlag(StatFlag.Poisoned) ? 50 + sink.Random.Next(50)
                 : sink.Random.Next(80);
        bool success = SkillEngine.UseQuick(ch, healingSkill, diff);

        if (!SkillEngine.HasFlag(healingSkill, SkillFlag.NoAnim))
            sink.Animation((ushort)Core.Enums.AnimationType.Bow);
        if (!SkillEngine.HasFlag(healingSkill, SkillFlag.NoSfx))
            sink.Sound(0x0057);
        sink.ConsumeAmount(bandage); // Source-X consumes on fail too.
        // Used bandages become bloody bandages (Source-X parity).
        var bloody = sink.World.CreateItem();
        bloody.BaseId = 0x0E20; // bloody bandage
        bloody.Name = "bloodied bandage";
        sink.DeliverItem(bloody);
        if (!success)
            return false;

        ch.FlagForHelpingCriminalIfNeeded(target);

        if (target.IsStatFlag(StatFlag.Poisoned))
        {
            int skillLvl = ch.GetSkill(healingSkill);
            if (sink.Random.Next(1000) < skillLvl)
            {
                target.CurePoison();
                sink.SysMessage(ServerMessages.GetFormatted(Msg.HealingCure1,
                    target == ch ? ServerMessages.Get(Msg.HealingYourself) : target.Name));
            }
            else
            {
                sink.SysMessage(ServerMessages.Get(Msg.HealingCure4));
                return false;
            }
            return true;
        }

        if (target.IsStatFlag(StatFlag.Dead))
        {
            // Source-X asks for STATF_INSUBSTANTIAL, which death sets only on a PLAYER
            // who was not already in war mode - "manifest war mode for ghosts"
            // (CCharAct.cpp:4468) - and never on an NPC. SphereNet has no ghost
            // insubstantial flag and uses war mode as the manifest signal for players,
            // so that half stands; but holding a dead NPC to it meant a bonded pet
            // that died out of combat could not be revived by Veterinary at all, and
            // no part of a pet's life makes it die in war mode.
            if (target.IsPlayer && !target.IsInWarMode)
            {
                sink.SysMessage(ServerMessages.Get(Msg.HealingResManifest));
                return false;
            }

            var corpse = selectedCorpse?.ItemType == ItemType.Corpse
                ? selectedCorpse
                : FindCorpseFor(sink.World, target);
            if (corpse != null)
            {
                var corpsePos = new Point3D(corpse.X, corpse.Y, corpse.Z, corpse.MapIndex);
                if (!CanReachPoint(ch, corpsePos, sink.World, 3, requireLos: false))
                {
                    sink.SysMessage(ServerMessages.Get(Msg.HealingResToofar));
                    return false;
                }
                if (!sink.World.CanSeeLOS(ch.Position, corpsePos))
                {
                    sink.SysMessage(ServerMessages.Get(Msg.HealingResLos));
                    return false;
                }
            }

            sink.ResurrectTarget(target);
            sink.SysMessage(ServerMessages.GetFormatted(Msg.HealingRes, target.Name));
            return true;
        }

        // Anatomy contributes to the amount healed (Source-X heal formula).
        int heal = veterinary
            ? ch.GetSkill(SkillType.Veterinary) / 40 + ch.GetSkill(SkillType.AnimalLore) / 80 + 3
            : ch.GetSkill(SkillType.Healing) / 40 + ch.GetSkill(SkillType.Anatomy) / 80 + 3;
        target.Hits = (short)Math.Min(target.MaxHits, target.Hits + heal);
        return true;
    }

    /// <summary>The corpse-side half of a bandage resurrection, mirroring Source-X
    /// CItemCorpse::IsCorpseResurrectable (CItemCorpse.cpp:28). These are checks ON TOP
    /// of the healer's own reach to the corpse, not a replacement for it - the healer
    /// can stand over a corpse whose ghost has wandered off, and used to be able to
    /// raise it from anywhere on the map, or from another one.</summary>
    private static bool IsCorpseResurrectable(IActiveSkillSink sink, Character healer,
        Character ghost, Item corpse)
    {
        // A corpse inside a container cannot be resurrected over; it has to be a
        // top-level ground object (:62).
        if (corpse.ContainedIn.IsValid)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingCorpseg));
            return false;
        }

        var corpsePos = corpse.Position;

        if (ghost.IsStatFlag(StatFlag.Dead))
        {
            // The ghost has to be able to see its own corpse and be within two tiles
            // of it (:48, :55) - conditions on the GHOST, which nothing here checked.
            if (ghost.MapIndex != corpsePos.Map ||
                ghost.Position.GetDistanceTo(corpsePos) > 2)
            {
                sink.SysMessage(ServerMessages.Get(Msg.HealingResToofar));
                return false;
            }
            if (!sink.World.CanSeeLOS(ghost.Position, corpsePos))
            {
                sink.SysMessage(ServerMessages.Get(Msg.HealingResLos));
                return false;
            }
        }

        // The antimagic flags of the CORPSE's region, refused before anything is spent
        // (:73). Character.Resurrect only ever looked at NoMagic, and only at the very
        // end - by which point the bandage was gone and the skill had reported success.
        var region = sink.World.FindRegion(corpsePos);
        if (region != null &&
            (region.IsFlag(RegionFlag.NoMagic) || region.IsFlag(RegionFlag.Recall) ||
             region.IsFlag(RegionFlag.NoTeleport)))
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingAm));
            return false;
        }

        return true;
    }

    // --------------------------------------------------------------- Taming

    /// <summary>Source-X CChar::Skill_Taming. Difficulty grows with target wildness.</summary>
    public static bool Taming(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingReach));
            return false;
        }
        if (target.NpcBrain is NpcBrainType.Human or NpcBrainType.Guard or
            NpcBrainType.Vendor or NpcBrainType.Banker or NpcBrainType.Stable or
            NpcBrainType.Healer)
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingCant));
            return false;
        }
        if (target.IsDead)
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingCant));
            return false;
        }
        if (target.IsStatFlag(StatFlag.Pet))
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.TamingTame, target.Name));
            return false;
        }
        if (!CanReachPoint(ch, target.Position, sink.World,
                SkillEngine.GetUseRange(SkillType.Taming, 6)))
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingLos));
            return false;
        }

        // Source-X cycles through TAMING_1..4 emotes during the stage loop.
        string[] tries = { Msg.Taming1, Msg.Taming2, Msg.Taming3, Msg.Taming4 };
        sink.Emote(ServerMessages.GetFormatted(tries[sink.Random.Next(tries.Length)], target.Name));

        // Difficulty approximated from the creature's hit points. CheckSuccess
        // expects a 0-100 difficulty (it scales x10 internally vs the 0-1000
        // skill value), so map HP onto 0-100 — using the raw HP here made high-HP
        // creatures (dragons/bosses) mathematically un-tameable.
        int tameRequirement = target.GetSkill(SkillType.Taming);
        int diff = tameRequirement > 0
            ? Math.Clamp(tameRequirement / 10, 1, 100)
            : Math.Clamp(target.MaxHits / 10, 1, 100);
        bool success = SkillEngine.UseQuick(ch, SkillType.Taming, diff);
        if (success)
        {
            success = target.TryAssignOwnership(ch, ch, summoned: false, enforceFollowerCap: true);
        }
        if (success)
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.TamingSuccess, target.Name));
            sink.SysMessage(ServerMessages.GetFormatted(Msg.TamingYmaster, target.Name));
        }
        return success;
    }

    // -------------------------------------------------------------- Herding

    /// <summary>Source-X CChar::Skill_Herding. Targets animal then a point; pets are immune.</summary>
    public static bool Herding(IActiveSkillSink sink, Character? animal, Point3D? destination)
    {
        var ch = sink.Self;
        if (animal == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HerdingLtarg));
            return false;
        }
        if (animal.IsStatFlag(StatFlag.Pet) || animal.NpcBrain != NpcBrainType.Animal)
        {
            // Source-X SysMessagef("%s %s", name, DEFMSG_HERDING_PLAYER).
            sink.SysMessage($"{animal.Name} {ServerMessages.Get(Msg.HerdingPlayer)}");
            return false;
        }
        if (!CanReachPoint(ch, animal.Position, sink.World,
                SkillEngine.GetUseRange(SkillType.Herding, 8)))
        {
            sink.SysMessage("That creature is too far away.");
            return false;
        }
        var crook = sink.FindBackpackItem(ItemType.WeaponMaceCrook);
        if (crook == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HerdingNocrook));
            return false;
        }

        if (!destination.HasValue || destination.Value.Map != ch.MapIndex ||
            !CanReachPoint(ch, destination.Value, sink.World, 12))
        {
            sink.SysMessage("You cannot herd the animal there.");
            return false;
        }

        DamageGatherTool(sink, crook);
        int diff = animal.Int / 2 + sink.Random.Next(Math.Max(1, animal.Int / 2));
        bool success = SkillEngine.UseQuick(ch, SkillType.Herding, diff);
        if (success)
        {
            sink.MoveCharacter(animal, destination.Value);
            animal.SetTag("HERD_MASTER", ch.Uid.Value.ToString());
            animal.SetTag("HERD_MASTER_UUID", ch.Uuid.ToString("D"));
            sink.ObjectMessage(animal, ServerMessages.Get(Msg.HerdingSuccess));
        }
        return success;
    }

    // ------------------------------------------------------------ Poisoning

    /// <summary>Source-X CChar::Skill_Poisoning. Apply potion poison level to target weapon.</summary>
    public static bool Poisoning(IActiveSkillSink sink, Item? weapon, Item? selectedPotion = null)
    {
        var ch = sink.Self;
        if (weapon == null || !IsBladeOrFood(weapon.ItemType))
        {
            sink.SysMessage(ServerMessages.Get(Msg.PoisoningWitem));
            return false;
        }
        var potion = selectedPotion ?? sink.FindBackpackItem(ItemType.Potion);
        if (potion == null ||
            !CanReachItem(ch, potion, sink.World, SkillType.Poisoning, 2,
                requirePossession: true) ||
            !potion.TryGetTag("POTION_SPELL", out string? spell) ||
            !string.Equals(spell, "Poison", StringComparison.OrdinalIgnoreCase))
        {
            sink.SysMessage(ServerMessages.Get(Msg.PoisoningSelect1));
            return false;
        }
        if (!CanReachItem(ch, weapon, sink.World, SkillType.Poisoning, 2,
                requirePossession: true))
        {
            sink.SysMessage("You must have that item in your possession.");
            return false;
        }

        int diff = potion.Quality / 2;
        bool success = SkillEngine.UseQuick(ch, SkillType.Poisoning, diff);
        if (success)
        {
            weapon.SetTag("POISON_SKILL", potion.Quality.ToString());
            sink.ConsumeAmount(potion);
            sink.SysMessage(ServerMessages.Get(Msg.PoisoningSuccess));
        }
        return success;
    }

    // ------------------------------------------------------------- Tracking

    /// <summary>Source-X CChar::Skill_Tracking (post-menu phase). Counts entities by category in radius.</summary>
    public static bool Tracking(IActiveSkillSink sink, TrackingCategory category)
    {
        var ch = sink.Self;
        var targets = FindTrackingTargets(sink, category);
        int count = targets.Count;

        bool success = SkillEngine.UseQuick(ch, SkillType.Tracking, sink.Random.Next(50));
        if (!success || count == 0)
        {
            sink.SysMessage(ServerMessages.Get(category switch
            {
                TrackingCategory.Animals => Msg.TrackingFailAnimal,
                TrackingCategory.Monsters => Msg.TrackingFailMonster,
                _ => Msg.TrackingFailHuman,
            }));
            return false;
        }

        // Source-X reports a band: 0/1/2/3/4/disc.
        string key = count switch
        {
            1     => Msg.TrackingResult1,
            2     => Msg.TrackingResult2,
            3     => Msg.TrackingResult3,
            >= 4  => Msg.TrackingResult4,
            _     => Msg.TrackingResult0,
        };
        sink.SysMessage(ServerMessages.GetFormatted(key, count));
        return true;
    }

    public static List<Character> FindTrackingTargets(IActiveSkillSink sink,
        TrackingCategory category)
    {
        var ch = sink.Self;
        int range = SkillEngine.GetUseRange(SkillType.Tracking,
            10 + SkillEngine.GetAdjustedSkill(ch, SkillType.Tracking) / 10);
        return sink.World.GetCharsInRange(ch.Position, range)
            .Where(c => c != ch && !c.IsDeleted && !c.IsDead && MatchesCategory(c, category))
            .OrderBy(c => ch.Position.GetDistanceTo(c.Position))
            .ThenBy(c => c.Uid.Value)
            .ToList();
    }

    public enum TrackingCategory { Animals, Monsters, Humans, Players }

    // ----------------------------------------------------------- primitives

    private static bool CanReachPoint(Character ch, Point3D point, GameWorld world,
        int range, bool requireLos = true)
    {
        if (point.Map != ch.MapIndex || ch.Position.GetDistanceTo(point) > range)
            return false;
        return !requireLos || world.CanSeeLOS(ch.Position, point);
    }

    private static bool TryResolveItemTop(Item item, GameWorld world,
        out Point3D position, out Character? owner)
    {
        owner = null;
        var seen = new HashSet<uint>();
        for (int depth = 0; depth < 32; depth++)
        {
            if (!seen.Add(item.Uid.Value))
                break;
            if (!item.ContainedIn.IsValid)
            {
                position = item.Position;
                return true;
            }

            var holder = world.FindObject(item.ContainedIn);
            if (holder is Character ch)
            {
                owner = ch;
                position = ch.Position;
                return true;
            }
            if (holder is Item parent)
            {
                item = parent;
                continue;
            }
            break;
        }

        position = default;
        return false;
    }

    private static bool CanReachItem(Character ch, Item item, GameWorld world,
        SkillType skill, int fallbackRange, bool requirePossession = false)
    {
        if (item.IsDeleted || !TryResolveItemTop(item, world, out Point3D top, out Character? owner))
            return false;
        if (requirePossession && owner != ch)
            return false;
        return CanReachPoint(ch, top, world, SkillEngine.GetUseRange(skill, fallbackRange));
    }

    private static Item? FindCorpseFor(GameWorld world, Character ghost)
    {
        foreach (var item in world.GetItemsInRange(ghost.Position, 20))
        {
            if (item.ItemType != Core.Enums.ItemType.Corpse) continue;
            if (item.TryGetTag("OWNER_UUID", out string? uuidStr) &&
                Guid.TryParse(uuidStr, out Guid uuid) && uuid == ghost.Uuid)
                return item;
            if (item.TryGetTag("OWNER_UID", out string? uidStr) &&
                uint.TryParse(uidStr, out uint ownerUid) && ownerUid == ghost.Uid.Value)
                return item;
        }
        return null;
    }

    private static int GetDistance(Point3D a, Point3D b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static int GetWeight(this Item it)
    {
        return Math.Max(1, it.TotalWeight);
    }

    private static bool IsBladeOrFood(ItemType t) => t is
        ItemType.WeaponMaceSharp or ItemType.WeaponSword or ItemType.WeaponFence or
        ItemType.WeaponAxe or ItemType.Food or ItemType.MeatRaw or ItemType.Fruit;

    private static bool IsWearingMeditationBlockingArmor(Character ch)
    {
        Layer[] armorLayers = [Layer.Chest, Layer.Legs, Layer.Gloves, Layer.Helm];
        foreach (var layer in armorLayers)
        {
            var armor = ch.GetEquippedItem(layer);
            if (armor != null && armor.ItemType is ItemType.Armor or ItemType.ArmorChain or ItemType.ArmorRing)
                return true;
        }
        return false;
    }

    private static Character? ResolveItemOwner(Item it, World.GameWorld world, int maxDepth = 16)
    {
        for (int i = 0; i < maxDepth; i++)
        {
            if (!it.ContainedIn.IsValid) return null;
            var holder = world.FindObject(it.ContainedIn);
            if (holder is Character c) return c;
            if (holder is Item parent) { it = parent; continue; }
            return null;
        }
        return null;
    }

    // --------------------------------------------------------------- Mining

    private const int FallbackResAmount = 20;
    private const int FallbackRegenMs = 36_000_000;

    public static bool Mining(IActiveSkillSink sink, Point3D target, GatheringEngine? gatheringEngine, World.GameWorld world)
    {
        var ch = sink.Self;

        int miningRange = SkillEngine.GetUseRange(SkillType.Mining, 2);
        if (!CanReachPoint(ch, target, world, miningRange) ||
            (ch.Position.GetDistanceTo(target) == 0 &&
             !SkillEngine.HasFlag(SkillType.Mining, SkillFlag.NoMinDist)))
        {
            sink.SysMessage(ServerMessages.Get(Msg.MiningReach));
            return false;
        }

        // Source-X REGION_FLAG_NOMINING: mining is banned in this region.
        var miningRegion = world.FindRegion(target);
        if (miningRegion != null && miningRegion.IsFlag(RegionFlag.NoMining))
        {
            sink.SysMessage(ServerMessages.Get(Msg.Mining4));
            return false;
        }

        if (!IsMinableTile(world, target))
        {
            sink.SysMessage(ServerMessages.Get(Msg.Mining4));
            return false;
        }

        // Source-X requires a pickaxe to mine; it wears out with use.
        var pickaxe = FindGatherTool(sink, ItemType.WeaponMacePick);
        if (pickaxe == null)
        {
            sink.SysMessage("You need a pickaxe to mine.");
            return false;
        }
        BroadcastAnimation(ch, SkillType.Mining, (ushort)AnimationType.Attack1HBash, 0x0125);
        DamageGatherTool(sink, pickaxe);

        if (gatheringEngine != null)
        {
            var result = gatheringEngine.TryGatherForSink(ch, SkillType.Mining, target);
            if (result.Handled)
            {
                if (result.Depleted)
                {
                    sink.SysMessage(ServerMessages.Get(Msg.Mining1));
                    return false;
                }
                if (result.Success && result.Item != null)
                {
                    sink.SysMessage("You dig some ore and put it in your backpack.");
                    sink.DeliverItem(result.Item);
                    return true;
                }
                sink.SysMessage(ServerMessages.Get(Msg.Mining3));
                return false;
            }
        }

        var marker = FindFallbackMarker(world, target, "Mining");
        if (marker != null && marker.Amount <= 0)
        {
            sink.SysMessage(ServerMessages.Get(Msg.Mining1));
            return false;
        }

        bool success = SkillEngine.UseQuick(ch, SkillType.Mining, 50);
        if (success)
        {
            int amount = sink.Random.Next(1, 3);
            ConsumeFallbackMarker(world, target, "Mining", amount, ref marker);
            var ore = world.CreateItem();
            ore.BaseId = 0x19B9;
            ore.Name = "iron ore";
            ore.Amount = (ushort)amount;
            sink.SysMessage("You dig some ore and put it in your backpack.");
            sink.DeliverItem(ore);
        }
        else
        {
            sink.SysMessage(ServerMessages.Get(Msg.Mining3));
        }
        return success;
    }

    private static bool IsMinableTile(World.GameWorld world, Point3D target)
    {
        var mapData = world.MapData;
        if (mapData == null) return true;

        // Check land tile name (rock, cave, mountain, ore)
        var terrain = mapData.GetTerrainTile(target.Map, target.X, target.Y);
        var landData = mapData.GetLandTileData(terrain.TileId);
        if (!string.IsNullOrEmpty(landData.Name))
        {
            string name = landData.Name;
            if (name.Contains("rock", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("cave", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("mountain", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ore", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check statics at target for minable rock walls
        var statics = mapData.GetStatics(target.Map, target.X, target.Y);
        foreach (var st in statics)
        {
            var itemData = mapData.GetItemTileData(st.TileId);
            if (string.IsNullOrEmpty(itemData.Name)) continue;
            if ((itemData.IsWall || itemData.IsImpassable) &&
                (itemData.Name.Contains("rock", StringComparison.OrdinalIgnoreCase) ||
                 itemData.Name.Contains("cave", StringComparison.OrdinalIgnoreCase) ||
                 itemData.Name.Contains("mountain", StringComparison.OrdinalIgnoreCase) ||
                 itemData.Name.Contains("ore", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>Find a gathering tool of the given type in the actor's hands or
    /// backpack (Source-X requires the tool to be present to mine/chop/fish).</summary>
    private static Item? FindGatherTool(IActiveSkillSink sink, ItemType toolType)
    {
        var ch = sink.Self;
        var hand = ch.GetEquippedItem(Layer.OneHanded) ?? ch.GetEquippedItem(Layer.TwoHanded);
        if (hand != null && !hand.IsDeleted && hand.ItemType == toolType)
            return hand;
        return sink.FindBackpackItem(toolType);
    }

    /// <summary>Wear a gathering tool on use. Decrements UsesRemaining when the
    /// item tracks it; otherwise Source-X damages the tool's HITPOINTS
    /// (CCharSkill.cpp:2447 OnTakeDamage(1)) — a tool whose def declares no
    /// hitpoints never wears. The old invented 1/50 random break made every
    /// untracked tool vanish after ~50 uses.</summary>
    private static void DamageGatherTool(IActiveSkillSink sink, Item tool)
    {
        if (tool.UsesRemaining > 0)
        {
            tool.UsesRemaining--;
            if (tool.UsesRemaining == 0)
                sink.ConsumeAmount(tool);
        }
        else
        {
            SphereNet.Game.Combat.CombatEngine.ApplyDirectItemDamage(tool, 1);
        }
    }

    /// <summary>True when the target tile is water (Source-X fishing terrain
    /// check). Uses the tiledata wet flag, which is reliable, not a name match.
    /// Permissive when no map data is loaded (bare test setups).</summary>
    private static bool IsWaterTile(World.GameWorld world, Point3D target)
    {
        var mapData = world.MapData;
        if (mapData == null) return true;

        var terrain = mapData.GetTerrainTile(target.Map, target.X, target.Y);
        if (mapData.GetLandTileData(terrain.TileId).IsWet) return true;

        // Some deep-water is rendered through statics rather than the land tile.
        foreach (var st in mapData.GetStatics(target.Map, target.X, target.Y))
        {
            if (mapData.GetItemTileData(st.TileId).IsWet) return true;
        }
        return false;
    }

    /// <summary>True when the target tile carries a tree (Source-X lumberjacking
    /// terrain check). Trees are statics named tree/log; some forests also name
    /// the land tile. Mirrors the IsMinableTile name-matching pattern. Permissive
    /// when no map data is loaded (bare test setups).</summary>
    private static bool IsTreeTile(World.GameWorld world, Point3D target)
    {
        var mapData = world.MapData;
        if (mapData == null) return true;

        foreach (var st in mapData.GetStatics(target.Map, target.X, target.Y))
        {
            var name = mapData.GetItemTileData(st.TileId).Name;
            if (!string.IsNullOrEmpty(name) &&
                (name.Contains("tree", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("log", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        var terrain = mapData.GetTerrainTile(target.Map, target.X, target.Y);
        var land = mapData.GetLandTileData(terrain.TileId).Name;
        return !string.IsNullOrEmpty(land) &&
            (land.Contains("forest", StringComparison.OrdinalIgnoreCase) ||
             land.Contains("tree", StringComparison.OrdinalIgnoreCase));
    }

    // -------------------------------------------------------------- Fishing

    public static bool Fishing(IActiveSkillSink sink, Point3D target, GatheringEngine? gatheringEngine, World.GameWorld world)
    {
        var ch = sink.Self;

        int fishingRange = SkillEngine.GetUseRange(SkillType.Fishing, 6);
        if (!CanReachPoint(ch, target, world, fishingRange) ||
            (ch.Position.GetDistanceTo(target) == 0 &&
             !SkillEngine.HasFlag(SkillType.Fishing, SkillFlag.NoMinDist)))
        {
            sink.SysMessage(ServerMessages.Get(Msg.FishingReach));
            return false;
        }

        // Source-X CWorldMap: fishing requires a water tile at the target.
        if (!IsWaterTile(world, target))
        {
            sink.SysMessage("You can't fish there.");
            return false;
        }

        // Source-X requires a fishing pole; it wears out with use.
        var pole = FindGatherTool(sink, ItemType.FishPole);
        if (pole == null)
        {
            sink.SysMessage("You need a fishing pole to fish.");
            return false;
        }
        BroadcastAnimation(ch, SkillType.Fishing, (ushort)AnimationType.AttackWeapon, 0x0240);
        DamageGatherTool(sink, pole);

        if (gatheringEngine != null)
        {
            var result = gatheringEngine.TryGatherForSink(ch, SkillType.Fishing, target);
            if (result.Handled)
            {
                if (result.Depleted)
                {
                    sink.SysMessage(ServerMessages.Get(Msg.Fishing1));
                    return false;
                }
                if (result.Success && result.Item != null)
                {
                    // Source-X SysMessagef(DEFMSG_FISHING_SUCCESS, name) — CCharSkill.cpp:1581
                    sink.SysMessage(ServerMessages.GetFormatted(Msg.FishingSuccess, result.Item.GetName()));
                    sink.DeliverItem(result.Item);
                    return true;
                }
                sink.SysMessage(ServerMessages.Get(Msg.Fishing3));
                return false;
            }
        }

        var marker = FindFallbackMarker(world, target, "Fishing");
        if (marker != null && marker.Amount <= 0)
        {
            sink.SysMessage(ServerMessages.Get(Msg.Fishing1));
            return false;
        }

        bool success = SkillEngine.UseQuick(ch, SkillType.Fishing, 40);
        if (success)
        {
            ConsumeFallbackMarker(world, target, "Fishing", 1, ref marker);
            var fish = world.CreateItem();
            fish.BaseId = 0x09CC;
            fish.Name = "fish";
            fish.Amount = 1;
            sink.SysMessage(ServerMessages.GetFormatted(Msg.FishingSuccess, fish.GetName()));
            sink.DeliverItem(fish);
        }
        else
        {
            sink.SysMessage(ServerMessages.Get(Msg.Fishing3));
        }
        return success;
    }

    // --------------------------------------------------------- Lumberjacking

    public static bool Lumberjacking(IActiveSkillSink sink, Point3D target, GatheringEngine? gatheringEngine, World.GameWorld world)
    {
        var ch = sink.Self;

        int lumberRange = SkillEngine.GetUseRange(SkillType.Lumberjacking, 2);
        if (!CanReachPoint(ch, target, world, lumberRange) ||
            (ch.Position.GetDistanceTo(target) == 0 &&
             !SkillEngine.HasFlag(SkillType.Lumberjacking, SkillFlag.NoMinDist)))
        {
            sink.SysMessage(ServerMessages.Get(Msg.LumberjackingReach));
            return false;
        }

        // Source-X CWorldMap: lumberjacking requires a tree at the target.
        if (!IsTreeTile(world, target))
        {
            sink.SysMessage("There is no tree there to chop.");
            return false;
        }

        // Source-X requires an axe to chop; it wears out with use.
        var axe = FindGatherTool(sink, ItemType.WeaponAxe);
        if (axe == null)
        {
            sink.SysMessage("You need an axe to chop wood.");
            return false;
        }
        BroadcastAnimation(ch, SkillType.Lumberjacking, (ushort)AnimationType.Attack2HSlash, 0x013E);
        DamageGatherTool(sink, axe);

        if (gatheringEngine != null)
        {
            var result = gatheringEngine.TryGatherForSink(ch, SkillType.Lumberjacking, target);
            if (result.Handled)
            {
                if (result.Depleted)
                {
                    sink.SysMessage(ServerMessages.Get(Msg.Lumberjacking1));
                    return false;
                }
                if (result.Success && result.Item != null)
                {
                    sink.SysMessage("You put some logs in your backpack.");
                    sink.DeliverItem(result.Item);
                    return true;
                }
                sink.SysMessage(ServerMessages.Get(Msg.Lumberjacking2));
                return false;
            }
        }

        var marker = FindFallbackMarker(world, target, "Lumberjacking");
        if (marker != null && marker.Amount <= 0)
        {
            sink.SysMessage(ServerMessages.Get(Msg.Lumberjacking1));
            return false;
        }

        bool success = SkillEngine.UseQuick(ch, SkillType.Lumberjacking, 50);
        if (success)
        {
            int amount = sink.Random.Next(1, 5);
            ConsumeFallbackMarker(world, target, "Lumberjacking", amount, ref marker);
            var logs = world.CreateItem();
            logs.BaseId = 0x1BDD;
            logs.Name = "logs";
            logs.Amount = (ushort)amount;
            sink.SysMessage("You put some logs in your backpack.");
            sink.DeliverItem(logs);
        }
        else
        {
            sink.SysMessage(ServerMessages.Get(Msg.Lumberjacking2));
        }
        return success;
    }

    private static Item? FindFallbackMarker(World.GameWorld world, Point3D tile, string skillTag)
    {
        foreach (var item in world.GetItemsInRange(tile, 0))
        {
            if (item.BaseId != GatheringEngine.MarkerGraphic) continue;
            if (!item.TryGetTag("RESOURCE_MARKER", out string? mk) || mk != "1") continue;
            if (!item.TryGetTag("RES_SKILL", out string? st) || st != skillTag) continue;
            if (item.X == tile.X && item.Y == tile.Y) return item;
        }
        return null;
    }

    private static void ConsumeFallbackMarker(World.GameWorld world, Point3D tile, string skillTag, int amount, ref Item? marker)
    {
        if (marker == null)
        {
            marker = world.CreateItem();
            marker.BaseId = GatheringEngine.MarkerGraphic;
            marker.Name = "worldgem bit";
            marker.Amount = (ushort)FallbackResAmount;
            marker.SetAttr(ObjAttributes.Invis | ObjAttributes.Move_Never);
            marker.SetTag("RESOURCE_MARKER", "1");
            marker.SetTag("RES_SKILL", skillTag);
            marker.DecayTime = Environment.TickCount64 + FallbackRegenMs;
            world.PlaceItem(marker, tile);
        }

        int remaining = marker.Amount - amount;
        if (remaining <= 0)
        {
            marker.Amount = 0;
            marker.DecayTime = Environment.TickCount64 + FallbackRegenMs;
        }
        else
        {
            marker.Amount = (ushort)remaining;
        }
    }

    private static void BroadcastAnimation(Character ch, SkillType skill, ushort animId, ushort soundId)
    {
        if (!SkillEngine.HasFlag(skill, SkillFlag.NoAnim))
        {
            // Mounted riders need the horse-variant action — the foot animation
            // makes the client dismount/remount the rider for its duration.
            ushort anim = ch.IsMounted
                ? Combat.BodyAnimTranslator.ToMounted(animId)
                : Combat.BodyAnimTranslator.Translate(ch.BodyId, animId);
            var animPkt = new SphereNet.Network.Packets.Outgoing.PacketAnimation(ch.Uid.Value, anim);
            Character.BroadcastNearby?.Invoke(ch.Position, 18, animPkt, 0);
        }
        if (!SkillEngine.HasFlag(skill, SkillFlag.NoSfx))
        {
            var soundPkt = new SphereNet.Network.Packets.Outgoing.PacketSound(soundId, ch.X, ch.Y, ch.Z);
            Character.BroadcastNearby?.Invoke(ch.Position, 18, soundPkt, 0);
        }
    }

    // ---------------------------------------------------------- Musicianship

    /// <summary>Source-X CChar::Skill_Musicianship. Requires a musical instrument.</summary>
    public static bool Musicianship(IActiveSkillSink sink)
    {
        var instrument = FindMusicalInstrument(sink);
        if (instrument == null)
        {
            sink.SysMessage("You have no musical instrument.");
            return false;
        }
        DamageGatherTool(sink, instrument);
        if (!SkillEngine.HasFlag(SkillType.Musicianship, SkillFlag.NoSfx))
            sink.Sound(0x045);
        return SkillEngine.UseQuick(sink.Self, SkillType.Musicianship, 40);
    }

    // ----------------------------------------------------------- Peacemaking

    /// <summary>Source-X CChar::Skill_Peacemaking. Pacifies a creature.</summary>
    public static bool Peacemaking(IActiveSkillSink sink, Character? target = null)
    {
        var ch = sink.Self;
        var instrument = FindMusicalInstrument(sink);
        if (instrument == null)
        {
            sink.SysMessage("You have no musical instrument.");
            return false;
        }
        DamageGatherTool(sink, instrument);
        if (!SkillEngine.UseQuick(ch, SkillType.Musicianship, 40))
            return false;

        sink.Emote(ServerMessages.Get(Msg.PeacemakingIgnore));
        if (!SkillEngine.UseQuick(ch, SkillType.Peacemaking, 50))
        {
            sink.SysMessage(ServerMessages.Get(Msg.PeacemakingDisobey));
            return false;
        }

        int radius = SkillEngine.GetUseRange(SkillType.Peacemaking, 8);
        int pacified = 0;
        foreach (var creature in sink.World.GetCharsInRange(ch.Position, radius))
        {
            if (creature == ch || creature.IsPlayer || creature.IsDead || creature.IsDeleted)
                continue;
            creature.ClearStatFlag(StatFlag.War);
            creature.FightTarget = Serial.Invalid;
            pacified++;
        }
        return pacified > 0;
    }

    // ----------------------------------------------------------- Discordance

    /// <summary>Source-X CChar::Skill_Enticement (Discordance): debuffs a
    /// creature's defenses for a short time via DISCORD_PCT/DISCORD_UNTIL tags
    /// read by the combat armor calc (lazy expiry, no separate timer).</summary>
    public static bool Discordance(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        if (target == null || target.IsPlayer || target.IsDead || target.IsDeleted)
            return false;
        if (!CanReachPoint(ch, target.Position, sink.World,
                SkillEngine.GetUseRange(SkillType.Enticement, 8)))
        {
            sink.SysMessage("That creature is too far away.");
            return false;
        }
        var instrument = FindMusicalInstrument(sink);
        if (instrument == null)
        {
            sink.SysMessage("You have no musical instrument.");
            return false;
        }
        DamageGatherTool(sink, instrument);
        if (!SkillEngine.UseQuick(ch, SkillType.Musicianship, 40))
            return false;
        if (!SkillEngine.UseQuick(ch, SkillType.Enticement, 50))
        {
            sink.SysMessage(ServerMessages.Get(Msg.PeacemakingDisobey));
            return false;
        }

        // Defense penalty scales with skill (up to ~28%), lasting 20s.
        int pct = Math.Clamp(SkillEngine.GetEffect(SkillType.Enticement,
            ch.GetSkill(SkillType.Enticement), ch.GetSkill(SkillType.Enticement) / 40), 1, 100);
        target.SetTag("DISCORD_PCT", pct.ToString());
        target.SetTag("DISCORD_UNTIL", (Environment.TickCount64 + 20_000).ToString());
        sink.Emote("*plays discordant music*");
        return true;
    }

    // ----------------------------------------------------------- Provocation

    /// <summary>Source-X CChar::Skill_Provocation. Incites one creature against another.</summary>
    public static bool Provocation(IActiveSkillSink sink, Character? target,
        Character? provokeAgainst = null)
    {
        var ch = sink.Self;
        if (target == null || target.IsPlayer || target.IsDead || target.IsDeleted)
            return false;
        if (!CanReachPoint(ch, target.Position, sink.World,
                SkillEngine.GetUseRange(SkillType.Provocation, 8)))
        {
            sink.SysMessage("That creature is too far away.");
            return false;
        }
        var instrument = FindMusicalInstrument(sink);
        if (instrument == null)
        {
            sink.SysMessage("You have no musical instrument.");
            return false;
        }
        DamageGatherTool(sink, instrument);
        if (!SkillEngine.UseQuick(ch, SkillType.Musicianship, 40))
            return false;

        if (provokeAgainst == null || provokeAgainst == target || provokeAgainst == ch ||
            provokeAgainst.IsDead || provokeAgainst.IsDeleted ||
            !CanReachPoint(ch, provokeAgainst.Position, sink.World,
                SkillEngine.GetUseRange(SkillType.Provocation, 8)))
            return false;

        sink.Emote(ServerMessages.GetFormatted(Msg.ProvocationPlayer, target.Name));
        if (!SkillEngine.UseQuick(ch, SkillType.Provocation, 60))
            return false;

        target.FightTarget = provokeAgainst.Uid;
        target.SetStatFlag(StatFlag.War);
        sink.Emote(ServerMessages.GetFormatted(Msg.ProvocationUpset, provokeAgainst.Name));
        return true;
    }

    private static Item? FindMusicalInstrument(IActiveSkillSink sink)
    {
        var packed = sink.FindBackpackItem(ItemType.Musical);
        if (packed != null) return packed;
        var hand = sink.Self.GetEquippedItem(Layer.OneHanded);
        return hand?.ItemType == ItemType.Musical ? hand : null;
    }

    private static bool MatchesCategory(Character c, TrackingCategory cat) => cat switch
    {
        TrackingCategory.Animals  => c.NpcBrain == NpcBrainType.Animal,
        TrackingCategory.Monsters => c.NpcBrain is NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon,
        TrackingCategory.Humans   => !c.IsPlayer && c.NpcBrain == NpcBrainType.Human,
        TrackingCategory.Players  => c.IsPlayer,
        _ => false,
    };

    // --------------------------------------------------------------- Tinkering

    /// <summary>Source-X weapon-dclick repair via tinkering skill.</summary>
    public static bool RepairItem(IActiveSkillSink sink, Item? target)
    {
        var ch = sink.Self;
        if (target == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairUnk));
            return false;
        }

        if (!CanReachItem(ch, target, sink.World, SkillType.Tinkering, 2))
        {
            sink.SysMessage("You must have that item in your possession.");
            return false;
        }

        if (target.IsEquipped)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairWorn));
            return false;
        }

        var def = DefinitionLoader.GetItemDef(target.BaseId);
        if (def != null && !def.Repair)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairNot));
            return false;
        }

        // Source-X Use_Repair: only items that actually have hitpoints can be
        // repaired. Inventing (and persisting) a 50-point pool here permanently
        // turned a never-wearing item into breakable gear.
        int maxHits = target.GetHitsMax();
        if (maxHits <= 0)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairFull));
            return false;
        }

        int curHits = target.GetHitsCur();
        if (curHits >= maxHits)
        {
            sink.SysMessage(ServerMessages.Get(Msg.RepairFull));
            return false;
        }

        // Source-X Use_Repair (CCharUse.cpp:792-852): difficulty is the item's
        // SKILLMAKE main-skill level scaled by the damage percent (floor: a
        // quarter of that level), rolled against THAT craft skill. Success is
        // a FULL repair; failure has a 1/6 chance to lower max durability and
        // otherwise a 1/3 chance to chip a point.
        int damagePercent = (maxHits - curHits) * 100 / Math.Max(1, maxHits);
        var (repairSkill, skillLevel) = ResolveRepairSkill(def);
        int difficulty = Math.Max(skillLevel * damagePercent / 100, skillLevel / 4);

        if (!SkillEngine.UseQuick(ch, repairSkill, difficulty))
        {
            if (sink.Random.Next(6) == 0)
            {
                target.HitsMax = Math.Max(1, maxHits - 1);
                target.HitsCur = Math.Max(0, curHits - 1);
                sink.SysMessage(ServerMessages.Get(Msg.Repair2));
            }
            else if (sink.Random.Next(3) == 0)
            {
                target.HitsCur = Math.Max(0, curHits - 1);
                sink.SysMessage(ServerMessages.Get(Msg.Repair3));
            }
            else
            {
                sink.SysMessage(ServerMessages.Get(Msg.Repair4));
            }
            return false;
        }

        target.HitsCur = maxHits; // Source-X: success restores to full
        sink.SysMessage(ServerMessages.GetFormatted(Msg.RepairMsg, "You repair", target.Name ?? "the item"));
        return true;
    }

    /// <summary>The item's SKILLMAKE main craft skill and its level on the
    /// 0-100 scale (Source-X Use_Repair reads m_SkillMake). No SKILLMAKE →
    /// Tinkering at 50, the pre-parity blanket.</summary>
    private static (SkillType Skill, int Level) ResolveRepairSkill(SphereNet.Scripting.Definitions.ItemDef? def)
    {
        if (def != null && !string.IsNullOrWhiteSpace(def.SkillMakeRaw))
        {
            foreach (var part in def.SkillMakeRaw.Split(','))
            {
                var bits = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (bits.Length >= 2 &&
                    Enum.TryParse<SkillType>(bits[0], ignoreCase: true, out var sk) &&
                    double.TryParse(bits[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lvl))
                    return (sk, (int)lvl);
            }
        }
        return (SkillType.Tinkering, 50);
    }
}
