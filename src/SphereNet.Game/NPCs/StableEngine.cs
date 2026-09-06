using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using System.Text;

namespace SphereNet.Game.NPCs;

/// <summary>
/// Stable master functionality. Stores and retrieves player pets.
/// Maps to Source-X stable master NPC brain behavior.
/// </summary>
public sealed class StableEngine
{
    // Stable storage: owner UID → the owner's identity and their stabled pets.
    // The UUID rides along because a UID is reassigned once the character holding it
    // is deleted, and this service outlives the character: keyed on the number alone,
    // a brand new player who inherited the serial was shown - and could claim - the
    // previous owner's stable.
    private readonly Dictionary<Serial, (Guid OwnerUuid, List<StabledPet> Pets)> _stabled = [];
    private const string StableTagPrefix = "STABLED_PET.";

    public const int MaxStabledPets = 5;
    public const int StableCost = 30; // gold per real-time day
    public const int StableTargetRange = 12; // max owner→pet distance to stable

    /// <summary>Per-owner stable capacity (Source-X CCharNPCAct_Vendor): an explicit
    /// MAXPLAYERPETS tag overrides; otherwise the base count plus one slot per ~60.0
    /// of combined handling skill (Taming + Animal Lore + Veterinary). Never below the
    /// base, so an unskilled owner keeps the default 5.</summary>
    public static int GetMaxStabledPets(Character owner)
    {
        if (owner.TryGetTag("MAXPLAYERPETS", out string? tag) &&
            int.TryParse(tag, out int max) && max > 0)
            return max;

        int handling = owner.GetSkill(SkillType.Taming)
                     + owner.GetSkill(SkillType.AnimalLore)
                     + owner.GetSkill(SkillType.Veterinary); // tenths, 0..3600
        return MaxStabledPets + handling / 600;
    }

    /// <summary>
    /// Stable a pet for the given owner. Removes pet from world.
    /// </summary>
    public bool StablePet(Character owner, Character pet, GameWorld world)
    {
        if (pet.IsPlayer || !pet.HasOwner(owner.Uid))
            return false;
        // Source-X stable validation: a summoned/temporary creature can't be stabled,
        // and the pet must be near the owner (the stablemaster only stables a pet the
        // owner can reach — the target cursor enforces this for the player flow).
        if (pet.IsSummoned)
            return false;
        if (owner.MapIndex != pet.MapIndex ||
            owner.Position.GetDistanceTo(pet.Position) > StableTargetRange)
            return false;
        // Source-X CClientTarg.cpp: a pet carrying anything in its pack can't be
        // stabled — the owner must empty it first, otherwise the carried items
        // would be lost with the snapshot.
        if (pet.Backpack is { ContentCount: > 0 })
            return false;

        var list = GetOwnerStableList(owner);

        if (list.Count >= GetMaxStabledPets(owner))
            return false;

        // Source-X CClientTarg stables by Make_Figurine: the creature is parked,
        // not destroyed, so the stable entry only has to remember which pet it is.
        // Rebuilding from a field list dropped every tag (BONDED included), the
        // follower-slot override and the live mana/stamina pools.
        if (!PetStorage.Park(pet, world))
            return false;

        list.Add(new StabledPet { Link = PetStorage.MakeLink(pet), Name = pet.Name ?? "" });
        PersistOwnerStableList(owner, list);

        return true;
    }

