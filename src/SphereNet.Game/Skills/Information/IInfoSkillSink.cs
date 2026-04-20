using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Skills.Information;

/// <summary>
/// Output sink for an information-skill execution (Anatomy, AnimalLore, ArmsLore,
/// EvalInt, Forensics, ItemID, TasteID). Mirrors the two delivery channels Source-X
/// uses inside CClientTarg.cpp:
///   * <see cref="SysMessage"/>      -> CClient::SysMessage* (text in player's journal)
///   * <see cref="ObjectMessage"/>   -> CClient::addObjMessage (overhead text on the
///                                       targeted object so the player sees the
///                                       analysis right above the creature/item).
///
/// Keeping this as an interface lets <see cref="InfoSkillEngine"/> stay engine-only
/// (no GameClient/Network references) and unit-testable with a fake sink.
/// </summary>
public interface IInfoSkillSink
{
    /// <summary>The character performing the skill (Source-X m_pChar).</summary>
    Character Self { get; }

    /// <summary>RNG source used for fTest randomisation. Engine never news up Random itself.</summary>
    Random Random { get; }

    /// <summary>Append a journal line to the performer's client.</summary>
    void SysMessage(string text);

    /// <summary>Render an overhead text bubble on <paramref name="target"/> visible to the performer.</summary>
    void ObjectMessage(ObjBase target, string text);
}
