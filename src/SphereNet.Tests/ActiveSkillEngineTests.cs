using System;
using System.Collections.Generic;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Active-skill (Healing/Hiding/Meditation/Stealing/Snooping/Lockpicking/...)
/// parity verifications. Recording sink captures every Source-X DEFMSG_*
/// emission so we can assert the exact channel + key sequence each branch
/// produces, without standing up the network stack.
/// </summary>
public class ActiveSkillEngineTests
{
    private sealed class RecordingActiveSink : IActiveSkillSink
    {
        public Character Self { get; }
        public Random Random { get; }
        public GameWorld World { get; }
        public List<(string Channel, string Text)> Log { get; } = new();
        public List<ushort> Sounds { get; } = new();
        public List<(Item Item, ushort Amount)> Consumed { get; } = new();
        public List<Item> Delivered { get; } = new();
        public Dictionary<ItemType, Item> Pack { get; } = new();

        public RecordingActiveSink(Character self, GameWorld world, int seed = 12345)
        {
            Self = self; World = world; Random = new Random(seed);
        }

        public void SysMessage(string text) => Log.Add(("SYS", text));
        public void ObjectMessage(ObjBase target, string text) => Log.Add(("OBJ", text));
        public void Emote(string text) => Log.Add(("EMOTE", text));
        public void Sound(ushort soundId) => Sounds.Add(soundId);
        public Item? FindBackpackItem(ItemType type) => Pack.TryGetValue(type, out var i) ? i : null;
        public void ConsumeAmount(Item item, ushort amount = 1) => Consumed.Add((item, amount));
        public void DeliverItem(Item item) => Delivered.Add(item);
    }

    private static GameWorld MakeWorld() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    private static Character MakeChar(string name = "Hero", bool player = true,
        NpcBrainType brain = NpcBrainType.Human, ushort body = 0x0190, short hits = 50, short maxHits = 50,
        short mana = 5, short maxMana = 50)
    {
        // MaxHits/MaxMana must be set before Hits/Mana because the setters clamp.
        return new Character
        {
            Name = name, IsPlayer = player, NpcBrain = brain, BodyId = body,
            MaxHits = maxHits, Hits = hits, MaxMana = maxMana, Mana = mana,
            Position = new Point3D(100, 100, 0, 0),
        };
    }

    [Fact]
    public void Hiding_LightCarried_RejectsWithToolitMessage()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        ch.SetTag("LIGHT_CARRIED", "1");
        var sink = new RecordingActiveSink(ch, world);

        bool ok = ActiveSkillEngine.Hiding(sink);

