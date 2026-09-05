using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Accounts;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Which comparison a stored password gets must follow the MD5PASSWORDS setting,
/// not the shape of the stored string.
///
/// Source-X CAccount::CheckPassword branches on g_Cfg.m_fMd5Passwords. Guessing
/// from the shape locked accounts out: with hashing off the password is stored
/// verbatim, and one that happens to be 32 hex characters - what a password
/// manager emits - was then compared as if it were an MD5 digest, so it could
/// never be entered again.
/// </summary>
public sealed class PasswordStorageModeTests
{
    private const string HexShaped = "0123456789abcdef0123456789abcdef";

    private static AccountManager NewManager(bool md5) =>
        new(LoggerFactory.Create(b => { })) { Md5Passwords = md5 };

    [Theory]
    [InlineData(HexShaped)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("deadbeefdeadbeefdeadbeefdeadbeef")]
    public void PlaintextMode_AcceptsAPasswordThatLooksLikeAHash(string password)
    {
        var accounts = NewManager(md5: false);
        var account = accounts.CreateAccount("alice", password)!;

        Assert.Equal(password, account.PasswordHash);
        Assert.True(account.CheckPassword(password),
            "a hash-shaped plaintext password must still authenticate");
        Assert.False(account.CheckPassword("something else"));
    }

    [Fact]
    public void PlaintextMode_AcceptsAPasswordThatLooksLikeTheVersionedForm()
    {
        // "SHA256:" is SphereNet's own self-describing form, so it is honoured in
        // either mode - but a plaintext password merely starting with those letters
        // must not be mistaken for one.
        var accounts = NewManager(md5: false);
        var account = accounts.CreateAccount("alice", "SHA256:not-really-a-digest")!;

        Assert.True(account.CheckPassword("SHA256:not-really-a-digest"));
    }

    [Fact]
    public void HashingMode_StillRejectsThePlaintextOfAStoredDigest()
    {
        var accounts = NewManager(md5: true);
        var account = accounts.CreateAccount("alice", "secret")!;

        Assert.True(account.CheckPassword("secret"));
        Assert.False(account.CheckPassword(PasswordHelper.Hash("secret")),
            "presenting the digest itself must not authenticate");
    }

    [Fact]
    public void HashingMode_AcceptsALegacyPlaintextFileEntryOnce_ThenUpgradesIt()
    {
        var accounts = NewManager(md5: true);
        var legacy = new Account { Name = "veteran", UseMd5Passwords = true, PasswordHash = "plainpw" };
        accounts.AddLoaded(legacy);

        Assert.NotNull(accounts.Authenticate("veteran", "plainpw"));
        Assert.Equal(PasswordHelper.Hash("plainpw"), legacy.PasswordHash);
        Assert.True(legacy.CheckPassword("plainpw"));
    }

    [Fact]
    public void HashingMode_DoesNotAcceptAHexShapedPlaintextAsItsOwnPassword()
    {
        // With hashing ON a 32-hex stored value is indistinguishable from a digest,
        // and Source-X treats it as one. Locked in so the migration branch above
        // cannot quietly widen into accepting any stored digest as a password.
        var accounts = NewManager(md5: true);
        var account = new Account { Name = "veteran", UseMd5Passwords = true, PasswordHash = HexShaped };
        accounts.AddLoaded(account);

        Assert.False(account.CheckPassword(HexShaped));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheAccountStillAuthenticatesAfterASaveLoadCycle(bool md5)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_pwmode_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var src = NewManager(md5);
            src.CreateAccount("alice", HexShaped);
            AccountPersistence.Save(src, dir, SaveFormat.Text);

            var dst = NewManager(md5);
            AccountPersistence.Load(dst, dir);

            Assert.NotNull(dst.Authenticate("alice", HexShaped));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ThePanelAdminPasswordKeepsItsAlwaysHashedContract()
    {
        // Not a Source-X account and has no MD5PASSWORDS of its own.
        string stored = PasswordHelper.Hash("panel-secret");
        Assert.True(PasswordHelper.Verify("panel-secret", stored));
        Assert.False(PasswordHelper.Verify("wrong", stored));
    }
}
