using SphereNet.Core.Types;

namespace SphereNet.Scripting.Resources;

/// <summary>
/// Base resource definition. Maps to CResourceDef in Source-X.
/// Every scriptable resource has a ResourceId and optional DEFNAME.
/// </summary>
public class ResourceDef
{
    public ResourceId Id { get; }
    public string? DefName { get; set; }

    public ResourceDef(ResourceId id)
    {
        Id = id;
    }

    public string GetResourceName()
    {
        if (!string.IsNullOrEmpty(DefName))
            return DefName;
        return Id.ToString();
    }

    public override string ToString() => GetResourceName();
}
