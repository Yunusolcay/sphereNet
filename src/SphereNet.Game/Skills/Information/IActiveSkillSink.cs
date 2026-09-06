using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Skills.Information;

/// <summary>
/// Extends <see cref="IInfoSkillSink"/> with the side-channels that active
/// skills (Healing, Hiding, Stealing, Meditation, ...) need: pose Emote()
/// over the actor, play a Sound() to nearby clients, and reach the world
/// for range/visibility checks. Implementing classes are typically the
/// <c>GameClient</c>; tests use a recording mock.
///
/// Active-skill methods on <see cref="ActiveSkillEngine"/> consume only this
/// surface, never the network layer directly, so logic is fully reusable
/// (NPC AI, scripted triggers, unit tests).
/// </summary>
public interface IActiveSkillSink : IInfoSkillSink
{
    /// <summary>Live game-world reference (entity lookup, range checks).</summary>
    GameWorld World { get; }

    /// <summary>
    /// Source-X <c>Emote</c>: third-person pose printed over <see cref="IInfoSkillSink.Self"/>
    /// for every observer in range. Action verbs like *attempts to mend*.
    /// </summary>
    void Emote(string text);

    /// <summary>
    /// Source-X <c>Sound()</c>: short SFX played at the actor's tile and
    /// broadcast to nearby clients (e.g. 0x0F9 meditation chime, 0x24A spirit speak).
    /// </summary>
    void Sound(ushort soundId);

    /// <summary>
    /// Broadcast a 0x6E animation packet for the actor to all nearby clients.
    /// </summary>
    void Animation(ushort animId);

    /// <summary>
    /// Search the actor's backpack (and one level of sub-containers) for the
    /// first item of <paramref name="type"/>. Returns null when not present.
    /// </summary>
    Item? FindBackpackItem(Core.Enums.ItemType type);

    /// <summary>
    /// Source-X <c>ConsumeAmount(1)</c> stand-in. Decrement amount or, when
    /// amount drops to zero, remove the item from its container/world and
    /// notify observers.
    /// </summary>
    void ConsumeAmount(Item item, ushort amount = 1);

    /// <summary>
    /// Source-X <c>ItemBounce(pItem)</c>: drop a freshly-created item into
    /// the actor's backpack (or the ground if none exists).
    /// </summary>
    void DeliverItem(Item item);

    /// <summary>Open a successfully snooped container for the acting client.</summary>
    void OpenContainer(Item container) { }

    /// <summary>The crafting engine, for the RESOURCES a repair spends
    /// (Source-X Use_Repair -> ResourceConsumePart). Null in bare test contexts,
    /// where the repair falls back to charging nothing.</summary>
    Crafting.CraftingEngine? Crafting => null;

    /// <summary>
    /// Resurrect a dead character through the full client pipeline (ghost→alive body,
    /// corpse restore, equipment re-equip, client packet sync). Falls back to
    /// <c>Character.Resurrect()</c> for offline/NPC targets.
    /// </summary>
    void ResurrectTarget(Objects.Characters.Character target) => target.Resurrect();

    /// <summary>
    /// Move a character through the world engine (sector update + broadcast).
    /// Default falls back to direct position assignment for offline/test contexts.
    /// </summary>
    void MoveCharacter(Objects.Characters.Character ch, Point3D dest) => World.MoveCharacter(ch, dest);
}
