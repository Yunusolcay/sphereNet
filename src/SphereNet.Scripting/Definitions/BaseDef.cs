using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Resources;
using SphereNet.Scripting.Variables;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Shared base for ITEMDEF/CHARDEF templates.
/// Maps to CBaseBaseDef in Source-X (CResourceLink + CEntityProps).
/// </summary>
public abstract class BaseDef : ResourceLink
{
    public ushort DispIndex { get; set; }
    public string Name { get; set; } = "";
    public byte Height { get; set; }

    public CanFlags Can { get; set; }
    public int AttackMin { get; set; }
    public int AttackMax { get; set; }
    public int DefenseMin { get; set; }
    public int DefenseMax { get; set; }

    // Range (shared by CHARDEF & ITEMDEF)
    public int RangeMin { get; set; }
    public int RangeMax { get; set; }

    // Res display / level (shared)
    public byte ResLevel { get; set; }
    public ushort ResDispDnHue { get; set; }
    public ushort ResDispDnId { get; set; }

    /// <summary>Dynamic property tags on the definition.</summary>
    public VarMap TagDefs { get; } = new();

    /// <summary>Base property overrides.</summary>
    public VarMap BaseDefs { get; } = new();

    /// <summary>Linked EVENTS resources.</summary>
    public List<ResourceId> Events { get; } = [];

    /// <summary>Resources required for creation (crafting).</summary>
    public List<ResourceId> BaseResources { get; } = [];

    protected BaseDef(ResourceId id) : base(id) { }
}
