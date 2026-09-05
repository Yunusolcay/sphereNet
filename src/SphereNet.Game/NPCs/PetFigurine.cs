using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using System.Text;

namespace SphereNet.Game.NPCs;

/// <summary>
/// Pet figurine (shrink / restore). Converts a controlled pet into a figurine item
/// carrying a full snapshot of the pet's state, and recreates the pet from it on use.
/// The snapshot mirrors <see cref="StableEngine"/>'s claim/stable format but lives on
/// the item (a PET_FIGURINE tag) instead of the owner's stable tag list.
/// </summary>
public static class PetFigurine
{
    private const string SnapshotTag = "PET_FIGURINE";

    /// <summary>True when an item carries a pet reference (a shrunk pet). Covers
    /// both the current link form and the legacy snapshot written before shrinking
    /// stopped destroying the pet.</summary>
    public static bool IsPetFigurine(Item item) => item.TryGetTag(SnapshotTag, out _);

    /// <summary>Shrink a controlled pet into the figurine item. Source-X
    /// Make_Figurine parks the creature rather than destroying it, so the figurine
    /// stores a reference and the pet keeps its tags, pools and pack. Validates
    /// ownership and rejects summoned creatures (like stabling).</summary>
    public static bool Shrink(Character owner, Character pet, Item figurine, GameWorld world)
    {
        if (pet.IsPlayer || !pet.HasOwner(owner.Uid) || pet.IsSummoned)
            return false;

        return Store(pet, figurine, world);
    }

    /// <summary>Shrink for the custom s_Shrink spell (Source-X SPELL_Shrink /
    /// NPC_Shrink): works on any unowned NPC or the caster's own pet — never
    /// on players, summons, or another player's pet.</summary>
    public static bool ShrinkBySpell(Character caster, Character npc, Item figurine, GameWorld world)
    {
        if (npc.IsPlayer || npc.IsSummoned)
            return false;
        if (npc.OwnerSerial.IsValid && !npc.HasOwner(caster.Uid))
            return false;

        return Store(npc, figurine, world);
    }

    /// <summary>Park the creature and stamp the figurine that refers to it.</summary>
    private static bool Store(Character pet, Item figurine, GameWorld world)
    {
        string name = pet.Name ?? "";
        if (!PetStorage.Park(pet, world))
            return false;

        figurine.ItemType = ItemType.Figurine;
        figurine.SetTag(SnapshotTag, PetStorage.MakeLink(pet));
        if (string.IsNullOrEmpty(figurine.Name))
            figurine.Name = $"{name} (figurine)";
        return true;
    }

    /// <summary>Recreate the pet stored in a figurine, re-owning it to <paramref name="owner"/>
    /// (follower-cap aware), placing it at <paramref name="pos"/>, and consuming the
    /// figurine. Returns null — and keeps the figurine — when the snapshot is missing/
    /// invalid or the owner's follower cap is full.</summary>
    public static Character? Restore(Character owner, Item figurine, GameWorld world, Point3D pos)
    {
        if (!figurine.TryGetTag(SnapshotTag, out string? raw))
            return null;

        // Current form: the pet is parked, not gone. Wake the same creature so its
        // tags, bonded state, mana/stamina and pack come back with it.
        if (PetStorage.IsLink(raw))
        {
            var parked = PetStorage.Resolve(raw, world);
            if (parked == null || !PetStorage.Unpark(parked, owner, world, pos))
                return null;

            world.DeleteObject(figurine);
            figurine.Delete();
            return parked;
        }

        // Legacy form: a figurine written before shrinking stopped destroying the
        // pet. The creature really is gone, so it can only be rebuilt from what the
        // old snapshot happened to carry.
        if (!TryDeserialize(raw, out var snap))
            return null;

        var pet = world.CreateCharacter();
        pet.Name = snap.Name;
        pet.BodyId = snap.BodyId;
        pet.BaseId = snap.BaseId;
        pet.Hue = new Color(snap.Hue);
        pet.Str = snap.Str;
        pet.Dex = snap.Dex;
        pet.Int = snap.Int;
        pet.MaxHits = snap.Hits;
        pet.Hits = snap.Hits;
        pet.NpcBrain = snap.NpcBrain;
        pet.NpcFood = snap.NpcFood;
        pet.PetAIMode = snap.PetAIMode;
        if (snap.CharDefIndex != 0)
            pet.CharDefIndex = snap.CharDefIndex;

        // Don't release the pet until it can be re-owned (follower cap), mirroring
        // StableEngine.ClaimPet — otherwise a capped owner would lose the figurine
        // AND spawn an ownerless creature.
        if (!pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: true))
        {
            world.DeleteObject(pet);
            pet.Delete();
            return null;
        }

