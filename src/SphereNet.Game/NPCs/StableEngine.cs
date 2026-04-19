using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Game.NPCs;

/// <summary>
/// Stable master functionality. Stores and retrieves player pets.
/// Maps to Source-X stable master NPC brain behavior.
/// </summary>
public sealed class StableEngine
{
    // Stable storage: owner UID → list of stabled pet data
    private readonly Dictionary<Serial, List<StabledPet>> _stabled = [];

    public const int MaxStabledPets = 5;
    public const int StableCost = 30; // gold per real-time day

    /// <summary>
    /// Stable a pet for the given owner. Removes pet from world.
    /// </summary>
    public bool StablePet(Character owner, Character pet, GameWorld world)
    {
        if (pet.IsPlayer || pet.NpcMaster != owner.Uid)
            return false;

        if (!_stabled.TryGetValue(owner.Uid, out var list))
        {
            list = [];
            _stabled[owner.Uid] = list;
        }

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
        });

        world.DeleteObject(pet);
        pet.Delete();

        return true;
    }

    /// <summary>
    /// Claim a stabled pet back. Creates a new NPC in the world.
    /// </summary>
    public Character? ClaimPet(Character owner, int index, GameWorld world, Point3D pos)
    {
        if (!_stabled.TryGetValue(owner.Uid, out var list))
            return null;

        if (index < 0 || index >= list.Count)
            return null;

        var data = list[index];
        list.RemoveAt(index);

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
        pet.NpcMaster = owner.Uid;

        if (data.OriginalUuid != Guid.Empty)
        {
            var oldUuid = pet.Uuid;
            pet.Uuid = data.OriginalUuid;
            world.ReIndexUuid(pet, oldUuid);
        }

        world.PlaceCharacter(pet, pos);
        return pet;
    }

    /// <summary>Get list of stabled pet names for an owner.</summary>
    public IReadOnlyList<string> GetStabledPetNames(Character owner)
    {
        if (!_stabled.TryGetValue(owner.Uid, out var list))
            return [];
        return list.Select(p => p.Name).ToList();
    }

    public int GetStabledCount(Character owner) =>
        _stabled.TryGetValue(owner.Uid, out var list) ? list.Count : 0;

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
    }
}
