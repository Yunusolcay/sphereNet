using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.World;

namespace SphereNet.Game.Death;

/// <summary>
/// Corpse and loot system. Maps to CChar::Death and CItemCorpse in Source-X.
/// Handles death processing, corpse creation, loot drop, and decay.
/// </summary>
public sealed class DeathEngine
{
    private readonly GameWorld _world;

    /// <summary>Corpse decay time for players (in seconds).</summary>
    public int CorpseDecayPlayer { get; set; } = 900; // 15 minutes

    /// <summary>Corpse decay time for NPCs (in seconds).</summary>
    public int CorpseDecayNPC { get; set; } = 300; // 5 minutes

    /// <summary>Whether looting others' corpses is a criminal act.</summary>
    public bool LootingIsACrime { get; set; } = true;

    /// <summary>Fired when a character is killed.</summary>
    public event Action<Character, Character?>? OnDeath;

    /// <summary>Party manager reference for loot rights.</summary>
    public Party.PartyManager? PartyManager { get; set; }

    public DeathEngine(GameWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// Process a character's death. Maps to CChar::Death in Source-X.
    /// Creates corpse, drops loot, handles NPC cleanup.
    /// </summary>
    public Item? ProcessDeath(Character victim, Character? killer = null)
    {
        if (victim.IsDead)
            return null;

        // Kill the character
        victim.Kill();

        // Karma/Fame changes for killer
        if (killer != null)
        {
            ApplyKarmaFameChange(killer, victim);

            // PvP murder tracking
            if (victim.IsPlayer && killer.IsPlayer)
            {
                killer.Kills++;
                killer.SetCriminal(120_000); // 2 minutes criminal flag
            }
        }

        OnDeath?.Invoke(victim, killer);

        // Create corpse
        var corpse = CreateCorpse(victim);

        // Drop equipped items and backpack contents to corpse
        if (victim.IsPlayer)
            DropLootToCorpse(victim, corpse);
        else
            DropNpcLootToCorpse(victim, corpse);

        // Set corpse decay timer. Using the Item.DecayTime field (not a
        // TAG) routes this through the sector-tick Item.OnTick path —
        // the same mechanism spell fields and summoned items use.
        // Source-X does the same via CItem::_SetTimeout on the corpse,
        // driven from its sector, with no central scanner.
        int decaySeconds = victim.IsPlayer ? CorpseDecayPlayer : CorpseDecayNPC;
        corpse.DecayTime = Environment.TickCount64 + decaySeconds * 1000;
        corpse.SetTag("OWNER_UID", victim.Uid.Value.ToString());
        corpse.SetTag("OWNER_UUID", victim.Uuid.ToString("D"));

        if (killer != null)
        {
            corpse.SetTag("KILLER_UID", killer.Uid.Value.ToString());
            corpse.SetTag("KILLER_UUID", killer.Uuid.ToString("D"));
        }

        // For NPCs, delete the character (players become ghosts)
        if (!victim.IsPlayer)
            victim.Delete();

        return corpse;
    }

    /// <summary>Apply Karma/Fame changes when killer kills victim.</summary>
    private static void ApplyKarmaFameChange(Character killer, Character victim)
    {
        // Fame: killing something stronger gives more fame
        int tier = Math.Max(1, (victim.Str + victim.Dex + victim.Int) / 100);
        short fameGain = (short)Math.Clamp(tier * 10, 1, 100);
        killer.Fame = (short)Math.Clamp(killer.Fame + fameGain, -10000, 10000);

        // Karma: killing good = bad karma, killing evil = good karma
        if (victim.Karma > 0)
        {
            // Killing an innocent — lose karma
            short karmaLoss = (short)Math.Clamp(victim.Karma / 5 + 10, 5, 200);
            killer.Karma = (short)Math.Clamp(killer.Karma - karmaLoss, -10000, 10000);
        }
        else if (victim.Karma < -50)
        {
            // Killing evil ��� gain karma
            short karmaGain = (short)Math.Clamp(-victim.Karma / 10, 1, 50);
            killer.Karma = (short)Math.Clamp(killer.Karma + karmaGain, -10000, 10000);
        }
    }

    /// <summary>Create a corpse item at the victim's position.</summary>
    private Item CreateCorpse(Character victim)
    {
        var corpse = _world.CreateItem();
        corpse.BaseId = 0x2006; // ITEMID_CORPSE
        corpse.Name = $"corpse of {victim.Name}";
        corpse.ItemType = ItemType.Corpse;
        corpse.Hue = victim.Hue;

        _world.PlaceItem(corpse, victim.Position);
        return corpse;
    }

    /// <summary>Drop player equipment and backpack to corpse.</summary>
    private void DropLootToCorpse(Character victim, Item corpse)
    {
        // Unequip all items (except hair, beard, etc.)
        Layer[] dropLayers = [
            Layer.OneHanded, Layer.TwoHanded, Layer.Shoes, Layer.Pants, Layer.Shirt,
            Layer.Helm, Layer.Gloves, Layer.Ring, Layer.Talisman, Layer.Neck,
            Layer.Waist, Layer.Chest, Layer.Bracelet, Layer.Tunic, Layer.Earrings,
            Layer.Arms, Layer.Cape, Layer.Robe, Layer.Skirt, Layer.Legs
        ];

        foreach (var layer in dropLayers)
        {
            var item = victim.Unequip(layer);
            if (item != null)
                corpse.AddItem(item);
        }

        // Move backpack contents to corpse
        var pack = victim.Backpack;
        if (pack != null)
        {
            var contents = new List<Item>(pack.Contents);
            foreach (var item in contents)
            {
                pack.RemoveItem(item);
                corpse.AddItem(item);
            }
        }
    }

    /// <summary>Drop NPC loot to corpse (all inventory + level-based loot).</summary>
    private void DropNpcLootToCorpse(Character victim, Item corpse)
    {
        DropLootToCorpse(victim, corpse);

        // Generate loot based on NPC brain type / stats tier
        var rand = new Random();
        int tier = Math.Max(1, (victim.Str + victim.Dex + victim.Int) / 60);

        // Gold drop
        int goldAmount = rand.Next(tier * 5, tier * 25 + 1);
        if (goldAmount > 0)
        {
            var gold = _world.CreateItem();
            gold.BaseId = 0x0EED;
            gold.Name = "Gold";
            gold.ItemType = ItemType.Gold;
            gold.Amount = (ushort)Math.Min(goldAmount, 60000);
            corpse.AddItem(gold);
        }

        // Random reagent drops (for magic creatures)
        if (victim.Int > 30 && rand.Next(100) < 40)
        {
            ushort[] reagents = [0x0F7A, 0x0F7B, 0x0F84, 0x0F85, 0x0F86, 0x0F88, 0x0F8C, 0x0F8D];
            var reagent = _world.CreateItem();
            reagent.BaseId = reagents[rand.Next(reagents.Length)];
            reagent.Name = "reagent";
            reagent.Amount = (ushort)rand.Next(1, tier + 1);
            corpse.AddItem(reagent);
        }

        // Gem drops for higher tier NPCs
        if (tier >= 3 && rand.Next(100) < 25)
        {
            ushort[] gems = [0x0F13, 0x0F15, 0x0F16, 0x0F18, 0x0F25, 0x0F26];
            var gem = _world.CreateItem();
            gem.BaseId = gems[rand.Next(gems.Length)];
            gem.Name = "gem";
            gem.Amount = 1;
            corpse.AddItem(gem);
        }
    }

    /// <summary>
    /// Check if looting a corpse is a criminal act.
    /// Maps to CChar::CheckCorpseCrime in Source-X.
    /// </summary>
    public bool IsLootingCriminal(Character looter, Item corpse)
    {
        if (!LootingIsACrime) return false;
        if (looter.PrivLevel >= PrivLevel.GM) return false;

        // Own corpse is not criminal — UUID check first, then Serial fallback
        if (corpse.TryGetTag("OWNER_UUID", out string? ownerUuidStr) &&
            Guid.TryParse(ownerUuidStr, out Guid ownerUuid) &&
            ownerUuid == looter.Uuid)
            return false;

        if (corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
            uint.TryParse(ownerStr, out uint ownerUid) &&
            ownerUid == looter.Uid.Value)
            return false;

        // Party member with loot rights is not criminal
        if (PartyManager != null)
        {
            if (corpse.TryGetTag("OWNER_UID", out string? ownerUidStr) &&
                uint.TryParse(ownerUidStr, out uint ownerUid2))
            {
                var ownerSerial = new Serial(ownerUid2);
                var party = PartyManager.FindParty(looter.Uid);
                if (party != null && party.IsMember(ownerSerial) && party.GetLootFlag(looter.Uid))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Carve a corpse (for hides, meat, etc.).
    /// Maps to @CarveCorpse trigger in Source-X.
    /// </summary>
    public List<Item> CarveCorpse(Character carver, Item corpse)
    {
        var results = new List<Item>();
        if (corpse.ItemType != ItemType.Corpse) return results;

        var rand = new Random();

        // Hides
        if (rand.Next(100) < 70)
        {
            var hides = _world.CreateItem();
            hides.BaseId = 0x1079; // hides
            hides.Name = "hides";
            hides.Amount = (ushort)rand.Next(1, 4);
            AddToPackOrGround(carver, hides);
            results.Add(hides);
        }

        // Raw meat
        var meat = _world.CreateItem();
        meat.BaseId = 0x09F1; // raw ribs
        meat.Name = "raw ribs";
        meat.Amount = (ushort)rand.Next(1, 3);
        AddToPackOrGround(carver, meat);
        results.Add(meat);

        // Bones (low chance)
        if (rand.Next(100) < 20)
        {
            var bones = _world.CreateItem();
            bones.BaseId = 0x0ECA; // bone pile
            bones.Name = "bones";
            bones.Amount = 1;
            AddToPackOrGround(carver, bones);
            results.Add(bones);
        }

        corpse.SetTag("CARVED", "1");
        return results;
    }

    private void AddToPackOrGround(Character ch, Item item)
    {
        var pack = ch.Backpack;
        if (pack != null)
            pack.AddItem(item);
        else
            _world.PlaceItem(item, ch.Position);
    }

    // Corpse decay is now driven by the per-item Item.DecayTime /
    // Item.OnTick path (see Program.cs wiring of Item.OnCorpseDecay).
    // The full-world scan that used to live here burned ~100 ms per
    // tick on busy worlds and fought the sector-sleep design.
}