        if (snap.OriginalUuid != Guid.Empty)
        {
            var oldUuid = pet.Uuid;
            pet.Uuid = snap.OriginalUuid;
            world.ReIndexUuid(pet, oldUuid);
        }

        foreach (var (skillId, skillVal) in snap.Skills)
            if (Enum.IsDefined((SkillType)skillId))
                pet.SetSkill((SkillType)skillId, skillVal);

        foreach (uint friendUid in snap.FriendUids)
        {
            var friend = world.FindChar(new Serial(friendUid));
            if (friend != null)
                pet.AddFriend(friend);
        }

        if (!world.PlaceCharacter(pet, pos))
        {
            pet.ClearOwnership(clearFriends: true);
            world.DeleteObject(pet);
            pet.Delete();
            return null;
        }
        world.DeleteObject(figurine);
        figurine.Delete();
        return pet;
    }

    // ---- snapshot serialization (mirrors StableEngine's StabledPet pipe format) ----

    private sealed record Snap(
        string Name, ushort BodyId, ushort BaseId, ushort Hue,
        short Str, short Dex, short Int, short Hits, NpcBrainType NpcBrain, Guid OriginalUuid,
        ushort NpcFood, PetAIMode PetAIMode, List<uint> FriendUids, int CharDefIndex,
        Dictionary<int, ushort> Skills);

    private static string Serialize(Character pet)
    {
        var skills = new Dictionary<int, ushort>();
        foreach (SkillType st in Enum.GetValues<SkillType>())
        {
            if (st == SkillType.None || st >= SkillType.Qty) continue;
            ushort sv = pet.GetSkill(st);
            if (sv > 0) skills[(int)st] = sv;
        }

        var friends = new List<uint>();
        foreach (var kvp in pet.Tags.GetAll())
            if (kvp.Key.StartsWith("FRIEND_", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(kvp.Key["FRIEND_".Length..], out uint uid) && uid != 0)
                friends.Add(uid);

        string name64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pet.Name ?? ""));
        string friendStr = string.Join(',', friends);
        string skillStr = string.Join(',', skills.Select(kv => $"{kv.Key}:{kv.Value}"));
        return string.Join('|',
            name64, pet.BodyId, pet.BaseId, pet.Hue.Value, pet.Str, pet.Dex, pet.Int,
            pet.MaxHits, (int)pet.NpcBrain, pet.Uuid.ToString("D"), pet.NpcFood,
            (int)pet.PetAIMode, friendStr, skillStr, pet.CharDefIndex);
    }

    private static bool TryDeserialize(string? raw, out Snap snap)
    {
        snap = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var p = raw.Split('|');
        if (p.Length < 15) return false;
        try
        {
            var friends = p[12]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(uint.Parse).ToList();
            var skills = new Dictionary<int, ushort>();
            if (!string.IsNullOrEmpty(p[13]))
                foreach (string e in p[13].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var kv = e.Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[0], out int sid) && ushort.TryParse(kv[1], out ushort sv))
                        skills[sid] = sv;
                }
            snap = new Snap(
                Encoding.UTF8.GetString(Convert.FromBase64String(p[0])),
                ushort.Parse(p[1]), ushort.Parse(p[2]), ushort.Parse(p[3]),
                short.Parse(p[4]), short.Parse(p[5]), short.Parse(p[6]), short.Parse(p[7]),
                (NpcBrainType)int.Parse(p[8]),
                Guid.TryParse(p[9], out Guid g) ? g : Guid.Empty,
                ushort.Parse(p[10]), (PetAIMode)int.Parse(p[11]),
                friends, int.Parse(p[14]), skills);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException
            or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }
}
