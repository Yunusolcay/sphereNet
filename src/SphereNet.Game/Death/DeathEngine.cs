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

        Character? effectiveKiller = killer;
        if (killer != null && killer.NpcMaster.IsValid)
        {
            var master = _world.FindChar(killer.NpcMaster);
            if (master != null && !master.IsDeleted)
                effectiveKiller = master;
        }

        // Kill the character
        victim.Kill();

        // Karma/Fame changes for killer
        if (effectiveKiller != null)
        {
            ApplyKarmaFameChange(effectiveKiller, victim);

            // PvP murder tracking
            if (victim.IsPlayer && effectiveKiller.IsPlayer)
            {
                effectiveKiller.Kills++;
                effectiveKiller.SetCriminal(120_000); // 2 minutes criminal flag
            }
        }

        // Source-X CChar::Death order is critical for players:
        //   1) MakeCorpse  (corpse.Amount = current/original body ID)
        //   2) Broadcast PacketDeath (0xAF) to nearby
        //   3) SetID(ghost) + SetHue(0)  ← only after the corpse exists
        // If we fire OnDeath here (which transitions the player to a ghost
        // body in OnCharacterDeath) BEFORE CreateCorpse runs, the corpse
        // will be created with amount=0x192 (ghost) instead of the player's
        // real body, and the corpse on the ground renders as a ghost shape
        // instead of a normal humanoid corpse. Source-X ordering avoids
        // exactly this.
        var corpse = CreateCorpse(victim);

        // Drop equipped items and backpack contents to corpse
        if (victim.IsPlayer)
            DropLootToCorpse(victim, corpse);
        else
            DropNpcLootToCorpse(victim, corpse);

        // Now that the corpse has snapshotted the original body, fire the
        // death callbacks so OnCharacterDeath / OnNpcKill can swap the
        // mobile to its ghost body and broadcast the new appearance.
        OnDeath?.Invoke(victim, effectiveKiller);

        // Set corpse decay timer. Using the Item.DecayTime field (not a
        // TAG) routes this through the sector-tick Item.OnTick path —
        // the same mechanism spell fields and summoned items use.
        // Source-X does the same via CItem::_SetTimeout on the corpse,
        // driven from its sector, with no central scanner.
        int decaySeconds = victim.IsPlayer ? CorpseDecayPlayer : CorpseDecayNPC;
        corpse.DecayTime = Environment.TickCount64 + decaySeconds * 1000;
        corpse.SetTag("OWNER_UID", victim.Uid.Value.ToString());
        corpse.SetTag("OWNER_UUID", victim.Uuid.ToString("D"));

        if (effectiveKiller != null)
        {
            corpse.SetTag("KILLER_UID", effectiveKiller.Uid.Value.ToString());
            corpse.SetTag("KILLER_UUID", effectiveKiller.Uuid.ToString("D"));
        }

        // For NPCs, remove the mobile from world state immediately so it no
        // longer blocks movement or lingers in sector/object queries after the
        // corpse has been created.
        if (!victim.IsPlayer)
        {
            _world.DeleteObject(victim);
            victim.Delete();
        }

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
        corpse.Amount = victim.BodyId; // body type for corpse display
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
            {
                // Source-X corpse "tag.equiplayer" — preserves the
                // original equip slot so a later Resurrect-with-Corpse
                // pass can put the item back where it came from. We
                // also store it on the item directly via the corpse
                // 0x89 PacketCorpseEquipment path (which reads
                // item.EquipLayer), but Unequip clears IsEquipped and
                // the layer can be wiped by container moves between
                // death and resurrect — so the tag is the durable copy.
                item.SetTag("EQUIPLAYER", ((byte)layer).ToString());
                corpse.AddItem(item);
            }
        }

        // Move backpack contents to corpse — these had no equip slot,
        // so on resurrect they go back to the backpack (no layer tag).
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
    /// Source-X "Resurrect with Corpse" — when a character is resurrected
    /// while standing on (or owning) their own corpse, automatically
    /// re-equip every item that was equipped at death (using the
    /// EQUIPLAYER tag DropLootToCorpse stamped on it) and dump the rest
    /// back into the backpack. The corpse is deleted once empty.
    ///
    /// Returns true iff a matching corpse was found and processed (the
    /// caller can then skip the "you are still naked" path). If the
    /// resurrected character has no backpack yet (rare — fresh char),
    /// remaining items fall to the ground at the corpse position so
    /// they aren't lost.
    ///
    /// Edge cases handled:
    ///   * The corpse may have decayed/been looted between death and
    ///     resurrect — search returns no match, return false.
    ///   * An equip slot may already be occupied (e.g. NPC healer
    ///     handed the player a robe) — fall back to the backpack.
    ///   * The character may be standing one tile off — we sweep the
    ///     character's tile only (matches Source-X CChar::ResurrectFromCorpse
    ///     which reads <c>g_World.GetItemsAt(GetTopPoint())</c>).
    /// </summary>
    public bool RestoreFromCorpse(Character resurrected)
    {
        Item? corpse = null;
        foreach (var item in _world.GetItemsInRange(resurrected.Position, 0))
        {
            if (item.ItemType != ItemType.Corpse) continue;
            if (!item.TryGetTag("OWNER_UID", out string? ownerStr)) continue;
            if (!uint.TryParse(ownerStr, out uint ownerUid)) continue;
            if (ownerUid != resurrected.Uid.Value) continue;
            corpse = item;
            break;
        }
        if (corpse == null) return false;

        // Snapshot first — RemoveItem mutates the underlying list and
        // would otherwise invalidate the iterator after the first call.
        var contents = new List<Item>(corpse.Contents);
        var pack = resurrected.Backpack;

        foreach (var item in contents)
        {
            corpse.RemoveItem(item);

            Layer? targetLayer = null;
            if (item.TryGetTag("EQUIPLAYER", out string? layerStr) &&
                byte.TryParse(layerStr, out byte layerByte))
            {
                targetLayer = (Layer)layerByte;
                item.RemoveTag("EQUIPLAYER");
            }

            bool placed = false;
            if (targetLayer.HasValue && targetLayer.Value != Layer.None)
            {
                // Re-equip on the original layer if free. Equip()
                // returns false on out-of-range; double-equipping the
                // same layer is handled internally by unequipping the
                // previous item, but we keep the slot-occupied check
                // explicit so that unexpected new gear (e.g. a healer
                // robe) isn't silently dropped on the ground.
                if (resurrected.GetEquippedItem(targetLayer.Value) == null &&
                    resurrected.Equip(item, targetLayer.Value))
                {
                    placed = true;
                }
            }

            if (!placed)
            {
                if (pack != null)
                {
                    pack.AddItem(item);
                    placed = true;
                }
                else
                {
                    _world.PlaceItem(item, resurrected.Position);
                    placed = true;
                }
            }
        }

        // Drop any remaining tags so a half-looted corpse doesn't keep
        // the killer/owner metadata alive on the recycled UID.
        corpse.RemoveTag("OWNER_UID");
        corpse.RemoveTag("OWNER_UUID");
        corpse.RemoveTag("KILLER_UID");
        corpse.RemoveTag("KILLER_UUID");

        _world.DeleteObject(corpse);
        return true;
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