    /// <summary>
    /// Claim a stabled pet back. Creates a new NPC in the world.
    /// </summary>
    public Character? ClaimPet(Character owner, int index, GameWorld world, Point3D pos)
    {
        var list = GetOwnerStableList(owner);

        if (index < 0 || index >= list.Count)
            return null;

        var data = list[index];

        // Current form: wake the parked creature itself.
        if (PetStorage.IsLink(data.Link))
        {
            var parked = PetStorage.Resolve(data.Link, world);
            if (parked == null || !PetStorage.Unpark(parked, owner, world, pos))
                return null;

            list.RemoveAt(index);
            PersistOwnerStableList(owner, list);
            return parked;
        }

        // Legacy form: an entry written before stabling stopped destroying the pet.
        var pet = world.CreateCharacter();
        pet.Name = data.Name;
        pet.BodyId = data.BodyId;
        pet.BaseId = data.BaseId;
        pet.Hue = new Color(data.Hue);
        pet.Str = data.Str;
        pet.Dex = data.Dex;
        pet.Int = data.Int;
        pet.MaxHits = data.Hits;
        pet.Hits = data.Hits;
        pet.NpcBrain = data.NpcBrain;
        pet.NpcFood = data.NpcFood;
        pet.PetAIMode = data.PetAIMode;
        if (data.CharDefIndex != 0)
            pet.CharDefIndex = data.CharDefIndex;

        // Source-X CCharNPCAct_Vendor: don't release the stabled pet until it can
        // actually be re-owned. If the follower cap is full, TryAssignOwnership
        // fails — delete the temp NPC and leave the pet in the stable instead of
        // dropping it from the list AND spawning it ownerless into the world.
        if (!pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: true))
        {
            world.DeleteObject(pet);
            pet.Delete();
            return null;
        }

        // Ownership succeeded — now it is safe to commit the stable removal.
        if (data.ControllerUid != 0 && data.ControllerUid != owner.Uid.Value)
            pet.TrySetProperty("CONTROLLER_UID", data.ControllerUid.ToString());

        if (data.OriginalUuid != Guid.Empty)
        {
            var oldUuid = pet.Uuid;
            pet.Uuid = data.OriginalUuid;
            world.ReIndexUuid(pet, oldUuid);
        }

        foreach (var (skillId, skillVal) in data.Skills)
        {
            if (Enum.IsDefined((SkillType)skillId))
                pet.SetSkill((SkillType)skillId, skillVal);
        }

        foreach (uint friendUid in data.FriendUids)
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