        Assert.False(ok);
        Assert.Single(sink.Log);
        Assert.Equal(ServerMessages.Get(Msg.HidingToolit), sink.Log[0].Text);
    }

    [Fact]
    public void Meditation_AlreadyAtMaxMana_PrintsPeace1AndExits()
    {
        var world = MakeWorld();
        var ch = MakeChar(mana: 50, maxMana: 50);
        var sink = new RecordingActiveSink(ch, world);

        bool ok = ActiveSkillEngine.Meditation(sink);

        Assert.False(ok);
        Assert.Single(sink.Log);
        Assert.Equal(ServerMessages.Get(Msg.MeditationPeace1), sink.Log[0].Text);
    }

    [Fact]
    public void Meditation_BelowMax_EmitsTryAndAttempts()
    {
        var world = MakeWorld();
        var ch = MakeChar(mana: 5, maxMana: 50);
        ch.SetSkill(SkillType.Meditation, (ushort)800);
        var sink = new RecordingActiveSink(ch, world);

        ActiveSkillEngine.Meditation(sink);

        Assert.Equal(ServerMessages.Get(Msg.MeditationTry), sink.Log[0].Text);
    }

    [Fact]
    public void SpiritSpeak_AlreadyActive_NoOp()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        ch.SetStatFlag(StatFlag.SpiritSpeak);
        var sink = new RecordingActiveSink(ch, world);

        bool ok = ActiveSkillEngine.SpiritSpeak(sink);

        Assert.False(ok);
        Assert.Empty(sink.Log);
    }

    [Fact]
    public void Healing_NoBandage_RejectsWithNoaids()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world);

        bool ok = ActiveSkillEngine.Healing(sink, null);

        Assert.False(ok);
        Assert.Equal(ServerMessages.Get(Msg.HealingNoaids), sink.Log[0].Text);
    }

    [Fact]
    public void Healing_HealthyTarget_PrintsHealthyMessage()
    {
        var world = MakeWorld();
        var ch = MakeChar(hits: 50, maxHits: 50);
        var sink = new RecordingActiveSink(ch, world);
        sink.Pack[ItemType.Bandage] = new Item { ItemType = ItemType.Bandage, Amount = 5 };

        bool ok = ActiveSkillEngine.Healing(sink, ch);

        Assert.False(ok);
        Assert.Equal(ServerMessages.Get(Msg.HealingHealthy), sink.Log[0].Text);
    }

    [Fact]
    public void Healing_WoundedSelf_EmitsSelfEmoteAndConsumesBandage()
    {
        var world = MakeWorld();
        var ch = MakeChar(hits: 10, maxHits: 50);
        ch.SetSkill(SkillType.Healing, (ushort)800);
        var sink = new RecordingActiveSink(ch, world);
        var bandage = new Item { ItemType = ItemType.Bandage, Amount = 3 };
        sink.Pack[ItemType.Bandage] = bandage;

        ActiveSkillEngine.Healing(sink, ch);

        Assert.Contains(sink.Log, l => l.Channel == "EMOTE" && l.Text == ServerMessages.Get(Msg.HealingSelf));
        Assert.Single(sink.Consumed);
        Assert.Same(bandage, sink.Consumed[0].Item);
    }

    [Fact]
    public void Stealing_NullTarget_PrintsNothing()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world);

        bool ok = ActiveSkillEngine.Stealing(sink, null);

        Assert.False(ok);
        Assert.Equal(ServerMessages.Get(Msg.StealingNothing), sink.Log[0].Text);
    }

    [Fact]
    public void Snooping_NonContainer_PrintsCant()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world);
        var sword = new Item { ItemType = ItemType.WeaponSword };

        bool ok = ActiveSkillEngine.Snooping(sink, sword);

        Assert.False(ok);
        Assert.Equal(ServerMessages.Get(Msg.SnoopingCant), sink.Log[0].Text);
    }

    [Fact]
    public void Lockpicking_NoLockpick_RejectsWithNopick()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world);
        var chest = new Item { ItemType = ItemType.ContainerLocked };

        bool ok = ActiveSkillEngine.Lockpicking(sink, chest);

        Assert.False(ok);
        Assert.Equal(ServerMessages.Get(Msg.LockpickingNopick), sink.Log[0].Text);
    }

    [Fact]
    public void RemoveTrap_NonTrap_RejectsWithWitem()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world);
        var clothing = new Item { ItemType = ItemType.Clothing };

        bool ok = ActiveSkillEngine.RemoveTrap(sink, clothing);

        Assert.False(ok);
        Assert.Equal(ServerMessages.Get(Msg.RemovetrapsWitem), sink.Log[0].Text);
    }

    [Fact]
    public void Taming_HumanTarget_RejectsWithCant()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var human = MakeChar("NPC", player: false, brain: NpcBrainType.Human);
        var sink = new RecordingActiveSink(ch, world);

        bool ok = ActiveSkillEngine.Taming(sink, human);

        Assert.False(ok);
        Assert.Equal(ServerMessages.Get(Msg.TamingCant), sink.Log[0].Text);
    }

    [Fact]
    public void Taming_AlreadyPet_PrintsTamedAlready()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var pet = MakeChar("Rex", player: false, brain: NpcBrainType.Animal);
        pet.SetStatFlag(StatFlag.Pet);
        var sink = new RecordingActiveSink(ch, world);

        bool ok = ActiveSkillEngine.Taming(sink, pet);

        Assert.False(ok);
        Assert.Contains("Rex", sink.Log[0].Text);
    }

    [Fact]
    public void Poisoning_NoPotion_PrintsSelect1()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var sink = new RecordingActiveSink(ch, world);
        var blade = new Item { ItemType = ItemType.WeaponMaceSharp };

        bool ok = ActiveSkillEngine.Poisoning(sink, blade);

        Assert.False(ok);
        Assert.Equal(ServerMessages.Get(Msg.PoisoningSelect1), sink.Log[0].Text);
    }

    [Fact]
    public void Tracking_NoEntities_EmitsFailHuman()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        ch.SetSkill(SkillType.Tracking, (ushort)900);
        var sink = new RecordingActiveSink(ch, world);

        bool ok = ActiveSkillEngine.Tracking(sink, ActiveSkillEngine.TrackingCategory.Humans);

        Assert.False(ok);
        Assert.Contains(sink.Log, l => l.Channel == "SYS");
    }

    [Fact]
    public void Herding_PetTarget_PrintsPlayer()
    {
        var world = MakeWorld();
        var ch = MakeChar();
        var pet = MakeChar("Bessie", player: false, brain: NpcBrainType.Animal);
        pet.SetStatFlag(StatFlag.Pet);
        var sink = new RecordingActiveSink(ch, world);
        sink.Pack[ItemType.WeaponMaceCrook] = new Item { ItemType = ItemType.WeaponMaceCrook };

        bool ok = ActiveSkillEngine.Herding(sink, pet, new Point3D(105, 105, 0, 0));

        Assert.False(ok);
        Assert.Contains("Bessie", sink.Log[0].Text);
    }
}
