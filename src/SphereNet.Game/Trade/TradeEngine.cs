using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Game.Trade;

/// <summary>
/// A trade item entry for vendor buy/sell lists.
/// </summary>
public readonly struct TradeEntry
{
    public Serial ItemUid { get; init; }
    public ushort ItemId { get; init; }
    public string Name { get; init; }
    public int Price { get; init; }
    public int Amount { get; init; }
}

/// <summary>
/// A secure trade session between two players (trade window).
/// Maps to CItemStone / trade container in Source-X.
/// </summary>
public sealed class SecureTrade
{
    private readonly Serial _sessionId;
    private readonly Character _initiator;
    private readonly Character _partner;

    private readonly List<Item> _initiatorItems = [];
    private readonly List<Item> _partnerItems = [];
    private int _initiatorGold;
    private int _partnerGold;

    private bool _initiatorAccepted;
    private bool _partnerAccepted;
    private bool _isCompleted;

    public Serial SessionId => _sessionId;
    public Character Initiator => _initiator;
    public Character Partner => _partner;
    public bool IsCompleted => _isCompleted;
    public IReadOnlyList<Item> InitiatorItems => _initiatorItems;
    public IReadOnlyList<Item> PartnerItems => _partnerItems;

    public SecureTrade(Serial sessionId, Character initiator, Character partner)
    {
        _sessionId = sessionId;
        _initiator = initiator;
        _partner = partner;
    }

    public void AddItem(Character from, Item item)
    {
        if (_isCompleted) return;

        if (from == _initiator) _initiatorItems.Add(item);
        else if (from == _partner) _partnerItems.Add(item);

        ResetAcceptance();
    }

    public void RemoveItem(Character from, Item item)
    {
        if (_isCompleted) return;

        if (from == _initiator) _initiatorItems.Remove(item);
        else if (from == _partner) _partnerItems.Remove(item);

        ResetAcceptance();
    }

    public void SetGold(Character from, int amount)
    {
        if (_isCompleted) return;

        if (from == _initiator) _initiatorGold = amount;
        else if (from == _partner) _partnerGold = amount;

        ResetAcceptance();
    }

    public void Accept(Character from)
    {
        if (_isCompleted) return;

        if (from == _initiator) _initiatorAccepted = true;
        else if (from == _partner) _partnerAccepted = true;

        if (_initiatorAccepted && _partnerAccepted)
            CompleteTrade();
    }

    public void Cancel()
    {
        if (_isCompleted) return;
        _isCompleted = true;

        // Return all items to original owners (handled by caller)
    }

    private void CompleteTrade()
    {
        _isCompleted = true;

        var partnerBackpack = _partner.Backpack;
        var initiatorBackpack = _initiator.Backpack;

        foreach (var item in _initiatorItems)
        {
            item.IsEquipped = false;
            if (partnerBackpack != null)
                partnerBackpack.AddItem(item);
            else
                item.ContainedIn = _partner.Uid;
            item.Position = _partner.Position;
        }

        foreach (var item in _partnerItems)
        {
            item.IsEquipped = false;
            if (initiatorBackpack != null)
                initiatorBackpack.AddItem(item);
            else
                item.ContainedIn = _initiator.Uid;
            item.Position = _initiator.Position;
        }
    }

    private void ResetAcceptance()
    {
        _initiatorAccepted = false;
        _partnerAccepted = false;
    }
}

/// <summary>
/// Vendor trade engine: buy/sell with NPCs.
/// Maps to CClient::Event_VendorBuy/Sell in Source-X.
/// </summary>
public static class VendorEngine
{
    /// <summary>Reference to the world for container lookups.</summary>
    public static GameWorld? World { get; set; }

    /// <summary>
    /// Process a buy request from player to vendor.
    /// Returns total gold cost. Negative = insufficient gold.
    /// </summary>
    public static int ProcessBuy(Character player, Character vendor, IReadOnlyList<TradeEntry> items)
    {
        if (vendor.NpcBrain != Core.Enums.NpcBrainType.Vendor)
            return -1;

        int totalCost = 0;
        foreach (var entry in items)
            totalCost += entry.Price * entry.Amount;

        bool isStaff = player.PrivLevel >= Core.Enums.PrivLevel.GM;
        bool isBot = Diagnostics.BotClient.IsBotCharName(player.Name ?? "");
        bool isOwner = vendor.HasOwner(player.Uid);
        if (!isStaff && !isBot && !isOwner)
        {
            int playerGold = CountGold(player);
            if (playerGold < totalCost)
                return -1;

            RemoveGold(player, totalCost);
        }
        else
        {
            totalCost = 0;
        }

        if (World != null)
        {
            var backpack = player.Backpack;
            foreach (var entry in items)
            {
                for (int n = 0; n < entry.Amount; n++)
                {
                    var newItem = World.CreateItem();
                    newItem.BaseId = entry.ItemId;
                    newItem.Name = entry.Name;
                    newItem.Amount = 1;
                    if (backpack != null)
                        backpack.AddItem(newItem);
                }
            }
        }

        return totalCost;
    }