        // Commit stable removal only after the restored pet has a valid world tile.
        list.RemoveAt(index);
        PersistOwnerStableList(owner, list);
        return pet;
    }

    /// <summary>Get list of stabled pet names for an owner.</summary>
    public IReadOnlyList<string> GetStabledPetNames(Character owner)
    {
        var list = GetOwnerStableList(owner);
        return list.Select(p => p.Name).ToList();
    }

    public int GetStabledCount(Character owner) =>
        GetOwnerStableList(owner).Count;

    private List<StabledPet> GetOwnerStableList(Character owner)
    {
        // A cache entry answers only for the character it was built for. When the
        // serial has been handed to someone else the entry is stale, and the list is
        // rebuilt from THIS character's own tags - which for a new character is empty.
        if (_stabled.TryGetValue(owner.Uid, out var cached) && cached.OwnerUuid == owner.Uuid)
            return cached.Pets;

        var list = LoadOwnerStableList(owner);
        _stabled[owner.Uid] = (owner.Uuid, list);
        return list;
    }

    private static List<uint> GetFriendUids(Character pet)
    {
        var friends = new List<uint>();
        foreach (var kvp in pet.Tags.GetAll())
        {
            if (!kvp.Key.StartsWith("FRIEND_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (uint.TryParse(kvp.Key["FRIEND_".Length..], out uint uid) && uid != 0)
                friends.Add(uid);
        }

        return friends;
    }

    private void PersistOwnerStableList(Character owner, List<StabledPet> list)
    {
        var existing = owner.Tags.GetAll()
            .Where(kvp => kvp.Key.StartsWith(StableTagPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in existing)
            owner.RemoveTag(key);

        for (int i = 0; i < list.Count; i++)
            owner.SetTag($"{StableTagPrefix}{i}", list[i].Serialize());
    }

    private static List<StabledPet> LoadOwnerStableList(Character owner)
    {
        var entries = new List<(int Index, StabledPet Pet)>();
        foreach (var kvp in owner.Tags.GetAll())
        {
            if (!kvp.Key.StartsWith(StableTagPrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!int.TryParse(kvp.Key[StableTagPrefix.Length..], out int index))
                continue;
            if (StabledPet.TryDeserialize(kvp.Value, out var pet))
                entries.Add((index, pet));
        }

        return entries
            .OrderBy(e => e.Index)
            .Select(e => e.Pet)
            .ToList();
    }

    private sealed class StabledPet
    {
        /// <summary>Reference to the parked pet (PetStorage link form). Empty on a
        /// legacy entry, which carries the snapshot fields below instead.</summary>
        public string Link { get; set; } = "";

        public string Name { get; set; } = "";
        public ushort BodyId { get; set; }
        public ushort BaseId { get; set; }
        public ushort Hue { get; set; }
        public short Str { get; set; }
        public short Dex { get; set; }
        public short Int { get; set; }
        public short Hits { get; set; }
        public NpcBrainType NpcBrain { get; set; }
        public Guid OriginalUuid { get; set; }
        public uint OwnerUid { get; set; }
        public uint ControllerUid { get; set; }
        public ushort NpcFood { get; set; }
        public PetAIMode PetAIMode { get; set; }
        public List<uint> FriendUids { get; set; } = [];
        public int CharDefIndex { get; set; }
        public Dictionary<int, ushort> Skills { get; set; } = [];

        public string Serialize()
        {
            // A parked pet needs nothing but its identity; the creature itself still
            // holds everything else.
            if (PetStorage.IsLink(Link))
                return Link + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Name ?? ""));

            string name64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Name ?? ""));
            string friends = string.Join(',', FriendUids);
            string skills = string.Join(',', Skills.Select(kv => $"{kv.Key}:{kv.Value}"));
            return string.Join('|',
                name64,
                BodyId,
                BaseId,
                Hue,
                Str,
                Dex,
                Int,
                Hits,
                (int)NpcBrain,
                OriginalUuid.ToString("D"),
                OwnerUid,
                ControllerUid,
                NpcFood,
                (int)PetAIMode,
                friends,
                skills,
                CharDefIndex);
        }

        public static bool TryDeserialize(string raw, out StabledPet pet)
        {
            pet = new StabledPet();
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var parts = raw.Split('|');

            if (PetStorage.IsLink(raw))
            {
                // "@link|<uid>|<uuid>|<nameBase64>"
                if (parts.Length < 3) return false;
                pet.Link = string.Join('|', parts[0], parts[1], parts[2]);
                if (parts.Length > 3)
                {
                    try { pet.Name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])); }
                    catch (FormatException) { pet.Name = ""; }
                }
                return true;
            }

            if (parts.Length < 15)
                return false;

            try
            {
                pet.Name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                pet.BodyId = ushort.Parse(parts[1]);
                pet.BaseId = ushort.Parse(parts[2]);
                pet.Hue = ushort.Parse(parts[3]);
                pet.Str = short.Parse(parts[4]);
                pet.Dex = short.Parse(parts[5]);
                pet.Int = short.Parse(parts[6]);
                pet.Hits = short.Parse(parts[7]);
                pet.NpcBrain = (NpcBrainType)int.Parse(parts[8]);
                pet.OriginalUuid = Guid.TryParse(parts[9], out Guid uuid) ? uuid : Guid.Empty;
                pet.OwnerUid = uint.Parse(parts[10]);
                pet.ControllerUid = uint.Parse(parts[11]);
                pet.NpcFood = ushort.Parse(parts[12]);
                pet.PetAIMode = (PetAIMode)int.Parse(parts[13]);
                pet.FriendUids = parts[14]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(uint.Parse)
                    .ToList();
                if (parts.Length > 16 && int.TryParse(parts[16], out int cdi))
                    pet.CharDefIndex = cdi;
                if (parts.Length > 15 && !string.IsNullOrEmpty(parts[15]))
                {
                    foreach (string entry in parts[15].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var kv = entry.Split(':');
                        if (kv.Length == 2 && int.TryParse(kv[0], out int sid) && ushort.TryParse(kv[1], out ushort sv))
                            pet.Skills[sid] = sv;
                    }
                }
                return true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException
                or IndexOutOfRangeException or ArgumentException)
            {
                // Malformed stable TAG entry (hand-edited save / older format) —
                // skip just this pet instead of failing the whole stable load.
                pet = new StabledPet();
                return false;
            }
        }
    }
}
