using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Blades, cannons, wheels and bedrolls.
///
/// Source-X gates a consuming use behind CanUse(target, MOVE) - reach plus the move
/// rules and the take-crime check (CCharStatus.cpp:1736) - and converts a fish where
/// it lies (CClientTarg.cpp:1919). Shearing hangs a timed fleece on the sheep, and
/// when it expires the shorn body becomes a sheep again (:1862; CCharAct.cpp:4067).
/// A fruit or raw reagent cut open becomes a DEFAULTSEED seed (:1939). Loading a
/// cannon checks the muzzle and the charge (Use_Cannon_Feed, CCharUse.cpp:298). A
/// spinning wheel is busy for two seconds afterwards (SetAnim, :2029). A bedroll
/// opens on the ground and rolls back up (Use_BedRoll, CCharUse.cpp:1534).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TargetedUseParity08BTests
{
    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack);

    private static Bench Setup()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8201);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = 100; me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);

        return new Bench(world, client, me, pack);
    }

    private static Item Blade(Bench bench)
    {
        var blade = bench.World.CreateItem();
        blade.ItemType = ItemType.WeaponSword;
        Assert.True(bench.Pack.TryAddItem(blade));
        return blade;
    }

    private static void UseOn(Bench bench, Item tool, uint targetSerial)
    {
        bench.Client.HandleDoubleClick(tool.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            targetSerial, 0, 0, 0, 0);
    }

    // --- 08B-1: cutting a fish ------------------------------------------

    private static Item Fish(Bench bench, Item? container = null, Point3D? onGround = null)
    {
        var fish = bench.World.CreateItem();
        fish.BaseId = 0x09CC;
        fish.ItemType = ItemType.Fish;
        fish.Amount = 2;
        if (onGround is { } spot)
            bench.World.PlaceItem(fish, spot);
        else
            Assert.True((container ?? bench.Pack).TryAddItem(fish));
        return fish;
    }

    [Fact]
    public void AFishIsCutWhereItLies()
    {
        var bench = Setup();
        var fish = Fish(bench);

        UseOn(bench, Blade(bench), fish.Uid.Value);

        Assert.False(fish.IsDeleted);              // the same object, converted
        Assert.Equal(0x097A, fish.BaseId);
        Assert.Equal(ItemType.Food, fish.ItemType);
        Assert.Equal(8, fish.Amount);              // four steaks per fish
    }

    [Fact]
    public void AFixtureFishIsNotCutUp()
    {
        var bench = Setup();
        var fish = Fish(bench, onGround: new Point3D(101, 100, 0, 0));
        fish.SetAttr(ObjAttributes.Move_Never);

        UseOn(bench, Blade(bench), fish.Uid.Value);

        Assert.False(fish.IsDeleted);
        Assert.Equal(ItemType.Fish, fish.ItemType);
        Assert.Equal(2, fish.Amount);
        Assert.DoesNotContain(bench.Pack.Contents, i => i.BaseId == 0x097A);
    }

    [Fact]
    public void SomebodyElsesCatchIsNotCutUp()
    {
        var bench = Setup();
        var other = bench.World.CreateCharacter();
        other.IsPlayer = true;
        bench.World.PlaceCharacter(other, new Point3D(101, 100, 0, 0));
        var theirPack = bench.World.CreateItem();
        theirPack.ItemType = ItemType.Container;
        other.Backpack = theirPack;
        other.Equip(theirPack, Layer.Pack);

        var fish = Fish(bench, theirPack);

        UseOn(bench, Blade(bench), fish.Uid.Value);

        Assert.False(fish.IsDeleted);
        Assert.Equal(ItemType.Fish, fish.ItemType);
        Assert.DoesNotContain(bench.Pack.Contents, i => i.BaseId == 0x097A);
    }

    // --- 08B-2: the fleece grows back -----------------------------------

    private static Character Sheep(Bench bench)
    {
        var sheep = bench.World.CreateCharacter();
        sheep.BodyId = 0x00CF;
        bench.World.PlaceCharacter(sheep, new Point3D(101, 100, 0, 0));
        return sheep;
    }

    [Fact]
    public void ShearingLeavesAFleeceGrowingBack()
    {
        var bench = Setup();
        var sheep = Sheep(bench);

        UseOn(bench, Blade(bench), sheep.Uid.Value);

        Assert.Equal(0x00DF, sheep.BodyId);
        var regrow = sheep.GetEquippedItem(Layer.FlagWool);
        Assert.NotNull(regrow);
        Assert.True(regrow!.Timeout > 0);
    }

    [Fact]
    public void TheFleeceComesBackWhenItsTimeIsUp()
    {
        var bench = Setup();
        var sheep = Sheep(bench);
        UseOn(bench, Blade(bench), sheep.Uid.Value);

        var regrow = sheep.GetEquippedItem(Layer.FlagWool)!;
        regrow.SetTimeout(1);                       // due now
        regrow.OnTick();

        Assert.Equal(0x00CF, sheep.BodyId);
        Assert.True(regrow.IsDeleted);
    }

    [Fact]
    public void AShornSheepHasNothingToGive()
    {
        var bench = Setup();
        var sheep = Sheep(bench);
        UseOn(bench, Blade(bench), sheep.Uid.Value);
        int woolAfterFirst = bench.Pack.Contents.Count(i => i.BaseId == 0x0DF8);

        UseOn(bench, Blade(bench), sheep.Uid.Value);

        Assert.Equal(woolAfterFirst, bench.Pack.Contents.Count(i => i.BaseId == 0x0DF8));
    }

    // --- 08B-5: a fruit becomes a seed ----------------------------------

    [Theory]
    [InlineData(ItemType.Fruit)]
    [InlineData(ItemType.ReagentRaw)]
    public void CuttingAFruitOpenGivesASeed(ItemType kind)
    {
        var bench = Setup();
        Item.ResolveDefName = name => name == "DEFAULTSEED" ? (ushort)0x0DCF : (ushort)0;

        var fruit = bench.World.CreateItem();
        fruit.BaseId = 0x09D0;
        fruit.ItemType = kind;
        fruit.Name = "apple";
        Assert.True(bench.Pack.TryAddItem(fruit));

        UseOn(bench, Blade(bench), fruit.Uid.Value);

        Assert.Equal(ItemType.Seed, fruit.ItemType);
        Assert.Equal(0x0DCF, fruit.BaseId);
        Assert.Equal("apple seed", fruit.Name);
    }

    // --- 08B-4: loading a cannon ----------------------------------------

    private static (Item Cannon, Item Ball) Battery(Bench bench, Point3D? cannonAt = null,
        Item? ballIn = null)
    {
        var cannon = bench.World.CreateItem();
        cannon.ItemType = ItemType.CannonMuzzle;
        bench.World.PlaceItem(cannon, cannonAt ?? new Point3D(101, 100, 0, 0));

        var ball = bench.World.CreateItem();
        ball.ItemType = ItemType.CannonBall;
        ball.Amount = 2;
        Assert.True((ballIn ?? bench.Pack).TryAddItem(ball));
        return (cannon, ball);
    }

    private static void Load(Bench bench, Item cannon, Item ball)
    {
        bench.Client.HandleDoubleClick(cannon.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            ball.Uid.Value, 0, 0, 0, 0);
    }

    [Fact]
    public void ACannonWithinReachTakesTheShot()
    {
        var bench = Setup();
        var (cannon, ball) = Battery(bench);

        Load(bench, cannon, ball);

        Assert.Equal(2u, cannon.More1 & 2);
        Assert.Equal(1, ball.Amount);
    }

    [Fact]
    public void ACannonLeftBehindTakesNothing()
    {
        // The muzzle is checked again when the charge is chosen, not only when the
        // cursor opened: the reference asks CanUse(cannon, false) at that moment.
        var bench = Setup();
        var (cannon, ball) = Battery(bench);

        bench.Client.HandleDoubleClick(cannon.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.World.MoveCharacter(bench.Me, new Point3D(140, 100, 0, 0));
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            ball.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(0u, cannon.More1);
        Assert.Equal(2, ball.Amount);
    }

    [Fact]
    public void AFixedChargeIsNotSpent()
    {
        var bench = Setup();
        var (cannon, ball) = Battery(bench);
        ball.SetAttr(ObjAttributes.Move_Never);

        Load(bench, cannon, ball);

        Assert.Equal(0u, cannon.More1);
        Assert.Equal(2, ball.Amount);
    }

    // --- 08B-6: the wheel is busy afterwards ----------------------------

    [Fact]
    public void ASpinningWheelIsBusyForAMoment()
    {
        var bench = Setup();
        var wheel = bench.World.CreateItem();
        wheel.BaseId = 0x1015;
        wheel.ItemType = ItemType.SpinWheel;
        bench.World.PlaceItem(wheel, new Point3D(101, 100, 0, 0));

        var wool = bench.World.CreateItem();
        wool.ItemType = ItemType.Wool;
        wool.Amount = 3;
        Assert.True(bench.Pack.TryAddItem(wool));

        UseOn(bench, wool, wheel.Uid.Value);

        Assert.Equal(ItemType.AnimActive, wheel.ItemType);
        Assert.Equal(0x1016, wheel.DispIdFull);
        Assert.True(wheel.Timeout > 0);

        // A second batch cannot be fed to a wheel that is still turning.
        var more = bench.World.CreateItem();
        more.ItemType = ItemType.Wool;
        more.Amount = 3;
        Assert.True(bench.Pack.TryAddItem(more));
        UseOn(bench, more, wheel.Uid.Value);
        Assert.Equal(3, more.Amount);

        // And it comes back to itself when it stops.
        wheel.SetTimeout(1);
        wheel.OnTick();
        Assert.Equal(ItemType.SpinWheel, wheel.ItemType);
        Assert.Equal(0x1015, wheel.DispIdFull);
    }

    // --- 08B-3: the bedroll ---------------------------------------------

    private static Item Bedroll(Bench bench, ushort id, bool onGround = true)
    {
        var roll = bench.World.CreateItem();
        roll.BaseId = id;
        roll.ItemType = ItemType.Bedroll;
        if (onGround)
            bench.World.PlaceItem(roll, bench.Me.Position);
        else
            Assert.True(bench.Pack.TryAddItem(roll));
        return roll;
    }

    [Theory]
    [InlineData(0x0A58, 0x0A56)]   // rolled north-south opens north-south
    [InlineData(0x0A59, 0x0A55)]   // rolled east-west opens east-west
    public void ARolledBedrollOpensTheWayItIsRolled(ushort closed, ushort open)
    {
        var bench = Setup();
        var roll = Bedroll(bench, closed);

        bench.Client.HandleDoubleClick(roll.Uid.Value);

        Assert.Equal(open, roll.BaseId);
    }

    [Fact]
    public void APlainRolledBedrollOpensOneWayOrTheOther()
    {
        var bench = Setup();
        var roll = Bedroll(bench, 0x0A57);

        bench.Client.HandleDoubleClick(roll.Uid.Value);

        Assert.Contains(roll.BaseId, new ushort[] { 0x0A55, 0x0A56 });
    }

    [Theory]
    [InlineData(0x0A55)]
    [InlineData(0x0A56)]
    public void AnOpenBedrollRollsBackUp(ushort open)
    {
        var bench = Setup();
        var roll = Bedroll(bench, open);

        bench.Client.HandleDoubleClick(roll.Uid.Value);

        Assert.Equal(0x0A57, roll.BaseId);
    }

    [Fact]
    public void ABedrollInThePackHasToBePutDownFirst()
    {
        var bench = Setup();
        var roll = Bedroll(bench, 0x0A57, onGround: false);

        bench.Client.HandleDoubleClick(roll.Uid.Value);

        Assert.Equal(0x0A57, roll.BaseId);
    }
}
