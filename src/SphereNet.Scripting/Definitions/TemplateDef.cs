using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// One selection entry inside a TEMPLATE block. Source-X packs use either
/// ID=itemdef (single pick) or ITEMID=itemdef,weight (weighted list).
/// Amount is optional and defaults to 1.
/// </summary>
public sealed class TemplateEntry
{
    /// <summary>Defname of the item or nested template to spawn.</summary>
    public string DefName { get; init; } = "";
    /// <summary>Selection weight when picking randomly (default 1).</summary>
    public int Weight { get; init; } = 1;
    /// <summary>Stack amount for stackable items (e.g. gold). 0 = let the
    /// ItemDef default decide.</summary>
    public int Amount { get; init; }
}

/// <summary>
/// Script <c>[TEMPLATE name]</c> resource. Can behave as:
///   - a <b>list</b> template — every ITEM= line gets spawned (used by NPC
///     equip blocks: ITEM=random_shirts adds one shirt);
///   - a <b>random pick</b> template — ID= / ITEMID= lines build a
///     weighted pool and one entry is picked per resolve.
/// Source-X has both modes in CItemBase / CCharBase template handling.
/// </summary>
public sealed class TemplateDef : ResourceLink
{
    /// <summary>Weighted entries from ID=/ITEMID= lines — random pick.</summary>
    public List<TemplateEntry> RandomEntries { get; } = [];

    /// <summary>Sequential entries from ITEM= lines — each spawned in turn.</summary>
    public List<TemplateEntry> ItemEntries { get; } = [];

    /// <summary>True when this template only has random-pick entries
    /// (i.e. random_hats, random_shirts). False when it enumerates items
    /// that must all be spawned (e.g. a VENDOR_S_* restock list).</summary>
    public bool IsRandomPick => RandomEntries.Count > 0 && ItemEntries.Count == 0;

    public TemplateDef(ResourceId id) : base(id) { }
}