    /// <summary>
    /// Process a sell request from player to vendor.
    /// Returns total gold earned.
    /// </summary>
    public static int ProcessSell(Character player, Character vendor, IReadOnlyList<TradeEntry> items)
    {
        if (vendor.NpcBrain != Core.Enums.NpcBrainType.Vendor)
            return 0;

        int totalValue = 0;
        foreach (var entry in items)
            totalValue += entry.Price * entry.Amount;

        if (World != null)
        {
            var backpack = player.Backpack;
            foreach (var entry in items)
            {
                var found = FindItemInBackpack(player, entry.ItemUid);
                if (found != null)
                {
                    if (found.Amount <= entry.Amount)
                        found.Delete();
                    else
                        found.Amount -= (ushort)entry.Amount;
                }
            }

            // Add gold to player
            if (totalValue > 0 && backpack != null)
            {
                var gold = World.CreateItem();
                gold.BaseId = 0x0EED; // gold pile
                gold.ItemType = Core.Enums.ItemType.Gold;
                gold.Amount = (ushort)Math.Min(totalValue, 60000);
                gold.Name = "Gold";
                backpack.AddItem(gold);
            }
        }

        return totalValue;
    }

    /// <summary>Count gold in player's backpack recursively.</summary>
    public static int CountGold(Character ch)
    {
        if (World == null) return 0;
        var backpack = ch.Backpack;
        if (backpack == null) return 0;

        int total = 0;
        foreach (var item in World.GetContainerContents(backpack.Uid))
        {
            if (item.ItemType == Core.Enums.ItemType.Gold || item.BaseId == 0x0EED)
                total += item.Amount;
        }
        return total;
    }

    /// <summary>Remove gold from player's backpack.</summary>
    public static void RemoveGold(Character ch, int amount)
    {
        if (World == null || amount <= 0) return;
        var backpack = ch.Backpack;
        if (backpack == null) return;

        int remaining = amount;
        foreach (var item in World.GetContainerContents(backpack.Uid).ToList())
        {
            if (remaining <= 0) break;
            if (item.ItemType != Core.Enums.ItemType.Gold && item.BaseId != 0x0EED)
                continue;

            if (item.Amount <= remaining)
            {
                remaining -= item.Amount;
                item.Delete();
            }
            else
            {
                item.Amount -= (ushort)remaining;
                remaining = 0;
            }
        }
    }

    private static Item? FindItemInBackpack(Character ch, Serial itemUid)
    {
        if (World == null) return null;
        return World.FindItem(itemUid);
    }

    /// <summary>Default restock interval in milliseconds (10 minutes).</summary>
    public const int DefaultRestockInterval = 600_000;

    /// <summary>
    /// Restock a vendor's inventory from their TAG.VENDORINV definition.
    /// TAG.VENDORINV format: "itemId1:amount1,itemId2:amount2,..."
    /// Called periodically by NPC tick or on first vendor interaction.
    /// </summary>
    public static void RestockVendor(Character vendor)
    {
        if (World == null) return;
        if (vendor.NpcBrain != Core.Enums.NpcBrainType.Vendor) return;

        var backpack = vendor.Backpack;
        if (backpack == null) return;

        if (!vendor.TryGetTag("VENDORINV", out string? invDef) || string.IsNullOrEmpty(invDef))
            return;

        // Parse "itemId:amount,itemId:amount,..."
        var entries = invDef.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var existing = new Dictionary<ushort, int>();

        // Count existing stock
        foreach (var item in World.GetContainerContents(backpack.Uid))
        {
            if (item.IsDeleted) continue;
            existing.TryGetValue(item.BaseId, out int count);
            existing[item.BaseId] = count + item.Amount;
        }

        foreach (var entry in entries)
        {
            var parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;

            ushort itemId;
            if (parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                (parts[0].StartsWith('0') && parts[0].Length > 1))
                ushort.TryParse(parts[0].AsSpan(parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0),
                    System.Globalization.NumberStyles.HexNumber, null, out itemId);
            else
                ushort.TryParse(parts[0], out itemId);

            if (itemId == 0) continue;
            if (!int.TryParse(parts[1], out int maxAmount)) continue;

            existing.TryGetValue(itemId, out int currentAmount);
            int deficit = maxAmount - currentAmount;
            if (deficit <= 0) continue;

            // Create restocked items
            var newItem = World.CreateItem();
            newItem.BaseId = itemId;
            newItem.Amount = (ushort)Math.Min(deficit, 60000);
            backpack.AddItem(newItem);
        }

        // Mark restock time
        vendor.SetTag("RESTOCK_TIME", Environment.TickCount64.ToString());
    }

    /// <summary>Check if vendor needs restocking (based on RESTOCK_TIME tag).</summary>
    public static bool NeedsRestock(Character vendor, int intervalMs = DefaultRestockInterval)
    {
        if (!vendor.TryGetTag("RESTOCK_TIME", out string? timeStr) || !long.TryParse(timeStr, out long lastRestock))
            return true; // never restocked
        return Environment.TickCount64 - lastRestock >= intervalMs;
    }
}

/// <summary>
/// Manages active trade sessions.
/// </summary>
public sealed class TradeManager
{
    private readonly Dictionary<Serial, SecureTrade> _activeTrades = [];
    private uint _nextSessionId;

    public SecureTrade? GetTrade(Serial sessionId) =>
        _activeTrades.GetValueOrDefault(sessionId);

    public SecureTrade StartTrade(Character initiator, Character partner)
    {
        var sessionId = new Serial(++_nextSessionId | 0x80000000);
        var trade = new SecureTrade(sessionId, initiator, partner);
        _activeTrades[sessionId] = trade;
        return trade;
    }

    public void EndTrade(Serial sessionId)
    {
        if (_activeTrades.TryGetValue(sessionId, out var trade))
        {
            if (!trade.IsCompleted)
                trade.Cancel();
            _activeTrades.Remove(sessionId);
        }
    }

    public SecureTrade? FindTradeFor(Character ch) =>
        _activeTrades.Values.FirstOrDefault(t =>
            !t.IsCompleted && (t.Initiator == ch || t.Partner == ch));
}
