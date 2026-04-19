using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.Skills;

/// <summary>
/// Region-based resource gathering engine.
/// Routes Mining/Fishing/Lumberjacking through REGIONTYPE → REGIONRESOURCE definitions.
/// Falls back to hardcoded items when no region resources are defined.
/// </summary>
public sealed class GatheringEngine
{
    private readonly GameWorld _world;
    private readonly TriggerDispatcher? _triggerDispatcher;
    private readonly Random _rng = new();

    /// <summary>Skill → ItemTypeFilter mapping for REGIONTYPE filtering.</summary>
    private static readonly Dictionary<SkillType, string> _skillTypeFilters = new()
    {
        [SkillType.Mining] = "t_rock",
        [SkillType.Lumberjacking] = "t_tree",
        [SkillType.Fishing] = "t_water",
    };

    public GatheringEngine(GameWorld world, TriggerDispatcher? triggerDispatcher = null)
    {
        _world = world;
        _triggerDispatcher = triggerDispatcher;
    }

    /// <summary>
    /// Try to gather a resource from the region at the target location.
    /// Returns true if a region resource was found and processed (success or fail).
    /// Returns false if no matching region resource exists (caller should use hardcoded fallback).
    /// </summary>
    public bool TryGather(Character ch, SkillType skill, Point3D target, out bool success, out ushort itemId, out int amount)
    {
        success = false;
        itemId = 0;
        amount = 0;

        if (!_skillTypeFilters.TryGetValue(skill, out var typeFilter))
            return false;

        // 1. Find the region at target
        var region = _world.FindRegion(target);
        if (region == null || region.RegionTypes.Count == 0)
            return false;

        // 2-3. Find matching REGIONTYPE for this skill's type filter
        RegionTypeDef? matchedType = null;
        foreach (var rtRid in region.RegionTypes)
        {
            var rtDef = DefinitionLoader.GetRegionTypeDef(rtRid.Index);
            if (rtDef == null) continue;

            // Filter by item type (t_rock for Mining, etc.)
            if (rtDef.ItemTypeFilter != null &&
                rtDef.ItemTypeFilter.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
            {
                matchedType = rtDef;
                break;
            }

            // If no filter set, accept it as a match for any skill
            if (rtDef.ItemTypeFilter == null && matchedType == null)
                matchedType = rtDef;
        }

        if (matchedType == null || matchedType.Resources.Count == 0)
            return false;

        // 4. Select a random REGIONRESOURCE from the REGIONTYPE
        var resRid = matchedType.SelectRandomResource(_rng);
        var resDef = DefinitionLoader.GetRegionResourceDef(resRid.Index);
        if (resDef == null)
            return false;

        // 5. Skill check against REGIONRESOURCE difficulty
        int difficulty = (resDef.SkillMin + resDef.SkillMax) / 2;
        // Convert from tenths (0-1000) to percentage (0-100)
        int diffPct = difficulty / 10;

        // 6. Fire @ResourceTest trigger
        if (_triggerDispatcher != null)
        {
            var args = new TriggerArgs
            {
                CharSrc = ch,
                N1 = resDef.SkillMin,
                N2 = resDef.SkillMax,
            };
            _triggerDispatcher.FireResourceTrigger(resDef, "ResourceTest", ch, args);
            // If trigger returned True (via TAG.EVENT_RESOURCETEST), cancel
        }

        success = SkillEngine.UseQuick(ch, skill, diffPct);

        if (success)
        {
            // 7. Fire @ResourceGather trigger
            if (_triggerDispatcher != null)
            {
                var args = new TriggerArgs
                {
                    CharSrc = ch,
                    N1 = resDef.Reap,
                };
                _triggerDispatcher.FireResourceTrigger(resDef, "ResourceGather", ch, args);
            }

            // 8. Create the gathered item
            itemId = resDef.Reap;
            amount = _rng.Next(resDef.ReapAmountMin, resDef.ReapAmountMax + 1);

            if (itemId > 0 && amount > 0)
            {
                var item = _world.CreateItem();
                item.BaseId = itemId;
                item.Amount = (ushort)amount;
                if (ch.Backpack != null)
                    item.ContainedIn = ch.Backpack.Uid;
                else
                    _world.PlaceItemWithDecay(item, ch.Position);
            }
        }

        return true; // Region resource was found and processed
    }
}
