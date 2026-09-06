using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the wired P1 triggers @Eat, @SkillMenu and @SkillWait. @Eat fires (and
// can block) on eating/drinking; @SkillMenu fires when a skill opens a selection
// menu (Tracking); @SkillWait fires when another skill is requested while an
// action is already in progress.
public class P1SkillEventTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (GameClient client, Character player, TriggerDispatcher d, List<string> order) Setup(GameWorld world)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1601);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        TestHarness.AttachCharacter(client, player);

        var d = new TriggerDispatcher();
        var order = new List<string>();
        client.SetEngines(skillHandlers: new SkillHandlers(world), triggerDispatcher: d);
        return (client, player, d, order);
    }

    [Fact]
    public void Eat_Food_FiresEatTrigger_AndFeedsByDefault()
    {
        var world = CreateWorld();
        var (client, player, d, _) = Setup(world);
        int eatN1 = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "Eat", (_, a) => { eatN1 = a.N1; return TriggerResult.Default; });

        player.Food = 0;
        var food = world.CreateItem();
        food.ItemType = ItemType.Food;
        food.Amount = 1;
        player.Backpack!.AddItem(food);

        client.HandleDoubleClick(food.Uid.Value);

        // ARGN1 is a STAT LIMIT and starts at ZERO, not the hunger restored: EatAnim
        // seeds it from uiStatsLimit (CCharAct.cpp:3456) and carries the gains in
        // LOCAL.Hits / Mana / Stam / Food instead. This assertion used to expect the
        // 5 the old hand-written call passed, which no script could act on.
        Assert.Equal(0, eatN1);
        Assert.Equal(10, player.Food);   // fed by the food's own value, not a flat 5
        Assert.True(food.IsDeleted);     // single unit consumed
    }

    [Fact]
    public void Eat_Food_ReturnTrue_BlocksTheGainButNotTheMeal()
    {
        var world = CreateWorld();
        var (client, player, d, _) = Setup(world);
        d.RegisterCharEvent("EVENTSPLAYER", "Eat", (_, _) => TriggerResult.True);

        player.Food = 0;
        var food = world.CreateItem();
        food.ItemType = ItemType.Food;
        food.Amount = 1;
        player.Backpack!.AddItem(food);

        client.HandleDoubleClick(food.Uid.Value);

        // RETURN 1 skips the GAINS and nothing else. EatAnim returns early
        // (CCharAct.cpp:3469) and its caller consumes the food regardless
        // (Use_EatQty, CCharUse.cpp:913) - so a vetoing script blocks the benefit,
        // not the meal. This test used to assert the food survived.
        Assert.Equal(0, player.Food);    // no gain
        Assert.True(food.IsDeleted);     // but the meal is still spent
    }

    [Fact]
    public void SkillMenu_TrackingOpensMenu_FiresSkillMenu()
    {
        var world = CreateWorld();
        var (client, player, d, _) = Setup(world);
        int menuSkill = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "SkillMenu", (_, a) => { menuSkill = a.N1; return TriggerResult.Default; });

        client.HandleUseSkill((int)SkillType.Tracking); // menu-kind skill

        Assert.Equal((int)SkillType.Tracking, menuSkill);
    }

    [Fact]
    public void SkillWait_NewSkillAttempt_FiresWithRequestedAndCurrentSkill()
    {
        var world = CreateWorld();
        var (client, player, d, _) = Setup(world);
        int requestedSkill = -1;
        int currentSkill = -1;
        d.RegisterCharEvent("EVENTSPLAYER", "SkillWait", (_, a) =>
        {
            requestedSkill = a.N1;
            currentSkill = a.N2;
            return TriggerResult.Default;
        });

        int skillId = (int)SkillType.Hiding;
        player.BeginSkillPending(skillId, delayEnd: long.MaxValue / 2, strokeNext: long.MaxValue / 2, Serial.Invalid, null);

        client.HandleUseSkill((int)SkillType.Tracking);

        Assert.Equal((int)SkillType.Tracking, requestedSkill);
        Assert.Equal(skillId, currentSkill);
        Assert.False(player.HasActiveSkillPending()); // Hiding can be replaced by another skill.
    }
}
