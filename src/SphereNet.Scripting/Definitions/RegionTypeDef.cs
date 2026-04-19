using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// REGIONTYPE definition. Maps to CRegionType in Source-X.
/// Binds weighted REGIONRESOURCE entries to a region and optionally filters by item type.
/// </summary>
public sealed class RegionTypeDef : ResourceLink
{
    /// <summary>Optional item type filter from the header (e.g. "t_rock", "t_tree", "t_water").</summary>
    public string? ItemTypeFilter { get; set; }

    /// <summary>Weighted resource list: (ResourceId, Weight).</summary>
    public List<(ResourceId ResId, int Weight)> Resources { get; } = [];

    /// <summary>Total cumulative weight for random selection.</summary>
    public int TotalWeight { get; private set; }

    public RegionTypeDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string arg)
    {
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "DEFNAME":
                base.DefName = arg.Trim();
                break;
            case "RESOURCES":
                ParseResourceEntry(arg);
                break;
        }
    }

    /// <summary>
    /// Parse "weight defname" or just "defname" resource entry.
    /// Format: RESOURCES weight,defname  or  RESOURCES defname
    /// </summary>
    private void ParseResourceEntry(string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int weight = 1;
        string resName;

        if (parts.Length >= 2 && int.TryParse(parts[0], out int w))
        {
            weight = w;
            resName = parts[1];
        }
        else
        {
            resName = parts[0];
        }

        var rid = ResourceId.FromString(resName, Core.Enums.ResType.RegionResource);
        Resources.Add((rid, weight));
        TotalWeight += weight;
    }

    /// <summary>
    /// Select a random resource using weighted cumulative selection.
    /// Returns the ResourceId of the chosen REGIONRESOURCE.
    /// </summary>
    public ResourceId SelectRandomResource(Random rng)
    {
        if (Resources.Count == 0)
            return ResourceId.Invalid;

        int roll = rng.Next(TotalWeight);
        int cumulative = 0;
        foreach (var (resId, weight) in Resources)
        {
            cumulative += weight;
            if (roll < cumulative)
                return resId;
        }

        return Resources[^1].ResId;
    }
}
