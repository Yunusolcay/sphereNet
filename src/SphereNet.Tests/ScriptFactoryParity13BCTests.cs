using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The object factories and the NEW reference (reviews 13B and 13C).
///
/// Source-X keeps ONE global reference to the last object a factory produced -
/// g_World.m_uidNew - and NEWITEM, NEWNPC and NEWDUPE all write it at the END of
/// their work (CScriptObj.cpp:1381), so nested creation inside an @Create hook cannot
/// steal it. A failed NEWDUPE clears it instead (:1311). The bare form of a factory
/// additionally points the CALLER'S ACT at what it made, which the explicit SERV form
/// does not (:1383). NEWITEM itself takes four fields - id, amount, parent, equip flag
/// - accepts a TEMPLATE header as readily as an ITEMDEF one (CreateHeader,
/// CItem.cpp:461), and equips a character parent at the layer its definition declares
/// rather than dropping everything in the pack (LoadSetContainer, CItem.cpp:2516).
/// A new NPC starts with full pools (CreateNewCharCheck, CChar.cpp:1042).
///
/// These exercise the parts that live in the world model; the host bridge that drives
/// them is covered by the script-surface tests.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ScriptFactoryParity13BCTests
{
    // ============================================================ 13C-1
    // One reference for both NEW and NEW.<prop>, and "nothing yet" is not a uid.

    [Fact]
    public void TheNewReferenceStartsInvalidRatherThanZero()
    {
        var world = TestHarness.CreateWorld();

        // Serial.Invalid is 0xFFFFFFFF, so a "!= 0" test would call this a real uid.
        Assert.False(world.LastNewObject.IsValid);
    }

    [Fact]
    public void TheNewReferenceFollowsTheLastObjectCreatedWhateverItsType()
    {
        var world = TestHarness.CreateWorld();

        var item = world.CreateItem();
        Assert.Equal(item.Uid, world.LastNewObject);

        // An NPC created after an item must win: reading the last ITEM instead made a
        // script that had just made a creature see the earlier object.
        var npc = world.CreateCharacter();
        Assert.Equal(npc.Uid, world.LastNewObject);
        Assert.True(world.LastNewObject.IsValid);

        // The per-type diagnostics behind SERV.LASTNEWITEM / LASTNEWCHAR still track
        // their own kind.
        Assert.Equal(item.Uid, world.LastNewItem);
        Assert.Equal(npc.Uid, world.LastNewChar);
    }

    // ============================================================ 13D-2 / 13E-1 support
    // The duplication entry points share one placement rule.

    [Fact]
    public void ADuplicateDropPositionIsTheSourcesTopLevelWorldPosition()
    {
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(140, 150, 0, 0));

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        owner.Backpack = pack;
        owner.Equip(pack, Layer.Pack);

        var carried = world.CreateItem();
        carried.BaseId = 0x0EED;
        pack.AddItem(carried);
        carried.Position = new Point3D(21, 35, 0, 0);   // container-local

        var drop = carried.GetDupeDropPosition();

        Assert.Equal(owner.Position.X, drop.X);
        Assert.Equal(owner.Position.Y, drop.Y);
    }

    // ============================================================ 13E-2 / 13E-3
    // Sphere numbers: a leading zero is a base marker, everything else is decimal.

    [Theory]
    [InlineData("10", 10L)]        // decimal ten, NOT hex sixteen
    [InlineData("010", 16L)]       // leading zero -> hex
    [InlineData("0A", 10L)]
    [InlineData("0x1F", 31L)]
    [InlineData("-5", -5L)]
    public void ASphereTokenReadsInTheRightBase(string token, long expected)
    {
        Assert.True(ScriptNumber.TryParseToken(token, out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1+1", 2L)]
    [InlineData("010+1", 17L)]
    [InlineData("10-4", 6L)]
    public void ASphereArgumentEvaluatesASimpleSum(string text, long expected)
    {
        Assert.True(ScriptNumber.TryParseArgument(text, out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notanumber")]
    public void AnUnreadableArgumentIsRefusedRatherThanDefaulted(string text)
    {
        Assert.False(ScriptNumber.TryParseArgument(text, out _));
    }

    // ============================================================ 13B-5 support

    [Fact]
    public void ANewCharacterStartsWithEmptyPoolsSoTheFactoryMustFillThem()
    {
        // The contract the NEWNPC fix depends on: applying a definition sets the
        // MAXIMA, and the creation path is what fills the pools.
        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        npc.MaxHits = 100;
        npc.MaxMana = 80;

        Assert.Equal(0, npc.Hits);
        Assert.Equal(0, npc.Mana);

        npc.Hits = npc.MaxHits;
        npc.Mana = npc.MaxMana;
        Assert.Equal(100, npc.Hits);
        Assert.Equal(80, npc.Mana);
    }
}
