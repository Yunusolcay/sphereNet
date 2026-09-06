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
            // 0) Inline weighted pool in defname position — Source-X resolves
            //    "{ a w b w }" through the expression engine before the
            //    resource lookup (loot rows like
            //    ITEM={ random_weapon_sword_normal 99 i_sword_demon 1 }).
            //    A "0" member is a deliberate empty slot ("nothing" chance).
            if (current.StartsWith('{'))
            {
                current = PickFromDefValue(current);
                if (string.IsNullOrEmpty(current) || current == "0")
                    return "";
                continue;
            }

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

    /// <summary>Roll one template row's chance and amount (Source-X
    /// CItem::CreateHeader, CItem.cpp:461-488): every arg after the defname is
    /// either <c>R#</c> — a 1-in-# chance to create the row at all — or an
    /// amount expression (<c>3</c> / <c>{600 750}</c> dice). Returns false when
    /// the chance roll fails or the amount resolves to 0; otherwise the rolled
    /// amount (minimum 1).</summary>
    public static bool TryRollTemplateRow(TemplateEntry entry, out int amount)
    {
        amount = 1;
        foreach (string rawArg in entry.RawArgs)
        {
            string arg = rawArg.Trim();
            if (arg.Length == 0) continue;
            if ((arg[0] == 'R' || arg[0] == 'r') && arg.Length > 1 &&
                int.TryParse(arg.AsSpan(1), out int chance))
            {
                if (chance > 1 && _rand.Next(chance) != 0)
                    return false; // g_Rand.GetVal(x) != 0 → don't create
                continue;
            }
            amount = RollAmountExpr(arg);
            if (amount <= 0)
                return false; // Source-X: amount == 0 → no item
        }
        return true;
    }

    /// <summary>Evaluate a template amount expression: plain integer or a
    /// <c>{lo hi}</c> dice range (inclusive, Source-X Exp_GetWVal on a braced
    /// range). Unparseable input yields 0.</summary>
    public static int RollAmountExpr(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return 0;
        string trimmed = expr.Trim();
        if (trimmed[0] == '{')
        {
            int close = trimmed.LastIndexOf('}');
            string inner = trimmed.Substring(1, (close < 0 ? trimmed.Length : close) - 1).Trim();
            var parts = inner.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out int lo) && int.TryParse(parts[^1], out int hi))
            {
                if (hi < lo) (lo, hi) = (hi, lo);
                return _rand.Next(lo, hi + 1);
            }
            return parts.Length == 1 && int.TryParse(parts[0], out int single) ? single : 0;
        }
        return int.TryParse(trimmed, out int plain) ? plain : 0;
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
            {
                if (entry.IsContainer) continue; // loot-template CONTAINER rows — not vendor stock
                yield return (entry.DefName, entry.Amount);
            }
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


    // ---- Building real items out of a [TEMPLATE] recipe -------------------
    //
    // A TEMPLATE is a recipe, not an itemdef. Source-X routes it through
    // CItem::CreateHeader, which accepts ITEMDEF and TEMPLATE alike and hands the
    // latter to CreateTemplate (CItem.cpp:461/554): the first entry becomes the
    // object the caller gets back, and when that entry is a CONTAINER the rows
    // after it are created inside it (CItem.cpp:628/642).

    /// <summary>The itemdef index the template's first resolvable entry names -
    /// the object a caller creating from this template receives. 0 when the recipe
    /// resolves to nothing.</summary>
    public static int ResolveTemplatePrimary(int templateIndex)
    {
        var tdef = DefinitionLoader.GetTemplateDef(templateIndex);
        if (tdef == null) return 0;
        foreach (var entry in tdef.ItemEntries)
        {
            int idx = ResolveTemplateEntryIndex(entry.DefName);
            if (idx > 0) return idx;
        }
        return 0;
    }

    /// <summary>Resolve one template row to an itemdef storage index.</summary>
    public static int ResolveTemplateEntryIndex(string defName)
    {
        var rid = DefinitionLoader.StaticResources?.ResolveDefName(defName);
        if (rid is { IsValid: true, Type: ResType.ItemDef })
            return rid.Value.Index;
        return int.TryParse(defName, System.Globalization.NumberStyles.HexNumber,
            null, out int raw) && raw > 0 ? raw : 0;
    }

    /// <summary>Create the template's remaining rows INSIDE <paramref name="container"/>,
    /// which must be the object built from its first (CONTAINER) entry. A recipe whose
    /// first row is not a container has no contents to place and is left alone.</summary>
    public static void FillTemplateContents(
        SphereNet.Game.World.GameWorld world, SphereNet.Game.Objects.Items.Item container,
        int templateIndex)
    {
        var tdef = DefinitionLoader.GetTemplateDef(templateIndex);
        if (tdef == null || tdef.ItemEntries.Count == 0) return;
        if (!tdef.ItemEntries[0].IsContainer) return;   // no box, nothing to fill

        for (int i = 1; i < tdef.ItemEntries.Count; i++)
        {
            int idx = ResolveTemplateEntryIndex(tdef.ItemEntries[i].DefName);
            if (idx <= 0) continue;

            var child = world.CreateItem();
            if (!ItemDefHelper.ApplyInstanceMetadata(child, idx))
            {
                if (idx > ushort.MaxValue) { world.RemoveItem(child); continue; }
                child.BaseId = (ushort)idx;
            }
            child.FireCreateTrigger();
            container.AddItem(child);
        }
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
