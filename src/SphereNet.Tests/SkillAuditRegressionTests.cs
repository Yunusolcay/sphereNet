using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public class SkillAuditRegressionTests
{
    private sealed class ActiveSink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(1);
        public Item? Bandage { get; set; }
        public int Consumed { get; private set; }
        public List<string> Messages { get; } = [];

        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Messages.Add(text);
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => type == ItemType.Bandage ? Bandage : null;
        public void ConsumeAmount(Item item, ushort amount = 1) => Consumed += amount;
        public void DeliverItem(Item item) { }
    }

    private sealed class InfoSink(Character self) : IInfoSkillSink
    {
        public Character Self { get; } = self;
        public Random Random { get; } = new(1);
        public List<string> Messages { get; } = [];
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Messages.Add(text);
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        world.InitMap(1, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static void LoadDefinitions(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_skill_audit_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, contents);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(path) ?? ""
        };
        resources.LoadResourceFile(path);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    [Fact]
    public void SkillBounds_RejectReservedSlotsAndClampLocks()
    {
        var ch = new Character();

        ch.SetSkill((SkillType)(-2), 500);
        ch.SetSkill((SkillType)58, 500);
        ch.SetSkillLock(SkillType.Hiding, 99);

        Assert.Equal(0, ch.GetSkill((SkillType)(-2)));
        Assert.False(SkillEngine.IsValidBaseSkill((SkillType)58));
        Assert.False(SkillEngine.UseQuick(ch, (SkillType)58, 0));
        Assert.Equal(2, ch.GetSkillLock(SkillType.Hiding));
    }

    [Fact]
    public void DisabledAndPassiveSkills_CannotBeStartedFromUseSkill()
    {
        LoadDefinitions("""
            [SKILL 21]
            FLAGS=skf_disabled|skf_selectable
            [SKILL 50]
            FLAGS=0
            """);

        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 3101);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        int selected = 0;
        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillSelect", (_, _) =>
        {
            selected++;
            return TriggerResult.Default;
        });
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);

        client.HandleUseSkill((int)SkillType.Hiding);
        client.HandleUseSkill((int)SkillType.Focus);

        Assert.Equal(0, selected);
        Assert.False(ch.HasActiveSkillPending());
    }

    [Fact]
    public void ScriptedFlag_OverridesBuiltInHandlerWithoutDuplicatingTriggerStages()
    {
        LoadDefinitions("""
            [SKILL 21]
            FLAGS=skf_scripted|skf_selectable
            """);

        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 3104);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        int selected = 0, preStarted = 0, started = 0, succeeded = 0, scripted = 0;
        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillSelect", (_, _) => { selected++; return TriggerResult.Default; });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillPreStart", (_, _) => { preStarted++; return TriggerResult.Default; });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillStart", (_, _) => { started++; return TriggerResult.Default; });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillSuccess", (_, _) => { succeeded++; return TriggerResult.Default; });
        SkillHandlers.OnScriptedSkillUse = (_, skill) =>
        {
            Assert.Equal(SkillType.Hiding, skill);
            scripted++;
            return true;
        };
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);

        client.HandleUseSkill((int)SkillType.Hiding);

        Assert.Equal(1, selected);
        Assert.Equal(1, preStarted);
        Assert.Equal(1, started);
        Assert.Equal(1, scripted);
        Assert.Equal(1, succeeded);
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void SkillWait_BlocksNewSkillWhileTargetCursorIsOpen()
    {
        LoadDefinitions("""
            [SKILL 17]
            FLAGS=0
            [SKILL 21]
            FLAGS=0
            """);

        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 3105);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, ch);
        long waitCurrent = -1, targetCancelled = -1;
        var dispatcher = new SphereNet.Game.Scripting.TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillWait", (_, args) =>
        {
            waitCurrent = args.N2;
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SkillTargetCancel", (_, args) =>
        {
            targetCancelled = args.N1;
            return TriggerResult.Default;
        });
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: dispatcher);

        client.HandleUseSkill((int)SkillType.Healing);
        Assert.True(client.HasPendingTarget);
        client.HandleUseSkill((int)SkillType.Hiding);

        Assert.Equal((int)SkillType.Healing, waitCurrent);
        Assert.Equal(-1, targetCancelled);
        Assert.True(client.HasPendingTarget);
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void InformationSkill_FailedRollDoesNotLeakResultText()
    {
        LoadDefinitions("""
            [SKILL 1]
            FLAGS=skf_selectable
            RANGE=3
            """);

        var world = CreateWorld();
        var examiner = world.CreateCharacter();
        var target = world.CreateCharacter();
        world.PlaceCharacter(examiner, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var sink = new InfoSink(examiner);
        Character.OnSkillUseQuickDetailed =
            (Character _, int _, ref int _, int _) => -1;

        bool result = new SkillHandlers(world).UseInfoSkill(sink, SkillType.Anatomy, target);

        Assert.False(result);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public void Forensics_ResolvesDecimalKillerUid()
    {
        LoadDefinitions("""
            [SKILL 19]
            FLAGS=skf_selectable
            RANGE=3
            """);

        var world = CreateWorld();
        var examiner = world.CreateCharacter();
        examiner.SetSkill(SkillType.Forensics, 1000);
        world.PlaceCharacter(examiner, new Point3D(100, 100, 0, 0));
        Character killer = null!;
        for (int i = 0; i < 20; i++)
            killer = world.CreateCharacter();
        killer.Name = "The Murderer";
        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.Name = "a corpse";
        corpse.SetTag("KILLER_UID", killer.Uid.Value.ToString());
        corpse.SetTag("DEATH_TIME", (Environment.TickCount64 - 5_000).ToString());
        world.PlaceItem(corpse, new Point3D(101, 100, 0, 0));
        var sink = new InfoSink(examiner);
        Character.OnSkillUseQuickDetailed =
            (Character _, int _, ref int _, int _) => 1;

        Assert.True(new SkillHandlers(world).UseInfoSkill(sink, SkillType.Forensics, corpse));
        Assert.Contains(sink.Messages, message => message.Contains("The Murderer", StringComparison.Ordinal));
    }

    [Fact]
    public void SkillCapAboveStorageWidth_DoesNotWrapAtUShortMax()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Hiding, ushort.MaxValue);
        SkillEngine.SkillMaxOverrides[SkillType.Hiding] = 100_000;

        SkillEngine.GainExperience(ch, SkillType.Hiding, 100);

        Assert.Equal(ushort.MaxValue, ch.GetSkill(SkillType.Hiding));
        Assert.Equal(ushort.MaxValue, SkillEngine.GetSkillMax(ch, SkillType.Hiding));
    }

    [Fact]
    public void Veterinary_UsesVeterinaryRollAndRejectsCrossMapTargetsBeforeConsumption()
    {
        LoadDefinitions("""
            [SKILL 39]
            FLAGS=skf_selectable
            RANGE=2
            """);

        var world = CreateWorld();
        var healer = world.CreateCharacter();
        healer.PrivLevel = PrivLevel.GM;
        healer.SetSkill(SkillType.Veterinary, 1000);
        healer.SetSkill(SkillType.AnimalLore, 1000);
        world.PlaceCharacter(healer, new Point3D(100, 100, 0, 0));
        var animal = world.CreateCharacter();
        animal.NpcBrain = NpcBrainType.Animal;
        animal.MaxHits = 100;
        animal.Hits = 20;
        world.PlaceCharacter(animal, new Point3D(101, 100, 0, 0));
        var sink = new ActiveSink(healer, world) { Bandage = world.CreateItem() };
        int rolledSkill = -1;
        Character.OnSkillUseQuickDetailed =
            (Character _, int skill, ref int _, int _) => { rolledSkill = skill; return 1; };

        Assert.True(ActiveSkillEngine.Healing(sink, animal, SkillType.Veterinary));
        Assert.Equal((int)SkillType.Veterinary, rolledSkill);
        Assert.Equal(1, sink.Consumed);

        world.MoveCharacter(animal, new Point3D(101, 100, 0, 1));
        rolledSkill = -1;
        int consumed = sink.Consumed;

        Assert.False(ActiveSkillEngine.Healing(sink, animal, SkillType.Veterinary));
        Assert.Equal(-1, rolledSkill);
        Assert.Equal(consumed, sink.Consumed);
    }

    [Fact]
    public void Crafting_RequiresOnePrimaryHueAndPersistsQuality()
    {
        var world = CreateWorld();
        var engine = new CraftingEngine(world);
        var crafter = world.CreateCharacter();
        crafter.PrivLevel = PrivLevel.GM;
        crafter.SetSkill(SkillType.Blacksmithing, 2000);
        world.PlaceCharacter(crafter, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        crafter.Equip(pack, Layer.Pack);
        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));
        var red = world.CreateItem();
        red.BaseId = 0x1BF2;
        red.Hue = new Color(0x0021);
        red.Amount = 5;
        pack.AddItem(red);
        var blue = world.CreateItem();
        blue.BaseId = 0x1BF2;
        blue.Hue = new Color(0x0055);
        blue.Amount = 5;
        pack.AddItem(blue);
        var recipe = new CraftRecipe
        {
            ResultItemId = 0x13B9,
            ResultName = "sword",
            PrimarySkill = SkillType.Blacksmithing,
            Difficulty = 0,
        };
        recipe.Resources.Add(new CraftResource { ItemId = 0x1BF2, Amount = 10 });

        Assert.False(engine.CanCraft(crafter, recipe));

        red.Amount = 10;
        var crafted = engine.TryCraft(crafter, recipe);

        Assert.NotNull(crafted);
        Assert.Equal(red.Hue, crafted!.Hue);
        Assert.Equal(crafter.Uid, crafted.Crafter);
        // Source-X band table: a GM smith lands in the superior band (176-200),
        // shiftable down two bands by the log variance → 126 floor.
        Assert.InRange((int)crafted.Quality, 126, 200);
        Assert.Equal(5, blue.Amount);
    }

    [Fact]
    public void FullContainer_ReportsFailureWithoutOrphaningItem()
    {
        var world = CreateWorld();
        var container = world.CreateItem();
        container.ItemType = ItemType.Container;
        for (int i = 0; i < Item.MaxContainerItems; i++)
            Assert.True(container.TryAddItem(world.CreateItem()));
        var overflow = world.CreateItem();

        Assert.False(container.TryAddItem(overflow));
        Assert.False(overflow.ContainedIn.IsValid);
        Assert.DoesNotContain(overflow, container.Contents);
    }

    [Fact]
    public void CampingSafeLogout_SkipsLingerWhileUnsafeLogoutExpiresNormally()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        GameClient.ClientLingerSeconds = 60;

        GameClient MakeClient(Character ch, int id, Action onDelete)
        {
            var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), id);
            client.BroadcastNearby = (_, _, _, _) => onDelete();
            TestHarness.AttachCharacter(client, ch);
            ch.IsOnline = true;
            world.AddOnlinePlayer(ch);
            return client;
        }

        var unsafeChar = world.CreateCharacter();
        unsafeChar.IsPlayer = true;
        world.PlaceCharacter(unsafeChar, new Point3D(100, 100, 0, 0));
        int unsafeDeletes = 0;
        var unsafeClient = MakeClient(unsafeChar, 3102, () => unsafeDeletes++);

        unsafeClient.OnDisconnect();

        Assert.True(unsafeChar.IsClientLingering);
        Assert.Contains(unsafeChar, world.OnlinePlayers);
        Assert.Equal(0, unsafeDeletes);

        var oldSector = world.GetSector(unsafeChar.Position)!;
        world.MoveCharacter(unsafeChar, new Point3D(200, 100, 0, 0));
        var lingerSector = world.GetSector(unsafeChar.Position)!;
        Assert.DoesNotContain(unsafeChar, oldSector.OnlinePlayers);
        Assert.Contains(unsafeChar, lingerSector.OnlinePlayers);

        bool expired = false;
        world.ClientLingerExpired += ch => expired |= ch == unsafeChar;
        unsafeChar.SetTag("CLIENT_LINGER_UNTIL",
            (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1).ToString());
        world.OnTick();

        Assert.True(expired);
        Assert.False(unsafeChar.IsClientLingering);
        Assert.DoesNotContain(unsafeChar, world.OnlinePlayers);
        Assert.DoesNotContain(unsafeChar, lingerSector.OnlinePlayers);

        var safeChar = world.CreateCharacter();
        safeChar.IsPlayer = true;
        world.PlaceCharacter(safeChar, new Point3D(110, 100, 0, 0));
        var bedroll = world.CreateItem();
        bedroll.ItemType = ItemType.Bedroll;
        safeChar.Act = bedroll.Uid;
        var campfire = world.CreateItem();
        campfire.ItemType = ItemType.Campfire;
        campfire.SetTag("CAMPFIRE_OWNER_UUID", safeChar.Uuid.ToString("D"));
        world.PlaceItem(campfire, new Point3D(111, 100, 0, 0));
        Assert.True(new SkillHandlers(world).UseSkill(safeChar, SkillType.Camping));
        Assert.True(safeChar.TryGetTag("CAMPING_SAFE_LOGOUT_UNTIL", out string? safeUntilText) &&
            long.TryParse(safeUntilText, out long safeUntil) &&
            safeUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        int safeDeletes = 0;
        var safeClient = MakeClient(safeChar, 3103, () => safeDeletes++);

        safeClient.OnDisconnect();

        Assert.False(safeChar.IsClientLingering);
        Assert.DoesNotContain(safeChar, world.OnlinePlayers);
        Assert.Equal(1, safeDeletes);
    }
}
