namespace SphereNet.Host;

/// <summary>
/// Decides how long the Host waits for the game server to shut down on its own.
///
/// A flat eight-second <c>WaitForExit</c> used to end in <c>Kill</c>. The server's
/// clean shutdown drains any in-flight background save, runs a final save and waits
/// for that too, so a large world on slow storage legitimately exceeds any short
/// timeout — and the kill landed on a healthy process mid-write, losing everything
/// since the last periodic save.
///
/// The rule instead: a child that is still reporting progress gets as long as it
/// needs, up to an absolute ceiling. Only silence, or that ceiling, is treated as
/// hung. Separated from <see cref="ServerProcess"/> so the policy can be tested
/// without starting a real process.
/// </summary>
internal static class ShutdownWaitPolicy
{
    internal enum Decision
    {
        /// <summary>Still working — keep waiting.</summary>
        KeepWaiting,

        /// <summary>Unresponsive or out of time — the caller must escalate.</summary>
        Hung,
    }

    /// <param name="elapsedMs">Time since the shutdown request.</param>
    /// <param name="silentMs">Time since the child last wrote a log line.</param>
    /// <param name="quietLimitMs">Silence that counts as unresponsive.</param>
    /// <param name="timeoutMs">Absolute ceiling, however chatty the child is.</param>
    internal static Decision Evaluate(long elapsedMs, long silentMs, int quietLimitMs, int timeoutMs)
    {
        if (elapsedMs >= timeoutMs) return Decision.Hung;
        if (silentMs >= quietLimitMs) return Decision.Hung;
        return Decision.KeepWaiting;
    }
}
