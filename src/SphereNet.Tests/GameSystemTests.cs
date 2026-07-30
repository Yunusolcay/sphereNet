using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.Party;
using SphereNet.Game.Guild;
using SphereNet.Game.Trade;
using SphereNet.Game.Gumps;
using SphereNet.Game.Death;
using SphereNet.Game.Crafting;
using SphereNet.Game.AI;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Interfaces;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;
using SphereNet.Game.Scripting;
using SphereNet.Game.Movement;
using SphereNet.Game.Accounts;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SphereNet.Network.Encryption;
using SphereNet.Network.Packets.Outgoing;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;

namespace SphereNet.Tests;

public class GameSystemTests
{
    private sealed class NullConsole : ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "test";
    }

    private static GameWorld CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    private static Queue<PacketBuffer> GetQueuedPackets(NetState state)
    {
        // Combined outbound snapshot in flush order (priority queues, Highest → Idle).
        var queues = (Queue<PacketBuffer>[])typeof(NetState)
            .GetField("_queues", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(state)!;
        var combined = new Queue<PacketBuffer>();
        for (int p = queues.Length - 1; p >= 0; p--)
            foreach (var pkt in queues[p])
                combined.Enqueue(pkt);
        return combined;
    }

    private static bool IsSpeedModeOnePacket(PacketBuffer packet)
    {
        var span = packet.Span;
        return span.Length == 6
            && span[0] == 0xBF
            && span[1] == 0x00
            && span[2] == 0x06
            && span[3] == 0x00
            && span[4] == 0x26
            && span[5] == 0x01;
    }

    private static void SetNetStateInUse(NetState state, bool value)
    {
        typeof(NetState)
            .GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, value);
    }

    private static void AttachCharacter(SphereNet.Game.Clients.GameClient client, Character ch, Account? account = null)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);
        if (account != null)
        {
            typeof(SphereNet.Game.Clients.GameClient)
                .GetField("_account", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, account);
        }
    }

    private static bool InvokePetCommand(SphereNet.Game.Clients.GameClient client, string text)
    {
        return (bool)typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod("TryHandlePetCommand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [text])!;
    }

    private static void InvokeEnterWorld(SphereNet.Game.Clients.GameClient client)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod("EnterWorld", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, []);
    }

    private static ushort ReadU16(PacketBuffer packet, int offset)
    {
        var span = packet.Span;
        return (ushort)((span[offset] << 8) | span[offset + 1]);
    }

    private static uint ReadU32(PacketBuffer packet, int offset)
    {
        var span = packet.Span;
        return (uint)((span[offset] << 24) | (span[offset + 1] << 16) |
            (span[offset + 2] << 8) | span[offset + 3]);
    }

    private static void InvokePrivate(SphereNet.Game.Clients.GameClient client, string methodName, params object[] args)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, args);
    }

    private static void SetPrivateField<T>(SphereNet.Game.Clients.GameClient client, string fieldName, T value)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, value);
    }

    [Fact]
    public void Speech_TwoWordNonPetText_DoesNotEmitPetFailure()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 16001);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, player);

        Assert.False(InvokePetCommand(client, "selam nasilsin"));

        var packets = GetQueuedPackets(client.NetState).ToList();
        Assert.DoesNotContain(packets, p =>
            p.Span.Length > 0 &&
            (p.Span[0] == 0x1C || p.Span[0] == 0xAE) &&
            System.Text.Encoding.ASCII.GetString(p.Span.ToArray()).Contains("Sorry"));
    }

    [Fact]
    public void DeadLoginThenResurrect_RestoresSkinHue()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 16002);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = 0x0190;
        player.Hue = new Color(0x03EA);
        player.MaxHits = 50;
        player.Kill();
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, player);

        InvokeEnterWorld(client);

        Assert.Equal((ushort)0x0192, player.BodyId);
        Assert.Equal((ushort)0x03EA, player.OSkin);
        Assert.Equal((ushort)0, player.Hue.Value);

        client.OnResurrect();

        Assert.False(player.IsDead);
        Assert.Equal((ushort)0x0190, player.BodyId);
        Assert.Equal((ushort)0x03EA, player.Hue.Value);
        Assert.Equal((ushort)0, player.OSkin);

        var drawPlayer = GetQueuedPackets(client.NetState)
            .LastOrDefault(p => p.Span.Length >= 19 && p.Span[0] == 0x20 && ReadU16(p, 5) == 0x0190);
        Assert.NotNull(drawPlayer);
        Assert.Equal((ushort)0x03EA, ReadU16(drawPlayer!, 8));
    }

    [Fact]
    public void OnCharacterDeath_DoesNotSendObserverDeathAnimationToSelf()
    {
        var world = CreateWorld();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 16003);
        var observerClient = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 16004);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = 0x0190;
        player.Hue = new Color(0x03EA);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, player);
        var staff = world.CreateCharacter();
        staff.IsPlayer = true;
        staff.PrivLevel = PrivLevel.Counsel;
        world.PlaceCharacter(staff, new Point3D(101, 100, 0, 0));
        AttachCharacter(observerClient, staff);
        client.ForEachClientInRange = (_, _, excludeUid, action) =>
        {
            if (staff.Uid.Value != excludeUid)
                action(staff, observerClient);
        };

        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.BaseId = 0x2006;
        corpse.Amount = player.BodyId;
        corpse.SetTag("OWNER_UID", player.Uid.Value.ToString());
        world.PlaceItem(corpse, player.Position);

        player.Kill();
        client.OnCharacterDeath();

        Assert.Equal((ushort)0x0192, player.BodyId);

        var packets = GetQueuedPackets(client.NetState).ToList();
        Assert.DoesNotContain(packets, p => p.Span.Length == 13 && p.Span[0] == 0xAF);
        Assert.DoesNotContain(packets, p => p.Span.Length > 0 && p.Span[0] == 0x70);
        Assert.Contains(packets, p => p.Span.Length == 3 && p.Span[0] == 0xBC &&
            p.Span[1] == (byte)SeasonType.Desolation);

        Assert.Contains(packets, p => p.Span.Length >= 19 && p.Span[0] == 0x20 && ReadU16(p, 5) == 0x0192);
        Assert.Contains(packets, p => p.Span.Length >= 17 && p.Span[0] == 0x77 && ReadU16(p, 5) == 0x0192);
        int drawPlayerIndex = packets.FindIndex(p => p.Span.Length >= 19 && p.Span[0] == 0x20 && ReadU16(p, 5) == 0x0192);
        int sysMessageIndex = packets.FindIndex(p => p.Span.Length > 0 && p.Span[0] == 0xAE);
        int deathStatusIndex = packets.FindIndex(p => p.Span.Length == 2 && p.Span[0] == 0x2C && p.Span[1] == 0x00);
        Assert.True(drawPlayerIndex >= 0);
        Assert.True(sysMessageIndex >= 0);
        Assert.True(deathStatusIndex > drawPlayerIndex);
        Assert.True(deathStatusIndex > sysMessageIndex);

        var observerPackets = GetQueuedPackets(observerClient.NetState).ToList();
        Assert.Contains(observerPackets, p => p.Span.Length >= 9 && p.Span[0] == 0x78 && ReadU16(p, 7) == 0x0192);
        Assert.Contains(observerPackets, p => p.Span.Length >= 17 && p.Span[0] == 0x77 && ReadU16(p, 5) == 0x0192);
    }

    // --- Party ---

    [Fact]
    public void Party_CreateAndAddMembers()
    {
        var pm = new PartyManager();
        var party = pm.CreateParty(new Serial(1));
        Assert.Equal(1, party.MemberCount);

        bool added = party.AddMember(new Serial(2));
        Assert.True(added);
        Assert.Equal(2, party.MemberCount);
    }

    [Fact]
    public void Party_MaxSize10()
    {
        var pm = new PartyManager();
        var party = pm.CreateParty(new Serial(1));
        for (uint i = 2; i <= 10; i++)
            party.AddMember(new Serial(i));
        Assert.True(party.IsFull);
        Assert.False(party.AddMember(new Serial(11)));
    }

    [Fact]
    public void Party_LeavePromotesNewMaster()
    {
        var pm = new PartyManager();
        pm.CreateParty(new Serial(1));
        pm.AcceptInvite(new Serial(1), new Serial(2));
        pm.AcceptInvite(new Serial(1), new Serial(3)); // need 3+ so party survives
        pm.Leave(new Serial(1));
        var party = pm.FindParty(new Serial(2));
        Assert.NotNull(party);
        Assert.Equal(new Serial(2), party.Master);
    }

    // --- Guild ---

    [Fact]
    public void Guild_CreateWithMaster()
    {
        var gm = new GuildManager();
        var guild = gm.CreateGuild(new Serial(100), "Test Guild", new Serial(1));
        Assert.Equal("Test Guild", guild.Name);
        var master = guild.GetMaster();
        Assert.NotNull(master);
        Assert.Equal(new Serial(1), master.CharUid);
        Assert.Equal(GuildPriv.Master, master.Priv);
    }

    [Fact]
    public void Guild_RecruitAndAccept()
    {
        var gm = new GuildManager();
        var guild = gm.CreateGuild(new Serial(100), "TG", new Serial(1));
        guild.AddRecruit(new Serial(2));
        Assert.False(guild.IsMember(new Serial(2))); // still candidate
        guild.AcceptMember(new Serial(2));
        Assert.True(guild.IsMember(new Serial(2)));
    }

    [Fact]
    public void Guild_War()
    {
        var gm = new GuildManager();
        var g1 = gm.CreateGuild(new Serial(100), "G1", new Serial(1));
        var g2 = gm.CreateGuild(new Serial(200), "G2", new Serial(2));

        // One-sided war: not yet mutual enemy
        g1.DeclareWar(g2.StoneUid);
        Assert.False(g1.IsAtWarWith(g2.StoneUid)); // needs both sides

        // Other side declares too: now mutual enemy
        var rel = g1.GetOrCreateRelation(g2.StoneUid);
        rel.TheyDeclaredWar = true;
        Assert.True(g1.IsAtWarWith(g2.StoneUid));

        // Peace
        g1.DeclarePeace(g2.StoneUid);
        Assert.False(g1.IsAtWarWith(g2.StoneUid));
    }

    [Fact]
    public void GuildChat_RoutesToMembersOnly_IncludingSpeakerEcho()
    {
        var world = CreateWorld();
        var speaker = world.CreateCharacter(); speaker.Name = "Speaker";
        var member = world.CreateCharacter(); member.Name = "Member";
        var outsider = world.CreateCharacter(); outsider.Name = "Outsider";

        var gm = new GuildManager();
        var guild = gm.CreateGuild(new Serial(100), "TG", speaker.Uid);
        guild.AddRecruit(member.Uid);
        guild.AcceptMember(member.Uid);

        var speech = new SpeechEngine(world) { GuildManager = gm };
        var delivered = new List<(Serial Recipient, TalkMode Mode)>();
        speech.OnChannelMessage += (_, recipient, _, mode) => delivered.Add((recipient.Uid, mode));

        bool handled = speech.ProcessSpeech(speaker, "hail", TalkMode.Guild);

        Assert.True(handled);
        Assert.Contains(delivered, d => d.Recipient == speaker.Uid); // server echo
        Assert.Contains(delivered, d => d.Recipient == member.Uid);
        Assert.DoesNotContain(delivered, d => d.Recipient == outsider.Uid);
        Assert.All(delivered, d => Assert.Equal(TalkMode.Guild, d.Mode));
    }

    [Fact]
    public void AllianceChat_ReachesMutualAlliesOnly()
    {
        var world = CreateWorld();
        var speaker = world.CreateCharacter();
        var mutualAllyMaster = world.CreateCharacter();
        var oneSidedAllyMaster = world.CreateCharacter();

        var gm = new GuildManager();
        var g1 = gm.CreateGuild(new Serial(100), "G1", speaker.Uid);
        var g2 = gm.CreateGuild(new Serial(200), "G2", mutualAllyMaster.Uid);
        var g3 = gm.CreateGuild(new Serial(300), "G3", oneSidedAllyMaster.Uid);

        g1.AddAlly(g2.StoneUid);
        g1.GetOrCreateRelation(g2.StoneUid).TheyDeclaredAlliance = true; // mutual
        g1.AddAlly(g3.StoneUid); // one-sided — must not receive

        var speech = new SpeechEngine(world) { GuildManager = gm };
        var recipients = new List<Serial>();
        speech.OnChannelMessage += (_, recipient, _, _) => recipients.Add(recipient.Uid);

        speech.ProcessSpeech(speaker, "allies!", TalkMode.Alliance);

        Assert.Contains(speaker.Uid, recipients);
        Assert.Contains(mutualAllyMaster.Uid, recipients);
        Assert.DoesNotContain(oneSidedAllyMaster.Uid, recipients);
    }

    // --- Script DISMOUNT verb ---

    [Fact]
    public void ScriptDismount_UsesHostHook_FlagFallbackWhenUnwired()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.SetStatFlag(StatFlag.OnHorse);

        Character? dismounted = null;
        Character.OnScriptDismount = c => dismounted = c;
        Assert.True(ch.TryExecuteCommand("DISMOUNT", "", new NullConsole()));
        Assert.Same(ch, dismounted); // host hook owns the full dismount

        Character.OnScriptDismount = null;
        Assert.True(ch.TryExecuteCommand("DISMOUNT", "", new NullConsole()));
        Assert.False(ch.IsStatFlag(StatFlag.OnHorse)); // headless fallback
    }

    [Fact]
    public void ScriptItemVerbs_OpenAndDClick_InvokeHostHooks()
    {
        var world = CreateWorld();
        var item = world.CreateItem();

        Item? opened = null;
        Item? dclicked = null;
        Item.OnScriptOpen = (i, _) => opened = i;
        Item.OnScriptDClick = (i, _) => dclicked = i;

        var console = new NullConsole();
        Assert.True(item.TryExecuteCommand("OPEN", "", console));
        Assert.Same(item, opened);

        Assert.True(item.TryExecuteCommand("DCLICK", "", console));
        Assert.Same(item, dclicked);

        dclicked = null;
        Assert.True(item.TryExecuteCommand("USE", "", console));
        Assert.Same(item, dclicked);

        // Unwired (headless) stays an ack-only no-op.
        Item.OnScriptOpen = null;
        Item.OnScriptDClick = null;
        Assert.True(item.TryExecuteCommand("OPEN", "", console));
        Assert.True(item.TryExecuteCommand("USE", "", console));
    }

    // --- Trade ---

    [Fact]
    public void Trade_StartAndComplete()
    {
        var tm = new TradeManager();
        var world = CreateWorld();
        var ch1 = world.CreateCharacter(); ch1.Name = "A";
        var ch2 = world.CreateCharacter(); ch2.Name = "B";
        var cont1 = world.CreateItem(); cont1.Name = "C1";
        var cont2 = world.CreateItem(); cont2.Name = "C2";
        var trade = tm.StartTrade(ch1, ch2, cont1, cont2);
        Assert.False(trade.IsCompleted);

        trade.ToggleAccept(ch1);
        Assert.False(trade.InitiatorAccepted && trade.PartnerAccepted);
        bool both = trade.ToggleAccept(ch2);
        Assert.True(both);
    }

    // --- NPC AI ---

    [Fact]
    public void Npc_RecordAttack_ResetsReacquireThrottle()
    {
        // Being attacked must clear the target-scan throttle so an idle NPC
        // retaliates immediately instead of waiting out ReacquireDelay.
        var world = CreateWorld();
        var npc = world.CreateCharacter();
        var attacker = world.CreateCharacter();

        npc.NextNpcReacquireTime = long.MaxValue; // fully throttled
        npc.RecordAttack(attacker.Uid, 5);

        Assert.Equal(0, npc.NextNpcReacquireTime);
    }

    [Fact]
    public void Npc_ThreatBonus_FavorsHeaviestAttacker()
    {
        // Threat targeting: a target that has dealt more damage to the NPC yields
        // a higher (bounded) bonus, so the NPC sticks to its biggest threat.
        var world = CreateWorld();
        var npc = world.CreateCharacter();
        var heavy = world.CreateCharacter();
        var light = world.CreateCharacter();

        npc.RecordAttack(heavy.Uid, 100);
        npc.RecordAttack(light.Uid, 10);

        Assert.Equal(50, SphereNet.Game.AI.NpcAI.GetThreatBonus(npc, heavy)); // 100/2
        Assert.Equal(5, SphereNet.Game.AI.NpcAI.GetThreatBonus(npc, light));  // 10/2
        Assert.True(SphereNet.Game.AI.NpcAI.GetThreatBonus(npc, heavy)
                  > SphereNet.Game.AI.NpcAI.GetThreatBonus(npc, light));
        Assert.Equal(0, SphereNet.Game.AI.NpcAI.GetThreatBonus(npc, npc)); // no record
    }

    [Fact]
    public void Npc_MoralePenalty_WeakFleesStrongPresses()
    {
        // Source-X morale: a weak, hurt NPC facing a strong, healthy foe loses
        // motivation (flees); a strong, healthy NPC facing a weak foe does not.
        var world = CreateWorld();
        var weak = world.CreateCharacter();
        weak.Str = 30; weak.Int = 16; weak.MaxHits = 40; weak.Hits = 10;
        var strong = world.CreateCharacter();
        strong.Str = 120; strong.Int = 0; strong.MaxHits = 100; strong.Hits = 100;

        Assert.True(SphereNet.Game.AI.NpcAI.GetMoralePenalty(weak, strong) < 0); // flee
        // Source-X applies morale UNCLAMPED: the strong side gains a positive
        // motivation bonus against a weak foe (extra target lock).
        Assert.True(SphereNet.Game.AI.NpcAI.GetMoralePenalty(strong, weak) > 0); // press
        // A healthy NPC below half HP still does not fear a far weaker target.
        strong.Hits = 40;
        Assert.True(SphereNet.Game.AI.NpcAI.GetMoralePenalty(strong, weak) > 0);
    }

    // --- Gump ---

    [Fact]
    public void GumpBuilder_BuildsLayoutString()
    {
        var gump = new GumpBuilder(0x1234, 0x5678, 400, 300);
        gump.SetPage(0)
            .AddResizePic(0, 0, 9200, 400, 300)
            .AddText(20, 20, 0, "Hello World")
            .AddButton(20, 260, 4005, 4007, 1);

        string layout = gump.BuildLayoutString();
        Assert.Contains("page", layout);
        Assert.Contains("resizepic", layout);
        Assert.Contains("text", layout);
        Assert.Contains("button", layout);
        Assert.Single(gump.Texts); // "Hello World"
    }

    // --- Death ---

    [Fact]
    public void Death_CreatesCorpse()
    {
        var world = CreateWorld();
        var engine = new DeathEngine(world);
        var victim = world.CreateCharacter();
        victim.Name = "Victim";
        victim.Str = 50; victim.MaxHits = 50; victim.Hits = 50;
        world.PlaceCharacter(victim, new Point3D(1000, 1000, 0, 0));

        var corpse = engine.ProcessDeath(victim);
        Assert.NotNull(corpse);
        Assert.Contains("corpse", corpse.Name.ToLower());
        Assert.True(victim.IsDead);
    }

    [Fact]
    public void Death_LootingOwnCorpseNotCriminal()
    {
        var world = CreateWorld();
        var engine = new DeathEngine(world);
        var ch = world.CreateCharacter();
        ch.Name = "Player"; ch.IsPlayer = true;
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.SetUid(new Serial(42));
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));

        var corpse = engine.ProcessDeath(ch);
        Assert.NotNull(corpse);
        Assert.False(engine.IsLootingCriminal(ch, corpse));
    }

    [Fact]
    public void Death_CarveCorpse_FiresItemTriggerAndCanBeBlocked()
    {
        var world = CreateWorld();
        var engine = new DeathEngine(world);
        var carver = world.CreateCharacter();
        carver.IsPlayer = true;
        world.PlaceCharacter(carver, new Point3D(100, 100, 0, 0));

        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.Name = "corpse";
        world.PlaceItem(corpse, carver.Position);

        var dispatcher = new TriggerDispatcher();
        int carveCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "CarveCorpse", (_, args) =>
        {
            carveCount++;
            Assert.Same(carver, args.CharSrc);
            Assert.Same(corpse, args.ItemSrc);
            return TriggerResult.True;
        });
        engine.TriggerDispatcher = dispatcher;

        var results = engine.CarveCorpse(carver, corpse);

        Assert.Equal(1, carveCount);
        Assert.Empty(results);
        Assert.False(corpse.TryGetTag("CARVED", out _));
    }

    // --- Crafting ---

    [Fact]
    public void Crafting_CanCraft_NoResources_ReturnsFalse()
    {
        var world = CreateWorld();
        var engine = new CraftingEngine(world);
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Blacksmithing, 1000);

        var recipe = new CraftRecipe
        {
            ResultItemId = 0x1000,
            ResultName = "Iron Ingot",
            PrimarySkill = SkillType.Blacksmithing,
            Difficulty = 50,
        };
        recipe.Resources.Add(new CraftResource { ItemId = 0x1BF2, Amount = 10 });
        engine.RegisterRecipe(recipe);

        Assert.False(engine.CanCraft(ch, recipe));
    }

    [Fact]
    public void Crafting_TryCraft_ConsumesNestedResourcesAndCreatesItem()
    {
        var world = CreateWorld();
        var engine = new CraftingEngine(world);
        var ch = world.CreateCharacter();
        // Past the success curve's certainty point so the craft roll is
        // deterministic for the test.
        ch.SetSkill(SkillType.Blacksmithing, 2000);
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        // Smithing requires a forge within 2 tiles (reference parity).
        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        var pouch = world.CreateItem();
        pouch.ItemType = ItemType.Container;
        pack.AddItem(pouch);
        var ingots = world.CreateItem();
        ingots.BaseId = 0x1BF2;
        ingots.Amount = 12;
        pouch.AddItem(ingots);

        var recipe = new CraftRecipe
        {
            ResultItemId = 0x13B9,
            ResultName = "Viking Sword",
            PrimarySkill = SkillType.Blacksmithing,
            Difficulty = 0,
        };
        recipe.Resources.Add(new CraftResource { ItemId = 0x1BF2, Amount = 10 });

        Assert.True(engine.CanCraft(ch, recipe));
        var crafted = engine.TryCraft(ch, recipe);

        Assert.NotNull(crafted);
        Assert.Equal(0x13B9, crafted!.BaseId);
        Assert.Equal(2, ingots.Amount);
        Assert.False(ingots.IsDeleted);
    }

    // --- Point3D ---

    [Fact]
    public void Point3D_DistanceTo_Works()
    {
        var a = new Point3D(0, 0, 0, 0);
        var b = new Point3D(3, 4, 0, 0);
        Assert.Equal(4, a.GetDistanceTo(b)); // Chebyshev would be 4
    }

    [Fact]
    public void Point3D_GetDirectionTo_Works()
    {
        var a = new Point3D(0, 0, 0, 0);
        var east = new Point3D(5, 0, 0, 0);
        Assert.Equal(Direction.East, a.GetDirectionTo(east));
    }

    [Fact]
    public void Character_WarModeFlag_Toggles()
    {
        var ch = new Character();
        Assert.False(ch.IsInWarMode);
        ch.SetStatFlag(StatFlag.War);
        Assert.True(ch.IsInWarMode);
        ch.ClearStatFlag(StatFlag.War);
        Assert.False(ch.IsInWarMode);
    }

    [Fact]
    public void CommandHandler_PrivilegeRejects_CounselCommandForPlayer()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(10, 10, 0, 0));

        var result = commands.TryExecute(player, "GO 100 100 0");
        Assert.Equal(CommandResult.InsufficientPriv, result);
    }

    [Fact]
    public void CommandHandler_Executes_GoForCounsel()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.Counsel;
        world.PlaceCharacter(gm, new Point3D(10, 10, 0, 0));

        var result = commands.TryExecute(gm, "GO 100 120 5");
        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal((short)100, gm.X);
        Assert.Equal((short)120, gm.Y);
        Assert.Equal((sbyte)5, gm.Z);
    }

    [Fact]
    public void GameClient_SpeedModeCommand_SendsSourceXSpeedPacket()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        var accounts = new AccountManager(loggerFactory);
        var client = TestHarness.CreateClient(loggerFactory, world, accounts, 1501);

        var commands = new CommandHandler();
        commands.RegisterDefaults(world);
        client.SetEngines(commands: commands);

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(10, 10, 0, 0));
        TestHarness.AttachCharacter(client, gm);

        client.HandleSpeech(0, 0x03B2, 3, ".speedmode 1");

        Assert.Equal((byte)1, gm.SpeedMode);
        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState), IsSpeedModeOnePacket);
    }

    [Fact]
    public void CommandHandler_ScriptFallback_ExecutesWhenBuiltinMissing()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.Owner;
        world.PlaceCharacter(gm, new Point3D(10, 10, 0, 0));

        string? forwardedLine = null;
        commands.ScriptFallbackExecutor = (ch, line) =>
        {
            Assert.Same(gm, ch);
            forwardedLine = line;
            ch.Name = "ScriptRenamed";
            return true;
        };

        var result = commands.TryExecute(gm, "MTELE 1500,1600,10");
        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal("MTELE 1500,1600,10", forwardedLine);
        Assert.Equal("ScriptRenamed", gm.Name);
    }

    [Fact]
    public void CommandHandler_SavePrefersBuiltinAndRaisesEvent()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var admin = world.CreateCharacter();
        admin.IsPlayer = true;
        admin.PrivLevel = PrivLevel.Admin;

        bool saveCalled = false;
        bool fallbackCalled = false;
        commands.OnSaveCommand += () => saveCalled = true;
        commands.ScriptFallbackExecutor = (_, _) =>
        {
            fallbackCalled = true;
            return true;
        };

        var result = commands.TryExecute(admin, "SAVE");

        Assert.Equal(CommandResult.Executed, result);
        Assert.True(saveCalled);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public void CommandHandler_ScriptPrivilegeMatrix_RejectsLowPrivAndAllowsHighPriv()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        // Simulate script-loaded privilege matrix (as loaded from [PLEVEL] sections).
        commands.LoadScriptCommandPrivileges(BuildResourcesWithPlevel("GETIR", PrivLevel.GM));

        bool executed = false;
        commands.ScriptFallbackExecutor = (_, line) =>
        {
            if (line.StartsWith("GETIR", StringComparison.OrdinalIgnoreCase))
            {
                executed = true;
                return true;
            }
            return false;
        };

        var low = world.CreateCharacter();
        low.IsPlayer = true;
        low.PrivLevel = PrivLevel.Counsel;

        var high = world.CreateCharacter();
        high.IsPlayer = true;
        high.PrivLevel = PrivLevel.GM;

        var lowResult = commands.TryExecute(low, "GETIR mortal");
        var highResult = commands.TryExecute(high, "GETIR mortal");

        Assert.Equal(CommandResult.InsufficientPriv, lowResult);
        Assert.Equal(CommandResult.Executed, highResult);
        Assert.True(executed);
        Assert.Equal(PrivLevel.GM, commands.GetRequiredPrivLevel("GETIR"));
    }

    private static SphereNet.Scripting.Resources.ResourceHolder BuildResourcesWithPlevel(string commandName, PrivLevel level)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_plevel_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, $"[PLEVEL {(int)level}]{Environment.NewLine}{commandName}{Environment.NewLine}");

        var loggerFactory = LoggerFactory.Create(b => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        return resources;
    }

    [Fact]
    public void ScriptInterpreter_Resolves_ArgvBracketNotation()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        // Source-X ARGV splits on commas only; "alpha,beta" indexes to two fields.
        var args = new ExecTriggerArgs(null, 0, 0, "alpha,beta");
        var scope = new ScriptScope();

        var lines = ParseKeys(
            "LOCAL.v=<ARGV[0]>",
            "TAG.RESULT=<LOCAL.v>");

        var result = interpreter.Execute(lines, target, new TestConsole(), args, scope);
        Assert.Equal(TriggerResult.Default, result);
        Assert.True(target.TryGetProperty("TAG.RESULT", out var value));
        Assert.Equal("alpha", value);
    }

    [Fact]
    public void ScriptInterpreter_Resolves_AngleBracketFunctionCalls()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_func_expr_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION SetProcessDelay]\n" +
            "TAG.FUNC_ARG0=<ARGV[0]>\n" +
            "TAG.FUNC_ARG1=<ARGV[1]>\n" +
            "RETURN 1\n\n" +
            "[FUNCTION f_test_angle]\n" +
            "LOCAL.RESULT <SetProcessDelay HelpPage,50>\n" +
            "TAG.RESULT=<LOCAL.RESULT>\n" +
            "RETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        interpreter.CallFunction = (name, target, source, args) =>
            runner.TryRunFunction(name, target, source, args, out var callResult) ? callResult : TriggerResult.Default;
        interpreter.ResolveFunctionExpression = (name, argString, target, source, args) =>
            runner.TryEvaluateFunction(name, argString, target, source, args, out var value) ? value : null;

        var target = new Character();
        bool handled = runner.TryRunFunction("f_test_angle", target, new TestConsole(), new ExecTriggerArgs(), out var result);

        Assert.True(handled);
        Assert.Equal(TriggerResult.True, result);
        Assert.True(target.TryGetProperty("TAG.RESULT", out var localResult));
        Assert.Equal("1", localResult);
        Assert.True(target.TryGetProperty("TAG.FUNC_ARG0", out var arg0));
        Assert.True(target.TryGetProperty("TAG.FUNC_ARG1", out var arg1));
        Assert.Equal("HelpPage", arg0);
        Assert.Equal("50", arg1);
    }

    [Fact]
    public void WeatherEngine_Configure_KeepsWorldSeasonInSync()
    {
        var world = CreateWorld();
        var engine = new WeatherEngine(world);

        engine.Configure(SphereNet.Core.Configuration.SeasonMode.Manual, SeasonType.Winter, intervalMs: 0);

        Assert.Equal(SeasonType.Winter, engine.CurrentSeason);
        Assert.Equal((byte)SeasonType.Winter, world.CurrentSeason);
        Assert.Equal(SphereNet.Core.Configuration.SeasonMode.Manual, engine.CurrentSeasonMode);
    }

    [Fact]
    public void WeatherEngine_ManualMode_DoesNotAdvanceOnTick()
    {
        var world = CreateWorld();
        var engine = new WeatherEngine(world);
        engine.Configure(SphereNet.Core.Configuration.SeasonMode.Manual, SeasonType.Fall, intervalMs: 0);

        bool changed = engine.OnTick();

        Assert.False(changed);
        Assert.Equal(SeasonType.Fall, engine.CurrentSeason);
        Assert.Equal((byte)SeasonType.Fall, world.CurrentSeason);
    }

    [Fact]
    public void WeatherEngine_AutoMode_AdvancesWhenIntervalElapsed()
    {
        var world = CreateWorld();
        var engine = new WeatherEngine(world);
        engine.Configure(SphereNet.Core.Configuration.SeasonMode.Auto, SeasonType.Spring, intervalMs: 1);
        typeof(WeatherEngine)
            .GetField("_lastSeasonChangeTick", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(engine, Environment.TickCount64 - 5);

        bool changed = engine.OnTick();

        Assert.True(changed);
        Assert.Equal(SeasonType.Summer, engine.CurrentSeason);
        Assert.Equal((byte)SeasonType.Summer, world.CurrentSeason);
    }

    [Fact]
    public void ScriptInterpreter_ServSeason_Command_UsesServerSetterBridge()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var args = new ExecTriggerArgs();
        var scope = new ScriptScope();
        string? captured = null;

        interpreter.ServerPropertyResolver = property =>
        {
            captured = property;
            return "2";
        };

        var lines = ParseKeys("SERV.SEASON 3");
        interpreter.Execute(lines, target, new TestConsole(), args, scope);

        Assert.Equal("_SET_SEASON=3", captured);
    }

    [Fact]
    public void GameClient_Resync_ReSendsSeasonPacket()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        world.CurrentSeason = (byte)SeasonType.Winter;
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 42 };
        SetNetStateInUse(netState, true);

        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);

        client.Resync();

        // GetQueuedPackets returns a snapshot of the priority queues, so read it
        // after the action under test.
        var queue = GetQueuedPackets(netState);
        Assert.Contains(queue, pkt => pkt.Span.Length > 0 && pkt.Span[0] == 0xBC && pkt.Span[1] == (byte)SeasonType.Winter);
    }

    [Fact]
    public void Character_PrivLevel_SyncsBoundAccount()
    {
        var world = CreateWorld();
        var account = new Account { Name = "tester", PrivLevel = PrivLevel.Player };
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var oldResolver = Character.ResolveAccountForChar;
        Character.ResolveAccountForChar = uid => uid == ch.Uid ? account : null;
        try
        {
            ch.PrivLevel = PrivLevel.GM;
            Assert.Equal(PrivLevel.GM, account.PrivLevel);
            Assert.True(ch.TrySetProperty("PRIVLEVEL", ((int)PrivLevel.Owner).ToString()));
            Assert.Equal(PrivLevel.Owner, account.PrivLevel);
        }
        finally
        {
            Character.ResolveAccountForChar = oldResolver;
        }
    }

    [Fact]
    public void EnterWorld_NormalizesMissingPlayerSkillClassToZero()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var account = accountManager.CreateAccount("tester", "pw")!;
        account.PrivLevel = PrivLevel.Counsel;
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 77 };
        SetNetStateInUse(netState, true);

        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.SkillClass = 99;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        AttachCharacter(client, ch, account);

        InvokeEnterWorld(client);

        Assert.Equal(0, ch.SkillClass);
        Assert.Equal(PrivLevel.Counsel, ch.PrivLevel);
    }

    [Fact]
    public void Character_OwnershipMemorySurface_ResolvesOwnerControllerAndFriends()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.Name = "Owner";
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var friend = world.CreateCharacter();
        friend.Name = "Friend";
        friend.IsPlayer = true;
        world.PlaceCharacter(friend, new Point3D(101, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Wolf";
        world.PlaceCharacter(pet, new Point3D(102, 100, 0, 0));

        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));
        Assert.True(pet.AddFriend(friend));

        Assert.True(pet.TryGetProperty("OWNER", out var ownerVal));
        Assert.Equal($"0{owner.Uid.Value:X}", ownerVal);
        Assert.True(pet.TryGetProperty("CONTROLLER", out var controllerVal));
        Assert.Equal($"0{owner.Uid.Value:X}", controllerVal);
        Assert.True(pet.TryGetProperty("MemoryFindType.memory_ipet.isValid", out var ownerMemValid));
        Assert.Equal("1", ownerMemValid);
        Assert.True(pet.TryGetProperty("MemoryFindType.memory_friend.link.name", out var friendName));
        Assert.Equal("Friend", friendName);
        Assert.Single(pet.GetMemoryEntriesByType("memory_friend"));
    }

    [Fact]
    public void GuildStone_AllMembers_DispatchesCommandToMatchingMembers()
    {
        var world = CreateWorld();
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));

        var member = world.CreateCharacter();
        world.PlaceCharacter(member, new Point3D(101, 100, 0, 0));
        var candidate = world.CreateCharacter();
        world.PlaceCharacter(candidate, new Point3D(102, 100, 0, 0));

        var guild = new GuildDef(stone.Uid);
        guild.JoinAsMember(member.Uid);
        guild.AddRecruit(candidate.Uid);

        var oldResolveGuild = Item.ResolveGuild;
        var oldExecute = Item.ExecuteGuildMemberCommand;
        var invoked = new List<Serial>();
        try
        {
            Item.ResolveGuild = uid => uid == stone.Uid ? guild : null;
            Item.ExecuteGuildMemberCommand = (_, uid, command) =>
            {
                if (command == "TAG.TEST=1")
                    invoked.Add(uid);
                return true;
            };

            Assert.True(stone.TryExecuteCommand("ALLMEMBERS", "1,TAG.TEST=1", new NullConsole()));
            Assert.Contains(member.Uid, invoked);
            Assert.DoesNotContain(candidate.Uid, invoked);
        }
        finally
        {
            Item.ResolveGuild = oldResolveGuild;
            Item.ExecuteGuildMemberCommand = oldExecute;
        }
    }

    [Fact]
    public void GuildStone_HouseShipSurface_AddsCountsAndRemoves()
    {
        var world = CreateWorld();
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));

        var guild = new GuildDef(stone.Uid);
        var oldResolveGuild = Item.ResolveGuild;
        try
        {
            Item.ResolveGuild = uid => uid == stone.Uid ? guild : null;

            // ADDHOUSE / ADDSHIP register multis; HOUSES / SHIPS read the counts.
            Assert.True(stone.TrySetProperty("ADDHOUSE", "0x401"));
            Assert.True(stone.TrySetProperty("ADDHOUSE", "0x402"));
            Assert.True(stone.TrySetProperty("ADDSHIP", "0x501"));
            Assert.True(stone.TryGetProperty("HOUSES", out string houses));
            Assert.Equal("2", houses);
            Assert.True(stone.TryGetProperty("SHIPS", out string ships));
            Assert.Equal("1", ships);

            // A uid can only be one kind — re-adding 0x401 as a ship moves it.
            Assert.True(stone.TrySetProperty("ADDSHIP", "0x401"));
            Assert.Equal(1, guild.HouseCount);
            Assert.Equal(2, guild.ShipCount);

            // DELHOUSE / DELSHIP unregister by uid regardless of kind.
            Assert.True(stone.TryExecuteCommand("DELSHIP", "0x401", new NullConsole()));
            Assert.Equal(1, guild.ShipCount);
            Assert.True(stone.TryExecuteCommand("DELHOUSE", "0x402", new NullConsole()));
            Assert.Equal(0, guild.HouseCount);

            // MAXHOUSES / MAXSHIPS round-trip through the script surface.
            Assert.True(stone.TrySetProperty("MAXHOUSES", "5"));
            Assert.True(stone.TryGetProperty("MAXHOUSES", out string maxHouses));
            Assert.Equal("5", maxHouses);
        }
        finally
        {
            Item.ResolveGuild = oldResolveGuild;
        }
    }

    [Fact]
    public void GuildStone_AllianceVerbs_RecordMutualDeclarationAndBreak()
    {
        var world = CreateWorld();
        var stone = world.CreateItem();
        stone.ItemType = ItemType.StoneGuild;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));

        var other = new Serial(0xABCDE);
        var guild = new GuildDef(stone.Uid);

        var oldResolveGuild = Item.ResolveGuild;
        try
        {
            Item.ResolveGuild = uid => uid == stone.Uid ? guild : null;

            // Our declaration alone is one-sided — not yet an active alliance.
            Assert.True(stone.TryExecuteCommand("DECLAREALLY", $"0{other.Value:X}", new NullConsole()));
            Assert.True(guild.GetRelation(other)!.WeDeclaredAlliance);
            Assert.False(guild.IsAlliedWith(other));

            // The other guild reciprocates (INVITEALLY uid, 0 = they declared) → mutual.
            Assert.True(stone.TryExecuteCommand("INVITEALLY", $"0{other.Value:X}, 0", new NullConsole()));
            Assert.True(guild.IsAlliedWith(other));

            // Withdrawing our own declaration breaks the alliance.
            Assert.True(stone.TryExecuteCommand("DECLAREUNALLY", $"0{other.Value:X}", new NullConsole()));
            Assert.False(guild.IsAlliedWith(other));
        }
        finally
        {
            Item.ResolveGuild = oldResolveGuild;
        }
    }

    [Fact]
    public void GuildManager_AbbrevSuffix_ShownOnlyForVisibleMembers()
    {
        var gm = new GuildManager();
        var master = new Serial(1);
        var outsider = new Serial(2);
        var guild = gm.CreateGuild(new Serial(100), "The Order", master);
        guild.Abbreviation = "TO";

        // A visible member shows " [TO]"; an unguilded character shows nothing.
        Assert.Equal(" [TO]", gm.GetAbbrevSuffix(master));
        Assert.Equal("", gm.GetAbbrevSuffix(outsider));

        // Hiding the abbreviation (TOGGLEABBREVIATION) suppresses the suffix.
        guild.FindMember(master)!.ShowAbbrev = false;
        Assert.Equal("", gm.GetAbbrevSuffix(master));

        // A guild with no abbreviation never appends a suffix.
        guild.FindMember(master)!.ShowAbbrev = true;
        guild.Abbreviation = "";
        Assert.Equal("", gm.GetAbbrevSuffix(master));
    }

    [Fact]
    public void GameClient_HairDye_ChangesHairAndBeardHue()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 101 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var ch = world.CreateCharacter();
        var hair = world.CreateItem();
        hair.BaseId = 0x203B;
        ch.Equip(hair, Layer.Hair);
        var beard = world.CreateItem();
        beard.BaseId = 0x203E;
        ch.Equip(beard, Layer.FacialHair);
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);

        var dye = world.CreateItem();
        dye.ItemType = ItemType.HairDye;
        dye.Hue = new Color(0x0455);

        client.HandleDoubleClick(dye.Uid.Value);

        Assert.Equal((ushort)0x0455, hair.Hue.Value);
        Assert.Equal((ushort)0x0455, beard.Hue.Value);
    }

    [Fact]
    public void GameClient_ForCharMemoryType_ReturnsOwnershipEntries()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 100 };
        SetNetStateInUse(netState, true);

        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var owner = world.CreateCharacter();
        owner.Name = "Owner";
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, owner);

        var pet = world.CreateCharacter();
        pet.Name = "Drake";
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));

        var entries = client.QueryScriptObjects("FORCHARMEMORYTYPE", pet, "memory_ipet", null);
        Assert.Single(entries);
        Assert.True(entries[0].TryGetProperty("LINK.NAME", out var linkName));
        Assert.Equal("Owner", linkName);
    }

    [Fact]
    public void StableEngine_PersistsStabledPetsViaOwnerTags()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.Name = "Owner";
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var friend = world.CreateCharacter();
        friend.Name = "Friend";
        friend.IsPlayer = true;
        world.PlaceCharacter(friend, new Point3D(101, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Mare";
        world.PlaceCharacter(pet, new Point3D(102, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false));
        pet.AddFriend(friend);
        pet.NpcFood = 37;

        var stableA = new SphereNet.Game.NPCs.StableEngine();
        Assert.True(stableA.StablePet(owner, pet, world));

        var stableB = new SphereNet.Game.NPCs.StableEngine();
        var claimed = stableB.ClaimPet(owner, 0, world, owner.Position);
        Assert.NotNull(claimed);
        Assert.True(claimed!.HasOwner(owner.Uid));
        Assert.True(claimed.IsFriendOf(friend.Uid));
        Assert.Equal((ushort)37, claimed.NpcFood);
    }

    [Fact]
    public void MountEngine_TryMount_PreservesOwnerAndController()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var mountEngine = new SphereNet.Game.Mounts.MountEngine(world);

        var rider = world.CreateCharacter();
        rider.Name = "Rider";
        rider.IsPlayer = true;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));

        var horse = world.CreateCharacter();
        horse.Name = "Horse";
        horse.BodyId = 0x00C8;
        world.PlaceCharacter(horse, new Point3D(101, 100, 0, 0));
        horse.TryAssignOwnership(rider, rider);

        Assert.True(mountEngine.TryMount(rider, horse));
        Assert.True(horse.HasOwner(rider.Uid));
        Assert.True(horse.HasController(rider.Uid));
    }

    [Fact]
    public void GameClient_DoubleClickMount_FiresMountTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 701 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var rider = world.CreateCharacter();
        rider.IsPlayer = true;
        world.PlaceCharacter(rider, new Point3D(100, 100, 0, 0));
        var horse = world.CreateCharacter();
        horse.BodyId = 0x00C8;
        world.PlaceCharacter(horse, new Point3D(101, 100, 0, 0));
        horse.TryAssignOwnership(rider, rider);

        var dispatcher = new TriggerDispatcher();
        int calls = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Mount", (_, args) =>
        {
            calls++;
            Assert.Same(horse, args.O1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher, mountEngine: new SphereNet.Game.Mounts.MountEngine(world));
        AttachCharacter(client, rider);

        client.HandleDoubleClick(horse.Uid.Value);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ScriptInterpreter_ForPlayers_UsesConsoleQueryObjects()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var args = new ExecTriggerArgs();
        var scope = new ScriptScope();

        var c1 = new Character { Name = "P1", IsPlayer = true };
        var c2 = new Character { Name = "P2", IsPlayer = true };
        var console = new TestConsole
        {
            QueryHandler = (query, _, _, _) =>
                query.Equals("FORPLAYERS", StringComparison.OrdinalIgnoreCase)
                    ? [c1, c2]
                    : []
        };

        // Source-X: loop body runs with each iterated object as target.
        // TAG.VISITED is set on each iterated character, not the original target.
        var lines = ParseKeys(
            "FORPLAYERS 9999",
            "TAG.VISITED=1",
            "ENDFOR");

        interpreter.Execute(lines, target, console, args, scope);
        Assert.True(c1.TryGetProperty("TAG.VISITED", out var v1));
        Assert.Equal("1", v1);
        Assert.True(c2.TryGetProperty("TAG.VISITED", out var v2));
        Assert.Equal("1", v2);
    }

    [Fact]
    public void ScriptInterpreter_UnhandledVerb_UsesConsoleScriptBridge()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var args = new ExecTriggerArgs();
        var scope = new ScriptScope();
        var console = new TestConsole
        {
            CommandHandler = (_, key, value, _) => key.Equals("TARGETFG", StringComparison.OrdinalIgnoreCase) && value == "f_mtele"
        };

        var lines = ParseKeys("TARGETFG f_mtele");

        interpreter.Execute(lines, target, console, args, scope);
        Assert.Equal("TARGETFG", console.LastCommandKey);
        Assert.Equal("f_mtele", console.LastCommandArgs);
    }

    [Fact]
    public void ScriptInterpreter_RoutesExternalServBridgeCommands()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var target = new Character();
        var args = new ExecTriggerArgs { Source = target };
        var scope = new ScriptScope();
        var captured = new List<string>();
        interpreter.ServerPropertyResolver = request =>
        {
            captured.Add(request);
            return "";
        };

        var lines = ParseKeys(
            "SERV.WRITEFILE audit/external.log|boot ok",
            "SERV.DELETEFILE audit/external.log",
            "SERV.CONSOLE resync",
            "SERV.LOG bridge ok",
            "SERV.GMPAGE stuck",
            "SERV.VARLIST quest_",
            "SERV.PRINTLISTS",
            "SERV.EXPORT exports/one",
            "SERV.EXPORT exports/scope, 3, 10",
            "SERV.LOAD exports/one.scp",
            "SERV.IMPORT exports/one.scp",
            "SERV.IMPORT exports/scope.scp, 3, 10",
            "SERV.RESTORE exports/one.scp",
            "SERV.SAVESTATICS exports/statics",
            "SERV.SAVESTATICS");

        interpreter.Execute(lines, target, new TestConsole(), args, scope);

        Assert.Contains("_WRITEFILE=audit/external.log|boot ok", captured);
        Assert.Contains("_DELETEFILE=audit/external.log", captured);
        Assert.Contains("_CONSOLE=resync", captured);
        Assert.Contains("_LOG=bridge ok", captured);
        Assert.Contains(captured, r => r.StartsWith("_GMPAGE=", StringComparison.Ordinal) && r.EndsWith("|stuck", StringComparison.Ordinal));
        // VARLIST/PRINTLISTS carry the caller's UID so the server can route the dump to
        // that client's console instead of only the server log.
        Assert.Contains(captured, r => r.StartsWith("_VARLIST=", StringComparison.Ordinal) && r.EndsWith("|quest_", StringComparison.Ordinal));
        Assert.Contains(captured, r => r.StartsWith("_PRINTLISTS=", StringComparison.Ordinal));
        Assert.Contains(captured, r => r.StartsWith("_EXPORT=", StringComparison.Ordinal) && r.EndsWith("|exports/one", StringComparison.Ordinal));
        Assert.Contains(captured, r => r.StartsWith("_EXPORT=", StringComparison.Ordinal) && r.EndsWith("|exports/scope, 3, 10", StringComparison.Ordinal));
        Assert.Contains("_LOAD=exports/one.scp", captured);
        Assert.Equal(1, captured.Count(r => r == "_LOAD=exports/one.scp"));
        Assert.Contains(captured, r => r.StartsWith("_IMPORT=", StringComparison.Ordinal) && r.EndsWith("|exports/one.scp", StringComparison.Ordinal));
        Assert.Contains(captured, r => r.StartsWith("_IMPORT=", StringComparison.Ordinal) && r.EndsWith("|exports/scope.scp, 3, 10", StringComparison.Ordinal));
        Assert.Contains("_RESTORE=exports/one.scp", captured);
        Assert.Contains("_SAVESTATICS=exports/statics", captured);
        Assert.Contains("_SAVESTATICS=", captured);
    }

    [Fact]
    public void ObjBase_TimerF_QueuesAndRunsDueCallback()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        var fired = false;
        world.TimerFExpired = (obj, entry) =>
        {
            fired = true;
            Assert.Same(ch, obj);
            Assert.Equal("f_remove_buff", entry.FunctionName);
            Assert.Equal("arg1 arg2", entry.Args);
        };

        Assert.True(ch.TryExecuteCommand("TIMERF", "0,f_remove_buff arg1 arg2", new TestConsole()));
        world.OnTick();

        Assert.True(fired);
        Assert.Empty(ch.TimerFEntries);
    }

    [Fact]
    public void ObjBase_TimerF_DeletedObjectIsSafeNoOp()
    {
        var world = CreateWorld();
        var item = world.CreateItem();
        var fired = false;
        world.TimerFExpired = (_, _) => fired = true;

        Assert.True(item.TryExecuteCommand("TIMERF", "0,f_cleanup", new TestConsole()));
        item.Delete();
        world.OnTick();

        Assert.False(fired);
    }

    [Fact]
    public void ObjBase_TimerFMs_SchedulesInMillisecondsNotSeconds()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();

        long before = Environment.TickCount64;
        // TIMERFMS delay is in milliseconds; TIMERF delay is in seconds.
        Assert.True(ch.TryExecuteCommand("TIMERFMS", "50, f_ms_payload x y", new TestConsole()));
        Assert.True(ch.TryExecuteCommand("TIMERF", "3600, f_sec_payload", new TestConsole()));
        long after = Environment.TickCount64;

        Assert.Equal(2, ch.TimerFEntries.Count);
        var ms = ch.TimerFEntries.Single(e => e.FunctionName == "f_ms_payload");
        var sec = ch.TimerFEntries.Single(e => e.FunctionName == "f_sec_payload");

        // Only the first comma splits the delay from the payload; name + args split on the first space.
        Assert.Equal("x y", ms.Args);

        // TIMERFMS 50 is due ~50ms out; TIMERF 3600 is due ~1 hour out.
        Assert.InRange(ms.DueTickMs, before + 50, after + 50);
        Assert.InRange(sec.DueTickMs, before + 3_600_000, after + 3_600_000);

        // Dequeuing 1 second later releases only the millisecond timer; the
        // second-scale timer is still pending — proving the units differ.
        var due = ch.DequeueDueTimerF(after + 1000);
        Assert.Single(due);
        Assert.Equal("f_ms_payload", due[0].FunctionName);
        Assert.Single(ch.TimerFEntries);
    }

    [Fact]
    public void GameClient_SendPacket_ParsesRawPacketSafely()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1701 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var ch = world.CreateCharacter();
        AttachCharacter(client, ch);

        Assert.True(client.TryExecuteScriptCommand(ch, "SENDPACKET", "065 BYTE:01 WORD:0203 DWORD:04050607", null));

        var queue = GetQueuedPackets(netState);
        Assert.Single(queue);
        Assert.Equal([0x65, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07], queue.Peek().Span.ToArray());
    }

    [Fact]
    public void GameClient_SendPacket_InvalidTokenDoesNotSend()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1702 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var ch = world.CreateCharacter();
        AttachCharacter(client, ch);

        Assert.True(client.TryExecuteScriptCommand(ch, "SENDPACKET", "065 WORD:999999", null));

        Assert.Empty(GetQueuedPackets(netState));
    }

    [Fact]
    public void GameClient_ExternalDialogControls_RenderWithoutUnknownGaps()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1703 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        AttachCharacter(client, ch);

        string dir = Path.Combine(Path.GetTempPath(), "spherenet-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string scriptPath = Path.Combine(dir, "dialog.scp");
        File.WriteAllText(scriptPath, """
[DIALOG d_external_smoke]
0,0
DORIGIN 20 30
PAGE 0
DHTMLGUMP +0 +0 180 40 0 1 <NAME>
DCROPPEDTEXT +0 +45 180 20 1153 Hello
DTEXTENTRYLIMITED +0 +70 180 20 1153 1 12 Default
BUTTON +0 +95 4005 4006 1 0 1

[DIALOG d_external_smoke BUTTON]
ON=1
TAG.BUTTON=1
""");

        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(scriptPath);
        var commands = new CommandHandler { Resources = resources };
        client.SetEngines(commands: commands);

        Assert.True(client.TryShowScriptDialog("d_external_smoke", 0));
        Assert.Single(GetQueuedPackets(netState));
    }

    [Fact]
    public void GameClient_DialogOnTarget_OpensOnSourceAndBindsSubject()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var gmClient = TestHarness.CreateClient(loggerFactory, world, accountManager, 1704);
        var targetClient = TestHarness.CreateClient(loggerFactory, world, accountManager, 1705);
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        gm.Name = "Admin";
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Name = "Target";
        TestHarness.AttachCharacter(gmClient, gm);
        TestHarness.AttachCharacter(targetClient, target);

        string dir = Path.Combine(Path.GetTempPath(), "spherenet-dialog-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string scriptPath = Path.Combine(dir, "dialog.scp");
        File.WriteAllText(scriptPath, """
[DIALOG d_admin_target_smoke]
0,0
PAGE 0
DTEXT 20 20 1153 <NAME>
BUTTON 20 50 4005 4006 1 0 1

[DIALOG d_admin_target_smoke BUTTON]
ON=1
TAG.DIALOG_SUBJECT_TOUCHED=1
""");

        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        resources.LoadResourceFile(scriptPath);
        var commands = new CommandHandler { Resources = resources };
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        gmClient.SetEngines(commands: commands, triggerDispatcher: dispatcher);

        Assert.True(gmClient.TryExecuteScriptCommand(target, "DIALOG", "d_admin_target_smoke", null));

        Assert.Single(GetQueuedPackets(gmClient.NetState));
        Assert.Empty(GetQueuedPackets(targetClient.NetState));

        uint gumpId = (uint)Math.Abs("d_admin_target_smoke".GetHashCode());
        gmClient.HandleGumpResponse(gm.Uid.Value, gumpId, 1, [], []);

        Assert.True(target.TryGetProperty("TAG.DIALOG_SUBJECT_TOUCHED", out var touched));
        Assert.Equal("1", touched);
    }

    [Fact]
    public void ProgramAllClientsSnapshot_UsesWorldOnlinePlayersWhenClientRegistryIsEmpty()
    {
        var world = CreateWorld();
        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        gm.IsOnline = true;
        world.AddOnlinePlayer(gm);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        player.IsOnline = true;
        world.AddOnlinePlayer(player);

        var method = typeof(SphereNet.Server.Program)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "BuildAllClientsSnapshot" && m.GetParameters().Length == 3);

        var result = (System.Collections.IEnumerable)method.Invoke(null,
            [
                world,
                Array.Empty<SphereNet.Game.Clients.GameClient>(),
                new Dictionary<Serial, SphereNet.Game.Clients.GameClient>()
            ])!;

        var targets = result.Cast<object>()
            .Select(entry => (Character)entry.GetType().GetField("Item1")!.GetValue(entry)!)
            .ToList();

        Assert.Equal(2, targets.Count);
        Assert.Contains(gm, targets);
        Assert.Contains(player, targets);
    }

    private static List<ScriptKey> ParseKeys(params string[] lines)
    {
        var keys = new List<ScriptKey>(lines.Length);
        foreach (var line in lines)
        {
            var key = new ScriptKey();
            key.Parse(line);
            keys.Add(key);
        }
        return keys;
    }

    private sealed class TestConsole : ITextConsole
    {
        public Func<string, IScriptObj, string, ITriggerArgs?, IReadOnlyList<IScriptObj>>? QueryHandler { get; set; }
        public Func<IScriptObj, string, string, ITriggerArgs?, bool>? CommandHandler { get; set; }
        public string LastCommandKey { get; private set; } = "";
        public string LastCommandArgs { get; private set; } = "";

        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "TEST";

        public bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs)
        {
            LastCommandKey = key;
            LastCommandArgs = args;
            return CommandHandler?.Invoke(target, key, args, triggerArgs) ?? false;
        }

        public IReadOnlyList<IScriptObj> QueryScriptObjects(string query, IScriptObj target, string args, ITriggerArgs? triggerArgs) =>
            QueryHandler?.Invoke(query, target, args, triggerArgs) ?? [];
    }

    [Fact]
    public void MovementEngine_RegionChange_CallsGlobalRegionFunctions()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);

        var dispatcher = new TriggerDispatcher();
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        dispatcher.Runner = runner;
        dispatcher.Resources = resources;

        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_region_hooks_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_onchar_regionleave]\nTAG.REGION_LEAVE=1\nRETURN 1\n\n" +
            "[FUNCTION f_onchar_regionenter]\nTAG.REGION_ENTER=1\nRETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var mover = new MovementEngine(world, dispatcher);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM; // bypass collision/map checks
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        ch.SetTag("CURRENT_REGION", "old_region");

        mover.TryMove(ch, Direction.East, running: false, sequence: 0);

        Assert.True(ch.TryGetProperty("TAG.REGION_LEAVE", out var leaveVal));
        Assert.Equal("1", leaveVal);
    }

    [Fact]
    public void TriggerDispatcher_FiresGenericGlobalCharFunction()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_global_char_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_onchar_attack]\nTAG.GLOBAL_ATTACK=1\nRETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        var ch = new Character();

        var result = dispatcher.FireCharTrigger(ch, CharTrigger.Attack, new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        Assert.True(ch.TryGetProperty("TAG.GLOBAL_ATTACK", out var value));
        Assert.Equal("1", value);
    }

    [Fact]
    public void TriggerDispatcher_FiresScriptTypeDefTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_typedef_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[TYPEDEF t_parity_container]\n" +
            "ON=@DClick\n" +
            "TAG.TYPEDEF_DCLICK=1\n" +
            "RETURN 1\n\n" +
            "[ITEMDEF 0E75]\n" +
            "TYPE=t_parity_container\n");
        resources.LoadResourceFile(tempFile);

        var registry = new SpellRegistry();
        new DefinitionLoader(resources, registry).LoadAll();
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        var item = new Item { BaseId = 0x0E75, ItemType = ItemType.Container };

        var result = dispatcher.FireItemTrigger(item, ItemTrigger.DClick, new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(TriggerResult.True, result);
        Assert.True(item.TryGetProperty("TAG.TYPEDEF_DCLICK", out var value));
        Assert.Equal("1", value);
    }

    [Fact]
    public void NpcAI_TriggerCallbacks_CanBlockLookAndFight()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereNet.Core.Configuration.SphereConfig());
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Monster;
        npc.Hits = npc.MaxHits = 100;
        npc.Karma = -5000;
        npc.NextNpcActionTime = 0;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.IsOnline = true;
        target.Hits = target.MaxHits = 100;
        target.Karma = 1000;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        world.AddOnlinePlayer(target);
        world.OnTick();

        int lookCount = 0;
        ai.OnNpcLookAtChar = (_, _) => { lookCount++; return true; };
        ai.OnTickAction(npc);

        Assert.True(lookCount > 0);
        Assert.False(npc.FightTarget.IsValid);

        npc.FightTarget = target.Uid;
        int fightCount = 0;
        ai.OnNpcLookAtChar = (_, _) => false;
        ai.OnNpcActFight = (_, _, _, _) => { fightCount++; return new NpcAI.NpcFightDecision(true, 0, SkillType.None, SpellType.None); };
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);

        Assert.True(fightCount > 0);
    }

    [Fact]
    public void NpcAI_LichBodyWithoutScriptSpellList_CastsFallbackSpell()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereNet.Core.Configuration.SphereConfig());

        var lich = world.CreateCharacter();
        lich.Name = "a lich";
        lich.NpcBrain = NpcBrainType.Monster;
        lich.BodyId = 0x0018;
        lich.Int = 5;
        lich.Dex = 100;
        lich.Mana = lich.MaxMana = 100;
        lich.Hits = lich.MaxHits = 100;
        world.PlaceCharacter(lich, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.IsOnline = true;
        target.BodyId = 0x0190;
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(104, 100, 0, 0));
        world.AddOnlinePlayer(target);
        world.OnTick();

        lich.FightTarget = target.Uid;
        SpellType castSpell = SpellType.None;
        ai.OnNpcCastSpell = (_, _, spell) => castSpell = spell;

        for (int i = 0; i < 50 && castSpell == SpellType.None; i++)
        {
            lich.NextNpcActionTime = 0;
            ai.OnTickAction(lich);
        }

        Assert.NotEmpty(lich.NpcSpells);
        Assert.NotEqual(SpellType.None, castSpell);
    }

    [Fact]
    public void NpcAI_MoveTowardTarget_DoesNotStepOntoLivingMobile()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereNet.Core.Configuration.SphereConfig());

        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Monster;
        npc.Hits = npc.MaxHits = 100;
        npc.Stam = npc.MaxStam = 100;
        npc.NextNpcActionTime = 0;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));

        var blocker = world.CreateCharacter();
        blocker.Hits = blocker.MaxHits = 100;
        world.PlaceCharacter(blocker, new Point3D(101, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.IsOnline = true;
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(105, 100, 0, 0));
        world.AddOnlinePlayer(target);
        world.OnTick();

        npc.FightTarget = target.Uid;

        ai.OnTickAction(npc);

        // The NPC may SIDESTEP around the blocker (Source-X NPC_WalkToPoint
        // random turn) but must never end up ON the living mobile's tile.
        Assert.False(npc.X == 101 && npc.Y == 100,
            "the NPC stepped onto the living blocker's tile");
        Assert.Equal(new Point3D(101, 100, 0, 0), blocker.Position);
    }

    [Fact]
    public void CommandHandler_NotFound_FiresScriptParityWarning()
    {
        var world = CreateWorld();
        var commands = new CommandHandler();
        commands.RegisterDefaults(world);

        var gm = world.CreateCharacter();
        gm.PrivLevel = PrivLevel.Owner;

        string? warnedVerb = null;
        commands.OnScriptParityWarning += (_, verb, _) => warnedVerb = verb;

        var res = commands.TryExecute(gm, "UNKNOWNCMD 1");
        Assert.Equal(CommandResult.NotFound, res);
        Assert.Equal("UNKNOWNCMD", warnedVerb);
    }

    [Fact]
    public void ScriptSystemHooks_DispatchesServerAndObjectFunctions()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_hooks_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_onserver_start]\nTAG.SERVER_HOOK=1\nRETURN 1\n\n" +
            "[FUNCTION f_onobj_create]\nSRC.TAG.OBJ_CREATED=1\nRETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        var hooks = new ScriptSystemHooks(runner);

        var serverObj = new Character();
        var obj = new Character();

        bool handled = hooks.DispatchServer("start", serverObj);
        hooks.DispatchObject("create", obj);

        Assert.True(handled);
        Assert.True(serverObj.TryGetProperty("TAG.SERVER_HOOK", out var sv) && sv == "1");
        Assert.True(obj.TryGetProperty("TAG.OBJ_CREATED", out var ov) && ov == "1");
    }

    [Fact]
    public void ScriptDbAdapter_ConnectQueryExecuteAndRows_Work()
    {
        SqliteFactory.Instance.ToString(); // ensure assembly load
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
        string dbFile = Path.Combine(Path.GetTempPath(), $"spherenet_db_{Guid.NewGuid():N}.db");
        string conn = $"Data Source={dbFile}";
        var loggerFactory = LoggerFactory.Create(_ => { });
        var db = new ScriptDbAdapter(loggerFactory.CreateLogger<ScriptDbAdapter>())
        {
            DefaultProvider = "Microsoft.Data.Sqlite",
            DefaultConnectionString = conn
        };

        Assert.True(db.ConnectDefault(out var connectErr), connectErr);
        Assert.True(db.Execute("CREATE TABLE test (id INTEGER PRIMARY KEY, name TEXT);", out _, out var createErr), createErr);
        Assert.True(db.Execute("INSERT INTO test(name) VALUES ('alice'),('bob');", out int affected, out var insErr), insErr);
        Assert.True(affected >= 2);
        Assert.True(db.Query("SELECT id,name FROM test ORDER BY id;", out int rows, out var queryErr), queryErr);
        Assert.Equal(2, rows);
        Assert.True(db.TryResolveRowValue("db.row.numrows", out var numRows));
        Assert.Equal("2", numRows);
        Assert.True(db.TryResolveRowValue("db.row.0.name", out var firstName));
        Assert.Equal("alice", firstName);

        // NUMCOLS reflects the last result's column count (Source-X DBO NUMCOLS).
        Assert.Equal(2, db.NumCols);
        Assert.True(db.TryResolveRowValue("db.row.numcols", out var numCols));
        Assert.Equal("2", numCols);

        // AEXECUTE / AQUERY (no worker thread configured) run inline and still
        // populate the rowset for later db.row.* reads.
        Assert.True(db.ExecuteAsync("INSERT INTO test(name) VALUES ('carol');"));
        Assert.True(db.QueryAsync("SELECT id FROM test ORDER BY id;"));
        Assert.True(db.TryResolveRowValue("db.row.numrows", out var afterAsync));
        Assert.Equal("3", afterAsync);
        Assert.Equal(1, db.NumCols); // single selected column
        db.Close();
    }

    [Fact]
    public void PacketHook_PilotOpcodes_CanShortCircuit()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var network = new NetworkManager(2, loggerFactory);
        var state = new NetState(loggerFactory.CreateLogger<NetState>())
        {
            Id = 1
        };
        byte[] op03 = [0x03, 0x00, 0x0A, 0x00, 0x35, 0x00, 0x03, 0x74, 0x65, 0x73, 0x74, 0x00];
        byte[] opad = [0xAD, 0x00, 0x0E, 0x00, 0x35, 0x00, 0x03, 0x49, 0x56, 0x4C, 0x00, 0x00, 0x00, 0x00];
        byte[] op6c = [0x6C, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 10, 0, 20, 0, 0, 0, 0, 0];
        byte[] op72 = [0x72, 0x01, 0x00, 0x00, 0x00];
        byte[] op22 = [0x22, 0x00, 0x01];
        bool called03 = false;
        bool calledAd = false;
        bool called6c = false;
        bool called72 = false;
        bool called22 = false;
        network.PacketScriptHook = (_, opcode, _) =>
        {
            switch (opcode)
            {
                case 0x03: called03 = true; break;
                case 0xAD: calledAd = true; break;
                case 0x6C: called6c = true; break;
                case 0x72: called72 = true; break;
                case 0x22: called22 = true; break;
            }
            return true;
        };

        Assert.True(network.InvokePacketScriptHook(state, 0x03, op03));
        Assert.True(network.InvokePacketScriptHook(state, 0xAD, opad));
        Assert.True(network.InvokePacketScriptHook(state, 0x6C, op6c));
        Assert.True(network.InvokePacketScriptHook(state, 0x72, op72));
        Assert.True(network.InvokePacketScriptHook(state, 0x22, op22));
        Assert.True(called03 && calledAd && called6c && called72 && called22);
    }

    [Fact]
    public void DialogAndDefMessage_Bridge_WorksInClient()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>())
        {
            Id = 7
        };
        // Keep socket null in tests; we only verify state transitions and callbacks.
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager, loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
        var dispatcher = new TriggerDispatcher();
        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_dialog_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_dialogclose_testdlg]\nSRC.TAG.DIALOG_CLOSED=1\nRETURN 1\n\n" +
            "[DEFMESSAGE]\nHELLO_MSG=Merhaba SourceX\n");
        resources.LoadResourceFile(tempFile);
        var interpreter = new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
        dispatcher.Runner = runner;
        dispatcher.Resources = resources;
        var hooks = new ScriptSystemHooks(runner);

        client.SetEngines(triggerDispatcher: dispatcher);
        client.SetScriptServices(hooks, null, k => resources.TryGetDefMessage(k, out var v) ? v : null);

        var acc = accountManager.CreateAccount("tester", "pw")!;
        // attach internals via login flow
        client.HandleLoginRequest(acc.Name, "pw");
        client.HandleGameLogin(acc.Name, "pw", 1);
        client.HandleCharSelect(-1, "Tester");

        var activeChar = client.Character!;
        Assert.True(client.TryExecuteScriptCommand(activeChar, "DIALOG", "testdlg", null));
        client.HandleGumpResponse(0, (uint)Math.Abs("testdlg".GetHashCode()), 1, [], []);
        Assert.True(activeChar.TryGetProperty("TAG.DIALOG_CLOSED", out var closedVal));
        Assert.Equal("1", closedVal);
    }

    [Fact]
    public void GumpResponse_WithoutActiveGump_DoesNotInvokeForgedCallback()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var client = TestHarness.CreateClient(loggerFactory, world, accountManager, 88);
        var ch = world.CreateCharacter();
        ch.Name = "GumpGuard";
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        bool called = false;
        var callbacks = client.Gumps.Callbacks;
        callbacks[0xDEADBEEF] = (_, _, _) => called = true;

        client.HandleGumpResponse(ch.Uid.Value, 0xDEADBEEF, 1, [], []);

        Assert.False(called);
    }

    [Fact]
    public void NetworkPipeline_LoginRelayGameLoginCharSelect_EntersWorld()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory)
        {
            AutoCreateAccounts = true
        };
        var network = new NetworkManager(2, loggerFactory)
        {
            UseCrypt = false,
            UseNoCrypt = true
        };
        var clients = new Dictionary<int, SphereNet.Game.Clients.GameClient>();
        int charSelectCount = 0;
        byte unknownOpcode = 0;
        network.OnUnknownPacket += (_, opcode, _) => unknownOpcode = opcode;

        SphereNet.Game.Clients.GameClient GetClient(NetState state)
        {
            if (!clients.TryGetValue(state.Id, out var client))
            {
                client = new SphereNet.Game.Clients.GameClient(state, world, accountManager,
                    loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
                clients[state.Id] = client;
            }
            return client;
        }

        network.SetHandlers(
            loginRequest: (state, account, password) => GetClient(state).HandleLoginRequest(account, password),
            serverSelect: (state, _) =>
            {
                const uint authId = 0x01020304;
                state.AuthId = authId;
                CryptoState.StoreRelayKeys(authId, state.Crypto.Key1, state.Crypto.Key2, state.ClientVersionNumber);
                state.Send(new PacketRelay(0x7F000001, 2593, authId));
                state.MarkClosing();
            },
            gameLogin: (state, account, password, authId) => GetClient(state).HandleGameLogin(account, password, authId),
            charSelect: (state, slot, name) =>
            {
                charSelectCount++;
                GetClient(state).HandleCharSelect(slot, name);
            });

        var loginState = network.GetState(0)!;
        SetNetStateInUse(loginState, true);
        ProcessPacket(network, loginState, BuildLoginPacket("loopacct", "pw"));
        AssertQueuedOpcode(loginState, 0xA8);

        ProcessPacket(network, loginState, [0xA0, 0x00, 0x00]);
        var relay = AssertQueuedOpcode(loginState, 0x8C);
        uint authId = ReadUInt32(relay.Span, 7);
        Assert.Equal(0x01020304u, authId);
        Assert.True(loginState.IsClosing);

        var gameState = network.GetState(1)!;
        SetNetStateInUse(gameState, true);
        ProcessPacket(network, gameState, BuildGameLoginPacket(authId, "loopacct", "pw"));
        Assert.True(gameState.IsSeeded);
        Assert.True(gameState.Crypto.IsInitialized);
        AssertQueuedOpcode(gameState, 0xB9);
        AssertQueuedOpcode(gameState, 0xA9);

        ProcessPacket(network, gameState, BuildCharSelectPacket("LoopChar", 0));
        Assert.Equal(0, unknownOpcode);
        Assert.Equal(0, gameState.PendingPacketLength);
        Assert.Equal(0, gameState.ReceivedData.Length);
        Assert.Equal(1, charSelectCount);

        var client = clients[gameState.Id];
        Assert.NotNull(client.Account);
        Assert.NotNull(client.Character);
        Assert.True(client.Character!.IsOnline);
        Assert.Contains(client.Character, world.OnlinePlayers);
        AssertQueuedOpcode(gameState, 0x1B);
        AssertQueuedOpcode(gameState, 0x55);
    }

    [Fact]
    public void LoginPipeline_InvalidPassword_SendsReject()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory) { AutoCreateAccounts = false };
        var network = new NetworkManager(2, loggerFactory)
        {
            UseCrypt = false,
            UseNoCrypt = true
        };
        var clients = new Dictionary<int, SphereNet.Game.Clients.GameClient>();

        SphereNet.Game.Clients.GameClient GetClient(NetState state)
        {
            if (!clients.TryGetValue(state.Id, out var client))
            {
                client = new SphereNet.Game.Clients.GameClient(state, world, accountManager,
                    loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
                clients[state.Id] = client;
            }
            return client;
        }

        network.SetHandlers(
            loginRequest: (state, account, password) => GetClient(state).HandleLoginRequest(account, password));

        var loginState = network.GetState(0)!;
        SetNetStateInUse(loginState, true);
        ProcessPacket(network, loginState, BuildLoginPacket("nonexistent", "badpw"));

        // Should receive 0x82 (LoginDenied) since account doesn't exist and AutoCreate is off
        AssertQueuedOpcode(loginState, 0x82);
    }

    [Fact]
    public void LoginPipeline_EnterWorld_ReceivesDrawPlayerAndStatus()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory) { AutoCreateAccounts = true };
        var network = new NetworkManager(2, loggerFactory)
        {
            UseCrypt = false,
            UseNoCrypt = true
        };
        var clients = new Dictionary<int, SphereNet.Game.Clients.GameClient>();

        SphereNet.Game.Clients.GameClient GetClient(NetState state)
        {
            if (!clients.TryGetValue(state.Id, out var client))
            {
                client = new SphereNet.Game.Clients.GameClient(state, world, accountManager,
                    loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());
                clients[state.Id] = client;
            }
            return client;
        }

        network.SetHandlers(
            loginRequest: (state, account, password) => GetClient(state).HandleLoginRequest(account, password),
            serverSelect: (state, _) =>
            {
                const uint authId = 0x01020304;
                state.AuthId = authId;
                CryptoState.StoreRelayKeys(authId, state.Crypto.Key1, state.Crypto.Key2, state.ClientVersionNumber);
                state.Send(new PacketRelay(0x7F000001, 2593, authId));
                state.MarkClosing();
            },
            gameLogin: (state, account, password, authId) => GetClient(state).HandleGameLogin(account, password, authId),
            charSelect: (state, slot, name) => GetClient(state).HandleCharSelect(slot, name));

        var loginState = network.GetState(0)!;
        SetNetStateInUse(loginState, true);
        ProcessPacket(network, loginState, BuildLoginPacket("entertest", "pw"));
        ProcessPacket(network, loginState, [0xA0, 0x00, 0x00]);
        var relay = AssertQueuedOpcode(loginState, 0x8C);
        uint authId = ReadUInt32(relay.Span, 7);

        var gameState = network.GetState(1)!;
        SetNetStateInUse(gameState, true);
        ProcessPacket(network, gameState, BuildGameLoginPacket(authId, "entertest", "pw"));
        ProcessPacket(network, gameState, BuildCharSelectPacket("EnterChar", 0));

        // After entering world: 0x1B (LoginConfirm), 0x20 (DrawPlayer), 0x11 (StatusFull)
        AssertQueuedOpcode(gameState, 0x1B);

        // Check remaining packets for 0x20 and 0x11
        var remainingPackets = TestHarness.GetQueuedPackets(gameState).ToList();
        Assert.Contains(remainingPackets, p => p.Span.Length > 0 && p.Span[0] == 0x20);
        Assert.Contains(remainingPackets, p => p.Span.Length > 0 && p.Span[0] == 0x11);
    }

    [Fact]
    public void BuildViewDelta_CapsItemsPerTileAtEighty()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var client = TestHarness.CreateClient(loggerFactory, world, accountManager, 89);

        var viewer = world.CreateCharacter();
        viewer.Name = "Viewer";
        viewer.IsPlayer = true;
        world.PlaceCharacter(viewer, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(client, viewer);

        for (int i = 0; i < 81; i++)
        {
            var item = world.CreateItem();
            item.BaseId = 0x0EED;
            world.PlaceItem(item, new Point3D(1001, 1000, 0, 0));
        }

        var delta = client.BuildViewDelta();

        Assert.NotNull(delta);
        Assert.Equal(80, delta!.CurrentItems.Count);
        Assert.Equal(80, delta.NewItems.Count);
    }

    [Fact]
    public void BuildViewDelta_GmSeesAttrInvisItems_PlayerGatedByAllShow()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);

        // A normal player never sees an ATTR_INVIS item without AllShow.
        var playerClient = TestHarness.CreateClient(loggerFactory, world, accountManager, 90);
        var player = world.CreateCharacter();
        player.Name = "Player";
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(player, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(playerClient, player);

        var mapPoint = world.CreateItem();
        mapPoint.BaseId = 0x1BC3;
        Assert.True(mapPoint.TrySetProperty("ATTR", "ATTR_INVIS|ATTR_STATIC|ATTR_MOVE_NEVER"));
        world.PlaceItem(mapPoint, new Point3D(1001, 1000, 0, 0));

        var playerDelta = playerClient.BuildViewDelta();
        Assert.NotNull(playerDelta);
        Assert.DoesNotContain(mapPoint.Uid.Value, playerDelta!.CurrentItems);
        Assert.True(mapPoint.IsAttr(ObjAttributes.Invis));
        Assert.True(mapPoint.IsAttr(ObjAttributes.Static));
        Assert.True(mapPoint.IsAttr(ObjAttributes.Move_Never));

        // A GM sees the same invisible item (spawn worldgem, trigger, …) dimmed
        // even with AllShow OFF, so staff can audit spawners on sight.
        var gmClient = TestHarness.CreateClient(loggerFactory, world, accountManager, 91);
        var gm = world.CreateCharacter();
        gm.Name = "Staff";
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        Assert.False(gm.AllShow);
        world.PlaceCharacter(gm, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(gmClient, gm);

        var gmDelta = gmClient.BuildViewDelta();
        Assert.NotNull(gmDelta);
        Assert.Contains(mapPoint.Uid.Value, gmDelta!.CurrentItems);
        Assert.Single(gmDelta.NewItems);
        Assert.True(gmDelta.NewItems[0].HiddenAsAllShow); // sent dimmed
    }

    [Fact]
    public void EnterWorld_SendsPaperdollWithTitle()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var client = TestHarness.CreateClient(loggerFactory, world, accountManager, 0);

        var acc = accountManager.CreateAccount("paper", "pw")!;
        client.HandleLoginRequest(acc.Name, "pw");
        client.HandleGameLogin(acc.Name, "pw", 1);
        client.HandleCharSelect(-1, "Paper");

        var packets = TestHarness.GetQueuedPackets(client.NetState).ToArray();
        Assert.Contains(packets, p => p.Span.Length > 0 && p.Span[0] == 0x88);
    }

    [Fact]
    public void CharacterOwnedMultiTokens_UseRuntimeResolvers()
    {
        var ch = new Character();
        ch.SetUid(new Serial(0x01));

        Character.ResolveHouseUidsByOwner = owner =>
            owner == ch.Uid
                ? [new Serial(0x40001000), new Serial(0x40001001)]
                : [];
        Character.ResolveShipUidsByOwner = owner =>
            owner == ch.Uid
                ? [new Serial(0x40002000)]
                : [];

        try
        {
            Assert.True(ch.TryGetProperty("HOUSES", out var houses));
            Assert.Equal("2", houses);
            Assert.True(ch.TryGetProperty("HOUSE.0", out var firstHouse));
            Assert.Equal("040001000", firstHouse);
            Assert.True(ch.TryGetProperty("HOUSE.5", out var missingHouse));
            Assert.Equal("0", missingHouse);

            Assert.True(ch.TryGetProperty("SHIPS", out var ships));
            Assert.Equal("1", ships);
            Assert.True(ch.TryGetProperty("SHIP.0", out var firstShip));
            Assert.Equal("040002000", firstShip);
        }
        finally
        {
            Character.ResolveHouseUidsByOwner = null;
            Character.ResolveShipUidsByOwner = null;
        }
    }

    [Fact]
    public void HousingEngine_PlaceHouse_EnforcesPlayerHouseLimit()
    {
        var world = CreateWorld();
        var registry = new SphereNet.Game.Housing.MultiRegistry();
        var multi = new SphereNet.Game.Housing.MultiDef { Id = 0x0064, Name = "small test house" };
        multi.Components.Add(new SphereNet.Game.Housing.MultiComponent
        {
            TileId = 0x0001,
            DeltaX = 0,
            DeltaY = 0,
            DeltaZ = 0,
            Visible = true
        });
        multi.RecalcBounds();
        registry.Register(multi);

        var engine = new SphereNet.Game.Housing.HousingEngine(world, registry)
        {
            MaxHousesPerPlayer = 1
        };
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var first = engine.PlaceHouse(owner, 0x0064, new Point3D(120, 120, 0, 0));
        var second = engine.PlaceHouse(owner, 0x0064, new Point3D(140, 140, 0, 0));

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(engine.GetHousesByOwner(owner.Uid));
    }

    [Fact]
    public void TriggerDispatcher_UsesSourceCompatibleItemTriggerNames()
    {
        var dispatcher = new TriggerDispatcher();
        var item = new Item();
        var ch = new Character();
        int itemCalls = 0;
        int charCalls = 0;

        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Trade", (_, _) =>
        {
            itemCalls++;
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPET", "itemDropOn_Trade", (_, _) =>
        {
            charCalls++;
            return TriggerResult.Default;
        });

        dispatcher.FireItemTrigger(item, ItemTrigger.DropOnTrade, new SphereNet.Game.Scripting.TriggerArgs());
        dispatcher.FireCharTrigger(ch, CharTrigger.itemDropOnTrade, new SphereNet.Game.Scripting.TriggerArgs());

        Assert.Equal(1, itemCalls);
        Assert.Equal(1, charCalls);
    }

    [Fact]
    public void GameClient_TradeLifecycle_FiresCreateAcceptedAndCloseTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 801 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));
        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        var tradeManager = new TradeManager();
        var dispatcher = new TriggerDispatcher();
        int createCount = 0;
        int acceptedCount = 0;
        int closeCount = 0;

        dispatcher.RegisterCharEvent("EVENTSPLAYER", "TradeCreate", (_, _) =>
        {
            createCount++;
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "TradeAccepted", (_, _) =>
        {
            acceptedCount++;
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "TradeClose", (_, _) =>
        {
            closeCount++;
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher, tradeManager: tradeManager);
        AttachCharacter(client, initiator);

        client.InitiateTrade(partner);
        var trade = tradeManager.FindTradeFor(initiator);
        Assert.NotNull(trade);
        Assert.Equal(2, createCount);

        trade!.ToggleAccept(partner);
        client.HandleSecureTrade(2, trade.InitiatorContainer.Uid.Value, 0);

        Assert.Equal(2, acceptedCount);
        Assert.Equal(2, closeCount);
        Assert.Null(tradeManager.FindTradeFor(initiator));
    }

    [Fact]
    public void GameClient_DropItemOnTradeContainer_FiresDropOnTradeTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 851 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));
        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, initiator.Position);

        var tradeManager = new TradeManager();
        var dispatcher = new TriggerDispatcher();
        int dropOnTradeCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Trade", (_, args) =>
        {
            dropOnTradeCount++;
            Assert.Same(initiator, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            Assert.Same(partner, args.O1);
            Assert.NotEqual(0, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher, tradeManager: tradeManager);
        AttachCharacter(client, initiator);

        client.InitiateTrade(partner);
        var trade = tradeManager.FindTradeFor(initiator);
        Assert.NotNull(trade);

        client.HandleItemPickup(item.Uid.Value, 1);
        client.HandleItemDrop(item.Uid.Value, 30, 30, 0, trade!.InitiatorContainer.Uid.Value);

        Assert.Equal(1, dropOnTradeCount);
        Assert.Equal(trade.InitiatorContainer.Uid, item.ContainedIn);
        Assert.Contains(item, trade.InitiatorContainer.Contents);
    }

    [Fact]
    public void GameClient_DropItemOnTradeContainer_ReturnTrueRejectsTradeDrop()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 852 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var initiator = world.CreateCharacter();
        initiator.IsPlayer = true;
        world.PlaceCharacter(initiator, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        initiator.Equip(pack, Layer.Pack);
        var partner = world.CreateCharacter();
        partner.IsPlayer = true;
        world.PlaceCharacter(partner, new Point3D(101, 100, 0, 0));

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        pack.AddItem(item); // lifted from the pack → a rejected trade drop bounces back to it

        var tradeManager = new TradeManager();
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "DropOn_Trade", (_, _) => TriggerResult.True);

        client.SetEngines(triggerDispatcher: dispatcher, tradeManager: tradeManager);
        AttachCharacter(client, initiator);

        client.InitiateTrade(partner);
        var trade = tradeManager.FindTradeFor(initiator);
        Assert.NotNull(trade);

        client.HandleItemPickup(item.Uid.Value, 1);
        client.HandleItemDrop(item.Uid.Value, 30, 30, 0, trade!.InitiatorContainer.Uid.Value);

        Assert.Equal(pack.Uid, item.ContainedIn);
        Assert.Contains(item, pack.Contents);
        Assert.DoesNotContain(item, trade.InitiatorContainer.Contents);
        Assert.Equal(0x28, GetQueuedPackets(netState).Last().Span[0]);
    }

    [Fact]
    public void GameClient_ItemColorChangeOnWornItem_SendsWornItemUpdate()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 853 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, player);

        var shirt = world.CreateItem();
        shirt.BaseId = 0x1517;
        player.Equip(shirt, Layer.Shirt);

        var oldCallback = Item.OnVisualUpdate;
        try
        {
            Item.OnVisualUpdate = item => client.SendItemVisualUpdate(item);

            Assert.True(shirt.TrySetProperty("COLOR", "0481"));

            var packet = Assert.Single(GetQueuedPackets(netState), p => p.Span[0] == 0x2E);
            Assert.Equal(0x04, packet.Span[13]);
            Assert.Equal(0x81, packet.Span[14]);
        }
        finally
        {
            Item.OnVisualUpdate = oldCallback;
        }
    }

    // Regression: an item the client knew as a ground item, once equipped, must
    // not be 0x1D-deleted by the per-tick view delta. The 0x2E worn-item packet
    // already re-homes it client-side; a stale delete made worn items vanish
    // until a resync (the "recolour/equip a worn item and it disappears" bug).
    [Fact]
    public void ApplyViewDelta_EquippedFormerGroundItem_NotDeleted()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 871 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, player);

        // Ground item next to the player → enters the client's known-item set.
        var robe = world.CreateItem();
        robe.BaseId = 0x1F03;
        world.PlaceItem(robe, new Point3D(101, 100, 0, 0));
        client.ApplyViewDelta(client.BuildViewDelta()!);
        Assert.Contains(GetQueuedPackets(netState), p => p.Span[0] == 0x1A); // 0x1A world item

        // Equip it (removes it from the ground sector), then refresh again.
        world.HideFromSector(robe);
        player.Equip(robe, Layer.Robe);
        client.ApplyViewDelta(client.BuildViewDelta()!);

        // No 0x1D PacketDeleteObject for the now-worn robe.
        uint serial = robe.Uid.Value;
        bool deleted = GetQueuedPackets(netState).Any(p =>
            p.Span[0] == 0x1D &&
            ((uint)(p.Span[1] << 24 | p.Span[2] << 16 | p.Span[3] << 8 | p.Span[4])) == serial);
        Assert.False(deleted, "worn item must not receive a 0x1D delete");
    }

    [Fact]
    public void GameClient_DropItemOnGround_SendsAckSoundAndWorldItemWithDirection()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 854 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, player);
        client.BroadcastNearby = (_, _, packet, _) => netState.Send(packet);

        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, player.Position);

        client.HandleItemPickup(item.Uid.Value, 1);
        client.HandleItemDrop(item.Uid.Value, 101, 100, 0, 0xFFFFFFFF);

        var packets = GetQueuedPackets(netState).ToArray();
        Assert.Contains(packets, p => p.Span[0] == 0x29);
        Assert.Contains(packets, p => p.Span[0] == 0x54);
        var worldItem = Assert.Single(packets, p => p.Span[0] == 0x1A);
        Assert.Equal(1, item.Direction);
        Assert.True((worldItem.Span[9] & 0x80) != 0);
        Assert.Equal(item.Direction, worldItem.Span[13]);
    }

    // Regression: dropping separate (non-stacking) items on the same tile must
    // scatter their facing instead of all landing on Direction=1, which made
    // piles look like a single uniformly-oriented item.
    [Fact]
    public void GameClient_DropMultipleItemsSameTile_ScattersDirection()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 872 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, player);
        client.BroadcastNearby = (_, _, packet, _) => netState.Send(packet);

        var first = world.CreateItem();
        first.BaseId = 0x0F7A;
        world.PlaceItem(first, player.Position);
        client.HandleItemPickup(first.Uid.Value, 1);
        client.HandleItemDrop(first.Uid.Value, 101, 100, 0, 0xFFFFFFFF);

        // Different graphic so it won't stack onto the first.
        var second = world.CreateItem();
        second.BaseId = 0x1F03;
        world.PlaceItem(second, player.Position);
        client.HandleItemPickup(second.Uid.Value, 1);
        client.HandleItemDrop(second.Uid.Value, 101, 100, 0, 0xFFFFFFFF);

        Assert.Equal(1, first.Direction);
        Assert.Equal(2, second.Direction);
    }

    // Source-X parity: turning to face a combat target must also reach the
    // attacker's OWN client (CChar::UpdateMove -> addPlayerView/addPlayerUpdate,
    // 0x20). Without it, in top-down view the player sees their avatar not facing
    // the target while others see it turn.
    [Fact]
    public void FaceTarget_TurnsPlayerAndUpdatesOwnClient()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 873 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, player);
        player.Direction = Direction.North;

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(105, 100, 0, 0)); // due east

        client.FaceTarget(target);

        Assert.Equal(Direction.East, player.Direction);
        // Own client receives a 0x20 PlayerUpdate (SendSelfRedraw) so the avatar turns.
        Assert.Contains(GetQueuedPackets(netState), p => p.Span[0] == 0x20);
    }

    [Fact]
    public void GameClient_VendorBuyAndSell_FireItemTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var oldVendorWorld = VendorEngine.World;
        VendorEngine.World = world;
        try
        {
            var accountManager = new AccountManager(loggerFactory);
            var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 901 };
            SetNetStateInUse(netState, true);
            var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
                loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

            var player = world.CreateCharacter();
            player.IsPlayer = true;
            player.PrivLevel = PrivLevel.GM; // skip gold checks; this test is about trigger visibility.
            world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            player.Equip(pack, Layer.Pack);

            var vendor = world.CreateCharacter();
            vendor.NpcBrain = NpcBrainType.Vendor;
            world.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));

            var vendorStock = world.CreateItem();
            vendorStock.ItemType = ItemType.Container;
            vendor.Equip(vendorStock, Layer.VendorStock);
            var stock = world.CreateItem();
            stock.BaseId = 0x0F7A;
            stock.Name = "Black Pearl";
            stock.Amount = 1;
            stock.SetTag("PRICE", "10");
            vendorStock.AddItem(stock);

            var stockSellRef = world.CreateItem();
            stockSellRef.BaseId = 0x0F7B;
            stockSellRef.Name = "Blood Moss";
            stockSellRef.SetTag("PRICE", "8");
            vendorStock.AddItem(stockSellRef);

            var sellItem = world.CreateItem();
            sellItem.BaseId = 0x0F7B;
            sellItem.Name = "Blood Moss";
            sellItem.Amount = 1;
            pack.AddItem(sellItem);

            var dispatcher = new TriggerDispatcher();
            int buyCount = 0;
            int sellCount = 0;
            dispatcher.RegisterItemEvent("EVENTSITEM", "Buy", (_, args) =>
            {
                buyCount++;
                Assert.Equal(1, args.N1);
                Assert.True(args.N2 > 0);
                return TriggerResult.Default;
            });
            dispatcher.RegisterItemEvent("EVENTSITEM", "Sell", (_, args) =>
            {
                sellCount++;
                Assert.Equal(1, args.N1);
                Assert.True(args.N2 > 0);
                return TriggerResult.Default;
            });

            client.SetEngines(triggerDispatcher: dispatcher);
            AttachCharacter(client, player);

            client.HandleVendorBuy(vendor.Uid.Value, 1,
            [
                new SphereNet.Network.Packets.Incoming.VendorBuyEntry
                {
                    ItemSerial = stock.Uid.Value,
                    Amount = 1
                }
            ]);
            client.HandleVendorSell(vendor.Uid.Value,
            [
                new SphereNet.Network.Packets.Incoming.VendorSellEntry
                {
                    ItemSerial = sellItem.Uid.Value,
                    Amount = 1
                }
            ]);

            Assert.Equal(1, buyCount);
            Assert.Equal(1, sellCount);
        }
        finally
        {
            VendorEngine.World = oldVendorWorld;
        }
    }

    [Fact]
    public void GameClient_ContextMenu_FiresRequestAndSelectTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1001 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Context Item";
        world.PlaceItem(item, player.Position);

        var dispatcher = new TriggerDispatcher();
        int charRequestCount = 0;
        int charSelectCount = 0;
        int itemRequestCount = 0;
        int itemSelectCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ContextMenuRequest", (_, args) =>
        {
            charRequestCount++;
            Assert.Equal(0, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ContextMenuSelect", (_, args) =>
        {
            charSelectCount++;
            Assert.Equal(1, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ContextMenuRequest", (_, args) =>
        {
            itemRequestCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            Assert.Equal(0, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ContextMenuSelect", (_, args) =>
        {
            itemSelectCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            Assert.Equal(42, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        InvokePrivate(client, "SendContextMenu", target.Uid.Value);
        InvokePrivate(client, "HandleContextMenuResponse", target.Uid.Value, (ushort)1);
        InvokePrivate(client, "SendContextMenu", item.Uid.Value);
        InvokePrivate(client, "HandleContextMenuResponse", item.Uid.Value, (ushort)42);

        Assert.Equal(1, charRequestCount);
        Assert.Equal(1, charSelectCount);
        Assert.Equal(1, itemRequestCount);
        Assert.Equal(1, itemSelectCount);
    }

    [Fact]
    public void GameClient_AOSTooltip_FiresItemTooltipTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        world.ToolTipMode = 1;
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>())
        {
            Id = 1101,
            ClientVersionNumber = 70_009_000
        };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Tooltip Item";
        item.ItemType = ItemType.Container;
        world.PlaceItem(item, player.Position);
        var targetChar = world.CreateCharacter();
        targetChar.IsPlayer = true;
        targetChar.Name = "Tooltip Character";
        world.PlaceCharacter(targetChar, new Point3D(101, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int beforeCount = 0;
        int afterCount = 0;
        int charTooltipCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "ClientTooltip", (_, args) =>
        {
            charTooltipCount++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ClientTooltip", (_, args) =>
        {
            beforeCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "ClientTooltipAfterDefault", (_, args) =>
        {
            afterCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleAOSTooltip(item.Uid.Value);
        client.HandleAOSTooltip(targetChar.Uid.Value);

        Assert.Equal(1, beforeCount);
        Assert.Equal(1, afterCount);
        Assert.Equal(1, charTooltipCount);
        Assert.Contains(GetQueuedPackets(netState), p => p.Span[0] == 0xD6);
        Assert.DoesNotContain(GetQueuedPackets(netState), p => p.Span[0] == 0xDC);
    }

    [Fact]
    public void GameClient_AOSTooltip_ReturnTrueSuppressesTooltipPackets()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        world.ToolTipMode = 1;
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>())
        {
            Id = 1102,
            ClientVersionNumber = 70_009_000
        };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Hidden Tooltip Item";
        world.PlaceItem(item, player.Position);

        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterItemEvent("EVENTSITEM", "ClientTooltip", (_, _) => TriggerResult.True);

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleAOSTooltip(item.Uid.Value);

        Assert.DoesNotContain(GetQueuedPackets(netState), p => p.Span[0] == 0xDC);
        Assert.DoesNotContain(GetQueuedPackets(netState), p => p.Span[0] == 0xD6);
    }

    [Fact]
    public void GameClient_TargetFunction_FiresItemTargOnItemTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1201 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var sourceItem = world.CreateItem();
        sourceItem.Name = "Targeting Wand";
        world.PlaceItem(sourceItem, player.Position);
        var targetItem = world.CreateItem();
        targetItem.Name = "Target Item";
        world.PlaceItem(targetItem, new Point3D(101, 100, 0, 0));

        var resources = new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>());
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_target_block_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile,
            "[FUNCTION f_never_run]\n" +
            "TAG.NEVER_RUN=1\n" +
            "RETURN 1\n");
        resources.LoadResourceFile(tempFile);

        var dispatcher = new TriggerDispatcher
        {
            Runner = new TriggerRunner(
                new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>()),
                resources,
                loggerFactory.CreateLogger<TriggerRunner>())
        };
        int targetItemCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "TargOn_Item", (_, args) =>
        {
            targetItemCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(sourceItem, args.ItemSrc);
            Assert.Same(targetItem, args.O1);
            Assert.Equal(10, args.N1);
            Assert.Equal(11, args.N2);
            Assert.Equal(12, args.N3);
            Assert.Equal("0", args.S1);
            return TriggerResult.True;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);
        client.Targets.Function = "f_never_run";
        client.Targets.ItemUid = sourceItem.Uid;

        client.HandleTargetResponse(0, client.ActiveTargetCursorId, targetItem.Uid.Value, 10, 11, 12, 0);

        Assert.Equal(1, targetItemCount);
        Assert.False(player.TryGetProperty("TAG.NEVER_RUN", out var neverRun) && neverRun == "1");
    }

    [Fact]
    public void GameClient_TargetCancel_FiresItemTargOnCancelTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1202 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var sourceItem = world.CreateItem();
        sourceItem.Name = "Targeting Wand";
        world.PlaceItem(sourceItem, player.Position);

        var dispatcher = new TriggerDispatcher();
        int cancelCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "TargOn_Cancel", (_, args) =>
        {
            cancelCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(sourceItem, args.ItemSrc);
            Assert.Null(args.O1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);
        client.Targets.Function = "f_cancelled";
        client.Targets.ItemUid = sourceItem.Uid;

        client.HandleTargetResponse(0, client.ActiveTargetCursorId, 0xFFFFFFFF, 0, 0, 0, 0);

        Assert.Equal(1, cancelCount);
    }

    [Fact]
    public void GameClient_TargetFunction_FiresItemTargOnCharAndGroundTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1203 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var sourceItem = world.CreateItem();
        sourceItem.Name = "Targeting Wand";
        world.PlaceItem(sourceItem, player.Position);
        var targetChar = world.CreateCharacter();
        targetChar.IsPlayer = true;
        world.PlaceCharacter(targetChar, new Point3D(101, 100, 0, 0));

        var dispatcher = new TriggerDispatcher
        {
            Runner = new TriggerRunner(
                new ScriptInterpreter(new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>()),
                new SphereNet.Scripting.Resources.ResourceHolder(loggerFactory.CreateLogger<SphereNet.Scripting.Resources.ResourceHolder>()),
                loggerFactory.CreateLogger<TriggerRunner>())
        };
        int charCount = 0;
        int groundCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "TargOn_Char", (_, args) =>
        {
            charCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(sourceItem, args.ItemSrc);
            Assert.Same(targetChar, args.O1);
            return TriggerResult.True;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "TargOn_Ground", (_, args) =>
        {
            groundCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(sourceItem, args.ItemSrc);
            Assert.Null(args.O1);
            Assert.Equal(20, args.N1);
            Assert.Equal(21, args.N2);
            Assert.Equal(22, args.N3);
            Assert.Equal("0", args.S1);
            return TriggerResult.True;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);
        client.Targets.Function = "f_char";
        client.Targets.ItemUid = sourceItem.Uid;
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, targetChar.Uid.Value, 10, 11, 12, 0);

        client.Targets.Function = "f_ground";
        client.Targets.AllowGround = true;
        client.Targets.ItemUid = sourceItem.Uid;
        client.HandleTargetResponse(1, client.ActiveTargetCursorId, 0, 20, 21, 22, 0);

        Assert.Equal(1, charCount);
        Assert.Equal(1, groundCount);
    }

    [Fact]
    public void GameClient_TargetCancel_FiresSpellTargetCancelTrigger()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1204 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int cancelCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "SpellTargetCancel", (_, args) =>
        {
            cancelCount++;
            Assert.Same(player, args.CharSrc);
            Assert.NotEqual(0, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);
        player.SetTag("CAST_SPELL", SpellType.MagicArrow.ToString());

        client.HandleTargetResponse(0, client.ActiveTargetCursorId, 0xFFFFFFFF, 0, 0, 0, 0);

        Assert.Equal(1, cancelCount);
        Assert.False(player.TryGetTag("CAST_SPELL", out _));
    }

    [Fact]
    public void GameClient_RenameProfileAndDye_FireUiActionTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1301 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var gm = world.CreateCharacter();
        gm.IsPlayer = true;
        gm.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Name = "Old Name";
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Dyed Item";
        item.Hue = new SphereNet.Core.Types.Color(1);
        world.PlaceItem(item, gm.Position);

        var account = accountManager.CreateAccount("gm", "pw")!;
        account.PrivLevel = PrivLevel.GM;

        var dispatcher = new TriggerDispatcher();
        int renameCount = 0;
        int profileCount = 0;
        int dyeCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Rename", (_, args) =>
        {
            renameCount++;
            Assert.Same(gm, args.CharSrc);
            Assert.Equal("New Name", args.S1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Profile", (_, args) =>
        {
            profileCount++;
            Assert.Same(gm, args.CharSrc);
            Assert.Equal(1, args.N1);
            Assert.Equal("blocked profile", args.S1);
            return TriggerResult.True;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "Dye", (_, args) =>
        {
            dyeCount++;
            Assert.Same(gm, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            Assert.Equal(0x0456, args.N1);
            return TriggerResult.True;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, gm, account);

        client.HandleRename(target.Uid.Value, "New Name");
        client.HandleProfileRequest(1, target.Uid.Value, "blocked profile");
        client.HandleDyeResponse(item.Uid.Value, 0x0456);

        Assert.Equal(1, renameCount);
        Assert.Equal("New Name", target.Name);
        Assert.Equal(1, profileCount);
        Assert.False(target.TryGetTag("PROFILE_BIO", out _));
        Assert.Equal(1, dyeCount);
        Assert.Equal((ushort)1, item.Hue.Value);
    }

    [Fact]
    public void GameClient_DyeVatApply_FiresDyeTriggerAndCanApplyHue()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1302 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var vat = world.CreateItem();
        vat.ItemType = ItemType.DyeVat;
        vat.SetTag("DYE_HUE", "1110");
        world.PlaceItem(vat, player.Position);
        var dest = world.CreateItem();
        dest.Hue = new SphereNet.Core.Types.Color(1);
        world.PlaceItem(dest, player.Position);

        var dispatcher = new TriggerDispatcher();
        int dyeCount = 0;
        dispatcher.RegisterItemEvent("EVENTSITEM", "Dye", (_, args) =>
        {
            dyeCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(dest, args.ItemSrc);
            Assert.Same(vat, args.O1);
            Assert.Equal(1110, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        // HandleDyeApply moved into the phase-3 item-use handler.
        typeof(SphereNet.Game.Clients.ClientItemUseHandler)
            .GetMethod("HandleDyeApply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client.ItemUse, [vat, dest.Uid]);

        Assert.Equal(1, dyeCount);
        Assert.Equal((ushort)1110, dest.Hue.Value);
    }

    [Fact]
    public void GameClient_SingleClick_FiresAfterClickAfterNamePacket()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1401 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Name = "Clicked Character";
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        var item = world.CreateItem();
        item.Name = "Clicked Item";
        world.PlaceItem(item, player.Position);

        var dispatcher = new TriggerDispatcher();
        int charAfterClickCount = 0;
        int itemAfterClickCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "AfterClick", (_, args) =>
        {
            charAfterClickCount++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterItemEvent("EVENTSITEM", "AfterClick", (_, args) =>
        {
            itemAfterClickCount++;
            Assert.Same(player, args.CharSrc);
            Assert.Same(item, args.ItemSrc);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleSingleClick(target.Uid.Value);
        client.HandleSingleClick(item.Uid.Value);

        Assert.Equal(1, charAfterClickCount);
        Assert.Equal(1, itemAfterClickCount);
        Assert.Equal(2, GetQueuedPackets(netState).Count(p => p.Span[0] == 0xAE));
    }

    [Fact]
    public void GameClient_UserPacketHooks_FireSkillsStatsAndButtonTriggers()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1501 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int skillsCount = 0;
        int statsCount = 0;
        int chatCount = 0;
        int guildCount = 0;
        int questCount = 0;
        int virtueCount = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserSkills", (_, args) =>
        {
            skillsCount++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserStats", (_, args) =>
        {
            statsCount++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserChatButton", (_, args) =>
        {
            chatCount++;
            Assert.Equal(0x000B, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserGuildButton", (_, args) =>
        {
            guildCount++;
            Assert.Equal(0x0028, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserQuestButton", (_, args) =>
        {
            questCount++;
            Assert.Equal(0x0032, args.N1);
            return TriggerResult.Default;
        });
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserVirtueInvoke", (_, args) =>
        {
            virtueCount++;
            // Virtue invoke now rides 0x12/0xF4 (Source-X EXTCMD_INVOKE_VIRTUE) with
            // the virtue id in N1 (m_iN1 = iVirtueID); 0xBF 0x2C is the bandage macro.
            Assert.Equal(3, args.N1);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleStatusRequest(5, player.Uid.Value);
        client.HandleStatusRequest(4, player.Uid.Value);
        client.HandleExtendedCommand(0x000B, []);
        // Guild and quest buttons ride the 0xD7 (EXTAOS) transport in the real
        // protocol, not 0xBF — see Source-X PacketGuildButton/PacketQuestButton.
        client.HandleEncodedCommand(0x28, player.Uid.Value, new PacketBuffer(System.Array.Empty<byte>()));
        client.HandleEncodedCommand(0x32, player.Uid.Value, new PacketBuffer(System.Array.Empty<byte>()));
        client.HandleVirtueInvoke(3); // Valor

        Assert.Equal(1, skillsCount);
        Assert.Equal(1, statsCount);
        Assert.Equal(1, chatCount);
        Assert.Equal(1, guildCount);
        Assert.Equal(1, questCount);
        Assert.Equal(1, virtueCount);
    }

    [Fact]
    public void GameClient_WrestleMacros_FireSpecialMoveTrigger()
    {
        // Source-X 0xBF 0x09 (disarm) / 0x0A (stun) route through
        // Event_CombatAbilitySelect → @UserSpecialMove with the ability id
        // (0x05 disarm / 0x0B paralyzing blow), the same sink as 0xD7 0x19.
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1502 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        int disarm = 0, stun = 0;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "UserSpecialMove", (_, args) =>
        {
            if (args.N1 == 0x05) disarm++;
            else if (args.N1 == 0x0B) stun++;
            Assert.Same(player, args.CharSrc);
            return TriggerResult.Default;
        });

        client.SetEngines(triggerDispatcher: dispatcher);
        AttachCharacter(client, player);

        client.HandleExtendedCommand(0x0009, []);
        client.HandleExtendedCommand(0x000A, []);

        Assert.Equal(1, disarm);
        Assert.Equal(1, stun);
    }

    [Fact]
    public void GameClient_CommCrystalDoubleClick_LinksToTargetedCrystal()
    {
        // Source-X CItemCommCrystal: double-click opens a target cursor; the
        // targeted item must itself be a comm crystal to become the relay partner.
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1601 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        AttachCharacter(client, ch);

        var crystalA = world.CreateItem();
        crystalA.ItemType = ItemType.CommCrystal;
        world.PlaceItem(crystalA, new Point3D(100, 100, 0, 0));
        var crystalB = world.CreateItem();
        crystalB.ItemType = ItemType.CommCrystal;
        world.PlaceItem(crystalB, new Point3D(101, 100, 0, 0));

        client.HandleDoubleClick(crystalA.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, crystalB.Uid.Value, 101, 100, 0, 0);
        Assert.Equal(crystalB.Uid, crystalA.Link);

        // Targeting a non-crystal leaves the crystal unlinked.
        var box = world.CreateItem();
        box.ItemType = ItemType.Container;
        world.PlaceItem(box, new Point3D(100, 100, 0, 0));
        var crystalC = world.CreateItem();
        crystalC.ItemType = ItemType.CommCrystal;
        world.PlaceItem(crystalC, new Point3D(100, 100, 0, 0));

        client.HandleDoubleClick(crystalC.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, box.Uid.Value, 100, 100, 0, 0);
        Assert.False(crystalC.Link.IsValid);
    }

    [Fact]
    public void GameClient_GargoyleFly_TogglesHoveringAndBuff()
    {
        // Source-X 0xBF 0x32 GargoyleFly: a living gargoyle with RACIALF_GARG_FLY
        // toggles STATF_HOVERING and the BI_GARGOYLEFLY buff.
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1503 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = 0x029A; // gargoyle
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        Character.RacialFlags = (int)RacialFlags.GargoyleFly;
        var buffs = new List<(BuffIcon Icon, bool Add)>();
        Character.OnClientBuffChanged = (_, icon, add, _, _) => buffs.Add((icon, add));

        AttachCharacter(client, player);

        client.HandleExtendedCommand(0x0032, []); // toggle on
        Assert.True(player.IsStatFlag(StatFlag.Hovering));
        Assert.Contains(buffs, b => b.Icon == BuffIcon.GargoyleFly && b.Add);

        client.HandleExtendedCommand(0x0032, []); // toggle off
        Assert.False(player.IsStatFlag(StatFlag.Hovering));
        Assert.Contains(buffs, b => b.Icon == BuffIcon.GargoyleFly && !b.Add);
    }

    [Fact]
    public void GameClient_GargoyleFly_NonGargoyle_NoOp()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var accountManager = new AccountManager(loggerFactory);
        var netState = new NetState(loggerFactory.CreateLogger<NetState>()) { Id = 1504 };
        SetNetStateInUse(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world, accountManager,
            loggerFactory.CreateLogger<SphereNet.Game.Clients.GameClient>());

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.BodyId = 0x0190; // human — not a gargoyle
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        Character.RacialFlags = (int)RacialFlags.GargoyleFly;
        AttachCharacter(client, player);

        client.HandleExtendedCommand(0x0032, []);
        Assert.False(player.IsStatFlag(StatFlag.Hovering)); // a human never hovers
    }

    // ───── Faz 5: Regression tests for bug fixes ─────

    [Fact]
    public void PasswordHelper_HashAndVerify_RoundTrips()
    {
        var hash = SphereNet.Core.Configuration.PasswordHelper.Hash("testpass123");
        Assert.Equal(32, hash.Length);
        Assert.True(SphereNet.Core.Configuration.PasswordHelper.Verify("testpass123", hash));
        Assert.False(SphereNet.Core.Configuration.PasswordHelper.Verify("wrongpass", hash));
    }

    [Fact]
    public void PasswordHelper_Verify_PlaintextBackwardsCompat()
    {
        Assert.True(SphereNet.Core.Configuration.PasswordHelper.Verify("admin", "admin"));
        Assert.False(SphereNet.Core.Configuration.PasswordHelper.Verify("admin", "other"));
    }

    [Fact]
    public void PasswordHelper_EmptyInputs_ReturnFalse()
    {
        Assert.False(SphereNet.Core.Configuration.PasswordHelper.Verify("", "hash"));
        Assert.False(SphereNet.Core.Configuration.PasswordHelper.Verify("pass", ""));
        Assert.False(SphereNet.Core.Configuration.PasswordHelper.Verify("", ""));
        Assert.Equal("", SphereNet.Core.Configuration.PasswordHelper.Hash(""));
    }

    [Fact]
    public void PasswordHelper_IsHashed_DetectsFormat()
    {
        var hash = SphereNet.Core.Configuration.PasswordHelper.Hash("x");
        Assert.True(SphereNet.Core.Configuration.PasswordHelper.IsHashed(hash));
        Assert.False(SphereNet.Core.Configuration.PasswordHelper.IsHashed("plaintext"));
        Assert.False(SphereNet.Core.Configuration.PasswordHelper.IsHashed(""));
    }

    [Fact]
    public void PacketByteOverflow_ContextMenuClampsTo255()
    {
        var entries = new (ushort, uint, ushort)[300];
        for (int i = 0; i < 300; i++)
            entries[i] = ((ushort)i, (uint)(3000000 + i), (ushort)0);

        var pkt = new SphereNet.Network.Packets.Outgoing.PacketContextMenu(0x12345678, entries);
        var buf = pkt.Build();
        var data = buf.Data;
        // opcode(1) + len(2) + sub(2) + subSub(2) + serial(4) = offset 11 → count byte
        Assert.Equal(255, data[11]);
    }

    [Fact]
    public void Character_PartySysMessage_UsesDelegate()
    {
        var lf = LoggerFactory.Create(b => { });
        var w = new GameWorld(lf);
        w.InitMap(0, 6144, 4096);

        var ch = w.CreateCharacter();
        ch.Name = "Tester";
        w.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        SphereNet.Network.Packets.PacketWriter? captured = null;
        Character.SendPacketToOwner = (c, pkt) => { if (c == ch) captured = pkt; };

        try
        {
            ch.TryExecuteCommand("PARTY.SYSMESSAGE", "Hello World", null!);
            Assert.NotNull(captured);
        }
        finally
        {
            Character.SendPacketToOwner = null;
        }
    }

    private static void ProcessPacket(NetworkManager network, NetState state, byte[] packet)
    {
        state.InjectReceived(packet);
        typeof(NetworkManager)
            .GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(network, [state]);
    }

    private static PacketBuffer AssertQueuedOpcode(NetState state, byte opcode)
    {
        var queue = TestHarness.GetQueuedPackets(state);
        Assert.True(queue.Count > 0, $"Expected queued packet 0x{opcode:X2}.");
        while (queue.Count > 0)
        {
            var packet = queue.Dequeue();
            if (packet.Length > 0 && packet.Span[0] == opcode)
                return packet;
        }

        throw new Xunit.Sdk.XunitException($"Queued packet 0x{opcode:X2} was not found.");
    }

    private static byte[] BuildLoginPacket(string account, string password)
    {
        byte[] packet = new byte[66];
        WriteUInt32(packet, 0, 0x12345678);
        packet[4] = 0x80;
        WriteAsciiFixed(packet, 5, 30, account);
        WriteAsciiFixed(packet, 35, 30, password);
        return packet;
    }

    private static byte[] BuildGameLoginPacket(uint authId, string account, string password)
    {
        byte[] packet = new byte[69];
        WriteUInt32(packet, 0, 0x87654321);
        packet[4] = 0x91;
        WriteUInt32(packet, 5, authId);
        WriteAsciiFixed(packet, 9, 30, account);
        WriteAsciiFixed(packet, 39, 30, password);
        return packet;
    }

    private static byte[] BuildCharSelectPacket(string name, int slot)
    {
        byte[] packet = new byte[73];
        packet[0] = 0x5D;
        WriteAsciiFixed(packet, 5, 30, name);
        WriteInt32(packet, 65, slot);
        return packet;
    }

    private static void WriteAsciiFixed(byte[] buffer, int offset, int length, string text)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length - 1));
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        WriteUInt32(buffer, offset, unchecked((uint)value));

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset) =>
        (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
}
