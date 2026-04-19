namespace SphereNet.Core.Interfaces;

/// <summary>
/// Timed object interface. Maps to CTimedObject in Source-X.
/// Objects that participate in the world tick system.
/// </summary>
public interface ITimedObject
{
    long Timeout { get; }
    bool IsSleeping { get; }

    void SetTimeout(long timeoutMs);
    void GoSleep();
    void GoAwake();

    /// <summary>
    /// Called by the world ticker when the timeout expires.
    /// Returns true to keep ticking, false to remove from tick list.
    /// </summary>
    bool OnTick();
}
