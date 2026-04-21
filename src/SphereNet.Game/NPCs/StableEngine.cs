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
    // Stable storage: owner UID → list of stabled pet data
    private readonly Dictionary<Serial, List<StabledPet>> _stabled = [];
    private const string StableTagPrefix = "STABLED_PET.";

    public const int MaxStabledPets = 5;
    public const int StableCost = 30; // gold per real-time day

    /// <summary>
    /// Stable a pet for the given owner. Removes pet from world.
    /// </summary>
    public bool StablePet(Character owner, Character pet, GameWorld world)
    {
        if (pet.IsPlayer || !pet.HasOwner(owner.Uid))
            return false;

        var list = GetOwnerStableList(owner);

        if (list.Count >= MaxStabledPets)
            return false;

        list.Add(new StabledPet
        {
            Name = pet.Name,
            BodyId = pet.BodyId,
            BaseId = pet.BaseId,
            Hue = pet.Hue.Value,
            Str = pet.Str,
            Dex = pet.Dex,
            Int = pet.Int,
            Hits = pet.MaxHits,
            NpcBrain = pet.NpcBrain,
            OriginalUuid = pet.Uuid,
            OwnerUid = pet.OwnerSerial.Value,
            ControllerUid = pet.ControllerSerial.Value,
            NpcFood = pet.NpcFood,
            PetAIMode = pet.PetAIMode,
            FriendUids = GetFriendUids(pet),
        });
        PersistOwnerStableList(owner, list);

        world.DeleteObject(pet);
        pet.Delete();

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
        list.RemoveAt(index);
        PersistOwnerStableList(owner, list);

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
        pet.TryAssignOwnership(owner, owner, summoned: false, enforceFollowerCap: false);
        if (data.ControllerUid != 0 && data.ControllerUid != owner.Uid.Value)
            pet.TrySetProperty("CONTROLLER_UID", data.ControllerUid.ToString());

        if (data.OriginalUuid != Guid.Empty)
        {
            var oldUuid = pet.Uuid;
            pet.Uuid = data.OriginalUuid;
            world.ReIndexUuid(pet, oldUuid);
        }

        foreach (uint friendUid in data.FriendUids)
        {
            var friend = world.FindChar(new Serial(friendUid));
            if (friend != null)
                pet.AddFriend(friend);
        }

        world.PlaceCharacter(pet, pos);
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
        if (_stabled.TryGetValue(owner.Uid, out var list))
            return list;

        list = LoadOwnerStableList(owner);
        _stabled[owner.Uid] = list;
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

        public string Serialize()
        {
            string name64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Name ?? ""));
            string friends = string.Join(',', FriendUids);
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
                friends);
        }

        public static bool TryDeserialize(string raw, out StabledPet pet)
        {
            pet = new StabledPet();
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var parts = raw.Split('|');
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
                return true;
            }
            catch
            {
                pet = new StabledPet();
                return false;
            }
        }
    }
}
