using SphereNet.Core.Enums;

namespace SphereNet.Core.Interfaces;

/// <summary>
/// ECS Entity interface. Maps to CEntity in Source-X.
/// Manages component subscriptions and tick delegation.
/// </summary>
public interface IEntity
{
    void SubscribeComponent(ComponentType type, IComponent component);
    IComponent? GetComponent(ComponentType type);
    bool HasComponent(ComponentType type);
    void RemoveComponent(ComponentType type);
}
