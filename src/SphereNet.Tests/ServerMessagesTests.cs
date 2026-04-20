using System.Collections.Generic;
using System.Linq;
using SphereNet.Game.Messages;

namespace SphereNet.Tests;

/// <summary>
/// Faz 0 doğrulaması: tüm 1165 Source-X DEFMSG_* anahtarı yüklenmiş, format
/// converter printf placeholder'larını doğru çeviriyor, MessageMacros &lt;SEX&gt;
/// resolve ediyor, override mekanizması default'ları geçiyor.
/// </summary>
public class ServerMessagesTests
{
    [Fact]
    public void SphereDefaultCount_MatchesGeneratorOutput()
    {
        // Generator (tools/GenerateDefMsg.ps1) re-runs deterministically; this
        // is a guard against silent drift between defmessages.tbl, the generated
        // partial and the typed Msg.* surface.
        Assert.Equal(1165, ServerMessages.SphereDefaultCount);
        Assert.Equal(1165, Msg.Count);
        Assert.Equal(1165, MessageCategoryMap.Count);
    }

    [Fact]
    public void AllSphereDefaults_AreRegistered()
    {
        // Every key emitted by the generator must be lookupable.
        Assert.True(ServerMessages.DefaultCount >= ServerMessages.SphereDefaultCount,
            "RegisterSphereDefaults should populate at least SphereDefaultCount entries.");
        Assert.True(ServerMessages.HasKey(Msg.AnatomyDex1));
        Assert.True(ServerMessages.HasKey(Msg.HealingHealthy));
        Assert.True(ServerMessages.HasKey(Msg.NpcVendorB1));
        Assert.True(ServerMessages.HasKey(Msg.TillerReply1));
        Assert.True(ServerMessages.HasKey(Msg.SpellRecallNotrune));
    }

    [Fact]
    public void Get_ReturnsDefault_WhenNoOverride()
    {
        ServerMessages.ClearOverrides();
        // anatomy_dex_1 -> "very clumsy" upstream
        Assert.Equal("very clumsy", ServerMessages.Get(Msg.AnatomyDex1));
    }

    [Fact]
    public void Get_UnknownKey_ReturnsKey()
    {
        // Source-X surfaces missing DEFMSG keys as their symbolic name in-game
        // so untranslated content is obvious.
        Assert.Equal("definitely_not_a_real_key", ServerMessages.Get("definitely_not_a_real_key"));
    }

    [Fact]
    public void Override_BeatsDefault_AndClearsCleanly()
    {
        ServerMessages.ClearOverrides();
        ServerMessages.SetOverride(Msg.AnatomyDex1, "cok beceriksiz");
        Assert.Equal("cok beceriksiz", ServerMessages.Get(Msg.AnatomyDex1));

        ServerMessages.ClearOverrides();
        Assert.Equal("very clumsy", ServerMessages.Get(Msg.AnatomyDex1));
    }

    [Fact]
    public void LoadOverrides_AppliesEntireDictionary()
    {
        ServerMessages.ClearOverrides();
        var batch = new Dictionary<string, string>
        {
            [Msg.AnatomyDex1] = "x",
            [Msg.AnatomyDex2] = "y",
        };
        ServerMessages.LoadOverrides(batch);
        Assert.Equal("x", ServerMessages.Get(Msg.AnatomyDex1));
        Assert.Equal("y", ServerMessages.Get(Msg.AnatomyDex2));
        ServerMessages.ClearOverrides();
    }

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("hello %s", "hello {0}")]
    [InlineData("%d gold", "{0} gold")]
    [InlineData("%i hits", "{0} hits")]
    [InlineData("%lld coins", "{0} coins")]
    [InlineData("%lu count", "{0} count")]
    [InlineData("%llu big", "{0} big")]
    [InlineData("%c char", "{0} char")]
    [InlineData("hex 0x%x", "hex 0x{0:x}")]
    [InlineData("HEX 0x%X", "HEX 0x{0:X}")]
    [InlineData("long hex 0x%lx", "long hex 0x{0:x}")]
    [InlineData("escape %% literal", "escape % literal")]
    [InlineData("mixed %s and %d at %lld", "mixed {0} and {1} at {2}")]
    [InlineData("braces {to} escape", "braces {{to}} escape")]
    public void ConvertFormatPlaceholders_HandlesAllPrintfSpecs(string input, string expected)
    {
        Assert.Equal(expected, ServerMessages.ConvertFormatPlaceholders(input));
    }

    [Fact]
    public void GetFormatted_ProducesSourceXLikeOutput()
    {
        ServerMessages.ClearOverrides();
        // npc_vendor_b1 -> "That will be %lld gold coin%s. " upstream.
        var actual = ServerMessages.GetFormatted(Msg.NpcVendorB1, 12, "s");
        Assert.Equal("That will be 12 gold coins. ", actual);
    }

    [Fact]
    public void GetFormatted_ReturnsTemplate_OnFormatException()
    {
        // Too few args for the placeholders -> graceful fallback to the raw template.
        ServerMessages.ClearOverrides();
        ServerMessages.SetOverride("test_fmt", "needs %s and %d");
        var result = ServerMessages.GetFormatted("test_fmt", "only-one");
        Assert.Equal("needs %s and %d", result);
        ServerMessages.ClearOverrides();
    }

    [Fact]
    public void MessageCategoryMap_ClassifiesByPrefix()
    {
        Assert.Equal(MessageCategory.Skill,     MessageCategoryMap.GetCategory(Msg.AnatomyDex1));
        Assert.Equal(MessageCategory.NpcVendor, MessageCategoryMap.GetCategory(Msg.NpcVendorB1));
        Assert.Equal(MessageCategory.Spell,     MessageCategoryMap.GetCategory(Msg.SpellRecallNotrune));
        Assert.Equal(MessageCategory.Ship,      MessageCategoryMap.GetCategory(Msg.TillerReply1));
        Assert.Equal(MessageCategory.Misc,      MessageCategoryMap.GetCategory("not_a_real_key"));
        Assert.True(MessageCategoryMap.IsSourceXKey(Msg.AnatomyDex1));
        Assert.False(MessageCategoryMap.IsSourceXKey("totally_made_up"));
    }

    [Fact]
    public void MessageMacros_Resolves_SexTag()
    {
        const string template = "Aye <SEX Sir/Mam>!";
        Assert.Equal("Aye Sir!", MessageMacros.Resolve(template, isFemale: false));
        Assert.Equal("Aye Mam!", MessageMacros.Resolve(template, isFemale: true));
    }

    [Fact]
    public void MessageMacros_Resolves_NameTag()
    {
        var ctx = new MessageMacros.Context(IsFemale: false, Name: "Bob");
        Assert.Equal("Hello Bob.", MessageMacros.Resolve("Hello <NAME>.", ctx));
    }

    [Fact]
    public void MessageMacros_LeavesUnknownTagsAlone()
    {
        // No context supplied -> tags pass through so a downstream handler can finish the job.
        Assert.Equal("Aye <SEX Sir/Mam>!", MessageMacros.Resolve("Aye <SEX Sir/Mam>!", MessageMacros.Context.Empty));
    }

    [Fact]
    public void AllKeys_AreUniqueAndLowercase()
    {
        var keys = ServerMessages.AllKeys.ToArray();
        Assert.Equal(keys.Length, keys.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());

        // Source-X DEFMSG_* anahtar adlari snake_case lowercase olarak kaydedilir;
        // tek istisna 'cmd_*' / 'gm_*' gibi SphereNet ekleridir, onlar da lowercase.
        Assert.All(keys, k => Assert.Equal(k.ToLowerInvariant(), k));
    }
}
