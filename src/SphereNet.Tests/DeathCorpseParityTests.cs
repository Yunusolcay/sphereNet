using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X death/corpse parity (wiki/6.txt audit): @Death/@Kill RETURN 1 honored
// (cancel death / skip killer credit), forensics+carve tag alignment, expanded
// loot retention, and corpse-looting criminality gated on a still-present innocent
// player owner.
public class DeathCorpseParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x, short karma = 0, short fame = 0)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Str = 50; ch.MaxHits = 50; ch.Hits = 50;
        ch.Karma = karma; ch.Fame = fame;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    private static void ClearNotoHooks()
    {
        Character.OnFameChanging = null;
        Character.OnKarmaChanging = null;
        Character.OnMurderMark = null;
    }

    // ---- A: @Death / @Kill RETURN 1 ----

    [Fact]
    public void Death_TriggerReturnsTrue_CancelsDeath()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Death", (_, _) => TriggerResult.True);
        death.TriggerDispatcher = dispatcher;

        var victim = MakePlayer(world, 100);
        var killer = MakePlayer(world, 101);

        var corpse = death.ProcessDeath(victim, killer);

        Assert.Null(corpse);             // no corpse made
        Assert.False(victim.IsDead);     // death vetoed — victim still alive
    }

    [Fact]
    public void Kill_TriggerReturnsTrue_SkipsKillerCredit_ButDeathProceeds()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);
        var dispatcher = new TriggerDispatcher();
        // @Kill fires on the killer (a player → EVENTSPLAYER global set).
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Kill", (_, _) => TriggerResult.True);
        death.TriggerDispatcher = dispatcher;

        var killer = MakePlayer(world, 100, karma: 0, fame: 0);
        var victim = MakePlayer(world, 101, karma: 1000, fame: 1000); // innocent

        var corpse = death.ProcessDeath(victim, killer);

        Assert.NotNull(corpse);          // death still happened
        Assert.True(victim.IsDead);
        Assert.Equal(0, killer.Kills);   // murder credit skipped
        Assert.Equal(0, killer.Fame);    // fame credit skipped
        Assert.False(killer.IsCriminal);
    }

    // ---- B: forensics / carve tags ----

    [Fact]
    public void Corpse_HasDeathTimeTag_AndCarveSetsCorpseCarved()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var victim = MakePlayer(world, 100);
        var corpse = death.ProcessDeath(victim);
        Assert.NotNull(corpse);
        Assert.True(corpse!.TryGetTag("DEATH_TIME", out _)); // forensics reads this

        death.CarveCorpse(victim, corpse);
        Assert.True(corpse.TryGetTag("CORPSE_CARVED", out string? cv) && cv == "1");
    }

    // ---- C: looting criminality ----

    [Fact]
    public void Looting_MonsterCorpse_IsNotCriminal()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world) { LootingIsACrime = true };

        var npc = world.CreateCharacter(); // not a player → deleted on death
        npc.Str = 50; npc.MaxHits = npc.Hits = 50;
        world.PlaceCharacter(npc, new Point3D(1000, 1000, 0, 0));
        var corpse = death.ProcessDeath(npc);
        Assert.NotNull(corpse);

        var looter = MakePlayer(world, 1001);
        Assert.False(death.IsLootingCriminal(looter, corpse!)); // owner gone → free loot
    }

    [Fact]
    public void Looting_InnocentPlayerCorpse_IsCriminal()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world) { LootingIsACrime = true };

        var owner = MakePlayer(world, 1000); // innocent player victim (stays as ghost)
        var corpse = death.ProcessDeath(owner);
        Assert.NotNull(corpse);

        var looter = MakePlayer(world, 1002);
        Assert.True(death.IsLootingCriminal(looter, corpse!));
        Assert.False(death.IsLootingCriminal(owner, corpse!)); // own corpse exempt
    }

    [Fact]
    public void Looting_PartyMemberCorpse_GatedOnOwnersLootFlag()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var pm = new SphereNet.Game.Party.PartyManager();
        var death = new DeathEngine(world) { LootingIsACrime = true, PartyManager = pm };

        var owner = MakePlayer(world, 1000); // innocent victim (stays as ghost)
        var corpse = death.ProcessDeath(owner);
        Assert.NotNull(corpse);

        var looter = MakePlayer(world, 1002);

        // Same party, but the owner has not granted loot rights → still criminal.
        var party = pm.CreateParty(owner.Uid);
        party.AddMember(looter.Uid);
        Assert.True(death.IsLootingCriminal(looter, corpse!));

        // The OWNER grants loot rights ("the party may loot me") → no longer criminal.
        party.SetLootFlag(owner.Uid, true);
        Assert.False(death.IsLootingCriminal(looter, corpse!));

        // The flag belongs to the corpse owner, not the looter: enabling only the
        // looter's own flag must NOT re-permit looting (guards the previously inverted check).
        party.SetLootFlag(owner.Uid, false);
        party.SetLootFlag(looter.Uid, true);
        Assert.True(death.IsLootingCriminal(looter, corpse!));
    }

    [Fact]
    public void HumanDeathSound_IsGenderSpecificAndInRange()
    {
        var rng = new Random(7);
        bool sawFemaleMin = false, sawFemaleMax = false, sawMaleMin = false, sawMaleMax = false;
        for (int i = 0; i < 400; i++)
        {
            // ServUO GetDeathSound: female Random(0x314, 4), male Random(0x423, 5).
            int female = DeathEngine.GetHumanDeathSound(true, rng);
            Assert.InRange(female, 0x314, 0x317);
            int male = DeathEngine.GetHumanDeathSound(false, rng);
            Assert.InRange(male, 0x423, 0x427);

            if (female == 0x314) sawFemaleMin = true;
            if (female == 0x317) sawFemaleMax = true;
            if (male == 0x423) sawMaleMin = true;
            if (male == 0x427) sawMaleMax = true;
        }
        // Both ends of each gender's range are reachable, and the ranges are disjoint.
        Assert.True(sawFemaleMin && sawFemaleMax && sawMaleMin && sawMaleMax);
    }

    // ---- D: loot retention ----

    [Fact]
    public void DropLoot_KeepsMoveNeverAndNoTradeItems_OnOwner()
    {
        var world = CreateWorld();
        ClearNotoHooks();
        var death = new DeathEngine(world);

        var victim = MakePlayer(world, 100);

        // Equipment and pack contents use DIFFERENT protected sets in Source-X.
        // UnEquipAllItems keeps NEWBIE/MOVE_NEVER/BLESSED/INSURED/NODROP/NOTRADE/
        // QUESTITEM (CCharAct.cpp:651), while ContentsTransfer keeps only
        // NEWBIE/MOVE_NEVER/CURSED2/BLESSED2 (CContainer.cpp:528). A no-trade item
        // therefore protects what you wear, not what is loose in your pack.
        var ring = world.CreateItem();
        ring.BaseId = 0x108A;
        ring.SetAttr(ObjAttributes.Move_Never);
        victim.Equip(ring, Layer.Ring);

        var wornBound = world.CreateItem();
        wornBound.BaseId = 0x1F02;
        wornBound.SetAttr(ObjAttributes.NotRading);
        victim.Equip(wornBound, Layer.Cape);

        var packMoveNever = world.CreateItem();
        packMoveNever.BaseId = 0x1F03;
        packMoveNever.SetAttr(ObjAttributes.Move_Never);
        victim.Backpack!.AddItem(packMoveNever);

        var packBound = world.CreateItem();
        packBound.BaseId = 0x1F05;
        packBound.SetAttr(ObjAttributes.NotRading);
        victim.Backpack!.AddItem(packBound);

        var loose = world.CreateItem();
        loose.BaseId = 0x1F04;
        victim.Backpack!.AddItem(loose);

        var corpse = death.ProcessDeath(victim);
        Assert.NotNull(corpse);

        // Source-X UnEquipAllItems moves protected equipment into the pack rather
        // than leaving it worn (CCharAct.cpp:664), so the layer is empty afterwards.
        Assert.Null(victim.GetEquippedItem(Layer.Ring));
        Assert.Contains(victim.Backpack!.Contents, i => i.Uid == ring.Uid);
        Assert.Contains(victim.Backpack!.Contents, i => i.Uid == wornBound.Uid);
        Assert.DoesNotContain(corpse!.Contents, i => i.Uid == ring.Uid);
        Assert.DoesNotContain(corpse.Contents, i => i.Uid == wornBound.Uid);
        Assert.DoesNotContain(corpse.Contents, i => i.Uid == packMoveNever.Uid);
        Assert.Contains(corpse.Contents, i => i.Uid == packBound.Uid);
        Assert.Contains(corpse.Contents, i => i.Uid == loose.Uid);
    }
}
