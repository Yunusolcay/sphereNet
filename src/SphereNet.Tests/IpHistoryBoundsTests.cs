using SphereNet.Core.Security;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Both per-IP collections on the accept path used to grow without bound.
///
/// The connect-attempt dictionary had no removal, expiry or cap at all, and
/// ConnectionRateLimiter.Cleanup had no production caller — only a test — so its
/// dictionary was equally unbounded. A long-running shard therefore kept one
/// record per source address it had ever seen.
///
/// Lifetime follows Source-X IPHistoryManager: an entry has a TTL refreshed by
/// activity, and the attempt count does not decay — it is forgotten with the entry.
/// </summary>
public sealed class IpHistoryBoundsTests
{
    // --- IpAttemptHistory ---------------------------------------------------

    [Fact]
    public void AnIdleIpIsForgottenOnceItsTtlLapses()
    {
        long now = 0;
        var history = new IpAttemptHistory(ttlSeconds: 300, clock: () => now);

        history.Register("10.0.0.1");
        Assert.Equal(1, history.Count);

        // Someone else connects well after the first IP's TTL expired.
        now += 301_000;
        history.Register("10.0.0.2");

        Assert.Equal(1, history.Count);
        Assert.Equal(0, history.Get("10.0.0.1").Count);
    }

    [Fact]
    public void ActivityRefreshesTheTtlAndKeepsTheRunningCount()
    {
        long now = 0;
        var history = new IpAttemptHistory(ttlSeconds: 300, clock: () => now);

        for (int i = 0; i < 5; i++)
        {
            history.Register("10.0.0.1");
            now += 100_000;          // inside the TTL every time
        }

        // Source-X: the count rides with the entry and does not decay.
        Assert.Equal(5, history.Get("10.0.0.1").Count);
        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void TheEntryTracksTheGapBetweenTheLastTwoAttempts()
    {
        long now = 1_000;
        var history = new IpAttemptHistory(ttlSeconds: 300, clock: () => now);

        history.Register("10.0.0.1");
        now += 2_500;
        var second = history.Register("10.0.0.1");

        Assert.Equal(1_000, second.PreviousMs);
        Assert.Equal(3_500, second.CurrentMs);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void ADistributedFloodOfLiveAddressesStaysUnderTheCeiling()
    {
        long now = 0;
        var history = new IpAttemptHistory(ttlSeconds: 300, maxEntries: 64, clock: () => now);

        // Every address stays inside its TTL, so expiry alone cannot bound this.
        for (int i = 0; i < 5_000; i++)
        {
            history.Register($"10.1.{i / 256}.{i % 256}");
            now += 10;
        }

        Assert.True(history.Count <= 64, $"history grew to {history.Count} entries");
    }

    [Fact]
    public void AnUnknownIpReadsAsEmptyRatherThanThrowing()
    {
        var history = new IpAttemptHistory();
        var attempt = history.Get("203.0.113.7");

        Assert.Equal(0, attempt.Count);
        Assert.Equal(0, attempt.CurrentMs);
    }

    // --- ConnectionRateLimiter ---------------------------------------------

    [Fact]
    public void TheRateLimiterExpiresIdleEntriesWithoutAnExternalCleanupCall()
    {
        var now = DateTimeOffset.UnixEpoch;
        var limiter = new ConnectionRateLimiter(
            window: TimeSpan.FromSeconds(10), clock: () => now);

        limiter.RegisterAttempt("10.0.0.1");
        Assert.Equal(1, limiter.Count);

        // No caller invokes Cleanup() in production; the write path has to expire.
        now = now.AddMinutes(5);
        limiter.RegisterAttempt("10.0.0.2");

        Assert.Equal(1, limiter.Count);
    }

    [Fact]
    public void TheRateLimiterStaysUnderItsCeilingUnderADistributedFlood()
    {
        var now = DateTimeOffset.UnixEpoch;
        var limiter = new ConnectionRateLimiter(clock: () => now, maxEntries: 64);

        for (int i = 0; i < 5_000; i++)
        {
            limiter.RegisterAttempt($"10.2.{i / 256}.{i % 256}");
            now = now.AddMilliseconds(10);
        }

        Assert.True(limiter.Count <= 64, $"limiter grew to {limiter.Count} entries");
    }

    [Fact]
    public void PruningDoesNotReleaseAnIpThatIsStillBeingThrottled()
    {
        var now = DateTimeOffset.UnixEpoch;
        var limiter = new ConnectionRateLimiter(
            threshold: 3, window: TimeSpan.FromSeconds(10),
            baseDelay: TimeSpan.FromMinutes(10), clock: () => now);

        for (int i = 0; i < 5; i++)
            limiter.RegisterAttempt("10.0.0.9");
        Assert.True(limiter.ShouldThrottle("10.0.0.9"));

        // Time passes and other addresses drive pruning; the active block holds.
        now = now.AddMinutes(1);
        limiter.RegisterAttempt("10.0.0.10");

        Assert.True(limiter.ShouldThrottle("10.0.0.9"));
    }
}
