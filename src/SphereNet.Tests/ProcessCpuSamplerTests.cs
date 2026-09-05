using SphereNet.Core.Diagnostics;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The panel used to sample CPU and thread count from its own process. Under the
/// Host that is the Host process, so the dashboard showed the Host's load beside
/// the game server's world counts. It also returned 0 whenever two callers arrived
/// inside the same half-second, so the HTTP endpoint and the SignalR push
/// disagreed about the same moment.
/// </summary>
public sealed class ProcessCpuSamplerTests
{
    [Fact]
    public void ASecondReadInsideTheIntervalRepeatsTheReadingInsteadOfReportingZero()
    {
        var sampler = new ProcessCpuSampler(minInterval: TimeSpan.FromSeconds(30));

        double first = sampler.SamplePercent();
        double second = sampler.SamplePercent();

        // Both paths that publish stats must see the same number for the same moment.
        Assert.Equal(first, second);
    }

    [Fact]
    public void AReadingIsAPercentageOfTheWholeMachine()
    {
        var sampler = new ProcessCpuSampler(minInterval: TimeSpan.Zero);

        // Give the process some work so the sample is not trivially zero.
        var spin = System.Diagnostics.Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 60) { }

        double percent = sampler.SamplePercent();

        Assert.True(percent >= 0, $"negative CPU reading: {percent}");
        Assert.True(percent <= 100.0 + 1e-6, $"reading exceeded whole-machine scale: {percent}");
    }

    [Fact]
    public void ThreadCountDescribesTheSamplersOwnProcess()
    {
        var sampler = new ProcessCpuSampler();
        Assert.True(sampler.ThreadCount > 0);
    }
}
