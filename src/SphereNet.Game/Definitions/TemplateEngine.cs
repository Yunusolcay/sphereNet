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
        var key = defname.Trim();
        var tpl = DefinitionLoader.GetTemplateDef(key);
        if (tpl == null)
        {
            // Unknown template name — Source-X behaviour treats this as a
            // single direct itemdef pick. Yield as-is and let the caller
            // try to resolve it through the regular ITEMDEF lookup.
            yield return (key, 0);
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

    /// <summary>
    /// Optional diagnostic sink — wired by Program.cs to bridge static
    /// helpers into the central logger without forcing every static
    /// method to plumb an ILogger argument.
    /// </summary>
    public static Action<string>? Diagnostic { get; set; }

    /// <summary>Resolve a single-item defname to its full ItemDef
    /// resource index (used as the key into DefinitionLoader._itemDefs).
    /// For numeric ITEMDEFs (<c>[ITEMDEF 0xF0E]</c>) this equals the
    /// graphic ID; for string-named ITEMDEFs (<c>[ITEMDEF i_potion_refresh]</c>)
    /// it's a 32-bit string hash and DOES NOT equal the wire graphic —
    /// callers must do <c>DefinitionLoader.GetItemDef(rid).DispIndex</c>
    /// to get the actual UO graphic. Returns 0 when the name is not
    /// an ItemDef (e.g. still a Template, or unknown).</summary>
    public static int ResolveItemDefIndex(ResourceHolder resources, string defname)
    {
        if (string.IsNullOrWhiteSpace(defname) || resources == null)
            return 0;
        var rid = resources.ResolveDefName(defname.Trim());
        return rid.IsValid && rid.Type == ResType.ItemDef ? rid.Index : 0;
    }

    /// <summary>Resolve a single-item defname to its wire-side UO
    /// graphic (DispIdFull). Honors Source-X <c>ID=</c> / <c>DISPID=</c>
    /// (becomes <c>ItemDef.DispIndex</c>) and <c>DUPEITEM=</c> alias
    /// chains; falls back to the numeric resource index for plain
    /// <c>[ITEMDEF 0xNNNN]</c> blocks. Returns 0 when the defname is
    /// unknown / not an ItemDef.</summary>
    public static ushort ResolveDispId(ResourceHolder resources, string defname)
    {
        int idx = ResolveItemDefIndex(resources, defname);
        if (idx == 0) return 0;
        var idef = DefinitionLoader.GetItemDef(idx);
        if (idef != null)
        {
            if (idef.DispIndex != 0) return idef.DispIndex;
            if (idef.DupItemId != 0) return idef.DupItemId;
        }
        // Plain numeric [ITEMDEF 0xNNNN] — index IS the graphic, but
        // only when it fits in 16 bits. String-hash indexes (>0xFFFF)
        // would otherwise truncate to garbage graphics (e.g. lava /
        // window-shutter / elven-plate were classic symptoms of this
        // exact bug — i_potion_refresh hashed to 0x40B2C7E1, the
        // (ushort) cast yielded 0xC7E1 which happens to be a random
        // valid UO tile). Refuse to truncate.
        return idx <= 0xFFFF ? (ushort)idx : (ushort)0;
    }

    /// <summary>Backwards-compat alias. Prefer <see cref="ResolveDispId"/>
    /// (returns the wire graphic) or <see cref="ResolveItemDefIndex"/>
    /// (returns the def storage key) at new call-sites.</summary>
    public static ushort ResolveItemId(ResourceHolder resources, string defname)
        => ResolveDispId(resources, defname);

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
