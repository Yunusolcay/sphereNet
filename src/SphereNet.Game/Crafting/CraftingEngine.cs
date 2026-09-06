using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Crafting;

/// <summary>
/// A resource requirement for crafting (e.g., 10 ingots, 5 cloth).
/// </summary>
public readonly struct CraftResource
{
    public ushort ItemId { get; init; }
    public int Amount { get; init; }
    /// <summary>When set, the resource matches by item TYPE (Source-X RES_TYPEDEF —
    /// e.g. "any t_ingot") rather than a specific item id. Lets a recipe consume any
    /// item of a category instead of a single hardcoded BaseId.</summary>
    public ItemType? Type { get; init; }
}

/// <summary>A primary-resource variant available to the crafting UI.</summary>
public readonly record struct CraftMaterialOption(
    ushort Hue, int Available, ushort DisplayId, string Name);

/// <summary>
/// A craftable item recipe. Loaded from [ITEMDEF] RESOURCES/SKILLMAKE sections.
/// </summary>
public sealed class CraftRecipe
{
    /// <summary>Resource index of the source ITEMDEF. This can differ from the
    /// display id for named/aliased definitions.</summary>
    public int ResultDefId { get; init; }
    public ushort ResultItemId { get; init; }
    public string ResultName { get; set; } = "";
    public SkillType PrimarySkill { get; init; } = SkillType.Blacksmithing;
    public int Difficulty { get; init; }
    public List<CraftResource> Resources { get; } = [];
    public List<(SkillType Skill, int MinValue)> SkillRequirements { get; } = [];
    /// <summary>Tool types from SKILLMAKE t_* entries — the crafter must
    /// carry (or wield) an item of this type; it is not consumed.</summary>
    public List<ItemType> RequiredToolTypes { get; } = [];
    /// <summary>Specific items from SKILLMAKE i_* entries — must be present,
    /// not consumed.</summary>
    public List<ushort> RequiredItemIds { get; } = [];
}

/// <summary>
/// Crafting engine. Maps to CChar::Skill_MakeItem in Source-X CCharSkill.cpp.
/// Handles resource checking, consumption, success/fail, and item creation.
/// </summary>
public sealed class CraftingEngine
{
    private readonly GameWorld _world;
    private readonly Dictionary<ushort, CraftRecipe> _recipes = [];

    public CraftingEngine(GameWorld world)
    {
        _world = world;
    }

    public void RegisterRecipe(CraftRecipe recipe) =>
        _recipes[recipe.ResultItemId] = recipe;

    public CraftRecipe? GetRecipe(ushort itemId) =>
        _recipes.GetValueOrDefault(itemId);

    public IReadOnlyDictionary<ushort, CraftRecipe> AllRecipes => _recipes;

    /// <summary>Get all recipes for a given primary skill.</summary>
    public List<CraftRecipe> GetRecipesBySkill(SkillType skill) =>
        _recipes.Values.Where(r => r.PrimarySkill == skill).ToList();

    /// <summary>
    /// Check if a character has the resources and skills to craft an item.
    /// Maps to SkillResourceTest in Source-X.
    /// </summary>
    /// <summary>Recipe lookup by result display id (SKILLMENU MAKEITEM).</summary>
    public CraftRecipe? TryGetRecipe(ushort resultDispId) =>
        _recipes.GetValueOrDefault(resultDispId);

