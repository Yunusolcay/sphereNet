using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

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
/// engine network-free and unit-testable. The synchronous collapse means
/// SphereNet does not yet emulate Source-X's per-tick STROKE animation
/// loop -- a future tick scheduler can drive these as well by calling the
/// individual phase helpers.
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
            sink.SysMessage(ServerMessages.Get(Msg.HidingStumble));
        }
        return success;
    }

    // ---------------------------------------------------------- DetectHidden

    /// <summary>Source-X CChar::Skill_DetectHidden. Reveals hidden chars in radius based on skill diff.</summary>
    public static bool DetectHidden(IActiveSkillSink sink)
    {
        var ch = sink.Self;
        bool succeeded = SkillEngine.UseQuick(ch, SkillType.DetectingHidden, sink.Random.Next(50));
        if (!succeeded) return false;

        sink.SysMessage(ServerMessages.Get(Msg.DetecthiddenSucc));

        int detectSkill = ch.GetSkill(SkillType.DetectingHidden);
        foreach (var nearby in sink.World.GetCharsInRange(ch.Position, 8))
        {
            if (nearby == ch || !nearby.IsStatFlag(StatFlag.Hidden)) continue;
            int diff = detectSkill - nearby.GetSkill(SkillType.Hiding);
            if (diff > 0 || sink.Random.Next(1000) < 300)
            {
                nearby.ClearStatFlag(StatFlag.Hidden);
                nearby.ClearStatFlag(StatFlag.Invisible);
            }
        }
        return true;
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

        sink.SysMessage(ServerMessages.Get(Msg.MeditationTry));

        bool success = SkillEngine.UseQuick(ch, SkillType.Meditation, sink.Random.Next(100));
        if (success)
        {
            ch.SetStatFlag(StatFlag.Meditation);
            sink.Sound(0x0F9);
            sink.SysMessage(ServerMessages.Get(Msg.MeditationSuccess));
            int gain = Math.Max(1, ch.GetSkill(SkillType.Meditation) / 100);
            ch.Mana = (short)Math.Min(ch.MaxMana, ch.Mana + gain);

            if (ch.Mana >= ch.MaxMana)
                sink.SysMessage(ServerMessages.Get(Msg.MeditationPeace2));
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
            sink.Sound(0x24A);
            sink.SysMessage(ServerMessages.Get(Msg.SpiritspeakSuccess));
            ch.SetStatFlag(StatFlag.SpiritSpeak);
        }
        return success;
    }

    // -------------------------------------------------------------- Begging

    /// <summary>Source-X CChar::Skill_Begging. Targets human NPC; success grants 1-10 gold.</summary>
    public static bool Begging(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        if (target == null || target.IsPlayer || target.NpcBrain != NpcBrainType.Human)
            return false;

        sink.Emote(ServerMessages.Get(Msg.BeggingStart));
        bool success = SkillEngine.UseQuick(ch, SkillType.Begging, 40);
        if (success && ch.Backpack != null)
        {
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
        if (target.GetWeight() > Math.Max(1, ch.GetSkill(SkillType.Stealing) / 10))
        {
            sink.SysMessage(ServerMessages.Get(Msg.StealingHeavy));
            return false;
        }

        Character? owner = ResolveItemOwner(target, sink.World);
        if (owner != null)
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.StealingPickpocket, owner.Name));
        }

        bool success = SkillEngine.UseQuick(ch, SkillType.Stealing, sink.Random.Next(60));

        if (success)
        {
            // Move item to backpack.
            ch.Backpack?.AddItem(target);
            target.ContainedIn = ch.Backpack?.Uid ?? Serial.Invalid;
        }
        else
        {
            // Source-X: ~50% chance to be noticed -> caught flag + MakeCriminal.
            if (sink.Random.Next(2) == 0)
            {
                if (owner != null)
                    sink.SysMessage(ServerMessages.GetFormatted(Msg.StealingMark, owner.Name));
                ch.MakeCriminal();
            }
        }
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

        sink.SysMessage(ServerMessages.Get(Msg.SnoopingAttempting));
        bool success = SkillEngine.UseQuick(ch, SkillType.Snooping, sink.Random.Next(50));
        if (!success)
        {
            sink.SysMessage(ServerMessages.Get(Msg.SnoopingFailed));
            if (Character.SnoopCriminalEnabled)
                ch.MakeCriminal();
        }
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

        bool success = SkillEngine.UseQuick(ch, SkillType.RemoveTrap, sink.Random.Next(60));
        if (success)
            trap.ItemType = ItemType.Trap; // disarm: clear active variant.
        return success;
    }

    // -------------------------------------------------------------- Healing

    /// <summary>
    /// Source-X CChar::Skill_Healing. Requires bandage; checks reach; healthy/dead/poisoned
    /// branches emit specific DEFMSG_HEALING_* messages and consume the bandage on fail
    /// or success (per upstream).
    /// </summary>
    public static bool Healing(IActiveSkillSink sink, Character? target)
    {
        var ch = sink.Self;
        target ??= ch;

        var bandage = sink.FindBackpackItem(ItemType.Bandage);
        if (bandage == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HealingNoaids));
            return false;
        }

        if (GetDistance(ch.Position, target.Position) > 2)
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

        // Difficulty: dead = 85+25, poisoned = 50+50, normal = 0..80.
        int diff = target.IsStatFlag(StatFlag.Dead) ? 85 + sink.Random.Next(25)
                 : target.IsStatFlag(StatFlag.Poisoned) ? 50 + sink.Random.Next(50)
                 : sink.Random.Next(80);
        bool success = SkillEngine.UseQuick(ch, SkillType.Healing, diff);

        sink.ConsumeAmount(bandage); // Source-X consumes on fail too.
        if (!success)
            return false;

        if (target.IsStatFlag(StatFlag.Poisoned))
        {
            int skillLvl = ch.GetSkill(SkillType.Healing);
            if (sink.Random.Next(1000) < skillLvl)
            {
                target.ClearStatFlag(StatFlag.Poisoned);
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
            // Resurrect path -- relies on existing GameWorld death pipeline; emit text only.
            sink.SysMessage(ServerMessages.Get(Msg.HealingRes));
            return true;
        }

        int heal = ch.GetSkill(SkillType.Healing) / 40 + 3;
        target.Hits = (short)Math.Min(target.MaxHits, target.Hits + heal);
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
        if (target.NpcBrain == NpcBrainType.Human)
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingCant));
            return false;
        }
        if (target.IsStatFlag(StatFlag.Pet))
        {
            sink.SysMessage(ServerMessages.GetFormatted(Msg.TamingTame, target.Name));
            return false;
        }
        if (GetDistance(ch.Position, target.Position) > 6)
        {
            sink.SysMessage(ServerMessages.Get(Msg.TamingLos));
            return false;
        }

        // Source-X cycles through TAMING_1..4 emotes during the stage loop.
        string[] tries = { Msg.Taming1, Msg.Taming2, Msg.Taming3, Msg.Taming4 };
        sink.Emote(ServerMessages.GetFormatted(tries[sink.Random.Next(tries.Length)], target.Name));

        // Difficulty is roughly 1.5x the target's combined skill -- approximated.
        int diff = Math.Min(1000, target.MaxHits * 4);
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
        var crook = sink.FindBackpackItem(ItemType.WeaponMaceCrook);
        if (crook == null)
        {
            sink.SysMessage(ServerMessages.Get(Msg.HerdingNocrook));
            return false;
        }

        int diff = animal.Int / 2 + sink.Random.Next(Math.Max(1, animal.Int / 2));
        bool success = SkillEngine.UseQuick(ch, SkillType.Herding, diff);
        if (success && destination.HasValue)
        {
            animal.Position = destination.Value;
            animal.SetTag("HERD_MASTER", ch.Uid.Value.ToString());
            animal.SetTag("HERD_MASTER_UUID", ch.Uuid.ToString("D"));
            sink.ObjectMessage(animal, ServerMessages.Get(Msg.HerdingSuccess));
        }
        return success;
    }

    // ------------------------------------------------------------ Poisoning

    /// <summary>Source-X CChar::Skill_Poisoning. Apply potion poison level to target weapon.</summary>
    public static bool Poisoning(IActiveSkillSink sink, Item? weapon)
    {
        var ch = sink.Self;
        if (weapon == null || !IsBladeOrFood(weapon.ItemType))
        {
            sink.SysMessage(ServerMessages.Get(Msg.PoisoningWitem));
            return false;
        }

        var potion = sink.FindBackpackItem(ItemType.Potion);
        if (potion == null ||
            !potion.TryGetTag("POTION_SPELL", out string? spell) ||
            !string.Equals(spell, "Poison", StringComparison.OrdinalIgnoreCase))
        {
            sink.SysMessage(ServerMessages.Get(Msg.PoisoningSelect1));
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
        int range = 10 + ch.GetSkill(SkillType.Tracking) / 10;
        int count = 0;

        foreach (var c in sink.World.GetCharsInRange(ch.Position, range))
        {
            if (c == ch) continue;
            if (!MatchesCategory(c, category)) continue;
            count++;
        }

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

    public enum TrackingCategory { Animals, Monsters, Humans, Players }

    // ----------------------------------------------------------- primitives

    private static int GetDistance(Point3D a, Point3D b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static int GetWeight(this Item it)
    {
        var def = DefinitionLoader.GetItemDef(it.BaseId);
        return def != null ? def.Weight : 1;
    }

    private static bool IsBladeOrFood(ItemType t) => t is
        ItemType.WeaponMaceSharp or ItemType.WeaponSword or ItemType.WeaponFence or
        ItemType.WeaponAxe or ItemType.Food or ItemType.MeatRaw or ItemType.Fruit;

    private static Character? ResolveItemOwner(Item it, World.GameWorld world)
    {
        if (!it.ContainedIn.IsValid) return null;
        // Walk up: container -> owner char.
        var holder = world.FindObject(it.ContainedIn);
        return holder switch
        {
            Character c => c,
            Item parent => ResolveItemOwner(parent, world),
            _ => null,
        };
    }

    // --------------------------------------------------------------- Mining

    private const int FallbackResAmount = 20;
    private const int FallbackRegenMs = 36_000_000;

    public static bool Mining(IActiveSkillSink sink, Point3D target, GatheringEngine? gatheringEngine, World.GameWorld world)
    {
        var ch = sink.Self;

        BroadcastAnimation(ch, (ushort)AnimationType.Attack1HBash, 0x0125);

        if (GetDistance(ch.Position, target) > 2)
        {
            sink.SysMessage(ServerMessages.Get(Msg.MiningReach));
            return false;
        }

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

    // -------------------------------------------------------------- Fishing

    public static bool Fishing(IActiveSkillSink sink, Point3D target, GatheringEngine? gatheringEngine, World.GameWorld world)
    {
        var ch = sink.Self;

        BroadcastAnimation(ch, (ushort)AnimationType.AttackWeapon, 0x0240);

        if (GetDistance(ch.Position, target) > 6)
        {
            sink.SysMessage(ServerMessages.Get(Msg.FishingReach));
            return false;
        }

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
                    sink.SysMessage(ServerMessages.Get(Msg.FishingSuccess));
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
            sink.SysMessage(ServerMessages.Get(Msg.FishingSuccess));
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

        BroadcastAnimation(ch, (ushort)AnimationType.Attack2HSlash, 0x013E);

        if (GetDistance(ch.Position, target) > 2)
        {
            sink.SysMessage(ServerMessages.Get(Msg.LumberjackingReach));
            return false;
        }

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
            if (item.BaseId != 0x1) continue;
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
            marker.BaseId = 0x1;
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

    private static void BroadcastAnimation(Character ch, ushort animId, ushort soundId)
    {
        var animPkt = new SphereNet.Network.Packets.Outgoing.PacketAnimation(ch.Uid.Value, animId);
        Character.BroadcastNearby?.Invoke(ch.Position, 18, animPkt, 0);
        var soundPkt = new SphereNet.Network.Packets.Outgoing.PacketSound(soundId, ch.X, ch.Y, ch.Z);
        Character.BroadcastNearby?.Invoke(ch.Position, 18, soundPkt, 0);
    }

    private static bool MatchesCategory(Character c, TrackingCategory cat) => cat switch
    {
        TrackingCategory.Animals  => c.NpcBrain == NpcBrainType.Animal,
        TrackingCategory.Monsters => c.NpcBrain is NpcBrainType.Monster or NpcBrainType.Berserk or NpcBrainType.Dragon,
        TrackingCategory.Humans   => !c.IsPlayer && c.NpcBrain == NpcBrainType.Human,
        TrackingCategory.Players  => c.IsPlayer,
        _ => false,
    };
}
