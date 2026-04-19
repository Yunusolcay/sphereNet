using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;

namespace SphereNet.Tests;

public class SkillEngineTests
{
    private static Character MakeChar(ushort skillVal = 500)
    {
        var ch = new Character();
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = 50; ch.MaxMana = 50; ch.MaxStam = 50;
        ch.Hits = 50; ch.Mana = 50; ch.Stam = 50;
        ch.SetSkill(SkillType.Blacksmithing, skillVal);
        ch.SetSkill(SkillType.Mining, skillVal);
        return ch;
    }

    [Fact]
    public void CheckSuccess_GM_AlwaysSucceeds()
    {
        var ch = MakeChar(0);
        ch.PrivLevel = PrivLevel.GM;
        bool result = SkillEngine.CheckSuccess(ch, SkillType.Blacksmithing, 99);
        Assert.True(result);
    }

    [Fact]
    public void CheckSuccess_GM_ParryingCanFail()
    {
        var ch = MakeChar(0);
        ch.PrivLevel = PrivLevel.GM;
        // Parrying is exempt from GM auto-success; with 0 skill and high difficulty it can fail
        // Run multiple times to check it's not always true
        int successes = 0;
        for (int i = 0; i < 100; i++)
            if (SkillEngine.CheckSuccess(ch, SkillType.Parrying, 99)) successes++;
        // Should not be 100% success with 0 skill
        Assert.True(successes < 100);
    }

    [Fact]
    public void CheckSuccess_NegativeDifficulty_AlwaysFails()
    {
        var ch = MakeChar(1000);
        bool result = SkillEngine.CheckSuccess(ch, SkillType.Blacksmithing, -1);
        Assert.False(result);
    }

    [Fact]
    public void GetSkillMax_Default_Is1000()
    {
        var ch = MakeChar(500);
        int max = SkillEngine.GetSkillMax(ch, SkillType.Blacksmithing);
        Assert.Equal(1000, max);
    }

    [Fact]
    public void GetSkillMax_LockDown_CapsAtCurrent()
    {
        var ch = MakeChar(500);
        ch.SetSkillLock(SkillType.Blacksmithing, 1); // down
        int max = SkillEngine.GetSkillMax(ch, SkillType.Blacksmithing);
        Assert.Equal(500, max);
    }

    [Fact]
    public void GetSkillSum_CalculatesCorrectly()
    {
        var ch = MakeChar(0);
        ch.SetSkill(SkillType.Swordsmanship, 100);
        ch.SetSkill(SkillType.Magery, 200);
        int sum = SkillEngine.GetSkillSum(ch);
        Assert.Equal(300, sum);
    }

    [Fact]
    public void GetSkillSumMax_DefaultIs7000()
    {
        var ch = MakeChar();
        int max = SkillEngine.GetSkillSumMax(ch);
        Assert.Equal(7000, max);
    }

    [Fact]
    public void UseQuick_WithGain_MayChangeSkill()
    {
        var ch = MakeChar(100);
        int before = ch.GetSkill(SkillType.Blacksmithing);
        // Run many times to trigger potential gain
        for (int i = 0; i < 200; i++)
            SkillEngine.UseQuick(ch, SkillType.Blacksmithing, 50);
        // Skill may or may not have changed — just verify no crash
        Assert.True(ch.GetSkill(SkillType.Blacksmithing) >= before);
    }
}
