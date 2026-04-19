using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Definitions;

/// <summary>
/// Runtime template resolver. Picks a single itemdef from a random-pool
/// template (random_hats, random_shirts_human, colors_red …) or
/// enumerates the full item list from a sequential template
/// (VENDOR_S_ALCHEMIST …). Nested templates are followed once before
/// giving up — prevents infinite loops if a shard scripts a cycle.
/// </summary>
public static class TemplateEngine
{
    private static readonly Random _rand = Random.Shared;
    private const int MaxNestedResolves = 8;

    /// <summary>Pick one concrete ItemDef defname from a template's
    /// random pool. Follows nested template references and Source-X
    /// <c>[DEFNAME ...]</c> text entries that use the <c>{ a w b w }</c>
    /// weighted form — random_shirts_human / random_hats / random_facial_hair
    /// etc. ship in those defname blocks, not in [TEMPLATE] sections.
    /// Returns the empty string when the pool is empty.</summary>
    public static string PickRandomItemDefName(string? defname)
    {
        if (string.IsNullOrWhiteSpace(defname))
            return "";
        string current = defname.Trim();
        for (int hops = 0; hops < MaxNestedResolves; hops++)
        {
            // 1) Explicit [TEMPLATE] block wins.
            var tpl = DefinitionLoader.GetTemplateDef(current);
            if (tpl != null)
            {
                if (tpl.RandomEntries.Count == 0)
                    return current; // sequential template — caller enumerates
                current = PickByWeight(tpl.RandomEntries);
                if (string.IsNullOrEmpty(current)) return "";
                continue;
            }

            // 2) [DEFNAME items_*] text entry — could be a weighted
            //    list "{ a w b w ... }" or a simple "i_foo" alias.
            var resources = DefinitionLoader.StaticResources;
            if (resources != null && resources.TryGetDefValue(current, out string? val) &&
                !string.IsNullOrWhiteSpace(val))
            {
                string picked = PickFromDefValue(val);
                if (!string.IsNullOrEmpty(picked) && !picked.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    current = picked;
                    continue;
                }
            }

            return current; // terminal — resolve as ItemDef or leave alone
        }
        return current;
    }

    /// <summary>Parse the RHS of a <c>[DEFNAME ...]</c> entry. Handles
    /// <c>{ a w b w }</c> weighted lists, single <c>i_foo</c> aliases,
    /// and unweighted space-separated lists. Returns empty when the
    /// value doesn't look like an item selector.</summary>
    private static string PickFromDefValue(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return "";

        if (trimmed[0] == '{')
        {
            int close = trimmed.LastIndexOf('}');
            if (close < 0) close = trimmed.Length;
            string inner = trimmed.Substring(1, close - 1).Trim();
            // Tokens: defname weight defname weight … (or defname defname …)
            var tokens = inner.Split(new[] { ' ', '\t', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) return "";

            // Detect weighted form: (name,number) pairs.
            var names = new List<string>();
            var weights = new List<int>();
            int i = 0;
            while (i < tokens.Length)
            {
                string name = tokens[i];
                int weight = 1;
                if (i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out int w))
                {
                    weight = Math.Max(1, w);
                    i += 2;
                }
                else
                {
                    i += 1;
                }
                names.Add(name);
                weights.Add(weight);
            }

            int total = 0;
            for (int k = 0; k < weights.Count; k++) total += weights[k];
            int roll = _rand.Next(total);
            for (int k = 0; k < names.Count; k++)
            {
                roll -= weights[k];
                if (roll < 0) return names[k];
            }
            return names[^1];
        }

        // Single alias: "random_pants_elven  i_elven_pants" form.
        int firstSpace = trimmed.IndexOfAny(new[] { ' ', '\t' });
        return firstSpace < 0 ? trimmed : trimmed[..firstSpace];
    }

    /// <summary>Enumerate every item the sequential-spawn template
    /// should produce. For VENDOR_S_* / VENDOR_B_* restock lists this
    /// returns (defname, amount) pairs. Random-pool templates fall
    /// back to a single random pick exposed as one entry.</summary>
    public static IEnumerable<(string DefName, int Amount)> EnumerateSequential(string? defname)
    {
        if (string.IsNullOrWhiteSpace(defname))
            yield break;
        var tpl = DefinitionLoader.GetTemplateDef(defname.Trim());
        if (tpl == null)
        {
            yield return (defname.Trim(), 0);
            yield break;
        }
        if (tpl.ItemEntries.Count > 0)
        {
            foreach (var entry in tpl.ItemEntries)
                yield return (entry.DefName, entry.Amount);
            yield break;
        }
        // Random pool only → emit one pick.
        string picked = PickByWeight(tpl.RandomEntries);
        if (!string.IsNullOrEmpty(picked))
            yield return (picked, 0);
    }

    /// <summary>Resolve a single-item defname to a concrete ItemDef
    /// index using the resource holder. Returns 0 when the name is not
    /// an ItemDef (e.g. still a Template, or unknown).</summary>
    public static ushort ResolveItemId(ResourceHolder resources, string defname)
    {
        if (string.IsNullOrWhiteSpace(defname) || resources == null)
            return 0;
        var rid = resources.ResolveDefName(defname.Trim());
        return rid.IsValid && rid.Type == ResType.ItemDef ? (ushort)rid.Index : (ushort)0;
    }

    private static string PickByWeight(List<TemplateEntry> pool)
    {
        if (pool.Count == 0) return "";
        int total = 0;
        foreach (var e in pool) total += Math.Max(1, e.Weight);
        int roll = _rand.Next(total);
        foreach (var e in pool)
        {
            roll -= Math.Max(1, e.Weight);
            if (roll < 0) return e.DefName;
        }
        return pool[^1].DefName;
    }
}
