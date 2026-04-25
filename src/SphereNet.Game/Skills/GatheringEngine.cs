using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;

namespace SphereNet.Game.Skills;

public readonly struct GatherResult
{
    public bool Handled { get; init; }
    public bool Success { get; init; }
    public bool Depleted { get; init; }
    public Item? Item { get; init; }
}

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
    /// Sink-aware gather: creates the item but does NOT add to backpack.
    /// Caller uses sink.DeliverItem for stacking + client notification.
    /// Includes sector-based resource depletion tracking.
    /// </summary>
    public GatherResult TryGatherForSink(Character ch, SkillType skill, Point3D target)
    {
        if (!_skillTypeFilters.TryGetValue(skill, out var typeFilter))
            return new GatherResult { Handled = false };

        var region = _world.FindRegion(target);
        if (region == null || region.RegionTypes.Count == 0)
            return new GatherResult { Handled = false };

        RegionTypeDef? matchedType = null;
        foreach (var rtRid in region.RegionTypes)
        {
            var rtDef = DefinitionLoader.GetRegionTypeDef(rtRid.Index);
            if (rtDef == null) continue;

            if (rtDef.ItemTypeFilter != null &&
                rtDef.ItemTypeFilter.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
            {
                matchedType = rtDef;
                break;
            }

            if (rtDef.ItemTypeFilter == null && matchedType == null)
                matchedType = rtDef;
        }

        if (matchedType == null || matchedType.Resources.Count == 0)
            return new GatherResult { Handled = false };

        var resRid = matchedType.SelectRandomResource(_rng);
        var resDef = DefinitionLoader.GetRegionResourceDef(resRid.Index);
        if (resDef == null)
            return new GatherResult { Handled = false };

        // Resource depletion check
        var sector = _world.GetSector(target);
        if (sector != null && resDef.AmountMax > 0)
        {
            int available = sector.GetResourceAmount(resRid.Index, resDef.AmountMax, resDef.Regen);
            if (available <= 0)
                return new GatherResult { Handled = true, Depleted = true };
        }

        int difficulty = (resDef.SkillMin + resDef.SkillMax) / 2;
        int diffPct = difficulty / 10;

        if (_triggerDispatcher != null)
        {
            var args = new TriggerArgs
            {
                CharSrc = ch,
                N1 = resDef.SkillMin,
                N2 = resDef.SkillMax,
            };
            _triggerDispatcher.FireResourceTrigger(resDef, "ResourceTest", ch, args);
        }

        bool success = SkillEngine.UseQuick(ch, skill, diffPct);

        if (success)
        {
            if (_triggerDispatcher != null)
            {
                var args = new TriggerArgs
                {
                    CharSrc = ch,
                    N1 = resDef.Reap,
                };
                _triggerDispatcher.FireResourceTrigger(resDef, "ResourceGather", ch, args);
            }

            ushort itemId = resDef.Reap;
            int amount = _rng.Next(resDef.ReapAmountMin, resDef.ReapAmountMax + 1);

            if (itemId > 0 && amount > 0)
            {
                sector?.ConsumeResource(resRid.Index, amount);

                var item = _world.CreateItem();
                item.BaseId = itemId;
                item.Amount = (ushort)amount;
                return new GatherResult { Handled = true, Success = true, Item = item };
            }
        }

        return new GatherResult { Handled = true, Success = false };
    }

    /// <summary>
    /// Legacy gather path. Returns true if a region resource was found and processed.
    /// Kept for backward compatibility with non-sink callers.
    /// </summary>
    public bool TryGather(Character ch, SkillType skill, Point3D target, out bool success, out ushort itemId, out int amount)
    {
        var result = TryGatherForSink(ch, skill, target);
        success = result.Success;
        itemId = 0;
        amount = 0;

        if (!result.Handled)
            return false;

        if (result.Item != null)
        {
            itemId = result.Item.BaseId;
            amount = result.Item.Amount;

            if (ch.Backpack != null)
                ch.Backpack.AddItem(result.Item);
            else
                _world.PlaceItemWithDecay(result.Item, ch.Position);
        }

        return true;
    }
}
