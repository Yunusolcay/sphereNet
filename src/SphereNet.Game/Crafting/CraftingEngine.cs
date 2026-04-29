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
}

/// <summary>
/// A craftable item recipe. Loaded from [ITEMDEF] RESOURCES/SKILLMAKE sections.
/// </summary>
public sealed class CraftRecipe
{
    public ushort ResultItemId { get; init; }
    public string ResultName { get; set; } = "";
    public SkillType PrimarySkill { get; init; } = SkillType.Blacksmithing;
    public int Difficulty { get; init; }
    public List<CraftResource> Resources { get; } = [];
    public List<(SkillType Skill, int MinValue)> SkillRequirements { get; } = [];
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
    public bool CanCraft(Character crafter, CraftRecipe recipe)
    {
        // Check skill requirements
        foreach (var (skill, minVal) in recipe.SkillRequirements)
        {
            if (crafter.GetSkill(skill) < minVal)
                return false;
        }

        // Check resource availability
        foreach (var res in recipe.Resources)
        {
            if (CountResource(crafter, res.ItemId) < res.Amount)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Attempt to craft an item. Returns the crafted item on success, null on failure.
    /// Maps to Skill_MakeItem / Skill_MakeItem_Success flow in Source-X.
    /// </summary>
    public Item? TryCraft(Character crafter, CraftRecipe recipe)
    {
        if (!CanCraft(crafter, recipe))
            return null;

        // Skill check
        bool success = SkillEngine.UseQuick(crafter, recipe.PrimarySkill, recipe.Difficulty);

        if (success)
        {
            // Consume resources
            foreach (var res in recipe.Resources)
                ConsumeResource(crafter, res.ItemId, res.Amount);

            // Create the item
            var item = _world.CreateItem();
            item.BaseId = recipe.ResultItemId;
            item.Name = recipe.ResultName;

            // Quality roll based on skill
            int skillVal = crafter.GetSkill(recipe.PrimarySkill);
            int quality = CalcQuality(skillVal, recipe.Difficulty);
            if (quality > 100)
                item.SetTag("QUALITY", quality.ToString());

            // Exceptional check
            if (quality >= 150)
                item.Name = "exceptional " + item.Name;

            // Caller (GameClient.OpenCraftingGump) handles placement + notification
            return item;
        }
        else
        {
            // Partial resource loss on failure (50% chance per resource)
            foreach (var res in recipe.Resources)
            {
                int lostAmount = res.Amount / 2;
                if (lostAmount > 0)
                    ConsumeResource(crafter, res.ItemId, lostAmount);
            }

            return null;
        }
    }

    /// <summary>
    /// Calculate item quality (100 = normal, 150+ = exceptional).
    /// </summary>
    private int CalcQuality(int skillLevel, int difficulty)
    {
        int excess = skillLevel - difficulty;
        int quality = 100 + excess / 10;
        quality += Random.Shared.Next(-10, 11);
        return Math.Max(10, Math.Min(200, quality));
    }

    /// <summary>Count how many of a specific item ID the character has in their backpack.</summary>
    private static int CountResource(Character ch, ushort itemId)
    {
        var pack = ch.Backpack;
        if (pack == null) return 0;
        return CountInContainer(pack, itemId);
    }

    private static int CountInContainer(Item container, ushort itemId)
    {
        int count = 0;
        foreach (var item in container.Contents)
        {
            if (item.BaseId == itemId)
                count += item.Amount;
            count += CountInContainer(item, itemId);
        }
        return count;
    }

    /// <summary>Consume a specific amount of items from the backpack.</summary>
    private static void ConsumeResource(Character ch, ushort itemId, int amount)
    {
        var pack = ch.Backpack;
        if (pack == null) return;
        ConsumeFromContainer(pack, itemId, ref amount);
    }

    private static void ConsumeFromContainer(Item container, ushort itemId, ref int remaining)
    {
        for (int i = container.Contents.Count - 1; i >= 0 && remaining > 0; i--)
        {
            var item = container.Contents[i];

            if (item.BaseId == itemId)
            {
                if (item.Amount <= remaining)
                {
                    remaining -= item.Amount;
                    container.RemoveItem(item);
                    item.Delete();
                }
                else
                {
                    item.Amount -= (ushort)remaining;
                    remaining = 0;
                }
            }
            else
            {
                ConsumeFromContainer(item, itemId, ref remaining);
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

            var recipe = ParseRecipe(def, (ushort)baseId, resources);
            if (recipe != null)
            {
                RegisterRecipe(recipe);
                count++;
            }
        }
        return count;
    }

    private static CraftRecipe? ParseRecipe(ItemDef def, ushort resultId, ResourceHolder resources)
    {
        var skillParts = def.SkillMakeRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (skillParts.Length == 0) return null;

        SkillType primarySkill = SkillType.None;
        int difficulty = 0;
        var skillReqs = new List<(SkillType Skill, int MinValue)>();

        foreach (var part in skillParts)
        {
            if (part.StartsWith("t_", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("i_", StringComparison.OrdinalIgnoreCase))
                continue;

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

        ushort dispId = def.DispIndex != 0 ? def.DispIndex : resultId;

        var recipe = new CraftRecipe
        {
            ResultItemId = dispId,
            ResultName = def.Name ?? "",
            PrimarySkill = primarySkill,
            Difficulty = difficulty / 10
        };

        foreach (var sr in skillReqs)
            recipe.SkillRequirements.Add(sr);

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
                ushort resItemId;
                if (rid.IsValid)
                {
                    var resDef = DefinitionLoader.GetItemDef(rid.Index);
                    resItemId = resDef?.DispIndex ?? (ushort)rid.Index;
                }
                else
                {
                    continue;
                }

                recipe.Resources.Add(new CraftResource { ItemId = resItemId, Amount = amount });
            }
        }

        return recipe;
    }
}
