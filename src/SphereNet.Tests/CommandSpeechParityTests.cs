using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X speech/command parity (wiki/12.txt audit): a plain player cannot set
// properties via the command prefix (the P0 escalation hole), the speaker's
// @Speech self-trigger can cancel an utterance, and being invisible does not stop
// a listener from hearing.
public class CommandSpeechParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakeChar(GameWorld world, PrivLevel priv, int x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = priv;
        ch.Str = 10;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    // ---- NPCDISTANCEHEAR: NPCs hear on their own radius, not the talk mode's ----

    /// <summary>Source-X scans for listening NPCs on the NPCDISTANCEHEAR radius
    /// (CClientEvent.cpp:1892) rather than the speaker's mode radius, so a yell
    /// does not reach NPCs far beyond it. The setting used to be parsed and
    /// ignored, leaving NPCs on the 48-tile yell range.</summary>
    [Fact]
    public void NpcDistanceHear_CapsHowFarAYellReachesNpcs()
    {
        var world = CreateWorld();
        var speech = new SpeechEngine(world) { NpcDistanceHear = 16 };
        var speaker = MakeChar(world, PrivLevel.Player);

        var near = world.CreateCharacter();
        near.IsPlayer = false;
        world.PlaceCharacter(near, new Point3D(110, 100, 0, 0)); // 10 tiles
        var far = world.CreateCharacter();
        far.IsPlayer = false;
        world.PlaceCharacter(far, new Point3D(130, 100, 0, 0));  // 30 tiles

        var heard = new List<Character>();
        speech.OnNpcHear += (_, listener, _, _) => heard.Add(listener);

        speech.ProcessSpeech(speaker, "hello there", TalkMode.Yell, 0, 3);

        Assert.Contains(near, heard);
        Assert.DoesNotContain(far, heard); // inside the 48-tile yell, outside 16
    }

    /// <summary>Left at 0 the NPC scan falls back to the default sight range,
    /// so an unconfigured server keeps hearing NPCs at 18 tiles.</summary>
    [Fact]
    public void NpcDistanceHear_Zero_UsesTheDefaultSightRange()
    {
        var world = CreateWorld();
        var speech = new SpeechEngine(world); // NpcDistanceHear stays 0
        var speaker = MakeChar(world, PrivLevel.Player);

        var npc = world.CreateCharacter();
        npc.IsPlayer = false;
        world.PlaceCharacter(npc, new Point3D(117, 100, 0, 0)); // 17 tiles

        var heard = new List<Character>();
        speech.OnNpcHear += (_, listener, _, _) => heard.Add(listener);

        speech.ProcessSpeech(speaker, "hello there", TalkMode.Say, 0, 3);

        Assert.Contains(npc, heard);
    }

    // ---- P0: players cannot set properties via the command prefix ----

    [Theory]
    [InlineData("GM=1")]
    [InlineData("INVUL=1")]
    [InlineData("MAGERY=1200")]
    [InlineData("STR=5000")]
    [InlineData("NAME=Cheater")]
    [InlineData("EVENTS=e_evil")]
    public void CommandPropertySet_Player_IsRejected(string commandLine)
    {
        var world = CreateWorld();
        var cmds = new CommandHandler();
        var player = MakeChar(world, PrivLevel.Player);

        var result = cmds.TryExecute(player, commandLine);

        Assert.Equal(CommandResult.InsufficientPriv, result);
        Assert.Equal(10, player.Str); // STR=5000 must not have applied
    }

    [Fact]
    public void CommandPropertySet_Staff_IsAllowed()
    {
        var world = CreateWorld();
        var cmds = new CommandHandler();
        var staff = MakeChar(world, PrivLevel.GM);

        var result = cmds.TryExecute(staff, "STR=100");

        Assert.Equal(CommandResult.Executed, result);
        Assert.Equal(100, staff.Str);
    }

    // ---- CommandTrigger (Source-X f_oncommand) ----

    [Fact]
    public void CommandTrigger_Return1_ConsumesTheCommand_BeforeDispatch()
    {
        var world = CreateWorld();
        var cmds = new CommandHandler();
        var player = MakeChar(world, PrivLevel.Player);

        string? seenLine = null;
        // The hook mirrors the wiring: RETURN 1 (true) = consumed. The pack's
        // f_oncommand intercepts REMOVE/FLIP/MTELE this way.
        cmds.CommandTriggerHook = (gm, line) =>
        {
            seenLine = line;
            return line.Contains("MYCUSTOM", StringComparison.OrdinalIgnoreCase);
        };

        // A command the hook claims: consumed, never reaches native dispatch.
        Assert.Equal(CommandResult.Executed, cmds.TryExecute(player, "MYCUSTOM foo"));
        Assert.Equal("MYCUSTOM foo", seenLine);

        // A command the hook declines still flows to normal dispatch: a GM's
        // native property-set runs, proving the hook does not swallow it.
        var staff = MakeChar(world, PrivLevel.GM, x: 105);
        Assert.Equal(CommandResult.Executed, cmds.TryExecute(staff, "STR=77"));
        Assert.Equal("STR=77", seenLine); // the hook saw it but declined
        Assert.Equal(77, staff.Str);      // native dispatch then applied it
    }

    // ---- P1: @Speech self-trigger RETURN 1 cancels the utterance ----

    [Fact]
    public void ProcessSpeech_SelfTriggerCancels_SuppressesNpcHear()
    {
        var world = CreateWorld();
        var speech = new SpeechEngine(world);
        var speaker = MakeChar(world, PrivLevel.Player);

        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        bool npcHeard = false;
        speech.OnNpcHear += (_, _, _, _) => npcHeard = true;

        // Self @Speech returns cancel → ProcessSpeech reports "do not broadcast"
        // and the NPC-hear loop is skipped.
        speech.OnPlayerSpeech = (_, t, _) => (true, t);
        Assert.True(speech.ProcessSpeech(speaker, "hello", TalkMode.Say));
        Assert.False(npcHeard);

        // Not cancelled → normal flow, NPC hears.
        speech.OnPlayerSpeech = (_, t, _) => (false, t);
        Assert.False(speech.ProcessSpeech(speaker, "hello", TalkMode.Say));
        Assert.True(npcHeard);
    }

    [Fact]
    public void ProcessSpeech_SelfTriggerRewritesText_PropagatesToHearAndBroadcast()
    {
        var world = CreateWorld();
        var speech = new SpeechEngine(world);
        var speaker = MakeChar(world, PrivLevel.Player);

        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        string? heardText = null;
        speech.OnNpcHear += (_, _, t, _) => heardText = t;

        // @Speech rewrites the utterance via ARGS (Source-X text rewrite): the NPC
        // hears the rewritten words and the caller's broadcast text (finalText) matches.
        speech.OnPlayerSpeech = (_, _, _) => (false, "reworded");

        bool cancelled = speech.ProcessSpeech(speaker, "original", TalkMode.Say, 0x03B2, 3, out string finalText);

        Assert.False(cancelled);
        Assert.Equal("reworded", finalText);
        Assert.Equal("reworded", heardText); // the NPC-hear routing used the rewrite
    }

    // ---- P2: invisible listeners still hear ----

    [Fact]
    public void ProcessSpeech_InvisibleNpc_StillHearsWhisper()
    {
        var world = CreateWorld();
        var speech = new SpeechEngine(world);
        var speaker = MakeChar(world, PrivLevel.Player);

        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;
        npc.SetStatFlag(StatFlag.Invisible);
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        bool npcHeard = false;
        speech.OnNpcHear += (_, listener, _, _) => { if (listener == npc) npcHeard = true; };

        speech.ProcessSpeech(speaker, "psst", TalkMode.Whisper);

        Assert.True(npcHeard); // invisibility must not block hearing
    }

    // ---- item @Hear coverage: nearby items hear, distant ones do not ----

    [Fact]
    public void ProcessSpeech_FiresItemHear_ForInRangeItemsOnly()
    {
        var world = CreateWorld();
        var speech = new SpeechEngine(world);
        var speaker = MakeChar(world, PrivLevel.Player); // at (100,100)

        // Listen-capable items (comm crystals): a mundane ground item no longer
        // opens the scan at all (Source-X per-sector listen-item gate, S3).
        var near = world.CreateItem();
        near.ItemType = ItemType.CommCrystal;
        world.PlaceItem(near, new Point3D(102, 100, 0, 0)); // 2 tiles — within say range (18)
        var far = world.CreateItem();
        far.ItemType = ItemType.CommCrystal;
        world.PlaceItem(far, new Point3D(100, 140, 0, 0));  // 40 tiles — out of say range

        var heard = new List<Item>();
        speech.OnItemHear = (_, item, _, _) => heard.Add(item);

        speech.ProcessSpeech(speaker, "hail", TalkMode.Say);

        Assert.Contains(near, heard);
        Assert.DoesNotContain(far, heard);
    }

    [Fact]
    public void ProcessSpeech_NoItemHearHook_SkipsItemScan()
    {
        var world = CreateWorld();
        var speech = new SpeechEngine(world);
        var speaker = MakeChar(world, PrivLevel.Player);

        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(101, 100, 0, 0));

        // OnItemHear not installed → no throw, item scan skipped (gated path).
        Assert.False(speech.ProcessSpeech(speaker, "hail", TalkMode.Say));
    }
}
