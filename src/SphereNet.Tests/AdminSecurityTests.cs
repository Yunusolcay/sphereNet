using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Security;
using SphereNet.Game.Accounts;
using SphereNet.Game.World;
using SphereNet.Server.Admin;

namespace SphereNet.Tests;

public class AdminSecurityTests
{
    private static TelnetConsole CreateTelnet(string adminPassword)
    {
        var loggerFactory = TestHarness.CreateLoggerFactory();
        var config = new SphereConfig { AdminPassword = adminPassword };
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        var accounts = new AccountManager(loggerFactory);
        return new TelnetConsole(world, accounts, config, () => 0,
            loggerFactory.CreateLogger<TelnetConsole>(), loggerFactory);
    }

    [Fact]
    public void TelnetConsole_Start_RejectsEmptyAdminPassword()
    {
        using var telnet = CreateTelnet("");

        Assert.False(telnet.Start(0));
    }

    [Fact]
    public void TelnetConsole_Start_AllowsConfiguredAdminPassword()
    {
        using var telnet = CreateTelnet("change-me");

        Assert.True(telnet.Start(0));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("change-me", true)]
    public void AdminHostPolicy_CanStartTelnet_RequiresConfiguredPassword(string password, bool expected)
    {
        Assert.Equal(expected, AdminHostPolicy.CanStartTelnet(new SphereConfig { AdminPassword = password }));
    }

    [Fact]
    public void SphereConfig_Validate_WarnsForUnsafePublicShardDefaults()
    {
        var warnings = new SphereConfig
        {
            AccApp = 2,
            DefaultCommandLevel = 1,
            AdminPassword = "",
            // Source-X MD5PASSWORDS=0 means plaintext storage, which is what
            // deserves the warning; the setting is honoured now rather than ignored.
            Md5Passwords = false
        }.Validate();

        Assert.Contains(warnings, w => w.Contains("AccApp=2"));
        Assert.Contains(warnings, w => w.Contains("DefaultCommandLevel=1"));
        Assert.Contains(warnings, w => w.Contains("AdminPassword is empty"));
        Assert.Contains(warnings, w => w.Contains("Md5Passwords=0"));
    }

    [Fact]
    public void Account_CheckPassword_RejectsEmptyStoredHash()
    {
        var account = new Account();

        Assert.False(account.CheckPassword("anything"));
    }

    [Fact]
    public void LoginRateLimiter_ThrottlesAfterThresholdAndClearsOnSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var limiter = new LoginRateLimiter(
            threshold: 2,
            window: TimeSpan.FromMinutes(1),
            baseDelay: TimeSpan.FromSeconds(5),
            maxDelay: TimeSpan.FromSeconds(5),
            clock: () => now);

        Assert.False(limiter.IsLimited("ip:account", out _));
        Assert.Equal(TimeSpan.Zero, limiter.RegisterFailure("ip:account"));
        Assert.Equal(TimeSpan.FromSeconds(5), limiter.RegisterFailure("ip:account"));
        Assert.True(limiter.IsLimited("ip:account", out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(5), retryAfter);

        limiter.RegisterSuccess("ip:account");
        Assert.False(limiter.IsLimited("ip:account", out _));
    }
}
