using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Combat;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class SkillDelayTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static void LoadDefinitions(string contents)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_skill_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    [Fact]
    public void SkillEngine_GetSkillDelayMs_ReadsSkillDefDelay()
    {
        LoadDefinitions("""
            [SKILL 17]
            DELAY=110
            """);

        Assert.Equal(11_000, SkillEngine.GetSkillDelayMs(SkillType.Healing));
    }

    [Fact]
    public void StrokeInterval_IsFullSkillDelayPerSourceX()
    {
        // Source-X Skill_Stroke re-arms with the full DELAY curve value
        // (CCharSkill.cpp:3649 SetTimeout(delay)), not a subdivision of it.
        LoadDefinitions("""
            [SKILL 18]
            DELAY=80
            """);

        Assert.Equal(8_000, SkillEngine.GetSkillStrokeIntervalMs(SkillType.Fishing));
        Assert.Equal(SkillEngine.GetSkillDelayMs(SkillType.Fishing),
            SkillEngine.GetSkillStrokeIntervalMs(SkillType.Fishing));
    }

    [Fact]
    public void RollStrokeCount_MatchesSourceXRanges()
    {
        // CCharSkill.cpp — fishing rand(2)+1 (:1568), mining/lumberjack
        // rand(5)+2 (:1463/:1667); non-gather skills get a single period.
        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(SkillEngine.RollStrokeCount(SkillType.Fishing), 1, 2);
            Assert.InRange(SkillEngine.RollStrokeCount(SkillType.Mining), 2, 6);
            Assert.InRange(SkillEngine.RollStrokeCount(SkillType.Lumberjacking), 2, 6);
            Assert.Equal(1, SkillEngine.RollStrokeCount(SkillType.Hiding));
        }
    }

    [Fact]
    public void DelayedActiveSkill_FiresStrokeBeforeSuccessInSourceXOrder()
    {
        LoadDefinitions("""
            [SKILL 21]
            DELAY=1
            """);

        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var client = TestHarness.CreateClient(loggerFactory, world, new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1301);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.SetSkill(SkillType.Hiding, 2000);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var dispatcher = new TriggerDispatcher();
        var order = new List<string>();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillPreStart", (_, args) => { order.Add($"pre:{args.N1}"); return TriggerResult.Default; });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillStart", (_, args) => { order.Add($"start:{args.N1}"); return TriggerResult.Default; });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillStroke", (_, args) => { order.Add($"stroke:{args.N1}:{args.N2}"); return TriggerResult.Default; });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillSuccess", (_, args) => { order.Add($"success:{args.N1}"); return TriggerResult.Default; });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillFail", (_, args) => { order.Add($"fail:{args.N1}"); return TriggerResult.Default; });
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);

        client.HandleUseSkill((int)SkillType.Hiding);
        Assert.True(player.HasActiveSkillPending());

        Thread.Sleep(150);
        client.TickPendingSkill();

        Assert.False(player.HasActiveSkillPending());
        Assert.Equal([
            "pre:21",
            "start:21",
            "stroke:21:1",
            "success:21"
        ], order);
    }

    [Fact]
    public void TargetCancel_FiresSkillTargetCancelAndDoesNotStartDelayedSkill()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var client = TestHarness.CreateClient(loggerFactory, world, new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1302);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        var dispatcher = new TriggerDispatcher();
        long cancelSkill = -1;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillTargetCancel", (_, args) =>
        {
            cancelSkill = args.N1;
            return TriggerResult.Default;
        });
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);

        client.HandleUseSkill((int)SkillType.Healing);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, 0xFFFFFFFF, 0, 0, 0, 0);

        Assert.Equal((int)SkillType.Healing, cancelSkill);
        Assert.False(player.HasActiveSkillPending());
    }

    [Fact]
    public void MovementInterrupt_FiresSkillAbortAndClearsPendingSkill()
    {
        LoadDefinitions("""
            [SKILL 21]
            FLAGS=skf_immobile
            """);

        var oldAbort = Character.ActiveSkillAborted;
        try
        {
            var world = CreateWorld();
            var player = world.CreateCharacter();
            player.IsPlayer = true;
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
            player.BeginSkillPending((int)SkillType.Hiding, Environment.TickCount64 + 10_000,
                Environment.TickCount64 + 1_000, Serial.Invalid, null);

            int aborted = -1;
            Character.ActiveSkillAborted = (_, skillId) => aborted = skillId;
            var movement = new SphereNet.Game.Movement.MovementEngine(world);

            Assert.True(movement.TryMove(player, Direction.East, running: false, sequence: 1));
            Assert.False(player.HasActiveSkillPending());
            Assert.Equal((int)SkillType.Hiding, aborted);
        }
        finally
        {
            Character.ActiveSkillAborted = oldAbort;
        }
    }

    [Fact]
    public void Movement_DoesNotInterruptSkillWithoutImmobileFlag()
    {
        LoadDefinitions("""
            [SKILL 21]
            FLAGS=0
            """);

        var world = CreateWorld();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        player.BeginSkillPending((int)SkillType.Hiding, Environment.TickCount64 + 10_000,
            Environment.TickCount64 + 1_000, Serial.Invalid, null);

        var movement = new SphereNet.Game.Movement.MovementEngine(world);

        Assert.True(movement.TryMove(player, Direction.East, running: false, sequence: 1));
        Assert.True(player.HasActiveSkillPending());
    }

    [Fact]
    public void DamageInterrupt_FiresSkillAbortAndClearsPendingSkill()
    {
        var oldAbort = Character.ActiveSkillAborted;
        var oldFlags = Character.CombatFlags;
        var oldWeaponDef = CombatEngine.WeaponDefLookup;
        try
        {
            Character.CombatFlags = (int)CombatFlags.FirstHitInstant;
            CombatEngine.WeaponDefLookup = _ => (20, 20);

            var loggerFactory = LoggerFactory.Create(_ => { });
            var world = CreateWorld();
            var client = TestHarness.CreateClient(loggerFactory, world, new SphereNet.Game.Accounts.AccountManager(loggerFactory), 1303);

            var attacker = world.CreateCharacter();
            attacker.IsPlayer = true;
            attacker.Str = 100;
            attacker.Stam = 100;
            attacker.SetSkill(SkillType.Swordsmanship, 1200);
            attacker.SetSkill(SkillType.Tactics, 1200);
            attacker.SetStatFlag(StatFlag.War);
            world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
            TestHarness.AttachCharacter(client, attacker);

            var weapon = world.CreateItem();
            weapon.ItemType = ItemType.WeaponSword;
            weapon.BaseId = 0x0F5E;
            attacker.Equip(weapon, Layer.OneHanded);

            var target = world.CreateCharacter();
            target.IsPlayer = true;
            target.Hits = target.MaxHits = 100;
            target.Stam = 100;
            target.SetSkill(SkillType.Wrestling, 0);
            target.BeginSkillPending((int)SkillType.Healing, Environment.TickCount64 + 10_000,
                Environment.TickCount64 + 1_000, Serial.Invalid, null);
            world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

            int aborted = -1;
            Character.ActiveSkillAborted = (_, skillId) => aborted = skillId;
            attacker.FightTarget = target.Uid;
            attacker.NextAttackTime = 0;

            // Combat uses Random.Shared (non-seedable), so a single swing may miss.
            // Loop enough swings that landing at least one damaging hit is effectively
            // certain — the test asserts the interrupt, not a per-swing hit rate.
            for (int i = 0; i < 80 && target.HasActiveSkillPending(); i++)
            {
                attacker.NextAttackTime = 0;
                attacker.SetCombatSwingState(SwingState.Ready);
                client.TickCombat();
            }

            Assert.False(target.HasActiveSkillPending());
            Assert.Equal((int)SkillType.Healing, aborted);
        }
        finally
        {
            Character.ActiveSkillAborted = oldAbort;
            Character.CombatFlags = oldFlags;
            CombatEngine.WeaponDefLookup = oldWeaponDef;
        }
    }

    [Fact]
    public void Character_ClearActiveSkillPending_ClearsPendingState()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.BeginSkillPending(17, 99999, 1000, Serial.Invalid, null);

        int id = ch.ClearActiveSkillPending();
        Assert.Equal(17, id);
        Assert.False(ch.HasActiveSkillPending());
    }

    [Fact]
    public void ActiveSkillEngine_Peacemaking_ClearsNpcWarMode()
    {
        var world = CreateWorld();
        var bard = world.CreateCharacter();
        bard.SetSkill(SkillType.Musicianship, 900);
        bard.SetSkill(SkillType.Peacemaking, 900);
        // The reference success curve tops out below certainty; GM
        // auto-success keeps the music + peace rolls deterministic here.
        bard.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(bard, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        bard.Backpack = pack;
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        pack.AddItem(lute);

        var wolf = world.CreateCharacter();
        wolf.NpcBrain = NpcBrainType.Animal;
        wolf.SetStatFlag(StatFlag.War);
        wolf.FightTarget = bard.Uid;
        world.PlaceCharacter(wolf, new Point3D(101, 100, 0, 0));

        var sink = new RecordingSkillSink(bard, world);
        Assert.True(ActiveSkillEngine.Peacemaking(sink, wolf));
        Assert.False(wolf.IsStatFlag(StatFlag.War));
        Assert.False(wolf.FightTarget.IsValid);
    }

    [Fact]
    public void SpellEngine_Polymorph_RestoresBodyOnExpiry()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Polymorph,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 5,
            DurationScale = 5,
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

        engine.ProcessExpirations(Environment.TickCount64 + 60_000);
        Assert.Equal(0x0190, caster.BodyId);
    }

    private sealed class RecordingSkillSink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public Random Random { get; } = new(1);
        public GameWorld World { get; } = world;
        public void SysMessage(string text) { }
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public SphereNet.Game.Objects.Items.Item? FindBackpackItem(ItemType type)
        {
            var pack = Self.Backpack;
            if (pack == null) return null;
            foreach (var it in pack.Contents)
                if (it.ItemType == type) return it;
            return null;
        }
        public void ConsumeAmount(SphereNet.Game.Objects.Items.Item item, ushort amount = 1) { }
        public void DeliverItem(SphereNet.Game.Objects.Items.Item item) { }
    }
}
