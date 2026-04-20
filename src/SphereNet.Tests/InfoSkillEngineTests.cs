using System;
using System.Collections.Generic;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;

namespace SphereNet.Tests;

/// <summary>
/// Phase 1 doğrulaması: InfoSkillEngine Source-X CClientTarg.cpp davranışını
/// tam olarak yansıtıyor mu. Sink yerine kaydeden mock kullanılıyor; testler
/// mesaj metnini ve kanal seçimini (SysMessage vs. ObjectMessage) doğruluyor.
/// </summary>
public class InfoSkillEngineTests
{
    private sealed class RecordingSink : IInfoSkillSink
    {
        public Character Self { get; }
        public Random Random { get; }
        public List<(string Channel, ObjBase? Target, string Text)> Log { get; } = new();

        public RecordingSink(Character self, int seed = 12345)
        {
            Self = self;
            Random = new Random(seed);
        }

        public void SysMessage(string text) => Log.Add(("SYS", null, text));
        public void ObjectMessage(ObjBase target, string text) => Log.Add(("OBJ", target, text));
    }

    private static Character MakeChar(string name = "Rex", short str = 55, short dex = 45, short intel = 10,
        bool isPlayer = false, NpcBrainType brain = NpcBrainType.Animal, ushort body = 0x00C8)
    {
        var ch = new Character
        {
            Name = name,
            Str = str, Dex = dex, Int = intel,
            IsPlayer = isPlayer,
            NpcBrain = brain,
            BodyId = body,
            Food = 30,
        };
        return ch;
    }

    [Fact]
    public void Anatomy_PrintsOverheadDescriptionOnTarget()
    {
        var self = MakeChar("Hero", isPlayer: true, brain: NpcBrainType.Human, body: 0x0190);
        var target = MakeChar("Rex", str: 55, dex: 35);
        var sink = new RecordingSink(self);

        int result = InfoSkillEngine.Anatomy(sink, target, iSkillLevel: 500);

        Assert.Equal(500, result);
        Assert.Single(sink.Log);
        Assert.Equal("OBJ", sink.Log[0].Channel);
        Assert.Same(target, sink.Log[0].Target);
        Assert.Contains("Rex", sink.Log[0].Text);
        // str=55 -> band (55-1)/10 = 5 -> AnatomyStr6 "very strong".
        Assert.Contains(ServerMessages.Get(Msg.AnatomyStr6), sink.Log[0].Text);
        Assert.Contains(ServerMessages.Get(Msg.AnatomyDex4), sink.Log[0].Text);
    }

    [Fact]
    public void Anatomy_ConjuredFlag_EmitsMagicNote()
    {
        var self = MakeChar("Hero");
        var target = MakeChar("Elemental");
        target.SetStatFlag(StatFlag.Conjured);
        var sink = new RecordingSink(self);

        InfoSkillEngine.Anatomy(sink, target, 300);

        Assert.Equal(2, sink.Log.Count);
        Assert.Equal(ServerMessages.Get(Msg.AnatomyMagic), sink.Log[1].Text);
    }

    [Fact]
    public void ArmsLore_UnusableItem_SendsUnableMessage()
    {
        var self = MakeChar();
        var it = new Item { ItemType = ItemType.Food };
        var sink = new RecordingSink(self);

        int rc = InfoSkillEngine.ArmsLore(sink, it, 500);

        Assert.Equal(-1, rc);
        Assert.Single(sink.Log);
        Assert.Equal(ServerMessages.Get(Msg.ArmsloreUnable), sink.Log[0].Text);
    }

    [Fact]
    public void ArmsLore_Weapon_EmitsDamageAndRepair()
    {
        var self = MakeChar();
        var weapon = new Item { ItemType = ItemType.WeaponMaceSharp };
        weapon.SetTag("DAM", "25");
        weapon.SetTag("HITS", "50");
        weapon.SetTag("HITSMAX", "50");
        var sink = new RecordingSink(self);

        InfoSkillEngine.ArmsLore(sink, weapon, 600);

        Assert.Single(sink.Log);
        Assert.Equal("SYS", sink.Log[0].Channel);
        string msg = sink.Log[0].Text;
        Assert.Contains("25", msg);
    }

    [Fact]
    public void EvalInt_AboveThreshold_EmitsSecondLine()
    {
        var self = MakeChar();
        var target = MakeChar("Wizard", intel: 75);
        target.Mana = 40;
        var sink = new RecordingSink(self);

        InfoSkillEngine.EvalInt(sink, target, 500);

        Assert.Equal(2, sink.Log.Count);
        Assert.All(sink.Log, l => Assert.Equal("SYS", l.Channel));
    }

    [Fact]
    public void EvalInt_BelowThreshold_SinglePrimaryLine()
    {
        var self = MakeChar();
        var target = MakeChar("Brute", intel: 20);
        var sink = new RecordingSink(self);

        InfoSkillEngine.EvalInt(sink, target, 300);

        Assert.Single(sink.Log);
    }

    [Fact]
    public void ItemID_NonItemTarget_FallsBackToSysMessage()
    {
        var self = MakeChar();
        var mob = MakeChar("Troll");
        var sink = new RecordingSink(self);

        InfoSkillEngine.ItemID(sink, mob, 500);

        Assert.Single(sink.Log);
        Assert.Equal("SYS", sink.Log[0].Channel);
        Assert.Contains("Troll", sink.Log[0].Text);
    }

    [Fact]
    public void ItemID_Item_StampsIdentifiedAndReportsPrice()
    {
        var self = MakeChar();
        var it = new Item { Name = "sword", Amount = 1, Price = 12 };
        var sink = new RecordingSink(self);

        InfoSkillEngine.ItemID(sink, it, 500);

        Assert.True(it.IsAttr(ObjAttributes.Identified));
        Assert.Single(sink.Log);
        Assert.Contains("12", sink.Log[0].Text);
    }

    [Fact]
    public void TasteID_Character_SelfVsOther()
    {
        var self = MakeChar("Me");
        var other = MakeChar("Other");
        var sink = new RecordingSink(self);

        InfoSkillEngine.TasteID(sink, self, 500);
        InfoSkillEngine.TasteID(sink, other, 500);

        Assert.Equal(2, sink.Log.Count);
        Assert.Equal(ServerMessages.Get(Msg.TasteidSelf), sink.Log[0].Text);
        Assert.Equal(ServerMessages.Get(Msg.TasteidChar), sink.Log[1].Text);
    }

    [Fact]
    public void Forensics_NonCorpse_Rejected()
    {
        var self = MakeChar();
        var shirt = new Item { ItemType = ItemType.Clothing };
        var sink = new RecordingSink(self);

        int rc = InfoSkillEngine.Forensics(sink, shirt, null, 0, false, false, 500);

        Assert.Equal(-1, rc);
        Assert.Single(sink.Log);
        Assert.Equal(ServerMessages.Get(Msg.ForensicsCorpse), sink.Log[0].Text);
    }

    [Fact]
    public void AnimalLore_Free_PrintsMasterFreeLine()
    {
        var self = MakeChar("Hero", isPlayer: true, brain: NpcBrainType.Human);
        var wild = MakeChar("Deer");
        var world = new Game.World.GameWorld(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var sink = new RecordingSink(self);

        InfoSkillEngine.AnimalLore(sink, wild, world, 500);

        Assert.True(sink.Log.Count >= 2);
        Assert.Contains(sink.Log, l => l.Channel == "OBJ" && l.Target == wild);
    }
}
