using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;

namespace SphereNet.Tests;

[Collection("GlobalConfigSerial")]
public class SkillMagicPhase4Tests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ActiveSkillEngine_Stealth_SetsStepStealthBudget()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        // The success die in CheckSuccess rolls against Random.Shared, which is
        // not seedable per-test, so a high skill alone still fails on the curve's
        // tail ~2% of runs. This test only cares about the success-branch budget,
        // so force a deterministic success via GM auto-pass; StepStealth still
        // derives purely from the stored skill value (the step budget itself is capped).
        ch.PrivLevel = PrivLevel.GM;
        ch.SetSkill(SkillType.Stealth, 3000);
        ch.SetStatFlag(StatFlag.Hidden);
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var sink = new RecordingSkillSink(ch, world);
        Assert.True(ActiveSkillEngine.Stealth(sink));
        Assert.Equal(10, ch.StepStealth);
        Assert.True(ch.IsStatFlag(StatFlag.Hidden));
        Assert.False(ch.IsStatFlag(StatFlag.Invisible));
    }

    [Fact]
    public void MovementEngine_StealthStep_DecrementsAndReveals()
    {
        var world = CreateWorld();
        var move = new MovementEngine(world);
        var ch = world.CreateCharacter();
        ch.SetStatFlag(StatFlag.Hidden);
        ch.SetStatFlag(StatFlag.Invisible);
        ch.StepStealth = 1;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        Assert.True(move.TryMove(ch, Direction.East, running: false, sequence: 1));
        Assert.Equal(0, ch.StepStealth);
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void SpellEngine_GateBothSides_OpensReturnGate()
    {
        var world = CreateWorld();
        Character.MagicFlags = (int)MagicConfigFlags.GateBothSides;
        try
        {
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.GateTravel,
                ManaCost = 0,
                CastTimeBase = 1,
            });

            var caster = world.CreateCharacter();
            caster.MaxMana = 100;
            caster.Mana = 100;
            caster.SetSkill(SkillType.Magery, 2000);
            world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

            var rune = world.CreateItem();
            rune.SetRuneMark(new Point3D(200, 210, 5, 0));
            world.PlaceItem(rune, new Point3D(101, 100, 0, 0));

            var engine = new SpellEngine(world, registry);
            Assert.True(engine.CastStart(caster, SpellType.GateTravel, rune.Uid, rune.Position) > 0);
            Assert.True(engine.CastDone(caster));

            int gateCount = 0;
            foreach (var item in world.GetItemsInRange(new Point3D(100, 100, 0, 0), 2))
                if (item.ItemType == ItemType.Moongate) gateCount++;
            foreach (var item in world.GetItemsInRange(new Point3D(200, 210, 5, 0), 2))
                if (item.ItemType == ItemType.Moongate) gateCount++;

            Assert.Equal(2, gateCount);
        }
        finally
        {
            Character.MagicFlags = 0;
        }
    }

    [Fact]
    public void SpellEngine_OutdoorSpell_BlockedInUndergroundRegion()
    {
        var world = CreateWorld();
        var dungeon = new Region { Name = "dungeon_test" };
        dungeon.Flags = RegionFlag.Underground;
        dungeon.AddRect(90, 90, 110, 110);
        world.AddRegion(dungeon);

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Flamestrike,
            Flags = SpellFlag.TargChar | SpellFlag.Damage,
            ManaCost = 0,
            CastTimeBase = 1,
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = 100;
        caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.Equal(-1, engine.CastStart(caster, SpellType.Flamestrike, target.Uid, target.Position));

        Character.MagicFlags = (int)MagicConfigFlags.DungeonOutdoorSpells;
        try
        {
            Assert.True(engine.CastStart(caster, SpellType.Flamestrike, target.Uid, target.Position) > 0);
        }
        finally
        {
            Character.MagicFlags = 0;
        }
    }

    [Fact]
    public void SpellEngine_RevertPolymorphOnDeath_RestoresBodyWhenFlagSet()
    {
        var world = CreateWorld();
        Character.MagicFlags = (int)MagicConfigFlags.PolymorphRevertDeath;
        try
        {
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Polymorph,
                ManaCost = 0,
                CastTimeBase = 1,
                DurationBase = 300,
            });

            var caster = world.CreateCharacter();
            caster.MaxMana = 100;
            caster.Mana = 100;
            caster.SetSkill(SkillType.Magery, 2000);
            caster.BodyId = 0x0190;
            world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

            var engine = new SpellEngine(world, registry);
            Assert.True(engine.CastStart(caster, SpellType.Polymorph, caster.Uid, caster.Position) > 0);
            Assert.True(engine.CastDone(caster));
            Assert.NotEqual(0x0190, caster.BodyId);

            engine.RevertPolymorphOnDeath(caster);
            Assert.Equal(0x0190, caster.BodyId);
            Assert.False(caster.IsStatFlag(StatFlag.Polymorph));
        }
        finally
        {
            Character.MagicFlags = 0;
        }
    }

    [Fact]
    public void ActiveSkillEngine_RepairItem_RestoresHits()
    {
        var world = CreateWorld();
        var smith = world.CreateCharacter();
        smith.SetSkill(SkillType.Tinkering, 2000);
        // Arms Lore identifies the piece before any work begins, and the work needs
        // an anvil within two tiles (Source-X Use_Repair, CCharUse.cpp:764/781).
        smith.SetSkill(SkillType.ArmsLore, 1000);
        world.PlaceCharacter(smith, new Point3D(100, 100, 0, 0));

        var anvil = world.CreateItem();
        anvil.ItemType = ItemType.Anvil;
        world.PlaceItem(anvil, new Point3D(100, 100, 0, 0));

        var item = world.CreateItem();
        item.BaseId = 0x13BB;
        item.SetTag("HITSMAX", "50");
        item.SetTag("HITS", "10");
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));

        var sink = new RecordingSkillSink(smith, world);
        Assert.True(ActiveSkillEngine.RepairItem(sink, item));
        Assert.True(item.GetHitsCur() > 10);
    }

    private sealed class RecordingSkillSink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public Random Random { get; } = new(7);
        public GameWorld World { get; } = world;
        public void SysMessage(string text) { }
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }
}
