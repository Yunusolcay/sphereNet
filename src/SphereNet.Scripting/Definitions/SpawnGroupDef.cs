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
            case "CATEGORY" or "SUBSECTION" or "DESCRIPTION":
                break;
            case "ID" or "CONTAINER":
                ParseMemberEntry(arg);
                break;
            case "WEIGHT":
                // WEIGHT changes the weight of the member declared just above it
                // (CRandGroupDef.cpp:95). It was ignored entirely, so the whole
                // alternative Source-X syntax produced a flat list.
                if (Members.Count > 0 && int.TryParse(arg.Trim(), out int newWeight))
                {
                    var last = Members[^1];
                    TotalWeight += Math.Max(1, newWeight) - last.Weight;
                    Members[^1] = (last.CharDefName, Math.Max(1, newWeight));
                }
                break;
            default:
                if (int.TryParse(key, out _))
                    ParseMemberEntry(arg);
                break;
        }
    }

    /// <summary>
    /// Parse a spawn group member entry.
    ///
    /// The Source-X form is <c>ID=&lt;chardef&gt;,&lt;weight&gt;</c> - the RESOURCE
    /// first and its weight second (CRandGroupDef.cpp:79). Reading it the other way
    /// round meant the weight on a normal entry was silently dropped and every member
    /// of a 9:1 group ended up equally likely. The reversed "weight,chardef" order is
    /// still accepted so a pack that was written against the old reading keeps working:
    /// the two are told apart by which half parses as a number.
    /// </summary>
    private void ParseMemberEntry(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
            return;

        var parts = arg.Split(',', 2, StringSplitOptions.TrimEntries);
        int weight = 1;
        string charDefName;

        if (parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[0]) &&
            !int.TryParse(parts[0], out _) && int.TryParse(parts[1], out int rw))
        {
            // Source-X order: chardef, weight.
            charDefName = parts[0];
            weight = Math.Max(1, rw);
        }
        else if (parts.Length >= 2 && int.TryParse(parts[0], out int w))
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
