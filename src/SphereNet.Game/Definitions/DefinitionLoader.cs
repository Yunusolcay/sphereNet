using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Definitions;

/// <summary>
/// Loads game definitions from parsed script resources.
/// Maps to CServerConfig resource loading in Source-X.
/// Reads [SPELL], [SKILLDEF], [ITEMDEF], [CHARDEF] sections and populates registries.
/// </summary>
public sealed class DefinitionLoader
{
    private readonly ResourceHolder _resources;
    private readonly SpellRegistry _spells;

    // Definition registries — accessible for runtime lookups
    private static readonly Dictionary<int, CharDef> _charDefs = new();
    private static readonly Dictionary<int, ItemDef> _itemDefs = new();
    private static readonly Dictionary<int, SkillClassDef> _skillClassDefs = new();
    private static readonly Dictionary<int, RegionResourceDef> _regionResourceDefs = new();
    private static readonly Dictionary<int, RegionTypeDef> _regionTypeDefs = new();
    private static readonly Dictionary<int, SkillDef> _skillDefs = new();
    private static readonly Dictionary<int, TemplateDef> _templateDefs = new();

    /// <summary>Look up a TEMPLATE by numeric resource index.</summary>
    public static TemplateDef? GetTemplateDef(int id) => _templateDefs.GetValueOrDefault(id);

    /// <summary>Look up a TEMPLATE by defname. Used by the NPC equip
    /// pipeline when resolving random_* pools.</summary>
    public static TemplateDef? GetTemplateDef(string? defname)
    {
        if (string.IsNullOrWhiteSpace(defname) || _resourcesStatic == null)
            return null;
        var rid = _resourcesStatic.ResolveDefName(defname.Trim());
        return rid.IsValid && rid.Type == ResType.Template
            ? GetTemplateDef(rid.Index)
            : null;
    }

    public int SpellsLoaded { get; private set; }
    public int ItemDefsLoaded { get; private set; }
    public int CharDefsLoaded { get; private set; }
    public int RegionResourceDefsLoaded { get; private set; }
    public int RegionTypeDefsLoaded { get; private set; }
    public int SkillDefsLoaded { get; private set; }

    public DefinitionLoader(ResourceHolder resources, SpellRegistry spells)
    {
        _resources = resources;
        _spells = spells;
    }

    public static CharDef? GetCharDef(int baseId) => _charDefs.GetValueOrDefault(baseId);
    public static ItemDef? GetItemDef(int baseId) => _itemDefs.GetValueOrDefault(baseId);
    public static RegionResourceDef? GetRegionResourceDef(int id) => _regionResourceDefs.GetValueOrDefault(id);
    public static RegionTypeDef? GetRegionTypeDef(int id) => _regionTypeDefs.GetValueOrDefault(id);
    public static SkillDef? GetSkillDef(int skillIndex) => _skillDefs.GetValueOrDefault(skillIndex);
    public static SkillDef? GetSkillDef(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var rid = _resourcesStatic?.ResolveDefName(name.Trim()) ?? ResourceId.Invalid;
        return rid.IsValid && rid.Type == ResType.SkillDef ? GetSkillDef(rid.Index) : null;
    }

    /// <summary>Resolve #NAMES_xxx placeholders in a string using loaded [NAMES] resources.</summary>
    public static string ResolveNames(string input) =>
        _resourcesStatic?.ResolveNamesInString(input) ?? input;

    /// <summary>Resolve a defname text value (e.g. "colors_skin" → "{1002 1058}").</summary>
    public static bool TryGetDefValue(string name, out string value)
    {
        value = "";
        return _resourcesStatic?.TryGetDefValue(name, out value) ?? false;
    }
    public static SkillClassDef? GetSkillClassDef(int id) => _skillClassDefs.GetValueOrDefault(id);
    public static SkillClassDef? GetSkillClassDef(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var rid = _resourcesStatic?.ResolveDefName(name.Trim()) ?? ResourceId.Invalid;
        return rid.IsValid && rid.Type == ResType.SkillClass ? GetSkillClassDef(rid.Index) : null;
    }

    private static ResourceHolder? _resourcesStatic;

