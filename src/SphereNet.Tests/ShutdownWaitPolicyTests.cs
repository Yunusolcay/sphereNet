using SphereNet.Host;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The Host used to give the game server a flat eight seconds and then kill its
/// whole process tree. A clean shutdown waits for an in-flight background save,
/// runs a final save and waits for that as well, so a large world on slow storage
/// was killed while it was working correctly.
/// </summary>
public sealed class ShutdownWaitPolicyTests
{
    private const int Quiet = 20_000;
    private const int Ceiling = 180_000;

    [Fact]
    public void ASlowButProgressingShutdownIsNotKilledAtTheOldEightSecondMark()
    {
        // 30s in, last log line 1s ago: the save is running, not stuck.
        Assert.Equal(ShutdownWaitPolicy.Decision.KeepWaiting,
            ShutdownWaitPolicy.Evaluate(elapsedMs: 30_000, silentMs: 1_000, Quiet, Ceiling));
    }

    [Fact]
    public void AChattyShutdownKeepsItsTimeRightUpToTheCeiling()
    {
        Assert.Equal(ShutdownWaitPolicy.Decision.KeepWaiting,
            ShutdownWaitPolicy.Evaluate(elapsedMs: Ceiling - 1, silentMs: 500, Quiet, Ceiling));
    }

    [Fact]
    public void SilenceBeyondTheQuietLimitCountsAsHung()
    {
        Assert.Equal(ShutdownWaitPolicy.Decision.Hung,
            ShutdownWaitPolicy.Evaluate(elapsedMs: 25_000, silentMs: Quiet, Quiet, Ceiling));
    }

    [Fact]
    public void TheAbsoluteCeilingStillAppliesToAChattyChild()
    {
        Assert.Equal(ShutdownWaitPolicy.Decision.Hung,
            ShutdownWaitPolicy.Evaluate(elapsedMs: Ceiling, silentMs: 0, Quiet, Ceiling));
    }

    [Fact]
    public void AFreshRequestAlwaysWaitsFirst()
    {
        Assert.Equal(ShutdownWaitPolicy.Decision.KeepWaiting,
            ShutdownWaitPolicy.Evaluate(elapsedMs: 0, silentMs: 0, Quiet, Ceiling));
    }
}
