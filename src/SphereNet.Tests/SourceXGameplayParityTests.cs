using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Components;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;

namespace SphereNet.Tests;

public class SourceXGameplayParityTests
{
    [Fact]
    public void DoubleClick_ItemOnAnotherFacet_IsRejectedBeforeDClickTrigger()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        world.InitMap(1, 6144, 4096);
        var client = CreatePlayingClient(loggerFactory, world, out var state, out var player);

        var item = world.CreateItem();
        item.Name = "remote item";
        world.PlaceItem(item, new Point3D(100, 100, 0, 1));

        int dclicks = 0;
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "DClick", (_, _) =>
        {
            dclicks++;
            return TriggerResult.Default;
        });
        client.SetEngines(triggerDispatcher: dispatcher);

        client.HandleDoubleClick(item.Uid.Value);

        Assert.Equal(0, dclicks);
        Assert.Contains(TestHarness.GetQueuedPackets(state), p =>
            p.Span.Length == 5 && p.Span[0] == 0x1D && ReadU32(p.Span, 1) == item.Uid.Value);
        Assert.Equal((byte)0, player.MapIndex);
    }

    [Fact]
    public void DoubleClick_GmCanOpenNonHumanNpcBackpackAndSeeLoot()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out var state, out var player);
        player.PrivLevel = PrivLevel.GM;

        var npc = world.CreateCharacter();
        npc.IsPlayer = false;
        npc.NpcBrain = NpcBrainType.Monster;
        npc.BodyId = 0x0001;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        npc.Equip(pack, Layer.Pack);
        var loot = world.CreateItem();
        loot.Name = "loot";
        pack.AddItem(loot);

        client.HandleDoubleClick(npc.Uid.Value);

        var packets = TestHarness.GetQueuedPackets(state).ToList();
        Assert.Contains(packets, p => p.Span.Length >= 7 && p.Span[0] == 0x24 &&
            ReadU32(p.Span, 1) == pack.Uid.Value);
        // Contents now ship as a single 0x3C batch (Source-X PacketItemContents),
        // not one 0x25 per child: opcode(1)+len(2)+count(2), then the first item
        // serial at offset 5.
        Assert.Contains(packets, p => p.Span.Length >= 9 && p.Span[0] == 0x3C &&
            ReadU32(p.Span, 5) == loot.Uid.Value);
    }

    [Fact]
    public void DoubleClick_PackAnimalBackpackIsAvailableToNormalPlayer()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out var state, out _);

        var packLlama = world.CreateCharacter();
        packLlama.IsPlayer = false;
        packLlama.NpcBrain = NpcBrainType.Animal;
        packLlama.BodyId = 0x0124;
        world.PlaceCharacter(packLlama, new Point3D(101, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        packLlama.Equip(pack, Layer.Pack);

        client.HandleDoubleClick(packLlama.Uid.Value);

        Assert.Contains(TestHarness.GetQueuedPackets(state), p =>
            p.Span.Length >= 7 && p.Span[0] == 0x24 && ReadU32(p.Span, 1) == pack.Uid.Value);
    }

    [Fact]
    public void SpawnCharDoubleClick_TogglesSpawnAndDelete()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out _, out var player);
        player.PrivLevel = PrivLevel.GM;

        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnChar;
        world.PlaceItem(spawner, player.Position);
        spawner.SpawnChar = new SpawnComponent(spawner, world)
        {
            CharDefId = 0x0001,
            SpawnRange = 0,
            MaxCount = 1,
        };
        int delObjTriggers = 0;
        SpawnComponent.OnSpawnTrigger = (_, trigger, _) =>
        {
            if (trigger == ItemTrigger.DelObj) delObjTriggers++;
            return TriggerResult.Default;
        };

        client.HandleDoubleClick(spawner.Uid.Value);
        Assert.Equal(1, spawner.SpawnChar.CurrentCount);
        var spawned = Assert.Single(spawner.SpawnChar.SpawnedUids);
        Assert.NotNull(world.FindChar(spawned));

        client.HandleDoubleClick(spawner.Uid.Value);
        Assert.Equal(0, spawner.SpawnChar.CurrentCount);
        Assert.Null(world.FindChar(spawned));
        Assert.Equal(1, delObjTriggers);
    }

    [Fact]
    public void SpawnedItemDoubleClick_DetachesItFromSpawnerWithoutDeletingIt()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out _, out var player);

        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnItem;
        world.PlaceItem(spawner, player.Position);
        spawner.SpawnItem = new ItemSpawnComponent(spawner, world)
        {
            ItemDefId = 0x0EED,
            SpawnRange = 0,
            MaxCount = 1,
        };
        spawner.SpawnItem.RespawnNow();
        Assert.Equal(1, spawner.SpawnItem.CurrentCount);

        var spawned = Assert.Single(world.GetItemsInRange(player.Position, 0),
            i => i != spawner && i.TryGetTag("SPAWN_POINT_UUID", out _));
        client.HandleDoubleClick(spawned.Uid.Value);

        Assert.Equal(0, spawner.SpawnItem.CurrentCount);
        Assert.False(spawned.IsDeleted);
        Assert.False(spawned.TryGetTag("SPAWN_POINT_UUID", out _));
    }

    [Fact]
    public void FeatureFlags_AreTranslatedFromSourceXCapabilityMasks()
    {
        Assert.Equal(0x004190FFu,
            GameClient.BuildFeatureFlags(ResDisplayVersion.TOL, maxChars: 7));
        Assert.Equal(0x000051E8u,
            GameClient.BuildCharacterListFlags(ResDisplayVersion.TOL, maxChars: 7,
                tooltipsEnabled: true));

        Assert.Equal(ResDisplayVersion.T2A,
            GameClient.DetectResDisplay(30_007_001, ClientEra.Sphere56x));
        Assert.Equal(ResDisplayVersion.LBR,
            GameClient.DetectResDisplay(30_007_002, ClientEra.Sphere56x));
        Assert.Equal(ResDisplayVersion.TOL,
            GameClient.DetectResDisplay(70_045_065, ClientEra.Sphere56x));
    }

    [Fact]
    public void CharacterCreation_RespectsEffectiveAccountSlotLimit()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(loggerFactory, 19000);
        var accounts = new AccountManager(loggerFactory);
        var client = new GameClient(state, world, accounts, loggerFactory.CreateLogger<GameClient>());
        var account = new Account { Name = "full", MaxChars = 1 };
        account.SetCharSlot(0, new Serial(0x00000001));
        TestHarness.SetPrivateField(client, "_account", account);
        client.PendingCharCreate = new CharCreateInfo { Name = "should-not-exist" };

        client.HandleCharSelect(-1, "should-not-exist");

        Assert.Null(client.Character);
        Assert.Equal(1, account.CharCount);
        Assert.Contains(TestHarness.GetQueuedPackets(state), p =>
            p.Span.Length > 0 && p.Span[0] == 0xA9);
    }

    [Fact]
    public void BuffIconSupport_StartsAtClient5002b()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var state = TestHarness.CreateActiveNetState(loggerFactory, 19001);
        state.ClientVersionNumber = 50_002_001;
        Assert.False(state.SupportsBuffIcon);
        state.ClientVersionNumber = 50_002_002;
        Assert.True(state.SupportsBuffIcon);
    }

    [Fact]
    public void ClientVersionParser_UnderstandsLetterPatchVersions()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out var state, out _);

        client.HandleClientVersion("5.0.2b");

        Assert.Equal(50_002_002u, state.ClientVersionNumber);
        Assert.True(state.SupportsBuffIcon);
    }

    /// <summary>The game socket never carries a client version of its own —
    /// the classic client sends the bare 4-byte seed there, so the server only
    /// learns the version from the 0xBD reply it requests after 0x91. With a
    /// non-Modern ClientEra that leaves SupportsBuffIcon false for the whole
    /// login sequence, and every 0xDF sent in that window was dropped for good.
    /// The buff bar must be rebuilt the moment the version reopens the gate.
    /// </summary>
    [Fact]
    public void ClientVersionArrivingAfterLogin_RebuildsBuffBar()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var engine = new SpellEngine(world, new SpellRegistry());
        var client = CreatePlayingClient(loggerFactory, world, out var state, out var player);
        client.SetEngines(spellEngine: engine);

        // A game connection before the 0xBD reply: no version, legacy era.
        state.ClientEra = ClientEra.Sphere56x;
        state.ClientVersionNumber = 0;
        Assert.False(state.SupportsBuffIcon);

        var schedule = typeof(SpellEngine).GetMethod("ScheduleEffectExpiry",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var def = new SpellDef { Id = SpellType.Bless, DurationBase = 300, DurationScale = 300 };
        schedule.Invoke(engine, [player, player, SpellType.Bless, def, 8]);

        var changes = new List<(BuffIcon Icon, bool Add)>();
        Character.OnClientBuffChanged = (target, icon, add, _, _) =>
        {
            if (target == player)
                changes.Add((icon, add));
        };

        client.HandleClientVersion("7.0.20.0");

        Assert.True(state.SupportsBuffIcon);
        Assert.Contains(changes, c => c.Icon == BuffIcon.Bless && c.Add);
    }

    /// <summary>Source-X keeps the effect magnitude in the spell memory's
    /// m_itSpell.m_spelllevel (MOREY), which the world save writes out with
    /// MOREP — so a restart still shows the exact number in the buff tooltip.
    /// SphereNet mirrors it into the memory item and the effect record.</summary>
    [Fact]
    public void BuffMagnitude_SurvivesSaveReloadAndReachesTheMemoryMoreY()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var engine = new SpellEngine(world, new SpellRegistry());
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var schedule = typeof(SpellEngine).GetMethod("ScheduleEffectExpiry",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var def = new SpellDef { Id = SpellType.Strength, DurationBase = 300, DurationScale = 300 };
        schedule.Invoke(engine, [ch, ch, SpellType.Strength, def, 12]);

        // The spell memory carries the magnitude in MOREY, as Source-X does.
        var memory = Assert.Single(ch.Memories, m => m.ItemType == ItemType.Spell);
        Assert.Equal(12, memory.MoreP.Y);

        // Round-trip the effect through the persisted record.
        var record = Assert.Single(engine.GetPersistedEffectRecords(ch, Environment.TickCount64));
        var reloaded = new SpellEngine(world, new SpellRegistry());
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        target.AddPendingSpellEffectRecord(record);
        Assert.Equal(1, reloaded.RestorePersistedEffects(target));

        var changes = new List<(BuffIcon Icon, bool Add, string[]? Args)>();
        Character.OnClientBuffChanged = (t, icon, add, _, args) =>
        {
            if (t == target)
                changes.Add((icon, add, args));
        };
        reloaded.ResendBuffs(target);

        var added = Assert.Single(changes, c => c.Icon == BuffIcon.Strength && c.Add);
        Assert.Equal(["12"], added.Args!);
    }

    /// <summary>Blood Oath is the one spell that raises two different icons on
    /// two characters (Source-X CCharSpell.cpp:1312): the victim gets the curse
    /// named after the caster, the caster gets the bond named after the victim.
    /// </summary>
    [Fact]
    public void BloodOath_RaisesCurseOnVictimAndBondOnCaster()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.BloodOath, DurationBase = 300, DurationScale = 300
        });
        var engine = new SpellEngine(world, registry);
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.Name = "Necro";
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var victim = world.CreateCharacter();
        victim.Name = "Prey";
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));

        var changes = new List<(Character Target, BuffIcon Icon, bool Add, string[]? Args)>();
        Character.OnClientBuffChanged = (t, icon, add, _, args) => changes.Add((t, icon, add, args));

        engine.ApplyDirectEffect(caster, victim, SpellType.BloodOath, 300);

        var curse = Assert.Single(changes,
            c => c.Target == victim && c.Icon == BuffIcon.BloodOathCurse && c.Add);
        Assert.Equal(["Necro", "Necro"], curse.Args!);
        var bond = Assert.Single(changes,
            c => c.Target == caster && c.Icon == BuffIcon.BloodOathCaster && c.Add);
        Assert.Equal(["Prey"], bond.Args!);

        // Breaking the bond drops both icons.
        changes.Clear();
        engine.ClearAllEffectsOnDeath(caster);
        Assert.Contains(changes, c => c.Target == caster && c.Icon == BuffIcon.BloodOathCaster && !c.Add);
        Assert.Contains(changes, c => c.Target == victim && c.Icon == BuffIcon.BloodOathCurse && !c.Add);
    }

    /// <summary>0xED unequip macro (Source-X PacketUnEquipItemMacro): the named
    /// layers are stripped and bounced into the pack.</summary>
    [Fact]
    public void UnequipMacro_StripsTheLayerIntoThePack()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out _, out var player);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        player.Backpack = pack;

        var weapon = world.CreateItem();
        weapon.BaseId = 0x0F5E; // broadsword
        weapon.ItemType = ItemType.WeaponSword;
        player.Equip(weapon, Layer.OneHanded);
        Assert.True(weapon.IsEquipped);

        client.HandleUnequipMacro([(ushort)Layer.OneHanded]);

        Assert.False(weapon.IsEquipped);
        Assert.Equal(pack.Uid, weapon.ContainedIn);
        Assert.Null(player.GetEquippedItem(Layer.OneHanded));
    }

    /// <summary>The macros must never touch the pack or hair layers, and the
    /// batch is capped at three entries upstream so a forged count cannot spin
    /// the server (Source-X "prevent packet exploit ... overload server CPU").
    /// </summary>
    [Fact]
    public void UnequipMacro_RefusesThePackLayer()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out _, out var player);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Equip(pack, Layer.Pack);
        player.Backpack = pack;

        client.HandleUnequipMacro([(ushort)Layer.Pack]);

        Assert.True(pack.IsEquipped);
        Assert.Same(pack, player.GetEquippedItem(Layer.Pack));
    }

    /// <summary>An item that is not in the character's possession must not be
    /// equippable through the macro (Source-X checks GetTopLevelObj).</summary>
    [Fact]
    public void EquipMacro_IgnoresItemsNotCarriedByTheCharacter()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out _, out var player);

        var onGround = world.CreateItem();
        onGround.BaseId = 0x0F5E;
        onGround.ItemType = ItemType.WeaponSword;
        world.PlaceItem(onGround, new Point3D((short)(player.X + 1), player.Y, 0, 0));

        client.HandleEquipMacro([onGround.Uid.Value]);

        Assert.False(onGround.IsEquipped);
    }

    [Fact]
    public void BuffPacket_UsesSourceXIconAndClilocLayout()
    {
        var packet = new PacketBuffIcon(0x00000001, BuffIcon.Bless, true, 30,
            1075847, 1075848).Build();

        Assert.Equal(0xDF, packet.Span[0]);
        Assert.Equal((ushort)BuffIcon.Bless, ReadU16(packet.Span, 7));
        Assert.Equal((ushort)BuffIcon.Bless, ReadU16(packet.Span, 15));
        Assert.Equal((ushort)30, ReadU16(packet.Span, 23));
        Assert.Equal(1075847u, ReadU32(packet.Span, 28));
        Assert.Equal(1075848u, ReadU32(packet.Span, 32));
    }

    [Fact]
    public void SpellBuffLifecycle_AddsResendsAndRemovesIcon()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var engine = new SpellEngine(world, new SpellRegistry());
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var changes = new List<(BuffIcon Icon, bool Add, ushort Duration, string[]? Args)>();
        Character.OnClientBuffChanged = (target, icon, add, duration, args) =>
        {
            if (target == ch)
                changes.Add((icon, add, duration, args));
        };

        var schedule = typeof(SpellEngine).GetMethod("ScheduleEffectExpiry",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var def = new SpellDef { Id = SpellType.Bless, DurationBase = 300, DurationScale = 300 };
        schedule.Invoke(engine, [ch, ch, SpellType.Bless, def, 8]);

        Assert.Contains(changes, c => c.Icon == BuffIcon.Bless && !c.Add && c.Duration == 0);
        var added = Assert.Single(changes, c => c.Icon == BuffIcon.Bless && c.Add);
        Assert.Equal(30, added.Duration);
        // Bless carries one cliloc argument per base stat (Source-X STAT_BASE_QTY).
        Assert.Equal(["8", "8", "8"], added.Args!);

        changes.Clear();
        engine.ResendBuffs(ch);
        Assert.Equal(2, changes.Count);
        Assert.False(changes[0].Add);
        Assert.True(changes[1].Add);
        Assert.InRange(changes[1].Duration, (ushort)1, (ushort)30);
        Assert.Equal(["8", "8", "8"], changes[1].Args!);

        changes.Clear();
        engine.ProcessExpirations(long.MaxValue);
        Assert.Contains(changes, c => c.Icon == BuffIcon.Bless && !c.Add && c.Duration == 0);
    }

    [Fact]
    public void TooltipModeOne_UsesRevisionPacketAfterInitialFullList()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        world.ToolTipMode = 1;
        var client = CreatePlayingClient(loggerFactory, world, out var state, out var player);
        state.ClientVersionNumber = 70_020_000;
        var item = world.CreateItem();
        item.Name = "test item";
        world.PlaceItem(item, player.Position);

        client.SendAosTooltip(item, requested: false);
        client.SendAosTooltip(item, requested: false);
        client.SendAosTooltip(item, requested: true);

        var opcodes = TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]).ToList();
        Assert.Equal(2, opcodes.Count(op => op == 0xD6));
        Assert.Single(opcodes, op => op == 0xDC);
    }

    [Fact]
    public void TooltipModes_OffSendsNothingAndForceModeAlwaysSendsFullList()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out var state, out var player);
        state.ClientVersionNumber = 70_020_000;
        var item = world.CreateItem();
        item.Name = "test item";
        world.PlaceItem(item, player.Position);

        world.ToolTipMode = 0;
        client.SendAosTooltip(item, requested: true);
        Assert.Empty(TestHarness.GetQueuedPackets(state));

        world.ToolTipMode = 2;
        client.SendAosTooltip(item, requested: false);
        client.SendAosTooltip(item, requested: false);
        Assert.Equal(2, TestHarness.GetQueuedPackets(state).Count(p => p.Span[0] == 0xD6));
    }

    [Fact]
    public void DeathAnimationDisabled_StillDrawsGhostWithoutDeathScreenOrParticle()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out var state, out var player);
        Character.PacketDeathAnimationEnabled = false;

        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.SetTag("OWNER_UID", player.Uid.Value.ToString());
        world.PlaceItem(corpse, player.Position);
        player.Kill();
        client.OnCharacterDeath();

        var packets = TestHarness.GetQueuedPackets(state).ToList();
        Assert.Contains(packets, p => p.Span.Length > 0 && p.Span[0] == 0x20);
        Assert.DoesNotContain(packets, p => p.Span.Length > 0 && p.Span[0] == 0x2C);
        Assert.DoesNotContain(packets, p => p.Span.Length > 0 && p.Span[0] == 0xAF);
        Assert.DoesNotContain(packets, p => p.Span.Length > 0 && p.Span[0] == 0x70);
    }

    [Fact]
    public void DeathMenuResponse_ContinuesAsGhostInsteadOfResurrecting()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = CreatePlayingClient(loggerFactory, world, out var state, out var player);
        player.Kill();

        client.HandleDeathMenu(1);

        Assert.True(player.IsDead);
        Assert.Contains(TestHarness.GetQueuedPackets(state), p =>
            p.Span.Length > 0 && p.Span[0] == 0x54);
    }

    private static GameClient CreatePlayingClient(ILoggerFactory loggerFactory, GameWorld world,
        out NetState state, out Character player)
    {
        state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(20_000, 30_000));
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Name = "Tester";
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return client;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];
}
