using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Persistence.Accounts;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Which file is the live account snapshot must be stated, not guessed.
///
/// Changing SaveFormat writes a new file and deletes the old ones, but a delete
/// can fail (a lock, a read-only attribute, an antivirus hold) and it only logs a
/// warning. The loader then picked by a fixed extension priority, so a surviving
/// older file outranked the current one: new accounts vanished and password/ban
/// changes appeared to roll back.
/// </summary>
public sealed class AccountSnapshotSelectionTests
{
    private static AccountManager NewManager() => new(LoggerFactory.Create(b => { }));

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_accsnap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AStaleFileThatSurvivedDeletionDoesNotShadowTheCurrentSnapshot()
    {
        string dir = TempDir();
        try
        {
            // An older generation in the format that used to win the probe.
            var old = NewManager();
            old.CreateAccount("veteran", "pw");
            AccountPersistence.Save(old, dir, SaveFormat.BinaryGz);
            byte[] staleBytes = File.ReadAllBytes(Path.Combine(dir, "sphereaccu.sbin.gz"));

            // The operator switches to text; the save deletes the old file.
            var current = NewManager();
            current.CreateAccount("veteran", "pw");
            current.CreateAccount("newcomer", "pw");
            Assert.Equal(2, AccountPersistence.Save(current, dir, SaveFormat.Text));

            // Simulate the delete having failed: the stale file is back.
            File.WriteAllBytes(Path.Combine(dir, "sphereaccu.sbin.gz"), staleBytes);

            var loaded = NewManager();
            Assert.Equal(2, AccountPersistence.Load(loaded, dir));
            Assert.NotNull(loaded.FindAccount("veteran"));
            Assert.NotNull(loaded.FindAccount("newcomer"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(SaveFormat.Text)]
    [InlineData(SaveFormat.TextGz)]
    [InlineData(SaveFormat.Binary)]
    [InlineData(SaveFormat.BinaryGz)]
    public void EveryFormatWritesAManifestNamingItsOwnFile(SaveFormat fmt)
    {
        string dir = TempDir();
        try
        {
            var accounts = NewManager();
            accounts.CreateAccount("alice", "pw");
            AccountPersistence.Save(accounts, dir, fmt);

            string manifest = Path.Combine(dir, "sphereaccu.manifest");
            Assert.True(File.Exists(manifest));

            string expected = "sphereaccu" + SphereNet.Persistence.Formats.SaveIO.ExtensionFor(fmt);
            Assert.Contains($"FILE={expected}", File.ReadAllText(manifest));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ADirectoryWithoutAManifestStillLoadsByExtensionProbe()
    {
        string dir = TempDir();
        try
        {
            var accounts = NewManager();
            accounts.CreateAccount("legacy", "pw");
            AccountPersistence.Save(accounts, dir, SaveFormat.Text);

            // A save directory written by a build that predates the manifest.
            File.Delete(Path.Combine(dir, "sphereaccu.manifest"));

            var loaded = NewManager();
            Assert.Equal(1, AccountPersistence.Load(loaded, dir));
            Assert.NotNull(loaded.FindAccount("legacy"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AManifestPointingAtAMissingFileFallsBackInsteadOfLoadingNothing()
    {
        string dir = TempDir();
        try
        {
            var accounts = NewManager();
            accounts.CreateAccount("restored", "pw");
            AccountPersistence.Save(accounts, dir, SaveFormat.Text);

            // A restored backup can leave the manifest naming a file that is gone.
            File.WriteAllText(Path.Combine(dir, "sphereaccu.manifest"),
                "FORMAT=BinaryGz\r\nSHARDS=1\r\nFILE=sphereaccu.sbin.gz\r\n");

            var loaded = NewManager();
            Assert.Equal(1, AccountPersistence.Load(loaded, dir));
            Assert.NotNull(loaded.FindAccount("restored"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AManifestNamingSomethingOutsideTheDirectoryIsIgnored()
    {
        string dir = TempDir();
        try
        {
            var accounts = NewManager();
            accounts.CreateAccount("safe", "pw");
            AccountPersistence.Save(accounts, dir, SaveFormat.Text);

            File.WriteAllText(Path.Combine(dir, "sphereaccu.manifest"),
                "FORMAT=Text\r\nSHARDS=1\r\nFILE=../../elsewhere/sphereaccu.scp\r\n");

            var loaded = NewManager();
            Assert.Equal(1, AccountPersistence.Load(loaded, dir));
            Assert.NotNull(loaded.FindAccount("safe"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
