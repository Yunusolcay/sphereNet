namespace SphereNet.Core.Enums;

/// <summary>
/// What came of a character touching a spell field, by walking onto it or by
/// standing in it.
///
/// The distinction between <see cref="Handled"/> and <see cref="SpellHit"/> is
/// the one Source-X draws in its location check: it caps a step at ONE spell
/// effect, but the cap follows the RESULT rather than the attempt —
/// <c>fSpellHit = OnSpellEffect(...)</c> (CCharAct.cpp:5008). A refusal such as
/// an invulnerable target (CCharSpell.cpp:3762) returns false and leaves the cap
/// clear, so the next field on the tile still gets its chance. Collapsing both
/// into one "handled" boolean would let an inert field swallow the one behind it.
/// </summary>
public enum FieldTouchResult
{
    /// <summary>Not a typed spell field — the caller's legacy flat-damage path
    /// (script-made fields carrying only FIELD_DAMAGE) still runs.</summary>
    NotHandled = 0,

    /// <summary>Recognised and consumed here, but no spell effect landed: an
    /// immune target, or a pure barrier that only blocks passage.</summary>
    Handled = 1,

    /// <summary>A spell effect was actually applied. This is what closes the
    /// door on further spell fields for this location check.</summary>
    SpellHit = 2,
}
