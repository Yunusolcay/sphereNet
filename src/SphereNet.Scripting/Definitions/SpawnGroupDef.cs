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
                    newWeight = Math.Max(0, newWeight);
                    var last = Members[^1];
                    TotalWeight += newWeight - last.Weight;
                    Members[^1] = (last.CharDefName, newWeight);
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

        // Which half is the weight is decided by the SECOND part, not the first: the
        // Source-X form is <resource>,<weight>, and a resource may perfectly well be
        // written as a number (ID=0200,9). Testing the FIRST part meant a numeric
        // chardef was mistaken for a weight, and the group then looked for a creature
        // called "9" and spawned nothing at all.
        if (parts.Length >= 2 && int.TryParse(parts[1], out int rw))
        {
            charDefName = parts[0];
            weight = rw;
        }
        else if (parts.Length >= 2 && int.TryParse(parts[0], out int w))
        {
            // The reversed order an older SphereNet pack may have been written against,
            // recognisable because the second half is NOT a number.
            weight = w;
            charDefName = parts[1];
        }
        else
        {
            charDefName = parts[0];
        }
        // A weight of zero is a real setting: it takes the member out of the draw
        // without removing it from the group (GetRandMemberIndex, CRandGroupDef.cpp:229).
        // Raising it to one meant "disabled" and "rare" were the same thing.
        weight = Math.Max(0, weight);

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
        if (Members.Count == 0 || TotalWeight <= 0)
            return null;

        int roll = rng.Next(TotalWeight);
        int cumulative = 0;
        foreach (var (charDefName, weight) in Members)
        {
            if (weight <= 0)
                continue;               // taken out of the draw
            cumulative += weight;
            if (roll < cumulative)
                return charDefName;
        }

        // Fall back to the last member that is actually in the draw.
        for (int i = Members.Count - 1; i >= 0; i--)
            if (Members[i].Weight > 0)
                return Members[i].CharDefName;
        return null;
    }
}
