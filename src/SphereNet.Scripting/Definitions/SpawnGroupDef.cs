using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// SPAWN group definition. Maps to CRandGroupDef (RES_SPAWN) in Source-X.
/// Defines a weighted random list of NPC chardef references.
/// Each spawn tick selects one member using weighted cumulative random selection.
/// </summary>
public sealed class SpawnGroupDef : ResourceLink
{
    /// <summary>Weighted member list: (CharDefName, Weight).</summary>
    public List<(string CharDefName, int Weight)> Members { get; } = [];

    /// <summary>Total cumulative weight for random selection.</summary>
    public int TotalWeight { get; private set; }

    public SpawnGroupDef(ResourceId id) : base(id) { }

    public void LoadFromKey(string key, string arg)
    {
        var upper = key.ToUpperInvariant();
        switch (upper)
        {
            case "DEFNAME":
                base.DefName = arg.Trim();
                break;
            default:
                // Numeric ID lines are member entries: "ID weight,chardefname"
                // Source-X format: each key line is "index weight,chardefname" or just "chardefname"
                ParseMemberEntry(arg);
                break;
        }
    }

    /// <summary>
    /// Parse a spawn group member entry.
    /// Formats: "weight,chardefname" or "chardefname" (weight defaults to 1).
    /// </summary>
    private void ParseMemberEntry(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return;

        var parts = arg.Split(',', 2, StringSplitOptions.TrimEntries);
        int weight = 1;
        string charDefName;

        if (parts.Length >= 2 && int.TryParse(parts[0], out int w))
        {
            weight = Math.Max(1, w);
            charDefName = parts[1];
        }
        else
        {
            charDefName = parts[0];
        }

        if (string.IsNullOrWhiteSpace(charDefName))
            return;

        Members.Add((charDefName, weight));
        TotalWeight += weight;
    }

    /// <summary>
    /// Select a random member using weighted cumulative selection.
    /// Returns the chardef name string of the chosen member.
    /// </summary>
    public string? SelectRandomMember(Random rng)
    {
        if (Members.Count == 0)
            return null;

        int roll = rng.Next(TotalWeight);
        int cumulative = 0;
        foreach (var (charDefName, weight) in Members)
        {
            cumulative += weight;
            if (roll < cumulative)
                return charDefName;
        }

        return Members[^1].CharDefName;
    }
}
