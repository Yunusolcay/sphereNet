using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Game.Skills;

/// <summary>
/// Per-skill use handlers. Maps to Skill_Start / Skill_Stage for each skill.
/// Each handler returns true if the skill use succeeded.
/// </summary>
public sealed class SkillHandlers
{
    private readonly GameWorld _world;
    private readonly GatheringEngine? _gatheringEngine;
    private readonly Dictionary<SkillType, Func<Character, Point3D?, bool>> _handlers = [];

    /// <summary>Callback to open crafting gump for a character. Set by GameClient.</summary>
    public static Action<Character, SkillType>? OnCraftSkillUsed { get; set; }

    /// <summary>Callback for scripted (custom) skill use. Set by Program.cs to fire trigger chain.</summary>
    public static Func<Character, SkillType, bool>? OnScriptedSkillUse { get; set; }

    public SkillHandlers(GameWorld world, GatheringEngine? gatheringEngine = null)
    {
        _world = world;
        _gatheringEngine = gatheringEngine;
        RegisterAll();
    }

    /// <summary>
    /// Information skills (Anatomy, AnimalLore, ArmsLore, EvalInt, Forensics,
    /// ItemID, TasteID) require a selected target to produce their Source-X
    /// message output. <see cref="GameClient.HandleUseSkill"/> detects these
    /// skills, opens a target cursor, and routes the resolved object here.
    ///
    /// Returns true when the underlying skill check succeeded (gain applies);
    /// regardless, the sink has already received the Source-X text.
    /// </summary>
    public bool UseInfoSkill(IInfoSkillSink sink, SkillType skill, ObjBase? target)
    {
        var ch = sink.Self;
        if (ch.IsDead) return false;

        int level = ch.GetSkill(skill);
        switch (skill)
        {
            case SkillType.Anatomy:
                InfoSkillEngine.Anatomy(sink, target as Character ?? ch, level);
                return SkillEngine.UseQuick(ch, skill, 30);

            case SkillType.AnimalLore:
                InfoSkillEngine.AnimalLore(sink, target as Character ?? ch, _world, level);
                return SkillEngine.UseQuick(ch, skill, 30);

            case SkillType.ArmsLore:
                InfoSkillEngine.ArmsLore(sink, target as Item, level);
                return SkillEngine.UseQuick(ch, skill, 30);

            case SkillType.EvalInt:
                InfoSkillEngine.EvalInt(sink, target as Character ?? ch, level);
                return SkillEngine.UseQuick(ch, skill, 30);

            case SkillType.Forensics:
                if (target is Item corpse && corpse.ItemType == ItemType.Corpse)
                {
                    Serial killerUid = ResolveKillerUid(corpse);
                    Character? killer = killerUid.IsValid ? _world.FindChar(killerUid) : null;
                    long secs = corpse.TryGetTag("DEATH_TIME", out string? ds) && long.TryParse(ds, out long dt)
                        ? Math.Max(0, (Environment.TickCount64 - dt) / 1000)
                        : 0;
                    bool sleeping = corpse.TryGetTag("CORPSE_SLEEPING", out string? sv) && sv == "1";
                    bool carved = corpse.TryGetTag("CORPSE_CARVED", out string? cv) && cv == "1";
                    InfoSkillEngine.Forensics(sink, corpse, killer, secs, sleeping, carved, level);
                }
                else
                {
                    InfoSkillEngine.Forensics(sink, target as Item, null, 0, false, false, level);
                }
                return SkillEngine.UseQuick(ch, skill, 30);

            case SkillType.ItemId:
                InfoSkillEngine.ItemID(sink, (object?)target ?? ch, level);
                return SkillEngine.UseQuick(ch, skill, 30);

            case SkillType.TasteId:
                InfoSkillEngine.TasteID(sink, (object?)target ?? ch, level);
                return SkillEngine.UseQuick(ch, skill, 30);

            default:
                return UseSkill(ch, skill, null);
        }
    }