    /// <summary>Static accessor used by systems that only have access
    /// to the DefinitionLoader surface (e.g. Character template
    /// resolution without a GameClient reference).</summary>
    public static ResourceHolder? StaticResources => _resourcesStatic;

    /// <summary>Load all definitions from parsed resources.</summary>
    public void LoadAll()
    {
        _resourcesStatic = _resources;
        foreach (var link in _resources.GetAllResources())
        {
            switch (link.Id.Type)
            {
                case ResType.SpellDef:
                    LoadSpellDef(link);
                    break;
                case ResType.ItemDef:
                    LoadItemDef(link);
                    break;
                case ResType.CharDef:
                    LoadCharDef(link);
                    break;
                case ResType.SkillClass:
                    LoadSkillClassDef(link);
                    break;
                case ResType.RegionResource:
                    LoadRegionResourceDef(link);
                    break;
                case ResType.RegionType:
                    LoadRegionTypeDef(link);
                    break;
                case ResType.SkillDef:
                    LoadSkillDef(link);
                    break;
                case ResType.Template:
                    LoadTemplateDef(link);
                    break;
            }
        }
    }

    /// <summary>Parse a <c>[TEMPLATE name]</c> block. Bodies use:
    /// <c>ID=itemdef</c> / <c>ITEMID=itemdef[,weight]</c> → weighted
    /// random pool; <c>ITEM=itemdef[,amount]</c> → sequential spawn list
    /// (used by VENDOR_S_* / VENDOR_B_* restock templates).</summary>
    private void LoadTemplateDef(ResourceLink link)
    {
        var def = new TemplateDef(link.Id);
        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0)
        {
            _templateDefs[link.Id.Index] = def;
            return;
        }

        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                continue;
            string upper = key.Key.ToUpperInvariant();
            if (upper == "ID" || upper == "ITEMID")
            {
                // ID=foo            → weight 1
                // ITEMID=foo,5      → weight 5
                var parts = key.Arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                int weight = 1;
                if (parts.Length >= 2 && int.TryParse(parts[1], out int w) && w > 0)
                    weight = w;
                def.RandomEntries.Add(new TemplateEntry { DefName = parts[0], Weight = weight });
            }
            else if (upper == "ITEM" || upper == "ITEMNEWBIE")
            {
                // ITEM=foo[,amount]
                var parts = key.Arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                int amount = 0;
                if (parts.Length >= 2 && int.TryParse(parts[1], out int a) && a > 0)
                    amount = a;
                def.ItemEntries.Add(new TemplateEntry { DefName = parts[0], Amount = amount });
            }
            else if (upper == "DEFNAME")
            {
                // Some templates rename themselves — register alias.
                _resources.RegisterDefName(key.Arg.Trim(), link.Id);
            }
        }

        _templateDefs[link.Id.Index] = def;
    }

    private void LoadSkillClassDef(ResourceLink link)
    {
        var def = new SkillClassDef(link.Id);

        var keys = link.StoredKeys;
        if (keys != null && keys.Count > 0)
        {
            foreach (var key in keys)
            {
                if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                    continue;
                def.LoadFromKey(key.Key, key.Arg);
            }
        }

        if (!string.IsNullOrWhiteSpace(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _skillClassDefs[link.Id.Index] = def;
    }

    private void LoadCharDef(ResourceLink link)
    {
        var def = new CharDef(link.Id);

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) { CharDefsLoaded++; return; }

        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                continue;
            def.LoadFromKey(key.Key, key.Arg);
        }

        if (!string.IsNullOrEmpty(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _charDefs[link.Id.Index] = def;
        CharDefsLoaded++;
    }

    private void LoadItemDef(ResourceLink link)
    {
        var def = new ItemDef(link.Id);

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) { ItemDefsLoaded++; return; }

        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                continue;

            // ID / DISPID accept both a numeric graphic ("0x0F6C") AND a
            // defname reference to another ITEMDEF ("i_moongate_blue") —
            // standard Sphere/Source-X form for "copy the graphic from
            // another template". ItemDef.LoadFromKey on its own only
            // parses numerics, so resolve the defname case here before
            // delegating and leave DispIndex at 0 only for truly unknown
            // values.
            if (key.Key.Equals("ID", StringComparison.OrdinalIgnoreCase) ||
                key.Key.Equals("DISPID", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseHex(key.Arg, out ushort idHex))
                {
                    def.DispIndex = idHex;
                }
                else
                {
                    var rid = _resources.ResolveDefName(key.Arg.Trim());
                    if (rid.IsValid && rid.Type == ResType.ItemDef)
                    {
                        // Two paths: referenced itemdef already loaded →
                        // inherit its DispIndex; or it's a forward/pending
                        // reference → fall back to its index (which for
                        // pure-graphic itemdefs [ITEMDEF 0f6c] equals the
                        // graphic hex). If the referenced def loaded later
                        // with a different DispIndex, GetItemDispId()
                        // resolves it at access time.
                        if (_itemDefs.TryGetValue(rid.Index, out var refDef) && refDef.DispIndex != 0)
                            def.DispIndex = refDef.DispIndex;
                        else
                            def.DispIndex = (ushort)rid.Index;
                    }
                }
                continue;
            }

            def.LoadFromKey(key.Key, key.Arg);
        }

        if (!string.IsNullOrEmpty(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _itemDefs[link.Id.Index] = def;
        ItemDefsLoaded++;
    }

    private void LoadSpellDef(ResourceLink link)
    {
        var def = new Magic.SpellDef { Id = (SpellType)(ushort)link.Id.Index };

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) return;

        foreach (var key in keys)
        {
            switch (key.Key.ToUpperInvariant())
            {
                case "NAME": def.Name = key.Arg; break;
                case "FLAGS":
                    def.Flags = ParseSpellFlags(key.Arg);
                    break;
                case "MANAUSE":
                    if (ushort.TryParse(key.Arg, out ushort m)) def.ManaCost = m;
                    break;
                case "TITHINGUSE":
                    if (ushort.TryParse(key.Arg, out ushort ti)) def.TithingCost = ti;
                    break;
                case "SOUND":
                    if (int.TryParse(key.Arg, out int s))
                        def.Sound = s;
                    else
                    {
                        var srid = _resourcesStatic?.ResolveDefName(key.Arg.Trim()) ?? ResourceId.Invalid;
                        if (srid.IsValid) def.Sound = srid.Index;
                    }
                    break;
                case "RUNES": def.Runes = key.Arg; break;
                case "PROMPT_MSG": def.TargetPrompt = key.Arg; break;
                case "EFFECT_ID":
                    if (TryParseHex(key.Arg, out ushort eid)) def.EffectId = eid;
                    else { var r = _resourcesStatic?.ResolveDefName(key.Arg.Trim()) ?? ResourceId.Invalid; if (r.IsValid) def.EffectId = (ushort)r.Index; }
                    break;
                case "RUNE_ITEM":
                    if (TryParseHex(key.Arg, out ushort ri)) def.RuneItemId = ri;
                    else { var r = _resourcesStatic?.ResolveDefName(key.Arg.Trim()) ?? ResourceId.Invalid; if (r.IsValid) def.RuneItemId = (ushort)r.Index; }
                    break;
                case "SCROLL_ITEM":
                    if (TryParseHex(key.Arg, out ushort si)) def.ScrollItemId = si;
                    else { var r = _resourcesStatic?.ResolveDefName(key.Arg.Trim()) ?? ResourceId.Invalid; if (r.IsValid) def.ScrollItemId = (ushort)r.Index; }
                    break;
                case "CAST_TIME":
                    // Support decimal (0.4 = 4 tenths) and integer
                    if (double.TryParse(key.Arg, System.Globalization.CultureInfo.InvariantCulture, out double ctd))
                        def.CastTimeBase = (int)Math.Round(ctd * 10);
                    else if (int.TryParse(key.Arg, out int ct))
                        def.CastTimeBase = ct;
                    break;
                case "EFFECT":
                    ParseCurve(key.Arg, out int eb, out int es);
                    def.EffectBase = eb; def.EffectScale = es;
                    break;
                case "DURATION":
                    // Sphere script writes DURATION in seconds; internal
                    // storage is tenths of a second to match CAST_TIME
                    // (which is also scaled x10). Multiply after
                    // ParseCurve so "3*60" becomes 1800 tenths = 180s.
                    ParseCurve(key.Arg, out int db, out int ds);
                    def.DurationBase = db * 10; def.DurationScale = ds * 10;
                    break;
                case "INTERRUPT":
                    if (int.TryParse(key.Arg, out int ic)) def.InterruptChance = ic;
                    break;
                case "LAYER":
                    if (byte.TryParse(key.Arg, out byte ly)) def.Layer = (Layer)ly;
                    break;
                case "GROUP":
                    if (ulong.TryParse(key.Arg, out ulong gr)) def.Group = gr;
                    break;
                case "RESOURCES":
                    ParseReagentList(key.Arg, def.Reagents);
                    break;
                case "SKILLREQ":
                    ParseSkillReqList(key.Arg, def.SkillReq);
                    break;
            }
        }

        _spells.Register(def);
        SpellsLoaded++;
    }

    private void LoadRegionResourceDef(ResourceLink link)
    {
        var def = new RegionResourceDef(link.Id);

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) { RegionResourceDefsLoaded++; return; }

        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                continue;
            def.LoadFromKey(key.Key, key.Arg);
        }

        if (!string.IsNullOrWhiteSpace(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _regionResourceDefs[link.Id.Index] = def;
        RegionResourceDefsLoaded++;
    }

    private void LoadRegionTypeDef(ResourceLink link)
    {
        var def = new RegionTypeDef(link.Id);

        // Parse item type filter from header if present (e.g. [REGIONTYPE defname t_rock])
        if (!string.IsNullOrEmpty(link.DefName))
        {
            var headerParts = link.DefName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (headerParts.Length >= 2)
            {
                def.ItemTypeFilter = headerParts[1].Trim();
                // The actual defname is the first part
                // DefName will be set from key if present
            }
        }

        var keys = link.StoredKeys;
        if (keys == null || keys.Count == 0) { RegionTypeDefsLoaded++; return; }

        foreach (var key in keys)
        {
            if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                continue;
            def.LoadFromKey(key.Key, key.Arg);
        }

        if (!string.IsNullOrWhiteSpace(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _regionTypeDefs[link.Id.Index] = def;
        RegionTypeDefsLoaded++;
    }

    private void LoadSkillDef(ResourceLink link)
    {
        var def = new SkillDef(link.Id);

        var keys = link.StoredKeys;
        if (keys != null && keys.Count > 0)
        {
            foreach (var key in keys)
            {
                if (key.Key.Equals("ON", StringComparison.OrdinalIgnoreCase))
                    continue;
                def.LoadFromKey(key.Key, key.Arg);
            }
        }

        if (!string.IsNullOrWhiteSpace(def.DefName))
            _resources.RegisterDefName(def.DefName, link.Id);

        _skillDefs[link.Id.Index] = def;
        SkillDefsLoaded++;
    }

    private static void ParseCurve(string val, out int baseVal, out int scale)
    {
        baseVal = 0; scale = 0;
        var parts = val.Split(',');
        if (parts.Length >= 1) baseVal = EvalCurveTerm(parts[0]);
        if (parts.Length >= 2) scale = EvalCurveTerm(parts[1]);
    }

    /// <summary>Evaluate a single Sphere-script curve term like "180",
    /// "3*60.0", "1.5*60", "0.5". Supports integer/decimal literals and
    /// chained <c>*</c>/<c>/</c> operators. The return is truncated to
    /// int — the DURATION / EFFECT curves only need integer precision.
    /// Without this, <c>int.TryParse</c> rejected anything with a
    /// <c>*</c> or <c>.</c>, leaving DurationBase/Scale at 0 and every
    /// timed spell effect expired the same tick it was applied.</summary>
    private static int EvalCurveTerm(string expr)
    {
        expr = expr.Trim();
        if (expr.Length == 0) return 0;
        double acc = 1.0;
        bool first = true;
        char op = '*';
        int i = 0;
        while (i < expr.Length)
        {
            while (i < expr.Length && char.IsWhiteSpace(expr[i])) i++;
            int start = i;
            while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.' || expr[i] == '-' || expr[i] == '+'))
                i++;
            if (start == i) break;
            if (!double.TryParse(expr.AsSpan(start, i - start),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double n))
                return 0;
            if (first) { acc = n; first = false; }
            else if (op == '*') acc *= n;
            else if (op == '/') acc = n == 0 ? 0 : acc / n;
            while (i < expr.Length && char.IsWhiteSpace(expr[i])) i++;
            if (i >= expr.Length) break;
            char c = expr[i];
            if (c == '*' || c == '/') { op = c; i++; }
            else break;
        }
        if (double.IsNaN(acc) || double.IsInfinity(acc)) return 0;
        return (int)acc;
    }

    private static bool TryParseHex(string val, out ushort result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        val = val.Trim();
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        if (val.StartsWith('0') && val.Length > 1)
            return ushort.TryParse(val.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out result);
        return ushort.TryParse(val, out result);
    }

    /// <summary>Parse "amount resDefName, amount resDefName, ..." into reagent dictionary.</summary>
    private void ParseReagentList(string val, Dictionary<ushort, int> dict)
    {
        var parts = val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            // Format: "amount defname" or "defname amount" or just "defname"
            var tokens = part.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            int amount = 1;
            string name;
            if (int.TryParse(tokens[0], out int a))
            {
                amount = a;
                name = tokens.Length > 1 ? tokens[1] : "";
            }
            else
            {
                name = tokens[0];
                if (tokens.Length > 1) int.TryParse(tokens[1], out amount);
            }

            if (string.IsNullOrEmpty(name)) continue;

            // Resolve defname to item ID
            var rid = _resourcesStatic?.ResolveDefName(name) ?? ResourceId.Invalid;
            ushort itemId = rid.IsValid ? (ushort)rid.Index : (ushort)0;
            if (itemId == 0 && TryParseHex(name, out ushort hex))
                itemId = hex;
            if (itemId != 0)
                dict[itemId] = amount;
        }
    }

    /// <summary>Parse "skillName minValue, ..." into skill requirement dictionary.</summary>
    private void ParseSkillReqList(string val, Dictionary<SkillType, int> dict)
    {
        var parts = val.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var tokens = part.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;

            // Format: "SKILL value" (e.g. "MAGERY 500")
            string skillName = tokens[0];
            if (!int.TryParse(tokens[1], out int minVal)) continue;

            // Try resolve as defname first, then as enum
            var rid = _resourcesStatic?.ResolveDefName(skillName) ?? ResourceId.Invalid;
            if (rid.IsValid && rid.Type == ResType.SkillDef)
            {
                dict[(SkillType)rid.Index] = minVal;
            }
            else if (Enum.TryParse<SkillType>(skillName, true, out var st))
            {
                dict[st] = minVal;
            }
        }
    }

    /// <summary>
    /// Parse spell flags from script: "spellflag_targ_char|spellflag_good|0x100" etc.
    /// Script-first: resolves defnames from [DEFNAME spell_flags] sections first,
    /// then falls back to numeric/hex parsing.
    /// </summary>
    private SpellFlag ParseSpellFlags(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return SpellFlag.None;

        // Try plain numeric first (single value, no pipes)
        val = val.Trim();
        if (!val.Contains('|'))
        {
            if (TryParseHexUlong(val, out ulong single))
                return (SpellFlag)single;
        }

        // Pipe-separated tokens: resolve each via defname or numeric
        SpellFlag result = SpellFlag.None;
        foreach (var token in val.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // 1) Try defname resolve first (script-first approach)
            var rid = _resourcesStatic?.ResolveDefName(token) ?? ResourceId.Invalid;
            if (rid.IsValid)
            {
                result |= (SpellFlag)(ulong)rid.Index;
                continue;
            }

            // 2) Try numeric / hex
            if (TryParseHexUlong(token, out ulong n))
            {
                result |= (SpellFlag)n;
            }
        }
        return result;
    }

    private static bool TryParseHexUlong(string val, out ulong result)
    {
        result = 0;
        if (string.IsNullOrEmpty(val)) return false;
        val = val.Trim();
        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || val.StartsWith("0X"))
            return ulong.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result);
        // Sphere scripts use bare hex (e.g. "00000004") — try hex if all chars are hex digits
        if (val.Length >= 2 && val[0] == '0')
            return ulong.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out result);
        return ulong.TryParse(val, out result);
    }
}
