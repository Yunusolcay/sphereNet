using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Mounts;

/// <summary>
/// Mount/dismount engine. Maps to Source-X CChar::Horse_Mount / Horse_UnMount.
/// NPC is hidden (removed from sector) while mounted and restored on dismount,
/// preserving all stats, skills, karma, kills etc.
/// </summary>
public sealed class MountEngine
{
    private readonly GameWorld _world;

    public MountEngine(GameWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// Attempt to mount an NPC. Returns true on success.
    /// The NPC is removed from the visible world but kept in the object table.
    /// </summary>
    public bool TryMount(Character rider, Character npc, ushort mountItemOverride = 0)
    {
        if (rider.IsDead || rider.IsMounted || npc.IsDead)
            return false;
        if (rider.FightTarget.IsValid || rider.IsStatFlag(StatFlag.Freeze))
            return false;
        if (rider.MapIndex != npc.MapIndex || rider.Position.GetDistanceTo(npc.Position) > 1)
            return false;
        // Source-X Horse_Mount requires NPC_IsOwnedBy. Mounting must never tame
        // or transfer a wild/foreign creature as a side effect.
        if (rider.PrivLevel < PrivLevel.GM && !npc.CanAcceptPetCommandFrom(rider, allowFriends: true))
            return false;

        ushort mountItemId = mountItemOverride != 0 ? mountItemOverride : GetMountItemId(npc.BodyId);
        if (mountItemId == 0)
            return false;

        // Store NPC identity so we can find the same NPC on dismount
        rider.Tags.Set("MOUNT_NPC_SERIAL", npc.Uid.Value.ToString());
        rider.Tags.Set("MOUNT_NPC_UUID", npc.Uuid.ToString("D"));
        // ...and the way back, so a mount that is deleted can break the rider's half
        // of the link instead of leaving it to be re-resolved later.
        npc.SetTag(RiderUuidTag, rider.Uuid.ToString("D"));

        // Hide NPC: remove from sector so it is invisible and won't tick,
        // but keep it in the world object table so it survives save/load.
        // Rider position is left untouched — the client's auto-walk already
        // brought the player to the mount and the caller enforces mount range.
        _world.HideFromSector(npc);
        npc.SetStatFlag(StatFlag.Ridden);

        // Create mount item and equip at Layer.Horse
        var mountItem = _world.CreateItem();
        mountItem.BaseId = mountItemId;
        mountItem.Hue = npc.Hue;
        mountItem.Name = "mount";

        rider.Equip(mountItem, Layer.Horse);
        rider.SetStatFlag(StatFlag.OnHorse);

        return true;
    }

    /// <summary>
    /// Keep persisted mount state consistent after login/load:
    /// - If rider has Layer.Horse item but missing OnHorse flag, restore flag.
    /// - If rider has OnHorse flag but missing Layer.Horse item, try rebuilding
    ///   the horse-layer item from saved mount tags.
    /// - If rebuild is impossible, clear invalid mounted state.
    /// </summary>
    public void EnsureMountedState(Character rider)
    {
        var horseItem = rider.GetEquippedItem(Layer.Horse);
        bool hasHorseItem = horseItem != null;
        bool hasMountedFlag = rider.IsMounted;

        Character? mountNpc = ResolveMountNpc(rider);
        if (mountNpc != null)
            mountNpc.SetTag(RiderUuidTag, rider.Uuid.ToString("D"));  // stamp older saves

        // Fallback: try legacy body tags for backward compat with old saves
        ushort taggedBodyId = 0;
        if (mountNpc == null && rider.TryGetTag("MOUNT_NPC_BODY", out string? bodyStr) &&
            !string.IsNullOrWhiteSpace(bodyStr))
        {
            _ = ushort.TryParse(bodyStr, out taggedBodyId);
        }

        ushort expectedMountItemId = mountNpc != null
            ? GetMountItemId(mountNpc.BodyId)
            : (taggedBodyId != 0 ? GetMountItemId(taggedBodyId) : (ushort)0);

        if (hasHorseItem && !hasMountedFlag)
        {
            rider.SetStatFlag(StatFlag.OnHorse);
        }

        if (horseItem != null)
        {
            bool needsRebuild = horseItem.IsDeleted || _world.FindItem(horseItem.Uid) == null;
            if (!needsRebuild && expectedMountItemId != 0 && horseItem.BaseId != expectedMountItemId)
            {
                horseItem.BaseId = expectedMountItemId;
            }
            if (!needsRebuild)
            {
                horseItem.IsEquipped = true;
                horseItem.EquipLayer = Layer.Horse;
                horseItem.ContainedIn = rider.Uid;
                if (horseItem.Amount == 0)
                    horseItem.Amount = 1;

                // Ensure mount NPC stays hidden while mounted AND follows the
                // rider's position. Without the position sync, an old save may
                // have the NPC's Position at a stale pre-mount tile — dismount
                // would then drop it far from the rider.
                if (mountNpc != null)
                {
                    if (!mountNpc.IsStatFlag(StatFlag.Ridden))
                    {
                        _world.HideFromSector(mountNpc);
                        mountNpc.SetStatFlag(StatFlag.Ridden);
                    }
                    if (mountNpc.Position != rider.Position)
                        mountNpc.Position = rider.Position;
                }
                return;
            }
        }

        if (!rider.IsMounted && expectedMountItemId == 0)
            return;

        ushort mountItemId = expectedMountItemId;
        if (mountItemId == 0)
        {
            rider.ClearStatFlag(StatFlag.OnHorse);
            return;
        }

        ushort hue = mountNpc?.Hue.Value ?? (ushort)0;
        if (hue == 0 && rider.TryGetTag("MOUNT_NPC_HUE", out string? hueStr) &&
            !string.IsNullOrWhiteSpace(hueStr))
            _ = ushort.TryParse(hueStr, out hue);

        var newMountItem = _world.CreateItem();
        newMountItem.BaseId = mountItemId;
        newMountItem.Hue = new Color(hue);
        newMountItem.Name = "mount";
        rider.Equip(newMountItem, Layer.Horse);
        rider.SetStatFlag(StatFlag.OnHorse);

        if (mountNpc != null)
        {
            if (!mountNpc.IsStatFlag(StatFlag.Ridden))
            {
                _world.HideFromSector(mountNpc);
                mountNpc.SetStatFlag(StatFlag.Ridden);
            }
            if (mountNpc.Position != rider.Position)
                mountNpc.Position = rider.Position;
        }
    }

    /// <summary>Tag on the mount NPC naming the rider it is carrying. Source-X finds
    /// the rider's side of the link from the ridden creature itself when the creature
    /// is torn down (Horse_GetMountItem in DeleteCleanup, CChar.cpp:395).</summary>
    private const string RiderUuidTag = "MOUNT_RIDER_UUID";

    /// <summary>The creature a rider is mounted on, or null.
    ///
    /// A rider that RECORDED a UUID is answered by that UUID alone. Serials are
    /// reassigned, so falling through to one when the recorded creature is gone
    /// returned whatever inherited the number - and the repair path then hid that
    /// creature, moved it onto the rider and handed it back from Dismount, which for
    /// a Player meant a real character was flagged Ridden, teleported and stamped
    /// with pet ownership. The serial answers only a link that never carried a UUID.
    /// A resolved object that is deleted, or is a player, is refused outright.</summary>
    private Character? ResolveMountNpc(Character rider)
    {
        if (rider.TryGetTag("MOUNT_NPC_UUID", out string? uuidStr) &&
            Guid.TryParse(uuidStr, out Guid npcUuid) && npcUuid != Guid.Empty)
            return Usable(_world.FindByUuid(npcUuid) as Character);

        if (rider.TryGetTag("MOUNT_NPC_SERIAL", out string? serialStr) &&
            uint.TryParse(serialStr, out uint npcSerial) && npcSerial != 0)
            return Usable(_world.FindChar(new Serial(npcSerial)));

        return null;

        static Character? Usable(Character? npc) =>
            npc is { IsDeleted: false, IsPlayer: false } ? npc : null;
    }

    /// <summary>A creature someone was riding has been deleted: break the rider's
    /// half of the link. Source-X does this in DeleteCleanup (CChar.cpp:395) - it
    /// clears the mount item's NPC uid, deletes the item and drops the ridden flag -
    /// so the link is never left to be re-resolved against a reassigned serial.</summary>
    public void OnMountNpcDeleted(Character npc)
    {
        if (!npc.TryGetTag(RiderUuidTag, out string? riderUuid) ||
            !Guid.TryParse(riderUuid, out Guid uuid) || uuid == Guid.Empty)
            return;

        npc.RemoveTag(RiderUuidTag);
        if (_world.FindByUuid(uuid) is not Character rider || rider.IsDeleted)
            return;

        var mountItem = rider.GetEquippedItem(Layer.Horse);
        if (mountItem != null)
        {
            rider.Unequip(Layer.Horse);
            _world.DeleteObject(mountItem);
            mountItem.Delete();
        }

        rider.ClearStatFlag(StatFlag.OnHorse);
        rider.RemoveTag("MOUNT_NPC_SERIAL");
        rider.RemoveTag("MOUNT_NPC_UUID");
        rider.RemoveTag("MOUNT_NPC_BODY");
        rider.RemoveTag("MOUNT_NPC_BASE");
        rider.RemoveTag("MOUNT_NPC_HUE");
        rider.RemoveTag("MOUNT_NPC_NAME");
    }

    /// <summary>
    /// Dismount the rider. Returns the original NPC (same object, all stats preserved).
    /// </summary>
    public Character? Dismount(Character rider, Func<Character, bool>? beforeDismount = null)
    {
        if (!rider.IsMounted)
            return null;

        Character? npc = ResolveMountNpc(rider);
        if (npc != null && beforeDismount?.Invoke(npc) == true)
            return null;

        var mountItem = rider.GetEquippedItem(Layer.Horse);
        if (mountItem != null)
        {
            rider.Unequip(Layer.Horse);
            _world.DeleteObject(mountItem);
            mountItem.Delete();
        }

        rider.ClearStatFlag(StatFlag.OnHorse);

        // Find the original NPC — UUID first, then Serial fallback
        rider.RemoveTag("MOUNT_NPC_SERIAL");
        rider.RemoveTag("MOUNT_NPC_UUID");
        rider.RemoveTag("MOUNT_NPC_BODY");
        rider.RemoveTag("MOUNT_NPC_BASE");
        rider.RemoveTag("MOUNT_NPC_HUE");
        rider.RemoveTag("MOUNT_NPC_NAME");

        if (npc == null)
            return null;

        npc.RemoveTag(RiderUuidTag);

        // Source-X Use_Figurine pulls the pet out of ridden/idle space itself
        // (StatFlag_Clear(STATF_RIDDEN), CCharUse.cpp:1197-1198) — callers were
        // each clearing it after the fact, and any path that forgot left the
        // mount invisible to its own AI tick (OnTickAction skips Ridden chars).
        npc.ClearStatFlag(StatFlag.Ridden);
        npc.Direction = rider.Direction;
        npc.NextNpcActionTime = Environment.TickCount64 + 1500;

        // Source-X Use_Figurine (CCharUse.cpp:1202-1206): every dismount
        // unconditionally re-owns the mount to the rider (NPC_PetSetOwner →
        // MEMORY_IPET memory), then leaves the pet IDLE (Skill_Start(SKILL_NONE)).
        // An idle owned pet that sees its owner defaults to GUARD_TARG
        // (NPC_LookAtChar, CCharNPCAct.cpp:1036), and NPC_Act_Guard joins any
        // fight the owner starts (CCharNPCAct.cpp:1300-1303). Guard mode is
        // that net effect: the mount stays with its owner AND auto-attacks
        // the owner's fight target.
        npc.TryAssignOwnership(rider, rider);
        npc.PetAIMode = PetAIMode.Guard;
        npc.SetTag("GUARD_TARGET", rider.Uid.Value.ToString());

        var pos = rider.Position;
        // Seat the spawned mount NPC through the shared standing resolver
        // (multi decks and house floors included — GetEffectiveZ saw neither).
        var mountStand = _world.Standing.ResolveStandingSurface(npc,
            pos.Map, pos.X, pos.Y, pos.Z,
            SphereNet.Game.Movement.WalkCheck.StandingPolicy.Settle);
        if (mountStand.Found && pos.Z < mountStand.Z)
            pos = new Point3D(pos.X, pos.Y, mountStand.Z, pos.Map);
        _world.PlaceCharacter(npc, pos);

        return npc;
    }

    /// <summary>
    /// Check if a character body is mountable.
    /// </summary>
    public static bool IsMountable(ushort bodyId) => GetMountItemId(bodyId) != 0;

    /// <summary>
    /// Map body ID to mount item graphic.
    /// Source-X: CCharBase::IsMount + item graphic lookup.
    /// </summary>
    public static ushort GetMountItemId(ushort bodyId) => bodyId switch
    {
        // PreT2A
        0xC8 => 0x3E9F, // horse 1
        0xE2 => 0x3EA0, // horse 2
        0xE4 => 0x3EA1, // horse 3
        0xCC => 0x3EA2, // horse 4
        // T2A
        0xD2 => 0x3EA3, // desert ostard
        0xDA => 0x3EA4, // frenzied ostard
        0xDB => 0x3EA5, // forest ostard
        0xDC => 0x3EA6, // llama
        // LBR and later
        0xBE => 0x3E9E, // fire steed
        0x72 => 0x3EA7, // dark steed
        0x75 => 0x3EA8, // silver steed
        0x73 => 0x3EAA, // ethereal horse
        0xAA => 0x3EAB, // ethereal llama
        0xAB => 0x3EAC, // ethereal ostard
        0x84 => 0x3EAD, // kirin
        0x78 => 0x3EAF, // minax warhorse
        0x79 => 0x3EB0, // shadowlord warhorse
        0x77 => 0x3EB1, // mage council warhorse
        0x76 => 0x3EB2, // britannian warhorse
        0x90 => 0x3EB3, // sea horse
        0x7A => 0x3EB4, // unicorn
        0x74 => 0x3EB7, // nightmare
        0xBC => 0x3EB8, // savage ridgeback
        0xBB => 0x3EBA, // ridgeback
        0x319 => 0x3EBB, // skeletal mount
        0x314 => 0x3EBB, // skeletal mount (alt)
        0x317 => 0x3EBC, // beetle
        0x31A => 0x3EBD, // swamp dragon
        0x31F => 0x3EBE, // armored swamp dragon
        0xF3 => 0x3E94, // hiryu
        0x11C => 0x3E92, // armored steed
        0x115 => 0x3E91, // cu sidhe
        0x114 => 0x3E90, // reptalon
        0xD5 => 0x3EC5, // polar bear
        0x124 => 0x3E4A, // pack llama
        0x123 => 0x3E4B, // pack horse
        // Legacy aliases used by some script packs
        0xC9 => 0x3EA0,
        0xCA => 0x3EA0,
        0xCB => 0x3EA0,
        0xD4 => 0x3EA5,
        0x39 => 0x3EB8,
        0x3A => 0x3EBA,
        0x34 => 0x3EAA,
        0x17 => 0x3EAB,
        0x19 => 0x3EAC,
        _ => 0
    };
}
