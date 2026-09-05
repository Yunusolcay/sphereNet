using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Accounts;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// MD5PASSWORDS used to be read from sphere.ini, copied onto every account, and
/// then never consulted: SetPassword hashed unconditionally. The setting therefore
/// did nothing at all.
///
/// Source-X CAccount::SetPassword branches on it - 1 stores an MD5 digest, 0 stores
/// the password verbatim - and CheckPassword compares in the matching form. The
/// default differs deliberately: Source-X defaults to 0, SphereNet to 1, because
/// hashing is what SphereNet has always actually done.
/// </summary>
public sealed class Md5PasswordsSettingTests
{
    private static AccountManager NewManager(bool md5) =>
        new(LoggerFactory.Create(b => { })) { Md5Passwords = md5 };

    [Fact]
    public void TheDefaultKeepsHashingOn()
    {
        Assert.True(new SphereConfig().Md5Passwords);
    }

    [Fact]
    public void WithHashingOn_ThePasswordIsStoredAsAnMd5Digest()
    {
        var accounts = NewManager(md5: true);
        var account = accounts.CreateAccount("alice", "secret")!;

        Assert.NotEqual("secret", account.PasswordHash);
        Assert.Equal(PasswordHelper.Hash("secret"), account.PasswordHash);
        Assert.True(account.CheckPassword("secret"));
        Assert.False(account.CheckPassword("wrong"));
    }

    [Fact]
    public void WithHashingOff_ThePasswordIsStoredVerbatimLikeSourceX()
    {
        var accounts = NewManager(md5: false);
        var account = accounts.CreateAccount("alice", "secret")!;

        Assert.Equal("secret", account.PasswordHash);
        Assert.True(account.CheckPassword("secret"));
        Assert.False(account.CheckPassword("wrong"));
    }

    [Fact]
    public void ChangingAPasswordUsesTheSameStorageForm()
    {
        var plain = NewManager(md5: false);
        var account = plain.CreateAccount("alice", "first")!;
        account.SetPassword("second");
        Assert.Equal("second", account.PasswordHash);

        var hashed = NewManager(md5: true);
        var other = hashed.CreateAccount("bob", "first")!;
        other.SetPassword("second");
        Assert.Equal(PasswordHelper.Hash("second"), other.PasswordHash);
    }

    // --- migration ----------------------------------------------------------

    [Fact]
    public void WithHashingOn_APlaintextLegacyPasswordIsUpgradedOnSuccessfulLogin()
    {
        var accounts = NewManager(md5: true);
        var legacy = new Account { Name = "veteran", UseMd5Passwords = true, PasswordHash = "plaintextpw" };
        accounts.AddLoaded(legacy);

        Assert.NotNull(accounts.Authenticate("veteran", "plaintextpw"));
        Assert.Equal(PasswordHelper.Hash("plaintextpw"), legacy.PasswordHash);
    }

    [Fact]
    public void WithHashingOff_APlaintextPasswordIsLeftAlone()
    {
        var accounts = NewManager(md5: false);
        var account = new Account { Name = "veteran", UseMd5Passwords = false, PasswordHash = "plaintextpw" };
        accounts.AddLoaded(account);

        Assert.NotNull(accounts.Authenticate("veteran", "plaintextpw"));
        Assert.Equal("plaintextpw", account.PasswordHash);
    }

    [Fact]
    public void AWrongPasswordNeverTriggersAnUpgrade()
    {
        var accounts = NewManager(md5: true);
        var legacy = new Account { Name = "veteran", UseMd5Passwords = true, PasswordHash = "plaintextpw" };
        accounts.AddLoaded(legacy);

        Assert.Null(accounts.Authenticate("veteran", "guess"));
        Assert.Equal("plaintextpw", legacy.PasswordHash);
    }

    // --- round-trip ---------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheStoredFormSurvivesSaveAndLoad(bool md5)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_md5_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var src = NewManager(md5);
            src.CreateAccount("alice", "secret");
            AccountPersistence.Save(src, dir, SaveFormat.Text);

            var dst = NewManager(md5);
            AccountPersistence.Load(dst, dir);

            var loaded = dst.FindAccount("alice")!;
            Assert.Equal(md5 ? PasswordHelper.Hash("secret") : "secret", loaded.PasswordHash);
            Assert.True(loaded.CheckPassword("secret"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
