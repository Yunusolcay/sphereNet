namespace SphereNet.Core.Enums;

/// <summary>
/// Thread/task priority levels.
/// </summary>
public enum ThreadPriority : byte
{
    Idle = 0,
    Low,
    Normal,
    High,
    Highest,
    RealTime,
    Disabled
}