    public bool CanCraft(Character crafter, CraftRecipe recipe, ushort? primaryResourceHue = null)
    {
        // Check skill requirements
        foreach (var (skill, minVal) in recipe.SkillRequirements)
        {
            if (crafter.GetSkill(skill) < minVal)
                return false;
        }

        // SKILLMAKE tool/required-item presence (not consumed).
        foreach (var toolType in recipe.RequiredToolTypes)
        {
            if (!HasItemOfType(crafter, toolType))
                return false;
        }
        foreach (var reqId in recipe.RequiredItemIds)
        {
            if (CountResource(crafter, reqId) < 1)
                return false;
        }

        // Work-site proximity (reference Skill_Blacksmith / Skill_Cooking):
        // smithing needs a forge within 2 tiles, cooking a heat source
        // within 3 (fire, forge or campfire).
        if (!HasRequiredWorkSite(crafter, recipe.PrimarySkill))
            return false;

        // Check resource availability
        for (int resourceIndex = 0; resourceIndex < recipe.Resources.Count; resourceIndex++)
        {
            var res = recipe.Resources[resourceIndex];
            if (CountResource(crafter, res) < res.Amount)
                return false;
            if (resourceIndex == 0 &&
                !TrySelectResourceHue(crafter, res, res.Amount, primaryResourceHue, out _))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Attempt to craft an item. Returns the crafted item on success, null on failure.
    /// Maps to Skill_MakeItem / Skill_MakeItem_Success flow in Source-X.
    /// </summary>
    public Item? TryCraft(Character crafter, CraftRecipe recipe, ushort? primaryResourceHue = null)
    {
        lock (crafter)
            return TryCraftCore(crafter, recipe, primaryResourceHue);
    }

    private Item? TryCraftCore(Character crafter, CraftRecipe recipe, ushort? primaryResourceHue)
    {
        if (crafter.IsDead) return null;
        if (!CanCraft(crafter, recipe, primaryResourceHue))
            return null;

        // Skill check
        bool success = SkillEngine.UseQuick(crafter, recipe.PrimarySkill, recipe.Difficulty);

        // Tools wear from use (Source-X Skill_MakeItem): damage the crafting tool
        // on each attempt, whether the craft succeeds or fails.
        foreach (var toolType in recipe.RequiredToolTypes)
        {
            var tool = FindItemOfType(crafter, toolType);
            if (tool != null)
            {
                SphereNet.Game.Combat.CombatEngine.DamageItem(tool);
                break;
            }
        }

        if (success)
        {
            // Re-verify resources before consuming (gump callback delay may have changed state)
            foreach (var res in recipe.Resources)
            {
                if (CountResource(crafter, res) < res.Amount)
                    return null;
            }

            // Capture the primary resource's hue BEFORE consuming so the crafted
            // item can inherit the material colour (e.g. coloured ingots produce
            // a coloured weapon/armour, matching UO material behaviour).
            ushort resourceHue = 0;
            if (recipe.Resources.Count > 0 &&
                !TrySelectResourceHue(crafter, recipe.Resources[0], recipe.Resources[0].Amount,
                    primaryResourceHue, out resourceHue))
                return null;

            // Consume resources
            for (int resourceIndex = 0; resourceIndex < recipe.Resources.Count; resourceIndex++)
            {
                var res = recipe.Resources[resourceIndex];
                if (!ConsumeResource(crafter, res, res.Amount,
                        resourceIndex == 0 ? resourceHue : null))
                    return null;
            }

            // Create the item
            var item = _world.CreateItem();
            item.BaseId = recipe.ResultItemId;
            var resultDef = DefinitionLoader.GetItemDef(
                recipe.ResultDefId != 0 ? recipe.ResultDefId : recipe.ResultItemId);
            item.Name = !string.IsNullOrWhiteSpace(recipe.ResultName)
                ? recipe.ResultName
                : DefinitionLoader.ResolveNames(resultDef?.Name ?? "");
            if (resultDef != null)
            {
                ItemDefHelper.ApplyInstanceMetadata(item,
                    recipe.ResultDefId != 0 ? recipe.ResultDefId : recipe.ResultItemId,
                    setDisplayId: false, setName: false);
                item.ItemType = resultDef.Type;
                item.TData1 = resultDef.TData1;
                item.TData2 = resultDef.TData2;
                item.TData3 = resultDef.TData3;
                item.TData4 = resultDef.TData4;
                foreach (var (key, value) in resultDef.TagDefs.GetAll())
                    item.SetTag(key, value);
                if (resultDef.HitsMax > 0 || resultDef.HitsMin > 0)
                {
                    int minHits = Math.Max(1, resultDef.HitsMin > 0 ? resultDef.HitsMin : resultDef.HitsMax);
                    int maxHits = Math.Max(minHits, resultDef.HitsMax > 0 ? resultDef.HitsMax : minHits);
                    int hits = minHits == maxHits ? minHits : Random.Shared.Next(minHits, maxHits + 1);
                    item.HitsMax = hits;
                    item.HitsCur = hits;
                }
            }
            item.Crafter = crafter.Uid;
            if (resourceHue != 0)
                item.Hue = new Core.Types.Color(resourceHue);

            // Quality roll based on skill (Source-X Skill_MakeItem band table).
            int skillVal = crafter.GetSkill(recipe.PrimarySkill);
            int quality = CalcQuality(skillVal);
            item.Quality = (ushort)quality;

            // Source-X CCharSkill.cpp:799: only a grandmaster (skill > 99.9)
            // producing quality > 175 gets the maker's mark on the name. The
            // old invented "exceptional" rename + 20% durability boost had no
            // reference basis (durability comes solely from the def).
            if (skillVal > 999 && quality > 175)
                item.Name = $"{item.Name} crafted by {crafter.Name}";

            // Caller (GameClient.OpenCraftingGump) handles placement + notification
            return item;
        }
        else
        {
            // Partial resource loss on failure (reference Skill_MakeItem
            // SKTRIG_FAIL → ResourceConsumePart): one 0-50%% roll applied
            // uniformly to every required resource.
            int lossPercent = Random.Shared.Next(50);
            for (int resourceIndex = 0; resourceIndex < recipe.Resources.Count; resourceIndex++)
            {
                var res = recipe.Resources[resourceIndex];
                int lostAmount = res.Amount * lossPercent / 100;
                if (lostAmount > 0)
                    ConsumeResource(crafter, res, lostAmount,
                        resourceIndex == 0 ? primaryResourceHue : null);
            }

            return null;
        }
    }

    /// <summary>Crafted item quality on the 1-200 scale (100 = average) —
    /// Source-X Skill_MakeItem (CCharSkill.cpp:724-794): the skill picks a
    /// quality band (skill*2/10), a logarithmic ±0..2 band variance shifts it,
    /// then the final value rolls inside the band.</summary>
    private int CalcQuality(int skillLevel)
    {
        int variance = 2 - (int)Math.Log10(1.0 + Random.Shared.Next(250));
        if (Random.Shared.Next(2) == 0)
            variance = -variance;

        int bandSelector = skillLevel * 2 / 10;
        int band =
            bandSelector < 25 ? 0 :   // shoddy
            bandSelector < 50 ? 1 :   // poor
            bandSelector < 75 ? 2 :   // below average
            bandSelector < 125 ? 3 :  // average
            bandSelector < 150 ? 4 :  // above average
            bandSelector < 175 ? 5 :  // excellent
            6;                        // superior
        band = Math.Clamp(band + variance, 0, 6);

        return band switch
        {
            0 => Random.Shared.Next(25) + 1,
            1 => Random.Shared.Next(25) + 26,
            2 => Random.Shared.Next(25) + 51,
            3 => Random.Shared.Next(50) + 76,
            4 => Random.Shared.Next(25) + 126,
            5 => Random.Shared.Next(25) + 151,
            _ => Random.Shared.Next(25) + 176,
        };
    }

    /// <summary>Count a recipe resource in the character's backpack — by item TYPE
    /// (RES_TYPEDEF) or by specific item id.</summary>
    private static int CountResource(Character ch, CraftResource res)
    {
        var pack = ch.Backpack;
        if (pack == null) return 0;
        return res.Type.HasValue
            ? CountInContainerByType(pack, res.Type.Value)
            : CountInContainer(pack, res.ItemId);
    }

    /// <summary>Count how many of a specific item ID the character has in their backpack.</summary>
    private static int CountResource(Character ch, ushort itemId)
    {
        var pack = ch.Backpack;
        if (pack == null) return 0;
        return CountInContainer(pack, itemId);
    }

    private static int CountInContainerByType(Item container, ItemType type, int depth = 0)
    {
        if (depth > 10) return 0;
        int count = 0;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type)
                count += item.Amount;
            // Source-X gates a recursive resource search on IsSearchable
            // (CContainer::ContentFind, CContainer.cpp:236): a locked chest in
            // the pack is not part of the reachable stock a craft may consume.
            if (item.IsSearchableContainer)
                count += CountInContainerByType(item, type, depth + 1);
        }
        return count;
    }

