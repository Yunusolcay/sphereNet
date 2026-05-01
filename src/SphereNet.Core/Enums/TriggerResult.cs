namespace SphereNet.Core.Enums;

/// <summary>
/// Trigger return values. Maps to TRIGRET_TYPE in Source-X.
/// </summary>
public enum TriggerResult : byte
{
    Default = 0,
    True = 1,
    False = 2,
    Qty
}
