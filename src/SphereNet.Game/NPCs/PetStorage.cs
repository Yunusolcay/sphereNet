using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Game.NPCs;

/// <summary>
/// Parking and unparking a pet for stabling and shrinking.
///
/// Source-X <c>CChar::Make_Figurine</c> does NOT destroy the creature: it sets
/// STATF_RIDDEN, disconnects it, and links the figurine to the pet's UID. The same
/// CChar comes back, so everything it owns comes back with it.
///
/// SphereNet used to delete the pet and rebuild it from a hand-listed snapshot, and
/// the snapshot could only ever carry what someone remembered to add. In practice it
/// dropped every TAG - which is where BONDED, the bonding timer and all script state
/// live - plus the follower-slot override, the current mana/stamina pools, and the
/// pet's entire inventory (a loaded pack animal lost its cargo outright). Every field
/// added to Character since was another silent loss.
///
/// <see cref="Mounts.MountEngine"/> already parks a mount this way; this is the same
/// mechanism, shared so stabling and shrinking cannot drift apart again.
/// </summary>
public static class PetStorage
{
    /// <summary>Marks a stable entry or figurine that links to a live parked pet
    /// rather than carrying a snapshot of a deleted one.</summary>
    public const string LinkPrefix = "@link";

    /// <summary>
    /// Take a pet out of play without destroying it: drop it from its sector so it
    /// is invisible and stops ticking, and flag it Ridden so it no longer counts
    /// against its owner's follower cap (Source-X FollowersUpdate on stable).
    /// </summary>
    public static bool Park(Character pet, GameWorld world)
    {
        if (pet.IsDeleted || pet.IsPlayer)
            return false;

        // Source-X Skill_Start(NPCACT_RIDDEN): a parked pet is not mid-fight.
        pet.FightTarget = Serial.Invalid;
        ClearStandingOrders(pet);

        world.HideFromSector(pet);
        pet.SetStatFlag(StatFlag.Ridden);
        return true;
    }

    /// <summary>
    /// Bring a parked pet back to <paramref name="pos"/> for <paramref name="owner"/>.
    /// Fails - leaving the pet parked and the caller's stable entry/figurine intact -
    /// when the pet is gone, the owner is at their follower cap, or the tile refuses
    /// the placement.
    /// </summary>
    public static bool Unpark(Character pet, Character owner, GameWorld world, Point3D pos)
    {
        if (pet.IsDeleted || pet.IsPlayer)
            return false;

        // TryAssignOwnership skips its cap check when the pet already belongs to this
        // owner, and a parked pet never lost its ownership - so the cap is checked
        // here instead (Source-X NPC_StablePetRetrieve checks slots on retrieve).
        if (owner.CurFollower + pet.ControlSlots > owner.MaxFollower)
            return false;

        pet.ClearStatFlag(StatFlag.Ridden);

        if (!world.PlaceCharacter(pet, pos))
        {
            // Put it straight back rather than leaving it visible nowhere.
            pet.SetStatFlag(StatFlag.Ridden);
            world.HideFromSector(pet);
            return false;
        }

        pet.TryAssignOwnership(owner, owner, summoned: false);
        owner.InvalidateFollowerCount();

        // A pet that spent the night in a stable should not act on a target it was
        // chasing when it went in.
        pet.NextNpcActionTime = Environment.TickCount64 + 1000;
        return true;
    }

    /// <summary>Drop the order a pet was carrying when it was taken out of play.
    /// Clearing FightTarget alone was not enough: the order itself lived on in tags,
    /// and the AI put the fight straight back on retrieval - a pet stabled mid-fight
    /// came out swinging at its old victim with no new command given. Source-X ends
    /// the creature's current job outright when it comes back out of a figurine
    /// (Skill_Start(SKILL_NONE), CCharUse.cpp:1205), leaving it idle.
    ///
    /// Only the transient order is dropped. Bonding, script state and the
    /// follower-slot override are the pet's own and must survive the stable.</summary>
    private static void ClearStandingOrders(Character pet)
    {
        pet.RemoveTag("ATTACK_TARGET");
        pet.RemoveTag("FOLLOW_TARGET");
        pet.RemoveTag("GUARD_TARGET");
        pet.RemoveTag("GO_TARGET");
        pet.RemoveTag("PREV_PET_MODE");
        pet.PetAIMode = PetAIMode.Follow;
    }

    /// <summary>True when a parked pet is still present and usable.</summary>
    public static bool IsParked(Character pet) =>
        !pet.IsDeleted && pet.IsStatFlag(StatFlag.Ridden);

    /// <summary>Encode the reference stored on a figurine or in a stable list. The
    /// UUID is the durable half - serials are reassigned across a legacy import,
    /// which is why MountEngine records both too.</summary>
    public static string MakeLink(Character pet) =>
        $"{LinkPrefix}|{pet.Uid.Value}|{pet.Uuid:D}";

    /// <summary>True when <paramref name="raw"/> is a link rather than a legacy
    /// snapshot.</summary>
    public static bool IsLink(string? raw) =>
        raw != null && raw.StartsWith(LinkPrefix + "|", StringComparison.Ordinal);

    /// <summary>Resolve a link back to its pet. A link that RECORDED a UUID is
    /// answered by that UUID alone: serials are reassigned, so falling through to one
    /// when the recorded creature is gone hands back whatever inherited the number -
    /// a different pet, or a player. The serial is consulted only for a link written
    /// before UUIDs were recorded, which is the case the fallback existed for.
    ///
    /// Returns null when the pet no longer exists (a GM removed it, or a save was
    /// rolled back), which callers report rather than silently conjuring a new one.</summary>
    public static Character? Resolve(string? raw, GameWorld world)
    {
        if (!IsLink(raw)) return null;

        var parts = raw!.Split('|');
        if (parts.Length < 3) return null;

        if (Guid.TryParse(parts[2], out var uuid) && uuid != Guid.Empty)
            return Usable(world.FindByUuid(uuid) as Character);

        if (uint.TryParse(parts[1], out uint serial) && serial != 0)
            return Usable(world.FindChar(new Serial(serial)));

        return null;

        static Character? Usable(Character? pet) =>
            pet is { IsDeleted: false, IsPlayer: false } ? pet : null;
    }

    /// <summary>Display name for a stable/figurine entry without waking the pet.</summary>
    public static string DescribeLink(string? raw, GameWorld world) =>
        Resolve(raw, world)?.Name ?? "(missing pet)";
}
