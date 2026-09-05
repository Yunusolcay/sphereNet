using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Accounts;
using SphereNet.Persistence.Formats;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Every account name the manager admits must survive a save/load round-trip in
/// all four formats. Source-X guards this with CAccount::NameStrip; before that
/// port an account called "EOF" or "WORLDx" was accepted and then silently
/// dropped on the next start, and a name carrying a line break aborted the whole
/// account file write.
/// </summary>
public sealed class AccountNameValidationTests
{
    private static AccountManager NewManager() =>
        new(LoggerFactory.Create(b => { }));

    // --- Admission (Source-X CAccount::NameStrip) ---------------------------

    [Theory]
    [InlineData("EOF")]
    [InlineData("eof")]
    [InlineData("EOFcatcher")]
    [InlineData("ACCOUNT")]
    [InlineData("WORLDplayer")]
    [InlineData("SPHEREadmin")]
    [InlineData("GLOBALS")]
    [InlineData("LIST")]
    [InlineData("BLOCKED")]
    [InlineData("update")]
    public void CreateAccount_RejectsReservedNames(string name)
    {
        var accounts = NewManager();
        Assert.Null(accounts.CreateAccount(name, "pw"));
        Assert.Equal(0, accounts.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    [InlineData("!@#$%")]
    public void CreateAccount_RejectsNamesThatStripToNothing(string name)
    {
        var accounts = NewManager();
        Assert.Null(accounts.CreateAccount(name, "pw"));
        Assert.Equal(0, accounts.Count);
    }

    [Fact]
    public void CreateAccount_StripsControlCharactersInsteadOfStoringThem()
    {
        var accounts = NewManager();

        // The 0x80 login packet reads a fixed 30-byte field without filtering, so
        // a crafted client can put a line break in the account name. Stored raw it
        // would make TextSaveWriter throw on every later account write.
        var created = accounts.CreateAccount("alice\ninvalid", "pw");

        Assert.NotNull(created);
        Assert.Equal("aliceinvalid", created!.Name);
        Assert.DoesNotContain('\n', created.Name);
    }

    [Fact]
    public void CreateAccount_StripsPunctuationSourceXStrips()
    {
        var accounts = NewManager();
        var created = accounts.CreateAccount("with space", "pw");

        Assert.NotNull(created);
        Assert.Equal("withspace", created!.Name);
    }

    [Fact]
    public void CreateAccount_TruncatesToSourceXNameLength()
    {
        var accounts = NewManager();
        var created = accounts.CreateAccount(new string('a', 60), "pw");

        Assert.NotNull(created);
        Assert.Equal(AccountNameValidator.MaxLength, created!.Name.Length);
    }

    [Fact]
    public void CreateAccount_HonoursObsceneList()
    {
        var accounts = NewManager();
        AccountNameValidator.ObsceneChecker = n => n.Equals("badword", StringComparison.OrdinalIgnoreCase);
        try
        {
            Assert.Null(accounts.CreateAccount("badword", "pw"));
            Assert.NotNull(accounts.CreateAccount("goodword", "pw"));
        }
        finally
        {
            AccountNameValidator.ObsceneChecker = null;
        }
    }

    // --- Lookup stays legacy-tolerant --------------------------------------

    [Fact]
    public void FindAccount_PrefersExactMatchSoLegacyNamesStayReachable()
    {
        var accounts = NewManager();

        // A name loaded from a file written before the validator existed. It must
        // keep working even though CreateAccount would reject it today.
        var legacy = new Account { Name = "WORLDveteran" };
        legacy.SetPassword("pw");
        accounts.AddLoaded(legacy);

        Assert.Same(legacy, accounts.FindAccount("WORLDveteran"));
    }

    [Fact]
    public void FindAccount_FallsBackToStrippedName()
    {
        var accounts = NewManager();
        accounts.CreateAccount("with space", "pw");

        // The client may send the decorated form; Source-X Account_Find strips first.
        Assert.NotNull(accounts.FindAccount("with space"));
        Assert.NotNull(accounts.FindAccount("withspace"));
    }

    // --- Round-trip --------------------------------------------------------

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void EveryAdmittedName_SurvivesRoundTrip(SaveFormat fmt)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_accname_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var src = NewManager();
            string[] candidates =
            [
                "normal", "EOF", "WORLDplayer", "alice\ninvalid", "with space",
                "GLOBALS", "LIST foo", "Lister", "user.name", "user-name_1",
            ];

            var admitted = new List<string>();
            foreach (string candidate in candidates)
            {
                var acc = src.CreateAccount(candidate, "pw");
                if (acc != null) admitted.Add(acc.Name);
            }

            Assert.NotEmpty(admitted);

            int saved = AccountPersistence.Save(src, tmp, fmt);
            Assert.Equal(admitted.Count, saved);

            var dst = NewManager();
            int loaded = AccountPersistence.Load(dst, tmp);

            Assert.Equal(admitted.Count, loaded);
            foreach (string name in admitted)
                Assert.NotNull(dst.FindAccount(name));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnwritableLegacyName_IsSkippedWithoutLosingTheOtherAccounts()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_accbad_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var src = NewManager();
            src.CreateAccount("alice", "pw");

            // Simulate a file written before the validator: a name the section
            // writer cannot represent. It must cost only itself, not the file.
            var broken = new Account { Name = "bad\nname" };
            broken.SetPassword("pw");
            src.AddLoaded(broken);

            src.CreateAccount("bob", "pw");

            int saved = AccountPersistence.Save(src, tmp, SaveFormat.Text);
            Assert.Equal(2, saved);

            var dst = NewManager();
            Assert.Equal(2, AccountPersistence.Load(dst, tmp));
            Assert.NotNull(dst.FindAccount("alice"));
            Assert.NotNull(dst.FindAccount("bob"));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // --- Reader skip list stays as loose as it was --------------------------

    [Theory]
    [InlineData("EOF", true)]
    [InlineData("WORLDITEM i_backpack", true)]
    [InlineData("SPHERE", true)]
    [InlineData("GLOBALS", true)]
    [InlineData("LIST foo", true)]
    [InlineData("Lister", false)]
    [InlineData("LISTING", false)]
    [InlineData("alice", false)]
    public void ReservedSectionSet_MatchesTheReadersHistoricSkipList(string section, bool reserved)
    {
        Assert.Equal(reserved, AccountNameValidator.IsReservedSection(section));
    }
}