    private static Serial ResolveKillerUid(Item corpse)
    {
        if (corpse.TryGetTag("CORPSE_KILLER", out string? kv) && !string.IsNullOrEmpty(kv))
        {
            string t = kv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? kv[2..] : kv;
            if (uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out uint hx))
                return new Serial(hx);
            if (uint.TryParse(kv, out uint dec))
                return new Serial(dec);
        }
        return Serial.Zero;
    }

    /// <summary>Source-X list of skills that prompt for a target and emit only descriptive text.</summary>
    public static bool IsInfoSkill(SkillType skill) => skill switch
    {
        SkillType.Anatomy or SkillType.AnimalLore or SkillType.ArmsLore or
        SkillType.EvalInt or SkillType.Forensics or SkillType.ItemId or
        SkillType.TasteId => true,
        _ => false,
    };

    /// <summary>
    /// Active skills routed through <see cref="ActiveSkillEngine"/> via the
    /// rich <see cref="IActiveSkillSink"/> path. Returns the kind of target
    /// prompt the client should open.
    /// </summary>
    public static ActiveSkillTargetKind GetActiveSkillTarget(SkillType skill) => skill switch
    {
        SkillType.Hiding         => ActiveSkillTargetKind.None,
        SkillType.DetectingHidden => ActiveSkillTargetKind.None,
        SkillType.Meditation     => ActiveSkillTargetKind.None,
        SkillType.SpiritSpeak    => ActiveSkillTargetKind.None,
        SkillType.Tracking       => ActiveSkillTargetKind.Menu,
        SkillType.Begging        => ActiveSkillTargetKind.Character,
        SkillType.Healing        => ActiveSkillTargetKind.Character,
        SkillType.Taming         => ActiveSkillTargetKind.Character,
        SkillType.Stealing       => ActiveSkillTargetKind.Item,
        SkillType.Snooping       => ActiveSkillTargetKind.Item,
        SkillType.Lockpicking    => ActiveSkillTargetKind.Item,
        SkillType.RemoveTrap     => ActiveSkillTargetKind.Item,
        SkillType.Poisoning      => ActiveSkillTargetKind.Item,
        SkillType.Herding        => ActiveSkillTargetKind.Character,
        _                        => ActiveSkillTargetKind.Unsupported,
    };

    /// <summary>
    /// Dispatch entry for active skills that takes the rich sink and routes
    /// to the matching <see cref="ActiveSkillEngine"/> method. Falls back to
    /// the legacy <see cref="UseSkill"/> path for unsupported skills.
    /// </summary>
    public bool UseActiveSkill(IActiveSkillSink sink, SkillType skill, ObjBase? target, Point3D? point = null)
    {
        var ch = sink.Self;
        if (ch.IsDead) return false;

        switch (skill)
        {
            case SkillType.Hiding:           return ActiveSkillEngine.Hiding(sink);
            case SkillType.DetectingHidden:  return ActiveSkillEngine.DetectHidden(sink);
            case SkillType.Meditation:       return ActiveSkillEngine.Meditation(sink);
            case SkillType.SpiritSpeak:      return ActiveSkillEngine.SpiritSpeak(sink);
            case SkillType.Begging:          return ActiveSkillEngine.Begging(sink, target as Character);
            case SkillType.Healing:          return ActiveSkillEngine.Healing(sink, target as Character);
            case SkillType.Taming:           return ActiveSkillEngine.Taming(sink, target as Character);
            case SkillType.Stealing:         return ActiveSkillEngine.Stealing(sink, target as Item);
            case SkillType.Snooping:         return ActiveSkillEngine.Snooping(sink, target as Item);
            case SkillType.Lockpicking:      return ActiveSkillEngine.Lockpicking(sink, target as Item);
            case SkillType.RemoveTrap:       return ActiveSkillEngine.RemoveTrap(sink, target as Item);
            case SkillType.Poisoning:        return ActiveSkillEngine.Poisoning(sink, target as Item);
            case SkillType.Herding:          return ActiveSkillEngine.Herding(sink, target as Character, point);
            case SkillType.Tracking:         return ActiveSkillEngine.Tracking(sink, ActiveSkillEngine.TrackingCategory.Animals);
            default:                         return UseSkill(ch, skill, point);
        }
    }

    public enum ActiveSkillTargetKind { None, Character, Item, Menu, Unsupported }

    public bool UseSkill(Character ch, SkillType skill, Point3D? target = null)
    {
        if (ch.IsDead) return false;

        if (_handlers.TryGetValue(skill, out var handler))
            return handler(ch, target);

        // Check for scripted (custom) skill via SkillDef
        var def = DefinitionLoader.GetSkillDef((int)skill);
        if (def != null && ((SkillFlag)def.Flags & SkillFlag.Scripted) != 0)
        {
            if (((SkillFlag)def.Flags & SkillFlag.Disabled) != 0)
                return false;
            return OnScriptedSkillUse?.Invoke(ch, skill) ?? false;
        }

        return SkillEngine.UseQuick(ch, skill, 50);
    }

    private void RegisterAll()
    {
        _handlers[SkillType.Hiding] = HandleHiding;
        _handlers[SkillType.Stealth] = HandleStealth;
        _handlers[SkillType.DetectingHidden] = HandleDetectHidden;
        _handlers[SkillType.Mining] = HandleMining;
        _handlers[SkillType.Fishing] = HandleFishing;
        _handlers[SkillType.Lumberjacking] = HandleLumberjacking;
        _handlers[SkillType.Taming] = HandleTaming;
        _handlers[SkillType.AnimalLore] = HandleAnimalLore;
        _handlers[SkillType.Healing] = HandleHealing;
        _handlers[SkillType.Anatomy] = HandleAnatomy;
        _handlers[SkillType.Peacemaking] = HandlePeacemaking;
        _handlers[SkillType.Provocation] = HandleProvocation;
        _handlers[SkillType.Musicianship] = HandleMusicianship;
        _handlers[SkillType.Meditation] = HandleMeditation;
        _handlers[SkillType.SpiritSpeak] = HandleSpiritSpeak;
        _handlers[SkillType.Poisoning] = HandlePoisoning;
        _handlers[SkillType.ItemId] = HandleItemID;
        _handlers[SkillType.ArmsLore] = HandleArmsLore;
        _handlers[SkillType.Tracking] = HandleTracking;
        _handlers[SkillType.Forensics] = HandleForensics;
        _handlers[SkillType.Begging] = HandleBegging;
        _handlers[SkillType.Stealing] = HandleStealing;
        _handlers[SkillType.Snooping] = HandleSnooping;
        _handlers[SkillType.Lockpicking] = HandleLockpicking;
        _handlers[SkillType.RemoveTrap] = HandleRemoveTrap;
        _handlers[SkillType.Inscription] = HandleInscription;
        _handlers[SkillType.Cooking] = HandleCooking;
        _handlers[SkillType.Alchemy] = HandleAlchemy;
        _handlers[SkillType.Tailoring] = HandleTailoring;
        _handlers[SkillType.Blacksmithing] = HandleBlacksmithing;
        _handlers[SkillType.Carpentry] = HandleCarpentry;
        _handlers[SkillType.Tinkering] = HandleTinkering;
        _handlers[SkillType.Cartography] = HandleCartography;
        _handlers[SkillType.Bowcraft] = HandleBowcraft;
        _handlers[SkillType.TasteId] = HandleTasteID;
        _handlers[SkillType.EvalInt] = HandleEvalInt;
        _handlers[SkillType.Veterinary] = HandleVeterinary;
        _handlers[SkillType.Herding] = HandleHerding;
        _handlers[SkillType.Camping] = HandleCamping;
        _handlers[SkillType.Focus] = HandleFocus;
    }

    private bool HandleHiding(Character ch, Point3D? target)
    {
        if (ch.IsInWarMode) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Hiding, 50);
        if (success)
        {
            ch.SetStatFlag(StatFlag.Hidden);
            ch.SetStatFlag(StatFlag.Invisible);
        }
        return success;
    }

    private bool HandleStealth(Character ch, Point3D? target)
    {
        if (!ch.IsStatFlag(StatFlag.Hidden)) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Stealth, 60);
        if (success)
            ch.SetTag("StealthSteps", "10");
        return success;
    }

    private bool HandleDetectHidden(Character ch, Point3D? target)
    {
        bool success = SkillEngine.UseQuick(ch, SkillType.DetectingHidden, 50);
        if (success)
        {
            foreach (var nearby in _world.GetCharsInRange(ch.Position, 8))
            {
                if (nearby == ch || !nearby.IsStatFlag(StatFlag.Hidden)) continue;
                int detectDiff = ch.GetSkill(SkillType.DetectingHidden) - nearby.GetSkill(SkillType.Hiding);
                if (detectDiff > 0 || new Random().Next(1000) < 300)
                {
                    nearby.ClearStatFlag(StatFlag.Hidden);
                    nearby.ClearStatFlag(StatFlag.Invisible);
                }
            }
        }
        return success;
    }

    private bool HandleMining(Character ch, Point3D? target)
    {
        if (target == null) return false;

        BroadcastSkillAnimation(ch, (ushort)Core.Enums.AnimationType.Attack1HBash, 0x0125);

        if (_gatheringEngine != null &&
            _gatheringEngine.TryGather(ch, SkillType.Mining, target.Value, out bool regionSuccess, out _, out _))
            return regionSuccess;

        bool success = SkillEngine.UseQuick(ch, SkillType.Mining, 50);
        if (success)
        {
            var ore = _world.CreateItem();
            ore.BaseId = 0x19B9;
            ore.Name = "iron ore";
            ore.Amount = (ushort)new Random().Next(1, 3);
            if (ch.Backpack != null)
                ch.Backpack.AddItem(ore);
            else
                _world.PlaceItemWithDecay(ore, ch.Position);
        }
        return success;
    }

    private bool HandleFishing(Character ch, Point3D? target)
    {
        if (target == null) return false;

        BroadcastSkillAnimation(ch, (ushort)Core.Enums.AnimationType.AttackWeapon, 0x0240);

        if (_gatheringEngine != null &&
            _gatheringEngine.TryGather(ch, SkillType.Fishing, target.Value, out bool regionSuccess, out _, out _))
            return regionSuccess;

        bool success = SkillEngine.UseQuick(ch, SkillType.Fishing, 40);
        if (success)
        {
            var fish = _world.CreateItem();
            fish.BaseId = 0x09CC;
            fish.Name = "fish";
            fish.Amount = 1;
            if (ch.Backpack != null)
                ch.Backpack.AddItem(fish);
            else
                _world.PlaceItemWithDecay(fish, ch.Position);
        }
        return success;
    }

    private bool HandleLumberjacking(Character ch, Point3D? target)
    {
        if (target == null) return false;

        BroadcastSkillAnimation(ch, (ushort)Core.Enums.AnimationType.Attack2HSlash, 0x013E);

        if (_gatheringEngine != null &&
            _gatheringEngine.TryGather(ch, SkillType.Lumberjacking, target.Value, out bool regionSuccess, out _, out _))
            return regionSuccess;

        bool success = SkillEngine.UseQuick(ch, SkillType.Lumberjacking, 50);
        if (success)
        {
            var logs = _world.CreateItem();
            logs.BaseId = 0x1BDD;
            logs.Name = "logs";
            logs.Amount = (ushort)new Random().Next(1, 5);
            if (ch.Backpack != null)
                ch.Backpack.AddItem(logs);
            else
                _world.PlaceItemWithDecay(logs, ch.Position);
        }
        return success;
    }

    private static void BroadcastSkillAnimation(Character ch, ushort animId, ushort soundId)
    {
        var animPkt = new SphereNet.Network.Packets.Outgoing.PacketAnimation(ch.Uid.Value, animId);
        Character.BroadcastNearby?.Invoke(ch.Position, 18, animPkt, 0);
        var soundPkt = new SphereNet.Network.Packets.Outgoing.PacketSound(soundId, ch.X, ch.Y, ch.Z);
        Character.BroadcastNearby?.Invoke(ch.Position, 18, soundPkt, 0);
    }

    private bool HandleTaming(Character ch, Point3D? target)
    {
        if (target == null) return false;
        var npc = FindNearbyNpc(ch, target.Value);
        if (npc == null || npc.NpcBrain == NpcBrainType.Human) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Taming, 60);
        if (success)
        {
            npc.NpcBrain = NpcBrainType.Animal;
            success = npc.TryAssignOwnership(ch, ch, summoned: false, enforceFollowerCap: true);
        }
        return success;
    }

    private bool HandleAnimalLore(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.AnimalLore, 30);
    }

    private bool HandleHealing(Character ch, Point3D? target)
    {
        var healTarget = target != null ? FindNearbyChar(ch, target.Value) : ch;
        if (healTarget == null) healTarget = ch;
        bool success = SkillEngine.UseQuick(ch, SkillType.Healing, 40);
        if (success)
        {
            int healAmount = ch.GetSkill(SkillType.Healing) / 40 + 3;
            healTarget.Hits += (short)healAmount;
        }
        return success;
    }

    private bool HandleAnatomy(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Anatomy, 30);
    }

    private bool HandlePeacemaking(Character ch, Point3D? target)
    {
        if (!SkillEngine.UseQuick(ch, SkillType.Musicianship, 40)) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Peacemaking, 50);
        if (success && target != null)
        {
            var npc = FindNearbyNpc(ch, target.Value);
            if (npc != null)
                npc.ClearStatFlag(StatFlag.War);
        }
        return success;
    }

    private bool HandleProvocation(Character ch, Point3D? target)
    {
        if (!SkillEngine.UseQuick(ch, SkillType.Musicianship, 40)) return false;
        return SkillEngine.UseQuick(ch, SkillType.Provocation, 60);
    }

    private bool HandleMusicianship(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Musicianship, 40);
    }

    private bool HandleMeditation(Character ch, Point3D? target)
    {
        if (ch.Mana >= ch.MaxMana) return false;
        bool success = SkillEngine.UseQuick(ch, SkillType.Meditation, 40);
        if (success)
            ch.SetStatFlag(StatFlag.Meditation);
        return success;
    }

    private bool HandleSpiritSpeak(Character ch, Point3D? target)
    {
        bool success = SkillEngine.UseQuick(ch, SkillType.SpiritSpeak, 50);
        if (success)
            ch.SetStatFlag(StatFlag.SpiritSpeak);
        return success;
    }

    private bool HandlePoisoning(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Poisoning, 50);
    }

    private bool HandleItemID(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.ItemId, 30);
    }

    private bool HandleArmsLore(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.ArmsLore, 30);
    }

    private bool HandleTracking(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Tracking, 50);
    }

    private bool HandleForensics(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Forensics, 30);
    }

    private bool HandleBegging(Character ch, Point3D? target)
    {
        bool success = SkillEngine.UseQuick(ch, SkillType.Begging, 40);
        if (success && ch.Backpack != null)
        {
            var gold = _world.CreateItem();
            gold.BaseId = 0x0EED;
            gold.Name = "Gold";
            gold.ItemType = ItemType.Gold;
            gold.Amount = (ushort)new Random().Next(1, 10);
            ch.Backpack.AddItem(gold);
        }
        return success;
    }

    private bool HandleStealing(Character ch, Point3D? target)
    {
        bool success = SkillEngine.UseQuick(ch, SkillType.Stealing, 60);
        if (success)
            ch.MakeCriminal();
        return success;
    }

    private bool HandleSnooping(Character ch, Point3D? target)
    {
        bool success = SkillEngine.UseQuick(ch, SkillType.Snooping, 50);
        if (!success && Character.SnoopCriminalEnabled)
            ch.MakeCriminal();
        return success;
    }

    private bool HandleLockpicking(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Lockpicking, 60);
    }

    private bool HandleRemoveTrap(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.RemoveTrap, 60);
    }

    private bool HandleInscription(Character ch, Point3D? target)
    {
        OnCraftSkillUsed?.Invoke(ch, SkillType.Inscription);
        return true;
    }

    private bool HandleCooking(Character ch, Point3D? target)
    {
        OnCraftSkillUsed?.Invoke(ch, SkillType.Cooking);
        return true;
    }

    private bool HandleAlchemy(Character ch, Point3D? target)
    {
        OnCraftSkillUsed?.Invoke(ch, SkillType.Alchemy);
        return true;
    }

    private bool HandleTailoring(Character ch, Point3D? target)
    {
        OnCraftSkillUsed?.Invoke(ch, SkillType.Tailoring);
        return true;
    }

    private bool HandleBlacksmithing(Character ch, Point3D? target)
    {
        OnCraftSkillUsed?.Invoke(ch, SkillType.Blacksmithing);
        return true;
    }

    private bool HandleCarpentry(Character ch, Point3D? target)
    {
        OnCraftSkillUsed?.Invoke(ch, SkillType.Carpentry);
        return true;
    }

    private bool HandleTinkering(Character ch, Point3D? target)
    {
        OnCraftSkillUsed?.Invoke(ch, SkillType.Tinkering);
        return true;
    }

    private bool HandleCartography(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.Cartography, 40);
    }

    private bool HandleBowcraft(Character ch, Point3D? target)
    {
        OnCraftSkillUsed?.Invoke(ch, SkillType.Bowcraft);
        return true;
    }

    private bool HandleTasteID(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.TasteId, 30);
    }

    private bool HandleEvalInt(Character ch, Point3D? target)
    {
        return SkillEngine.UseQuick(ch, SkillType.EvalInt, 30);
    }

    private bool HandleVeterinary(Character ch, Point3D? target)
    {
        // Heal animals/pets — requires bandages in backpack
        var targetPt = target ?? ch.Position;
        var animal = FindNearbyNpc(ch, targetPt);
        if (animal == null) return false;

        // Must be an animal (or tamed pet with a master)
        if (animal.NpcBrain != NpcBrainType.Animal && animal.NpcBrain != NpcBrainType.Monster)
            return false;

        bool success = SkillEngine.UseQuick(ch, SkillType.Veterinary, 40);
        if (success)
        {
            int heal = ch.GetSkill(SkillType.Veterinary) / 40 + 3;
            animal.Hits = (short)Math.Min(animal.Hits + heal, animal.MaxHits);
        }
        return success;
    }

    private bool HandleHerding(Character ch, Point3D? target)
    {
        // Direct an animal to a location using a crook
        var targetPt = target ?? ch.Position;
        var animal = FindNearbyNpc(ch, targetPt);
        if (animal == null) return false;

        if (animal.NpcBrain != NpcBrainType.Animal)
            return false;

        bool success = SkillEngine.UseQuick(ch, SkillType.Herding, 40);
        if (success)
        {
            // Move animal toward caster's target direction
            animal.SetTag("HERD_MASTER", ch.Uid.Value.ToString());
            animal.SetTag("HERD_MASTER_UUID", ch.Uuid.ToString("D"));
        }
        return success;
    }

    private bool HandleCamping(Character ch, Point3D? target)
    {
        // Create a campfire and allow safe logout
        bool success = SkillEngine.UseQuick(ch, SkillType.Camping, 30);
        if (success)
        {
            // Create campfire item at character's location
            var campfire = _world.CreateItem();
            campfire.BaseId = 0x0DE3; // campfire
            campfire.Position = ch.Position;
            campfire.SetTag("CAMPFIRE_OWNER", ch.Uid.Value.ToString());
            campfire.SetTag("CAMPFIRE_OWNER_UUID", ch.Uuid.ToString("D"));
            campfire.SetTimeout(Environment.TickCount64 + 30000); // 30 sec duration
        }
        return success;
    }

    private bool HandleFocus(Character ch, Point3D? target)
    {
        // Passive skill — boosts mana regeneration
        // When actively used, grants a short focus buff
        bool success = SkillEngine.UseQuick(ch, SkillType.Focus, 20);
        if (success)
        {
            ch.SetTag("FOCUS_BUFF", (Environment.TickCount64 + 10000).ToString());
        }
        return success;
    }

    private Character? FindNearbyNpc(Character ch, Point3D target)
    {
        foreach (var c in _world.GetCharsInRange(target, 2))
        {
            if (!c.IsPlayer && !c.IsDeleted && !c.IsDead)
                return c;
        }
        return null;
    }

    private Character? FindNearbyChar(Character ch, Point3D target)
    {
        foreach (var c in _world.GetCharsInRange(target, 2))
        {
            if (!c.IsDeleted && !c.IsDead)
                return c;
        }
        return null;
    }
}
