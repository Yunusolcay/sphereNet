using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit findings (wiki/test.txt #6/#7): the Polymorph/Summon selection menus
/// must start the REAL cast instead of applying instantly, and the mana
/// requirement/consumption must use one discounted cost (wand free, scroll
/// half) at both cast start and completion.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class MagicCastFlowParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void PolymorphMenuSelection_RunsTheRealCast_NotInstantBodySwap()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Polymorph,
            Name = "Polymorph",
            Flags = SpellFlag.TargChar | SpellFlag.Good,
            ManaCost = 0,
            CastTimeBase = 10, // 1.0s — the cast must NOT complete instantly
        });
        var engine = new SpellEngine(world, registry);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.GM; // deterministic: no fizzle
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2612);
        client.SetEngines(spellEngine: engine);
        TestHarness.AttachCharacter(client, player);

        ushort bodyBefore = player.BodyId;

        // The cast flow opened the sm_polymorph menu and marked it; the menu
        // entry now runs the POLY verb with the pick.
        player.SetTag("CAST_MENU_SPELL", ((int)SpellType.Polymorph).ToString());
        Assert.True(client.TryExecuteScriptCommand(player, "POLY", "400", null));

        // Selection must START a cast, not swap the body immediately.
        Assert.True(player.IsCasting, "menu selection did not start the real cast");
        Assert.Equal(bodyBefore, player.BodyId);

        // Complete the cast: the selected form (0x190 = 400) applies with the
        // timed Polymorph effect.
        player.SetCastTimerEnd(1);
        client.TickSpellCast();
        Assert.Equal((ushort)400, player.BodyId);
        Assert.True(player.IsStatFlag(StatFlag.Polymorph));
    }

    [Fact]
    public void WandCast_RequiresNoMana_AtCastStart()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        var def = new SpellDef
        {
            Id = SpellType.Heal,
            Name = "Heal",
            Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
            ManaCost = 40,
            CastTimeBase = 5,
        };
        registry.Register(def);
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100;
        caster.Mana = 0; // no mana at all
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var wand = world.CreateItem();
        wand.ItemType = ItemType.Wand;
        caster.Equip(wand, Layer.OneHanded);

        int castTime = engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position);
        Assert.True(castTime > 0, "wand cast refused for lack of mana it does not need");
    }

    [Fact]
    public void ScrollCast_ChecksAndConsumesTheSameHalvedCost()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Heal,
            Name = "Heal",
            Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
            ManaCost = 40,
            CastTimeBase = 5,
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100;
        caster.Mana = 25; // below full cost (40), above the scroll half (20)
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        // A real scroll on the caster: the discount is priced off the RESOLVED
        // source now, not off the bare presence of the tag.
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        caster.Backpack = pack;
        caster.Equip(pack, Layer.Pack);
        var scroll = world.CreateItem();
        scroll.ItemType = ItemType.Scroll;
        scroll.More1 = (uint)SpellType.Heal;
        Assert.True(pack.TryAddItem(scroll));
        caster.SetTag("SCROLL_UID", scroll.Uid.Value.ToString());

        int castTime = engine.CastStart(caster, SpellType.Heal, caster.Uid, caster.Position);
        Assert.True(castTime > 0, "scroll cast demanded the FULL mana cost at start");

        Assert.True(engine.CastDone(caster));
        Assert.Equal(5, caster.Mana); // consumed exactly the halved cost (20)
    }
}