    /// <summary>Find the first backpack item matching an item ID (used to read
    /// the resource hue before it is consumed).</summary>
    /// <summary>The crafter carries (or wields) an item of the given type.</summary>
    private static bool HasItemOfType(Character ch, ItemType type)
    {
        var oneHand = ch.GetEquippedItem(Layer.OneHanded);
        if (oneHand?.ItemType == type) return true;
        var twoHand = ch.GetEquippedItem(Layer.TwoHanded);
        if (twoHand?.ItemType == type) return true;
        return ch.Backpack != null && HasItemOfTypeIn(ch.Backpack, type, depth: 3);
    }

    private static bool HasItemOfTypeIn(Item container, ItemType type, int depth)
    {
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type) return true;
            if (depth > 0 && item.ContentCount > 0 && item.IsSearchableContainer &&
                HasItemOfTypeIn(item, type, depth - 1))
                return true;
        }
        return false;
    }

    private static Item? FindItemOfType(Character ch, ItemType type)
    {
        var oneHand = ch.GetEquippedItem(Layer.OneHanded);
        if (oneHand?.ItemType == type) return oneHand;
        var twoHand = ch.GetEquippedItem(Layer.TwoHanded);
        if (twoHand?.ItemType == type) return twoHand;
        return ch.Backpack != null ? FindItemOfTypeIn(ch.Backpack, type, 3) : null;
    }

    private static Item? FindItemOfTypeIn(Item container, ItemType type, int depth)
    {
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type) return item;
            if (depth > 0 && item.ContentCount > 0)
            {
                var found = FindItemOfTypeIn(item, type, depth - 1);
                if (found != null) return found;
            }
        }
        return null;
    }

    /// <summary>Work-site proximity (reference Skill_Blacksmith /
    /// Skill_Cooking): smithing needs a forge within 2 tiles, cooking a
    /// heat source within 3. Other craft skills have no site requirement.</summary>
    private bool HasRequiredWorkSite(Character crafter, SkillType skill)
    {
        switch (skill)
        {
            case SkillType.Blacksmithing:
                return HasNearbyType(crafter, 2, ItemType.Forge);
            case SkillType.Cooking:
                return HasNearbyType(crafter, 3, ItemType.Fire, ItemType.Forge, ItemType.Campfire);
            default:
                return true;
        }
    }

    private bool HasNearbyType(Character crafter, int range, params ItemType[] types)
    {
        foreach (var item in _world.GetItemsInRange(crafter.Position, range))
        {
            if (item.IsDeleted) continue;
            foreach (var t in types)
            {
                if (item.ItemType == t)
                    return true;
            }
        }

        // Map statics count as work sites too: a static graphic's type comes
        // from its itemdef (the way the reference resolves static tiles).
        var mapData = _world.MapData;
        if (mapData == null)
            return false;
        for (int dx = -range; dx <= range; dx++)
        {
            for (int dy = -range; dy <= range; dy++)
            {
                short x = (short)(crafter.X + dx);
                short y = (short)(crafter.Y + dy);
                foreach (var s in mapData.GetStatics(crafter.MapIndex, x, y))
                {
                    var sdef = DefinitionLoader.GetItemDef(s.TileId);
                    if (sdef == null) continue;
                    foreach (var t in types)
                    {
                        if (sdef.Type == t)
                            return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>Find the first matching item for a recipe resource (by TYPE or id),
    /// used to read the material hue before consumption.</summary>
    private static Item? FindResourceItem(Character ch, CraftResource res)
    {
        var pack = ch.Backpack;
        if (pack == null) return null;
        return res.Type.HasValue
            ? FindInContainerByType(pack, res.Type.Value, 0)
            : FindInContainer(pack, res.ItemId, 0);
    }

    private static Item? FindResourceItem(Character ch, ushort itemId)
    {
        var pack = ch.Backpack;
        return pack == null ? null : FindInContainer(pack, itemId, 0);
    }

    private static Item? FindInContainerByType(Item container, ItemType type, int depth)
    {
        if (depth > 10) return null;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            if (item.ItemType == type) return item;
            if (!item.IsSearchableContainer) continue;
            var found = FindInContainerByType(item, type, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static Item? FindInContainer(Item container, ushort itemId, int depth)
    {
        if (depth > 10) return null;
        foreach (var item in container.Contents)
        {
            if (item.BaseId == itemId) return item;
            if (!item.IsSearchableContainer) continue;
            var found = FindInContainer(item, itemId, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static int CountInContainer(Item container, ushort itemId, int depth = 0)
    {
        if (depth > 10) return 0;
        int count = 0;
        foreach (var item in container.Contents)
        {
            if (item.BaseId == itemId)
                count += item.Amount;
            if (item.IsSearchableContainer)
                count += CountInContainer(item, itemId, depth + 1);
        }
        return count;
    }

    public IReadOnlyList<CraftMaterialOption> GetPrimaryResourceOptions(
        Character crafter, CraftRecipe recipe)
    {
        if (crafter.Backpack == null || recipe.Resources.Count == 0)
            return [];

        var resource = recipe.Resources[0];
        var totals = new SortedDictionary<ushort, long>();
        CollectResourceHues(crafter.Backpack, resource, totals, 0, []);
        var options = new List<CraftMaterialOption>();
        foreach (var pair in totals)
        {
            if (pair.Value < resource.Amount) continue;
            var sample = FindResourceItemByHue(crafter.Backpack, resource, pair.Key, 0, []);
            string name = sample?.GetName() ?? "";
            if (string.IsNullOrWhiteSpace(name))
                name = pair.Key == 0 ? "Default material" : $"Material 0x{pair.Key:X4}";
            options.Add(new CraftMaterialOption(
                pair.Key, (int)Math.Min(int.MaxValue, pair.Value),
                sample?.BaseId ?? resource.ItemId, name));
        }
        return options;
    }

    private static bool TrySelectResourceHue(Character ch, CraftResource res,
        int requiredAmount, ushort? requestedHue, out ushort hue)
    {
        hue = 0;
        if (ch.Backpack == null) return false;
        var totals = new SortedDictionary<ushort, long>();
        CollectResourceHues(ch.Backpack, res, totals, 0, []);
        if (requestedHue.HasValue)
        {
            if (!totals.TryGetValue(requestedHue.Value, out long requestedTotal) ||
                requestedTotal < requiredAmount)
                return false;
            hue = requestedHue.Value;
            return true;
        }
        foreach (var pair in totals)
        {
            if (pair.Value < requiredAmount) continue;
            hue = pair.Key;
            return true;
        }
        return false;
    }

    private static Item? FindResourceItemByHue(Item container, CraftResource res,
        ushort hue, int depth, HashSet<uint> seen)
    {
        if (depth > 16 || !seen.Add(container.Uid.Value)) return null;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = res.Type.HasValue
                ? item.ItemType == res.Type.Value
                : item.BaseId == res.ItemId;
            if (matches && item.Hue.Value == hue)
                return item;
            if (item.ContentCount > 0 && item.IsSearchableContainer)
            {
                var found = FindResourceItemByHue(item, res, hue, depth + 1, seen);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static void CollectResourceHues(Item container, CraftResource res,
        SortedDictionary<ushort, long> totals, int depth, HashSet<uint> seen)
    {
        if (depth > 16 || !seen.Add(container.Uid.Value)) return;
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = res.Type.HasValue
                ? item.ItemType == res.Type.Value
                : item.BaseId == res.ItemId;
            if (matches)
            {
                totals.TryGetValue(item.Hue.Value, out long amount);
                totals[item.Hue.Value] = Math.Min(int.MaxValue, amount + item.Amount);
            }
            if (item.ContentCount > 0 && item.IsSearchableContainer)
                CollectResourceHues(item, res, totals, depth + 1, seen);
        }
    }

    /// <summary>Consume a recipe resource (by TYPE or id). Returns true if fully consumed.</summary>
    private static bool ConsumeResource(Character ch, CraftResource res, int amount,
        ushort? hue = null)
    {
        var pack = ch.Backpack;
        if (pack == null) return false;
        if (res.Type.HasValue)
            ConsumeFromContainerByType(pack, res.Type.Value, ref amount, hue: hue);
        else
            ConsumeFromContainer(pack, res.ItemId, ref amount, hue: hue);
        return amount == 0;
    }

    /// <summary>Consume a specific amount of items from the backpack. Returns true if fully consumed.</summary>
    private static bool ConsumeResource(Character ch, ushort itemId, int amount)
    {
        var pack = ch.Backpack;
        if (pack == null) return false;
        ConsumeFromContainer(pack, itemId, ref amount);
        return amount == 0;
    }

    private static void ConsumeFromContainerByType(Item container, ItemType type,
        ref int remaining, int depth = 0, ushort? hue = null)
    {
        if (depth > 10) return;
        for (int i = container.Contents.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var item = container.Contents[i];
            if (item.IsDeleted) continue;
            if (item.ItemType == type && (!hue.HasValue || item.Hue.Value == hue.Value))
            {
                if (item.Amount <= remaining)
                {
                    remaining -= item.Amount;
                    item.RemoveFromWorld();
                }
                else
                {
                    item.Amount -= (ushort)remaining;
                    remaining = 0;
                }
            }
            else
            {
                ConsumeFromContainerByType(item, type, ref remaining, depth + 1, hue);
            }
        }
    }

    private static void ConsumeFromContainer(Item container, ushort itemId,
        ref int remaining, int depth = 0, ushort? hue = null)
    {
        if (depth > 10) return;
        for (int i = container.Contents.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var item = container.Contents[i];

            if (item.BaseId == itemId && (!hue.HasValue || item.Hue.Value == hue.Value))
            {
                if (item.Amount <= remaining)
                {
                    remaining -= item.Amount;
                    item.RemoveFromWorld();
                }
                else
                {
                    item.Amount -= (ushort)remaining;
                    remaining = 0;
                }
            }
            else
            {
                ConsumeFromContainer(item, itemId, ref remaining, depth + 1, hue);
            }
        }
    }

    /// <summary>
    /// Scan all loaded ItemDefs for SKILLMAKE entries and register them as
    /// CraftRecipes. Called once after definitions are loaded.
    /// </summary>
    public int LoadRecipesFromDefs(ResourceHolder resources)
    {
        _recipes.Clear();
        int count = 0;
        foreach (var (baseId, def) in DefinitionLoader.AllItemDefs)
        {
            if (string.IsNullOrWhiteSpace(def.SkillMakeRaw))
                continue;

            var recipe = ParseRecipe(def, baseId, resources);
            if (recipe != null)
            {
                RegisterRecipe(recipe);
                count++;
            }
        }
        return count;
    }

    private static CraftRecipe? ParseRecipe(ItemDef def, int resultDefId, ResourceHolder resources)
    {
        var skillParts = def.SkillMakeRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (skillParts.Length == 0) return null;

        SkillType primarySkill = SkillType.None;
        int difficulty = 0;
        var skillReqs = new List<(SkillType Skill, int MinValue)>();
        var pendingToolTypes = new List<ItemType>();
        var pendingItemIds = new List<ushort>();

        foreach (var part in skillParts)
        {
            // t_* = a tool TYPE that must be carried; i_* = a specific item
            // that must be present. Neither is consumed (reference
            // SkillResourceTest semantics).
            if (part.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            {
                string toolName = part.Split(' ', 2)[0].Trim();
                var trid = resources.ResolveDefName(toolName);
                if (trid.IsValid && trid.Type == Core.Enums.ResType.TypeDef)
                    pendingToolTypes.Add((ItemType)trid.Index);
                continue;
            }
            if (part.StartsWith("i_", StringComparison.OrdinalIgnoreCase))
            {
                string itemName = part.Split(' ', 2)[0].Trim();
                var irid = resources.ResolveDefName(itemName);
                if (irid.IsValid)
                {
                    var reqDef = DefinitionLoader.GetItemDef(irid.Index);
                    ushort requiredId = reqDef is { DispIndex: > 0 }
                        ? reqDef.DispIndex
                        : irid.Index <= ushort.MaxValue ? (ushort)irid.Index : (ushort)0;
                    if (requiredId != 0)
                        pendingItemIds.Add(requiredId);
                }
                continue;
            }

            int spaceIdx = part.LastIndexOf(' ');
            if (spaceIdx < 0) continue;

            string skillName = part[..spaceIdx].Trim();
            string valStr = part[(spaceIdx + 1)..].Trim();

            if (!Enum.TryParse<SkillType>(skillName, true, out var skill))
                continue;

            int val = 0;
            if (valStr.Contains('.'))
            {
                if (double.TryParse(valStr, System.Globalization.CultureInfo.InvariantCulture, out double dv))
                    val = (int)(dv * 10);
            }
            else if (int.TryParse(valStr, out int iv))
            {
                val = iv * 10;
            }

            if (primarySkill == SkillType.None)
            {
                primarySkill = skill;
                difficulty = val;
            }
            skillReqs.Add((skill, val));
        }

        if (primarySkill == SkillType.None) return null;

        ushort dispId = def.DispIndex != 0
            ? def.DispIndex
            : resultDefId <= ushort.MaxValue ? (ushort)resultDefId : (ushort)0;
        if (dispId == 0) return null;

        var recipe = new CraftRecipe
        {
            ResultDefId = resultDefId,
            ResultItemId = dispId,
            ResultName = def.Name ?? "",
            PrimarySkill = primarySkill,
            Difficulty = difficulty / 10
        };

        foreach (var sr in skillReqs)
            recipe.SkillRequirements.Add(sr);
        recipe.RequiredToolTypes.AddRange(pendingToolTypes);
        recipe.RequiredItemIds.AddRange(pendingItemIds);

        if (!string.IsNullOrWhiteSpace(def.ResourcesRaw))
        {
            var resParts = def.ResourcesRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var rp in resParts)
            {
                int spIdx = rp.IndexOf(' ');
                if (spIdx < 0) continue;

                string amtStr = rp[..spIdx].Trim();
                string resName = rp[(spIdx + 1)..].Trim();
                if (!int.TryParse(amtStr, out int amount) || amount <= 0) continue;

                var rid = resources.ResolveDefName(resName);
                if (!rid.IsValid) continue;

                // A RESOURCES entry can name an item TYPE (t_ingot) or a specific
                // item (i_ingot_iron). A type entry resolves to a TypeDef and must
                // match by ItemType — storing the type index as a BaseId (the old
                // behaviour) made the recipe uncraftable.
                if (rid.Type == Core.Enums.ResType.TypeDef)
                {
                    recipe.Resources.Add(new CraftResource { Type = (ItemType)rid.Index, Amount = amount });
                }
                else
                {
                    var resDef = DefinitionLoader.GetItemDef(rid.Index);
                    ushort resItemId = resDef is { DispIndex: > 0 }
                        ? resDef.DispIndex
                        : rid.Index <= ushort.MaxValue ? (ushort)rid.Index : (ushort)0;
                    if (resItemId != 0)
                        recipe.Resources.Add(new CraftResource { ItemId = resItemId, Amount = amount });
                }
            }
        }

        return recipe;
    }
}
