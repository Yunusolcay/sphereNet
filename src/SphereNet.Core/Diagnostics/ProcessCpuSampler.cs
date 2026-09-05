using System.Diagnostics;

namespace SphereNet.Core.Diagnostics;

/// <summary>
/// CPU usage of the current process, as a percentage of one machine's worth of
/// cores, sampled between calls.
///
/// Sampling has to be owned by the process being measured. The panel used to call
/// Process.GetCurrentProcess() on its own side, which in Host mode measures the
/// Host, not the game server whose object and player counts it was displaying next
/// to it - the dashboard could show an idle Host while the game server was pegged.
///
/// A reading also has to be stable regardless of who asks: the previous code
/// returned 0 whenever two callers arrived within the same half-second, so the HTTP
/// endpoint and the SignalR push reported different numbers for the same moment.
/// This keeps the last computed value and re-reports it until a new interval has
/// actually elapsed.
/// </summary>
public sealed class ProcessCpuSampler
{
    private readonly TimeSpan _minInterval;
    private readonly Process _process;

    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleUtc;
    private double _lastPercent;
    private readonly object _gate = new();

    public ProcessCpuSampler(TimeSpan? minInterval = null)
    {
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(500);
        _process = Process.GetCurrentProcess();
        _lastCpuTime = _process.TotalProcessorTime;
        _lastSampleUtc = DateTime.UtcNow;
    }

    /// <summary>Percentage of the whole machine, so a fully busy box reads 100
    /// regardless of core count — the scale the dashboard has always shown.
    /// Returns the previous reading until <c>minInterval</c> has passed.</summary>
    public double SamplePercent()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - _lastSampleUtc;
            if (elapsed < _minInterval)
                return _lastPercent;

            _process.Refresh();
            var cpuTime = _process.TotalProcessorTime;
            var delta = cpuTime - _lastCpuTime;

            _lastCpuTime = cpuTime;
            _lastSampleUtc = now;
            _lastPercent = Math.Round(
                delta.TotalSeconds / elapsed.TotalSeconds / Environment.ProcessorCount * 100.0, 1);
            return _lastPercent;
        }
    }

    /// <summary>Thread count of this process, read at the same place as the CPU
    /// figure so the two always describe the same process.</summary>
    public int ThreadCount
    {
        get
        {
            try
            {
                _process.Refresh();
                return _process.Threads.Count;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }
    }
}
