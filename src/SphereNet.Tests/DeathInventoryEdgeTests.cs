using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Death has to account for everything the character is holding, not just what is
/// tidily equipped or sitting in the pack.
///
/// An item on the cursor is parented to the character but is neither, so the loot
/// walk never saw it: it survived death attached to its owner, and the ghost could
/// drop it straight back into its own pack. Source-X reaches it because a dragged
/// item sits on LAYER_DRAGGING and UnEquipAllItems walks that layer
/// (CCharAct.cpp:636).
///
/// The pack transfer also uses a NARROWER protected set than equipment does, which
/// is Source-X's rule and not an oversight: CContainer::ContentsTransfer keeps only
/// NEWBIE / MOVE_NEVER / CURSED2 / BLESSED2 (CContainer.cpp:528), while
/// UnEquipAllItems additionally keeps BLESSED and friends.
/// </summary>
public sealed class DeathInventoryEdgeTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Str = 50; ch.MaxHits = ch.Hits = 50;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container; pack.BaseId = 0x0E75;
        ch.Backpack = pack; ch.Equip(pack, Layer.Pack);
        return ch;
    }

    private static Item MakeItem(GameWorld world, ushort baseId = 0x0F51)
    {
        var item = world.CreateItem();
        item.BaseId = baseId;
        return item;
    }

    /// <summary>Put <paramref name="item"/> on the character's cursor the way the
    /// pickup packet does: parented to the character, with the DRAGGING tag.</summary>
    private static void StartDragging(Character ch, Item item)
    {
        ch.Backpack?.RemoveItem(item);
        item.ContainedIn = ch.Uid;
        ch.SetTag("DRAGGING", item.Uid.Value.ToString());
    }

    // --- dragged item ------------------------------------------------------

    [Fact]
    public void AnItemOnTheCursorReachesTheCorpse()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world);

        var dagger = MakeItem(world);
        Assert.True(victim.Backpack!.TryAddItem(dagger));
        StartDragging(victim, dagger);

        var corpse = death.ProcessDeath(victim);

        Assert.NotNull(corpse);
        Assert.Contains(dagger, corpse!.Contents);
        Assert.False(victim.TryGetTag("DRAGGING", out _), "the drag was left open");
    }

    [Fact]
    public void AProtectedItemOnTheCursorStaysWithTheOwner()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world);

        var relic = MakeItem(world);
        relic.SetAttr(ObjAttributes.Newbie);
        Assert.True(victim.Backpack!.TryAddItem(relic));
        StartDragging(victim, relic);

        var corpse = death.ProcessDeath(victim);

        Assert.NotNull(corpse);
        Assert.DoesNotContain(relic, corpse!.Contents);
        Assert.Contains(relic, victim.Backpack.Contents);
    }

    [Fact]
    public void DeathWithNothingOnTheCursorIsUnchanged()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world);

        var loot = MakeItem(world);
        Assert.True(victim.Backpack!.TryAddItem(loot));

        var corpse = death.ProcessDeath(victim);

        Assert.NotNull(corpse);
        Assert.Contains(loot, corpse!.Contents);
    }

    [Fact]
    public void AStaleDragTagPointingAtNothingDoesNotBreakDeath()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world);
        victim.SetTag("DRAGGING", "4294967295");

        var corpse = death.ProcessDeath(victim);

        Assert.NotNull(corpse);
        Assert.False(victim.TryGetTag("DRAGGING", out _));
    }

    // --- pack protection set (Source-X ContentsTransfer) --------------------

    [Theory]
    [InlineData(ObjAttributes.Newbie)]
    [InlineData(ObjAttributes.Move_Never)]
    [InlineData(ObjAttributes.Cursed2)]
    [InlineData(ObjAttributes.Blessed2)]
    public void ThePackKeepsTheAttributesSourceXKeeps(ObjAttributes attr)
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world);

        var item = MakeItem(world);
        item.SetAttr(attr);
        Assert.True(victim.Backpack!.TryAddItem(item));

        var corpse = death.ProcessDeath(victim);

        Assert.NotNull(corpse);
        Assert.Contains(item, victim.Backpack.Contents);
        Assert.DoesNotContain(item, corpse!.Contents);
    }

    [Fact]
    public void PlainBlessedProtectsWhatYouWear_NotWhatIsLooseInThePack()
    {
        // Source-X UnEquipAllItems keeps ATTR_BLESSED; ContentsTransfer does not.
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world);

        var worn = MakeItem(world);
        worn.SetAttr(ObjAttributes.Blessed);
        victim.Equip(worn, Layer.Cape);

        var carried = MakeItem(world);
        carried.SetAttr(ObjAttributes.Blessed);
        Assert.True(victim.Backpack!.TryAddItem(carried));

        var corpse = death.ProcessDeath(victim);

        Assert.NotNull(corpse);
        Assert.Equal(worn, victim.GetEquippedItem(Layer.Cape));
        Assert.Contains(carried, corpse!.Contents);
    }

    [Fact]
    public void ANestedProtectedItemTravelsWithItsBag()
    {
        // Parity, not a defect: both engines transfer the pack one level deep, so
        // protection is a property of what you carry directly.
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world);

        var bag = world.CreateItem();
        bag.ItemType = ItemType.Container;
        Assert.True(victim.Backpack!.TryAddItem(bag));

        var relic = MakeItem(world);
        relic.SetAttr(ObjAttributes.Newbie);
        Assert.True(bag.TryAddItem(relic));

        var corpse = death.ProcessDeath(victim);

        Assert.NotNull(corpse);
        Assert.Contains(bag, corpse!.Contents);
        Assert.Contains(relic, bag.Contents);
    }
}
