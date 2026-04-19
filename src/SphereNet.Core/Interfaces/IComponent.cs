using SphereNet.Core.Enums;

namespace SphereNet.Core.Interfaces;

/// <summary>
/// ECS Component interface. Maps to CComponent in Source-X.
/// </summary>
public interface IComponent
{
    ComponentType Type { get; }
    void Delete();

    /// <summary>
    /// Called during the entity tick. Returns true to continue, false to stop further component ticks.
    /// </summary>
    bool OnTickComponent();

    bool TryGetProperty(string key, out string value);
    bool TrySetProperty(string key, string value);
}
