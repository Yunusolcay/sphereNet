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
/// Per-tile invisible marker items track depletion (Source-X parity).
/// </summary>
public sealed class GatheringEngine
{
    private readonly GameWorld _world;
    private readonly TriggerDispatcher? _triggerDispatcher;
    private readonly Random _rng = new();

    private const string TagResourceMarker = "RESOURCE_MARKER";
    private const string TagSkillType = "RES_SKILL";
    private const ushort MarkerBaseId = 0x1;

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
    /// Per-tile invisible marker items track resource depletion.
    /// </summary>
    public GatherResult TryGatherForSink(Character ch, SkillType skill, Point3D target)
    {
        if (!_skillTypeFilters.TryGetValue(skill, out var typeFilter))
            return new GatherResult { Handled = false };

        RegionTypeDef? matchedType = null;

        var region = _world.FindRegion(target);
        if (region != null && region.RegionTypes.Count > 0)
        {
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
        }

        matchedType ??= DefinitionLoader.FindRegionTypeByFilter(typeFilter);

        if (matchedType == null || matchedType.Resources.Count == 0)
            return new GatherResult { Handled = false };

        var resRid = matchedType.SelectRandomResource(_rng);
        var resDef = DefinitionLoader.GetRegionResourceDef(resRid.Index);
        if (resDef == null)
            return new GatherResult { Handled = false };

        // mr_nothing: weighted "found nothing" result
        if (resDef.Reap == 0)
            return new GatherResult { Handled = true, Success = false };

        string skillTag = skill.ToString();
        var marker = FindMarker(target, skillTag);

        if (marker != null && marker.Amount <= 0)
            return new GatherResult { Handled = true, Depleted = true };

        int playerSkill = ch.GetSkill(skill);
        if (resDef.SkillMin > 0 && playerSkill < resDef.SkillMin)
            return new GatherResult { Handled = true, Success = false };

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

            int reapAmount = _rng.Next(resDef.ReapAmountMin, resDef.ReapAmountMax + 1);

            if (marker == null)
            {
                int poolAmount = _rng.Next(
                    Math.Max(1, resDef.AmountMin),
                    Math.Max(2, resDef.AmountMax + 1));
                marker = CreateMarker(target, skillTag, (ushort)poolAmount, resDef.Regen);
            }

            int remaining = marker.Amount - reapAmount;
            if (remaining <= 0)
            {
                marker.Amount = 0;
                long regenMs = resDef.Regen > 0 ? resDef.Regen * 1000L : 36_000_000L;
                marker.DecayTime = Environment.TickCount64 + regenMs;
            }
            else
            {
                marker.Amount = (ushort)remaining;
            }

            var item = _world.CreateItem();
            item.BaseId = resDef.Reap;
            item.Amount = (ushort)reapAmount;
            return new GatherResult { Handled = true, Success = true, Item = item };
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

    private Item? FindMarker(Point3D tile, string skillTag)
    {
        foreach (var item in _world.GetItemsInRange(tile, 0))
        {
            if (item.BaseId != MarkerBaseId) continue;
            if (!item.TryGetTag(TagResourceMarker, out string? mk) || mk != "1") continue;
            if (!item.TryGetTag(TagSkillType, out string? st) || st != skillTag) continue;
            if (item.X == tile.X && item.Y == tile.Y) return item;
        }
        return null;
    }

    private Item CreateMarker(Point3D tile, string skillTag, ushort amount, int regenSeconds)
    {
        var marker = _world.CreateItem();
        marker.BaseId = MarkerBaseId;
        marker.Amount = amount;
        marker.SetAttr(ObjAttributes.Invis | ObjAttributes.Move_Never);
        marker.SetTag(TagResourceMarker, "1");
        marker.SetTag(TagSkillType, skillTag);

        long regenMs = regenSeconds > 0 ? regenSeconds * 1000L : 36_000_000L;
        marker.DecayTime = Environment.TickCount64 + regenMs;

        _world.PlaceItem(marker, tile);
        return marker;
    }
}
